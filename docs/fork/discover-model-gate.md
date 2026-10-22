# Discover: model adoption gate (Milestone D)

**Verdict: no-go.** No audio model ships for For you, and none is worth prototyping today. The deciding problem is
audio access (section 3): For you only recommends songs the listener doesn't own and hasn't heard. Noctis has no audio
for those songs, and no source of embeddings for them that is allowed to be used this way. Licences (section 1) would
block the leading candidate on their own, even if audio access were solved. The gate in section 4 says what would
reopen the question.

Rules followed: only official pages and repositories were read. No model, weights or runtime was downloaded, installed or
run, and no library audio was processed. Ranking is unchanged.

**How the sources were read.** This session's network blocks `essentia.upf.edu`, `huggingface.co`, `onnxruntime.ai`,
`last.fm` and `listenbrainz.org`. Where the same text lives in the project's own GitHub repository, I read it there and
cite both. Where it doesn't, the claim is marked **unverified here**: the official page could only be seen through a
search result, or not at all.

## 1. Candidate models

| | Essentia Discogs-EffNet (MTG) | LAION-CLAP | MERT-v1-95M (m-a-p) |
|---|---|---|---|
| Exact artifact | `discogs-effnet-bsdynamic-1.onnx` (ONNX, dynamic batch). TF siblings: `discogs-effnet-bs64-1.pb` (style classifier) and `discogs_{artist,label,release,track,multi}_embeddings-effnet-bs64-1.pb` (contrastive similarity embeddings). Not inspected: no download allowed. | `music_audioset_epoch_15_esc_90.14.pt` (the README's recommendation "for music") | `MERT-v1-95M` (Transformers checkpoint), `MERT-v1-95M_fairseq.pt` |
| Code licence | Essentia: **AGPL-3.0** for non-commercial use; commercial licence from MTG | **CC0-1.0** (repo `LICENSE`) | **Apache-2.0** (repo `LICENSE`) |
| Weights licence | **CC BY-NC-SA 4.0** ("for non-commercial use"); proprietary licence on request | **Unverified here.** A search result says CC0-1.0 for `lukewys/laion_clap`. LAION's own `laion/clap-*` copies say Apache-2.0. | **Unverified here.** Mirrors of the model card say **CC BY-NC 4.0**. |
| Training data | "an in-house dataset annotated with Discogs metadata" (MAEST: 4M tracks). Not released, so it can't be audited. | LAION-Audio-630K plus music and AudioSet. The README says "Due to copyright reasons, we cannot release the dataset". | 20K hours (model card table). Sources not checked: Hugging Face is blocked here. |
| Open or commercial redistribution | Weights: non-commercial only, ShareAlike. Can't be bundled with an MIT app for any use. At most an optional, user-started download under its own terms, if the owner accepts NC-SA. Whether ShareAlike reaches stored embeddings is an open legal question. | Possible if CC0 is confirmed. Training data rights unknown. | Weights non-commercial (if the mirrors are right) |
| Real, supported ONNX export | **Yes, first-party, documented.** The models page links the `.onnx` file and says the TF models were *ported from* ONNX. Its input is a **mel-spectrogram** (input node `serving_default_melspectrogram`: frames of 512 samples, hop 256, 96 bands, patches of 128 frames). Noctis would have to reproduce Essentia's preprocessing exactly in C#, or ship AGPL Essentia. | **No.** The repository and README mention no ONNX export. A conversion would be ours and unsupported. | **No first-party ONNX** in the repository or README |

Sources:
- Essentia models page: https://essentia.upf.edu/models.html, read as its source
  https://github.com/MTG/essentia/blob/master/doc/sphinxdoc/models.rst. Its licence line: "All the models created by the
  MTG are licensed under CC BY-NC-SA 4.0 and are also available under proprietary license upon request". The ONNX note is
  in the Discogs-EffNet section.
- Essentia licensing: https://essentia.upf.edu/licensing_information.html, read as
  https://github.com/MTG/essentia/blob/master/doc/sphinxdoc/licensing_information.rst
- Essentia code licence: https://github.com/MTG/essentia/blob/master/COPYING.txt (AGPL-3.0)
- Preprocessing: https://github.com/MTG/essentia/blob/master/src/algorithms/machinelearning/tensorflowpredicteffnetdiscogs.h
  (`_frameSize = 512`, `_hopSize = 256`, `_numberBands = 96`, `patchSize` 128). The example
  `src/examples/python/models/scripts/feature-extractors/discogs-effnet/discogs-effnet-bs64-1_embeddings.py` loads audio
  with `MonoLoader(sampleRate=16000)`.
- LAION-CLAP: https://github.com/LAION-AI/CLAP (`LICENSE` CC0-1.0, `README.md` checkpoints and dataset note)
- MERT: https://github.com/yizhilll/MERT (`LICENSE` Apache-2.0, `README.md` checkpoint links). Model card:
  https://huggingface.co/m-a-p/MERT-v1-95M (unverified here).
- Noctis Plus itself is MIT (`LICENSE` in this repository).

Only Discogs-EffNet clears the ONNX row, and its weights are non-commercial and ShareAlike. The other two have no
supported ONNX path, and their weight licences couldn't be confirmed from a primary source.

## 2. Windows inference path (if a model ever passes)

- **Runtime:** ONNX Runtime, MIT (https://github.com/microsoft/onnxruntime/blob/main/LICENSE).
  - CPU: NuGet `Microsoft.ML.OnnxRuntime`, 1.31.0 (published 2026-10-09). `win-x64/native/onnxruntime.dll` is 17.3 MB;
    the whole package is 159.6 MB because it carries every platform. Sizes are from nuget.org's catalog metadata; the
    package wasn't downloaded.
  - GPU (optional): `Microsoft.ML.OnnxRuntime.DirectML`, latest 1.24.4 (March 2026). Its docs say: "DirectML is in
    sustained engineering … new feature development has moved to WinML". DirectML supports up to ONNX opset 20.
  - Docs read as their source:
    https://github.com/microsoft/onnxruntime/blob/gh-pages/docs/install/index.md and
    https://github.com/microsoft/onnxruntime/blob/gh-pages/docs/execution-providers/DirectML-ExecutionProvider.md
    (https://onnxruntime.ai/docs/ is blocked here).
  - **Use CPU only.** A GPU path competes with the game for the GPU, which is the one resource these PCs must not share.
- **Cost per song (Discogs-EffNet; arithmetic, not measured).**
  - Mel patches: about 1 per second of audio, with hop 62 frames ≈ 1 s. A 3.5-minute song is about 210 patches of
    128×96.
  - EfficientNet-B0 is about 0.39 GFLOP at 224×224. At 128×96 that's about 0.1 GFLOP per patch, so roughly 20 GFLOP per
    song.
  - On one CPU thread that's on the order of 1–3 s of inference, plus decoding and resampling to 16 kHz mono (Noctis
    already runs ffmpeg for waveforms).
  - Memory: model and session are likely 100–200 MB while running, and nothing when unloaded.
  - Disk: the runtime DLL (17.3 MB) and the model (size not checked).
  - Feature stored per song: 1,280 floats ≈ 5 KB for the classifier's penultimate layer (dimension from the Essentia
    metadata, unverified here). About 250 MB for 50,000 songs; 8-bit quantisation would cut that to about 1.3 KB per
    song (64 MB).
  - **All of these must be measured on Windows before any gate decision.** Section 4 sets the limits.
- **Low priority, so it never hurts gaming or WordLyrics.** These patterns already exist in Noctis:
  - Run as a separate process at `ProcessPriorityClass.BelowNormal`, as WordLyrics' `Tool.cs` does for Whisper. That also
    contains a native crash.
  - Decode as `FfmpegWaveformDecoder` does: BelowNormal, one thread, reading in slices in Windows background mode.
  - ONNX Runtime session: `IntraOpNumThreads = 1`, sequential execution, and spinning off (`session.intra_op.allow_spinning
    = 0`). Without that, idle worker threads spin and use CPU. Source:
    https://github.com/microsoft/onnxruntime/blob/gh-pages/docs/performance/tune-performance/threading.md
  - Pause, unload the session and wait whenever `GamingBudget.IsActive()` (fullscreen or exclusive Direct3D), a WordLyrics
    timing run is active, or the machine is on battery. Resume only after a quiet period.
  - One song at a time; never on the UI thread; never during a For you build (the model would only score songs that
    already have a feature).

## 3. The audio-access problem (this decides the gate)

An audio model compares sounds, so every **candidate** needs audio or a compatible embedding. For you's candidates are,
by construction, songs the listener **doesn't own and hasn't heard** (C: 0 owned, 0 heard on every persona). Here is
where audio could come from:

| Source | Covers remote candidates? | Why not |
|---|---|---|
| The library | No | Owned songs are never candidates. The library could give a *taste profile*, but there would be nothing to compare it with. |
| The stream cache (`StreamCache.cs`) | No | It holds only songs the listener **played**. A played song is heard, and heard songs are removed from For you (`familiar` in `Discover.BuildAsync`). |
| 30-second previews | No (as things stand) | Finder's catalog takes no preview URLs, by design (`ArtistCatalog.cs`: "No Telegram, audio, preview URLs, credentials or library writes"). Apple's Search API allows previews "only to promote store content and not for entertainment purposes", and audio must sit "proximate to a store badge". Analysing them for ranking is neither (https://performance-partners.apple.com/search-api, unverified here: seen through a search result). Deezer's terms on previews are **unverified here**. Either way, previews would mean about 40–250 audio fetches per page (the pool before the caps), a new data flow to the providers, and decoding at build time. |
| Fetching full songs through Finder | No | Downloading songs to fill a shelf is ruled out by the brief, and it would turn every page into gigabytes of traffic. |
| Precomputed public embeddings for MusicBrainz recordings | None found | None of the candidates' publishers offers embeddings for arbitrary recordings. The one dataset of precomputed audio features I know of, AcousticBrainz, stopped taking data in 2022 and has no Discogs-EffNet-compatible embeddings (unverified here). |

**Plain answer: there's no good answer.** A model could only rank songs it has heard, and For you exists to show songs
the listener hasn't heard. That alone is a no-go for Discover, whatever the licences or the speed.

(A model on the *owned* library, such as "more like this" or ordering a queue, avoids this problem, but it isn't
Discover. It would need its own brief and its own licence decision.)

## 4. The adoption gate

A model ships only if **every** condition holds. Each is checked by one of: the C metrics (`DiscoverPageMetrics`), the
synthetic comparison (`DiscoverEvaluationTests`), `DiscoverReplay.ReportAsync` on the owner's data, or a Windows
measurement.

| # | Condition | Measured by | Pass |
|---|---|---|---|
| G1 | **Candidate coverage.** Remote candidates have a feature obtained within the provider's terms, without downloading full songs. | The replay, extended to print the share of ranked candidates with a feature | ≥ 80 % of the pool before the caps, on every saved page |
| G2 | **Licences.** Code, weights and training data all allow distribution with Noctis (MIT), or as an optional separate download the owner approves in writing. Stored features carry no restriction Noctis can't meet. | A written record in this file, from primary sources | All three named and compatible; no "unverified here" left |
| G3 | **Supported ONNX.** A first-party `.onnx`, pinned by SHA-256 and opset, that loads in `Microsoft.ML.OnnxRuntime` CPU. Noctis's C# preprocessing reproduces the reference. | A test with recorded reference outputs | Embedding cosine ≥ 0.999 against the reference on ≥ 20 clips |
| G4 | **Resources** (Windows, the owner's PC, music playing) | Measured over 100 songs | ≤ 3 CPU-seconds per song on one BelowNormal thread; ≤ 300 MB peak working set; ≤ 50 MB runtime plus model on disk; ≤ 8 KB per song stored; nothing runs while `GamingBudget.IsActive()` or a WordLyrics run is active, and work stops within 1 s when either starts; no audible glitch over a 1-hour playback test |
| G5 | **Improvement over the current engine**, judged against the deterministic engine rather than 2.2.0 | The synthetic comparison and the owner's replay | P1/P2 still hold (0 duplicate, owned, heard or dismissed cards); top-3 share falls by ≥ 10 points on the replay and on ≥ 3 of 4 personas; repeat rate, distinct artists and seed spread no worse; ranking time grows by ≤ 100 ms; dismissals move the next page at least as much (M8) |
| G6 | **Real outcomes.** Two weeks of the owner's listening on an isolated test profile, from the For you feedback file | `DiscoverFeedback.Summary` | Plays per card shown and finishes per card shown no lower than the previous two weeks without the model |
| G7 | **Optional and safe.** The shelf works, unchanged, with the model missing, corrupt, slow or crashing. | Tests in `DiscoverReliabilityTests` style | Same page as without the model; no exception; the build is no slower |

**If a model fails the gate:**
- It doesn't ship and nothing model-related is merged, not even behind a setting.
- The gate is not loosened to let it through, as C's rule S3 already says: report, don't tune.
- The gate is re-run only when the failing condition itself changes, such as a new audio or embedding source (G1), a
  licence change (G2) or a first-party export (G3). That needs a new entry in this file and the owner's approval.
- Discover stays the deterministic engine, and the famous-songs lean is attacked through retrieval instead (section 6).

**Today's result:**
- G1 fails (section 3).
- G2 fails for Discogs-EffNet (non-commercial and ShareAlike) and is unverified for the others.
- G3 passes on paper only for Discogs-EffNet, and its preprocessing is an unbuilt C# port.
- G4–G7 can't be measured without G1.
- **Verdict: no-go.**

## 5. Architecture boundaries (what today's code justifies)

**Where a model would plug in.**
- **One place:** the per-song score in `Discover.BuildAsync` (`ForYou.cs`). That's the line
  `songs.Add((song, value * kept * learning.ArtistFactor(...), key))`, after the safety filters (`hidden`, `Credits`,
  `familiar`, `learning.Rest`) and before `DiscoverIdentity.Distinct`, `unavailable` and the per-artist, per-seed and
  own-artist caps.
- A model factor would be one more bounded multiplier there, for example 0.8–1.25. It could re-order candidates but never
  bypass the filters or caps, so every C guarantee still holds.
- **Keying:**
  - features keyed by recording identity, `DiscoverIdentity.Key(song)` (title plus main artist, as "heard" and "shown"
    use), plus `model id + SHA-256 + preprocessing version`
  - a version change makes the old features invisible, so they're rebuilt, never mixed with the new ones
  - provider IDs are not keys, because they differ across Deezer and Apple
- **Optional and rebuildable:**
  - features live outside every library folder and the For you files, so deleting them changes nothing else
  - a missing, unreadable or late feature means the factor is 1.0, so the page is exactly today's

**No code was added in D.** The brief allows at most a small interface and a test that the shelf works without it. With
the gate at no-go and no audio source in sight, an interface would be unused code for a model that can't arrive. The
existing tests already pin the no-model behaviour: `DiscoverEvaluationTests` and `DiscoverReliabilityTests` cover the
page, the guarantees and the failure cases with no model at all. If G1 ever passes, the first change is the interface and
a G7 test, before any model code. There's no feature storage, vector database or pipeline.

## 6. Next: track-level retrieval

C's synthetic comparison crossed stopping rule S2: the top-3 share is still 48–62 % on three of four personas. Re-ranking
within an artist's top songs can't fix that, because the candidates *are* each artist's top songs. The lever is a
**new candidate source** that names songs rather than artists. Not built.

**Smallest option: Last.fm `track.getSimilar`.**
- **Parameters:** `artist` and `track` (or `mbid`), `limit`, `autocorrect`, `api_key`; no user authentication. Source:
  https://www.last.fm/api/show/track.getSimilar (unverified here: blocked, seen through a search result).
- **Why it's smallest:** it's the same host, API key, client, pacing (350 ms), persisted cache (≤ 15 days) and offline
  handling as today's `LastFmSimilar` (`artist.getSimilar`).
- **Seeds:** the listener's ~10 best-liked recordings: finished plays and saves weighted by taste, excluding imports, as
  `Taste.Seeds` already weighs them. Ask for `limit=30`; keep at most 3 per seed that are unowned, unheard and not
  dismissed.
- **Requests:**
  - ~10 Last.fm calls per cold build (about 3.5 s at the current pace), close to 0 when cached, so about 10 a week.
  - Last.fm's terms cap clients at about 5 requests per second per IP, averaged over 5 minutes, and require caching that
    follows the HTTP headers. Unverified here: seen in a search result quoting §4.4 of https://www.last.fm/api/tos.
  - The real cost is **matching**: each kept song must become a playable catalog song through Finder's catalog search.
    That's up to 30 searches on a cold build, through the existing catalog's slots, pacing and cache.
- **Privacy:** this sends Last.fm the **titles and artists of about 10 songs the listener likes most**, with the
  listener's own API key, so tied to their account. Today Last.fm only receives seed artist names (MusicBrainz already
  receives up to 3 titles per artist). It's a real step beyond today's data flow. It needs the owner's decision, and the
  setting text would need to say so; that would be a UI text change, so a separate approval. Not sent: play counts,
  history, files.
- **Limit:** it only helps listeners who entered a Last.fm key.
- **Alternative: ListenBrainz Labs `similar-recordings`** (https://labs.api.listenbrainz.org).
  - The query exists in `listenbrainz/labs_api/labs/api/similar_recordings/` in
    https://github.com/metabrainz/listenbrainz-server. Input: `recording_mbids` and an `algorithm`. Output: recording
    MBIDs, names and a score.
  - Same host as today's similar artists, and no API key. It sends MusicBrainz recording IDs, not tied to an account.
  - But it reads a table named `recording_dev`, discovers its algorithm names at run time (no stable contract), and needs
    recording MBIDs Noctis doesn't store. That's one more MusicBrainz lookup per seed at 1 request per second.
  - Worth a stability check before choosing it; it isn't the smallest.
- **How it would be judged:** by the same C metrics.
  - The replay can't run it offline the first time: the owner's saved cache has no track-similar data. So: recorded
    synthetic fixtures in CI, then one owner-run online build, then the offline replay.
  - Pass if top-3 share falls by ≥ 10 points with P1–P4 holding.
- **Cost estimate: about $8–14 in credits** (a client mirroring `LastFmSimilar`, merging song candidates into
  `BuildAsync` under the same filters and caps, fixtures, replay metrics, tests, two full local test runs). Uncertain;
  more if ListenBrainz is chosen.
