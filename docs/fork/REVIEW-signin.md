# Review: phone Telegram sign-in (rev-signin vs origin/android-plus)

Scope: IPhoneTelegramSignIn.cs, TelegramPaneViewModel.cs, MobileTelegramSignInTests.cs, PHONE-UI-255.md.

## Findings (most important first)

1. **Unknown next step leaves the user stuck with no message** - src/Noctis.Mobile/ViewModels/Settings/TelegramPaneViewModel.cs:97.
   `_ => Step` keeps the current step when Telegram asks for anything other than
   "verification_code"/"password" (e.g. "name" for a number with no account, "email" for
   email-code login). The call "succeeds", ErrorText stays null, and the screen does nothing.
   Fix: set ErrorText (e.g. "Sign in on the Telegram app first") or add a step.

2. **ChangeNumber runs while a request is in flight** - src/Noctis.Mobile/ViewModels/Settings/TelegramPaneViewModel.cs:71.
   No IsBusy guard: tapping it during StartLoginAsync/ContinueLoginAsync resets Step to Phone,
   then RunAsync finishes and sets Step back to Code/Done for the old number. Also it does not
   reset the sign-in service, so the next StartLoginAsync may continue the half-done old login.
   Fix: `if (IsBusy) return;` and tell the service to abandon the pending login.

3. **Auto-submit at exactly 5 digits** - src/Noctis.Mobile/ViewModels/Settings/TelegramPaneViewModel.cs:41.
   Telegram codes can be 5 or 6 digits (and other lengths for some delivery types); a 6-digit
   code is sent after the 5th digit and fails as "Wrong code". Use the code length Telegram
   reports, or only auto-submit when the length is known.

4. **No cancellation** - src/Noctis.Mobile/ViewModels/Settings/TelegramPaneViewModel.cs:91.
   The interface takes a CancellationToken but the view model never passes one; leaving the pane
   during "Connecting..." cannot stop the login, and IsBusy stays true until the network times out.

5. Minor: ErrorText is not cleared when the user edits the field, so a stale "Wrong code"
   stays visible while typing the next attempt.
