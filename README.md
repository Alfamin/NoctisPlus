<div align="center">

<img src="src/Noctis/Assets/Icons/Noctis.ico" width="96" />

# Noctis Plus

### Search any song and play it straight away.

[Noctis](https://github.com/heartached/Noctis) with Free Music Finder, For you, WordLyrics, Lyric Motion<br/>
and True Shuffle built into the app.

<br/>

[![Release](https://img.shields.io/github/v/release/Alfamin/NoctisPlus?style=for-the-badge&color=8A5CF6&labelColor=2B2B2B&label=Latest)](https://github.com/Alfamin/NoctisPlus/releases)
&nbsp;
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=for-the-badge&logo=windows&logoColor=white&labelColor=2B2B2B)](#-install)
&nbsp;
[![License](https://img.shields.io/badge/License-MIT-2EA043?style=for-the-badge&labelColor=2B2B2B)](LICENSE)
&nbsp;
[![Based on Noctis](https://img.shields.io/badge/Based%20on-Noctis%201.6.3-E74856?style=for-the-badge&labelColor=2B2B2B)](https://github.com/heartached/Noctis)

<br/>

**[Install](#-install)** &nbsp;·&nbsp; **[Features](#-whats-inside)** &nbsp;·&nbsp; **[All changes](#-every-difference-from-noctis)** &nbsp;·&nbsp; **[Credits](#-credits)**

</div>

<br/>

---

## ✨ What's inside

<table>
<tr>
<td width="50%" valign="top">

### 🔎 Free Music Finder
Any song, album or artist, including ones you don't have yet.

- **Press Play and it starts while it downloads**
- Played songs stay in a cache; **Add to songs** keeps them
- Artist pages show the **full discography**
- Imported **Spotify and Deezer playlists** play every song, missing ones too
- Sign in to Telegram by **scanning a QR code**

</td>
<td width="50%" valign="top">

### 🧭 Search
One search box for your library **and** the online catalog.

- Results ranked by **popularity**
- Tolerant of typos and missing accents
- Search by a **line of lyrics**
- **Songs, Albums, Artists and Playlists**

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 💜 For you
25 songs a page, picked from what you listen to.

- Learns from what you **play, keep and skip**
- Play any pick, even before it's downloaded
- **Not for me** drops a song, **Don't recommend artist** drops an artist
- Your last **10 pages** are kept

</td>
<td width="50%" valign="top">

### 🔀 True Shuffle
Shuffle modes in the player, mini player and queue.

- **Equal chance, No repeats, Discovery, Never played, For you**
- Shuffle the queue, a playlist or the whole library
- Songs you add yourself play **first**, in your order

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 🎤 WordLyrics
Word-by-word lyric timing, made on your own PC.

- Times the song **you're playing**, and new songs automatically
- No lyrics? One **Time words** button
- **Eases off** while you're gaming or the PC is busy
- Nothing downloads until you press **Set up** (1.3 GB)

</td>
<td width="50%" valign="top">

### 🌊 Lyric Motion
Lyrics that move letter by letter with the singer.

- **Smooth, Crisp or Flowing**, at Subtle, Balanced or Expressive strength
- Long notes **grow and glow**
- **Right-to-left** for Persian, Arabic and Hebrew

</td>
</tr>
</table>

### 🧩 Also

| | |
|---|---|
| 🚀 **Set up from the first page** | Find music, add a folder, import playlists, connect Telegram and get word lyrics, all from the welcome card. |
| 🏠 **Runs next to Noctis** | Its own profile, install folder and updates. Keep both. |
| 📦 **Brings your stuff over** | First start can copy your Noctis library, playlists, stats and settings. |
| 🔄 **Updates itself** | Asks at startup, and checks every download against its published checksum. |
| 🎨 **Looks like Noctis** | New screens use Noctis's own controls, themes and accent colours. |

<br/>

---

## 📥 Install

Open **PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/Alfamin/NoctisPlus/main/scripts/install.ps1 | iex
```

Or grab the **Setup** from the [latest release](https://github.com/Alfamin/NoctisPlus/releases/latest). It isn't code-signed, so Windows SmartScreen may warn the first time: **More info › Run anyway**.

**Requirements:** Windows 10 or 11, and a Telegram account for finding and downloading songs. Word lyrics also need a graphics card or about 3 GB of free memory.

> [!TIP]
> Already using Noctis? Keep it. Plus installs separately, and both can run.

Building it yourself: **[QUICKSTART](docs/fork/QUICKSTART.md)**.

<br/>

---

## 🛠 Built in, not bolted on

These started out as add-on plugins. Since **2.0** they're part of the app itself.

| Feature | Native |
|---|:---:|
| Search, artist, album and playlist pages | ✅ 2.0 |
| For you | ✅ 2.0 |
| Free Music Finder (catalog, downloads, play while downloading) | ✅ 2.0 |
| Downloads list | ✅ 2.0 |
| Lyric Motion and the lyrics Sync button | ✅ 2.0 |
| True Shuffle | ✅ 2.0 |
| WordLyrics | ✅ 2.0 |

<br/>

---

## 📋 Every difference from Noctis

On top of Noctis 1.6.3, biggest first.

<details>
<summary><b>Show all 179 changes</b></summary>

#### 🔥 The big ones

1. Search any song, album, artist or playlist and download it into your library
2. Press Play on a song you don't have and it starts while it downloads
3. One search box for your own music and the whole online catalog
4. Sign in to Telegram by scanning a QR code
5. Every setup on the first page of a new install: music, folders, playlists, Telegram, word lyrics
6. For you: 25 songs a page from what you play, learning as you listen
7. Played songs stay in a cache, and Add to songs keeps them
8. Imported playlists, albums and artist pages play their missing songs too
9. Artist pages show everything the artist released, not just what's on your drive
10. Own half an album? The missing songs are listed under yours
11. Artists and albums you don't own still open as full Noctis pages
12. Import Spotify and Deezer playlists by pasting the link
13. Find public playlists in Search and save them as your own
14. Search by a line of lyrics
15. Shuffle modes: Equal chance, No repeats, Discovery, Never played, For you
16. Karaoke-style word-by-word lyrics, made on your own PC
17. Word timing while you listen, and for new songs automatically (Off, Light, Medium or Full)
18. Lyrics animate letter by letter, in time with the singer
19. Right-to-left lyrics for Persian, Arabic and Hebrew
20. Offline mode: your own music keeps playing while online work waits
21. A Downloads page where finished songs play right from the list
22. MP3 or FLAC
23. Two download bots, with automatic fallback to the other
24. Installs next to the original Noctis without touching it
25. First start copies your whole Noctis: library, play counts, playlists, stats and sign-ins
26. Updates itself, with Update or Later at startup and checksum-verified downloads
27. One-line install in PowerShell
28. Every feature has its own switch in Settings > Features
29. Scroll the lyrics freely; a Sync button brings you back to the current line
30. Opens about twice as fast
31. Dark theme and Liquid Glass out of the box
32. Community plugins made for Noctis 1.6.3 keep working

#### ⭐ Search and downloads

33. Results ranked by popularity across artists, songs and albums
34. Tolerant of typos and missing accents
35. Your own songs show up in results, ready to play
36. Songs, Artists, Albums and Playlists filters
37. Public playlists ranked by how many people saved them
38. Songs, playlists and Favorites each have their own search bar
39. Library results appear as you type, online ones a moment later
40. Your query and results are kept when you leave Search
41. Offline notice with a Retry button; reconnects by itself when your VPN comes on
42. Ctrl+Shift+F opens Search from anywhere (rebindable)
43. "Find more by this artist…" in every song's menu
44. A badge marks songs and albums that aren't in your library yet
45. Download straight from the results
46. The Downloads button shows how many are running
47. Clear a download from the list without deleting the song
48. Downloads are added to your library automatically
49. Choose any folder for your downloads
50. Uses your VPN's system proxy with no setup
51. Or set your own proxy, Telegram proxy links included
52. See which Telegram account is signed in, switch it or disconnect
53. Your own Telegram app id and phone number sign-in are still available
54. Two-step verification passwords are asked in the same window
55. The song you last pressed Play on goes first; earlier downloads wait
56. Download ahead: Off to 4 of the next songs (2 by default)
57. Albums download back to back
58. Choose which bot goes first: Music Hunters or DeezLoad
59. Files the bots sent before are remembered and reused
60. A small icon shows each step: asking the bot, trying the other, receiving, saving
61. Optional MP3 fallback when a song has no FLAC
62. Switch between MP3 and FLAC at any time, even mid-download
63. Stuck downloads restart automatically
64. The download queue survives closing the app
65. Remixes, live versions and radio edits are kept apart from the original
66. Interrupted streams resume instead of starting over
67. A paused song resumes at the same spot after a restart
68. Choose how long played songs stay in the cache

#### 🎨 Artist, album and playlist pages

69. Online albums open instantly and fill in as they load
70. Song count and album length update as the details arrive
71. The artist's newest release sits at the top of their page
72. Small catalog covers are replaced by high-resolution ones
73. A loading line while an artist's releases come in
74. When several artists share a name, the best-known one opens
75. Songs on artist pages play while they download
76. Your own albums find their missing songs in the background
77. Missing songs sit under "Not in your library" with a download button
78. Every missing song in a playlist has its own Play and Download
79. A downloaded song moves into its place in the playlist
80. Playlists keep repeated songs and unavailable entries, like Spotify does
81. Playlist cards count every song, not just the ones you own
82. Playlists from Search play in order before you save them
83. Sorting and filtering include the songs you don't have yet
84. Album tracklists offer Retry if they fail to load
85. The empty Playlists page links straight to Spotify and Deezer import

#### 🏠 Home and For you

86. A new install opens with Find music, Add folder, Import playlists, Connect Telegram and Word lyrics
87. A new library gets a Play button on Home right away
88. Empty Songs, Albums and Artists pages show how to add music
89. For you on Home, with album tiles
90. About half of each page from your favourite artists, the rest from related and new ones
91. Learns from what you play, keep, save, skip and scroll past
92. Picks based on your favourite songs as well as their artists
93. At most two songs per artist on a page
94. Songs you've already heard are left out
95. Every pick plays right away
96. Playing a For you song queues the rest of that page
97. Short plays don't count as skips; Not for me hides a song for good
98. Not for me next to the lyrics
99. Not for me on the playing song skips to the next one
100. Don't recommend artist, from cards, song menus, the player, artist pages and Search, with Undo
101. Your last 10 pages are kept, with a pager under the shelf
102. Reset For you clears what it learned
103. Refresh shows its progress, step by step
104. Optional Last.fm key for better picks

#### 🔀 Shuffle and queue

105. Shuffle modes in the player, mini player and queue
106. The shuffle button lights up for every mode
107. The menu shows which mode you're in
108. Songs you add yourself play before the shuffle, in your order
109. Shuffle the queue, the open playlist or your whole library
110. Leave out songs you disliked
111. Choose how many unheard songs the For you mix brings in
112. Ctrl+S opens shuffle, with the modes
113. The first shuffle after opening the app starts at once
114. A progress indicator while the shuffle gets ready
115. The queue scrolls by itself while you drag a song near its edge

#### 🎤 Lyrics

116. One "Time words" button on songs without lyrics
117. The playing song and the next two are timed first
118. The lyrics page shows when a song is queued or being timed
119. Fix a song's timing from its menu
120. Ignore a song from its lyrics menu; Time words turns it back on
121. Pause, resume or stop timing at any time
122. Time a whole folder, or your whole library, in one go
123. Eases off while a game or another app is using the PC
124. Switches to Light while a fullscreen app is open
125. About 3 GB of memory on the processor
126. Falls back to the processor if a graphics card keeps crashing
127. Nothing large downloads without your go, and you see the size first
128. Your audio files are backed up before anything is written
129. A notification when a song's timing is ready
130. Animation styles: smooth, crisp, flowing or off
131. Motion strength from subtle to expressive
132. Long notes grow and glow
133. Letters light up one by one, or whole words at once
134. The same motion in the side panel and the mini player
135. Mixed-direction lines, such as Persian inside an English song
136. The Sync button in the side panel too
137. Removing a song removes its lyric files too
138. The timing panel opens inside the app

#### 🧩 Smaller touches

139. A frosted player bar that stays readable over anything
140. Shuffle and 15-second skip buttons on the player by default
141. Settings that open something are buttons, not switches
142. Settings search covers the new features
143. Shorter text and tooltips throughout
144. Empty Downloads and Favorites pages say what to do in one line
145. The search bar stays above the results while you scroll
146. The side rail highlights Search while you're on it
147. Search and Downloads keep their state between visits
148. Back returns to the same search
149. What's new shows every update you missed, biggest changes first
150. Every update's notes in Settings > About
151. New text translated into all 10 Noctis languages
152. The mini player and About page say Noctis Plus, and credit Noctis 1.6.3 and heartached
153. Screen-reader names on many more buttons

#### 📦 Install and updates

154. Its own data folder, install folder and server ports, so it runs side by side with Noctis
155. Waits for Noctis to close before copying your profile
156. Never overwrites a Plus profile that already has your data
157. New installs download MP3; existing users keep their choice
158. The installer works through your VPN
159. Installer and update downloads resume and are verified
160. A quick first start after an update
161. A smaller download
162. Every release is built, tested and test-installed before it goes out
163. Public build results, including which tests failed

#### 🔧 Under the hood

164. One connection kept warm, so searches are quick
165. Apple Music's catalog fills in while Deezer is down
166. The connection refreshes when your VPN switches on or off
167. Big playlists load covers only for the rows on screen
168. Bot menus are pressed at a human pace
169. FLAC files are checked before they're kept
170. Unsafe file names from the bots are refused
171. Half-finished files are cleaned up after a crash
172. The log of a crashed run is kept
173. Every download step is logged with its timing
174. Huge playlists update in one go instead of row by row
175. Precompiled builds, less waiting on first use
176. About 6,800 automated tests and 1,700 download checks run on every change
177. Noctis Plus changes are MIT licensed
178. Every built-in part ships with its licence
179. Where every piece came from is written down in [PROVENANCE](docs/fork/PROVENANCE.md)

</details>

<br/>

---

## 💐 Credits

<table>
<tr>
<td align="center" width="50%">
<a href="https://github.com/heartached/Noctis"><b>Noctis</b></a><br/>
by <a href="https://github.com/heartached">heartached</a><br/>
<sub>The player all of this is built on.<br/>Please <a href="https://buymeacoffee.com/heartached">support the original</a>.</sub>
</td>
<td align="center" width="50%">
<a href="https://github.com/Pr0nE/noctic-download-plugin"><b>noctic-download-plugin</b></a><br/>
by <a href="https://github.com/Pr0nE">Pr0nE</a><br/>
<sub>The download plugin Free Music Finder grew from.<br/>MIT licensed.</sub>
</td>
</tr>
</table>

<div align="center">
<sub>Noctis Plus is released under the <a href="LICENSE">MIT License</a>. Licensing details are in <a href="docs/fork/PROVENANCE.md">PROVENANCE</a>.</sub>
</div>

<br/>

---

<details>
<summary><b>The original Noctis README</b> (its install commands and links point to official Noctis, not Plus)</summary>


<div align="center">

<h1>
  <img src="src/Noctis/Assets/Icons/Noctis.ico" width="48" align="absmiddle" />&nbsp;Noctis
</h1>

**A music player that respects what's yours. Zero tracking, total control.**

[![Discord](https://img.shields.io/badge/DISCORD-JOIN%20SERVER-5865F2?style=for-the-badge&logo=discord&logoColor=white&labelColor=4F4F4F)](https://discord.gg/BNCDZQUVx7) &nbsp; [![Downloads](https://img.shields.io/github/downloads/heartached/Noctis/total?color=E74856&style=for-the-badge&cacheSeconds=600)](https://github.com/heartached/Noctis/releases) &nbsp; [![Support](https://img.shields.io/badge/SUPPORT-BUY%20ME%20A%20COFFEE-FFDD00?style=for-the-badge&logo=buymeacoffee&logoColor=black&labelColor=4F4F4F)](https://buymeacoffee.com/heartached)

[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-blue.svg?style=for-the-badge)]()

</div>

---

## Screenshots

#### Artist pages

![Artist page](docs/images/screenshot-1.png)

#### Word-by-word karaoke lyrics

![Lyrics page](docs/images/screenshot-2.png)

#### Cover Flow

![Cover Flow](docs/images/screenshot-3.png)

#### Themes & accent colors

![Appearance settings](docs/images/screenshot-4.png)

---

## Install

**Windows**, via a package manager:

```powershell
# winget
winget install heartached.Noctis

# Scoop (add the bucket once, then install)
scoop bucket add noctis https://github.com/heartached/scoop-bucket
scoop install noctis
```

Or download the installer or portable zip from the
[latest release](https://github.com/heartached/Noctis/releases/latest).

**macOS and Linux**: download the `.dmg` or AppImage from the
[latest release](https://github.com/heartached/Noctis/releases/latest).
Both ship with everything they need, so there is nothing else to install.

**Self-hosting**: `noctis-server` is the same library scanner and OpenSubsonic API
without a window, for a NAS, a VPS or Docker. Grab `noctis-server-linux-x64.tar.gz`
or `noctis-server-linux-arm64.tar.gz` from the
[latest release](https://github.com/heartached/Noctis/releases/latest), or build the image:

```sh
docker build -t noctis-server .
docker run -d --name noctis -p 4747:4747 -v /path/to/music:/music:ro -v noctis-data:/data noctis-server
docker exec -it noctis /app/noctis-server user add alice
```

Options and client notes are in [docs/SELF-HOSTING.md](docs/SELF-HOSTING.md).

---

## Features

### Sound

- [x] Plays FLAC, ALAC, WAV, AIFF, APE, WavPack, MP3, AAC, OGG, Opus, WMA and M4A
- [x] Bit-perfect exclusive output on Windows
- [x] Parametric EQ with presets, and a saved preset per track if you want one
- [x] Gapless playback, crossfade and AutoMix transitions
- [x] ReplayGain and Sound Check volume leveling
- [x] Automatic BPM and musical key detection
- [x] Track Radio and Autoplay keep the music going when the queue runs out
- [x] Batch converter between formats (ffmpeg)
- [x] Audio CD playback
- [x] Multi-channel upmix, and pitch shift independent of playback speed
- [x] Spectrogram window and an audio visualizer (Bars, Mirror, Wave)

### Library

- [x] Songs, Albums, Artists, Folders and Playlists views
- [x] Albums split into Albums, Singles and EPs
- [x] Smart playlists, favorites, star ratings and play counts
- [x] Full metadata editor for artwork, lyrics and per-track options
- [x] Auto-tagging and cover art search using Deezer, MusicBrainz and Apple Music
- [x] Drag and drop import, watched folders, bulk edits
- [x] Playlist import from Exportify CSV, TuneMyMusic JSON and m3u files
- [x] Duplicate finder and file organizer
- [x] Command palette
- [x] Listening stats with a monthly and yearly Wrap
- [x] Artist pages with Top Songs, About, Similar Artists and fan counts from Deezer
- [x] Playlist and album import from TIDAL and Deezer links
- [x] Add from YouTube, Send to Folder, and Open with Noctis file associations
- [x] Explicit content toggle, with an Explicit badge on albums
- [x] Group Artists By with editable separators; drag and drop for playlists and the sidebar
- [x] Rebindable keyboard shortcuts

### Lyrics

- [x] Word-by-word karaoke lyrics, Apple Music style
- [x] Reads plain text, LRC, word-level LRC, TTML and Lyricsfile sidecars
- [x] Auto-fetched from LRCLIB and NetEase, cached offline
- [x] Lyrics panel you can keep open next to any page
- [x] Written By credits for the songwriters and producers
- [x] Built-in lyrics editor with `.lrc` export
- [x] Share lyrics as image cards or short clips
- [x] Lyrics Studio: speech-to-lyrics alignment on your own machine with Whisper
- [x] Video or GIF backgrounds on the lyrics page, per song or per album, and music videos in the artwork slot
- [x] Global lyrics offset and lyrics import from file

### Look and feel

- [x] Cover Flow browsing with Carousel, Cascade and Collage layouts
- [x] Now Playing artwork as a CD, a vinyl record or a cassette
- [x] Animated cover art
- [x] Ambient blurred backdrops on the lyrics page
- [x] Themes and accent colors, plus a custom theme editor
- [x] Liquid Glass translucent window mode
- [x] Resizable mini player with search, queue, volume and karaoke lyrics
- [x] Kawarp warped background, album pages tinted from the cover, accent color that can follow the album art
- [x] Settings organized into 13 searchable pages

### Connect

- [x] Stream from Jellyfin, Navidrome, Airsonic, Gonic or any Subsonic server
- [x] Built-in Noctis Server: stream your library to Symfonium, substreamer, Feishin or any Subsonic client
- [x] Self-hosted `noctis-server` for a NAS, a VPS or Docker, see [docs/SELF-HOSTING.md](docs/SELF-HOSTING.md)
- [x] Discord Rich Presence
- [x] Scrobble to Last.fm and ListenBrainz
- [x] Web remote so you can control playback from your phone
- [x] Media keys on every platform, plus Windows taskbar controls
- [x] Artist images from Deezer
- [x] Sleep timer, tray icon and launch at login
- [x] Updates itself from GitHub releases
- [x] Song progress on the Windows taskbar

### Languages

- [x] English, Arabic, French, Japanese, Korean, Russian, Spanish, Turkish, Simplified Chinese and Traditional Chinese
- [x] Switch language from Settings without a restart

<p align="center">
  <img src="docs/images/discord.png" width="380" alt="Discord Rich Presence" />
</p>

---

## Plugins

Noctis can be extended three ways, from safest to most powerful:

- **Content packs**: themes, lyrics presets and languages as plain JSON, with no code. They keep working in restricted mode. See [Content packs](docs/PLUGINS.md#content-packs).
- **Local API**: HTTP + JSON on 127.0.0.1 for Stream Deck buttons, OBS overlays and scripts in any language. Nothing runs inside Noctis. See the [Local API reference](docs/LOCAL-API.md).
- **Plugins**: .NET libraries built on the `Noctis.Plugins.Abstractions` SDK, with a `plugin.json` manifest and permissions, installed from a .zip. See the [plugin guide](docs/PLUGINS.md).

Community plugins start off (restricted mode) and ask for approval before they run. Sample plugins, a sample content pack and Local API examples are in [`samples/`](samples/).

---

## Build

```bash
git clone https://github.com/heartached/Noctis
cd Noctis
dotnet run --project src/Noctis/Noctis.csproj
```

**Requirements:** .NET 10 SDK

Supported platforms: Windows 10/11 (x64), macOS 12+ (Intel and Apple Silicon), Linux (x64 and ARM64).

### Native dependency: libvlc

The released downloads already carry everything they need. This only matters if
you are building from source.

- **Windows:** bundled automatically via NuGet, nothing to install.
- **macOS:** install [VLC](https://www.videolan.org/vlc/), which Noctis loads from
  `/Applications/VLC.app`. Packaged release builds bundle their own copy of VLC
  inside the app, so a downloaded Noctis needs no VLC install.
  ```bash
  brew install --cask vlc
  ```
- **Linux:** install via your package manager. The `-dev` package provides the
  unversioned `libvlc.so` symlink that the .NET loader looks for. The released
  AppImage bundles libvlc and its plugins, so it runs without this.
  ```bash
  # Debian/Ubuntu
  sudo apt install libvlc-dev
  # Fedora
  sudo dnf install vlc-devel
  # Arch
  sudo pacman -S vlc
  ```

### Running a downloaded build (macOS / Linux)

The macOS and Linux artifacts on the [Releases page](https://github.com/heartached/Noctis/releases)
are unsigned self-contained builds.

**macOS**, using the portable zip:
```bash
unzip Noctis-osx-arm64.zip
xattr -dr com.apple.quarantine Noctis.app   # remove Gatekeeper quarantine flag
open Noctis.app
```

**Linux**, using the AppImage:
```bash
chmod +x Noctis-x86_64.AppImage
./Noctis-x86_64.AppImage
```

Or the portable tarball, which extracts without a top-level folder:
```bash
mkdir noctis && tar -xzf Noctis-linux-x64.tar.gz -C noctis
chmod +x noctis/Noctis
./noctis/Noctis
```

### Build for another OS

```bash
dotnet publish src/Noctis/Noctis.csproj -c Release -r linux-x64   --self-contained
dotnet publish src/Noctis/Noctis.csproj -c Release -r osx-arm64   --self-contained
dotnet publish src/Noctis/Noctis.csproj -c Release -r osx-x64     --self-contained
dotnet publish src/Noctis/Noctis.csproj -c Release -r linux-arm64 --self-contained
```

### Build the server

```bash
dotnet publish src/Noctis.Server/Noctis.Server.csproj -c Release -r linux-x64 --self-contained
docker build -t noctis-server .
```

---

## Star History

[![Star History Chart](https://star-history.dera.page/svg?repos=heartached/Noctis&type=Date)](https://star-history.dera.page/#heartached/Noctis&Date)

---

## Feedback

If you have any feedback about bugs, feature requests, etc. about the app, please let me know through [issues](https://github.com/heartached/Noctis/issues).

Yours Truly, heartached.

---

## License

MIT, see [LICENSE](LICENSE)

---

> [!WARNING]
> Windows may flag the installer as untrusted because it isn't code-signed. This is normal for indie software and the app is safe to use.

</details>
