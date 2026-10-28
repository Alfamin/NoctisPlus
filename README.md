<div align="center">

<img src="src/Noctis/Assets/Icons/Noctis.ico" width="96" />

# Noctis Plus

**[Noctis](https://github.com/heartached/Noctis) with a music finder built in.**<br/>
Search for any song, play it straight away, keep the ones you like.

<br/>

[![Release](https://img.shields.io/github/v/release/Alfamin/NoctisPlus?style=for-the-badge&color=8A5CF6&labelColor=2B2B2B&label=Latest)](https://github.com/Alfamin/NoctisPlus/releases)
&nbsp;
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=for-the-badge&logo=windows&logoColor=white&labelColor=2B2B2B)](#install)
&nbsp;
[![License](https://img.shields.io/badge/License-MIT-2EA043?style=for-the-badge&labelColor=2B2B2B)](LICENSE)
&nbsp;
[![Based on Noctis](https://img.shields.io/badge/Based%20on-Noctis%201.6.3-E74856?style=for-the-badge&labelColor=2B2B2B)](https://github.com/heartached/Noctis)

**[Install](#install)** &nbsp;·&nbsp; **[Features](#features)** &nbsp;·&nbsp; **[Full list](#everything-plus-changes)** &nbsp;·&nbsp; **[Credits](#credits)**

</div>

<br/>

Noctis is a great local music player. The one thing it can't do is get you music you don't already have. Plus adds that, along with a few things we wanted on top: recommendations, word-by-word lyrics made on your own PC, and a shuffle that actually feels random.

It installs next to the original Noctis and doesn't touch it, so you can keep both.

## Install

Paste this into PowerShell (no admin needed):

```powershell
irm https://raw.githubusercontent.com/Alfamin/NoctisPlus/main/scripts/install.ps1 | iex
```

Or download the Setup from the [latest release](https://github.com/Alfamin/NoctisPlus/releases/latest). The installer isn't code-signed, so Windows SmartScreen may warn you the first time: click **More info**, then **Run anyway**.

If you already use Noctis, Plus offers to copy your library, playlists, play counts, stats and settings on first start. After that it updates itself.

**You'll need**

- Windows 10 or 11
- A Telegram account, for finding and downloading songs (you sign in by scanning a QR code)
- For word-by-word lyrics: a 1.3 GB download, and either a graphics card or about 3 GB of free memory

## Features

### Play any song, even ones you don't have

Search for a song, album, artist or playlist and press Play. If it isn't in your library, Plus gets it through Telegram music bots (Music Hunters and DeezLoad) and starts playing while it downloads.

- Played songs stay in a cache for 30 days. Press **Add to songs** to keep one for good.
- The next 2 songs in your queue download ahead, so there's no wait between them (Off to 4).
- Artist pages show everything the artist released, not just what's on your drive. Albums list the songs you're missing under yours.
- Paste a Spotify or Deezer playlist link to import it. Every song stays in order, and the ones you don't have play too.
- Find public playlists by typing a name ("gym", "hype drake"), then save one as your own.
- MP3 by default, FLAC if you want it.
- **Offline mode** keeps you on your own music while online stuff waits.

### Search that knows who you mean

One search box for your library and the online catalog.

- "kanye" gives you Kanye West, not a song called KANYE. "plot twis" gives you Drake's Plot Twist before you've finished typing.
- Typos are fine ("post melone"), and so are missing accents ("rosalia despecha").
- Remember a lyric but not the title? Type the line and the song comes up first.
- Your own songs show up as yours, ready to play.

### For you

A page of 25 songs picked from what you listen to, on Home. It learns from what you play, keep and skip.

- About half come from your favourite artists, the rest from related artists and some new ones.
- **Not for me** drops a song. **Don't recommend artist** drops an artist, with Undo.
- Your last 10 pages are kept if you want to go back.
- Works without an account. A free Last.fm key makes the picks better.

### WordLyrics

Karaoke-style word-by-word timing for your songs, made on your own PC.

- Times the song you're playing and the next two, plus new songs as they arrive.
- A song without lyrics gets a **Time words** button.
- Eases off while you're gaming or the PC is busy, and uses the processor if your graphics card can't handle it.
- Nothing downloads until you press Set up.

### Lyric Motion

Lyrics that move with the singer: letter by letter, with long notes that grow and glow.

- Styles: smooth, crisp, flowing or off, and Subtle, Balanced or Expressive strength.
- Persian, Arabic and Hebrew read right to left.

### True Shuffle

Shuffle modes in the player, mini player and queue: **Equal chance, No repeats, Discovery, Never played** and **For you**. Shuffle the queue, a playlist or your whole library. Songs you add yourself still play first, in your order.

### Set up in a couple of clicks

A new install opens on a card with everything in one place: Find music, Add a music folder, Import playlists, Connect Telegram and Word lyrics. Every feature can be switched off in **Settings › Features**.

## Everything Plus changes

What you get on top of Noctis 1.6.3, grouped by area.

<details>
<summary><b>Finding and downloading music</b></summary>

- Play a song you don't have and it starts while it downloads
- Your last click plays first; earlier downloads wait
- Imported playlists, albums and artist pages can stream their missing songs, shuffled or not
- Played songs wait in a cache; choose how long they're kept
- Download ahead: Off to 4 of the next songs
- Albums download back to back: the next songs are asked for while one arrives
- Grab everything missing from an album or playlist with one button
- Pick which bot goes first; if one is busy, quiet or can't find a song, the other is asked
- Files the bots sent before are remembered, so they aren't asked twice
- A small icon shows what a download is doing: asking, trying the other bot, receiving, saving
- MP3 or FLAC, with an optional MP3 fallback when there's no FLAC; switching takes seconds
- Choose your download folder; downloads go into your library on their own
- Works through your VPN's system proxy, or set your own (Telegram proxy links work)
- Sign in to Telegram with a QR code; phone number and your own app id are still there
- A stuck download restarts by itself instead of holding up the queue
- A short Telegram limit is waited out instead of stopping downloads for a day
- Close the app mid-download and the queue is still there next time
- A remix, live version or radio edit never sneaks in as the original
- Two different songs with the same title never overwrite each other
- Small length differences between catalogs and censored titles still match
- Interrupted streams carry on from where they stopped
- Offline mode

</details>

<details>
<summary><b>Search</b></summary>

- One search box for your library and the online catalog, with Songs, Albums, Artists and Playlists
- The top result is the song or artist people mean, even half typed
- Famous artists come before small namesakes; "The" is optional ("killers", "beatles")
- Search by a line of lyrics
- Typos and missing accents are fine
- Public playlists in Search, ranked by how many people saved them
- Songs, playlists and Favorites each have their own search bar
- Your library results appear as you type; online ones follow a moment later
- Ctrl+Shift+F opens Search from anywhere (you can change it)
- "Find more by this artist" from any song's menu
- Your query and results are still there when you come back
- No internet? A clear notice and a Retry button; turning your VPN on reconnects by itself
- If Deezer is down, Apple Music's catalog fills in

</details>

<details>
<summary><b>Artist, album and playlist pages</b></summary>

- Artist pages show their whole discography, newest first
- Artists and albums you don't own at all open as full pages
- Missing songs sit under "Not in your library", each with Play and Download
- When several artists share a name, the best-known one opens
- Online albums open instantly and fill in as they load
- Small catalog covers swap to sharp ones
- Playlists keep repeated songs and unavailable entries, like Spotify does
- Playlist cards count every song, not just the ones you own
- Sorting and filtering include songs you don't have yet
- Just browsing never downloads anything

</details>

<details>
<summary><b>For you</b></summary>

- 25 songs a page, on Home, playable straight away
- Learns from what you play, keep, save, skip and scroll past
- Picks come from your favourite songs as well as their artists
- At most two songs per artist on a page
- Leaves out songs you've heard, including ones you have under another name
- Playing a For you song queues the rest of that page
- A quick skip isn't a no; Not for me hides a song for good
- Don't recommend artist, from cards, song menus, the player, artist pages and Search
- Not for me next to the lyrics
- Your last 10 pages are kept
- Reset For you clears what it learned
- A catalog outage keeps your picks instead of emptying the shelf

</details>

<details>
<summary><b>Lyrics</b></summary>

- Word-by-word timing made on your PC, for the song you're playing and the next two
- New songs get timed automatically: Off, Light, Medium or Full
- A Time words button on songs with no lyrics
- Time a whole folder or your whole library in one go
- Pause, resume or stop timing any time; ignore a song from its menu
- Eases off when the PC is busy and switches to Light for fullscreen apps
- Uses a third of the memory it used to on the processor; a graphics card that keeps crashing is set aside for the processor
- Your audio files are backed up before anything is written
- A quick note when a song's timing is ready
- Letter-by-letter animation with smooth, crisp and flowing styles
- Long notes grow and glow
- Persian, Arabic and Hebrew read right to left, even inside an English song
- The same motion in the side panel and the mini player
- Scroll the lyrics freely; Sync brings you back to the current line

</details>

<details>
<summary><b>Shuffle and queue</b></summary>

- Equal chance, No repeats, Discovery, Never played and For you
- Shuffle the queue, the open playlist or the whole library
- Leave out songs you disliked
- Songs you add yourself play before the shuffle, in your order
- The first shuffle after opening the app starts at once
- The queue scrolls by itself while you drag a song near its edge

</details>

<details>
<summary><b>Setup, updates and everything else</b></summary>

- Installs with one line in PowerShell, next to the original Noctis, with its own folders and ports
- First start can copy your whole Noctis profile: library, playlists, play counts, stats and sign-ins
- A first-run card with Find music, Add folder, Import playlists, Connect Telegram and Word lyrics
- New installs download MP3 by default; anyone who used the finder before keeps their choice
- Updates ask at startup (Update or Later), and each download is checked against its published checksum
- What's new shows every update you missed, biggest changes first (Settings › About)
- Every feature has its own switch in Settings › Features
- Opens about twice as fast
- Dark theme and Liquid Glass out of the box
- The player bar is frosted so it stays readable over anything
- Shuffle and 15-second skip buttons on the player
- Less text on screen, shorter tooltips
- Statistics and Wrap count streamed songs at their real length
- New strings translated into all 10 Noctis languages
- Community plugins made for Noctis 1.6.3 keep working
- Every release is built, tested (about 6,800 app tests and 1,700 download checks) and test-installed before it goes out

</details>

## Everything from Noctis

Plus is Noctis 1.6.3 underneath, so everything Noctis does is here too: bit-perfect output, EQ, gapless and crossfade, ReplayGain, smart playlists, the metadata editor, Lyrics Studio, Cover Flow, themes and Liquid Glass, the mini player, Subsonic and Jellyfin streaming, Last.fm and ListenBrainz scrobbling, Discord presence, plugins and more. The [Noctis README](https://github.com/heartached/Noctis#readme) has the full list.

Noctis also runs on macOS and Linux. Plus is Windows only for now.

## Building

See [QUICKSTART](docs/fork/QUICKSTART.md). You'll need the .NET 10 SDK.

## Credits

- **[Noctis](https://github.com/heartached/Noctis)** by [heartached](https://github.com/heartached). Plus is built on it. If you like it, [support the original](https://buymeacoffee.com/heartached).
- **[noctic-download-plugin](https://github.com/Pr0nE/noctic-download-plugin)** by [Pr0nE](https://github.com/Pr0nE), which Free Music Finder grew from.

Noctis Plus is MIT licensed, see [LICENSE](LICENSE). Where every piece comes from, and its licence, is in [PROVENANCE](docs/fork/PROVENANCE.md).
