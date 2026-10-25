# For you V2: native sound-ranking experiment

Owner #167, 2026-10-25. **Not promoted, not released.** Ordinary For you remains the default.
The switch is experimental; model setup/distribution is not ready for a public release. Do not tag a release for this work.

## What is implemented

The existing candidate generator, recording/ownership/heard filters, source turns and diversity constraints remain.
An optional stage compares versioned, normalized audio vectors with the nearest liked recordings. It applies a bounded
relative score adjustment, keeps metadata-led exploration every fifth place, and never adds a filtered-out recording.
Missing vectors keep their original scores. Dismissal/Undo and low ratings contribute song-level negatives without an
artist ban. The service reads the same taste signals as For you, not another telemetry database.

Cold pages do not wait for audio. Cached vectors are ranked before the page is saved/shown. A bounded background pass
warms the latest candidate set, taste first, then candidates only if two usable positive taste vectors exist. It never
reorders a displayed page. The next explicit refresh uses those vectors. At most four previews are in flight and one
two-thread CPU encoder runs. Gaming/fullscreen workloads pause new work. A pass has a three-minute ceiling; ranking
has a two-second fail-open ceiling. Offline, cancellation, bad metadata/model/audio and unavailable previews preserve
the metadata page. Turning the feature off stops analysis. Model sessions unload after two idle minutes.

Signed preview URLs are fetched just before use and never persisted. Encoded audio and PCM remain in memory.
Only embeddings survive under the existing Finder data folder. Model revision, preprocessing, recording identity,
catalog ID and preview/local domain distinguish cache entries. Corrupt/oversized entries become cache misses.
The cache is bounded. Model loading checks a pinned SHA-256. Preview URLs are restricted to official HTTPS audio hosts,
with byte/time budgets and a single expired-URL refresh. Metadata/preview work uses the same proxy/offline routing
policy as Finder in a separate connection lane so it does not occupy Search's slots.

**Scope:** Search's native For you shelf and every entry point that opens that shelf share `ForYouSource`.
Saved pages retain their order. Existing playback, Telegram/download queues, lyrics, catalog browsing and
True Shuffle's own For You mode are deliberately unchanged. No reflection hooks, injected menus or visual-tree scans.

## Model measurement and provisional choice

Published sources were inspected and downloaded by immutable revision, with SHA-256 verification:

| Model | Source / licence | Download used |
|---|---|---:|
| Music CLAP | [LAION](https://huggingface.co/laion/larger_clap_music), Apache-2.0, revision a0b4534a14f58e20944452dff00a22a06ce629d1 | 776,444,665 bytes |
| MuQ-MuLan | [Tencent/OpenMuQ](https://huggingface.co/OpenMuQ/MuQ-MuLan-large), CC-BY-NC-4.0, revision 2e01c796b71dca71b45251384c04cd7b237c9020 | 2,653,954,401 bytes |
| Small generic CLAP | [Xenova conversion](https://huggingface.co/Xenova/clap-htsat-unfused), original LAION checkpoint | 34,301,667-byte INT8 audio graph |
| Larger music-and-speech CLAP | [Xenova conversion](https://huggingface.co/Xenova/larger_clap_music_and_speech), original LAION checkpoint | 78,155,433-byte INT8 audio graph |

The original **music** CLAP audio tower was exported locally (no retraining) to FP32 ONNX, then INT8 MatMul/Gemm.
Do not confuse it with the music-and-speech sibling. FP32 audio graph: 277,114,832 bytes; INT8: **72,976,022 bytes**.
INT8 SHA-256: `53887eb9e065cd45add21d4162e7bd10664333165467385b9b3f954ea5b28644`.
First real-preview cosine against original PyTorch: FP32 1.0, INT8 0.999527. The native frontend/encoder matched Python
on eight real previews at cosine >=0.99999997. Frontend equations/golden values were checked against the official
[Transformers CLAP frontend](https://github.com/huggingface/transformers/blob/v4.57.6/src/transformers/models/clap/feature_extraction_clap.py).

Ryzen 7 7800X3D, CPU measurements: original music CLAP median 637 ms/30-second preview; INT8 Python median 293 ms,
p95 302 ms over the expanded pool. Native Debug initially ~1.05 s, reduced to ~0.63 s by skipping exactly-zero triangular
mel weights (output unchanged; two inference threads). This includes decode, three windows and embedding aggregation.
Native cold first preview ~1.57 s; process working-set growth ~333 MB, not a whole-app RAM bound. MuQ took about 5 s
per three-window preview. Timings are machine/run-specific, not minimum-PC promises. DirectML was not needed for this
CLAP development choice and has not been benchmarked.

On the identical first 182 available recordings, the chronological observed-choice diagnostic gave pooled pairwise
AUC 0.657 for music CLAP INT8 versus 0.351 for MuQ; mean NDCG@10 0.531 versus 0.488. This is **noisy TEST history**,
not a claim of personal recommendation accuracy. MuQ did not earn the required clear benefit for its cost/licence,
so further heavy-model deployment work was stopped. Downloading a non-commercial model separately does not waive
its use restrictions. No MuQ weights are bundled or enabled.

## Quality gate: still open

`scripts/sonic/prepare.py` reads only the in-place TEST library/listens and public candidate metadata; known audit-library
identities are excluded. There were 135 classified events, 28 substantial plays, and 525 requested recordings after
adding the actual native engine's candidate pools. 465 previews were fetched; 464 yielded non-silent music-CLAP vectors.
Neutral/unplayed cards are not labelled disliked. Model selection must use identical coverage/masks and chronological
training before each cutoff; no future favorites/playlist marks may be treated as historical truth.

The real engine replay uses the same candidate builder, filters and final diversity selection as the shelf, entirely
offline over saved public metadata. Today's catalog is not a historical catalog, and past ownership is unknown, so the
replay explicitly omits current ownership/favorites. The first expanded replay had 851/994 candidate vector coverage,
but only five of twenty held-out positives were retrieved at all; later folds had none. Baseline hit@20 was 1/20;
blended CLAP did not improve that and lowered NDCG in that run. **This does not satisfy promotion.** The small TEST history
also contains manual app testing, so its skips are noisy preference labels. Better coverage and a representative,
untouched holdout are needed; do not tune repeatedly on these same five positives or declare synthetic tests a quality win.
The owner has since personally configured Last.fm in TEST; re-check the public candidate-cache replay after the live pass.

The first library-wide pass, model setup/download UI, model distribution endpoint, final calibration/promotion and
local-only fallback are **not finished**. Production ranking intentionally does not mix local-file vectors with preview
vectors until the domain measurement justifies it. No 2,000-file scan is automatically started and no WordLyrics model
is downloaded. The private exported model is installed in TEST only for verification, not shipped in the installer.

## Reproduce without credentials

Research environment lives in ignored `artifacts/sonic`; Python is not an app runtime dependency. Scripts operate on
explicit benchmark paths, use the system proxy, and never read Telegram files or API keys. Model/audio artifacts,
private listening inputs and vector JSON are not committed. App inference is C# + ONNX Runtime + the existing ffmpeg.

```powershell
# TEST-only public-preview preparation; add --pool-file after the engine exports pool-songs.json.
& artifacts\sonic\venv\Scripts\python.exe scripts\sonic\prepare.py --test-profile C:\Users\Alfamine\NoctisPlus-Test\profile --work artifacts\sonic
# Pin, resume and verify model weights (bounded small HTTP ranges for an unreliable proxy).
& artifacts\sonic\venv\Scripts\python.exe scripts\sonic\fetch_models.py artifacts\sonic\models clap-music
& artifacts\sonic\venv\Scripts\python.exe scripts\sonic\export_clap.py --work artifacts\sonic --ffmpeg C:\Users\Alfamine\AppData\Local\Programs\Noctis\ffmpeg.exe
& artifacts\sonic\venv\Scripts\python.exe scripts\sonic\benchmark.py --work artifacts\sonic --model clap-music-int8 --ffmpeg C:\Users\Alfamine\AppData\Local\Programs\Noctis\ffmpeg.exe
$env:FMF_SONIC_REPLAY = (Resolve-Path artifacts\sonic).Path
$env:FMF_SONIC_TEST_PROFILE = 'C:\Users\Alfamine\NoctisPlus-Test\profile'
& C:\Users\Alfamine\dotnet-sdk\dotnet.exe tests\FreeMusicFinder.Tests\bin\Debug\net10.0-windows10.0.19041.0\FreeMusicFinder.Tests.dll
# Unset opt-in harness modes before running the ordinary Finder gates.
Remove-Item Env:FMF_SONIC_REPLAY,Env:FMF_SONIC_TEST_PROFILE
```

`FMF_SONIC_ENCODER` instead of `FMF_SONIC_REPLAY` runs real native decoder/frontend/ONNX parity, with
`NOCTIS_TEST_FFMPEG` set. Do not run both modes together. Model/intersection reports and replay JSON live only under
`artifacts/sonic`. The MuQ audio-only loader loads every audio tensor strictly from the verified official checkpoint,
avoids constructing its unused text/vision stack, and never requests an automatic secondary model download.

ONNX Runtime 1.23.2 CPU [C# API](https://onnxruntime.ai/docs/get-started/with-csharp.html) is pinned. Its original MIT
licence and third-party notices are copied unchanged beside the built-in feature on publish. Model setup must show
the selected model's own licence/attribution before a public model download is offered.

## Final live checkpoint (2026-10-25)
Native TEST For you shelf and Refresh were observed in the real app with the approved model installed. The new Last.fm configuration populated public catalog cache and was preserved. Active audio warming is NOT yet proved: no sonic cache folders appeared. Investigate service readiness/positive-seed count with bounded disk-visible diagnostics; the current plugin log is memory-only. Do not call the feature done. The audit restored only its temporary fields and verified the Last.fm key unchanged. Domain check on12 TEST tracks: median local/preview cosine .99385345; same-local 128k MP3 cosine .99771655. Keep domains separate pending full distributions. Final full gates:6813 total,0failed,43skipped; Finder1645ok,0wrong. Latest staged Release test build: publish-codex-sonic-c; feature remains experimental/off and is not released.
## Claude continuation (2026-10-25 evening): live path proved, quality still not shown

**Why the earlier live check made no vectors.** The lane now writes a few status/count lines to the session log
(`[ForYou] sonic idle: …`, `sonic warm: taste … liked, … found now, … with sound; … candidates, … new vectors …`), with
no paths, keys or song names. A hidden-desktop run showed `sonic warm stopped after 180 s (time ceiling): 0 taste found,
0 new vectors, waited for a full-screen app`: on a non-interactive desktop Windows always reports "busy"
(`SHQueryUserNotificationState` = 2), so the gaming guard paused the pass for its whole budget. That is the test
setup, not a product bug; on the real desktop the guard only pauses while a full-screen app or game runs.
The TEST taste is not too thin: 463 plays (103 positive) give **10 liked recordings**, all found in the catalog.

**Live TEST check (normal desktop, nothing full-screen, Release `publish-claude-sonic-d`).** Search → For you →
Refresh; the background pass found 10 liked + 20 skip examples and wrote **148 vectors in 100.5 s** (1 song had no
preview); the next Refresh ranked with **117/296** candidate vectors and 30 taste examples, then topped up 12 more in
7.7 s. Page build stayed ~5 s; nothing waited for audio. Feature switched off again afterwards (#170 profile kept;
Last.fm key unchanged True). Harness `FMF_SONIC_LIVE` runs the same lane against the TEST taste in a scratch folder.

**Offline replay, updated caches.** The new Last.fm relations named artists whose catalog pages were never cached
(4,176 refused lookups), so `FMF_SONIC_REPLAY_CATALOG` + `FMF_SONIC_REPLAY_FILL=1` now fill a *copy* of the public
cache once (Last.fm stays cache-only; TEST profile untouched), then the replay runs fully offline (0 misses).
Previews for the larger pool were fetched (714/718) and embedded with the pinned INT8 model. Result, 5 folds:
candidates 1,296, vector coverage **1,290/1,296**, held-out positives in any pool **5/20**, hit@20 **0/20 baseline,
0/20 with sound at the app's fixed 0.20 blend** (0.10/0.35 likewise 0). No blend was tuned on these positives.

**Conclusion.** With full coverage the sound lane still shows no gain, and retrieval, not ranking, is the binding
limit: three quarters of the songs the listener went on to play never enter the candidate pool. Promotion stays
blocked; the switch stays experimental and off. Better use of effort: candidate retrieval (more of the listener's
real next plays in the pool) and a larger, untouched held-out history from real use, before revisiting embeddings.

### Across refreshes (owner's point: Refresh brings new songs, so one page is too strict)
The replay now refreshes 8 times per fold the way the shelf does (kept pages and shown-but-unused cards sink), so
up to 160 songs are seen. Coverage ~99% (1,712/1,724). Songs the listener later played that appeared somewhere in
the first 20/40/…/160 songs (of 20 held out):

| songs seen | 20 | 40 | 60 | 80 | 100 | 120 | 140 | 160 |
|---|---|---|---|---|---|---|---|---|
| normal For you | 0 | 0 | 0 | 1 | 1 | 1 | 2 | 2 |
| with sound (0.20, app default) | 0 | 0 | 0 | 1 | 3 | 3 | 3 | 4 |

Sound was equal or better on every fold, never worse. Direction is positive but the numbers are tiny (a difference of
two songs), so this is a lead, not proof. Why the rest are missed: of the held-out plays, 16 of 22 were by an artist
For you already suggested (it showed other songs of theirs), only 6 by artists it never reached. The obvious next step
is deeper per-artist candidates (more songs of each suggested artist), letting the sound lane choose among them.

### Main profile (owner's go, read-only copy, 2026-10-26)
The owner's main profile (2,138 library songs, 940 plays; 165 classified listens, 107 positive) was copied read-only
(real AppData read via a process outside the Claude package; see the blueprint) and replayed over 12 refreshes (240 songs).
Two scorings: *taste* (any later first play, ownership ignored; 51 held out, 8 days) and *discovery* (library rebuilt per
day from each song's date added; owned songs filtered as in the app; only later plays of songs not owned yet; 10 held
out, 5 days). Vector coverage ~91-95%.

| | normal: found by 100 / 240 songs | sound 0.20: by 100 / 240 | in any candidate pool |
|---|---|---|---|
| taste (51) | 3 / 4 | 3 / 4 | 5 |
| discovery (10) | 4 / 5 | 5 / 5 | 5 |

"Top picks" layout (10 sound-chosen picks above the normal 20, against normal 30): 0 extra songs found in the picks; normal
reached 5/10 discoveries by its 5th refresh, the layout by its 6th. Verdict: on real data the sound lane finds the same
songs, at best a little sooner, and never more. The ceiling is retrieval: only 5 of 51 later first plays ever enter a
pool. In the discovery scoring roughly half the misses are by artists For you already suggests, half by artists it never
reaches. Keep the lane off; work on candidate retrieval before any more sound work.
