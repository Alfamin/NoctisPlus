using System.Net;
using System.Net.Sockets;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

/// <summary>
/// A network that swallows packets: a local "proxy" accepts the connection and never answers.
/// Nothing reaches Telegram; the fake API ID and phone are never sent anywhere.
/// </summary>
internal static class TelegramLoginDeadlineTests
{
    public static async Task RunAsync()
    {
        Check.Section("Telegram sign-in on a network that never answers");
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var port = ((IPEndPoint)silent.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            try { while (true) held.Add(await silent.AcceptTcpClientAsync()); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        });
        var folder = Check.NewFolder("login-deadline");
        try
        {
            using var account = new TelegramAccount(Path.Combine(folder, "telegram.dat"), () => "socks5://127.0.0.1:" + port)
            { LoginStepDeadline = TimeSpan.FromSeconds(2) };
            var started = DateTime.UtcNow;
            var failure = await Check.CatchAsync<TimeoutException>(() => account.StartLoginAsync(1, "0123456789abcdef0123456789abcdef", "+10000000000"),
                "the sign-in step gives up instead of spinning for ever");
            Check.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "it gives up close to its deadline");
            Check.True(failure is not null && TelegramSignInViewModelExplain(failure).StartsWith("TELEGRAM_CONNECTION_FAILED", StringComparison.Ordinal),
                "the dialog says Telegram could not be reached");
            Check.False(account.IsLoggedIn, "nothing is recorded as signed in");
            var again = await Check.CatchAsync<InvalidOperationException>(() => account.ContinueLoginAsync("12345"), "a code after the timeout asks to start the login again");
            Check.True(again?.Message.Contains("Start the login again") == true, "it says so");

            Check.Section("a switched-off account opens no new connection");
            account.Dispose();
            await Task.Delay(200);
            var connectionsBefore = held.Count;
            await Check.ThrowsAsync<ObjectDisposedException>(() => account.StartLoginAsync(1, "0123456789abcdef0123456789abcdef", "+10000000000"),
                "no sign-in after the feature was switched off");
            await Check.ThrowsAsync<ObjectDisposedException>(() => account.ConnectAsync(default), "no connection after the feature was switched off");
            await Task.Delay(300);
            Check.Equal(connectionsBefore, held.Count, "nothing reached the network after it was switched off");
        }
        finally
        {
            silent.Stop();
            foreach (var client in held) client.Dispose();
        }
    }

    private static string TelegramSignInViewModelExplain(Exception ex) => Noctis.ViewModels.TelegramSignInViewModel.Explain(ex);
}
