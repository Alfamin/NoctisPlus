# Isolated test launcher

Copy `scripts/Start-NoctisPlus-Test.ps1` and `scripts/Start-NoctisPlus-FreshProfile.ps1`
beside the isolated test folder's `app/` directory. These scripts never point at the installed profile.

The owner twice observed the old launcher returning with no window or profile log.
That exact failure did not reproduce from Codex's shell; its original cause remains unproven.
The old script returned immediately without observing the child process.

The replacement starts the executable directly with captured stdout/stderr, an explicit test
profile and no inherited profile-import request. It restores the caller's environment after
spawning. It reports an opened window, successful activation of an already-open instance,
early exit (with exit code), or a bounded startup wait. It never kills or retries a slow
startup. Launch records are under `launch-logs/`, outside the profile, so native loader
errors can be retained even before Noctis creates `session.log`.

Validation on 2026-10-18:
- Windows PowerShell 5.1 `-NoProfile -File`, working directory `C:\Windows\System32`:
  clean launch opened a real window; second launch activated the existing window.
- Owner independently ran the replacement and confirmed: "it does open".
- Real test window inspected with computer use; existing separate test login retained.
- Subprocess checks: a missing build reports failure without creating a profile; a native
  program exiting before managed startup is never reported as successfully opened.
  Paths include spaces; standalone script-root resolution is exercised under PowerShell 5.1.
- FastGates `codex-night-launch`: Noctis 6,582 total / 0 failed / 43 skipped;
  Finder 1,207 passed / 0 failed; ReadyToRun publish succeeded.

This is launcher hardening and a live-confirmed resolution, not proof of a specific original
SmartScreen, console-lifetime, antivirus or app-startup defect. No security settings changed.
