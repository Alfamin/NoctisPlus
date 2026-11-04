using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TL;

namespace FreeMusicFinder;

/// <summary>Telegram as the song index needs it (a fake in the tests).</summary>
internal interface IBotChats
{
    bool Ready { get; }
    /// <summary>The bot chat's song files older than <paramref name="olderThan"/> (0: the newest), newest first, up to 100.</summary>
    Task<IReadOnlyList<Message>> SongsAsync(string bot, int olderThan, CancellationToken ct);
    /// <summary>The message with that id as it is now; null when it was deleted.</summary>
    Task<Message?> MessageAsync(string bot, int id, CancellationToken ct);
    Task DownloadAsync(Document document, Stream output, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Where a request looks first: the files the bots already sent (owner #105).</summary>
internal interface IChatFiles
{
    /// <summary>A file a bot already sent that is this recording in an accepted format, transferred into
    /// <paramref name="output"/> (only noted for an ask ahead); its extension, or null when no chat has it.</summary>
    Task<string?> FetchAsync(MusicRequest wanted, Func<string, bool> accepts, Stream output, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>
/// Owner #105: every song file Music Hunters and DeezLoad have ever sent in their chats, kept on this PC, so a song is
/// never asked for twice: not when a queue reshuffles, not when a playlist is played again days later. Built once with
/// Telegram's music search (only song messages, 100 a page), topped up on every start and after each new file, and read
/// from memory: a lookup is a word-index probe (microseconds, any size) and the file is fetched with the reference saved
/// for it, so a song already in a chat starts with no bot message and usually no extra Telegram call. A deleted message
/// or an expired reference is found out at that moment and the song is asked for as before.
/// </summary>
internal sealed class BotLibrary : IChatFiles
{
    internal sealed class Song
    {
        public int Message { get; set; }
        public long Id { get; set; }
        public long Hash { get; set; }
        public byte[] Reference { get; set; } = [];
        public int Dc { get; set; }
        public long Size { get; set; }
        public string Mime { get; set; } = "";
        public string Name { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Title { get; set; } = "";
        public double Seconds { get; set; }
        /// <summary>When <see cref="Reference"/> was read (UTC).</summary>
        public DateTime Seen { get; set; }
        [JsonIgnore] public string Bot { get; set; } = "";
        [JsonIgnore] public string Format => TelegramBotSource.Extension(Document()) ?? "";
        internal Document Document() => new()
        {
            id = Id, access_hash = Hash, file_reference = Reference, dc_id = Dc, size = Size, mime_type = Mime,
            attributes = Name.Length > 0 ? [new DocumentAttributeFilename { file_name = Name }] : [],
        };
    }

    internal sealed class Chat
    {
        /// <summary>The newest message read; everything from it down to <see cref="Oldest"/> is in <see cref="Songs"/>.</summary>
        public int Newest { get; set; }
        public int Oldest { get; set; }
        /// <summary>Read down to the chat's first message.</summary>
        public bool Complete { get; set; }
        public List<Song> Songs { get; set; } = [];
    }

    internal static BotLibrary? Current { get; set; }
    /// <summary>A reference read this recently is used as it is; an older one is read again first (one small call).</summary>
    internal static TimeSpan FreshFor { get; set; } = TimeSpan.FromHours(1);
    internal static TimeSpan PagePause { get; set; } = TimeSpan.FromMilliseconds(300);
    internal static readonly string[] Bots = [TelegramBotSource.Bot, DeezLoadSource.Bot];

    private readonly string _file;
    private readonly IBotChats _chats;
    private readonly object _gate = new();
    private readonly Dictionary<string, Chat> _saved;
    private readonly Dictionary<string, HashSet<int>> _messages = new(StringComparer.Ordinal);
    // The title's core (what the matcher compares: no credits, no additions) finds nearly every file in one probe; the
    // words find the rest (a title Telegram cut short, one written another way).
    private readonly Dictionary<string, List<Song>> _byKey = new(StringComparer.Ordinal), _byWord = new(StringComparer.Ordinal);
    // Owner #105: one entry per recording. A bot sends the same file again for every ask (888 of the owner's 3,821 Music
    // Hunters files were repeats), so a recording already listed keeps only its newest copy.
    private readonly Dictionary<string, Song> _bySame = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _syncs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _again = new(StringComparer.Ordinal), _synced = new(StringComparer.Ordinal);
    private readonly object _saveGate = new();

    public BotLibrary(string file, IBotChats chats)
    {
        _file = file; _chats = chats;
        Dictionary<string, Chat>? saved = null;
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length < 256 * 1024 * 1024)
                saved = JsonSerializer.Deserialize<Dictionary<string, Chat>>(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Noctis.Services.DebugLog.Write("Finder", "song index unreadable, built again: " + ex.Message); }
        _saved = new(StringComparer.Ordinal);
        foreach (var (bot, chat) in saved ?? [])
        {
            if (!Bots.Contains(bot) || chat?.Songs is null) continue;
            var kept = new Chat { Newest = chat.Newest, Oldest = chat.Oldest, Complete = chat.Complete };
            _saved[bot] = kept;
            foreach (var song in chat.Songs) { song.Bot = bot; AddLocked(kept, song); }
        }
    }

    /// <summary>How many song files are indexed (all chats).</summary>
    internal int Count { get { lock (_gate) return _saved.Values.Sum(c => c.Songs.Count); } }
    /// <summary>The chat was read up to its newest message this session and no new file is waiting to be read.</summary>
    internal bool UpToDate(string bot) { lock (_gate) return _synced.Contains(bot) && !_syncs.ContainsKey(bot); }
    private bool Started(string bot) { lock (_gate) return _synced.Contains(bot) || _syncs.ContainsKey(bot); }

    // ── reading the chats ──

    /// <summary>Reads what is new in every chat (and, the first time, the whole history). Safe to call often.</summary>
    internal Task SyncAsync(CancellationToken ct = default) => Task.WhenAll(Bots.Select(b => SyncAsync(b, ct)));

    internal Task SyncAsync(string bot, CancellationToken ct = default)
    {
        if (!_chats.Ready) return Task.CompletedTask;
        lock (_gate)
        {
            if (_syncs.TryGetValue(bot, out var running)) { _again.Add(bot); return running; }
            var sync = Task.Run(() => SyncLoop(bot, ct));
            _syncs[bot] = sync;
            return sync;
        }
    }

    /// <summary>A bot just sent a file: read it into the index right away.</summary>
    internal void Touch(string bot) => _ = SyncAsync(bot);

    private async Task SyncLoop(string bot, CancellationToken ct)
    {
        try
        {
            do
            {
                lock (_gate) _again.Remove(bot);
                await ReadNewAsync(bot, ct).ConfigureAwait(false);
            } while (Again(bot));
            await ReadOlderAsync(bot, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { Noctis.Services.DebugLog.Write("Finder", $"song index: {Name(bot)} not read now: {ex.Message}"); }
        finally { lock (_gate) _syncs.Remove(bot); }
        bool Again(string b) { lock (_gate) return _again.Contains(b); }
    }

    // Newest first, down to what was read before. The first time this is the whole chat.
    private async Task ReadNewAsync(string bot, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int known, older = 0, newest = 0, added = 0, oldest = 0; var start = false;
        lock (_gate) { var chat = ChatLocked(bot); known = chat.Newest; start = known == 0; }
        while (true)
        {
            var page = await _chats.SongsAsync(bot, older, ct).ConfigureAwait(false);
            if (page.Count == 0) { if (start) MarkComplete(bot, oldest); break; }
            newest = Math.Max(newest, page.Max(m => m.id)); older = oldest = page.Min(m => m.id);
            var reached = older <= known;
            added += Add(bot, page.Where(m => m.id > known));
            if (start) lock (_gate) { var chat = ChatLocked(bot); chat.Newest = Math.Max(chat.Newest, newest); chat.Oldest = oldest; }
            if (reached) break; // the end of the chat is the empty page above (a page may hold fewer than 100)
            await Task.Delay(PagePause, ct).ConfigureAwait(false);
        }
        lock (_gate) { var chat = ChatLocked(bot); chat.Newest = Math.Max(chat.Newest, newest); _synced.Add(bot); }
        Save();
        if (added > 0 || start)
            Noctis.Services.DebugLog.Write("Finder", $"song index: {Name(bot)} +{added} ({Count} in all) in {clock.ElapsedMilliseconds} ms");
    }

    // An earlier first reading that stopped part way goes on from where it was.
    private async Task ReadOlderAsync(string bot, CancellationToken ct)
    {
        int older; lock (_gate) { var chat = ChatLocked(bot); if (chat.Complete || chat.Oldest <= 0) return; older = chat.Oldest; }
        var added = 0;
        while (true)
        {
            await Task.Delay(PagePause, ct).ConfigureAwait(false);
            var page = await _chats.SongsAsync(bot, older, ct).ConfigureAwait(false);
            if (page.Count == 0) { MarkComplete(bot, older); break; }
            added += Add(bot, page);
            older = page.Min(m => m.id);
            lock (_gate) ChatLocked(bot).Oldest = older;
            Save();
        }
        Save();
        if (added > 0) Noctis.Services.DebugLog.Write("Finder", $"song index: {Name(bot)} older songs +{added} ({Count} in all)");
    }

    private void MarkComplete(string bot, int oldest) { lock (_gate) { var chat = ChatLocked(bot); chat.Complete = true; if (oldest > 0) chat.Oldest = oldest; } }

    private int Add(string bot, IEnumerable<Message> messages)
    {
        var added = 0;
        lock (_gate)
        {
            var chat = ChatLocked(bot);
            foreach (var m in messages)
                if (m.media is MessageMediaDocument { document: Document doc } && From(doc, m.id) is { } song)
                { song.Bot = bot; if (AddLocked(chat, song)) added++; }
        }
        return added;
    }

    internal static Song? From(Document doc, int message)
    {
        if (DeezLoadSource.Request(doc, null) is not { } sent || TelegramBotSource.Extension(doc) is null) return null;
        return new()
        {
            Message = message, Id = doc.id, Hash = doc.access_hash, Reference = doc.file_reference ?? [], Dc = doc.dc_id,
            Size = doc.size, Mime = doc.mime_type ?? "", Name = doc.attributes?.OfType<DocumentAttributeFilename>().FirstOrDefault()?.file_name ?? "",
            Artist = sent.Artist, Title = sent.Title, Seconds = sent.Seconds ?? 0, Seen = DateTime.UtcNow,
        };
    }

    private Chat ChatLocked(string bot)
    {
        if (!_saved.TryGetValue(bot, out var chat)) _saved[bot] = chat = new();
        return chat;
    }

    private static string Same(Song s) => $"{s.Artist.ToLowerInvariant()}|{s.Title.ToLowerInvariant()}|{s.Format}|{Math.Round(s.Seconds)}";

    private bool AddLocked(Chat chat, Song song)
    {
        if (!_messages.TryGetValue(song.Bot, out var ids)) _messages[song.Bot] = ids = [];
        if (ids.Contains(song.Message)) return false;
        if (_bySame.TryGetValue(Same(song), out var listed))
        {
            // The newest copy in the same chat is kept (the likeliest to still be there); another chat's copy stays first.
            if (listed.Bot != song.Bot || listed.Message > song.Message) return false;
            RemoveLocked(listed);
        }
        ids.Add(song.Message);
        _bySame[Same(song)] = song;
        chat.Songs.Add(song);
        var key = Key(song.Title);
        if (!_byKey.TryGetValue(key, out var same)) _byKey[key] = same = [];
        same.Add(song);
        foreach (var word in Words(song.Title).Distinct())
        {
            if (!_byWord.TryGetValue(word, out var list)) _byWord[word] = list = [];
            list.Add(song);
        }
        return true;
    }

    private void RemoveLocked(Song song)
    {
        if (_saved.TryGetValue(song.Bot, out var chat)) chat.Songs.Remove(song);
        if (_messages.TryGetValue(song.Bot, out var ids)) ids.Remove(song.Message);
        if (_bySame.TryGetValue(Same(song), out var listed) && ReferenceEquals(listed, song)) _bySame.Remove(Same(song));
        if (_byKey.TryGetValue(Key(song.Title), out var same)) same.Remove(song);
        foreach (var word in Words(song.Title).Distinct())
            if (_byWord.TryGetValue(word, out var list)) list.Remove(song);
    }

    private void Save()
    {
        lock (_saveGate)
        try
        {
            Dictionary<string, Chat> copy;
            lock (_gate) copy = _saved.ToDictionary(p => p.Key, p => new Chat { Newest = p.Value.Newest, Oldest = p.Value.Oldest, Complete = p.Value.Complete, Songs = [.. p.Value.Songs] });
            AtomicJson.Write(_file, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Noctis.Services.DebugLog.Write("Finder", "song index not saved: " + ex.Message); }
    }

    // ── looking a song up ──

    private static string Key(string title) => RecordingMatch.TitleKey(RecordingMatch.Uncensored(title));

    /// <summary>The words a title is indexed and looked up by: lower case, without accents or punctuation.</summary>
    internal static IEnumerable<string> Words(string title)
    {
        var plain = new StringBuilder(title.Length);
        foreach (var c in RecordingMatch.Uncensored(title).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Indexed files that are this recording, FLAC first, the newest first. No network: one hash probe, and only
    /// when that finds nothing, a few word probes.</summary>
    internal IReadOnlyList<Song> Find(MusicRequest wanted, Func<string, bool>? accepts = null)
    {
        bool Fits(Song s) => (accepts is null || accepts(s.Format)) &&
            RecordingMatch.Fits(wanted, s.Artist, s.Title, s.Seconds > 0 ? TimeSpan.FromSeconds(s.Seconds) : null);
        static IReadOnlyList<Song> Ordered(IEnumerable<Song> songs)
            => songs.OrderByDescending(s => s.Format == "flac").ThenByDescending(s => s.Seen).ThenByDescending(s => s.Message).ToList();
        lock (_gate)
        {
            if (_byKey.TryGetValue(Key(wanted.Title), out var same) && same.Where(Fits).ToList() is { Count: > 0 } exact) return Ordered(exact);
            var words = Words(wanted.Title).ToArray();
            if (words.Length == 0) return [];
            // Every word of a title is indexed, so a cut-off or reworded title still shares the wanted one's first word or its
            // longest (a feat. credit or a starred word in one of the two still leaves the other).
            var probes = new[] { words[0], words.OrderByDescending(w => w.Length).First() }.Distinct();
            return Ordered(probes.SelectMany(w => _byWord.TryGetValue(w, out var list) ? list : []).Distinct().Where(Fits));
        }
    }

    public async Task<string?> FetchAsync(MusicRequest wanted, Func<string, bool> accepts, Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        if (!_chats.Ready) return null;
        foreach (var bot in Bots) if (!Started(bot)) _ = SyncAsync(bot); // the first lookup of a session also reads what is new
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var song in Find(wanted, accepts))
        {
            ct.ThrowIfCancellationRequested();
            // An ask ahead only has to know the bot has it: nothing is sent and nothing is read.
            if (output is AskOnlyStream asked)
            {
                asked.Found = song.Document();
                Noctis.Services.DebugLog.Write("Finder", $"\"{wanted.Title}\": already in {Name(song.Bot)}'s chat, not asked again");
                return song.Format;
            }
            var document = DateTime.UtcNow - song.Seen < FreshFor ? song.Document() : await ReadAgainAsync(song, ct).ConfigureAwait(false);
            if (document is null) continue;
            TransferProgress.Stage(progress, "In " + Name(song.Bot) + "'s chat already · receiving…", Noctis.Services.Search.OnlineDownloadStep.Receiving);
            try { await _chats.DownloadAsync(document, output, progress, ct).ConfigureAwait(false); }
            catch (RpcException ex) when (ex.Message.StartsWith("FILE_REFERENCE", StringComparison.Ordinal))
            {
                // The saved reference ran out: read the message again and start over (a kept part restored into the
                // output is dropped with it).
                if (await ReadAgainAsync(song, ct).ConfigureAwait(false) is not { } fresh) continue;
                if (output.CanSeek) { output.SetLength(0); output.Position = 0; }
                await _chats.DownloadAsync(fresh, output, progress, ct).ConfigureAwait(false);
            }
            Noctis.Services.DebugLog.Write("Finder", $"\"{wanted.Title}\": from {Name(song.Bot)}'s chat (message {song.Message}), no bot asked, in {clock.Elapsed.TotalSeconds:0.0} s");
            return song.Format;
        }
        return null;
    }

    // The message as it is now: a fresh reference, or null (and out of the index) when it was deleted or changed.
    private async Task<Document?> ReadAgainAsync(Song song, CancellationToken ct)
    {
        var message = await _chats.MessageAsync(song.Bot, song.Message, ct).ConfigureAwait(false);
        if (message?.media is MessageMediaDocument { document: Document doc } && doc.id == song.Id)
        {
            lock (_gate) { song.Reference = doc.file_reference ?? []; song.Hash = doc.access_hash; song.Dc = doc.dc_id; song.Seen = DateTime.UtcNow; }
            _ = Task.Run(Save);
            return doc;
        }
        lock (_gate) RemoveLocked(song);
        Noctis.Services.DebugLog.Write("Finder", $"song index: \"{song.Title}\" is no longer in {Name(song.Bot)}'s chat");
        _ = Task.Run(Save);
        return null;
    }

    private static string Name(string bot) => bot == TelegramBotSource.Bot ? BotSelections.Hunters : bot == DeezLoadSource.Bot ? "DeezLoad" : bot;
}

/// <summary>The signed-in account's bot chats.</summary>
internal sealed class TelegramBotChats(TelegramAccount account) : IBotChats
{
    public bool Ready => account.IsLoggedIn;

    public async Task<IReadOnlyList<Message>> SongsAsync(string bot, int olderThan, CancellationToken ct)
    {
        var client = await account.ConnectAsync(ct).ConfigureAwait(false);
        var peer = await account.BotAsync(client, bot, ct).ConfigureAwait(false);
        var found = await client.Messages_Search(peer, null, new InputMessagesFilterMusic(), offset_id: olderThan, limit: 100)
            .WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        return found.Messages.OfType<Message>().ToArray();
    }

    public async Task<Message?> MessageAsync(string bot, int id, CancellationToken ct)
    {
        var client = await account.ConnectAsync(ct).ConfigureAwait(false);
        var peer = await account.BotAsync(client, bot, ct).ConfigureAwait(false);
        var page = await client.Messages_GetHistory(peer, offset_id: id + 1, limit: 1).WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        return page.Messages.OfType<Message>().FirstOrDefault(m => m.id == id);
    }

    public async Task DownloadAsync(Document document, Stream output, IProgress<double>? progress, CancellationToken ct)
    {
        var client = await account.ConnectAsync(ct).ConfigureAwait(false);
        await AudioFileTransfer.DownloadAsync(account, client, document, output, progress, ct).ConfigureAwait(false);
    }
}
