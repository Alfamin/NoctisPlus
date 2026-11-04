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
        public int? ExpectedCodeLength { get; set; } = 5;
        public int ResetCalls;
        public List<string> Submitted = new();
        public Func<CancellationToken, Task<string?>>? Start;
        public Task ResetLoginAsync(CancellationToken ct = default) { ResetCalls++; return Task.CompletedTask; }
        public Task<string?> StartLoginAsync(string phone, string? s, string? sec, CancellationToken ct = default)
        { Proxy = s; return Start?.Invoke(ct) ?? Task.FromResult(Answers.Dequeue()()); }
        public Task<string?> ContinueLoginAsync(string answer, CancellationToken ct = default)
        {
            Submitted.Add(answer);
            var r = Answers.Dequeue()();
            if (r is null) { IsLoggedIn = true; SignedInAs = "@me"; }
            return Task.FromResult(r);
        }
        public Task LogOutAsync(CancellationToken ct = default) { IsLoggedIn = false; return Task.CompletedTask; }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(null)]
    public async Task SixDigitOrUnknownLengthCodeIsNotSentAfterFiveDigits(int? length)
    {
        var f = new FakeSignIn { ExpectedCodeLength = length };
        f.Answers.Enqueue(() => "verification_code"); f.Answers.Enqueue(() => null);
        var vm = new TelegramPaneViewModel(f) { Phone = "+989120004521" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        vm.Code = "12345";
        Assert.Empty(f.Submitted); Assert.Equal(TelegramStep.Code, vm.Step);
        vm.Code = "123456";
        if (length is null) { Assert.Empty(f.Submitted); await vm.SubmitCodeCommand.ExecuteAsync(null); }
        Assert.Equal(new[] { "123456" }, f.Submitted); Assert.True(vm.IsSignedIn);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    public async Task UnsupportedAccountStepsExplainWhatToDo_AndResetTheAttempt(string next)
    {
        var f = new FakeSignIn(); f.Answers.Enqueue(() => next);
        var vm = new TelegramPaneViewModel(f) { Phone = "+989120004521" };
        await vm.SendCodeCommand.ExecuteAsync(null);
        Assert.Equal(TelegramStep.Phone, vm.Step); Assert.Equal(1, f.ResetCalls);
        Assert.Equal("Sign in in the Telegram app first", vm.ErrorText); Assert.False(vm.IsBusy);
        vm.Phone = "+989120004522"; Assert.Null(vm.ErrorText);
    }

    [Fact]
    public async Task BusyAttemptCannotChangeNumber_AndCancellationRejectsALateReply()
    {
        var held = new TaskCompletionSource<string?>(); CancellationToken received = default;
        var f = new FakeSignIn { Start = ct => { received = ct; return held.Task; } };
        var vm = new TelegramPaneViewModel(f) { Phone = "+989120004521" };
        var pending = vm.SendCodeCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        await vm.ChangeNumberCommand.ExecuteAsync(null); Assert.Equal(0, f.ResetCalls);
        vm.CancelSignInCommand.Execute(null); await pending;
        Assert.True(received.IsCancellationRequested); Assert.False(vm.IsBusy); Assert.Null(vm.ErrorText);
        held.SetResult(null); await Task.Yield(); Assert.False(vm.IsSignedIn);
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
