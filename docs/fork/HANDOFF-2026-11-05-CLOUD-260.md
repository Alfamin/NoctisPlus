# Handoff — Claude cloud #260 (2026-11-05)

Branch `claude/android-plus-260` (pushed). Base `cloud/android-plus-260` @ **efcca6d0**. Nothing was merged into main/native, tagged, released or published. Exact results and design notes: `docs/fork/CLOUD-260-STATUS.md`.

**Standing rule for local work:** preserve the owner's local profiles, settings, API pairs and Telegram logins. Never copy a PC/TEST session to the phone; the phone signs in with its own session.

## Commits
| Commit | Content |
|---|---|
| 322cf445 | Phone Music Finder wiring. Main files: `Mobile/Services/Finder/{PhoneFinderHost,PhonePlaylistLibrary}.cs`, `Mobile/ViewModels/OnlineResultsViewModel.cs`, Settings/Shell/Search VMs + views, `TelegramPane.axaml`, `Core/Services/PathTrackFileAccess.cs`, `Android/AndroidApp.axaml.cs`, `Android/Services/{AndroidFileSystemSource,SafTrackFileAccess}.cs`, `Android/Noctis.Android.csproj`, `.gitignore`, `Finder/{FinderSession,FinderOnlineSearch}.cs`, tests `PhoneFinderTests.cs` |
| 38917dce | Sync M2/M7. `TelegramSync/{TgLedger,TgSyncLog,TgDeviceIdentity}.cs`, tests `ConvergenceTests.cs`; fake drive gains a server clock |
| 8ffefe4d | `Finder/Sync/WTelegramDrive.cs` (real adapter, not wired), `ITelegramDrive` request-id overload, `TgRequestIds`, Finder → TelegramSync project reference, tests `WTelegramDriveTests.cs` |
| (docs) | This handoff + status |

## Integration (owner, PowerShell)
```powershell
git fetch origin claude/android-plus-260
git switch -c integrate-260 origin/claude/android-plus-260   # in a separate worktree, not the main checkout
# Optional packed Telegram pair for a Plus phone build (never commit it):
#   copy the release's telegram-app.dat to src\Noctis.Android\private\telegram-app.dat
#   or pass -p:NoctisTelegramApp=<path>
```

## Remaining checks, priority order
1. **Windows gates.** Full ten-shard Noctis.Tests (FastGates local copy pointing at the integration worktree) plus FreeMusicFinder.Tests. Expect Finder 1760/0 on Windows. The 7 Linux "wrong" checks must pass there. The Finder changes since the last Finder run (`RequireCompleteFile`, the new FinderSession parameter, WTelegramDrive) need this.
2. **Android Debug compile (x64 emulator, then ARM64).** It could not run in cloud: the network policy blocks the Android SDK download. Compile errors are possible in `AndroidApp.axaml.cs`, specifically the `Java.IO.FileNotFoundException` catch and the `Mobile.Services.Finder` namespace resolution. Command: `dotnet build src\Noctis.Android\Noctis.Android.csproj -c Debug -p:RuntimeIdentifier=android-x64 -p:JavaSdkDirectory=<JDK> -p:AndroidSdkDirectory=<SDK>`. Also check a build WITHOUT `private\telegram-app.dat` and one with it.
3. **Isolated emulator.**
   - Settings shows a Telegram row; the pane covers the phone/code/password steps, cancel, Change number, proxy, and the missing-setup message on a build without the pair.
   - Search with a query keeps local results immediate and shows the online section only when signed in.
   - The Downloads list appears under the signed-in pane.
   - Startup time with the Finder session constructed on the UI thread. If it is slow, move construction off the startup path.
   - Notification permission re-prompt (an older open item).
4. **Own-device Telegram login on the phone** (its own session). Code with unknown length → Continue; two-step password; FLOOD_WAIT text; logout pauses downloads; reset keeps a signed-in account.
5. **Real TEST bot.** Play an online song (waits for the complete file, stays out of Songs). Download one, and confirm it appears in Songs and survives restart plus rescan. Repeated taps send one request. Removing, pausing or clearing a running download. Long Persian title and name collision. Disk full / revoked SAF grant behaviour.
6. **WTelegramDrive against a real TEST channel** (only after the owner opts in; never on the owner's main library):
   - channel find/create by marker;
   - history paging (`HistoryWindow`: offset_id = after+1, add_offset = −limit, min_id = after);
   - pin and read the pinned message;
   - forward from a bot chat;
   - resumed download at a non-zero offset across DCs;
   - an expired file reference;
   - `RANDOM_ID_DUPLICATE` after a lost reply.
7. **Two-device sync.** Only after host wiring exists (still OPEN): clock skew, reinstall counters, interrupted-network playback.

## Not done (still open)
- **Sync host:** durable upload/transfer job storage across restarts, desktop/phone sync controller and settings (must default OFF; Wi-Fi-only on phone, optional charging), library/playlist/counter/sidecar wiring, snapshot bootstrap and pruning, LAN + Telegram double-apply guard, storage/SAF pause actions. Use `TgDeviceIdentity` under each host's private no-backup folder and `TgSyncLog.RecordPlay` for counters.
- **Phone True Shuffle:** mode controls, indicators, persistence. Reuse the existing shared/phone engine (`PhoneShuffle.cs`); do not write a second one.
- **Mobile lyrics:** online word-timed and synced PC sidecars. Plain-path sidecars now load through `PathTrackFileAccess`.
- **Media3 progressive DataSource:** the phone currently waits for complete files.
- **Plus release signing:** owner keystore only. No temporary-key APK may be presented as an update.
