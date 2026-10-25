"""Opt-in sonic benchmark inputs. TEST history only; never reads Telegram/settings.

python scripts/sonic/prepare.py --test-profile ... --work artifacts/sonic
Uses the Windows/system proxy through urllib. No audio is retained outside --work.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import time
import urllib.parse
import urllib.request


def request(url, timeout=15):
    req = urllib.request.Request(url, headers={"User-Agent": "Noctis-Sonic-Evaluation/1"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read(4 * 1024 * 1024 + 1)


def key(artist, title):
    artist = re.split(r",|;|\s+(?:feat\.?|ft\.?|with)\s", artist, flags=re.I)[0]
    title = re.sub(r"\s*[\[(](?:feat\.?|ft\.?|with).*?[\])]", "", title, flags=re.I)
    return re.sub(r"[^a-z0-9]", "", artist.casefold()) + ":" + re.sub(r"[^a-z0-9]", "", title.casefold())


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--test-profile", required=True, type=Path)
    p.add_argument("--work", required=True, type=Path)
    p.add_argument("--pool-file", type=Path, help="Candidate metadata exported by the offline native replay")
    args = p.parse_args()
    root, work = args.test_profile.resolve(), args.work.resolve()
    # The TEST profile in place, or a read-only copy of the main profile's few replay files kept under artifacts.
    copy = root.parent.name == "NoctisPlus-Main" and (Path(__file__).resolve().parents[2] / "artifacts") in root.parents
    if root.name != "profile" or not (root.parent.name == "NoctisPlus-Test" or copy):
        raise ValueError("Only the in-place NoctisPlus-Test/profile or an artifacts copy of the main profile is allowed")
    work.mkdir(parents=True, exist_ok=True)
    library = json.loads((root / "library.json").read_text(encoding="utf-8-sig"))
    audit = {key(s["artist"], s["title"]) for s in library if re.search("audit|scratch|testlib", s.get("filePath", ""), re.I)}
    rows = []
    for line in (root / "plugin-data/dev.moshi.trueshuffle/listens.jsonl").read_text(encoding="utf-8-sig").splitlines():
        try:
            r = json.loads(line)
        except ValueError:
            continue
        a, t = r.get("artist", ""), r.get("title", "")
        k = key(a, t)
        if k in audit or not a or not t or r.get("imported") or r.get("durationMs", 0) < 90000:
            continue
        coverage, ms = r.get("coverage", 0), r.get("listenedMs", 0)
        label = 1 if coverage >= .5 and ms >= 30000 else -1 if r.get("end") == "Skipped" and .01 <= coverage <= .15 and ms >= 5000 else 0
        if label:
            rows.append(dict(key=k, artist=a, title=t, at=r["startUtc"], label=label))
    rows.sort(key=lambda r: r["at"])
    tracks = {r["key"]: dict(key=r["key"], artist=r["artist"], title=r["title"]) for r in rows}
    # Saved candidates are metadata only. They are not labelled disliked just because never played.
    finder = root / "plugin-data/dev.moshi.freemusicfinder"
    page = json.loads((finder / "for-you-last.json").read_text(encoding="utf-8-sig"))
    for s in page.get("Result", {}).get("Songs", []):
        song = s["Track"]["Song"]
        a, t = song["Artists"][0]["Name"], song["Title"]
        tracks.setdefault(key(a, t), dict(key=key(a, t), artist=a, title=t, id=song["Id"], baseline=s["Score"]))
    if args.pool_file:
        for song in json.loads(args.pool_file.read_text(encoding="utf-8-sig")):
            if not song.get("Artists"):
                continue
            a, t = song["Artists"][0]["Name"], song["Title"]
            tracks.setdefault(key(a, t), dict(key=key(a, t), artist=a, title=t, id=song["Id"]))
    manifest = dict(version=1, source="TEST only; audit-library recordings excluded; not a population quality estimate", events=rows, tracks=list(tracks.values()))
    print(f"TEST evidence: {len(rows)} classified events, {sum(r['label'] > 0 for r in rows)} positive, {len(tracks)} recordings; audit keys excluded {len(audit)}", flush=True)
    cache = work / "previews"
    cache.mkdir(exist_ok=True)
    def fetch(t):
        path = cache / (hashlib.sha256(t["key"].encode()).hexdigest() + ".mp3")
        try:
            if path.exists() and path.stat().st_size > 1000:
                return dict(t, audio=str(path), cached=True)
            id = t.get("id", "")
            if not id.startswith("deezer:"):
                q = t["artist"] + ' ' + t["title"]
                songs = json.loads(request("https://api.deezer.com/search?q=" + urllib.parse.quote(q))) .get("data", [])
                # Never borrow a namesake or a different recording just to fill the benchmark.
                songs = [s for s in songs if key(s.get("artist", {}).get("name", ""), s.get("title", "")) == t["key"]]
                if not songs:
                    return dict(t, error="no exact catalog match")
                id = "deezer:" + str(songs[0]["id"])
            detail = json.loads(request("https://api.deezer.com/track/" + id[7:]))
            url = detail.get("preview", "")
            host = urllib.parse.urlsplit(url).hostname or ""
            if not url.startswith("https://") or not host.endswith(".dzcdn.net"):
                return dict(t, error="no approved preview")
            data = request(url)
            if not 1000 < len(data) <= 4 * 1024 * 1024:
                return dict(t, error="preview outside size budget")
            path.write_bytes(data)
            return dict(t, id=id, audio=str(path), bytes=len(data))
        except Exception as ex:
            return dict(t, error=type(ex).__name__ + ": " + str(ex)[:140])
    start = time.perf_counter()
    with concurrent.futures.ThreadPoolExecutor(max_workers=10) as pool:
        manifest["tracks"] = list(pool.map(fetch, manifest["tracks"]))
    (work / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(dict(seconds=round(time.perf_counter()-start, 2), available=sum("audio" in t for t in manifest["tracks"]), total=len(tracks), errors=[t["error"] for t in manifest["tracks"] if "error" in t][:8])), flush=True)


if __name__ == "__main__":
    main()
