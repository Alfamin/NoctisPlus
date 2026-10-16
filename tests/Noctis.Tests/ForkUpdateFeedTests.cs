using System.Net;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ForkUpdateFeedTests
{
    private sealed class Handler(HttpStatusCode status, string json = "[]") : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }

    [Theory]
    [InlineData(404)]
    [InlineData(200)]
    public async Task UnpublishedOrEmptyRepository_IsNoUpdate(int status)
    {
        var handler = new Handler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var service = new UpdateService(http);
        Assert.Null(await service.CheckForUpdateAsync(ct: TestContext.Current.CancellationToken));
        Assert.Empty(await service.ListReleasesAsync(TestContext.Current.CancellationToken));
        Assert.All(handler.Requests, uri => Assert.StartsWith("/repos/Alfamin/NoctisPlus/releases", uri.AbsolutePath));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task NetworkOrRateLimitFailures_AreNotReportedAsUpToDate(int status)
    {
        using var http = new HttpClient(new Handler((HttpStatusCode)status));
        var service = new UpdateService(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckForUpdateAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WindowsUpdate_SelectsPlusInstallerAndIgnoresOfficialNames()
    {
        if (!OperatingSystem.IsWindows()) return;
        const string json = """
            [{"tag_name":"v99.0.0","assets":[
              {"name":"Noctis-v99.0.0-Setup.exe","url":"https://api.github.com/repos/Alfamin/NoctisPlus/releases/assets/1"},
              {"name":"NoctisPlus-v99.0.0-Setup.exe","url":"https://api.github.com/repos/Alfamin/NoctisPlus/releases/assets/2"}]}]
            """;
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, json));
        var update = await new UpdateService(http).CheckForUpdateAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal("NoctisPlus-v99.0.0-Setup.exe", update!.InstallerAssetName);
        Assert.EndsWith("/assets/2", update.InstallerApiUrl);
    }
}
