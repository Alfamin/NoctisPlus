using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

internal sealed record MusicRequest(string Title, string Artist, double? Seconds = null, string? Url = null,
    bool? Explicit = null, string? Playlist = null, int Position = 0, int? MessageId = null, string? CachedFrom = null, string? PreferredSource = null, string? OriginUri = null,
    BotSelection? Selection = null, AudioQuality? Quality = null)
{
    /// <summary>Owner #93: the second-chance pass after both bots found nothing: plainer names are asked and a close match
    /// is taken (the song is then marked for the user to check). Never saved with a queued request.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool Loose { get; init; }
    public TimeSpan? Duration => Seconds is > 0 and <= 86400 ? TimeSpan.FromSeconds(Seconds.Value) : null;
    public string Identity => OriginUri is {Length:>0} ? OriginUri : Url is { Length: > 0 } ? Url : BotText.Key(Artist) + "|" + BotText.Key(Title) + "|" + Seconds;
}

internal interface IMusicSource
{
    string Id { get; }
    Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct);
    Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct);
}

internal sealed class SourceLimitException(string provider, string message, DateTimeOffset until, bool global = false, bool searchOnly = false)
    : Exception(message)
{
    public string Provider { get; } = provider;
    public DateTimeOffset Until { get; } = until;
    public bool Global { get; } = global;
    public bool SearchOnly { get; } = searchOnly;
}
internal sealed class SourcesWaitingException(string message, DateTimeOffset until) : Exception(message)
{
    public DateTimeOffset Until { get; } = until;
}
internal sealed class TrackUnavailableException(string message) : Exception(message);

/// <summary>A bot that answered "wait for the current download to complete" (owner #84). <see cref="ReplyId"/> is that
/// answer, so its "free again" can be watched for after it.</summary>
internal sealed class BotBusyException(string provider, string message, int replyId) : Exception(message)
{
    public string Provider { get; } = provider;
    public int ReplyId { get; } = replyId;
}

/// <summary>A bot's matching file, found but not yet transferred: the first bot to supply one is transferred.</summary>
internal sealed record BotFile(string Source, Func<Stream, IProgress<double>?, CancellationToken, Task<string>> Transfer);

/// <summary>Owner #84: a bot that is busy with another song races the other bot for the song clicked last.</summary>
internal interface IRacingMusicSource : IMusicSource
{
    /// <param name="afterBusyReply">The busy bot's "please wait" answer: it is asked again as soon as it is free.</param>
    Task<BotFile> RequestFileAsync(MusicRequest request, int? afterBusyReply, CancellationToken ct);
}

/// <summary>Limits are observations from bot replies, never guessed allowances. Unknown resets get a conservative retry.</summary>
internal static partial class BotLimits
{
    [GeneratedRegex(@"(?:FLOOD_(?:PREMIUM_)?WAIT[_ :]*|(?:wait|retry|try again|in|after)\s+)(\d+)\s*(seconds?|secs?|s\b|minutes?|mins?|hours?|hrs?|days?)?", RegexOptions.IgnoreCase)]
    private static partial Regex Wait();
    /// <summary>Owner #156: Telegram's own limit arrives as "FLOOD_WAIT_X" with the seconds in <see cref="TL.RpcException.X"/>;
    /// read as text alone it had no number, so a 30-second wait stopped both bots for 24 hours (kept across restarts).</summary>
    public static SourceLimitException? Read(string provider, TL.RpcException ex, DateTimeOffset now) => Read(provider, Text(ex), now);
    internal static string Text(TL.RpcException ex) => ex.X > 0 && ex.Message.EndsWith("_X", StringComparison.Ordinal) ? ex.Message[..^1] + ex.X : ex.Message;

    public static SourceLimitException? Read(string provider, string text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lower = text.ToLowerInvariant();
        var global = lower.Contains("flood_wait") || lower.Contains("flood_premium_wait") || lower.Contains("flood wait");
        // Queue position, a donation advertisement and a playlist's total are not quotas.
        var match = Wait().Match(text);
        var limited = global || (lower.Contains("daily limit") || lower.Contains("daily quota")) && (lower.Contains("reached") || lower.Contains("exceeded") || lower.Contains("try again") || lower.Contains("used all")) || lower.Contains("limit reached")
            || lower.Contains("quota exceeded") || lower.Contains("too many requests") || lower.Contains("try again tomorrow")
            || lower.Contains("download limit") && lower.Contains("reached") || lower.Contains("you have reached") && lower.Contains("limit")
            || match.Success && (lower.Contains("please wait") || lower.Contains("retry after") || lower.Contains("try again in"));
        if (!limited) return null;
        var retry = now.AddHours(24);
        if (match.Success && long.TryParse(match.Groups[1].Value, out var number))
        {
            var unit = match.Groups[2].Value.ToLowerInvariant();
            var factor = unit.StartsWith("day") ? 86400 : unit.StartsWith("hour") || unit.StartsWith("hr") ? 3600
                : unit.StartsWith("min") ? 60 : 1;
            retry = now.AddSeconds(Math.Clamp(number * (double)factor, 2, 7 * 86400) + 2);
        }
        return new(provider, global ? "Telegram requires a wait before either bot can be used." :
            provider + " reported a " + (BotText.IsSearchLimit(text) ? "name-search" : "download") + " limit. " + (match.Success ? "Retrying after its requested wait." : "No reset time was supplied; checking again in 24 hours."), retry, global, searchOnly: !global && BotText.IsSearchLimit(text));
    }
}

/// <param name="Scope">"global", "search" or "download"; absent in files written before owner #79.</param>
internal sealed record SourceCooldown(string Id, DateTimeOffset Until, string Reason, string? Scope = null);

/// <summary>Primary first on each track. Failed or limited requests use the backup; Telegram-wide waits stop both.</summary>
internal sealed class MusicSources
{
    private readonly IMusicSource[] _sources;
    private readonly Dictionary<string, SourceCooldown> _cooldowns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _rechecked = new(StringComparer.Ordinal);
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(10);
    // Two downloads at a time (owner #79: a Play must not wait for another song's whole transfer) and one search at
    // a time; a search does not wait for a download. Each bot keeps its own conversations apart (TelegramBotSource, DeezLoadSource).
    internal const int FetchLanes = 2;
    private readonly SemaphoreSlim _gate = new(FetchLanes, FetchLanes), _searchGate = new(1, 1), _syncGate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _state;
    private readonly Action<string> _notice;
    private readonly Func<bool> _allowMp3;
    private readonly Func<bool> _preferFlac;
    private readonly Func<AudioQuality>? _quality;
    private readonly Func<string>? _firstBot;
    /// <summary>Owner #105: the files the bots already sent, looked at before either bot is asked.</summary>
    internal IChatFiles? Chats { get; set; }
    /// <param name="allowMp3">Whether an MP3 is taken when no bot has the recording in FLAC (a leak, say). Asked per song.</param>
    public MusicSources(IMusicSource primary, IMusicSource backup, Action<string> notice, string? state = null, Func<DateTimeOffset>? now = null, Func<bool>? allowMp3 = null, Func<bool>? preferFlac = null, Func<AudioQuality>? quality = null, Func<string>? firstBot = null)
    {
        _sources = [primary, backup]; _notice = notice; _state = state; _now = now ?? (() => DateTimeOffset.UtcNow); _allowMp3 = allowMp3 ?? (() => false);
        _preferFlac = preferFlac ?? (() => true);
        _quality = quality;
        _firstBot = firstBot;
        if (state is null || !File.Exists(state)) return;
        try
        {
            if (new FileInfo(state).Length > 64 * 1024) throw new InvalidDataException();
            foreach (var entry in JsonSerializer.Deserialize<SourceCooldown[]>(File.ReadAllText(state)) ?? [])
                if ((_sources.Any(s => s.Id == entry.Id || s.Id + " search" == entry.Id) || entry.Id == "Telegram") && entry.Until <= _now().AddDays(8))
                {
                    // The earlier daily-search fix blocked the whole provider; public track links still work.
                    var key = entry.Id == BotSelections.Hunters && entry.Reason?.Contains("daily search limit", StringComparison.OrdinalIgnoreCase) == true ? entry.Id + " search" : entry.Id;
                    var kept = entry with { Id = key };
                    // Owner #79: builds before the name-search fix saved Music Hunters' "daily search limit reached"
                    // answer as a 24-hour download limit, so the chosen first bot was skipped all day. Such an
                    // unscoped entry without a reset time becomes the 3-hour name-search wait it was; a real
                    // download limit is learned again from the bot's next answer.
                    if (LegacySearchLimit(kept)) kept = kept with { Id = entry.Id + " search", Until = kept.Until - LegacyRetry + TelegramBotSource.SearchLimitPause, Scope = "search" };
                    if (kept.Until > _now()) _cooldowns[kept.Id] = kept;
                }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        { throw new InvalidDataException("SOURCE_STATE_UNREADABLE: saved bot limits could not be read. Keep the file for diagnosis; repair it before resuming.", ex); }
    }
    private static readonly TimeSpan LegacyRetry = TimeSpan.FromHours(24);
    internal static bool LegacySearchLimit(SourceCooldown entry) =>
        entry.Scope is null && entry.Id == BotSelections.Hunters &&
        entry.Reason?.Contains("reported a download limit. No reset time was supplied", StringComparison.Ordinal) == true;

    public BotTrack Track(MusicRequest request)
    {
        var name = Downloader.FileNames([new BotText.Line("1", request.Artist, request.Title, request.Duration)])[0];
        // A stable identifier prevents two recordings sharing a title from overwriting one another.
        if (request.Url is { Length: > 0 }) name += " [" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Url)))[..8].ToLowerInvariant() + "]";
        return new(request.Title, request.Artist, request.Duration, name, (output, progress, ct) => FetchAsync(request, output, progress, ct)) { Request = request };
    }
    public async Task<IReadOnlyList<BotTrack>> SearchAsync(string query, CancellationToken ct)
    {
        await _searchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckGlobal(); var errors = new List<string>();
            foreach (var source in InPreferredOrder())
            {
                if (Cooling(source.Id) || Cooling(source.Id + " search")) continue;
                try
                {
                    var items = await source.SearchAsync(query, ct).ConfigureAwait(false);
                    // The result belongs to the bot that returned it, including a backup search.
                    if (items.Count > 0) return items.Select(r=>Track(r with {PreferredSource=source.Id})).ToArray();
                }
                catch (SourceLimitException ex) { Block(ex); if (ex.Global) throw Waiting(); errors.Add(ex.Message); }
                catch (TL.RpcException ex) when (BotLimits.Read(source.Id, ex, _now()) is not null)
                {var limit=BotLimits.Read(source.Id, ex, _now())!;Block(limit);if(limit.Global)throw Waiting();errors.Add(limit.Message);}
                catch(System.Net.Sockets.SocketException ex)
                {throw;}
                catch (Exception ex) when (ex is TimeoutException or BotAnswerException or TrackUnavailableException) { errors.Add(source.Id + ": " + ex.Message); }
            }
            if (_sources.Any(s => Cooling(s.Id) || Cooling(s.Id + " search"))) throw Waiting();
            if (errors.Count > 0) throw new BotAnswerException(string.Join(" ", errors));
            return [];
        }
        finally { _searchGate.Release(); }
    }
    public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        CheckThese.TakeFetched(request);
        try { return await FetchOnceAsync(request, output, progress, ct).ConfigureAwait(false); }
        // Owner #93: one more pass with plainer names and close matches, only after both bots found nothing; most songs never
        // get here. What it finds is saved and marked "check this".
        catch (TrackUnavailableException first) when (!request.Loose && !ct.IsCancellationRequested)
        {
            Trace($"fetch \"{request.Title}\": nothing exact; trying plainer names and close matches");
            try
            {
                var ext = await FetchOnceAsync(request with { Loose = true }, output, progress, ct).ConfigureAwait(false);
                CheckThese.Fetched(request);
                Trace($"fetch \"{request.Title}\": a close match was taken; marked to check");
                return ext;
            }
            catch (TrackUnavailableException) { throw first; }
        }
    }

    private async Task<string> FetchOnceAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        // Owner 10-18: a click took about a minute before any bot saw a message; these lines (always in
        // session.log) say where the time went: this gate, the quality check, or a bot.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var preferred = _quality?.Invoke();
        var allowMp3 = preferred == AudioQuality.MP3 || _allowMp3();
        var preferFlac = preferred is { } format ? format == AudioQuality.FLAC : _preferFlac();
        if (output is Noctis.Features.ProgressivePlayback.IProgressiveAudioWriter publisher)
            publisher.SetFormatPolicy(ext => ext == "flac" ? preferred != AudioQuality.MP3 : ext == "mp3" && allowMp3 && !preferFlac);
        // Owner #105: a file either bot already sent is taken from its chat, before any bot is asked and without waiting for a
        // bot lane. A song missing from the chats goes on below as before.
        if (Chats is { } chats)
        {
            try
            {
                var wanted = preferred is null ? request : request with { Quality = preferred };
                if (await chats.FetchAsync(wanted, ext => ext == "flac" ? preferred != AudioQuality.MP3 : ext == "mp3" && (preferred == AudioQuality.MP3 || allowMp3 && !preferFlac),
                    output, progress, ct).ConfigureAwait(false) is { } found) return found;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
            { Trace($"fetch \"{request.Title}\": the chats' copy failed ({ex.Message}); asking the bots"); }
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Trace($"fetch \"{request.Title}\": gate {clock.Elapsed.TotalSeconds:0.0} s");
        try
        {
            CheckGlobal(); var errors = new List<string>();
            var qualityErrors = await ConfigureQualityAsync(progress, ct, preferred).ConfigureAwait(false);
            Trace($"fetch \"{request.Title}\": quality checked at {clock.Elapsed.TotalSeconds:0.0} s ({qualityErrors.Count} unconfirmed)");
            if (preferred is not null) request = request with { Quality = preferred };
            // An MP3 a bot sent is kept aside while the other bot is asked for FLAC. It is used only when no
            // bot has the recording in FLAC and the setting allows it.
            using var mp3 = new MemoryStream(); var haveMp3 = false; var skipped = false;
            foreach (var source in InPreferredOrder().OrderBy(s => qualityErrors.ContainsKey(s.Id) ? 1 : 0)
                .ThenBy(s => _firstBot is not null ? 0 : request.CachedFrom == s.Id ? 0 : request.Selection?.Provider == s.Id ? 1 : request.PreferredSource == s.Id ? 2 : 3))
            {
                // A changed settings keyboard must not disable both download providers.
                // Confirmed providers go first. A responsive unconfirmed one can still supply
                // the requested format, which is checked below; rate limits are still kept.
                if (qualityErrors.TryGetValue(source.Id, out var qualityError))
                {
                    errors.Add(qualityError);
                    if (Cooling(source.Id)) { skipped = true; continue; }
                    _notice(source.Id + " did not confirm its setting. Checking the actual supplied audio format instead.");
                }
                if (Cooling(source.Id) && request.CachedFrom != source.Id) { skipped = true; continue; }
                try
                {
                    async Task<string> AskAsync()
                    {
                        if (output.CanSeek) { output.SetLength(0); output.Position = 0; }
                        TransferProgress.Stage(progress, haveMp3
                            ? "MP3 ready · checking FLAC with " + source.Id + "…"
                            : "Asking " + source.Id + " for the file…",
                            // Owner #54: the step icon shows when the other bot is being tried.
                            haveMp3 || errors.Count > 0 ? Noctis.Services.Search.OnlineDownloadStep.Switching : Noctis.Services.Search.OnlineDownloadStep.Asking);
                        // Callbacks and chat message IDs are scoped to one provider. Only recording
                        // metadata and public track URLs may cross to the fallback bot.
                        Trace($"fetch \"{request.Title}\": asking {source.Id} at {clock.Elapsed.TotalSeconds:0.0} s");
                        var got = await source.FetchAsync(BotSelections.ForSource(request, source.Id), output, progress, ct).ConfigureAwait(false);
                        Trace($"fetch \"{request.Title}\": {source.Id} sent {got} at {clock.Elapsed.TotalSeconds:0.0} s");
                        return got;
                    }
                    var ext = await AskAsync().ConfigureAwait(false);
                    // A bot whose format was confirmed sent the other one: its setting may have been changed in the
                    // bot itself (owner, 2026-10-18). Check its menu once; if it really had moved, ask it again now.
                    if (preferred is { } want && (want == AudioQuality.FLAC ? ext == "mp3" : ext == "flac")
                        && !qualityErrors.ContainsKey(source.Id) && source is IQualityMusicSource drifted
                        && await RecheckQualityAsync(drifted, source.Id, want, ct).ConfigureAwait(false))
                        ext = await AskAsync().ConfigureAwait(false);
                    if (preferred == AudioQuality.MP3 && ext != "mp3")
                        throw new TrackUnavailableException(source.Id + " returned " + ext + " despite confirming MP3. Trying the other provider; no format was relabelled.");
                    if (ext != "flac")
                    {
                        if (ext == "mp3" && allowMp3 && !preferFlac) return ext;
                        // Owner #100: only asked; the MP3 stays in the chat as the fallback.
                        if (ext == "mp3" && allowMp3 && output is AskOnlyStream) haveMp3 = true;
                        else if (ext == "mp3" && !haveMp3 && output.CanSeek && output.Length > 0 && allowMp3)
                        {
                            output.Position = 0; await output.CopyToAsync(mp3, ct).ConfigureAwait(false); haveMp3 = true;
                        }
                        throw new TrackUnavailableException("FLAC was requested, but this source returned " + ext + ".");
                    }
                    return ext;
                }
                // Owner #84: "please wait for the current download" — the other bot is asked at once, and this one again the
                // moment it is free; whichever sends the matching file first is transferred.
                catch (BotBusyException busy) when (source is IRacingMusicSource busySource &&
                    _sources.FirstOrDefault(s => s != source && !Cooling(s.Id)) is IRacingMusicSource other)
                {
                    Trace($"fetch \"{request.Title}\": {source.Id} busy at {clock.Elapsed.TotalSeconds:0.0} s; asking {other.Id} now and {source.Id} again once it is free");
                    var ext = await RaceAsync(request, other, busySource, busy.ReplyId, output, progress, clock, ct).ConfigureAwait(false);
                    if (ext != "flac" && !(ext == "mp3" && allowMp3))
                        throw new TrackUnavailableException("FLAC was requested, but the first bot to answer returned " + ext + ".");
                    return ext;
                }
                catch (AudioTransferStalledException ex)
                {
                    Block(new(source.Id, source.Id + ": " + ex.Message, _now().AddMinutes(2)));
                    errors.Add(ex.Message); skipped = true;
                }
                catch (SourceLimitException ex) { Block(ex); if (ex.Global) throw Waiting(); errors.Add(ex.Message); skipped = true; }
                catch (TL.RpcException ex) when (BotLimits.Read(source.Id, ex, _now()) is not null)
                {var limit=BotLimits.Read(source.Id, ex, _now())!;Block(limit);if(limit.Global)throw Waiting();errors.Add(limit.Message);skipped=true;}
                catch(System.Net.Sockets.SocketException ex)
                {throw;}
                catch (Exception ex) when (ex is TimeoutException or BotAnswerException or TrackUnavailableException or StaleSelectionException)
                { errors.Add(source.Id + ": " + ex.Message); Trace($"fetch \"{request.Title}\": {source.Id} failed at {clock.Elapsed.TotalSeconds:0.0} s: {ex.Message}"); _notice(source.Id + " could not supply “" + request.Title + "”; trying the backup."); }
            }
            // A bot that is waiting out a limit (not asked, or it just said so) may still have FLAC: the song waits for it.
            if (haveMp3 && !skipped && output is AskOnlyStream) return "mp3";
            if (haveMp3 && !skipped)
            {
                output.SetLength(0); output.Position = 0; mp3.Position = 0;
                await mp3.CopyToAsync(output, ct).ConfigureAwait(false);
                progress?.Report(1);
                _notice("No FLAC of “" + request.Title + "” was found; the MP3 was saved instead.");
                return "mp3";
            }
            if (_sources.Any(s => Cooling(s.Id))) throw Waiting();
            throw new TrackUnavailableException("Neither bot supplied a matching " + (preferred == AudioQuality.MP3 ? "MP3" : allowMp3 ? "FLAC or MP3" : "FLAC") + " recording. " + string.Join(" ", errors));
        }
        finally { _gate.Release(); }
    }
    private static void Trace(string line) => Noctis.Services.DebugLog.Write("Finder", line);

    private async Task<string> RaceAsync(MusicRequest request, IRacingMusicSource other, IRacingMusicSource busy, int busyReply,
        Stream output, IProgress<double>? progress, System.Diagnostics.Stopwatch clock, CancellationToken ct)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        TransferProgress.Stage(progress, "Asking " + other.Id + " while " + busy.Id + " finishes another song…", Noctis.Services.Search.OnlineDownloadStep.Switching);
        var pending = new List<Task<BotFile>>
        {
            other.RequestFileAsync(BotSelections.ForSource(request, other.Id), null, race.Token),
            busy.RequestFileAsync(BotSelections.ForSource(request, busy.Id), busyReply, race.Token),
        };
        foreach (var ask in pending)
            _ = ask.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var errors = new List<string>();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                if (done.IsCompletedSuccessfully)
                {
                    race.Cancel(); // the slower bot stops waiting; a file it sends later stays in its chat for next time
                    var file = done.Result;
                    Trace($"fetch \"{request.Title}\": {file.Source} supplied the file first at {clock.Elapsed.TotalSeconds:0.0} s");
                    if (output.CanSeek) { output.SetLength(0); output.Position = 0; }
                    var ext = await file.Transfer(output, progress, ct).ConfigureAwait(false);
                    Trace($"fetch \"{request.Title}\": {file.Source} sent {ext} at {clock.Elapsed.TotalSeconds:0.0} s");
                    return ext;
                }
                ct.ThrowIfCancellationRequested();
                switch (done.Exception?.GetBaseException())
                {
                    case SourceLimitException limit: Block(limit); if (limit.Global) throw Waiting(); errors.Add(limit.Message); break;
                    case { } error: errors.Add(error.Message); break;
                }
            }
        }
        finally { race.Cancel(); }
        if (_sources.Any(s => Cooling(s.Id))) throw Waiting();
        throw new TrackUnavailableException("Neither bot supplied a matching recording. " + string.Join(" ", errors));
    }

    private IEnumerable<IMusicSource> InPreferredOrder()
    {
        var first = _firstBot?.Invoke();
        return _sources.OrderBy(s => s.Id == first ? 0 : 1);
    }

    /// <summary>The preference was changed in the app: each bot's menu is checked once more.</summary>
    public async Task SynchronizeQualityAsync(CancellationToken ct)
    {
        // No download runs while the menus are changed: every lane is taken.
        await _syncGate.WaitAsync(ct).ConfigureAwait(false);
        var taken = 0;
        try
        {
            for (; taken < FetchLanes; taken++) await _gate.WaitAsync(ct).ConfigureAwait(false);
            CheckGlobal();
            foreach (var source in _sources.OfType<IQualityMusicSource>()) source.ForgetQuality();
            var quality = _quality?.Invoke();
            var errors = await ConfigureQualityAsync(null, ct, quality).ConfigureAwait(false);
            foreach (var source in _sources.Where(s => Cooling(s.Id)))
                errors.TryAdd(source.Id, source.Id + " is waiting for its reported limit; its quality has not been confirmed yet.");
            if (errors.Count > 0) throw new BotAnswerException(string.Join(" ", errors.Values));
            if (quality is not null) _notice("Both bots confirmed preferred quality: " + quality);
        }
        finally { if (taken > 0) _gate.Release(taken); _syncGate.Release(); }
    }

    private async Task<Dictionary<string, string>> ConfigureQualityAsync(IProgress<double>? progress, CancellationToken ct, AudioQuality? preferred)
    {
        var errors = new Dictionary<string, string>();
        if (_quality is null) return errors;
        // Settings replies have keyboards too. They must not be mistaken for search results.
        await _searchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var quality = preferred ?? _quality();
            foreach (var source in _sources)
            {
                if (Cooling(source.Id)) continue;
                if (source is not IQualityMusicSource configurable) continue;
                // Confirmed before (kept with the login): no "/settings" message for this song.
                if (configurable.QualityConfirmed(quality)) continue;
                TransferProgress.Stage(progress, "Setting " + source.Id + " quality to " + quality + "…");
                try { await configurable.SetQualityAsync(quality, ct).ConfigureAwait(false); }
                catch (SourceLimitException ex) { Block(ex); if (ex.Global) throw Waiting(); errors[source.Id] = ex.Message; }
                catch (TL.RpcException ex) when (BotLimits.Read(source.Id, ex, _now()) is { })
                { var limit = BotLimits.Read(source.Id, ex, _now())!; Block(limit); if (limit.Global) throw Waiting(); errors[source.Id] = limit.Message; }
                catch (TL.RpcException ex)
                { errors[source.Id] = "BOT_QUALITY_FAILED: " + source.Id + " rejected the quality request: " + ex.Message; _notice(errors[source.Id]); }
                catch (System.Net.Sockets.SocketException ex)
                { throw; }
                catch (Exception ex) when (ex is BotAnswerException or TimeoutException)
                { errors[source.Id] = ex.Message; _notice(ex.Message); }
            }
            return errors;
        }
        finally { _searchGate.Release(); }
    }
    /// <returns>True when the bot's setting had moved and was put back, so the song is worth asking for again.</returns>
    private async Task<bool> RecheckQualityAsync(IQualityMusicSource source, string id, AudioQuality quality, CancellationToken ct)
    {
        // Some songs exist only as MP3, so the other format alone is no proof. An album of them must not open the
        // settings menu for every song: once per bot in ten minutes.
        lock (_rechecked)
        {
            if (_rechecked.TryGetValue(id, out var last) && _now() - last < RecheckEvery) return false;
            _rechecked[id] = _now();
        }
        source.ForgetQuality();
        // Settings replies have keyboards too. They must not be mistaken for search results.
        await _searchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var changed = await source.SetQualityAsync(quality, ct).ConfigureAwait(false);
            if (changed) _notice(id + " had been switched away from " + quality + " in the bot; it is set back and asked again.");
            return changed;
        }
        catch (Exception ex) when (ex is BotAnswerException or TimeoutException or TL.RpcException)
        { _notice(id + ": " + ex.Message); return false; }
        finally { _searchGate.Release(); }
    }

    // Searches and downloads run side by side, so the limits they share are read and written under a lock.
    private bool Cooling(string id) { lock (_cooldowns) return _cooldowns.TryGetValue(id, out var value) && value.Until > _now(); }
    private void CheckGlobal() { if (Cooling("Telegram")) throw Waiting(); }
    private SourcesWaitingException Waiting()
    {
        SourceCooldown[] active; lock (_cooldowns) active = _cooldowns.Values.Where(c => c.Until > _now()).ToArray();
        return new("Downloads saved for later. " + string.Join(" ", active.Select(c => c.Reason)),
            active.Where(c => c.Id == "Telegram").Select(c => c.Until).DefaultIfEmpty(active.Min(c => c.Until)).Max());
    }
    private void Block(SourceLimitException error)
    {
        lock (_cooldowns)
        {
            var key = error.Global ? "Telegram" : error.SearchOnly ? error.Provider + " search" : error.Provider;
            _cooldowns[key] = new(key, error.Until, error.Message, error.Global ? "global" : error.SearchOnly ? "search" : "download");
            if (_state is not null) AtomicJson.Write(_state, _cooldowns.Values.ToArray());
        }
        _notice(error.Message + (error.Global ? "" : " Switching to the backup."));
    }
}

internal static class AtomicJson
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Refusing a redirected queue file.");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // A virus scanner or the search indexer can hold the old file for a moment ("Access to the path is denied",
            // seen in the checks under load): three short retries (60 ms in all; callers may hold the queue lock) before giving up.
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); break; }
                catch (Exception ex) when (attempt < 4 && ex is UnauthorizedAccessException or IOException) { Thread.Sleep(10 * attempt); }
            }
        }
        finally
        {
            try {File.Delete(temp);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
    }
}
