using System.Text.RegularExpressions;
using TL;
using WTelegram;

namespace FreeMusicFinder;

internal sealed class HunterMusicSource : IRacingMusicSource, IQualityMusicSource
{
    private readonly TelegramAccount? _account;
    private readonly IHunterBotSource _bot;
    private readonly Action<string> _log;
    private readonly Func<MusicRequest, CancellationToken, Task<string?>>? _resolveLink;
    public HunterMusicSource(TelegramAccount account,Action<string> log, Func<MusicRequest, CancellationToken, Task<string?>>? resolveLink = null)
    {_account=account;_bot=new TelegramBotSource(account,log);_log=log;_resolveLink=resolveLink;}
    internal HunterMusicSource(IHunterBotSource bot,Action<string>? log=null, Func<MusicRequest, CancellationToken, Task<string?>>? resolveLink = null)
    {_bot=bot;_log=log??(_=>{});_resolveLink=resolveLink;}
    internal static readonly TimeSpan LinkBudget = TimeSpan.FromSeconds(15);
    internal static bool IsTrackLink(string? url) => MusicLink.TryParse(url, out var link) && link!.Kind == "track";
    public string Id => BotSelections.Hunters;
    public Task<bool> SetQualityAsync(AudioQuality quality, CancellationToken ct)
        => _account?.SetQualityAsync(TelegramBotSource.Bot, Id, quality, ct) ?? Task.FromResult(false);
    public bool QualityConfirmed(AudioQuality quality) => _account?.QualityConfirmed(TelegramBotSource.Bot, quality) ?? true;
    public void ForgetQuality() => _account?.ForgetQuality(TelegramBotSource.Bot);
    public async Task<IReadOnlyList<MusicRequest>> SearchAsync(string query, CancellationToken ct)
        => (await _bot.SearchAsync(query, ct).ConfigureAwait(false)).Select(t =>
            (t.Request??new MusicRequest(t.Title,t.Artist,t.Duration?.TotalSeconds)) with {PreferredSource=Id}).ToArray();
    public async Task<string> FetchAsync(MusicRequest request, Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        request=BotSelections.ForSource(request,Id);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        // Owner #105: with the song index read up to now, the chat was already looked at (MusicSources.Chats); only the
        // file a request names by its message is checked here.
        if(_account is not null && await TryCachedAsync(request,output,progress,ct,BotLibrary.Current?.UpToDate(TelegramBotSource.Bot)==true).ConfigureAwait(false) is {} cached)return cached;
        Noctis.Services.DebugLog.Write("Finder",$"Music Hunters: chat history checked in {clock.Elapsed.TotalSeconds:0.0} s");
        if(request.Selection is not null)
        {
            try {return await _bot.FetchSelectedAsync(request,output,progress,ct).ConfigureAwait(false);}
            catch(StaleSelectionException ex){_log(ex.Message);}
        }
        using var resolving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Owner #81: long enough for every way of finding the link (DeezLoad's inline search takes ~7 s); it is only
        // waited for when the name search cannot be used, and cancelled as soon as the song is settled.
        resolving.CancelAfter(LinkBudget);
        // Start metadata lookup before asking by name. Catalog requests already carry a usable link.
        var linkTask = ResolveLinkAsync(request, resolving.Token);
        try
        {
            IReadOnlyList<BotTrack> tracks;
            try { tracks = await _bot.SearchAsync(BotQuery.For(request), ct).ConfigureAwait(false); }
            catch (SourceLimitException ex) when (ex.SearchOnly && !ex.Global)
            {
                ct.ThrowIfCancellationRequested();
                var url = await linkTask.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (url is null) throw new TrackUnavailableException("Music Hunters' name search is limited and no matching track link was found; trying the reserve.");
                return await _bot.FetchLinkAsync(request with { Url = url }, output, progress, ct).ConfigureAwait(false);
            }
            var match = tracks.FirstOrDefault(t => RecordingMatch.Fits(request,t.Artist,t.Title,t.Duration));
            if (match is null)
            {
                var url = await linkTask.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (url is not null) return await _bot.FetchLinkAsync(request with { Url = url }, output, progress, ct).ConfigureAwait(false);
                if (BotQuery.Full(request) != BotQuery.For(request))
                {
                    tracks = await _bot.SearchAsync(BotQuery.Full(request), ct).ConfigureAwait(false);
                    match = tracks.FirstOrDefault(t => RecordingMatch.Fits(request,t.Artist,t.Title,t.Duration));
                }
            }
            if (match is not null) return await match.Fetch(output,progress,ct).ConfigureAwait(false);
            // What it offered instead, so a refused song can be explained from session.log (owner #53).
            Noctis.Services.DebugLog.Write("Finder", $"Music Hunters: none of {tracks.Count} results fit \"{request.Artist} - {request.Title}\" ({request.Duration:m\\:ss}): " +
                string.Join(" | ", tracks.Take(6).Select(t => $"{t.Artist} - {t.Title} ({t.Duration:m\\:ss})")));
            throw new TrackUnavailableException("No matching recording in Music Hunters' results.");
        }
        finally { resolving.Cancel(); }
    }
    /// <summary>
    /// Owner #84: after Music Hunters said "please wait for the current download", it races DeezLoad. The song's link is
    /// sent again the moment it is free; a second "please wait" moves that point on, a third gives up.
    /// </summary>
    public async Task<BotFile> RequestFileAsync(MusicRequest request, int? afterBusyReply, CancellationToken ct)
    {
        request=BotSelections.ForSource(request,Id);
        using var resolving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolving.CancelAfter(LinkBudget);
        var url = await ResolveLinkAsync(request, resolving.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (url is null) throw new TrackUnavailableException("No track link was found to send Music Hunters.");
        for (var attempt = 0; ; attempt++)
        {
            try { return await _bot.RequestLinkFileAsync(request with { Url = url }, afterBusyReply, ct).ConfigureAwait(false); }
            catch (BotBusyException again) when (attempt < 2) { afterBusyReply = again.ReplyId; }
        }
    }
    private async Task<string?> ResolveLinkAsync(MusicRequest request, CancellationToken ct)
    {
        if (IsTrackLink(request.Url)) return request.Url;
        if (_resolveLink is null) return null;
        try
        {
            var url = await _resolveLink(request, ct).WaitAsync(ct).ConfigureAwait(false);
            return IsTrackLink(url) ? url : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException or IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return null; } // Optional metadata must not delay or break a successful name search.
    }
    private async Task<string?> TryCachedAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct,bool indexed=false)
    {
        var account=_account!;
        var cachedClient=await account.ConnectAsync(ct).ConfigureAwait(false);
        var cachedPeer=await account.BotAsync(cachedClient,TelegramBotSource.Bot).ConfigureAwait(false);
        if(!indexed && await TelegramAudioCache.Find(cachedClient,cachedPeer,request,ct).ConfigureAwait(false) is {} existing)
        {
            await AudioFileTransfer.DownloadAsync(account,cachedClient,existing,output,progress,ct).ConfigureAwait(false);
            return TelegramBotSource.Extension(existing)!;
        }
        if (request.MessageId is {} id && request.CachedFrom == Id)
        {
            var client=await account.ConnectAsync(ct).ConfigureAwait(false);
            var bot=await account.BotAsync(client,TelegramBotSource.Bot).ConfigureAwait(false);
            var old=await client.Messages_GetHistory(bot,offset_id:id+1,limit:1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            if(old.Messages.OfType<Message>().FirstOrDefault(m=>m.id==id)?.media is MessageMediaDocument{document:Document doc} && DeezLoadSource.Fits(request,doc) && TelegramAudioCache.AcceptsQuality(request, doc))
            {
                await AudioFileTransfer.DownloadAsync(account,client,doc,output,progress,ct).ConfigureAwait(false);
                return TelegramBotSource.Extension(doc)!;
            }
        }
        return null;
    }
}

/// <summary>
/// What is typed to the bots for a song (owner #57). Spotify wrote "JACKBOYS,&lt;no-break space&gt;Sheck Wes": with that
/// space and every credit in the query Music Hunters found nothing, while "gang gang jack boys" found the song.
/// Plain spaces, the first credited artist and the title without its feat./with credit; the full credits are a
/// second try. The results are still checked against all of the song's artists.
/// </summary>
internal static class BotQuery
{
    internal static string Spaces(string text) => Regex.Replace(text, @"[\s  -​  　﻿]+", " ").Trim();

    internal static string For(MusicRequest request) => request.Loose
        ? Spaces(FirstArtist(request.Artist) + " " + Regex.Split(BotText.Plain(PlainTitle(request.Title)), @"\s+[-–—]\s+")[0])
        : Spaces(FirstArtist(request.Artist) + " " + PlainTitle(request.Title));

    /// <summary>The second try. In the loose pass (owner #93): the song's next credited artist with the plain title, since
    /// a bot may list it under them ("I'm God x Demons Mashup" under Clams Casino).</summary>
    internal static string Full(MusicRequest request)
    {
        if (!request.Loose) return Spaces(request.Artist + " " + request.Title);
        var others = Regex.Split(Spaces(request.Artist), @"\s*(?:,|&|;|/|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b)\s+", RegexOptions.IgnoreCase);
        var second = others.Skip(1).FirstOrDefault(a => a.Length > 0 && !a.StartsWith("The ", StringComparison.OrdinalIgnoreCase));
        return second is null ? For(request) : Spaces(second + " " + Regex.Split(BotText.Plain(PlainTitle(request.Title)), @"\s+[-–—]\s+")[0]);
    }

    internal static string FirstArtist(string artist)
    {
        var parts = Regex.Split(Spaces(artist), @"\s*(?:,|&|;|/|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b)\s+", RegexOptions.IgnoreCase);
        // "Tyler, The Creator" is one artist.
        return parts.Length > 1 && parts[1].StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? parts[0] + ", " + parts[1] : parts[0];
    }

    private static string PlainTitle(string title)
        => Regex.Replace(Spaces(title), @"\s*[\(\[](?:feat\.?|ft\.?|featuring|with)\s[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
}

internal static class RecordingMatch
{
    public static bool Fits(MusicRequest wanted, string artist, string title, TimeSpan? duration)
        => Strict(wanted, artist, title, duration) || wanted.Loose && Close(wanted, artist, title, duration);

    /// <summary>
    /// Owner #93, the second-chance pass only: the same artist, the same kind of version (never a remix for the original,
    /// never clean for explicit), a title that starts the same way or names the same song, and a length within 10 s
    /// (or 5 %). Such a song is saved and marked for the user to check.
    /// </summary>
    internal static bool Close(MusicRequest wanted, string artist, string title, TimeSpan? duration)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return false;
        (wanted, title) = Starred(wanted, title);
        if (wanted.Explicit == true && Regex.IsMatch(title, @"\b(clean|censored|radio edit)\b", RegexOptions.IgnoreCase)) return false;
        foreach (var version in new[] { "live", "remix", "instrumental", "acoustic", "cover", "sped up", "slowed", "karaoke", "nightcore", "8d" })
            if (Regex.IsMatch(wanted.Title, @"\b" + version + @"\b", RegexOptions.IgnoreCase) != Regex.IsMatch(title, @"\b" + version + @"\b", RegexOptions.IgnoreCase)) return false;
        if (wanted.Duration is { } length && duration is { } other && Math.Abs((length - other).TotalSeconds) > Math.Max(10, length.TotalSeconds * 0.05)) return false;
        static IEnumerable<string> ArtistKeys(string value) => Regex.Split(value, @"\s*(?:,|&|;|/|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b)\s*", RegexOptions.IgnoreCase).Select(Key).Where(k => k.Length > 0);
        if (!ArtistKeys(wanted.Artist).Intersect(ArtistKeys(artist)).Any()) return false;
        string a = Title(wanted.Title).Name, b = Title(title).Name;
        if (a == b) return true;
        string shorter = a.Length <= b.Length ? a : b, longer = a.Length <= b.Length ? b : a;
        return shorter.Length >= 5 && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static bool Strict(MusicRequest wanted, string artist, string title, TimeSpan? duration)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return false;
        // Catalogs star a censored word differently: Spotify "I'M DAT N***A", Deezer "I'M DAT N****" (owner #79).
        (wanted, title) = Starred(wanted, title);
        if (wanted.Explicit == true && Regex.IsMatch(title, @"\b(clean|censored|radio edit)\b",RegexOptions.IgnoreCase)) return false;
        static string Versions(string name)=>Regex.Replace(name,@"\(with instrumental by[^)]*\)","",RegexOptions.IgnoreCase);
        foreach (var version in new[]{"live","remix","instrumental","acoustic","cover","sped up","slowed"})
            if (Regex.IsMatch(Versions(wanted.Title), @"\b"+version+@"\b",RegexOptions.IgnoreCase) != Regex.IsMatch(Versions(title),@"\b"+version+@"\b",RegexOptions.IgnoreCase)) return false;
        // Catalogs time one master differently: Apple lists Drake's "Hold On, We're Going Home" at 3:51.2, Deezer
        // (where the bots' files come from) at 3:47. Past 3 s only the same title, or one that adds just a label
        // ("Album Version"), counts; never more than 2 % of the song or 6 s.
        var off=wanted.Duration is {} length && duration is {} other ? Math.Abs((length-other).TotalSeconds) : 0;
        // Owner #81: Apple lists SZA "Saturn" at 3:06.2, Deezer (the bots' file) at 3:01, 2.8 % apart. The very same
        // title by the same artist may differ by 3 % (at most 8 s); version words above still keep other cuts apart.
        var sameTitle=Key(wanted.Title)==Key(title);
        if (off>3 && off>(sameTitle ? Math.Min(8,wanted.Duration!.Value.TotalSeconds*0.03) : Math.Min(6,wanted.Duration!.Value.TotalSeconds*0.02))) return false;
        var close=off<=3;
        // Several artists are written "A, B", "A & B", "A feat. B"; tags also join them as "A; B" and "A/B".
        static IEnumerable<string> ArtistKeys(string value)=>Regex.Split(value,@"\s*(?:,|&|;|/|\bfeat\.?|\bft\.?|\bfeaturing\b)\s*",RegexOptions.IgnoreCase).Select(Key).Where(k=>k.Length>0);
        var a=Key(wanted.Artist); var b=Key(artist);
        if(a.Length==0 || b.Length==0 || !(a==b || ArtistKeys(wanted.Artist).Intersect(ArtistKeys(artist)).Any())) return false;
        var lengths=wanted.Duration is not null && duration is not null;
        if(Key(wanted.Title)==Key(title)) return true;
        // Owner #89: Telegram keeps only the start of a long audio title, so both bots' files of A$AP Rocky's "I Smoked Away My
        // Brain (I'm God x Demons Mashup) (feat. Imogen Heap & Clams Casino)" said "… (feat. Imogen H" and were refused. A cut-off
        // title that is the start of the wanted one, by the same artist and of the same length, is that recording.
        if(lengths && close && Truncated(wanted.Title,title)) return true;
        // The same title written another way.
        var (name,extras)=Title(wanted.Title); var (otherName,otherExtras)=Title(title);
        if(lengths && Key(BotText.Plain(wanted.Title))==Key(BotText.Plain(title)) && (close || LabelsOnly(extras,otherExtras))) return true;
        if(name!=otherName) return false;
        if(extras.SetEquals(otherExtras)) return true;
        // One names an addition the other leaves out ("- Remastered 2011", "(Bonus)"): the same recording when the
        // lengths say so. Two different additions ("- Pt. 1", "- Pt. 2") are two recordings.
        return lengths && (extras.IsSubsetOf(otherExtras) || otherExtras.IsSubsetOf(extras)) && (close || LabelsOnly(extras,otherExtras));
    }

    /// <summary>Is <paramref name="sent"/> the cut-off start of <paramref name="wanted"/>? It must look cut (a bracket left open,
    /// a trailing "…", or long enough to have hit the limit) and keep enough of the title to mean something.</summary>
    internal static bool Truncated(string wanted,string sent)
    {
        var a=Key(wanted); var b=Key(sent);
        if(b.Length<16 || b.Length>=a.Length || !a.StartsWith(b,StringComparison.Ordinal)) return false;
        var open=sent.Count(c=>c=='(')>sent.Count(c=>c==')') || sent.Count(c=>c=='[')>sent.Count(c=>c==']');
        return open || sent.TrimEnd().EndsWith('…') || sent.TrimEnd().EndsWith("...",StringComparison.Ordinal) || sent.Length>=60;
    }

    /// <summary>Owner #165: one title stars a word the other spells out (Deezer "Trap N*ggas", the file "Trap Niggas"): the
    /// spelled-out word is starred too when it starts with the same letter, then both are <see cref="Uncensored"/>.</summary>
    internal static (MusicRequest Wanted, string Title) Starred(MusicRequest wanted, string title)
        => (wanted with { Title = Uncensored(CensoredLike(wanted.Title, title)) }, Uncensored(CensoredLike(title, wanted.Title)));

    /// <summary><paramref name="title"/> with each word that <paramref name="starred"/> stars at the same place starred the same way.</summary>
    internal static string CensoredLike(string title, string starred)
    {
        if (!starred.Contains('*') || title.Contains('*')) return title;
        var words = title.Split(' '); var stars = starred.Split(' ');
        if (words.Length != stars.Length) return title;
        for (var i = 0; i < words.Length; i++)
            if (stars[i].Contains('*') && words[i].Length > 0 && stars[i].Length > 0 && char.ToLowerInvariant(words[i][0]) == char.ToLowerInvariant(stars[i][0]))
                words[i] = stars[i];
        return string.Join(' ', words);
    }

    /// <summary>
    /// Owner #165: the same song, whatever its cut or length: the same name and kind of version (never a remix for the
    /// original, never clean for explicit) by a shared artist. For You leaves out a song the library has this way ("Drip
    /// Too Hard (Lil Baby &amp; Gunna)", a 3:07 video cut, for the 2:26 album one); downloads still want the very recording.
    /// </summary>
    internal static bool SameSong(MusicRequest wanted, string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)) return false;
        (wanted, title) = Starred(wanted, title);
        if (wanted.Explicit == true && Regex.IsMatch(title, @"\b(clean|censored|radio edit)\b", RegexOptions.IgnoreCase)) return false;
        foreach (var version in new[] { "live", "remix", "instrumental", "acoustic", "cover", "sped up", "slowed", "karaoke", "nightcore", "8d" })
            if (Regex.IsMatch(wanted.Title, @"\b" + version + @"\b", RegexOptions.IgnoreCase) != Regex.IsMatch(title, @"\b" + version + @"\b", RegexOptions.IgnoreCase)) return false;
        static IEnumerable<string> ArtistKeys(string value) => Regex.Split(value, @"\s*(?:,|&|;|/|\bfeat\.?|\bft\.?|\bfeaturing\b|\bx\b)\s*", RegexOptions.IgnoreCase).Select(Key).Where(k => k.Length > 0);
        return ArtistKeys(wanted.Artist).Intersect(ArtistKeys(artist)).Any() && Title(wanted.Title).Name == Title(title).Name;
    }

    /// <summary>A starred word is kept as its first letter and one star, however many letters were hidden.</summary>
    internal static string Uncensored(string title) => title.Contains('*') ? Regex.Replace(title, @"(?<![\w*])(\w)[\w*]*\*[\w*]*", "$1*") : title;

    // Additions that name the release, not another cut of the song ("Album Version", "Remastered 2011", "Explicit").
    private static bool LabelsOnly(HashSet<string> one,HashSet<string> other)
    {
        var differ=new HashSet<string>(one,StringComparer.Ordinal); differ.SymmetricExceptWith(other);
        return differ.All(e=>Regex.IsMatch(e,@"^(?:albumversion|explicit(?:version)?|originalversion|(?:\d{4})?(?:digital)?remaster(?:ed)?(?:\d{4})?(?:version)?)$"));
    }

    /// <summary>The key playlist entries and library songs are looked up by: the title without its additions and credits.</summary>
    public static string TitleKey(string title)=>Title(title).Name;

    /// <summary>
    /// A title taken apart the same way however it is written. Spotify writes "Girls Need Love (with Drake) - Remix"
    /// and "LOVE. FEAT. ZACARI."; the files the bots send say "Girls Need Love (Remix)" and "LOVE.". The name is what
    /// comes first; additions are what stands in brackets or after " - "; who else is on it (feat., with, prod.) is
    /// said by the artist and left out.
    /// </summary>
    private static (string Name,HashSet<string> Extras) Title(string title)
    {
        var extras=new HashSet<string>(StringComparer.Ordinal);
        void Add(string text){if(!Regex.IsMatch(text,@"^\s*(?:feat|ft|featuring|with|prod|produced by)\b",RegexOptions.IgnoreCase) && Key(text) is {Length:>0} key)extras.Add(key);}
        var rest=title;
        for(var depth=0;depth<4;depth++) // brackets inside brackets, from the inside out
        {
            var before=rest;rest=Regex.Replace(rest,@"\([^()]*\)|\[[^\[\]]*\]",m=>{Add(m.Value[1..^1]);return " ";});
            if(rest==before)break;
        }
        var pieces=Regex.Split(rest,@"\s+[-–—]\s+");
        foreach(var piece in pieces.Skip(1))Add(piece);
        var name=Key(Regex.Replace(pieces[0],@"\s(?:feat|ft|featuring|prod|produced by)\b\.?\s*\S.*$","",RegexOptions.IgnoreCase));
        return (name.Length>0?name:Key(title),extras);
    }

    // BotText.Key and the letters Unicode does not take apart: "Podsiadło" is written "Podsiadlo" in many files.
    private static string Key(string? text)
    {
        var key=BotText.Key(text);
        return key.AsSpan().IndexOfAny("łøđðßæœıþ")<0?key:key.Replace("ł","l").Replace("ø","o").Replace("đ","d").Replace("ð","d").Replace("ß","ss").Replace("æ","ae").Replace("œ","oe").Replace("ı","i").Replace("þ","th");
    }
}

internal sealed class DeezLoadSource : IRacingMusicSource, IQualityMusicSource
{
    public const string Bot = "deezload2bot";
    private static readonly TimeSpan OtherRecordingGrace = TimeSpan.FromMinutes(1);
    // Owner #80: a silent DeezLoad is given up after about three times its usual first answer (learned from its chat),
    // 8 s until that is known, never sooner than 4 s nor later than 20 s; then it rests for 10 minutes.
    internal static readonly TimeSpan SilentLimit = TimeSpan.FromSeconds(20), SilentFirst = TimeSpan.FromSeconds(8), SilentMin = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan SilentRest = TimeSpan.FromMinutes(10);
    internal const string ReplyTiming = "deezload-reply";
    public string Id => "DeezLoad";
    public Task<bool> SetQualityAsync(AudioQuality quality, CancellationToken ct) => _account.SetQualityAsync(Bot, Id, quality, ct);
    public bool QualityConfirmed(AudioQuality quality) => _account.QualityConfirmed(Bot, quality);
    public void ForgetQuality() => _account.ForgetQuality(Bot);
    private readonly TelegramAccount _account;
    private readonly Action<string> _notice;
    private readonly Func<MusicRequest, CancellationToken, Task<string?>>? _resolveLink;
    private readonly BotTimings _timings;
    private readonly SemaphoreSlim _conversation = new(1,1);
    public DeezLoadSource(TelegramAccount account, Action<string> notice, Func<MusicRequest, CancellationToken, Task<string?>>? resolveLink = null)
    { _account=account; _notice=notice; _resolveLink=resolveLink; _timings=BotTimings.Shared(Path.Combine(account.DataFolder,"bot-timings.json")); }
    public async Task<IReadOnlyList<MusicRequest>> SearchAsync(string query,CancellationToken ct)
    {
        var client=await _account.ConnectAsync(ct).ConfigureAwait(false);
        var bot=await _account.BotAsync(client,Bot).ConfigureAwait(false);
        try
        {
            var results=await InlineAsync(client,bot,query,ct).ConfigureAwait(false);
            var found=new List<MusicRequest>();
            foreach(var result in results.results)
            {
                if(result is BotInlineMediaResult media && media.document is Document doc)
                {
                    var track=Request(doc,null);
                    if(track is not null) found.Add(track);
                }
                else if(result is BotInlineResult plain && plain.send_message is BotInlineMessageText text)
                {
                    if(InlineTrack(plain.title??"",plain.description??"",text.message) is {} track)found.Add(track);
                }
            }
            return found;
        }
        catch(RpcException ex) { throw Translate(ex); }
    }
    internal static MusicRequest? InlineTrack(string title,string description,string message)
    {
        var match=Regex.Match(message,@"https://(?:www\.)?deezer\.com/(?:[a-z]{2}/)?track/\d+");
        var artistMatch=Regex.Match(description,@"^Artists?\s*:\s*(.+)$",RegexOptions.Multiline|RegexOptions.IgnoreCase);
        var artist=artistMatch.Success?artistMatch.Groups[1].Value.Trim():description.Split('\n')[0].Trim();
        title=title.Trim();
        if(!match.Success || !MusicLink.TryParse(match.Value,out var link) || title.Length==0 || artist.Length==0 || title.EndsWith('…'))return null;
        return new(title,artist,Url:link!.Url);
    }
    public async Task<string> FetchAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct)
        => await (await RequestFileAsync(request,null,ct).ConfigureAwait(false)).Transfer(output,progress,ct).ConfigureAwait(false);
    /// <summary>DeezLoad's matching file (its chat first, then a request), not yet transferred: transferred by the caller,
    /// so it can race Music Hunters for the song clicked last (owner #84).</summary>
    public async Task<BotFile> RequestFileAsync(MusicRequest request,int? afterBusyReply,CancellationToken ct)
    {
        var phaseClock = System.Diagnostics.Stopwatch.StartNew();
        using var resolving = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolving.CancelAfter(TimeSpan.FromSeconds(4));
        // Owner #80: songs from the Apple catalog carry no track link, and DeezLoad's own inline search took 7-8 s.
        // The catalog lookup runs while the chat is checked, not after it.
        var linkTask = ResolveLinkAsync(request, resolving.Token);
        try
        {
            var client=await _account.ConnectAsync(ct).ConfigureAwait(false);
            var bot=await _account.BotAsync(client,Bot).ConfigureAwait(false);
            // One history read serves the cache check and learning how fast the bot usually answers.
            var chat=(await client.Messages_GetHistory(bot,limit:100).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false)).Messages.OfType<Message>().ToArray();
            _timings.Seed(ReplyTiming, ReplyTimes(chat));
            if(TelegramAudioCache.Find(chat,request) is {} existing) return Ready(client,existing);
            if(request.MessageId is {} id && request.CachedFrom == Id)
            {
                // The message is already in this bot's chat; reading its file does not request another bot download.
                var old=await client.Messages_GetHistory(bot,offset_id:id+1,limit:1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                var message=old.Messages.OfType<Message>().FirstOrDefault(m=>m.id==id);
                if(message?.media is MessageMediaDocument {document:Document cached} && Fits(request,cached) && TelegramAudioCache.AcceptsQuality(request, cached))
                    return Ready(client,cached);
            }
            Noctis.Services.DebugLog.Write("Finder", $"DeezLoad: cache checks finished at {phaseClock.Elapsed.TotalSeconds:0.0}s");
            await _conversation.WaitAsync(ct).ConfigureAwait(false);
            Noctis.Services.DebugLog.Write("Finder", $"DeezLoad: conversation acquired at {phaseClock.Elapsed.TotalSeconds:0.0}s");
            // Owner #79: once the file is known the conversation is free, so the next song does not wait for these bytes.
            try { return Ready(client,await ConverseAsync(client,bot,request,linkTask,phaseClock,ct).ConfigureAwait(false)); }
            catch(RpcException ex) { throw Translate(ex); }
            finally { _conversation.Release(); }
        }
        finally { resolving.Cancel(); }
    }
    private BotFile Ready(Client client,Document doc) => new(Id, async (output,progress,ct) =>
    {
        try { return await Download(client,doc,output,progress,ct).ConfigureAwait(false); }
        catch(RpcException ex) { throw Translate(ex); }
    });
    private async Task<string?> ResolveLinkAsync(MusicRequest request, CancellationToken ct)
    {
        if (MusicLink.TryParse(request.Url, out var given) && given!.Kind == "track") return request.Url;
        if (_resolveLink is null) return null;
        try
        {
            var url = await _resolveLink(request, ct).WaitAsync(ct).ConfigureAwait(false);
            return MusicLink.TryParse(url, out var link) && link!.Kind == "track" ? url : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or HttpRequestException or IOException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        { return null; } // DeezLoad's own inline search remains when the catalogs cannot help.
    }
    /// <summary>For each track link sent, how long until the bot's next message (another request in between drops
    /// that pair). Telegram dates are whole seconds, so a same-second answer counts as half a second.</summary>
    internal static List<TimeSpan> ReplyTimes(IEnumerable<Message> messages)
    {
        var ordered = messages.OrderBy(m => m.id).ToList(); var times = new List<TimeSpan>();
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var asked = ordered[i]; var next = ordered[i + 1];
            if (!asked.flags.HasFlag(Message.Flags.out_) || !MusicLink.TryParse(asked.message?.Trim(), out var link) || link!.Kind != "track") continue;
            if (next.flags.HasFlag(Message.Flags.out_)) continue;
            var took = next.date - asked.date;
            times.Add(took > TimeSpan.Zero ? took : TimeSpan.FromSeconds(0.5));
        }
        return times;
    }
    /// <summary>Asks for the track and waits for the bot's matching file, inside the one conversation.</summary>
    private async Task<Document> ConverseAsync(Client client,User bot,MusicRequest request,Task<string?> linkTask,System.Diagnostics.Stopwatch phaseClock,CancellationToken ct)
    {
        var url=request.Url ?? await linkTask.ConfigureAwait(false);
        if(url is null)
        {
            // The slow way, only when the catalogs had no matching track.
            var matches=await InlineAsync(client,bot,BotQuery.For(request),ct).ConfigureAwait(false);
            foreach(var item in matches.results.OfType<BotInlineMediaResult>())
                if(item.document is Document doc && Fits(request,doc) && (request.Quality != AudioQuality.MP3 || TelegramBotSource.Extension(doc) == "mp3")) return doc;
            url=MatchingInlineLink(request,matches.results.OfType<BotInlineResult>());
            if(url is null)
                Noctis.Services.DebugLog.Write("Finder",$"DeezLoad: none of {matches.results.Length} results fit \"{request.Artist} - {request.Title}\": "+
                    string.Join(" | ",matches.results.Take(6).Select(r=>r is BotInlineResult text ? (text.title??"")+" / "+(text.description??"").ReplaceLineEndings(" ")
                        : r is BotInlineMediaResult{document:Document doc} && Request(doc,null) is {} file ? file.Artist+" - "+file.Title+" ("+file.Duration+")" : "file")));
        }
        if(!MusicLink.TryParse(url,out var link) || link!.Kind!="track") throw new TrackUnavailableException("DeezLoad did not return a usable track link.");
        Noctis.Services.DebugLog.Write("Finder", $"DeezLoad: track link resolved at {phaseClock.Elapsed.TotalSeconds:0.0}s");
        await _account.StartBotAsync(client,bot,Bot,ct).ConfigureAwait(false);
        await BotPacing.TurnAsync("deezload", ct).ConfigureAwait(false);
        var sent=await client.SendMessageAsync(bot,link.Url).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        Noctis.Services.DebugLog.Write("Finder", $"DeezLoad: track link sent at {phaseClock.Elapsed.TotalSeconds:0.0}s");
        var end=DateTimeOffset.UtcNow.AddMinutes(15); var cursor=sent.id; var lastNotice="";
        string? other=null; DateTimeOffset? otherSince=null;
        var asked=DateTimeOffset.UtcNow; var heard=false;
        var silent=_timings.Wait(ReplyTiming,SilentFirst,SilentMin,SilentLimit);
        while(DateTimeOffset.UtcNow<end)
        {
            ct.ThrowIfCancellationRequested();
            var next=_account.NextUpdate();
            var replies=await ReadNew(client,bot,sent.id,ct).ConfigureAwait(false);
            if(!heard && replies.Count>0) _timings.Record(ReplyTiming,DateTimeOffset.UtcNow-asked);
            heard|=replies.Count>0;
            // A bot that is working says so ("Downloading", its queue position); one that has said nothing at all
            // is not working on it (owner #53: never sit and wait). The next songs go to the other bot meanwhile.
            if(!heard && DateTimeOffset.UtcNow-asked>=silent)
                throw new SourceLimitException(Id,$"DeezLoad did not answer within {silent.TotalSeconds:0} seconds; the other bot takes the songs for the next 10 minutes.",DateTimeOffset.UtcNow.Add(SilentRest));
            foreach(var m in replies)
            {
                if(BotLimits.Read(Id,m.message??"",DateTimeOffset.UtcNow) is {} limited) throw limited;
                if(BotJoin.Links(m,Bot) is {Count:>0} channels) throw BotJoin.Gate(Id,Bot,channels);
                // Joined in Telegram itself: the bot greets this first message instead of answering it.
                if(BotJoin.IsUnlocked(m)) throw new TrackUnavailableException("DeezLoad was just unlocked by joining its channel; the other bot takes this song.");
                if(m.media is MessageMediaDocument {document:Document doc})
                {
                    if(Fits(request,doc)) { BotPacing.Answered("deezload"); return doc; }
                    if(Request(doc,null) is {} sentTrack){other??=Shorten(sentTrack.Artist+" - "+sentTrack.Title)+(sentTrack.Duration is {} took?$", {took:m\\:ss}":"");otherSince??=DateTimeOffset.UtcNow;}
                }
                if(m.message is {Length:>0} status && status!=lastNotice && (status.Contains("position in the queue") || status.Contains("Downloading")))
                {
                    _notice("DeezLoad: "+(lastNotice=status).ReplaceLineEndings(" "));
                    if (status.Contains("position in the queue")) throw new SourceLimitException(Id,"DeezLoad has a queue delay; trying the other bot.",DateTimeOffset.UtcNow.AddMinutes(10));
                }
                if(Regex.IsMatch(m.message??"",@"cannot download|not available|not found",RegexOptions.IgnoreCase)) throw new TrackUnavailableException("DeezLoad could not supply this recording.");
            }
            // The bot sent another recording (often the same song in a version of another length). The right one
            // still gets a minute, in case that file was a late answer to an earlier request; then the backup is
            // tried, instead of every other song and search waiting out the whole quarter of an hour.
            if(otherSince is {} since && DateTimeOffset.UtcNow-since>=OtherRecordingGrace)
                throw new TrackUnavailableException($"DeezLoad sent another recording ({other}) and not this one.");
            // Owner #84: woken the moment DeezLoad's file or status arrives; the poll is only the fallback.
            await TelegramAccount.UntilUpdateAsync(next,TimeSpan.FromSeconds(2),ct).ConfigureAwait(false);
        }
        throw new TimeoutException("DeezLoad did not deliver the track within 15 minutes. The backup will be tried.");
    }
    // Telegram answers BOT_RESPONSE_TIMEOUT when the bot is slow to answer an inline search; one more try usually lands.
    private static async Task<Messages_BotResults> InlineAsync(Client client,User bot,string query,CancellationToken ct)
    {
        try { return await client.Messages_GetInlineBotResults(bot,bot,query,"").WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false); }
        catch(RpcException ex) when(ex.Message.Contains("BOT_RESPONSE_TIMEOUT",StringComparison.OrdinalIgnoreCase))
        {
            Noctis.Services.DebugLog.Write("Finder","DeezLoad: inline search timed out; asking once more");
            await Task.Delay(1000,ct).ConfigureAwait(false);
            return await client.Messages_GetInlineBotResults(bot,bot,query,"").WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        }
    }
    internal Exception Translate(RpcException ex)
        => BotLimits.Read(Id,ex,DateTimeOffset.UtcNow) ?? (Exception)new BotAnswerException("DeezLoad: "+ex.Message);
    internal static string? MatchingInlineLink(MusicRequest wanted,IEnumerable<BotInlineResult> results)
        => results.Where(r=>r.send_message is BotInlineMessageText)
            .Select(r=>InlineTrack(r.title??"",r.description??"",((BotInlineMessageText)r.send_message).message))
            .FirstOrDefault(r=>r is not null && RecordingMatch.Fits(wanted,r.Artist,r.Title,r.Duration))?.Url;
    private static string Shorten(string text)=>text.Length<=80?text:text[..80]+"…";
    internal static MusicRequest? Request(Document doc,int? messageId)
    {
        var audio=doc.attributes?.OfType<DocumentAttributeAudio>().FirstOrDefault();
        if(audio is null || string.IsNullOrWhiteSpace(audio.title) || string.IsNullOrWhiteSpace(audio.performer)) return null;
        return new(audio.title,audio.performer,audio.duration>0?audio.duration:null,MessageId:messageId);
    }
    internal static bool Fits(MusicRequest wanted,Document doc)
    {
        var sent=Request(doc,null);
        return sent is not null && RecordingMatch.Fits(wanted,sent.Artist,sent.Title,sent.Duration);
    }
    private async Task<string> Download(Client client,Document doc,Stream output,IProgress<double>? progress,CancellationToken ct)
    {
        var ext = TransferExtension(doc);
        await AudioFileTransfer.DownloadAsync(_account,client,doc,output,progress,ct).ConfigureAwait(false);
        return ext;
    }
    internal static string TransferExtension(Document doc)
    {
        var ext = TelegramBotSource.Extension(doc);
        return ext is "flac" or "mp3" ? ext
            : throw new TrackUnavailableException("The bot returned an unsupported format; a FLAC or MP3 audio file is needed.");
    }
    internal static async Task<IReadOnlyList<Message>> ReadNew(Client client,InputPeer bot,int after,CancellationToken ct)
    {
        var all=new List<Message>(); var offset=0;
        for(var page=0;page<100;page++)
        {
            var history=await client.Messages_GetHistory(bot,offset_id:offset,min_id:after,limit:100).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            if(history.Messages.Length==0) break;
            all.AddRange(history.Messages.OfType<Message>().Where(m=>!m.flags.HasFlag(Message.Flags.out_)));
            var next=history.Messages.Min(m=>m.ID);
            if(next<=after || next==offset) break;
            offset=next;
            if(history.Messages.Length<100) break;
            await Task.Delay(600,ct).ConfigureAwait(false);
        }
        return all.OrderBy(m=>m.id).ToArray();
    }
}

/// <summary>Download progress in steps of 2 %: every report redraws the queue in the window.</summary>
internal static class TransferProgress
{
    public static void Stage(IProgress<double>? progress, string text, Noctis.Services.Search.OnlineDownloadStep step = Noctis.Services.Search.OnlineDownloadStep.Asking)
    {
        if (progress is IDownloadProgress detailed) detailed.Stage(text, step);
        else progress?.Report(0);
    }

    public static Client.ProgressCallback Steps(IProgress<double>? progress,CancellationToken ct)
    {
        var last=0.0;
        return (done,total)=>
        {
            ct.ThrowIfCancellationRequested();
            if(total<=0)return;
            var share=(double)done/total;
            if(share-last>=0.02 || done>=total)progress?.Report(last=share);
        };
    }
}

internal static class TelegramAudioCache
{
    internal static bool AcceptsQuality(MusicRequest wanted, Document doc)
        => TelegramBotSource.Extension(doc) is { } format && (wanted.Quality is null ||
            format == (wanted.Quality == AudioQuality.MP3 ? "mp3" : "flac"));
    /// <summary>Already received files can be copied directly, without another bot request or a new provider queue.</summary>
    public static async Task<Document?> Find(Client client,InputPeer peer,MusicRequest wanted,CancellationToken ct)
    {
        var history=await client.Messages_GetHistory(peer,limit:100).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        return Find(history.Messages.OfType<Message>(),wanted);
    }
    public static Document? Find(IEnumerable<Message> messages,MusicRequest wanted)
        => messages.Select(m=>(m.media as MessageMediaDocument)?.document).OfType<Document>()
            .Where(doc => AcceptsQuality(wanted, doc) && DeezLoadSource.Fits(wanted, doc))
            .OrderByDescending(doc => TelegramBotSource.Extension(doc) == "flac").FirstOrDefault();
}

internal sealed record MusicLink(string Service,string Kind,string Id,string Url)
{
    public static bool TryParse(string? text,out MusicLink? link)
    {
        link=null;
        if(string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(),UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.UserInfo.Length>0 || !uri.IsDefaultPort) return false;
        var pieces=uri.AbsolutePath.Trim('/').Split('/');
        if(uri.Host.Equals("open.spotify.com",StringComparison.OrdinalIgnoreCase))
        {
            if(pieces.Length==3 && pieces[0].StartsWith("intl-")) pieces=pieces[1..];
            if(pieces.Length!=2 || pieces[0] is not ("track" or "album" or "playlist") || !Regex.IsMatch(pieces[1],@"^[A-Za-z0-9]{22}$")) return false;
            link=new("spotify",pieces[0],pieces[1],"https://open.spotify.com/"+string.Join('/',pieces));return true;
        }
        if(uri.Host is "deezer.com" or "www.deezer.com")
        {
            if(pieces.Length==3 && pieces[0].Length==2) pieces=pieces[1..];
            if(pieces.Length!=2 || pieces[0] is not ("track" or "album" or "playlist") || !Regex.IsMatch(pieces[1],@"^\d{1,20}$")) return false;
            link=new("deezer",pieces[0],pieces[1],"https://www.deezer.com/"+string.Join('/',pieces));return true;
        }
        return false;
    }
}
