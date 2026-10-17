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
        _defaults = TelegramDefaults.Read(defaultsPath ?? Path.Combine(Path.GetDirectoryName(storePath)!, "telegram-defaults.private.json"));
        _proxy = proxy;
        _log = log;
    }

    public bool IsLoggedIn => _store.Account.Length > 0;

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
                try { await client.ConnectAsync().ConfigureAwait(false); }
                catch
                {
                    client.Dispose();
                    throw;
                }
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
    private Client? _botsClient;

    /// <summary>
    /// A bot's account, looked up once per connection. Telegram limits how often a username may be
    /// resolved, and a playlist asks for many songs in a row.
    /// </summary>
    public async Task<User> BotAsync(Client client, string username)
    {
        lock (_bots)
            if (ReferenceEquals(_botsClient, client) && _bots.TryGetValue(username, out var known)) return known;
        var user = (await client.Contacts_ResolveUsername(username).ConfigureAwait(false)).User;
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
        var history = await client.Messages_GetHistory(bot, limit: 1).WaitAsync(ct).ConfigureAwait(false);
        if (history.Messages.Length == 0)
        {
            await client.SendMessageAsync(bot, "/start").WaitAsync(ct).ConfigureAwait(false);
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
                    await _client.ConnectAsync().ConfigureAwait(false);
                    await _client.Auth_LogOut().ConfigureAwait(false);
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
        _client?.Dispose();
        _client = null;
    }

    private void Forget()
    {
        _client?.Dispose();
        _client = null;
        _store.ClearSession();
    }

    private async Task<string?> StepAsync(Client client, string answer)
    {
        var next = await client.Login(answer).ConfigureAwait(false);
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
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FileWait = TimeSpan.FromMinutes(3);

    private readonly TelegramAccount _account;
    private readonly Action<string> _log;
    private (Client Client, InputPeer Peer)? _bot;
    // Searches may now run while a download waits for its file. Two searches at once would each take
    // the other's answer, since the bot answers both in the same chat; a download's file is told apart
    // from a search answer by being a file.
    private readonly SemaphoreSlim _searching = new(1, 1);

    public TelegramBotSource(TelegramAccount account, Action<string> log)
    {
        _account = account;
        _log = log;
    }

    /// <summary>
    /// The first page of the bot's results for <paramref name="query"/>; empty when it found
    /// nothing. Throws <see cref="TimeoutException"/> when the bot does not answer and
    /// <see cref="BotAnswerException"/> when its answer cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<BotTrack>> SearchAsync(string query, CancellationToken ct)
    {
        await _searching.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunAsync<IReadOnlyList<BotTrack>>(async (client, bot) =>
            {
                var sent = await client.SendMessageAsync(bot, "/search " + query).ConfigureAwait(false);
                // The answer always carries buttons; without results it has no numbered ones.
                var results = await WaitForAsync(client, bot, sent.id, m => m.media is not MessageMediaDocument && Buttons(m).Any(), SearchWait, ct).ConfigureAwait(false)
                    ?? throw new TimeoutException("The bot did not answer.");

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
            var old=await client.Messages_GetHistory(bot,offset_id:selection.MessageId+1,limit:1).WaitAsync(ct).ConfigureAwait(false);
            var button=ValidateSelection(request,old.Messages.OfType<Message>().FirstOrDefault(m=>m.id==selection.MessageId));
            var resultsId=selection.MessageId;
            var newest = (await client.Messages_GetHistory(bot, limit: 1).ConfigureAwait(false)).Messages.FirstOrDefault()?.ID ?? resultsId;
            string? notice = null;
            try { notice = (await client.Messages_GetBotCallbackAnswer(bot, resultsId, button).ConfigureAwait(false))?.message; }
            // The bot may acknowledge the press later than Telegram waits; the file still arrives.
            catch (RpcException ex) when (ex.Message.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)) { }
            catch(RpcException ex) when(IsStaleCallback(ex.Message))
            {throw new StaleSelectionException("Music Hunters' selection expired; finding a fresh matching result.");}
            if (BotLimits.Read("Music Hunters", notice ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;

            string? other = null;
            var file = await WaitForAsync(client, bot, newest, m =>
            {
                if (m.media is not MessageMediaDocument { document: Document sent }) return false;
                if (Extension(sent) is not null && DeezLoadSource.Fits(request, sent)) return true;
                other = Describe(sent);
                return false;
            }, FileWait, ct).ConfigureAwait(false);
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

    public async Task<string> FetchSelectedAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct)
    {
            var document=await RequestSelectedDocumentAsync(request,ct).ConfigureAwait(false);
            var client=await _account.ConnectAsync(ct).ConfigureAwait(false);
            var lastReport = 0.0;
            await client.DownloadFileAsync(document, output, (PhotoSizeBase?)null, (done, total) =>
            {
                ct.ThrowIfCancellationRequested();
                if (total > 0 && (double)done / total - lastReport >= 0.02) progress?.Report(lastReport = (double)done / total);
            }).ConfigureAwait(false);
            return Extension(document)!;
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
            var history = await client.Messages_GetHistory(bot, min_id: afterId, limit: 30).ConfigureAwait(false);
            foreach (var reply in history.Messages.OfType<Message>().Where(m => !m.flags.HasFlag(Message.Flags.out_)))
                if (BotLimits.Read("Music Hunters", reply.message ?? "", DateTimeOffset.UtcNow) is { } limited) throw limited;
            var found = history.Messages.OfType<Message>().Reverse()
                .FirstOrDefault(m => !m.flags.HasFlag(Message.Flags.out_) && matches(m));
            if (found is not null) return found;

            var waited = TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (waited >= timeout) return null;
            await Task.Delay(PollInterval(waited), ct).ConfigureAwait(false);
        }
    }

    private static TimeSpan PollInterval(TimeSpan waited)
        => TimeSpan.FromSeconds(waited < TimeSpan.FromSeconds(15) ? 1.5 : waited < TimeSpan.FromMinutes(1) ? 3 : 5);

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
    private static string? Extension(Document document)
    {
        var fromName = Path.GetExtension(document.Filename ?? "").TrimStart('.').ToLowerInvariant();
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
