using System.Net;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class LastFmSessionRetentionTests
{
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    [Theory]
    [InlineData(503, "service unavailable")]
    [InlineData(429, "{}")]
    [InlineData(407, "proxy authentication required")]
    [InlineData(200, "not JSON")]
    [InlineData(200, "{}")]
    [InlineData(200, "{\"error\":8}")]
    [InlineData(200, "{\"error\":10}")]
    [InlineData(200, "{\"error\":29}")]
    [InlineData(200, "{\"error\":90}")]
    [InlineData(200, "{\"error\":\"9\"}")]
    public async Task Startup_UnavailableOrUncertain_KeepsStoredLogin(int status, string body)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(body, (HttpStatusCode)status))));
        var service = new LastFmService(http);
        var expired = 0;
        service.SessionExpired += (_, _) => expired++;
        service.Configure("test-session");
        await service.SessionVerification;
        Assert.True(service.IsAuthenticated);
        Assert.Equal("test-session", service.GetSessionKey());
        Assert.Equal(0, expired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_OfflineOrTimeout_KeepsStoredLogin(bool timeout)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromException<HttpResponseMessage>(
            timeout ? new TaskCanceledException() : new HttpRequestException("offline"))));
        var service = new LastFmService(http);
        service.Configure("test-session");
        await service.SessionVerification;
        Assert.Equal("test-session", service.GetSessionKey());
    }

    [Theory]
    [InlineData(200, "{\"error\":9}")]
    [InlineData(403, "{ \"error\" : 9, \"message\":\"Invalid session key\" }")]
    public async Task Startup_ConfirmedInvalidSession_ClearsAndNotifiesOnce(int status, string body)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(body, (HttpStatusCode)status))));
        var service = new LastFmService(http);
        var expired = 0;
        service.SessionExpired += (_, _) => expired++;
        service.Configure("test-session");
        await service.SessionVerification;
        Assert.False(service.IsAuthenticated);
        Assert.Null(service.Username);
        Assert.Equal(1, expired);
    }

    [Fact]
    public async Task Startup_Verified_RecordsUsername()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply("{\"user\":{\"name\":\"Test listener\"}}"))));
        var service = new LastFmService(http);
        service.Configure("test-session");
        await service.SessionVerification;
        Assert.Equal("Test listener", service.Username);
        Assert.True(service.IsAuthenticated);
    }

    [Theory]
    [InlineData("{\"error\":9}")]
    [InlineData("{\"user\":{\"name\":\"Old listener\"}}")]
    public async Task LateVerification_CannotOverwriteNewLogin(string oldBody)
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(req => req.RequestUri!.Query.Contains("old-session")
            ? pending.Task : Task.FromResult(Reply("{\"user\":{\"name\":\"New listener\"}}"))));
        var service = new LastFmService(http);
        service.Configure("old-session");
        var oldVerification = service.SessionVerification;
        service.Configure("new-session");
        await service.SessionVerification;
        pending.SetResult(Reply(oldBody));
        await oldVerification;
        Assert.Equal("new-session", service.GetSessionKey());
        Assert.Equal("New listener", service.Username);
    }

    [Fact]
    public async Task LogoutDuringVerification_StaysLoggedOut()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(_ => pending.Task));
        var service = new LastFmService(http);
        service.Configure("old-session");
        var verification = service.SessionVerification;
        service.Logout();
        pending.SetResult(Reply("{\"user\":{\"name\":\"Old listener\"}}"));
        await verification;
        Assert.Null(service.GetSessionKey());
        Assert.Null(service.Username);
    }

    [Theory]
    [InlineData("{\"error\":9}", true)]
    [InlineData("{ \"error\" : 9 }", true)]
    [InlineData("{\"error\":90}", false)]
    public async Task Scrobble_ParsesExactInvalidSessionCode(string body, bool expired)
    {
        using var http = new HttpClient(new Handler(req => Task.FromResult(Reply(req.Method == HttpMethod.Post ? body : "{}"))));
        var service = new LastFmService(http);
        service.Configure("test-session");
        await service.SessionVerification;
        await service.ScrobbleAsync(new Track { Title = "Test song", Artist = "Test artist" }, DateTime.UtcNow);
        Assert.Equal(!expired, service.IsAuthenticated);
    }
}
