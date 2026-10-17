using System.Net;
using System.Net.Sockets;
using System.Text;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class DiscoveryNetworkTests
{
    public static async Task RunAsync()
    {
        Check.Section("discovery proxy route: both metadata providers use the selected system proxy");
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        var port = ((IPEndPoint)proxy.LocalEndpoint).Port;
        var requested = new List<string>(); var lookedUp = new List<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using var client = await proxy.AcceptTcpClientAsync(stop.Token); var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                var first = await reader.ReadLineAsync(stop.Token); requested.Add(first ?? "");
                while ((await reader.ReadLineAsync(stop.Token)) is { Length: > 0 }) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Synthetic proxy outage\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
            }
        });
        var choice = ProxyChoice.Parse("http://127.0.0.1:" + port);
        using var route = new MetadataRoute("", null, (host, _) => { lookedUp.Add(host); return choice; }, TimeSpan.FromSeconds(2));
        using var catalog = new ArtistCatalog(Check.NewFolder("discovery-proxy"), route, applePace: TimeSpan.Zero);
        await Check.ThrowsAsync<HttpRequestException>(() => catalog.SearchAsync("North Star", false, stop.Token), "proxy failure reports metadata connection error");
        await Check.ThrowsAsync<HttpRequestException>(() => catalog.ArtworkAsync("https://is1-ssl.mzstatic.com/synthetic.jpg", stop.Token), "album artwork follows the same proxy and reports its connection failure");
        await server;
        Check.True(requested.Any(r => r.StartsWith("CONNECT api.deezer.com:443 ")) && requested.Any(r => r.StartsWith("CONNECT itunes.apple.com:443 ")), "Deezer and Apple both tunnel through system-selected proxy");
        Check.True(lookedUp.Contains("api.deezer.com") && lookedUp.Contains("itunes.apple.com"), "system route is evaluated for each external host, not bypassed during fallback");
        Check.True(requested.Any(r => r.StartsWith("CONNECT is1-ssl.mzstatic.com:443 ")), "artwork CDN requests also use system-selected proxy");
    }
}
