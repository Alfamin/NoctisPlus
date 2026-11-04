using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services.Telegram;
using Noctis.Mobile.ViewModels.Settings;
using Xunit;

namespace Noctis.Tests;

public class MobileTelegramSignInTests
{
    private sealed class FakeSignIn : IPhoneTelegramSignIn
    {
        public Queue<Func<string?>> Answers = new();
        public string? Proxy;
        public bool IsLoggedIn { get; set; }
        public string? SignedInAs { get; set; }
        public Task<string?> StartLoginAsync(string phone, string? s, string? sec, CancellationToken ct = default)
        { Proxy = s; return Task.FromResult(Answers.Dequeue()()); }
        public Task<string?> ContinueLoginAsync(string answer, CancellationToken ct = default)
        {
            var r = Answers.Dequeue()();
            if (r is null) { IsLoggedIn = true; SignedInAs = "@me"; }
            return Task.FromResult(r);
        }
        public Task LogOutAsync(CancellationToken ct = default) { IsLoggedIn = false; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Phone_code_password_done()
    {
        var f = new FakeSignIn();
        f.Answers.Enqueue(() => "verification_code");
        f.Answers.Enqueue(() => "password");
        f.Answers.Enqueue(() => null);
        var vm = new TelegramPaneViewModel(f) { Phone = "+98 912 000 4521" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        Assert.Equal(TelegramStep.Code, vm.Step);
        vm.Code = "12345"; // auto-submits on 5 digits
        Assert.Equal(TelegramStep.Password, vm.Step);
        vm.Password = "pw";
        await vm.SubmitPasswordCommand.ExecuteAsync(null);
        Assert.Equal(TelegramStep.Done, vm.Step);
        Assert.Equal("@me", vm.SignedInAs);
        Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task No_password_step_when_not_asked()
    {
        var f = new FakeSignIn();
        f.Answers.Enqueue(() => "verification_code");
        f.Answers.Enqueue(() => null);
        var vm = new TelegramPaneViewModel(f) { Phone = "+989120004521" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        vm.Code = "11111";
        Assert.Equal(TelegramStep.Done, vm.Step);
    }

    [Fact]
    public async Task Wrong_code_is_one_short_line_and_stays_on_code()
    {
        var f = new FakeSignIn();
        f.Answers.Enqueue(() => "verification_code");
        f.Answers.Enqueue(() => throw new Exception("RPC 400 PHONE_CODE_INVALID"));
        var vm = new TelegramPaneViewModel(f) { Phone = "+989120004521" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        vm.Code = "99999";
        Assert.Equal(TelegramStep.Code, vm.Step);
        Assert.Equal("Wrong code", vm.ErrorText);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData("FLOOD_WAIT_300", "Too many tries. Wait 5 min")]
    [InlineData("PHONE_CODE_EXPIRED", "Code expired. Send again")]
    [InlineData("weird", "Sign-in failed. Try again")]
    public void Errors_map_to_short_lines(string msg, string expected)
        => Assert.Equal(expected, TelegramPaneViewModel.ShortError(new Exception(msg)));

    [Fact]
    public async Task Proxy_passed_only_when_on_and_sign_out_returns_to_phone()
    {
        var f = new FakeSignIn();
        f.Answers.Enqueue(() => "verification_code");
        var vm = new TelegramPaneViewModel(f) { Phone = "+989120004521", ProxyServer = "p.example:443" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        Assert.Null(f.Proxy);
        vm.ChangeNumberCommand.Execute(null);
        f.Answers.Enqueue(() => "verification_code");
        vm.UseProxy = true;
        await vm.SendCodeCommand.ExecuteAsync(null);
        Assert.Equal("p.example:443", f.Proxy);

        var signedIn = new TelegramPaneViewModel(new FakeSignIn { IsLoggedIn = true, SignedInAs = "@x" });
        Assert.True(signedIn.IsSignedIn);
        await signedIn.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(TelegramStep.Phone, signedIn.Step);
    }
}
