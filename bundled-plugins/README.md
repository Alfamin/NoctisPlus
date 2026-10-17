# Included modules

This folder retains historical pinned packages for provenance. Finder and Lyric Motion are
compiled into the app and excluded from packaging; True Shuffle and WordLyrics are native
too, and their former bundled packages were removed. `manifest.json`
records source repositories, commits, package hashes and every delivered file hash.
The build script verifies the files before publishing. No user settings, Telegram sessions,
Last.fm user keys, listening history, music, Python runtime or model files belong here.

Built-in modules start enabled, separately from the community-plugin switch. Their normal
settings and individual on/off switches remain available. The application owns their code:
the plugin manager cannot uninstall or overwrite it. Duplicate copies in the user plugin
folder are ignored. Built-ins are updated by a Plus release in this first version.

Native features are updated in their source folders, with their tests and notices. Historical
packages stay pinned for provenance. Do not copy build directories
or private profile files into this folder. Keep the original package's API references; the
1.6.3 host supports API 1.1 and 1.2 plugins.

Licensing is in `docs/fork/PROVENANCE.md`: all four features are MIT, with the original
authors' notices retained. Finder's original author added its MIT licence on 2026-10-17.
