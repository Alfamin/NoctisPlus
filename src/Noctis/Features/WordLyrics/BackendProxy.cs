using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FreeMusicFinder;

namespace Noctis.Features.WordLyrics;

/// <summary>A process-scoped, authenticated loopback CONNECT endpoint. Python keeps TLS
/// end-to-end and uses exactly Finder's HTTP/SOCKS/system route, including remote DNS.
/// No certificates, global proxy settings or Python dependencies are installed.</summary>
internal sealed class BackendProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly Func<string, int, CancellationToken, Task<TcpClient>> _connect;
    private readonly string _authorization;
    private readonly HashSet<TcpClient> _clients = [];
    internal string Address { get; }

    internal BackendProxy(Func<string> setting, Func<string, int, CancellationToken, Task<TcpClient>>? connect = null)
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _authorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("noctis:" + password));
        _connect = connect ?? (async (host, port, ct) =>
        {
            using var route = new MetadataRoute(setting());
            var via = route.ProxyFor(host, port);
            if (via is null)
            {
                var tcp = new TcpClient();
                try { await tcp.ConnectAsync(host, port, ct); return tcp; } catch { tcp.Dispose(); throw; }
            }
            var pending = via.ConnectAsync(host, port, "WordLyrics");
            try { return await pending.WaitAsync(ct); }
            catch { _ = pending.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default); throw; }
        });
        _listener.Start(16);
        Address = $"http://noctis:{password}@127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = AcceptAsync();
    }

    internal void Configure(System.Diagnostics.ProcessStartInfo info)
    {
        foreach (var key in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY" }) info.Environment[key] = Address;
        info.Environment["NO_PROXY"] = info.Environment["no_proxy"] = "";
        info.Environment.Remove("ALL_PROXY"); info.Environment.Remove("all_proxy");
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token);
                TcpClient tcp;
                try { tcp = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { _slots.Release(); throw; }
                lock (_clients) _clients.Add(tcp);
                _ = ServeAsync(tcp);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var headStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                headStop.CancelAfter(TimeSpan.FromSeconds(15));
                var input = client.GetStream();
                var header = new List<byte>(); var one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await input.ReadAsync(one, headStop.Token) == 0) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var auth = lines.FirstOrDefault(l => l.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                if (auth is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(auth), Encoding.ASCII.GetBytes(_authorization)))
                { await Reply(input, "407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Noctis\"", headStop.Token); return; }
                var first = lines[0].Split(' ');
                if (header.Count >= 8192 || first.Length != 3 || first[0] != "CONNECT" ||
                    !Uri.TryCreate("https://" + first[1] + "/", UriKind.Absolute, out var target) ||
                    target.Port != 443 || target.UserInfo.Length > 0 || target.HostNameType != UriHostNameType.Dns || !target.Host.Contains('.') || target.AbsolutePath != "/")
                { await Reply(input, "400 Bad Request", headStop.Token); return; }
                using var remote = await _connect(target.DnsSafeHost, target.Port, headStop.Token);
                await Reply(input, "200 Connection Established", headStop.Token);
                using var copying = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var a = input.CopyToAsync(remote.GetStream(), copying.Token);
                var b = remote.GetStream().CopyToAsync(input, copying.Token);
                await Task.WhenAny(a, b); copying.Cancel();
                try { await Task.WhenAll(a, b); } catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or FormatException) { }
        finally { lock (_clients) _clients.Remove(client); _slots.Release(); }
    }
    private static Task Reply(Stream stream, string status, CancellationToken ct)
        => stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n\r\n"), ct).AsTask();
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel(); _listener.Stop();
        lock (_clients) foreach (var client in _clients) client.Dispose();
    }
}
