using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FreeMusicFinder;

/// <summary>How a <see cref="ProxyChoice"/> is spoken to.</summary>
internal enum ProxyKind
{
    /// <summary>Whatever the computer itself is set to use, looked up again at every connection (<see cref="SystemProxy"/>).</summary>
    System,
    /// <summary>A Telegram (MTProto) proxy.</summary>
    Telegram,
    Socks5,
    /// <summary>An HTTP proxy, asked to open a tunnel (CONNECT).</summary>
    Http,
    /// <summary>Only "host:port" is known: HTTP is tried first, then SOCKS5.</summary>
    HttpOrSocks5,
}

/// <summary>
/// The "Proxy" setting, for places where Telegram cannot be reached directly: the computer's own
/// proxy (what an empty setting means), a Telegram (MTProto) proxy, <see cref="MtProxyUrl"/>, or
/// a SOCKS5 or HTTP proxy, <see cref="Host"/> and the rest.
/// </summary>
internal sealed record ProxyChoice(ProxyKind Kind, string Host, int Port, string? User = null, string? Password = null, string? MtProxyUrl = null)
{
    public const string Help = "Leave it empty to use the computer's own proxy setting, or use host:port, socks5://host:port, http://host:port "
        + "or a Telegram proxy link (https://t.me/proxy?server=…&port=…&secret=…). \"direct\" means no proxy at all.";

    /// <summary>"Use what the computer is set to use": what an empty setting means.</summary>
    public static readonly ProxyChoice System = new(ProxyKind.System, "", 0);

    private static readonly Regex HostAndPort = new(@"^(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9._-]+):(\d{1,5})$", RegexOptions.CultureInvariant);

    /// <summary>
    /// <see cref="System"/> for an empty setting (or "system"), null for "direct". Understands
    /// "host:port", "socks5://[user:password@]host:port", "http://[user:password@]host:port" and
    /// Telegram's own proxy links ("https://t.me/proxy?…", "tg://proxy?…", and "…/socks?…" for
    /// SOCKS5). Throws <see cref="FormatException"/> for anything else.
    /// </summary>
    public static ProxyChoice? Parse(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || Is(text, "system") || Is(text, "auto")) return System;
        if (Is(text, "direct") || Is(text, "none") || Is(text, "off")) return null;
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            // "127.0.0.1:10808": the way Windows and VPN apps show a proxy, which does not say what kind it is.
            var bare = HostAndPort.Match(text);
            if (bare.Success)
                return int.TryParse(bare.Groups[2].Value, out var barePort) && barePort is > 0 and <= 65535
                    ? new ProxyChoice(ProxyKind.HttpOrSocks5, bare.Groups[1].Value.Trim('[', ']'), barePort)
                    : throw NotUnderstood();
            text = "https://" + text; // "t.me/proxy?…"
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) throw NotUnderstood();

        var scheme = uri.Scheme.ToLowerInvariant();
        var telegramSite = scheme is "https" or "http" && uri.Host.ToLowerInvariant() is "t.me" or "telegram.me";
        var socks = scheme is "socks5" or "socks5h" or "socks";
        if (socks || (scheme == "http" && !telegramSite))
        {
            if (uri.Host.Length == 0 || uri.Port is <= 0 or > 65535 || uri.AbsolutePath.Length > 1 || uri.Query.Length > 0) throw NotUnderstood();
            var login = uri.UserInfo.Split(':', 2);
            return new ProxyChoice(socks ? ProxyKind.Socks5 : ProxyKind.Http, uri.DnsSafeHost, uri.Port,
                login[0].Length > 0 ? Uri.UnescapeDataString(login[0]) : null,
                login.Length > 1 ? Uri.UnescapeDataString(login[1]) : null);
        }

        // tg://proxy?… has "proxy" as its host; https://t.me/proxy?… has it as its path.
        var kind = scheme == "tg" ? uri.Host.ToLowerInvariant()
            : telegramSite ? uri.AbsolutePath.Trim('/').ToLowerInvariant()
            : "";
        var values = Query(uri.Query);
        values.TryGetValue("server", out var server);
        if (string.IsNullOrEmpty(server) || !values.TryGetValue("port", out var portText) || !int.TryParse(portText, out var port) || port is <= 0 or > 65535)
            throw NotUnderstood();

        switch (kind)
        {
            case "proxy":
                if (!values.TryGetValue("secret", out var secret) || secret.Length == 0) throw NotUnderstood();
                return new ProxyChoice(ProxyKind.Telegram, server, port,
                    MtProxyUrl: $"https://t.me/proxy?server={Uri.EscapeDataString(server)}&port={port}&secret={Uri.EscapeDataString(secret)}");
            case "socks":
                values.TryGetValue("user", out var user);
                values.TryGetValue("pass", out var password);
                return new ProxyChoice(ProxyKind.Socks5, server, port, string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(password) ? null : password);
            default:
                throw NotUnderstood();
        }
    }

    /// <summary>A connection to <paramref name="host"/>:<paramref name="port"/> that runs through this SOCKS5 or HTTP proxy.</summary>
    public Task<TcpClient> ConnectAsync(string host, int port) => ConnectAsync(host, port, "Telegram");

    /// <param name="what">What is at the other end, for the words of a refusal: "Telegram", "Spotify".</param>
    public async Task<TcpClient> ConnectAsync(string host, int port, string what)
    {
        switch (Kind)
        {
            case ProxyKind.Socks5:
                return await Socks5.ConnectAsync(this, host, port, what).ConfigureAwait(false);
            case ProxyKind.Http:
                return await HttpTunnel.ConnectAsync(this, host, port, what: what).ConfigureAwait(false);
            case ProxyKind.HttpOrSocks5:
                try
                {
                    // HTTP goes first: a SOCKS5 proxy turns down an HTTP request at its first letter,
                    // while an HTTP proxy would wait for the rest of a SOCKS5 greeting.
                    return await HttpTunnel.ConnectAsync(this, host, port, TimeSpan.FromSeconds(10), what).ConfigureAwait(false);
                }
                // It did not answer the way an HTTP proxy does. (One that answered and said no is believed.)
                catch (Exception ex) when (ex is TimeoutException || (ex is IOException && ex is not ProxyRefusedException))
                {
                    return await Socks5.ConnectAsync(this, host, port, what).ConfigureAwait(false);
                }
            default:
                throw new InvalidOperationException("Not a proxy a connection can be opened through.");
        }
    }

    private static bool Is(string text, string word) => text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static FormatException NotUnderstood() => new("The proxy setting is not understood. " + Help);

    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return values;
    }
}

/// <summary>The proxy was reached and understood, and said no.</summary>
internal sealed class ProxyRefusedException(string message) : IOException(message);

/// <summary>
/// The proxy the computer itself is set to use. On Windows that is Settings → Network → Proxy,
/// the setting a VPN app switches on in its "system proxy" mode; on macOS the network settings;
/// on Linux the usual variables (HTTPS_PROXY, ALL_PROXY).
/// </summary>
internal static class SystemProxy
{
    private const string WindowsSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly char[] Between = { ';', ' ' };

    /// <summary>
    /// The proxy to reach <paramref name="host"/>:<paramref name="port"/> through, as the computer
    /// is set at this moment. Null when it is set to use none, or none for this address.
    /// </summary>
    public static ProxyChoice? For(string host, int port)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return From(HttpClient.DefaultProxy, host, port);
            // Read from the registry each time: it is where the switch lives, and it is cheap.
            using var settings = Registry.CurrentUser.OpenSubKey(WindowsSettings);
            if (settings is null) return null;
            if (settings.GetValue("ProxyEnable") is int on && on != 0 && settings.GetValue("ProxyServer") is string server && server.Trim().Length > 0)
                return FromWindows(server, settings.GetValue("ProxyOverride") as string, host);
            // Set by a script instead: Windows works the answer out, .NET asks it.
            return settings.GetValue("AutoConfigURL") is string { Length: > 0 } ? From(HttpClient.DefaultProxy, host, port) : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or FormatException)
        {
            return null; // a setting that cannot be read is no proxy
        }
    }

    /// <summary>
    /// Windows' own notation: one address for everything ("127.0.0.1:10808") or one per kind
    /// ("http=…;https=…;socks=…"), and the addresses that go without a proxy
    /// ("localhost;192.168.*;&lt;local&gt;").
    /// </summary>
    internal static ProxyChoice? FromWindows(string server, string? without, string host)
    {
        if (GoesWithout(without, host)) return null;
        var byKind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in server.Split(Between, StringSplitOptions.RemoveEmptyEntries))
        {
            var at = entry.IndexOf('=');
            byKind.TryAdd(at < 0 ? "" : entry[..at], entry[(at + 1)..]);
        }
        // Telegram's connection is not a web page: what carries secure web pages (a tunnel) carries
        // it too, and so does a SOCKS proxy; the one for plain web pages is the last to try.
        foreach (var kind in new[] { "", "https", "socks", "http" })
            if (byKind.TryGetValue(kind, out var address) && Address(address, kind == "socks") is { } proxy) return proxy;
        return null;
    }

    /// <summary>What .NET knows of the computer's proxy: the macOS settings, the Linux variables, a script on Windows.</summary>
    internal static ProxyChoice? From(IWebProxy? system, string host, int port)
    {
        if (system is null) return null;
        // Asked the way a secure web page would be: that picks the proxy that can open a tunnel.
        var target = new Uri("https://" + Bracketed(host) + ":" + port + "/");
        if (system.IsBypassed(target)) return null;
        var via = system.GetProxy(target);
        if (via is null || via.Host.Length == 0 || via.Equals(target)) return null;
        var login = via.UserInfo.Split(':', 2);
        var user = login[0].Length > 0 ? Uri.UnescapeDataString(login[0]) : null;
        var password = login.Length > 1 ? Uri.UnescapeDataString(login[1]) : null;
        if (user is null && system.Credentials?.GetCredential(via, "Basic") is { UserName.Length: > 0 } known)
            (user, password) = (known.UserName, known.Password);
        return new ProxyChoice(via.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? ProxyKind.Socks5 : ProxyKind.Http,
            via.DnsSafeHost, via.Port, user, password);
    }

    /// <summary>An IPv6 address the way it is written next to a port.</summary>
    internal static string Bracketed(string host) => host.Contains(':') && !host.StartsWith('[') ? "[" + host + "]" : host;

    private static ProxyChoice? Address(string text, bool socks)
    {
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            socks |= text.StartsWith("socks", StringComparison.OrdinalIgnoreCase);
            text = text[(scheme + 3)..];
        }
        text = text.TrimEnd('/');
        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        // Without a port Windows takes the usual one of that kind of proxy.
        var port = Regex.IsMatch(text, @":\d+$") ? uri.Port : socks ? 1080 : 80;
        return port is > 0 and <= 65535 ? new ProxyChoice(socks ? ProxyKind.Socks5 : ProxyKind.Http, uri.DnsSafeHost, port) : null;
    }

    private static bool GoesWithout(string? list, string host)
    {
        foreach (var entry in (list ?? "").Split(Between, StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                // every name without a dot; an address is not a name
                if (!host.Contains('.') && !host.Contains(':')) return true;
                continue;
            }
            var pattern = entry;
            var scheme = pattern.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) pattern = pattern[(scheme + 3)..];
            if (pattern.Count(c => c == ':') == 1) pattern = pattern[..pattern.IndexOf(':')]; // "host:port"
            if (Regex.IsMatch(host, "^" + Regex.Escape(pattern.Trim('[', ']')).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }
        return false;
    }
}

/// <summary>
/// How the web services that list a playlist (Spotify, Deezer) are reached: the way the "Proxy"
/// setting says, as Telegram is. With the setting empty that is the computer's own proxy, asked
/// again at every connection, so a VPN app switched on or off while Noctis runs needs nothing
/// here. A Telegram (MTProto) proxy carries Telegram only: with one set these go the computer's
/// way as well. The setting is read when a connection is made, so one that is not understood
/// fails that request with its reason and not the plugin as it starts.
/// </summary>
internal sealed class MetadataRoute : DelegatingHandler
{
    private readonly string _setting;
    private readonly Action<string>? _log;
    private readonly Func<string, int, ProxyChoice?> _computer;
    private string? _last;

    /// <param name="computer">The computer's proxy for a host and port; the tests put their own here.</param>
    /// <param name="patience">How long a connection may take to open.</param>
    public MetadataRoute(string setting, Action<string>? log = null, Func<string, int, ProxyChoice?>? computer = null, TimeSpan? patience = null)
    {
        _setting = setting;
        _log = log;
        _computer = computer ?? Computer;
        InnerHandler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false, // the connections are opened here
            ConnectTimeout = patience ?? TimeSpan.FromSeconds(15),
            ConnectCallback = ConnectAsync,
            // Catalog JSON shrinks ~5x compressed: fewer round trips through a slow VPN.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            // Keep a warmed connection (Search opens one ahead of the first query) for a while.
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
        };
    }

    /// <summary>What to tell the user when <paramref name="to"/> did not answer in time.</summary>
    public string Unanswered(Uri to)
    {
        try
        {
            var (way, advice) = Describe(to);
            return $"{Service(to.Host)} did not answer in time {way}. {advice}";
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            // The first thing that went wrong says the most: the proxy's own words, or the system's.
            var first = ex.GetBaseException();
            if (first is FormatException) throw new HttpRequestException("METADATA_PROXY_SETTING: " + first.Message, ex);
            var to = request.RequestUri!;
            var (way, advice) = Describe(to);
            throw new HttpRequestException($"METADATA_UNREACHABLE: {Service(to.Host)} could not be reached {way}. {first.Message.Trim().TrimEnd('.')}. {advice}", ex);
        }
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var (via, way) = Route(host, port);
        if (Interlocked.Exchange(ref _last, way) != way) _log?.Invoke("reading playlist metadata " + way);
        if (via is null)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        // The name goes to the proxy as it is: looked up at its end, not here, where a blocked name gets a wrong answer.
        var connect = via.ConnectAsync(host, port, Service(host));
        try
        {
            return (await connect.WaitAsync(ct).ConfigureAwait(false)).GetStream();
        }
        catch (OperationCanceledException)
        {
            // Given up on. The connection may still come: it is closed then.
            _ = connect.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>The proxy a connection to <paramref name="host"/> goes through at this moment (null: none), and that in words.</summary>
    private (ProxyChoice? Via, string Way) Route(string host, int port)
    {
        var set = ProxyChoice.Parse(_setting);
        if (set is null) return (null, "directly (the plugin's Proxy setting says direct)");
        if (set.Kind is not (ProxyKind.System or ProxyKind.Telegram)) return (set, $"through the proxy {set.Host}:{set.Port} from the plugin's settings");
        var via = _computer(host, port);
        if (via is not null) return (via, $"through the computer's proxy {via.Host}:{via.Port}");
        return (null, set.Kind == ProxyKind.Telegram
            ? "directly (the Telegram proxy in the plugin's settings carries Telegram only, and the computer has no proxy switched on)"
            : "directly (the computer has no proxy switched on)");
    }

    private (string Way, string Advice) Describe(Uri to)
    {
        var (via, way) = Route(to.Host, to.Port);
        return (way, via is not null
            ? "Check that the VPN or proxy is connected, then retry."
            : "Where it is blocked, turn on a VPN or its system proxy, or set a proxy in the plugin's settings, then retry.");
    }

    // Windows' own switch first, as for Telegram; then what only .NET knows there (a proxy found
    // automatically, the HTTPS_PROXY variable), which these requests followed before.
    private static ProxyChoice? Computer(string host, int port)
        => SystemProxy.For(host, port) ?? (OperatingSystem.IsWindows() ? SystemProxy.From(HttpClient.DefaultProxy, host, port) : null);

    private static string Service(string host)
        => host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase) ? "Spotify"
            : host.EndsWith("deezer.com", StringComparison.OrdinalIgnoreCase) ? "Deezer"
            : host;
}

/// <summary>Opens a connection through an HTTP proxy by asking it for a tunnel (CONNECT, RFC 9110 section 9.3.6).</summary>
internal static class HttpTunnel
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>A connection to <paramref name="host"/>:<paramref name="port"/> that runs through the proxy.</summary>
    public static async Task<TcpClient> ConnectAsync(ProxyChoice proxy, string host, int port, TimeSpan? patience = null, string what = "Telegram")
    {
        if (host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new IOException("The server name cannot be sent to the proxy.");
        using var timeout = new CancellationTokenSource(patience ?? Patience);
        var ct = timeout.Token;
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var target = SystemProxy.Bracketed(host) + ":" + port;
            var request = new StringBuilder($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\n");
            if (proxy.User is not null)
                request.Append("Proxy-Authorization: Basic ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(proxy.User + ":" + proxy.Password))).Append("\r\n");
            request.Append("\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct).ConfigureAwait(false);

            // The answer ends with an empty line. It is read one byte at a time, so that nothing of
            // what comes after it (that is Telegram's) is taken away from the connection.
            var answer = new List<byte>(128);
            var one = new byte[1];
            while (!(answer.Count >= 4 && answer[^4] == '\r' && answer[^3] == '\n' && answer[^2] == '\r' && answer[^1] == '\n'))
            {
                if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new IOException("The proxy closed the connection.");
                answer.Add(one[0]);
                if (answer.Count > 16384 || (answer.Count <= 5 && one[0] != "HTTP/"[answer.Count - 1])) throw new IOException("The proxy is not an HTTP proxy.");
            }

            // "HTTP/1.1 200 Connection established"
            var words = Encoding.ASCII.GetString(answer.ToArray()).Split('\r')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || !int.TryParse(words[1], out var status)) throw new IOException("The proxy is not an HTTP proxy.");
            if (status == 407) throw new ProxyRefusedException(proxy.User is null ? "The proxy wants a name and password." : "The proxy did not accept the name and password.");
            if (status is < 200 or > 299) throw new ProxyRefusedException($"The proxy could not connect to {what} (answer {status}).");
            return tcp;
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException("The proxy did not answer.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}

/// <summary>Opens a connection through a SOCKS5 proxy (RFC 1928, with the name-and-password login of RFC 1929).</summary>
internal static class Socks5
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>A connection to <paramref name="host"/>:<paramref name="port"/> that runs through the proxy.</summary>
    public static async Task<TcpClient> ConnectAsync(ProxyChoice proxy, string host, int port, string what = "Telegram")
    {
        using var timeout = new CancellationTokenSource(Patience);
        var ct = timeout.Token;
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            var stream = tcp.GetStream();

            var withLogin = proxy.User is not null;
            await stream.WriteAsync(withLogin ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, ct).ConfigureAwait(false);
            var method = await ReadAsync(stream, 2, ct).ConfigureAwait(false);
            if (method[0] != 5) throw new IOException("The proxy is not a SOCKS5 proxy.");
            if (method[1] == 2 && withLogin)
            {
                var user = Short(proxy.User!, "name");
                var password = Short(proxy.Password ?? "", "password", mayBeEmpty: true);
                await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user).Append((byte)password.Length).Concat(password).ToArray(), ct).ConfigureAwait(false);
                if ((await ReadAsync(stream, 2, ct).ConfigureAwait(false))[1] != 0) throw new ProxyRefusedException("The proxy did not accept the name and password.");
            }
            else if (method[1] != 0)
            {
                throw new ProxyRefusedException(withLogin ? "The proxy accepts none of the ways to log in." : "The proxy wants a name and password.");
            }

            byte[] address;
            if (IPAddress.TryParse(host, out var ip))
            {
                address = new[] { (byte)(ip.AddressFamily == AddressFamily.InterNetworkV6 ? 4 : 1) }.Concat(ip.GetAddressBytes()).ToArray();
            }
            else
            {
                var name = Short(host, "server name");
                address = new byte[] { 3, (byte)name.Length }.Concat(name).ToArray();
            }
            await stream.WriteAsync(new byte[] { 5, 1, 0 }.Concat(address).Append((byte)(port >> 8)).Append((byte)port).ToArray(), ct).ConfigureAwait(false);

            var reply = await ReadAsync(stream, 4, ct).ConfigureAwait(false);
            if (reply[0] != 5) throw new IOException("The proxy answered in a way that is not SOCKS5.");
            if (reply[1] != 0) throw new ProxyRefusedException($"The proxy could not connect to {what} (answer {reply[1]}).");
            // The address the proxy connected from follows; it is of no use here.
            var rest = reply[3] switch
            {
                1 => 4,
                4 => 16,
                3 => (await ReadAsync(stream, 1, ct).ConfigureAwait(false))[0],
                _ => throw new IOException("The proxy answered in a way that is not SOCKS5."),
            };
            await ReadAsync(stream, rest + 2, ct).ConfigureAwait(false);
            return tcp;
        }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException("The proxy did not answer.");
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    // SOCKS5 gives a name one byte for its length.
    private static byte[] Short(string text, string what, bool mayBeEmpty = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes.Length <= 255 && (mayBeEmpty || bytes.Length > 0) ? bytes : throw new IOException($"The proxy {what} is empty or too long.");
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        return buffer;
    }
}
