using System.Net;
using System.Net.Sockets;
using System.Text;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>The parts that need neither Telegram nor a window.</summary>
internal static class LogicTests
{
    public static async Task RunAsync()
    {
        PrivateDefaults();
        ResultList();
        FileNames();
        RightFile();
        SameSong();
        ProxySetting();
        ComputerProxy();
        ShortcutSetting();
        await ProxiesAsync();
        await MetadataRouteAsync();
        await SavingAsync();
        Leftovers();
        await QueueAsync();
    }

    private static void PrivateDefaults()
    {
        Check.Section("private Telegram defaults and saved-account precedence");
        var folder = Check.NewFolder("private-defaults");
        var path = Path.Combine(folder, "telegram-defaults.private.json");
        const string hash = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(path, "{\"api_id\":12345,\"api_hash\":\"" + hash + "\"}");
        var config = TelegramDefaults.Read(path);
        Check.True(config.Available, "valid private defaults accepted");
        using var account = new TelegramAccount(Path.Combine(folder, "telegram.dat"), () => "");
        Check.Equal(12345, account.ApiId, "new account uses deployment defaults");
        Check.True(account.UsesBuiltInApi, "owner #185: the built-in pair is used without being saved as the user's own");
        Check.False(account.IsLoggedIn, "defaults never authenticate somebody else's account");
        var store = new TelegramStore(Path.Combine(folder, "saved.dat"));
        store.Begin(98765, "saved private hash");
        using var saved = new TelegramAccount(Path.Combine(folder, "saved.dat"), () => "");
        Check.Equal(98765, saved.ApiId, "existing custom API settings take precedence");
        Check.False(saved.UsesBuiltInApi, "a pair of the user's own is not the built-in one");
        Check.Equal("saved private hash", saved.ApiHash, "saved API hash is preserved");
        store.ClearSession();
        using var loggedOut = new TelegramAccount(Path.Combine(folder, "saved.dat"), () => "");
        Check.Equal(98765, loggedOut.ApiId, "logging out keeps the custom API id for the next login");
        Check.Equal("saved private hash", loggedOut.ApiHash, "logging out keeps the custom API hash");
        File.WriteAllText(path, "{\"api_id\":0,\"api_hash\":\"invalid secret\"}");
        using var customAfterDefaultsChanged = new TelegramAccount(Path.Combine(folder, "saved.dat"), () => "");
        Check.Equal(98765, customAfterDefaultsChanged.ApiId, "invalid replacement defaults cannot override the user's custom pair");
        Check.Equal("", customAfterDefaultsChanged.DefaultsError, "an unused broken defaults file does not block a saved custom account");
        config = TelegramDefaults.Read(path);
        Check.False(config.Available, "invalid defaults do not enable login");
        Check.True(config.Error.StartsWith("TELEGRAM_DEFAULTS_INVALID"), "invalid defaults have a stable error code");
        Check.False(config.Error.Contains("invalid secret"), "private values are not exposed in errors");
        File.WriteAllText(path, new string('x', 4097));
        Check.False(TelegramDefaults.Read(path).Available, "oversized defaults rejected");
        Check.False(TelegramDefaults.Read(Path.Combine(folder, "missing.json")).Available, "public installation works without private defaults");
        var warm = new TelegramStore(Path.Combine(folder, "warm.dat"));
        warm.Begin(0, "");
        using (var session = warm.OpenSession()) session.Write([1, 2, 3], 0, 3);
        warm.BeginKeepingKey(0, "");
        Check.True(warm.HasSession, "owner #185: a signed-out session keeps its handshake for the next QR code");
        warm.BeginKeepingKey(98765, "fedcba9876543210fedcba9876543210");
        Check.False(warm.HasSession, "another API pair starts from a new session");
        warm.OpenSession().Write([1, 2, 3], 0, 3);
        warm.SetAccount("@someone");
        warm.BeginKeepingKey(98765, "fedcba9876543210fedcba9876543210");
        Check.False(warm.HasSession, "a signed-in session is never reused for a new sign-in");
        var packed = Path.Combine(folder, TelegramDefaults.PackedFile);
        File.WriteAllBytes(packed, TelegramDefaults.Pack("{\"api_id\":12345,\"api_hash\":\"" + hash + "\"}"));
        var shipped = TelegramDefaults.ReadPacked(packed);
        Check.True(shipped.Available && shipped.ApiId == 12345 && shipped.ApiHash == hash, "owner #186: the shipped pair is read from its scrambled file");
        var raw = File.ReadAllText(packed);
        Check.False(raw.Contains(hash) || raw.Contains("12345") || raw.Contains("api_hash"), "the shipped file does not show the pair as text");
    }

    private static BotText.Line Line(string artist, string title, string? length = null, string number = "1")
        => new(number, artist, title, length is null ? null : BotText.ParseDuration(length));

    private static void ResultList()
    {
        Check.Section("reading the bot's result list");
        var lines = BotText.Lines("Results for \"x\":\n\n1. Kevin MacLeod - Darkest Child (3:59)\n2. Some Artist - A Song - Remastered\n3. DJ Long - The Set (1:02:03)\r\n4. Artist - Song (Live) (4:10)\nPage 1 of 3");
        Check.Equal(4, lines.Count, "four result lines");
        Check.Equal("Kevin MacLeod|Darkest Child|00:03:59", $"{lines[0].Artist}|{lines[0].Title}|{lines[0].Duration}", "artist, title, length");
        Check.Equal("Some Artist|A Song - Remastered|", $"{lines[1].Artist}|{lines[1].Title}|{lines[1].Duration}", "a title with a dash, no length");
        Check.Equal(TimeSpan.FromSeconds(3723), lines[2].Duration, "a length with hours");
        Check.Equal("Song (Live)", lines[3].Title, "brackets in the title stay; the length is taken off");
        Check.Equal(0, BotText.Lines("🎵 Darkest Child — Kevin MacLeod\n🎵 Another one").Count, "another format reads as no lines");
        Check.Equal(null, BotText.ParseDuration("99999999999:00"), "an absurd length is no length");
    }

    private static void FileNames()
    {
        Check.Section("file names");
        var names = Downloader.FileNames(new[]
        {
            Line("A", "Song", "4:18"), Line("A", "Song", "5:02"), Line("A", "Song", "4:18"), Line("B", "Other", "3:00"),
            Line("AC/DC", "Who? What: \"That\"", "3:00"), Line("C", "Twice"), Line("c", "twice"),
        });
        Check.Equal("A - Song (4.18)", names[0], "album and live version get their lengths");
        Check.Equal("A - Song (5.02)", names[1], "the other version");
        Check.Equal(names[0], names[2], "the very same track twice is one file");
        Check.Equal("B - Other", names[3], "a name that is alone stays plain");
        Check.Equal("AC_DC - Who_ What_ _That_", names[4], "characters a file name cannot have");
        Check.Equal("C - Twice", names[5], "the same name without lengths stays plain");
        var longName = Downloader.FileNames(new[] { Line(new string('x', 200), "T", "1:00"), Line(new string('x', 200), "T", "2:00") });
        Check.True(longName[0] != longName[1] && longName[0].EndsWith(" (1.00)"), "a cut-off long name still keeps its length");
    }

    private static void RightFile()
    {
        Check.Section("is the file the bot sent the one asked for");
        var wanted = Line("Kevin MacLeod", "Darkest Child", "3:59");
        Check.True(BotText.IsRequested(wanted, "Kevin MacLeod", "Darkest Child", "x.mp3", 239), "same name and length");
        Check.True(BotText.IsRequested(wanted, "", "", "", 240), "no names, length one second off");
        Check.True(BotText.IsRequested(wanted, null, null, "Kevin MacLeod - Darkest Child.flac", 0), "only a file name");
        Check.True(BotText.IsRequested(wanted, "KEVIN MACLEOD", "Darkest  Child (Remastered)", null, 250), "longer title, other length");
        Check.True(BotText.IsRequested(wanted, null, null, null, 0), "a file that says nothing about itself is taken");
        Check.False(BotText.IsRequested(wanted, "Other Artist", "Another Song", "Other Artist - Another Song.mp3", 201), "another song (the late file of an earlier request)");
        Check.False(BotText.IsRequested(wanted, null, null, "promo.mp3", 30), "a short clip under another name");
        Check.False(BotText.IsRequested(Line("X", "Go", "3:00"), "Y", "Gone Forever", "Y - Gone Forever.mp3", 250), "a short title is not found inside another");
        Check.True(BotText.IsRequested(Line("X", "Go", "3:00"), "X", "Go", null, 0), "a short title that is equal");
        Check.True(BotText.IsRequested(Line("گوگوش", "من آمده ام"), "گوگوش", "من آمده‌ام", null, 0), "Persian title, written with and without the half space");
        Check.True(BotText.IsRequested(Line("A", "علي"), "A", "علی", null, 0), "Arabic and Persian ya are the same letter");
    }

    private static void SameSong()
    {
        Check.Section("is a result already in the library");
        static TimeSpan t(int seconds) => TimeSpan.FromSeconds(seconds);
        Check.True(BotText.SameSong("Kevin MacLeod", "Darkest Child", t(239), "Kevin Macleod", "Darkest Child", t(240)), "same song");
        Check.False(BotText.SameSong("Kevin MacLeod", "Darkest Child", t(239), "Kevin MacLeod", "Darkest Child", t(300)), "same name, other length: another version");
        Check.True(BotText.SameSong("A", "Song (Remastered 2011)", t(200), "A", "Song", t(201)), "bracketed addition, same length");
        Check.False(BotText.SameSong("A", "Song (Live)", null, "A", "Song", t(201)), "bracketed addition, length unknown");
        Check.False(BotText.SameSong("A", "Song", t(200), "Somebody Else", "Song", t(200)), "another artist");
        Check.True(BotText.SameSong("Artist feat. Guest", "Song", null, "Artist", "Song", t(200)), "artist with a guest");
        Check.False(BotText.SameSong("A", "Song", t(200), "A", "Song Two", t(200)), "another title");
        Check.Equal("Song", BotText.Plain("Song (Live) [2011]"), "title without brackets");
    }

    private static void ShortcutSetting()
    {
        Check.Section("the shortcut setting");
        static string Read(string text, bool isMac = false) => Shortcut.Parse(text, isMac) is { } g ? $"{g.Key}|{g.KeyModifiers}" : "off";
        Check.Equal("F|Control, Shift", Read(Shortcut.Default), "the default is Ctrl+Shift+F");
        Check.Equal("M|Alt, Control", Read(" ctrl+alt+m "), "written in small letters, with spaces around");
        Check.Equal("F|Shift, Meta", Read(Shortcut.Default, isMac: true), "on a Mac, Ctrl means Cmd");
        Check.Equal("F9|None", Read("F9"), "a function key alone is fine");
        Check.Equal("off", Read(""), "empty is off");
        Check.Equal("off", Read("Off"), "so is \"off\"");
        Check.Throws<FormatException>(() => Shortcut.Parse("Shift+F", false), "a letter without Ctrl or Alt would fire while typing");
        Check.Throws<FormatException>(() => Shortcut.Parse("M", false), "so would a letter alone");
        Check.Throws<FormatException>(() => Shortcut.Parse("Ctrl+Banana", false), "a key that does not exist");
        Check.Throws<FormatException>(() => Shortcut.Parse("Ctrl+Shift", false), "modifiers without a key");
    }

    private static void ProxySetting()
    {
        Check.Section("the proxy setting");
        Check.Equal(ProxyKind.System, ProxyChoice.Parse("  ")!.Kind, "empty means: what the computer is set to use");
        Check.Equal(ProxyKind.System, ProxyChoice.Parse("System")!.Kind, "so does \"system\"");
        Check.Equal(null, ProxyChoice.Parse("direct"), "\"direct\" means no proxy at all");
        var mt = ProxyChoice.Parse("https://t.me/proxy?server=1.2.3.4&port=443&secret=ee0123abcd")!;
        Check.Equal("https://t.me/proxy?server=1.2.3.4&port=443&secret=ee0123abcd", mt.MtProxyUrl, "Telegram proxy link");
        Check.Equal(mt.MtProxyUrl, ProxyChoice.Parse("tg://proxy?server=1.2.3.4&port=443&secret=ee0123abcd")!.MtProxyUrl, "tg:// form of the same link");
        Check.Equal(mt.MtProxyUrl, ProxyChoice.Parse("t.me/proxy?server=1.2.3.4&port=443&secret=ee0123abcd")!.MtProxyUrl, "link without https://");
        var socks = ProxyChoice.Parse("socks5://127.0.0.1:1080")!;
        Check.Equal("Socks5||127.0.0.1|1080||", $"{socks.Kind}|{socks.MtProxyUrl}|{socks.Host}|{socks.Port}|{socks.User}|{socks.Password}", "socks5 without login");
        var login = ProxyChoice.Parse("socks5://me:p%40ss@proxy.example:9050")!;
        Check.Equal("proxy.example|9050|me|p@ss", $"{login.Host}|{login.Port}|{login.User}|{login.Password}", "socks5 with login");
        var shared = ProxyChoice.Parse("https://t.me/socks?server=5.6.7.8&port=1080&user=u&pass=p")!;
        Check.Equal("|5.6.7.8|1080|u|p", $"{shared.MtProxyUrl}|{shared.Host}|{shared.Port}|{shared.User}|{shared.Password}", "Telegram's socks link");
        var http = ProxyChoice.Parse("http://me:p%40ss@10.0.0.1:8080")!;
        Check.Equal("Http|10.0.0.1|8080|me|p@ss", $"{http.Kind}|{http.Host}|{http.Port}|{http.User}|{http.Password}", "an HTTP proxy");
        var bare = ProxyChoice.Parse("127.0.0.1:10808")!;
        Check.Equal("HttpOrSocks5|127.0.0.1|10808", $"{bare.Kind}|{bare.Host}|{bare.Port}", "only host:port, the way Windows shows a proxy");
        foreach (var bad in new[] { "hello", "127.0.0.1", "127.0.0.1:99999", "socks5://host", "https://example.com/proxy?server=a&port=1&secret=b", "https://t.me/proxy?server=a&port=1", "http://1.2.3.4:8080/page", "https://1.2.3.4:8080" })
            Check.Throws<FormatException>(() => ProxyChoice.Parse(bad), "refused: " + bad);
    }

    private static void ComputerProxy()
    {
        Check.Section("the computer's own proxy setting");
        static string Show(ProxyChoice? proxy) => proxy is null ? "none" : $"{proxy.Kind} {proxy.Host}:{proxy.Port}";
        const string telegram = "149.154.167.51";
        const string without = "<local>;localhost;127.*;10.*;192.168.*";
        Check.Equal("Http 127.0.0.1:10808", Show(SystemProxy.FromWindows("127.0.0.1:10808", without, telegram)), "Windows: one address for everything");
        Check.Equal("none", Show(SystemProxy.FromWindows("127.0.0.1:10808", without, "192.168.1.5")), "an address on the list of exceptions goes without");
        Check.Equal("none", Show(SystemProxy.FromWindows("127.0.0.1:10808", without, "nas")), "so does a name without a dot (<local>)");
        Check.Equal("Http 127.0.0.1:10808", Show(SystemProxy.FromWindows("127.0.0.1:10808", null, "nas")), "no list of exceptions");
        Check.Equal("Http 10.0.0.2:3128", Show(SystemProxy.FromWindows("http=10.0.0.1:8080;https=10.0.0.2:3128;socks=10.0.0.3:1080", "", telegram)), "one address per kind: the one for secure pages is taken");
        Check.Equal("Socks5 10.0.0.3:1080", Show(SystemProxy.FromWindows("http=10.0.0.1:8080;socks=10.0.0.3", "", telegram)), "without that one the SOCKS proxy, on its usual port when none is given");
        Check.Equal("Http 10.0.0.1:8080", Show(SystemProxy.FromWindows("http=http://10.0.0.1:8080", "", telegram)), "and last the one for plain pages");
        Check.Equal("none", Show(SystemProxy.FromWindows("ftp=10.0.0.1:21", "", telegram)), "nothing that can be used");

        Check.Equal("Http 127.0.0.1:10808", Show(SystemProxy.From(new WebProxy("http://127.0.0.1:10808"), telegram, 443)), "what .NET knows (macOS, Linux, a script): an HTTP proxy");
        Check.Equal("Socks5 127.0.0.1:1080", Show(SystemProxy.From(new WebProxy("socks5://127.0.0.1:1080"), telegram, 443)), "a SOCKS proxy");
        Check.Equal("none", Show(SystemProxy.From(new WebProxy("http://127.0.0.1:10808") { BypassList = new[] { @"149\.154\..*" } }, telegram, 443)), "an exception");
        Check.Equal("none", Show(SystemProxy.From(null, telegram, 443)), "no proxy");
        var withLogin = SystemProxy.From(new WebProxy("http://10.0.0.1:8080") { Credentials = new NetworkCredential("me", "secret") }, telegram, 443)!;
        Check.Equal("me:secret", withLogin.User + ":" + withLogin.Password, "with its name and password");
        Console.WriteLine("        (this computer, right now: " + Show(SystemProxy.For(telegram, 443)) + ")");
    }

    /// <summary>A SOCKS5 proxy, an HTTP proxy and a target that answers "pong", all on this computer.</summary>
    private static async Task ProxiesAsync()
    {
        Check.Section("connecting through a SOCKS5 proxy");
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var client = await target.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var ping = new byte[4];
                await stream.ReadExactlyAsync(ping);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("pong"));
            }
        });

        var asked = new List<string>();
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await proxy.AcceptTcpClientAsync();
                _ = Task.Run(() => FakeSocksAsync(client, asked));
            }
        });

        async Task<string> PingAsync(ProxyChoice through, string host)
        {
            using var tcp = await through.ConnectAsync(host, targetPort);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("ping"));
            var answer = new byte[4];
            await stream.ReadExactlyAsync(answer);
            return Encoding.ASCII.GetString(answer);
        }

        Check.Equal("pong", await PingAsync(ProxyChoice.Parse($"socks5://127.0.0.1:{proxyPort}")!, "127.0.0.1"), "without login, to an address");
        Check.Equal("pong", await PingAsync(ProxyChoice.Parse($"socks5://me:secret@127.0.0.1:{proxyPort}")!, "localhost"), "with login, to a name");
        Check.Equal("none ip 127.0.0.1 | me:secret name localhost", string.Join(" | ", asked), "what the proxy was asked");
        await Check.ThrowsAsync<IOException>(() => PingAsync(ProxyChoice.Parse($"socks5://me:wrong@127.0.0.1:{proxyPort}")!, "127.0.0.1"), "a wrong password is reported");
        await Check.ThrowsAsync<IOException>(() => PingAsync(ProxyChoice.Parse($"socks5://127.0.0.1:{proxyPort}")!, "refuse.me"), "a refused connection is reported");

        using var web = new TcpListener(IPAddress.Loopback, 0);
        web.Start();
        _ = Task.Run(async () =>
        {
            using var client = await web.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"));
        });
        await Check.ThrowsAsync<IOException>(() => PingAsync(ProxyChoice.Parse($"socks5://127.0.0.1:{((IPEndPoint)web.LocalEndpoint).Port}")!, "127.0.0.1"), "something that is no SOCKS5 proxy is reported");

        Check.Section("connecting through an HTTP proxy");
        var tunnels = new List<string>();
        using var httpProxy = new TcpListener(IPAddress.Loopback, 0);
        httpProxy.Start();
        var httpPort = ((IPEndPoint)httpProxy.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await httpProxy.AcceptTcpClientAsync();
                _ = Task.Run(() => FakeHttpProxyAsync(client, tunnels));
            }
        });
        Check.Equal("pong", await PingAsync(ProxyChoice.Parse($"http://127.0.0.1:{httpPort}")!, "127.0.0.1"), "without login, to an address");
        Check.Equal("pong", await PingAsync(ProxyChoice.Parse($"http://me:secret@127.0.0.1:{httpPort}")!, "localhost"), "with login, to a name");
        Check.Equal($"none 127.0.0.1:{targetPort} | me:secret localhost:{targetPort}", string.Join(" | ", tunnels), "what the proxy was asked");
        await Check.ThrowsAsync<ProxyRefusedException>(() => PingAsync(ProxyChoice.Parse($"http://me:wrong@127.0.0.1:{httpPort}")!, "127.0.0.1"), "a wrong password is reported");
        await Check.ThrowsAsync<ProxyRefusedException>(() => PingAsync(ProxyChoice.Parse($"http://127.0.0.1:{httpPort}")!, "refuse.me"), "a refused connection is reported");
        await Check.ThrowsAsync<IOException>(() => PingAsync(ProxyChoice.Parse($"http://127.0.0.1:{proxyPort}")!, "127.0.0.1"), "something that is no HTTP proxy is reported");

        Check.Section("connecting through a proxy of which only host:port is known");
        tunnels.Clear();
        asked.Clear();
        Check.Equal("pong", await PingAsync(ProxyChoice.Parse($"127.0.0.1:{httpPort}")!, "127.0.0.1"), "an HTTP proxy is found out");
        Check.Equal("pong", await PingAsync(ProxyChoice.Parse($"127.0.0.1:{proxyPort}")!, "127.0.0.1"), "a SOCKS5 proxy is found out");
        Check.Equal("1 1", $"{tunnels.Count} {asked.Count}", "each was asked once");
        await Check.ThrowsAsync<ProxyRefusedException>(() => PingAsync(ProxyChoice.Parse($"127.0.0.1:{httpPort}")!, "refuse.me"), "an HTTP proxy that says no is believed");
        using var nobody = new TcpListener(IPAddress.Loopback, 0);
        nobody.Start();
        var free = ((IPEndPoint)nobody.LocalEndpoint).Port;
        nobody.Stop();
        await Check.ThrowsAsync<SocketException>(() => PingAsync(ProxyChoice.Parse($"127.0.0.1:{free}")!, "127.0.0.1"), "a proxy that is not there is reported");
    }

    /// <summary>
    /// The playlist reader's way to the web: the plugin's setting first, otherwise the computer's
    /// proxy, asked at every connection. A web server, an HTTP proxy and a SOCKS5 proxy, all on
    /// this computer; "playlist.test" is a name only the proxies know the way to.
    /// </summary>
    private static async Task MetadataRouteAsync()
    {
        Check.Section("the way Spotify and Deezer are reached");
        // It closes after each answer, so every request opens a connection of its own.
        using var web = new TcpListener(IPAddress.Loopback, 0);
        web.Start();
        var webPort = ((IPEndPoint)web.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await web.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var _ = client;
                        var stream = client.GetStream();
                        var head = new StringBuilder();
                        var one = new byte[1];
                        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        {
                            if (await stream.ReadAsync(one) == 0) return;
                            head.Append((char)one[0]);
                        }
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    }
                    catch (IOException)
                    {
                    }
                });
            }
        });

        var tunnels = new List<string>();
        using var httpProxy = new TcpListener(IPAddress.Loopback, 0);
        httpProxy.Start();
        var httpPort = ((IPEndPoint)httpProxy.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await httpProxy.AcceptTcpClientAsync();
                _ = Task.Run(() => FakeHttpProxyAsync(client, tunnels));
            }
        });
        var asked = new List<string>();
        using var socksProxy = new TcpListener(IPAddress.Loopback, 0);
        socksProxy.Start();
        var socksPort = ((IPEndPoint)socksProxy.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await socksProxy.AcceptTcpClientAsync();
                _ = Task.Run(() => FakeSocksAsync(client, asked));
            }
        });
        using var nobody = new TcpListener(IPAddress.Loopback, 0);
        nobody.Start();
        var free = ((IPEndPoint)nobody.LocalEndpoint).Port;
        nobody.Stop();

        var viaHttp = new ProxyChoice(ProxyKind.Http, "127.0.0.1", httpPort);
        var viaSocks = new ProxyChoice(ProxyKind.Socks5, "127.0.0.1", socksPort);
        ProxyChoice? computer = null;
        var said = new List<string>();
        static async Task<string> Read(HttpClient http, string address)
        {
            try { return await http.GetStringAsync("http://" + address + "/"); }
            catch (HttpRequestException ex) { return ex.Message; }
        }
        async Task<string> Once(string setting, string address)
        {
            using var http = new HttpClient(new MetadataRoute(setting, null, (_, _) => computer));
            return await Read(http, address);
        }
        string Asked() => $"{tunnels.Count} {asked.Count}";

        // One reader, kept while the computer's setting changes: as Noctis is while a VPN is switched on and off.
        using (var kept = new HttpClient(new MetadataRoute("", text => said.Add(text), (_, _) => computer)))
        {
            computer = viaHttp;
            Check.Equal("ok", await Read(kept, $"playlist.test:{webPort}"), "the setting is empty and the computer's proxy is on: read through it");
            Check.Equal($"none playlist.test:{webPort}", string.Join(" | ", tunnels), "the proxy is given the name; it is not looked up here, where a blocked name gets a wrong answer");
            computer = null;
            Check.Equal("ok", await Read(kept, $"127.0.0.1:{webPort}"), "switched off while Noctis runs: the next connection goes directly");
            Check.Equal("1 0", Asked(), "without a proxy");
            computer = viaSocks;
            Check.Equal("ok", await Read(kept, $"playlist.test:{webPort}"), "switched to a SOCKS proxy: read through that");
            Check.Equal("none name playlist.test", string.Join(" | ", asked), "by its name as well");
            Check.Equal($"reading playlist metadata through the computer's proxy 127.0.0.1:{httpPort} | reading playlist metadata directly (the computer has no proxy switched on) | reading playlist metadata through the computer's proxy 127.0.0.1:{socksPort}",
                string.Join(" | ", said), "the log says which way, once each time it changes");
        }

        tunnels.Clear();
        asked.Clear();
        computer = viaSocks;
        Check.Equal("ok", await Once($"http://127.0.0.1:{httpPort}", $"playlist.test:{webPort}"), "a proxy named in the plugin's setting is the one used");
        Check.Equal("1 0", Asked(), "and not the computer's");
        Check.Equal("ok", await Once("direct", $"127.0.0.1:{webPort}"), "\"direct\" goes without the computer's proxy");
        Check.Equal("1 0", Asked(), "no proxy was asked");
        Check.Equal("ok", await Once("https://t.me/proxy?server=1.2.3.4&port=443&secret=ee0123abcd", $"playlist.test:{webPort}"), "a Telegram proxy carries Telegram only: the reader goes the computer's way");
        Check.Equal("1 1", Asked(), "through the computer's proxy");

        computer = viaHttp;
        Check.Equal($"METADATA_UNREACHABLE: refuse.me could not be reached through the computer's proxy 127.0.0.1:{httpPort}. The proxy could not connect to refuse.me (answer 502). Check that the VPN or proxy is connected, then retry.",
            await Once("", $"refuse.me:{webPort}"), "a proxy that says no: which way was tried, what it said, what to do");
        computer = new ProxyChoice(ProxyKind.Http, "127.0.0.1", free);
        var gone = await Once("", $"playlist.test:{webPort}");
        Check.True(gone.StartsWith($"METADATA_UNREACHABLE: playlist.test could not be reached through the computer's proxy 127.0.0.1:{free}. ", StringComparison.Ordinal)
            && gone.EndsWith(". Check that the VPN or proxy is connected, then retry.", StringComparison.Ordinal), "a proxy that is not there (a VPN app that was closed): " + gone);
        computer = null;
        var blocked = await Once("", $"127.0.0.1:{free}");
        Check.True(blocked.StartsWith("METADATA_UNREACHABLE: 127.0.0.1 could not be reached directly (the computer has no proxy switched on). ", StringComparison.Ordinal)
            && blocked.EndsWith(". Where it is blocked, turn on a VPN or its system proxy, or set a proxy in the plugin's settings, then retry.", StringComparison.Ordinal), "no proxy and no answer: " + blocked);

        HttpMessageHandler? made = null;
        try { made = PlaylistCatalog.MetadataHandler("hello"); }
        catch (FormatException) { }
        Check.True(made is not null, "a setting that is not understood does not stop the plugin as it starts");
        made?.Dispose();
        Check.Equal("METADATA_PROXY_SETTING: The proxy setting is not understood. " + ProxyChoice.Help, await Once("hello", $"127.0.0.1:{webPort}"), "it fails the request, with its reason");

        // A proxy that takes the connection and then says nothing, as a VPN app that has lost its server does.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var silentPort = ((IPEndPoint)silent.LocalEndpoint).Port;
        var waiting = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await silent.AcceptTcpClientAsync();
                lock (waiting) waiting.Add(client);
            }
        });
        var lost = new ProxyChoice(ProxyKind.Http, "127.0.0.1", silentPort);
        using (var catalog = new PlaylistCatalog(new MetadataRoute("", null, (_, _) => lost, TimeSpan.FromSeconds(1))))
        {
            MusicLink.TryParse("https://open.spotify.com/playlist/" + new string('0', 22), out var link);
            var answer = "a list was read";
            try { await catalog.ReadAsync(link!, null, _ => { }, _ => { }, default); }
            catch (TimeoutException ex) { answer = ex.Message; }
            Check.Equal($"METADATA_TIMEOUT: Spotify did not answer in time through the computer's proxy 127.0.0.1:{silentPort}. Check that the VPN or proxy is connected, then retry.",
                answer, "a proxy that has lost its server: the playlist reader says so, by name");
        }
        lock (waiting) foreach (var client in waiting) client.Dispose();
    }

    private static async Task FakeHttpProxyAsync(TcpClient client, List<string> asked)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one) == 0) return;
                head.Append((char)one[0]);
            }
            var lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var words = lines[0].Split(' ');
            if (words.Length != 3 || words[0] != "CONNECT") return;
            const string header = "Proxy-Authorization: Basic ";
            var login = lines.FirstOrDefault(l => l.StartsWith(header, StringComparison.Ordinal)) is { } line
                ? Encoding.UTF8.GetString(Convert.FromBase64String(line[header.Length..]))
                : "none";
            Task Answer(string status) => stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n")).AsTask();
            if (login is not ("none" or "me:secret"))
            {
                await Answer("407 Proxy Authentication Required");
                return;
            }
            if (words[1].StartsWith("refuse.me:", StringComparison.Ordinal))
            {
                await Answer("502 Bad Gateway");
                return;
            }
            lock (asked) asked.Add(login + " " + words[1]);

            using var onward = new TcpClient();
            await onward.ConnectAsync(IPAddress.Loopback, int.Parse(words[1][(words[1].LastIndexOf(':') + 1)..]));
            await Answer("200 Connection established");
            var there = onward.GetStream();
            await Task.WhenAny(stream.CopyToAsync(there), there.CopyToAsync(stream));
        }
        catch (IOException)
        {
        }
    }

    private static async Task FakeSocksAsync(TcpClient client, List<string> asked)
    {
        try
        {
            using var _ = client;
            var stream = client.GetStream();
            async Task<byte[]> Read(int count)
            {
                var buffer = new byte[count];
                await stream.ReadExactlyAsync(buffer);
                return buffer;
            }

            var hello = await Read(2);
            if (hello[0] != 5) return; // not SOCKS5: hung up on, as a real proxy does
            var methods = await Read(hello[1]);
            var login = "none";
            if (methods.Contains((byte)2))
            {
                await stream.WriteAsync(new byte[] { 5, 2 });
                var user = Encoding.UTF8.GetString(await Read((await Read(2))[1]));
                var password = Encoding.UTF8.GetString(await Read((await Read(1))[0]));
                login = user + ":" + password;
                var ok = password == "secret";
                await stream.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
                if (!ok) return;
            }
            else
            {
                await stream.WriteAsync(new byte[] { 5, 0 });
            }

            var request = await Read(4);
            string host;
            if (request[3] == 1) host = "ip " + new IPAddress(await Read(4));
            else host = "name " + Encoding.UTF8.GetString(await Read((await Read(1))[0]));
            var portBytes = await Read(2);
            var port = (portBytes[0] << 8) | portBytes[1];
            if (host == "name refuse.me")
            {
                await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
                return;
            }
            lock (asked) asked.Add(login + " " + host);

            using var onward = new TcpClient();
            await onward.ConnectAsync(IPAddress.Loopback, port);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 3, 4, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0, 80 }); // a name as the bound address
            var there = onward.GetStream();
            await Task.WhenAny(stream.CopyToAsync(there), there.CopyToAsync(stream));
        }
        catch (IOException)
        {
        }
    }

    internal static BotTrack Track(string name, string extension = "mp3", int bytes = 5000, Func<Task>? before = null, Exception? fails = null)
        => new(name, "Artist", TimeSpan.FromSeconds(200), "Artist - " + name, async (output, progress, ct) =>
        {
            if (before is not null) await before();
            if (fails is not null) throw fails;
            for (var done = 0; done < bytes; done += 1000)
            {
                ct.ThrowIfCancellationRequested();
                await output.WriteAsync(new byte[Math.Min(1000, bytes - done)], ct);
                progress?.Report((done + 1000.0) / Math.Max(bytes, 1000));
            }
            return extension;
        });

    private static async Task SavingAsync()
    {
        Check.Section("saving a track");
        var folder = Check.NewFolder("saving");
        Check.Equal(DownloadOutcome.Saved, await Downloader.DownloadAsync(Track("One", "flac"), folder, null, default), "saved");
        Check.Equal("Artist - One.flac", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName)), "under its name, and nothing else in the folder");
        Check.Equal(5000L, new FileInfo(Path.Combine(folder, "Artist - One.flac")).Length, "complete");
        Check.Equal(DownloadOutcome.AlreadyThere, await Downloader.DownloadAsync(Track("One", "mp3"), folder, null, default), "not fetched twice, whatever the format");

        await Check.ThrowsAsync<InvalidDataException>(() => Downloader.DownloadAsync(Track("Bad", "exe"), folder, null, default), "a file that is not a song is refused");
        await Check.ThrowsAsync<InvalidDataException>(() => Downloader.DownloadAsync(Track("Empty", "mp3", bytes: 0), folder, null, default), "an empty file is refused");
        await Check.ThrowsAsync<TimeoutException>(() => Downloader.DownloadAsync(Track("Never", fails: new TimeoutException("no file")), folder, null, default), "a failure is passed on");
        Check.Equal("Artist - One.flac", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName)), "none of the three left anything behind");

        // While a download runs, its part file must not look like a song to Noctis or to WordLyrics.
        var during = "";
        await Downloader.DownloadAsync(Track("Two", before: () => { during = string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).Order()); return Task.CompletedTask; }), folder, null, default);
        Check.Equal("Artist - One.flac,Artist - Two.fmf.part", during, "while downloading: a part file");
        Check.Equal("mp3,ogg,wav", string.Join(",", new[] { "mp3", "ogg", "wav", "exe", "zip", "html", "lrc" }.Where(Downloader.AudioExtensions.Contains)), "only song files are on the list");
    }

    private static void Leftovers()
    {
        Check.Section("tidying up after a crash");
        var folder = Check.NewFolder("leftovers");
        string Make(string name, int minutesOld)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesOld));
            return path;
        }
        Make("Artist - Old.fmf.part", 60);
        Make("Artist - Fresh.fmf.part", 1);
        Make("firefox-download.mp3.part", 600);
        Make("Artist - Song.mp3", 600);
        Check.Equal(1, Downloader.CleanLeftovers(folder), "one file removed");
        Check.Equal("Artist - Fresh.fmf.part,Artist - Song.mp3,firefox-download.mp3.part",
            string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)),
            "only the plugin's own old part file is gone");
        Check.Equal(0, Downloader.CleanLeftovers(Path.Combine(folder, "not there")), "a folder that is not there is fine");
    }

    private static async Task QueueAsync()
    {
        Check.Section("the download queue");
        var folder = Check.NewFolder("queue");
        var log = new List<string>();
        var finished = new List<string>();
        var running = 0;
        var most = 0;
        var order = new List<string>();
        Func<Task> Slow(string name) => async () =>
        {
            lock (order) order.Add(name);
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(150);
            Interlocked.Decrement(ref running);
        };

        using (var downloads = new Downloads(m => { lock (log) log.Add(m); }))
        {
            downloads.Finished += m => { lock (finished) finished.Add(m); };
            var a = downloads.Start(Track("A", before: Slow("A")), folder);
            var b = downloads.Start(Track("B", before: Slow("B")), folder);
            var c = downloads.Start(Track("C", before: Slow("C"), fails: new TimeoutException("The bot did not send the file.")), folder);
            var again = downloads.Start(Track("B"), folder);
            Check.True(ReferenceEquals(b, again), "asking twice for the same track gives the same download");
            Check.Equal(3, downloads.Active.Count, "three are waiting or running");
            Check.True(downloads.Find("artist - b", folder) == b && downloads.Find("Artist - B", folder + "x") is null, "found by name and folder");

            await Check.UntilAsync(() => downloads.Active.Count == 0, "the queue runs empty");
            Check.Equal("A,B,C", string.Join(",", order), "in the order they were asked for");
            Check.Equal(1, most, "one at a time");
            Check.Equal("Saved,Saved,Failed", $"{a.State},{b.State},{c.State}", "two saved, one failed");
            Check.Equal("The bot did not send the file.", c.Error, "the failure says why");
            await Check.UntilAsync(() => finished.Count == 1, "one message at the end");
            Check.Equal("Saved 2 of 3 songs", finished[0], "partial completion keeps its count without a folder path");

            var retry = downloads.Start(Track("C"), folder);
            Check.True(!ReferenceEquals(retry, c), "a failed one can be asked for again");
            await Check.UntilAsync(() => finished.Count == 2, "the retry finishes");
            Check.Equal("Saved to library", finished[1], "single success is a short line without song or folder");

            var gate = new TaskCompletionSource();
            var stuck = downloads.Start(Track("Stuck", bytes: 500_000, before: () => gate.Task), folder);
            var waiting = downloads.Start(Track("Waiting"), folder);
            await Check.UntilAsync(() => stuck.State == DownloadState.Running, "one is running");
            downloads.Dispose();
            gate.SetResult();
            await Check.UntilAsync(() => stuck.State == DownloadState.Cancelled && waiting.State == DownloadState.Cancelled, "stopping the plugin cancels the running and the waiting one");
            var late = downloads.Start(Track("Late"), folder);
            Check.Equal(DownloadState.Cancelled, late.State, "nothing starts after that");
        }
        await Task.Delay(100);
        Check.Equal("Artist - A.mp3,Artist - B.mp3,Artist - C.mp3", string.Join(",", Directory.GetFiles(folder).Select(Path.GetFileName).Order()), "only finished songs are in the folder");
        Check.Equal(2, finished.Count, "stopping says nothing");
    }
}
