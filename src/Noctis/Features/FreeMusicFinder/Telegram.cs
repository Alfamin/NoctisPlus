using System.Globalization;
using System.Net.Sockets;
using TL;
using WTelegram;

namespace FreeMusicFinder;

/// <summary>The user's own Telegram account: the login steps and the connection the bot source talks through.</summary>
internal sealed class TelegramAccount : ITelegramSignIn, IDisposable
{
    private readonly TelegramStore _store;
    private readonly Func<string> _proxy;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Client? _client;
    private string _clientProxy = "";

    // WTelegramClient logs every packet to the console unless told otherwise.
    static TelegramAccount() => Helpers.Log = (_, _) => { };

    /// <param name="storePath">The file the login is kept in.</param>
    /// <param name="proxy">The "Proxy" setting as it is now; read whenever a connection is made.</param>
    /// <param name="log">Told which way a connection goes.</param>
    public TelegramAccount(string storePath, Func<string> proxy, Action<string>? log = null, string? defaultsPath = null)
    {
        _store = new TelegramStore(storePath);
        DataFolder = Path.GetDirectoryName(Path.GetFullPath(storePath))!;
        _defaults = TelegramDefaults.Read(defaultsPath ?? Path.Combine(Path.GetDirectoryName(storePath)!, "telegram-defaults.private.json"));
        _proxy = proxy;
        _log = log;
    }

    public bool IsLoggedIn => _store.Account.Length > 0;

    /// <summary>The folder the login file lives in (the feature's data folder).</summary>
    public string DataFolder { get; }

    /// <summary>Display name of the signed-in account; empty when logged out.</summary>
    public string SignedInAs => _store.Account;

    private readonly TelegramDefaults _defaults;
    public int ApiId => _store.ApiId > 0 ? _store.ApiId : _defaults.ApiId;
    public string ApiHash => _store.ApiId > 0 ? _store.ApiHash : _defaults.ApiHash;
    public bool HasCredentials => ApiId > 0 && ApiHash.Length > 0;
    public string DefaultsError => _store.ApiId > 0 ? "" : _defaults.Error;

    /// <summary>The connected client of the signed-in account.</summary>
    public async Task<Client> ConnectAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsLoggedIn) throw new InvalidOperationException("Not logged in to Telegram.");
            // A connection made through another proxy than the one now set is of no use any more.
            if (_client is not null && (_client.Disconnected || _clientProxy != _proxy()))
            {
                _client.Dispose();
                _client = null;
            }
            if (_client is null)
            {
                var client = CreateClient();
                try { await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(40), ct).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    client.Dispose();
                    throw new SocketException((int)SocketError.TimedOut);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                // Switched off while connecting: this connection must not outlive the account. A new account on the
                // same login file would otherwise run beside it, and Telegram can end a login used twice at once.
                if (_disposed) { client.Dispose(); throw new ObjectDisposedException(nameof(TelegramAccount)); }
                _client = client;
            }
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private readonly Dictionary<string, User> _bots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _qualityGate = new(1, 1);

    /// <summary>A bot's audio format as last confirmed for an account, or why its menu could not be used.</summary>
    internal sealed record BotQualityState(string Quality, DateTimeOffset At, string? Problem = null);
    private string QualityFile => Path.Combine(DataFolder, "bot-quality.json");
    private string QualityKey(string username) => SignedInAs + "|" + username.ToLowerInvariant();
    // A menu Noctis could not follow is not asked again for each song; it is retried after a day, after a
    // preference change or when the bot's audio shows its setting moved.
    private static readonly TimeSpan ProblemRetry = TimeSpan.FromDays(1);
    // A bot that did not answer at all is tried again sooner: it is usually back within minutes.
    private static readonly TimeSpan SilentRetry = TimeSpan.FromMinutes(10);

    private Dictionary<string, BotQualityState> ReadQualities()
    {
        try
        {
            if (File.Exists(QualityFile) && new FileInfo(QualityFile).Length < 64 * 1024)
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BotQualityState>>(File.ReadAllText(QualityFile))
                    ?? new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return new(StringComparer.Ordinal); // unreadable: the bots are simply checked again
    }

    public bool QualityConfirmed(string username, AudioQuality quality)
    {
        lock (_qualityFileLock)
            return ReadQualities().TryGetValue(QualityKey(username), out var known) && known.Quality == quality.ToString() && known.Problem is null;
    }

    public void ForgetQuality(string username)
    {
        lock (_qualityFileLock)
        {
            var states = ReadQualities();
            if (states.Remove(QualityKey(username))) AtomicJson.Write(QualityFile, states);
        }
    }
    private readonly object _qualityFileLock = new();

    private void RecordQuality(string username, BotQualityState state)
    {
        lock (_qualityFileLock)
        {
            var states = ReadQualities();
            states[QualityKey(username)] = state;
            AtomicJson.Write(QualityFile, states);
        }
    }

    /// <summary>
    /// Sets a bot to this format through its own settings menu, once: the result is kept with the login, so later
    /// songs, reconnects and restarts send nothing. Asked again only when the preference changes or the bot's audio
    /// shows the setting moved (owner, 2026-10-18).
    /// </summary>
    /// <returns>True when the bot's setting had to be changed.</returns>
    public async Task<bool> SetQualityAsync(string username, string provider, AudioQuality quality, CancellationToken ct)
    {
        await _qualityGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BotQualityState? known;
            lock (_qualityFileLock) ReadQualities().TryGetValue(QualityKey(username), out known);
            if (known is not null && known.Quality == quality.ToString())
            {
                if (known.Problem is null) return false;
                var silent = known.Problem.StartsWith("BOT_QUALITY_TIMEOUT", StringComparison.Ordinal);
                if (DateTimeOffset.UtcNow - known.At < (silent ? SilentRetry : ProblemRetry))
                    throw silent ? new TimeoutException(known.Problem) : new BotAnswerException(known.Problem);
            }
            var client = await ConnectAsync(ct).ConfigureAwait(false);
            // Recorded BEFORE anything is typed: whatever happens next (an answer Noctis does not know, a Telegram
            // error, a crash), the next song does not send "/settings" again; it is tried again after ten minutes at
            // the earliest (owner, 2026-10-18: it was typed before every song).
            RecordQuality(username, new(quality.ToString(), DateTimeOffset.UtcNow, "BOT_QUALITY_TIMEOUT: " + provider + "'s settings were being checked."));
            bool changed;
            try
            {
                var user = await BotAsync(client, username).ConfigureAwait(false);
                await StartBotAsync(client, user, username, ct).ConfigureAwait(false);
                changed = await BotQuality.ApplyAsync(provider, new TelegramQualityConversation(client, user, provider), quality, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // A menu Noctis could not follow waits a day; a bot or network that did not answer, ten minutes.
                var message = ex is BotAnswerException || ex.Message.StartsWith("BOT_QUALITY_", StringComparison.Ordinal) ? ex.Message
                    : ex is TimeoutException or RpcException or IOException or System.Net.Sockets.SocketException
                        ? "BOT_QUALITY_TIMEOUT: " + provider + " did not answer: " + ex.Message
                        : "BOT_QUALITY_UNVERIFIED: " + provider + "'s settings failed: " + ex.Message;
                RecordQuality(username, new(quality.ToString(), DateTimeOffset.UtcNow, message));
                _log?.Invoke(message);
                throw;
            }
            RecordQuality(username, new(quality.ToString(), DateTimeOffset.UtcNow));
            _log?.Invoke(provider + (changed ? " switched to " : " already uses ") + quality + ".");
            return changed;
        }
        finally { _qualityGate.Release(); }
    }
    private Client? _botsClient;

    /// <summary>
    /// A bot's account, looked up once per connection. Telegram limits how often a username may be
    /// resolved, and a playlist asks for many songs in a row.
    /// </summary>
    public async Task<User> BotAsync(Client client, string username, CancellationToken ct = default)
    {
        lock (_bots)
            if (ReferenceEquals(_botsClient, client) && _bots.TryGetValue(username, out var known)) return known;
        var user = (await client.Contacts_ResolveUsername(username).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false)).User;
        lock (_bots)
        {
            if (!ReferenceEquals(_botsClient, client)) { _bots.Clear(); _started.Clear(); _botsClient = client; }
            _bots[username] = user;
        }
        return user;
    }

    /// <summary>
    /// Sends "/start" to a bot this account has no chat with yet, before the first real message: a bot that
    /// was never started may ignore anything else. Looked at once per connection; an existing chat is left alone.
    /// </summary>
    public async Task StartBotAsync(Client client, InputPeer bot, string username, CancellationToken ct)
    {
        lock (_bots)
            if (ReferenceEquals(_botsClient, client) && _started.Contains(username)) return;
        var history = await client.Messages_GetHistory(bot, limit: 1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        if (history.Messages.Length == 0)
        {
            await client.SendMessageAsync(bot, "/start").WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            _log?.Invoke("started the chat with @" + username);
            // Its welcome arrives first, so it is not taken for the answer to the request that follows.
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        lock (_bots)
        {
            if (!ReferenceEquals(_botsClient, client)) { _bots.Clear(); _started.Clear(); _botsClient = client; }
            _started.Add(username);
        }
    }

    /// <summary>
    /// Sends the phone number. Returns what Telegram wants next ("verification_code",
    /// "password", …) for <see cref="ContinueLoginAsync"/>, or null when the login is complete.
    /// </summary>
    public async Task<string?> StartLoginAsync(int apiId, string apiHash, string phone)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _client?.Dispose();
            _client = null;
            _store.Begin(apiId, apiHash);
            _client = CreateClient();
            return await StepAsync(_client, phone).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ContinueLoginAsync(string answer)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_client is null) throw new InvalidOperationException("Start the login again.");
            return await StepAsync(_client, answer).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session at Telegram and forgets it here; forgets it here even when Telegram cannot be reached.</summary>
    public async Task LogOutAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsLoggedIn)
            {
                try
                {
                    _client ??= CreateClient();
                    await _client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                    await _client.Auth_LogOut().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is RpcException or WTException or IOException or TimeoutException or FormatException)
                {
                }
            }
            Forget();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Telegram no longer accepts the session (ended from another device): go back to logged out.</summary>
    public void SessionEnded()
    {
        _gate.Wait();
        try { Forget(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Exchange(ref _client, null)?.Dispose();
    }
    private volatile bool _disposed;

    /// <summary>Abort stalled transport/RPCs, keeping the saved login for the next connection.</summary>
    internal async Task RetireConnectionAsync(Client expected)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_client, expected)) return;
            var retired = Interlocked.Exchange(ref _client, null);
            if (retired is null) return;
            await retired.DisposeAsync().ConfigureAwait(false);
            _log?.Invoke("Telegram connection reset after an interrupted transfer; saved login retained.");
        }
        finally { _gate.Release(); }
    }

    private void Forget()
    {
        _client?.Dispose();
        _client = null;
        _store.ClearSession();
    }

    /// <summary>How long one sign-in step may take before the dialog says Telegram could not be reached.</summary>
    internal TimeSpan LoginStepDeadline { get; init; } = TimeSpan.FromSeconds(60);

    private async Task<string?> StepAsync(Client client, string answer)
    {
        var step = client.Login(answer);
        string? next;
        // A network that swallows packets must not leave the sign-in spinning for ever (audit, 2026-10-18).
        // This only happens during a login that is not finished, so no saved login is lost; it is started again.
        try { next = await step.WaitAsync(LoginStepDeadline).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            _ = step.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (ReferenceEquals(_client, client)) _client = null;
            client.Dispose();
            throw;
        }
        if (next is null) _store.SetAccount(client.User.MainUsername is { Length: > 0 } name ? "@" + name : client.User.first_name);
        return next;
    }

    /// <summary>A client that connects the way the "Proxy" setting says. Throws <see cref="FormatException"/> when the setting cannot be read.</summary>
    private Client CreateClient()
    {
        var setting = _proxy();
        var proxy = ProxyChoice.Parse(setting);
        var client = new Client(Config, _store.OpenSession());
        switch (proxy?.Kind)
        {
            case null: // "direct"
                break;
            case ProxyKind.Telegram:
                client.MTProxyUrl = proxy.MtProxyUrl;
                break;
            case ProxyKind.System:
                // Asked at every connection (the first one and each time Telegram is reconnected to),
                // so switching the computer's proxy on or off, as a VPN app does, needs nothing here.
                var direct = client.TcpHandler;
                string? last = null;
                client.TcpHandler = (host, port) =>
                {
                    var via = SystemProxy.For(host, port);
                    var way = via is null ? "directly (the computer has no proxy switched on)" : $"through the computer's proxy {via.Host}:{via.Port}";
                    if (way != last) _log?.Invoke("connecting to Telegram " + (last = way));
                    return via is not null ? via.ConnectAsync(host, port) : direct is not null ? direct(host, port) : DirectAsync(host, port);
                };
                break;
            default:
                client.TcpHandler = proxy.ConnectAsync;
                break;
        }
        _clientProxy = setting;
        return client;
    }

    private static async Task<TcpClient> DirectAsync(string host, int port)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port).ConfigureAwait(false);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private string? Config(string what) => what switch
    {
        "api_id" => _store.ApiId.ToString(CultureInfo.InvariantCulture),
        "api_hash" => _store.ApiHash,
        _ => null, // WTelegramClient's defaults
    };
}

/// <summary>
/// A Telegram music bot, driven through the user's own account: "/search" lists tracks with one
/// numbered button each, and pressing a button makes the bot send that track as a file.
/// The bot answers every request in the same chat, so only one download may run at a time:
/// <see cref="Downloads"/> sees to that.
/// </summary>
internal sealed class TelegramBotSource : IHunterBotSource
{
    public const string Bot = "MusicsHuntersbot";
    // The longest waits, used until the bot's usual answer times are known (BotTimings).
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(20);
    // The shortest: a normal answer is never cut off (owner: 8 to 10 s for a ~2 s bot).
    private static readonly TimeSpan MinSearchWait = TimeSpan.FromSeconds(8);
    // Its files come within seconds of a press that lands (0.2-2.3 s measured). A dropped press once left a song
    // waiting minutes with DeezLoad never asked. Owner #53: press 0.5 s after the list, press again when nothing
    // comes, and after 10 s without the file ask DeezLoad.
    internal static readonly TimeSpan PressAfterAnswer = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan RepressEvery = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan FileBudget = TimeSpan.FromSeconds(10);
    internal const int MaxPresses = 3;
    private static readonly TimeSpan PressAnswerWait = TimeSpan.FromSeconds(3);
    internal const string SearchTiming = "hunters-search", FileTiming = "hunters-file";
    private readonly BotTimings _timings;
    private bool _seeded;

    private readonly TelegramAccount _account;
    private readonly Action<string> _log;
    private (Client Client, InputPeer Peer)? _bot;
    // Searches may now run while a download waits for its file. Two searches at once would each take
    // the other's answer, since the bot answers both in the same chat; a download's file is told apart
    // from a search answer by being a file.
    private readonly SemaphoreSlim _searching = new(1, 1);
    private long _answered = long.MinValue;
    /// <summary>How long Music Hunters is left out after "daily search limit reached" (owner #73); then it is asked again.</summary>
    internal static readonly TimeSpan SearchLimitPause = TimeSpan.FromHours(3);
    private DateTimeOffset _nameSearchUntil;
    private string SearchLimitPath => Path.Combine(_account.DataFolder, "hunters-search-until.json");

    public TelegramBotSource(TelegramAccount account, Action<string> log)
    {
        _account = account;
        _log = log;
        _timings = BotTimings.Shared(Path.Combine(account.DataFolder, "bot-timings.json"));
        try
        {
            if (File.Exists(SearchLimitPath) && new FileInfo(SearchLimitPath).Length < 1024)
            {
                var until = System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(SearchLimitPath));
                if (until <= DateTimeOffset.UtcNow.Add(SearchLimitPause)) _nameSearchUntil = until;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { _log("Music Hunters' saved search wait could not be read; track links remain available."); }
    }

    /// <summary>
    /// The first page of the bot's results for <paramref name="query"/>; empty when it found
    /// nothing. Throws <see cref="TimeoutException"/> when the bot does not answer and
    /// <see cref="BotAnswerException"/> when its answer cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<BotTrack>> SearchAsync(string query, CancellationToken ct)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        await _searching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_nameSearchUntil > DateTimeOffset.UtcNow)
                throw new SourceLimitException(BotSelections.Hunters, "Music Hunters' name search is waiting; track links remain available.", _nameSearchUntil, searchOnly: true);
            return await RunAsync<IReadOnlyList<BotTrack>>(async (client, bot) =>
            {
                await LearnFromHistoryAsync(client, bot, ct).ConfigureAwait(false);
                Noctis.Services.DebugLog.Write("Finder", $"Music Hunters: /search sent {waited.Elapsed.TotalSeconds:0.0} s after it was asked");
                var wait = _timings.Wait(SearchTiming, SearchWait, MinSearchWait, SearchWait);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var sent = await client.SendMessageAsync(bot, "/search " + query).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                // The answer always carries buttons; without results it has no numbered ones.
                // Owner #73: the daily-limit answer has no result buttons; it is taken at once instead of waited out.
                var results = await WaitForAsync(client, bot, sent.id, m => m.media is not MessageMediaDocument && (Buttons(m).Any() || BotText.IsSearchLimit(m.message) || BotText.IsEmptySearch(m.message)), wait, ct).ConfigureAwait(false)
                    ?? throw new TimeoutException($"The bot did not answer within {wait.TotalSeconds:0} seconds (it usually answers in about {Usual(SearchTiming)}).");
                _timings.Record(SearchTiming, clock.Elapsed);
                _answered = System.Diagnostics.Stopwatch.GetTimestamp();
                Noctis.Services.DebugLog.Write("Finder", $"Music Hunters: results in {clock.Elapsed.TotalSeconds:0.0} s");
                if (BotText.IsSearchLimit(results.message))
                {
                    _nameSearchUntil = DateTimeOffset.UtcNow.Add(SearchLimitPause);
                    try { AtomicJson.Write(SearchLimitPath, _nameSearchUntil); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("Music Hunters' search wait could not be saved; using it for this session."); }
                    Noctis.Services.DebugLog.Write("Finder", "Music Hunters: daily name-search limit; using the track link");
                    throw new SourceLimitException(BotSelections.Hunters, "Music Hunters reached its daily search limit; track links remain available.", _nameSearchUntil, searchOnly: true);
                }

                return ParseResults(results,FetchSelectedAsync);
            }, ct).ConfigureAwait(false);
        }
        finally { _searching.Release(); }
    }

    internal static IReadOnlyList<BotTrack> ParseResults(Message results,
        Func<MusicRequest,Stream,IProgress<double>?,CancellationToken,Task<string>> fetch)
    {
        var buttons=new Dictionary<string,byte[]>();
        foreach(var button in Buttons(results).Where(IsNumber))
            if(!buttons.TryAdd(button.Text,button.Data))throw new BotAnswerException("Music Hunters returned ambiguous duplicate option numbers.");
        if(buttons.Count==0)return [];
        var lines=BotText.Lines(results.message??"").Where(l=>buttons.ContainsKey(l.Number)).ToList();
        if(lines.Count==0 || lines.GroupBy(l=>l.Number).Any(g=>g.Count()!=1))
            throw new BotAnswerException($"@{Bot} answered, but its numbered results could not be read.");
        var names=Downloader.FileNames(lines);
        return lines.Select((line,i)=>
        {
            var selection=new BotSelection(BotSelections.Hunters,Bot,results.id,line.Number,Convert.ToBase64String(buttons[line.Number]));
            _=BotSelections.DecodeHunter(selection);
            var request=new MusicRequest(line.Title,line.Artist,line.Duration?.TotalSeconds,PreferredSource:BotSelections.Hunters,Selection:selection);
            return new BotTrack(line.Title,line.Artist,line.Duration,names[i],(output,progress,ct)=>fetch(request,output,progress,ct)){Request=request};
        }).ToArray();
    }

    internal static byte[] ValidateSelection(MusicRequest request,Message? message)
    {
        var selection=request.Selection??throw new StaleSelectionException("Music Hunters' selection was not saved.");
        var data=BotSelections.DecodeHunter(selection);
        if(message is null || message.id!=selection.MessageId || message.flags.HasFlag(Message.Flags.out_))
            throw new StaleSelectionException("Music Hunters' selected message is no longer available; finding a fresh matching result.");
        var button=Buttons(message).Where(b=>b.Text==selection.Number).ToArray();
        var line=BotText.Lines(message.message??"").Where(l=>l.Number==selection.Number).ToArray();
        if(button.Length!=1 || !button[0].Data.SequenceEqual(data) || line.Length!=1 ||
            !RecordingMatch.Fits(request,line[0].Artist,line[0].Title,line[0].Duration))
            throw new StaleSelectionException("Music Hunters' selected option changed; finding a fresh matching result.");
        return data;
    }

    /// <summary>
    /// Presses the result's button, waits for the file the bot sends and copies it to
    /// <paramref name="output"/>. Only a song file that is the track asked for is taken: any
    /// other file the bot sends meanwhile (the late answer to an earlier request, or something
    /// that is not a song) is left alone.
    /// </summary>
    internal Task<Document> RequestSelectedDocumentAsync(MusicRequest request,CancellationToken ct)
    {
        var selection=request.Selection??throw new StaleSelectionException("Music Hunters' selection was not saved.");
        _=BotSelections.DecodeHunter(selection);
        return RunAsync(async (client, bot) =>
        {
            var old=await client.Messages_GetHistory(bot,offset_id:selection.MessageId+1,limit:1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            var button=ValidateSelection(request,old.Messages.OfType<Message>().FirstOrDefault(m=>m.id==selection.MessageId));
            var resultsId=selection.MessageId;
            var newest = (await client.Messages_GetHistory(bot, limit: 1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false)).Messages.FirstOrDefault()?.ID ?? resultsId;
            string? notice = null, other = null;
            Message? file = null;
            // Owner #53: press half a second after the list arrives, again if no file comes, and after 10 s DeezLoad.
            var sinceAnswer = _answered == long.MinValue ? TimeSpan.MaxValue : System.Diagnostics.Stopwatch.GetElapsedTime(_answered);
            if (sinceAnswer < PressAfterAnswer) await Task.Delay(PressAfterAnswer - sinceAnswer, ct).ConfigureAwait(false);
            var fileClock = System.Diagnostics.Stopwatch.StartNew();
            for (var press = 1; file is null; press++)
            {
                // Telegram can hold a press until the bot acknowledges it; never longer than a moment here.
                try { notice = (await client.Messages_GetBotCallbackAnswer(bot, resultsId, button).WaitAsync(PressAnswerWait, ct).ConfigureAwait(false))?.message; }
                catch (RpcException ex) when (ex.Message.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)) { }
                catch (TimeoutException) { }
                catch(RpcException ex) when(IsStaleCallback(ex.Message))
                {throw new StaleSelectionException("Music Hunters' selection expired; finding a fresh matching result.");}
                if (BotLimits.Read("Music Hunters", notice ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
                Noctis.Services.DebugLog.Write("Finder", $"Music Hunters: pressed {selection.Number}" + (press > 1 ? $" again at {fileClock.Elapsed.TotalSeconds:0.0} s" : ""));

                var window = FileWindow(press, fileClock.Elapsed);
                if (window <= TimeSpan.Zero) break;
                file = await WaitForAsync(client, bot, newest, m =>
                {
                    if (m.media is not MessageMediaDocument { document: Document sent }) return false;
                    if (Extension(sent) is not null && DeezLoadSource.Fits(request, sent)) return true;
                    other = Describe(sent);
                    return false;
                }, window, ct).ConfigureAwait(false);
                if (file is null && FileWindow(press + 1, fileClock.Elapsed) <= TimeSpan.Zero) break;
            }
            Noctis.Services.DebugLog.Write("Finder", file is not null
                ? $"Music Hunters: file after {fileClock.Elapsed.TotalSeconds:0.0} s"
                : $"Music Hunters: no file after {fileClock.Elapsed.TotalSeconds:0.0} s; trying DeezLoad");
            if (file is not null) _timings.Record(FileTiming, fileClock.Elapsed);
            if (file is null)
            {
                throw new TimeoutException(
                    other is not null ? $"The bot sent another file ({other}) and not this track."
                    : string.IsNullOrWhiteSpace(notice) ? "The bot did not send the file."
                    : "The bot answered: " + notice);
            }
            return (Document)((MessageMediaDocument)file.media).document;
        }, ct);
    }

    /// <summary>How long to wait for the file after press number <paramref name="press"/> (1-based), counted from
    /// the first press: presses at 0, 3 and 6 s, the last one waits until <see cref="FileBudget"/>; zero = give up.</summary>
    internal static TimeSpan FileWindow(int press, TimeSpan elapsed)
    {
        if (press > MaxPresses || elapsed >= FileBudget) return TimeSpan.Zero;
        var until = press < MaxPresses ? RepressEvery * press : FileBudget;
        if (until > FileBudget) until = FileBudget;
        var window = until - elapsed;
        return window > TimeSpan.Zero ? window : TimeSpan.Zero;
    }

    public async Task<string> FetchSelectedAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct)
    {
            var document=await RequestSelectedDocumentAsync(request,ct).ConfigureAwait(false);
            var client=await _account.ConnectAsync(ct).ConfigureAwait(false);
            await AudioFileTransfer.DownloadAsync(_account, client, document, output, progress, ct).ConfigureAwait(false);
            return Extension(document)!;
    }

    public async Task<string> FetchLinkAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        if (!MusicLink.TryParse(request.Url, out var link) || link!.Kind != "track") throw new TrackUnavailableException("No supported track link was found for Music Hunters.");
        Document document;
        await _searching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            document = await RunAsync(async (client, bot) =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var sent = await client.SendMessageAsync(bot, link.Url).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                Noctis.Services.DebugLog.Write("Finder", "Music Hunters: track link sent");
                var reply = await WaitForLinkReplyAsync(token => WaitForAsync(client, bot, sent.id, m => IsLinkFile(request, m) || Buttons(m).Any(IsNumber), FileBudget, token), ct).ConfigureAwait(false)
                    ?? throw new TimeoutException("Music Hunters did not supply the linked recording within 10 seconds; trying the reserve.");
                if (IsLinkFile(request, reply))
                {
                    Noctis.Services.DebugLog.Write("Finder", $"Music Hunters: linked file after {clock.Elapsed.TotalSeconds:0.0} s");
                    return (Document)((MessageMediaDocument)reply.media).document;
                }
                var selected = ParseResults(reply, FetchSelectedAsync).FirstOrDefault(t => RecordingMatch.Fits(request, t.Artist, t.Title, t.Duration))?.Request
                    ?? throw new TrackUnavailableException("Music Hunters' link results did not match this recording.");
                _answered = System.Diagnostics.Stopwatch.GetTimestamp();
                return await RequestSelectedDocumentAsync(request with { Selection = selected.Selection }, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
        }
        finally { _searching.Release(); }
        // Once a matched document is known, independent name searches need not wait for its bytes.
        var transferClient = await _account.ConnectAsync(ct).ConfigureAwait(false);
        await AudioFileTransfer.DownloadAsync(_account, transferClient, document, output, progress, ct).ConfigureAwait(false);
        return Extension(document)!;
    }

    internal static async Task<Message?> WaitForLinkReplyAsync(Func<CancellationToken, Task<Message?>> wait, CancellationToken ct, TimeSpan? budget = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget ?? FileBudget);
        try { return await wait(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Music Hunters' linked reply timed out; trying the reserve."); }
    }

    internal static bool IsLinkFile(MusicRequest request, Message message)
        => !message.flags.HasFlag(Message.Flags.out_) && message.media is MessageMediaDocument { document: Document file }
            && Extension(file) is not null && DeezLoadSource.Fits(request, file);
    private string Usual(string kind) => _timings.Usual(kind) is { } usual ? $"{usual.TotalSeconds:0.#} seconds" : "an unknown time";

    /// <summary>
    /// Once per session, while too little is known: how fast the bot answered the searches already
    /// in this chat (each "/search" and the bot's next message with result buttons). Read through
    /// the app's own signed-in client, one history request, message dates only.
    /// </summary>
    private async Task LearnFromHistoryAsync(Client client, InputPeer bot, CancellationToken ct)
    {
        if (_seeded) return;
        _seeded = true;
        if (_timings.Count(SearchTiming) >= BotTimings.Enough) return;
        try
        {
            var history = await client.Messages_GetHistory(bot, limit: 100).WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            var answers = SearchAnswerTimes(history.Messages.OfType<Message>());
            _timings.Seed(SearchTiming, answers);
            if (_timings.Usual(SearchTiming) is { } usual)
                _log($"Music Hunters answered {answers.Count} earlier searches in about {usual.TotalSeconds:0.#} s; waiting up to " +
                     $"{_timings.Wait(SearchTiming, SearchWait, MinSearchWait, SearchWait).TotalSeconds:0} s before trying DeezLoad.");
        }
        catch (Exception ex) when (ex is RpcException or WTException or IOException or TimeoutException) { }
    }

    /// <summary>For each "/search" sent, how long until the bot's next message carrying result
    /// buttons (another request in between drops that pair). Telegram dates are whole seconds,
    /// so a same-second answer counts as half a second.</summary>
    internal static List<TimeSpan> SearchAnswerTimes(IEnumerable<Message> messages)
    {
        var ordered = messages.OrderBy(m => m.id).ToList();
        var times = new List<TimeSpan>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var asked = ordered[i];
            if (!asked.flags.HasFlag(Message.Flags.out_) || asked.message?.StartsWith("/search ", StringComparison.Ordinal) != true) continue;
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var next = ordered[j];
                if (next.flags.HasFlag(Message.Flags.out_)) break;
                if (next.media is MessageMediaDocument || !Buttons(next).Any()) continue;
                var took = next.date - asked.date;
                times.Add(took < TimeSpan.FromSeconds(0.5) ? TimeSpan.FromSeconds(0.5) : took);
                break;
            }
        }
        return times;
    }

    internal static bool IsStaleCallback(string error)=>new[]{"MESSAGE_ID_INVALID","DATA_INVALID","BUTTON_DATA_INVALID","QUERY_ID_INVALID","RESULT_ID_INVALID"}
        .Any(code=>error.Contains(code,StringComparison.OrdinalIgnoreCase));

    private async Task<T> RunAsync<T>(Func<Client, InputPeer, Task<T>> work, CancellationToken ct)
    {
        try
        {
            var client = await _account.ConnectAsync(ct).ConfigureAwait(false);
            if (_bot?.Client != client)
                _bot = (client, await _account.BotAsync(client, Bot).ConfigureAwait(false));
            await _account.StartBotAsync(client, _bot.Value.Peer, Bot, ct).ConfigureAwait(false);
            return await work(client, _bot.Value.Peer).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.Code == 401)
        {
            _account.SessionEnded();
            throw new InvalidOperationException("Telegram ended the session. Log in again.", ex);
        }
        catch (RpcException ex) when (BotLimits.Read("Music Hunters", ex.Message, DateTimeOffset.UtcNow) is not null)
        {
            throw BotLimits.Read("Music Hunters", ex.Message, DateTimeOffset.UtcNow)!;
        }
        catch(RpcException ex){throw new BotAnswerException("Music Hunters: "+ex.Message);}
    }

    /// <summary>
    /// The oldest message from the bot after <paramref name="afterId"/> that matches, or null
    /// when none came in time. Asks often at first, when an answer is most likely, and less
    /// often the longer it takes: an account that asks Telegram too much gets limited.
    /// </summary>
    private static async Task<Message?> WaitForAsync(Client client, InputPeer bot, int afterId, Func<Message, bool> matches,
        TimeSpan timeout, CancellationToken ct)
    {
        var started = Environment.TickCount64;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var history = await client.Messages_GetHistory(bot, min_id: afterId, limit: 30).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            foreach (var reply in history.Messages.OfType<Message>().Where(m => !m.flags.HasFlag(Message.Flags.out_)))
            {
                // A name-search quota is not a file/download quota. Let SearchAsync scope it correctly.
                // Owner #79: a download waiting for its file never takes another search's quota answer as its own.
                if (BotText.IsSearchLimit(reply.message)) { if (matches(reply)) return reply; continue; }
                if (BotLimits.Read("Music Hunters", reply.message ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
            }
            var found = history.Messages.OfType<Message>().Reverse()
                .FirstOrDefault(m => !m.flags.HasFlag(Message.Flags.out_) && matches(m));
            if (found is not null) return found;

            var waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (waited >= timeout) return null;
            await Task.Delay(PollInterval(waited), ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan PollInterval(TimeSpan waited)
        => TimeSpan.FromSeconds(waited < TimeSpan.FromSeconds(15) ? 1 : waited < TimeSpan.FromMinutes(1) ? 3 : 5);

    /// <summary>The buttons under a message that send data back to the bot when pressed.</summary>
    private static IEnumerable<(string Text, byte[] Data)> Buttons(Message message)
    {
        if (message.reply_markup is not ReplyInlineMarkup markup) yield break;
        foreach (var button in markup.rows.SelectMany(r => r.buttons))
            if (button.type is InlineButtonTypeCallback callback) yield return (button.text, callback.data);
    }

    private static bool IsNumber((string Text, byte[] Data) button) => button.Text.Length > 0 && button.Text.All(char.IsAsciiDigit);

    /// <summary>What a file calls itself, for the message that says it was not the one asked for.</summary>
    private static string Describe(Document sent)
    {
        var audio = sent.attributes?.OfType<DocumentAttributeAudio>().FirstOrDefault();
        var name = string.Join(" - ", new[] { audio?.performer, audio?.title }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (name.Length == 0) name = sent.Filename ?? "";
        return name.Length == 0 ? "without a name" : Shorten(name, 80);
    }

    /// <summary>The extension to save a file under, or null when it is not a song file.</summary>
    internal static string? Extension(Document document)
    {
        var fromName = Path.GetExtension(document.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name ?? "").TrimStart('.').ToLowerInvariant();
        if (Downloader.AudioExtensions.Contains(fromName)) return fromName;
        // No usable name ("Mr. Brightside" ends in no extension at all): the declared type decides.
        return document.mime_type switch
        {
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "m4a",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/aac" => "aac",
            _ => null,
        };
    }

    private static string Shorten(string text, int length)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= length ? text : text[..length] + "…";
    }
}
