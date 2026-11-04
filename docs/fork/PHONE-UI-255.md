# Phone UI for #255 (Telegram sign-in, Finder, sync, notices)

Status: DESIGN ONLY (2026-11-04). Companion to `ANDROID-PLUS-PLAN.md` (stages 3-4). Nothing built.

Owner rules that shape every line below: a label + one short line at most; icons, visuals and
subtle motion carry the meaning; everything uses the phone app's own controls, styles and
animations (`MobileStyles.axaml`, `GroupedStyles.axaml`, `Motion.cs`, `Appear.cs`); easy to spot.
No new theme, no Finder window look, no desktop controls dropped in as-is.

All paths are under `src/Noctis.Mobile/` unless they start with `src/`.

## 0. Shared pieces (build once, used by all four parts)

| Piece | Reuse | New |
|---|---|---|
| Finder core | `src/Noctis.Finder` (plan stage 3; extracted from `src/Noctis/Features/FreeMusicFinder`) | phone host adapter `Services/Finder/PhoneFinderHost.cs` (session file in app private data, music folder from `IFolderPicker`) |
| Online search contract | `IOnlineMusicSearch`, `IDownloadQueue`, `OnlineSong`, `OnlineDownloadStatus`, `OnlineDownloadStep` (today in `src/Noctis/Services/Search/OnlineMusicSearch.cs`) | move these records/interfaces to `src/Noctis.UI` or `Noctis.Finder` so `Noctis.Mobile` can see them (it only references `Noctis.Core` + `Noctis.UI`) |
| Download ring | idea of desktop `src/Noctis/Controls/DownloadStepBadge.axaml(.cs)` (Asking / Switching / Receiving / Saving) | `Views/DownloadRing.cs`: a drawn control (like `PlayingBars.cs` / `GridIcon.cs`), accent arc for progress, a slow pulsing dot for Asking/Switching, a check that pops (`Motion.cs` spring) when saved |
| Icons | `PhoneIcons.axaml` (`PhDownload`, `PhCheck`, `PhSearch`), `SettingsIcons.axaml` (`SetIconWarning`, `SetIconShield`, `SetIconGlobe`, `SetIconDownload`) | add `PhPaperPlane` (Telegram, generic plane shape, no logo), `PhCloud` (online-only badge), `SetIconSync` (two arrows), `PhWifi`, `PhBolt` (charging) |
| One-line notice | `Views/LibraryNotices.axaml` card pattern (`card` + one line + one pill button) and `PromptSheet.axaml` for anything that needs a choice | `NoticeViewModel` (icon, line, action label, command, `Key` so one notice shows once) hosted by `ShellViewModel` |

## 1. Telegram sign-in on the phone

**Where it lives.** A new row in `Views/SettingsPage.axaml`, first card under the existing
`AccountCard` (same `group-card` + `row` + `setting-row` grid as `BackgroundRow`):

| Row | Chip icon | Label | Value (row-value) |
|---|---|---|---|
| `TelegramRow` | `PhPaperPlane` | Telegram | signed in: `@name` + `synced-dot` (same `Ellipse` as `SyncedDot`); signed out: "Sign in" |

Tapping it opens a new pane `Views/Settings/TelegramPane.axaml` (pattern: `ScrobblingPane.axaml`,
opened through `SettingsPageViewModel.OpenPaneCommand`, header title from `HeaderTitle`).

**Pane, signed out** (copy the `AccountPage.axaml` "Enter manually" block, it already has
`hero-disc`, `hero-title`, `hero-text`, `group-card`, `field-row`, `field-label`, `inline-field`,
`field-error`, `primary-action` with `spinner`):

1. Hero: plane icon in `hero-disc`. Title **"Sign in to Telegram"**. Line **"Finds and saves music."**
2. Step A, phone: `field-row` "Phone number", `inline-field` placeholder `+98 912 …`, `InputScope=TelephoneNumber`.
   Button `primary-action` **"Send code"** (busy: spinner + `StatusText` "Connecting…").
3. Step B, code (same card swaps with `Appear.cs` fade/slide): `field-row` "Code", numeric field, auto-submits on 5 digits.
   Under it `text-action` **"Change number"**.
4. Step C, only if Telegram asks: "Password" field with the existing `RevealPasswordToggle` (`ToggleButton Classes="reveal"`).
5. Done: card collapses, hero shows `@name`, a check pops on the disc.

**Advanced (hidden).** Last card, a collapsed `row` **"Advanced"** with `PhChevronDown` that
rotates open (same chevron motion as lists). Inside:
- `switch-row` ToggleButton **"Use a proxy"** (off). On: two `field-row`s "Server" and "Secret" (MTProto).
  Footnote (`group-footnote`): **"Off: uses your VPN."**
- Nothing else. No api_id/api_hash fields on the phone (built-in api, `UsesBuiltInApi` = true).

**Pane, signed in.** `group-card` with avatar initials (`avatar large`), `@name`, phone masked
(`+98 ••• 4521`). One `danger-row` **"Sign Out"** -> `PromptSheet` (title "Sign out of Telegram?",
line "Downloads stay.", destructive action "Sign Out").

**Hooks.**
- `ViewModels/Settings/TelegramPaneViewModel.cs`: `Phone`, `Code`, `Password`, `Step` (Phone/Code/Password/Done),
  `IsBusy`, `StatusText`, `ErrorText`, `SendCodeCommand`, `SubmitCodeCommand`, `SignOutCommand`,
  `UseProxy`, `ProxyServer`, `ProxySecret`, `ShowAdvanced`.
- Calls `ITelegramSignIn` (`src/Noctis/Features/FreeMusicFinder/ITelegramSignIn.cs`, moved with the core):
  `StartLoginAsync(apiId, apiHash, phone)` -> returns what Telegram asks next ("verification_code",
  "password", null = done); `ContinueLoginAsync(answer)`; `LogOutAsync()`; `IsLoggedIn`, `SignedInAs`.
  QR login is not offered (needs a second device; plan section 6).
- After sign-in: `BotChannelsAsync` + `JoinAsync` run silently (same as desktop #190), errors become a notice (part 4).
- Errors shown in `field-error` under the field, one line: "Wrong code", "Code expired. Send again",
  "Too many tries. Wait 5 min" (from FLOOD_WAIT seconds).

## 2. Free Music Finder on the phone

**Search results include online songs.** `Views/SearchPage.axaml` + `ViewModels/SearchPageViewModel.cs`.
No new tab, no new page: online results join the existing sections, exactly like desktop
`src/Noctis/Views/SearchView.axaml` (library first, online after, same rows).

- `SearchPageViewModel.Songs` changes from `BulkObservableCollection<Track>` to a row item
  `SearchSongRow` (`Local` Track or `Online` OnlineSong, `IsOnline`, `IsInBotLibrary`, `Status`).
  Same for `Albums` (`IsOnline`). Featured card unchanged (artist).
- Online row look (same `Button Classes="row"` template as `SongResults`): cover gets a small
  `PhCloud` badge at the bottom-right; title normal, artist line uses `row-sub` at lower opacity.
  The trailing slot (where the heart sits on library rows) shows the action:

| State | Trailing slot | Motion |
|---|---|---|
| online, not saved | `PhDownload` icon button | none |
| in BotLibrary (already sent to you) | `PhDownload` + tiny accent dot on the cover badge | none |
| downloading | `DownloadRing` (Asking/Switching = pulsing dot, Receiving = arc, Saving = full arc) | arc eases, no jumps |
| saved | `PhCheck`, then the row turns into a library row (heart slot) | check pop, 300 ms |
| failed | `SetIconWarning` small, tap = retry | fade in (no shake) |

- Online results stream in progressively (`IAsyncEnumerable<OnlineSearchResult>`): rows appear with
  the existing `Appear.cs` fade; while the first online batch is pending, a 3-dot `PlayingBars`-style
  shimmer row under "Songs" (no text).
- Scope chips (`ScopeAll`, `ScopeSongs`, …) unchanged. No "Online" chip (it would be one more choice).
- Empty library + no Telegram: `SearchPrompt` text becomes **"Sign in to Telegram to find more"**
  with a `text-chip` "Sign in" that opens `TelegramPane`.

**Play.** Tap an online row = play (desktop `PreparePlaybackAsync(song, keep:false)`; later progressive
playback, plan stage 5). Now Playing shows the normal page; while the file is still arriving the
progress `CapsuleSlider` shows the buffered part (same as streaming from the PC).

**Download.** Trailing `PhDownload` = `DownloadAsync([song], now:true)`. Long-press (`LongPress.cs`)
opens `ContextSheet.axaml` with the existing rows plus: **"Download"**, **"Add to Downloads"**
(queue, `now:false`), **"Play Next"**. Albums: `ContextSheet` row **"Download Album"**.

**Download progress overview.** No new page in the tab bar. In `Views/LibraryPage.axaml` the
existing `Downloads` row area (same row that `ListTools` uses) gets, only while something is queued,
a row **"Downloading"** with `DownloadRing` + count value (`3`). Tap opens a list page built from
`SongListPage.axaml` (rows with rings), header action `glass-round` pause/resume (`IDownloadQueue.SetPaused`),
long-press a row: "Download Next" / "Remove" (`DownloadNext`, `Remove`). Failed rows: tap = `RetryFailed`.

**BotLibrary hits.** Songs the bots already sent show the accent dot (above) and download straight
from the chat message (`BotLibrary.FetchAsync`), no bot request, so the ring skips the Asking step.
Nothing else is shown; no separate "Bot Library" page on the phone in version 1.

**Hooks.**
- `ViewModels/SearchPageViewModel.cs`: inject `IOnlineMusicSearch?` (null when signed out = today's behaviour).
  `SearchAsync` loop on a background task, results merged on the UI thread in batches; cancel on new query.
- `ViewModels/MobileSearch.cs`: local search unchanged; dedupe online songs already in the library
  by `RecordingMatch.TitleKey` (artist + title + 2 s).
- `ViewModels/SongRowStatus.cs` (new): maps `OnlineDownloadStatus` -> ring state; subscribes
  `IOnlineMusicSearch.DownloadsChanged` / `IDownloadQueue.Changed`, throttled to 10 updates/s.
- `ViewModels/DownloadsPageViewModel.cs` (new): wraps `IDownloadQueue.Snapshot()`.
- Finished file -> `MobileLibrary` scan of that one file (no full rescan).

## 3. "Sync with your PC over Telegram"

**Where.** `Views/SettingsPage.axaml`, inside the new `TelegramPane` (signed in only), and a mirror
row under `AccountCard` so it is easy to spot:

| Row | Chip icon | Label | Value |
|---|---|---|---|
| `SyncRow` (Settings main list) | `SetIconSync` | Sync with PC | `On` / `Off`; when on, a small spinning sync icon while working |

`SyncRow` opens `Views/Settings/SyncPane.axaml` (pattern: `DownloadsPane` in `SettingsPage.axaml`):

1. Card 1, `switch-row` ToggleButton **"Sync with your PC"**. Footnote: **"Over Telegram. Works anywhere."**
2. Card 2 (enabled only when 1 is on), three `switch-row`s:
   - **"Download New Songs"** (on)
   - **"Wi-Fi Only"** with `PhWifi` (on)
   - **"While Charging Only"** with `PhBolt` (off)
3. Card 3, status, two `setting-row`s with `row-value` (no buttons):
   - **"Last Synced"** -> "2 min ago" (relative, updates every minute; "Now" + spinning icon while running)
   - **"Waiting"** -> "12 songs" (tap row = start now, ignores Wi-Fi rule once; only if > 0)
4. Footnote under card 3, only when blocked: one line from part 4 (e.g. "Waiting for Wi-Fi").

The `stats-strip` in `AccountCard` keeps its five stats; no new stat.

**Hooks.**
- `ViewModels/Settings/SyncPaneViewModel.cs`: `IsEnabled`, `AutoDownload`, `WifiOnly`, `ChargingOnly`,
  `LastSyncedText`, `WaitingCount`, `IsSyncing`, `BlockedReason`, `SyncNowCommand`.
- Service `Services/Sync/PhoneTelegramSync.cs` (wraps the stage-1 sync core in `Noctis.Finder`/shared lib):
  `Start()/Stop()`, `event StateChanged`, `LastSyncedUtc`, `PendingDownloads`, `Blocked` (enum).
- Rules from the plan: starts after first frame, update stream while the app is open, poll <= 2 min
  foreground; background only via an Android WorkManager job (Wi-Fi, <= every 6 h, off by default)
  in `src/Noctis.Android` behind `IBackgroundSync` (new interface in `Services/`, like `IBatterySaver.cs`).
- Network/charging state: reuse whatever backs `DownloadWifiOnly` today (`SettingsPageViewModel`);
  add `IPowerState` (charging yes/no) next to `IBatterySaver.cs`.
- Settings persist in the phone settings store next to `DownloadWifiOnly`.

## 4. Error states as one-line notices

One shape everywhere: icon + one line + at most one action. Shown (a) as a `LibraryNotices.axaml`
card at the top of Library, (b) as the footnote in `SyncPane`/`TelegramPane`. A notice shows once per
cause (`NoticeViewModel.Key`), goes away by itself when the cause is fixed. Never a retry loop,
never a modal unless a choice is needed (then `PromptSheet`).

| Cause (plan edge case) | Icon | Line | Action | Behaviour |
|---|---|---|---|---|
| PC and phone on different Telegram accounts | `SetIconWarning` | "Your PC uses another Telegram account." | "Switch" -> `TelegramPane` | sync stops; detected by user id in the Sync channel marker |
| Storage full | `SetIconWarning` | "Phone storage is full." | none | downloads pause; resume when > 200 MB free |
| Music folder permission lost (SAF) | `SetIconFolder` | reuse `ReconnectCard`: "Noctis lost access to your music folder." | "Reconnect folder" (`Library.AddFolderCommand`) | downloads + sync pause |
| Telegram unreachable / VPN off | `SetIconGlobe` | "Can't reach Telegram. Turn on your VPN." | none | search shows library only; online rows not shown; retry on route change (`IOnlineMusicSearch.Retry()` when `Route` changes) |
| Signed out / session revoked | `PhPaperPlane` | "Signed out of Telegram." | "Sign in" | sync stops, local data kept |
| Waiting (not an error) | `PhWifi` / `PhBolt` | "Waiting for Wi-Fi." / "Waiting to charge." | none | footnote in SyncPane only, never a Library card |
| Slow down (FLOOD_WAIT) | `SetIconHistory` | "Telegram asked to wait. Back in 4 min." | none | queue kept, countdown text only |
| Sync/Library channel deleted | `SetIconSync` | "Sync channel was rebuilt." | none | shown once after recreating |
| Song gone online | `PhCloud` struck | row sub-line "Missing online" | none | local file kept |

**Hooks.** `ViewModels/NoticeViewModel.cs` (new: `Icon`, `Line`, `ActionLabel`, `ActionCommand`, `Key`);
`ShellViewModel.Notices` (`ObservableCollection<NoticeViewModel>`, `Show(notice)`, `Clear(key)`);
`Views/LibraryNotices.axaml` gets an `ItemsControl` over `Shell.Notices` using the existing
`card` template. Sources raise them: `PhoneTelegramSync.Blocked`, `IDownloadQueue` failures
(`IOException` disk full -> storage notice), `LibraryViewModel.NeedsReconnect` (already exists),
`IOnlineMusicSearch` errors.

## 5. Build order and checks

1. Shared pieces (section 0) + `TelegramPane` (part 1). Check: sign in on the owner's phone with VPN on.
2. Search with online rows + play + download ring (part 2). Check: a known song plays, downloads, becomes a library row.
3. Downloads list + BotLibrary dot.
4. `SyncPane` + `PhoneTelegramSync` (part 3) after plan stage 1-2 land.
5. Notices (part 4); each row of the table gets a fake-driven UI test.

Every screen: dark and light theme, 360 px wide, right-to-left titles (Persian) in rows,
`AutomationProperties.Name` on every new control (existing pattern).
