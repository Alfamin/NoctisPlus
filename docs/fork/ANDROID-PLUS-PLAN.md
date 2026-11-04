# Noctis Plus on Android: plan (owner #255, 2026-11-04)

Status: RESEARCHED + DESIGNED. Nothing built yet. Read this whole file before writing code.

## What the owner wants

1. The Plus features on the Noctis Android app, as many as make sense on a phone.
2. Free Music Finder (the Telegram music bots) on the phone.
3. One library across phone and PC, even when they are far apart and never on the same Wi-Fi.
   A song downloaded on the phone shows up on the PC, and the other way round, automatically.
4. Fast, reliable, light, few bugs. Free: no paid services, no API keys for the user (repo rule).
   Preferably no server.

## What already exists (verified in code, 2.7.0 = a1ca2c58)

- `src/Noctis.Android` + `src/Noctis.Mobile`: heartached's phone app (Avalonia 12, net10.0-android, Media3 player).
  Shares `Noctis.Core` and `Noctis.UI` with the desktop. Beta, not public. ApplicationId `com.heartached.noctis`.
- Phone <-> PC today is **LAN only**:
  - The PC's built-in server (`Noctis.Core.Server/NoctisServer`, Subsonic-style + `noctis*` methods,
    HTTPS with pinned self-signed cert, QR pairing `noctis-pair:1`, port 4747).
  - The phone streams/downloads the PC's songs, sends its own songs to the PC
    (`NoctisAccountService.Upload`: matches by title+artist+length via `noctisHasTracks`, then uploads).
  - Sync ledger `Noctis.Core/Services/Sync` (`SyncStore`, SQLite, state-based last-writer-wins,
    kinds `track` (favorite, rating, disliked, playCount, lastPlayed) and `playlist`), hosted by the PC.
    Item ids are the **PC's track Guids** (`NoctisRemoteIds`: `noctis-remote://tr-<32 hex>`).
- `src/Noctis.Server`: a headless `noctis-server` (same data layout as the desktop).
- Free Music Finder: `src/Noctis/Features/FreeMusicFinder`, 57 files / 13k lines, WTelegramClient 4.4.9
  (pure .NET, documented to run on Xamarin/Android). Only 4 files touch Avalonia
  (ArtistPicture, FreeMusicPlugin, PlaylistLibrary, Shortcut) and 1 is Windows-only (Proxy: system proxy).
  It is plugin-shaped (talks to Noctis through the plugin host API).
- **BotLibrary** (#105): an index of every audio file the bots ever sent in the user's chats
  (owner: 3,816 files in 22 s, `Messages_Search` + `InputMessagesFilterMusic`). Those files live in the
  user's Telegram account, so **they are already reachable from any device signed in to that account**.
- CI: `.github/workflows/dotnet.yml` job `android` already compiles `Noctis.Android` on ubuntu-latest
  (it passed on the 2.7.0 commit). GitHub's runner has the Android SDK; it can produce an APK.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| **Telegram as the shared drive + sync log (own account, private channels)** | **CHOSEN** | Nothing to host. Works whenever Telegram works, which the Finder already needs. Bot songs are already stored there. Unlimited storage (2 GB per file). PC can be off. |
| Small server on the owner's VPS (`noctis-server` sync-only) | Fallback only | Always-on hub, reuses HTTP sync code. But the disk is tiny (state only, songs still need Telegram), it is one more thing to keep alive, it adds a second network path that can be filtered, and a new open port on the VPN box can draw attention to the VPN's IP. Telegram already gives an always-on place, so the upside is small. |
| Tailscale / ZeroTier to reach the PC from anywhere | No | Android allows **one VPN at a time**; it would fight v2rayNG. Tailscale's relays are reported blocked in Iran. PC must be on. |
| Syncthing | No | Separate app (official Android app discontinued Dec 2024, a fork continues), needs both devices online or relays, syncs files but not library state. |
| Google Drive / Dropbox | No | 15 GB free is too small, keys and sign-in, reachability. |
| Telegram reactions on bot messages as state | No | Owner removed reaction import (#92). Few emoji per message, fragile. |

Facts behind this (sources in the research notes at the end):
- Telegram API terms: own `api_id` (the Finder has one), say the app uses the Telegram API,
  no "Telegram" in the app name, no data for AI training. Nothing forbids storing your own files.
- Non-Premium accounts can get `FLOOD_PREMIUM_WAIT_X` on uploads only after **tens of gigabytes**;
  downloads have a speed cap Premium lifts (user reports ~10 MB/s range). A song is 5-40 MB: fine.
- Unofficial-client sessions are watched by Telegram for abuse: pace everything, honour `FLOOD_WAIT`,
  no bursts of thousands of messages.
- Telegram is blocked in Iran; the owner already uses a VPN/proxy (desktop follows the system proxy;
  on Android a VPN app such as v2rayNG covers all apps). During national shutdowns only LAN works,
  so the existing LAN path must stay.

## Design

### 1. Where the songs live

Every library song gets **one Telegram home** on the owner's own account:
- A song the bots sent: its message in the bot chat (already indexed by BotLibrary).
- Any other song (ripped, imported, phone-only): uploaded once into a private channel the app creates,
  **"Noctis Plus Library"**. Wi-Fi only by default, one file at a time, paced, resumable.
- Optional "Keep a copy of bot songs": *forward* the bot message into the Library channel
  (server-side, no re-upload) so deleting a bot chat loses nothing. Paced (for example 1 per 2 s).

### 2. Song identity across devices

Use the Telegram **document id** (`Document.id`, stable for the same stored file; forwards keep it: VERIFY this first with a real forward)
as the cross-device song id: `tg:<documentId>`. Each device keeps a local map
`documentId <-> local Track` (and the message location `peer + msgId` to download it; file references
are refreshed by re-reading the message, as BotLibrary already does).
Fallback match for the same recording in different files: the existing recording key
(artist + title + duration within 2 s, `RecordingMatch.TitleKey`), same as `noctisHasTracks`.

### 3. How library state syncs (no server)

A second private channel, **"Noctis Plus Sync"**, is an append-only log:
- Each post is a small JSON document (gzip if > 3 KB) with a batch of ledger items:
  `{ v:1, device, seq, items:[{kind, id, payload, updatedUtc}] }`.
- Kinds: the existing `track` and `playlist` payloads, plus a new `song`
  (library membership: `{documentId, peer, msgId, title, artist, album, durationMs, size, addedUtc, removed}`).
- Ids are the cross-device ids (`tg:<documentId>`; playlists keep their Guid ids, track lists as `tg:` ids).
- Each device reads posts newer than the last message id it saw (`Messages_GetHistory` with `min_id`)
  and merges with the **same last-writer-wins rule as `SyncStore`** (ties by device id, clock-skew clamp).
- Every N posts (or weekly) a device posts a full **snapshot** document and pins it; a new device reads
  the snapshot + newer posts. Old posts can then be deleted.
- Channels are found again by an id marker in the channel description (`noctis-plus:library:v1:<guid>`),
  never by title alone.

### 4. Auto-download (the "it just shows up" part)

On each device: "Download new songs automatically" (default on, Wi-Fi only on the phone; optional
"only while charging"). A new `song` item that is not in the local library is downloaded from its
Telegram home into the device's music folder and scanned in. Removing a song is a tombstone:
it leaves the library everywhere, and the file is deleted only if the user turned that on.

### 5. When both devices are home

The existing LAN pairing and server keep working and stay the fast path (heartached's protocol,
unchanged, so his phone app keeps working with Plus). Plus devices may also exchange the Telegram-sync
batches over LAN later; not needed for version 1.

### 6. Free Music Finder on the phone

Move the Finder's non-UI core (catalog search, MusicSources, bot pacing and timings, BotLibrary,
download queue, Telegram client wrapper, For You) into a **shared net10.0 library** (for example
`src/Noctis.Finder`) that both `Noctis` (desktop) and `Noctis.Mobile` reference. Keep the desktop
behaviour byte-for-byte (the Finder test project must stay green). The phone signs in to Telegram
**on its own** (phone number + code; QR login needs a second device), with its own session file in
the app's private data folder (never copy the PC's session). Proxy: system/VPN by default, optional
MTProto proxy field (WTelegramClient supports MTProxy).

### 7. The Plus phone app

Build a Plus variant of the Android head: ApplicationId `com.alfamin.noctisplus`, name "Noctis Plus",
so it installs **next to** heartached's beta. Release APKs from CI (GitHub Actions), signed with one
keystore kept as a repo secret so updates install over each other.

### Feature map

| Plus feature | Phone plan |
|---|---|
| Free Music Finder (search, play, download from bots, BotLibrary) | Yes, stage 3 |
| Library sync + auto-download | Yes, stages 1-2 |
| True Shuffle modes | Yes, stage 4 (service is mostly UI-free) |
| Lyric Motion letter rise, Plus lyrics look | Yes, stage 4 (port LetterLayer/Tuning into the mobile lyrics page) |
| WordLyrics word timing | The phone cannot run the model; it uses `.elrc` made on the PC (sync them with the song, posted as a second document) and the online word-timing lane |
| Progressive playback (play while the bot file downloads) | Later, stage 5 (Media3 progressive source) |
| Lyrics Reels | No (desktop rendering + ffmpeg) |

## Stages and checks

1. **Telegram sync core (shared library, no UI).** Channel bootstrap/find, post/read batches,
   snapshot, merge into a `SyncStore`-compatible ledger, song model, document-id map, paced uploader
   and downloader with `FLOOD_WAIT` handling, all behind an interface with a fake in-memory Telegram
   for tests. Unit tests: two fake devices converge (adds, removes, hearts, play counts, playlists,
   conflicts, clock skew, lost posts, snapshot + tail, duplicate recordings).
2. **Desktop wiring.** Settings > Features > "Sync with phone over Telegram" (off by default),
   uses the Finder's existing Telegram sign-in; posts the desktop's songs and state; auto-downloads
   new songs; Library channel upload of non-bot songs (Wi-Fi/idle only).
3. **Finder core extraction** into the shared library (desktop unchanged, all tests green), then the
   phone UI: search, play, download, Telegram sign-in, settings.
4. **Phone wiring** of sync + auto-download, True Shuffle, Lyric Motion.
5. Plus APK in CI + signing; progressive playback.

Each stage: full desktop gates (Noctis.Tests + FreeMusicFinder.Tests) green, Android head compiles,
live check on the owner's phone or an emulator before DONE.

## Performance budgets (owner: performance matters)

Measure these with tests or benchmarks; a stage is not DONE if it misses one.
- Sync never runs on the UI thread and adds no more than 50 ms to app start (it starts after the first frame).
- Merging 10,000 songs + 10,000 track states: under 200 ms on the PC, under 1 s on a mid-range phone.
- Snapshot of a 10,000-song library: under 2 MB gzip, built in under 500 ms.
- Reading 100 new log posts: one history call per 100 posts; no per-item network calls.
- Live updates: use WTelegramClient's update stream while the app is open (no tight polling);
  when updates are not flowing, poll at most every 2 minutes in the foreground. On the phone in the
  background: no polling at all except an Android WorkManager job (Wi-Fi, at most every 6 hours, off by default).
- Transfers stream to disk in chunks (never a whole file in memory); one upload and one download
  at a time; playback and the Finder's play requests always go before background sync transfers.
- Battery/data on the phone: auto-download Wi-Fi only by default, optional "only while charging".
- Telegram calls are paced and honour FLOOD_WAIT; never more than about 1 post per few seconds,
  forwards at most about 1 per 2 s.

## Edge cases to handle (each needs a test)

Sync and identity
- Same song added on both devices while apart (different document ids): one library entry
  (recording-key match), the other copy is kept as an alternate source, not shown twice.
- A song removed on one device while the other changes its heart or play count: removal wins for
  membership, the state is kept (it comes back if the song is added again).
- **Play counts must not be lost**: plays on two devices while apart must add up. Store play counts
  per device (each device owns its own counter, total = sum) instead of last-writer-wins on one number.
- **Playlists edited on both devices while apart**: merge additions and removals per song
  (add/remove sets with timestamps) instead of replacing the whole playlist; order: last writer.
- A device offline for months: snapshot + newer posts; posts older than the snapshot may be gone.
- Posts read twice, out of order, or with gaps: idempotent apply by (device, seq).
- Device clock wrong by hours: existing clamp rule.
- The LAN sync (heartached's protocol) and Telegram sync both active: the same change must not be
  applied twice or counted twice.

Telegram
- Sync or Library channel deleted or left by the user: detect, recreate from local state, tell the user once.
- Phone and PC signed in to **different** Telegram accounts: detect by user id and refuse to sync, say why.
- Telegram session revoked or signed out on one device: stop sync, show sign-in, keep local data.
- Bot message deleted, bot chat cleared, expired file reference: re-read, else fall back to another
  copy (Library channel), else mark the song "missing online" without deleting the local file.
- FLOOD_WAIT storms and FLOOD_PREMIUM_WAIT: back off, keep the queue, show progress, never spin.
- VPN or proxy drops mid-transfer: resume from the last finished part, no corrupt files.

Files
- Partial downloads: write to a temp file, verify size (and hash when known), then atomic rename.
- Storage full on the phone; the music folder's Android (SAF) permission lost: pause, tell the user, no retry loop.
- File name collisions and names invalid on the other OS; Persian/Arabic titles (Unicode
  normalisation NFC, right-to-left text), very long names.
- The user deletes a downloaded file by hand: the next scan notices; it is not re-downloaded
  unless the song is still in the synced library and auto-download is on (then it is, once).
- Same file already in the local library under another path: link it, don't download again.

## Research notes (2026-11-04)

- Telegram API terms: https://core.telegram.org/api/terms
- Telegram file transfer limits, FLOOD_PREMIUM_WAIT: https://core.telegram.org/api/files
- Premium lifts download speed limits: https://telegram.org/faq_premium ; user speed reports:
  https://github.com/telegramdesktop/tdesktop/issues/24700
- Unofficial clients watched for abuse: https://docs.telethon.dev/en/v2/basic/next-steps.html
- WTelegramClient supports Xamarin/Mono.Android: https://www.nuget.org/packages/WTelegramClient
- Telegram-as-storage projects exist (TeleDrive etc.): https://github.com/khrj/teledrive
- Syncthing Android discontinued: https://itsfoss.com/news/syncthing-android-app-no-more/
- Tailscale relays blocked in some countries: https://tailscale.com/kb/1232/derp-servers
- Telegram in Iran, proxies: https://www.pcrisk.com/blog/tips/14063-how-to-bypass-telegram-ban
