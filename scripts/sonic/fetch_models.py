"""Pinned benchmark downloads, SHA-256 verified, restartable; never installs a model in a user profile."""
import concurrent.futures
import hashlib
import json
from pathlib import Path
import sys
import urllib.request

ROOT = Path(sys.argv[1])
MODELS = [
    ("clap-music", "laion/larger_clap_music", "a0b4534a14f58e20944452dff00a22a06ce629d1", "pytorch_model.bin", 776444665, "5c289311f4a030d768af7ffbfdecd01b008aa64824211899a4e59f4f9d154fd1"),
    ("muq-mulan", "OpenMuQ/MuQ-MuLan-large", "2e01c796b71dca71b45251384c04cd7b237c9020", "pytorch_model.bin", 2653954401, "d42ae3f7cb9b66759ee0089ddc70e2f28b130c2d8ba621457358272d32dd0444"),
    ("clap-small", "Xenova/clap-htsat-unfused", "c28f2883575e590e04d3146ff0713c2448d691ba", "onnx/audio_model_quantized.onnx", 34301667, "3fcff2c8824e7bcb83a983f2a49edab3b60cbcf4872ac70efee517355173bd1f"),
    ("clap-music-speech", "Xenova/larger_clap_music_and_speech", "e9fd5ac1dbf3280936a7fc3ec8a020453ff184db", "onnx/audio_model_quantized.onnx", 78155433, "021dd4fb962b4ed20cc3a6730b09e0ccf9f9c49931047032118a3b64828513a6"),
]
selected = [a for a in sys.argv[2:] if not a.startswith("--")]
if selected:
    MODELS = [s for s in MODELS if s[0] in selected]
CHUNK = 4 * 1024 * 1024


def digest(path):
    with path.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def prepare(spec):
    name, repo, rev, file, size, expected = spec
    folder = ROOT / name
    folder.mkdir(parents=True, exist_ok=True)
    base = f"https://huggingface.co/{repo}/resolve/{rev}/"
    final = folder / Path(file).name
    if final.exists() and final.stat().st_size == size and digest(final) == expected:
        print(name + " verified cache", flush=True)
        return []
    for meta in ("config.json", "README.md") + (("preprocessor_config.json",) if name.startswith("clap") else ()):
        target = folder / meta
        if not target.exists():
            with urllib.request.urlopen(base + meta, timeout=30) as r:
                target.write_bytes(r.read(1024 * 1024))
    partial = final.with_suffix(final.suffix + ".part")
    pieces = folder / "parts"
    pieces.mkdir(exist_ok=True)
    if partial.exists():
        with partial.open("rb") as f:
            for start in range(0, min(size, partial.stat().st_size), CHUNK):
                n = min(CHUNK, size - start)
                data = f.read(n)
                if len(data) == n and not (pieces / str(start)).exists():
                    (pieces / str(start)).write_bytes(data)
    jobs = []
    for start in range(0, size, CHUNK):
        end = min(start + CHUNK, size) - 1
        piece = pieces / str(start)
        if not piece.exists() or piece.stat().st_size != end - start + 1:
            jobs.append((name, base + file, start, end, size, piece))
    print(f"{name}: {len(jobs)} ranges remaining", flush=True)
    return jobs


def chunk(job):
    name, url, start, end, size, piece = job
    for attempt in range(3):
        try:
            req = urllib.request.Request(url + f"?download=true&range_start={start}", headers={"Range": f"bytes={start}-{end}"})
            with urllib.request.urlopen(req, timeout=20) as r:
                if r.status != 206 or r.headers.get("Content-Range") != f"bytes {start}-{end}/{size}":
                    raise ValueError("server did not honor the exact range")
                data = r.read(end - start + 2)
            if len(data) != end - start + 1:
                raise ValueError("truncated range")
            piece.write_bytes(data)
            return name, len(data)
        except Exception as ex:
            if attempt == 2:
                print(f"{name} range {start} deferred: {type(ex).__name__}: {ex}", flush=True)
    return name, 0


jobs = []
for spec in MODELS:
    jobs.extend(prepare(spec))
jobs.sort(key=lambda j: ({"clap-music-speech": 0, "clap-music": 1, "muq-mulan": 2}.get(j[0], 0), j[2]))
if "--assemble-only" in sys.argv:
    jobs = []
with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
    done = 0
    for name, n in pool.map(chunk, jobs):
        done += n
        print(f"{name} progress: +{n:,} bytes (this pass {done:,})", flush=True)
for name, repo, rev, file, size, expected in MODELS:
    folder, final = ROOT / name, ROOT / name / Path(file).name
    if final.exists() and final.stat().st_size == size and digest(final) == expected:
        continue
    pieces = folder / "parts"
    if any(not (pieces / str(start)).exists() for start in range(0, size, CHUNK)):
        print(name + " INCOMPLETE; run again to resume", flush=True)
        continue
    partial = final.with_suffix(final.suffix + ".part")
    with partial.open("wb") as out:
        for start in range(0, size, CHUNK):
            out.write((pieces / str(start)).read_bytes())
    if digest(partial) != expected:
        raise ValueError(name + " SHA-256 mismatch; do not use these files")
    partial.replace(final)
    (folder / "source.json").write_text(json.dumps(dict(repo=repo, revision=rev, sha256=expected, file=file)), encoding="utf-8")
    print(f"{name} VERIFIED {size:,} bytes", flush=True)
