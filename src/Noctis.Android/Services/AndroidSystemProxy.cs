using System.Net;

namespace Noctis.Android.Services;

/// <summary>.NET's default proxy does not discover Android's network settings. Delegate each
/// destination to Android's own selector, including PAC/exclusions and live Wi-Fi proxy changes.
/// VPN-only routes remain direct sockets through the VPN; no app-specific proxy is invented.</summary>
internal sealed class AndroidSystemProxy : IWebProxy
{
    public ICredentials? Credentials { get; set; }
    public bool IsBypassed(Uri host) => GetProxy(host) == host;
    public Uri GetProxy(Uri destination)
    {
        if (destination.IsLoopback) return destination;
        using var target = new Java.Net.URI(destination.AbsoluteUri);
        var routes = Java.Net.ProxySelector.Default?.Select(target);
        if (routes is null) return destination;
        foreach (var route in routes)
        {
            var kind = route.InvokeType()?.ToString();
            if (kind == "DIRECT") return destination;
            if (route.Address() is not Java.Net.InetSocketAddress address) continue;
            return new UriBuilder(kind == "SOCKS" ? "socks5" : "http", address.HostString ?? address.HostName, address.Port).Uri;
        }
        return destination;
    }
}
