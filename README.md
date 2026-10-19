<div align="center">

<img src="src/Noctis/Assets/Icons/Noctis.ico" width="96" />

# Noctis Plus

### Your music player, now with a way to find more music.

Everything you love about [Noctis](https://github.com/heartached/Noctis), plus discovery, downloads,<br/>
smarter shuffle and lyrics that move with the song, all built right into the app.

<br/>

[![Release](https://img.shields.io/github/v/release/Alfamin/NoctisPlus?style=for-the-badge&color=8A5CF6&labelColor=2B2B2B&label=Latest)](https://github.com/Alfamin/NoctisPlus/releases)
&nbsp;
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=for-the-badge&logo=windows&logoColor=white&labelColor=2B2B2B)](#-install)
&nbsp;
[![License](https://img.shields.io/badge/License-MIT-2EA043?style=for-the-badge&labelColor=2B2B2B)](LICENSE)
&nbsp;
[![Based on Noctis](https://img.shields.io/badge/Based%20on-Noctis%201.6.3-E74856?style=for-the-badge&labelColor=2B2B2B)](https://github.com/heartached/Noctis)

<br/>

**[Install](#-install)** &nbsp;·&nbsp; **[Features](#-whats-inside)** &nbsp;·&nbsp; **[All 200 changes](#-every-difference-from-noctis)** &nbsp;·&nbsp; **[Credits](#-credits)**

</div>

<br/>

---

## ✨ What's inside

<table>
<tr>
<td width="50%" valign="top">

### 🔎 Free Music Finder
Look up any artist, album or song, even ones you don't own yet, and pull them straight into your library.

- **Play any song while it downloads**
- Artist pages show their **full discography**, not just what's on your drive
- Albums and imported **Spotify / Deezer playlists** list the songs you're missing
- Grab one song or **everything that's missing**; it lands in your library

</td>
<td width="50%" valign="top">

### 🧭 Search
One search box for your library **and** the online catalog.

- **Songs, Albums and Artists** tabs, styled just like the rest of Noctis
- Songs you already have show up as your own tracks, ready to play
- Works offline too, with a clear notice and a **Retry** button when the catalog can't be reached

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 💜 For you
Suggestions picked from what you actually listen to.

- Press **Play** on any pick, even before it's downloaded
- **Not for me** skips a song, and never mutes the whole artist
- **Refresh** shows a progress ring while it builds your list

</td>
<td width="50%" valign="top">

### 🔀 True Shuffle
Shuffle that feels random, the way you'd expect.

- Modes: **Equal chance, No repeats, Discovery, Never played, For you**
- Songs you add yourself stay **ahead** of the shuffle, in order

</td>
</tr>
<tr>
<td width="50%" valign="top">

### 🎤 WordLyrics
Word-by-word timed lyrics, made for you on your own PC.

- **Times new songs automatically** (Off, Light, Medium or Full)
- A song without lyrics gets one **Time words** button
- Runs locally, and sets up only when you choose to

</td>
<td width="50%" valign="top">

### 🌊 Lyric Motion
Lyrics that move with the music: smooth, subtle and always in sync.

- **Letter-by-letter** animation, with Smooth, Crisp and Flowing styles
- Choose how strong it feels: **Subtle, Balanced or Expressive**
- **Right-to-left** support for Persian, Arabic and Hebrew

</td>
</tr>
</table>

### 🧩 And a few quality-of-life touches

| | |
|---|---|
| 🏠 **Lives next to Noctis** | Its own profile, install folder and update feed, so Plus and the original never touch each other's settings. |
| 📦 **Brings your stuff over** | On first launch, Plus can copy your existing Noctis library, playlists, stats and settings. |
| 🔄 **Updates itself** | New versions come straight from this repo's Releases, checked against published checksums. |
| 🎨 **Looks like Noctis** | Every new screen uses Noctis's own controls, themes and accent colors. Nothing feels bolted on. |

<br/>

---

## 📥 Install

Open **PowerShell** and paste:

```powershell
irm https://raw.githubusercontent.com/Alfamin/NoctisPlus/main/scripts/install.ps1 | iex
```

Prefer clicking? Grab the **Setup** from the [latest release](https://github.com/Alfamin/NoctisPlus/releases/latest).

> [!TIP]
> Already using Noctis? Keep it. Plus installs separately, and you can run both.

Want to build it yourself? See **[QUICKSTART](docs/fork/QUICKSTART.md)**.

<br/>

---

## 🛠 Where it's heading

Plus started out with these features as add-on plugins. Since **2.0** they are all built **natively into the app**, so they look, feel and perform like they were always part of Noctis.

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

What you get on top of official Noctis 1.6.3, biggest first.

<details>
<summary><b>Show all 200 changes</b></summary>

#### 🔥 The big ones

1. Search for any song, album or artist and download it straight into your library
2. Hit Play on a song you don't have yet and it starts while it downloads
3. One search box covers your own music and the whole online catalog
4. Artist pages show everything the artist released, not just what's on your drive
5. Own half an album? The missing songs are listed right under yours
6. Artists and albums you don't own at all still open as full Noctis pages
7. Bring over your Spotify and Deezer playlists by pasting the link
8. Imported playlists keep every song in the original order, even the ones you don't have yet
9. Grab everything missing from an album or playlist with one button
10. For you suggests songs and albums based on what you actually play
11. Shuffle modes that make sense: Equal chance, No repeats, Discovery, Never played, For you
12. Karaoke-style word-by-word lyrics, made on your own PC
13. New songs get their word timing by themselves (Off, Light, Medium or Full)
14. Lyrics animate letter by letter, in time with the singer
15. Persian, Arabic and Hebrew lyrics read right to left, as they should
16. A Downloads page where finished songs play right from the list
17. Sign in to Telegram inside the app with your phone number and code
18. Pick FLAC or MP3, and both download bots respect it
19. Two download bots: if one can't find a song, the other is asked
20. Installs next to official Noctis without touching it
21. First start copies your whole Noctis: library, play counts, playlists, stats and sign-ins
22. Updates itself, and every update is checked against its published checksum
23. Installs with one line in PowerShell
24. Everything comes built in; there's nothing extra to set up
25. Every feature has its own on/off switch in Settings > Features
26. Scroll the lyrics freely; a Sync button brings you back to the current line
27. Opens about twice as fast
28. Dark theme and Liquid Glass out of the box
29. Community plugins made for Noctis 1.6.3 keep working
30. Your Last.fm sign-in stays put

#### ⭐ Search and downloads

31. "drake passionfruit" gives you Drake's Passionfruit, not karaoke covers
32. Type an artist's name and you get their songs first
33. Small typos still land on the right artist ("post melone")
34. A famous artist shows up before some tiny account with the same name
35. Songs you already own show up as yours, ready to play
36. Songs, Artists and Albums filters
37. Leave Search and come back: your query and results are still there
38. No internet? A clear banner with a Retry button
39. Turn your VPN on and Search reconnects by itself
40. Ctrl+Shift+F opens Search from anywhere (you can change the keys)
41. "Find more by this artist…" from any song's menu
42. A small badge tells you which songs and albums aren't in your library yet
43. Download right from the search results
44. The Downloads button shows how many are going
45. Clear a download from the list without deleting the song
46. Downloads go into your library on their own
47. Choose any folder for your downloads
48. Works through your VPN's system proxy with no setup
49. Or set your own proxy, Telegram proxy links included
50. See which Telegram account is signed in, switch it or disconnect
51. The bots' quality setting is checked once, not before every song
52. Allow MP3 when a song has no FLAC, or keep it FLAC-only
53. You see each step: transfer, checking, saving
54. 100% means the file is actually saved
55. Waits as long as the bot really needs, no more
56. A dead bot or dropped connection never freezes your queue
57. A stuck transfer restarts without signing you out
58. Close the app mid-download and the queue is still there next time
59. Clicking Play five times sends one request, not five
60. A song that just failed isn't sent again and again
61. Gets the right song even when Apple and Deezer list slightly different lengths
62. A remix, live cut or radio edit never sneaks in as the original
63. Two different songs with the same title never overwrite each other
64. Seeking works as soon as the download is saved
65. A slow connection pauses once with a ring instead of stuttering
66. The first seconds of a downloading song are never skipped
67. If a downloading song stops early, it carries on from the saved file
68. A paused song comes back at the same spot after a restart

#### 🎨 Artist, album and playlist pages

69. Online albums open instantly and fill in as they load
70. Song count and album length update as the details arrive
71. Never a fake "0 songs" or "0 min"
72. The artist's newest release sits at the top of their page
73. The latest release shows its real song count
74. Small catalog covers swap to sharp ones
75. A soft glowing line while an artist's releases load
76. Two artists with the same name? You pick the right one once
77. Songs on artist pages play while they download
78. Artists you own open as the right online artist, not a namesake
79. Your own albums find their missing songs in the background
80. Missing songs sit under "Not in your library" with a download button
81. Just looking around never downloads anything
82. Albums with nothing on your drive hide the buttons that wouldn't work
83. Every missing song in a playlist has its own Play and Download
84. A song you download moves into its spot in the playlist
85. Repeated songs and unavailable entries stay in the playlist, like on Spotify
86. Playlist cards count all the songs, not just the ones you own
87. Play and Shuffle hide while a playlist has nothing to play yet
88. Missing songs get a music note instead of an empty black box
89. Sorting and filtering include the songs you don't have yet
90. The empty Playlists page points you straight to Spotify or Deezer import

#### 🏠 Home and For you

91. A fresh install greets you with Find music, Add folder and Import
92. A brand-new library gets a Play button on Home right away
93. Empty Songs, Albums and Artists pages show how to add music
94. For you lives on Home, with album tiles
95. Every For you pick can be played right away
96. "Not for me" drops a song without hiding the whole artist
97. Refresh shows its progress, step by step
98. If a refresh fails you keep the picks you had
99. Add a free Last.fm key for even better picks
100. Opening one album from For you doesn't grey out the others

#### 🔀 Shuffle

101. Shuffle modes in the player, mini player and queue
102. The shuffle button lights up for every mode
103. The menu shows which mode you're in
104. Songs you add yourself play before the shuffle, in your order
105. Shuffle the queue, the open playlist or your whole library
106. Leave out songs you disliked
107. Choose how many unheard songs the For you mix brings in
108. Ctrl+S still opens shuffle, now with the modes
109. Start a shuffle right after opening the app, no need to pick a song
110. You can see the shuffle getting ready

#### 🎤 Lyrics

111. No lyrics? One "Time words" button gets them timed
112. The lyrics page shows when a song is queued or being timed
113. Fix a song's timing from its menu
114. Pause, resume or stop timing whenever you want
115. Time a whole folder of songs in one go
116. Nothing big downloads until you say so, and you see the size first
117. Your audio files are backed up before anything touches them
118. Get a quick note when a song's timing is ready
119. Animation styles: smooth, crisp, flowing or off
120. Make the motion subtle or bold
121. Long notes grow and glow
122. Letters light up one by one, or whole words at once
123. The same motion in the side panel and the mini player
124. A Persian line inside an English song still reads right
125. Lyrics stay in time after you pause and play
126. Lyrics keep following as long as the current line is on screen
127. Scrolling the lyrics no longer snaps back on its own
128. The Sync button works in the side panel too
129. Removing a song cleans up its lyric files too
130. The timing panel opens inside the app, not in a separate window

#### 🧩 Smaller touches

131. The player bar is frosted, so it stays readable over anything
132. Shuffle and 15-second skip buttons on the player by default
133. Settings that open something are proper buttons, not switches
134. Settings search finds the new feature settings
135. Less text on screen: a label and one short line
136. 41 long tooltips cut down
137. No more wall-of-text tooltip on the search box
138. Short albums say "24 sec", not "0 min"
139. No "0 KB" on playlists with nothing downloaded
140. The Favorites tab lights up like the other tabs
141. The Favorites heart sits properly centred
142. Empty Downloads and Favorites pages tell you what to do in one line
143. The search bar stays above the results while you scroll
144. The side rail lights up Search while you're on it
145. Search and Downloads stay as you left them
146. Pages opened from Search don't carry a stray "Search" title
147. Back takes you to the same search
148. An old song no longer shows a Pause button
149. The mini player says Noctis Plus
150. About credits Noctis 1.6.3 and heartached
151. The About page opens faster
152. The Downloads menu uses Noctis's own icon
153. Screen readers can name many more buttons
154. The Telegram sign-in's Advanced toggle matches the app
155. Album footers under a minute show seconds

#### 📦 Install and updates

156. Its own data folder, so nothing mixes with official Noctis
157. Its own install folder
158. Its own server ports, so both apps can run side by side
159. Waits for official Noctis to close before copying your profile
160. Never copies over a Plus profile that already has your stuff
161. The installer works through your VPN
162. A broken download of the installer resumes and is verified
163. The install command finishes even while Plus is open
164. The first start after an update is quick
165. A smaller download
166. Updates like 1.6.3.1 and 1.6.3.2 are never skipped
167. Slow connections can still finish an update
168. A stalled update download tries again
169. Every release is built, tested and test-installed before it goes out
170. Anyone can see which tests failed on a build

#### 🔧 Under the hood

171. Your library results appear as you type each letter
172. Online results follow a moment later
173. One connection stays warm, so searches are quick
174. If Deezer is down, Apple Music's catalog fills in
175. Once Deezer is back, its results return within 10 minutes
176. Switching your VPN on or off refreshes the connection
177. Big playlists only load covers for the rows you can see
178. The bots' menus are pressed at a human pace, so they don't get confused
179. A bot menu that changes is reported once, not on every song
180. A bot that stops answering isn't waited on for every song
181. Telegram sign-in gives up after a minute with a clear message
182. Turning downloads off really disconnects Telegram
183. MP3 downloads no longer fail with "Stream does not support reading"
184. A file that claims to be FLAC is checked before it's kept
185. Bad file names from the bots are refused
186. Half-finished files are cleaned up after a crash
187. If the app ever dies, the log of that run is kept for a fix
188. Every download step is logged with its timing
189. The queue remembers the current song by its file
190. Different artists with the same name stay separate
191. The artist you meant comes first in the artist results
192. Huge playlists update in one go instead of row by row
193. Older imports without covers show a placeholder
194. Precompiled builds, less waiting on first use
195. About 6,600 automated tests and 1,200 download checks run on every change
196. Tests that used to fail at random were fixed
197. Reset and fallback themes are Dark too
198. Noctis Plus changes are MIT licensed
199. Every built-in part ships with its licence
200. Where every piece came from is written down in [PROVENANCE](docs/fork/PROVENANCE.md)

</details>

<br/>

---

## 💐 Credits

<table>
<tr>
<td align="center" width="50%">
<a href="https://github.com/heartached/Noctis"><b>Noctis</b></a><br/>
by <a href="https://github.com/heartached">heartached</a><br/>
<sub>The beautiful player all of this is built on.<br/>Please <a href="https://buymeacoffee.com/heartached">support the original</a>.</sub>
</td>
<td align="center" width="50%">
<a href="https://github.com/Pr0nE/noctic-download-plugin"><b>noctic-download-plugin</b></a><br/>
by <a href="https://github.com/Pr0nE">Pr0nE</a><br/>
<sub>The download plugin that Free Music Finder grew from.<br/>MIT licensed.</sub>
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
