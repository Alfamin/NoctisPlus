"""Local, repeatable audio-only model measurements. No network, settings or Telegram access.

The chronological diagnostic does NOT measure end-to-end discovery quality: its pool is observed
TEST recordings, not all retrieved candidates. The app's replay/evaluation must also pass.
"""
import argparse
import json
import math
from pathlib import Path
import subprocess
import sys
import time

import numpy as np


def pcm(ffmpeg, path, rate, start=0, seconds=30):
    p = subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-ss", str(start), "-i", path,
                        "-t", str(seconds), "-ac", "1", "-ar", str(rate), "-f", "f32le", "pipe:1"],
                       capture_output=True, timeout=20, check=True)
    return np.frombuffer(p.stdout, dtype="<f4").copy()


def features(wave):
    """CLAP rand_trunc frontend: Slaney-normalized filters, periodic Hann, centered reflect STFT.
    Kept explicit for the native frontend parity check; no random window selection.
    """
    n = 480000
    if not len(wave):
        raise ValueError("empty audio")
    if len(wave) < n:
        wave = np.pad(np.tile(wave, n // len(wave)), (0, n % len(wave)))
    wave = wave[:n].astype(np.float64)
    padded = np.pad(wave, (512, 512), mode="reflect")
    frames = np.lib.stride_tricks.sliding_window_view(padded, 1024)[::480]
    window = .5 - .5 * np.cos(2 * np.pi * np.arange(1024) / 1024)
    power = np.abs(np.fft.rfft(frames * window, axis=1)) ** 2
    def hz_to_mel(hz):
        hz = np.asarray(hz, dtype=np.float64)
        return np.where(hz >= 1000, 15 + np.log(np.maximum(hz, 1e-12) / 1000) / (np.log(6.4) / 27), hz / (200 / 3))
    def mel_to_hz(mel):
        return np.where(mel >= 15, 1000 * np.exp((mel - 15) * (np.log(6.4) / 27)), mel * (200 / 3))
    points = mel_to_hz(np.linspace(hz_to_mel(50), hz_to_mel(14000), 66))
    hz = np.linspace(0, 24000, 513)[:, None]
    bank = np.maximum(0, np.minimum((hz - points[:-2]) / (points[1:-1] - points[:-2]),
                                   (points[2:] - hz) / (points[2:] - points[1:-1])))
    bank *= 2 / (points[2:] - points[:-2])
    return (10 * np.log10(np.maximum(power @ bank, 1e-10))).astype(np.float32)[None, None]


def normalize(v):
    v = np.asarray(v, dtype=np.float32)
    norm = np.linalg.norm(v)
    if not np.isfinite(v).all() or norm < 1e-8:
        raise ValueError("invalid embedding")
    return v / norm


def diagnostic(manifest, vectors):
    events = sorted([e for e in manifest["events"] if e["key"] in vectors], key=lambda e: e["at"])
    days = sorted({e["at"][:10] for e in events})
    rows = []
    for day in days[1:]:
        past = [e for e in events if e["at"][:10] < day]
        positives = {e["key"] for e in past if e["label"] > 0}
        # Do not let a replay of a known recording make retrieval look artificially good.
        future = [e for e in events if e["at"][:10] == day and e["key"] not in positives]
        labels = {e["key"]: e["label"] for e in future}
        if len(positives) < 2 or sum(v > 0 for v in labels.values()) < 1 or sum(v < 0 for v in labels.values()) < 1:
            continue
        taste = np.stack([vectors[k] for k in positives])
        ranked = []
        for key, label in labels.items():
            sims = np.sort(taste @ vectors[key])[-3:]
            ranked.append((key, label, float(sims.mean())))
        ranked.sort(key=lambda x: -x[2])
        pos = [x for x in ranked if x[1] > 0]
        neg = [x for x in ranked if x[1] < 0]
        auc = np.mean([float(p[2] > n[2]) + .5 * float(p[2] == n[2]) for p in pos for n in neg])
        dcg = sum(1 / math.log2(i+2) for i, r in enumerate(ranked[:10]) if r[1] > 0)
        ideal = sum(1 / math.log2(i+2) for i in range(min(10, len(pos))))
        rows.append(dict(day=day, train=len(positives), candidates=len(ranked), positives=len(pos), pairs=len(pos)*len(neg), pairwise_auc=float(auc), ndcg10=dcg/ideal))
    return dict(note="Small TEST-history, observed-choice-pool diagnostic only; not a baseline-engine A/B or a user-quality claim", folds=rows,
                mean_auc=float(np.mean([r["pairwise_auc"] for r in rows])) if rows else None,
                pooled_auc=sum(r["pairwise_auc"]*r["pairs"] for r in rows) / sum(r["pairs"] for r in rows) if rows else None,
                mean_ndcg10=float(np.mean([r["ndcg10"] for r in rows])) if rows else None)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--work", type=Path, required=True)
    p.add_argument("--model", choices=["clap-small", "clap-music-speech", "clap-music", "clap-music-int8", "muq-mulan"], required=True)
    p.add_argument("--ffmpeg", required=True)
    args = p.parse_args()
    folder = args.work / "models" / ("clap-music" if args.model == "clap-music-int8" else args.model)
    source = json.loads((folder / "source.json").read_text())
    if args.model == "clap-music-int8":
        export = json.loads((folder / "export.json").read_text())
        variant = next(v for v in export["variants"] if "int8" in v["file"])
        source = dict(source, file=variant["file"], sha256=variant["sha256"], original_sha256=source["sha256"])
    manifest = json.loads((args.work / "manifest.json").read_text())
    # The download script must have verified this artifact; recheck here before loading.
    import hashlib
    path = folder / Path(source["file"]).name
    with path.open("rb") as f:
        if hashlib.file_digest(f, "sha256").hexdigest() != source["sha256"]:
            raise ValueError("unverified model")
    load_start = time.perf_counter()
    if args.model in ("clap-small", "clap-music-speech", "clap-music-int8"):
        import onnxruntime as ort
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        options.inter_op_num_threads = 1
        session = ort.InferenceSession(str(path), options, providers=["CPUExecutionProvider"])
        def infer(w):
            return session.run(None, {"input_features": features(w)})[0][0]
        rate = 48000
    elif args.model == "clap-music":
        import torch
        from transformers import ClapModel
        torch.set_num_threads(4)
        model = ClapModel.from_pretrained(str(folder), local_files_only=True).eval()
        def infer(w):
            with torch.inference_mode():
                return model.get_audio_features(input_features=torch.from_numpy(features(w))).numpy()[0]
        rate = 48000
    else:
        from muq_audio_only import load_audio_only
        infer, rate = load_audio_only(folder)
    load_ms = (time.perf_counter() - load_start) * 1000
    vector_path = args.work / (args.model + "-vectors.json")
    saved = json.loads(vector_path.read_text()) if vector_path.exists() else {}
    vectors = {k: np.array(v, dtype=np.float32) for k, v in saved.items()}
    times = []
    for i, track in enumerate(manifest["tracks"]):
        if (args.work / "pause-bench").exists():
            print(args.model + ": paused between recordings", flush=True)
            while (args.work / "pause-bench").exists():
                time.sleep(.5)
        if "audio" not in track or track["key"] in vectors:
            continue
        start = time.perf_counter()
        try:
            wave = pcm(args.ffmpeg, track["audio"], rate)
            if len(wave) < rate * 5 or np.sqrt(np.mean(wave ** 2)) < 1e-5:
                continue
            chunks = [wave[x:x + rate*10] for x in range(0, len(wave), rate*10) if len(wave[x:x+rate*10]) >= rate*5]
            vectors[track["key"]] = normalize(np.mean([normalize(infer(w)) for w in chunks], axis=0))
            times.append((time.perf_counter()-start)*1000)
            saved[track["key"]] = vectors[track["key"]].tolist()
            temp = vector_path.with_suffix(".tmp")
            temp.write_text(json.dumps(saved), encoding="utf-8")
            temp.replace(vector_path)
            print(f"{args.model}: {len(vectors)} embedded, last {times[-1]:.0f} ms", flush=True)
        except Exception as ex:
            print(f"{args.model}: input skipped: {type(ex).__name__}: {str(ex)[:200]}", flush=True)
    result = dict(model=args.model, source=source, dimensions=512, vectors=len(vectors), load_ms=load_ms,
                  median_preview_ms=float(np.median(times)) if times else None,
                  p95_preview_ms=float(np.percentile(times, 95)) if times else None, diagnostic=diagnostic(manifest, vectors))
    (args.work / (args.model + "-report.json")).write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2), flush=True)


if __name__ == "__main__":
    main()
