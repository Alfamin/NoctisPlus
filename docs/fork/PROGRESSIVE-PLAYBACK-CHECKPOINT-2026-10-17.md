# Progressive playback checkpoint — not yet exposed in the app

Order 6 is PARTIAL. Owner reprioritized reliability after friend-facing failures. There is
no streaming Play button yet and ordinary download/playback behavior is unchanged.

## Built and exercised

- `ProgressiveAudioBuffer`: disk-backed seekable transfer writer, merged byte ranges,
  contiguous prefix, readers that wait rather than returning EOF during gaps. Telegram's
  parallel chunks can arrive out of order; file Length or percentage is not valid availability.
- Independent cancellation for readers; abort/error wakes blocked reads; replacement/truncation
  invalidates old readers so backup audio cannot be stitched into the previous recording.
  Completion rejects sparse, truncated and empty transfers. Startup policy uses recording
  duration, contiguous throughput and safety headroom, rather than a fixed percentage.
- `ProgressiveMediaInput`: LibVLC callback input, unknown size and nonseekable during download.
  Native callback errors return -1; 0 is reserved for real EOF. CancelRead before VLC.Stop is
  essential: Stop can otherwise wait for a callback that is waiting for missing bytes.
- Nine focused checks pass. The real bundled LibVLC decoded PCM from generated WAV, MP3 and
  FLAC with only the first third of each file available and the producer unfinished. MP3/FLAC
  were encoded by the existing installed ffmpeg into scratch fixtures. No music download,
  real session or sound output. Log: artifacts/plus/progressive-decoder.log.

## Verified upstream contracts

- WTelegram 4.4.9 Client.Helpers.cs, DownloadFileAsync: CanSeek=true enables parallel writes
  with Seek to each chunk offset. Completion awaits all chunks and flushes. Keep transfer
  parallelism; do not pretend every byte up to FileStream.Length is downloaded.
  https://github.com/wiz0u/WTelegramClient/blob/4.4.9/src/Client.Helpers.cs
- LibVLCSharp 3.10.1 MediaInput: unknown size is ulong.MaxValue; Read returns positive bytes,
  0 for EOF and -1 for failure. Media does not own/dispose its input. Media inherits virtual
  protected Dispose(bool), so a small owned-input Media subclass can clean up without editing
  every existing Media.Dispose call.
  https://github.com/videolan/libvlcsharp/blob/3.10.1/src/LibVLCSharp/Shared/MediaInput.cs
  https://github.com/videolan/libvlcsharp/blob/3.10.1/src/LibVLCSharp/Shared/Media.cs

## Next concrete work

1. Confirm the final reliability gates first. Then add the owned-input Media wrapper and a
   native transfer/playback lease with clear producer/reader lifetime. MediaInput.Dispose must
   happen only after VLC has stopped using its callbacks. Cancel before native Stop/skip/shutdown.
2. Publish document size/extension before writes from ALL four Telegram download call sites
   (Hunter selected/cached, DeezLoad selected/cached), through a typed progress/transfer contract.
   Capture the user's preferred quality for the operation. Never play a provisional MP3 that
   the source manager still plans to discard in favor of FLAC. Wrong codec/unknown duration or
   unsafe headers must retain complete-file fallback. No transcoding masquerading as lossless.
3. Share the existing durable Downloads job and file, not another bot/session/download. Keep
   the source reset/truncation generation rule, existing final format validation and atomic
   final rename. Do not import temporary files. File handles and registry leases must survive
   rename and close when producer plus readers are finished; clean abandoned state on restart.
4. Add explicit Play for missing online songs in native Search and album rows, keeping Download
   now/Add to downloads semantics. Browsing never fetches audio. Latest Play wins; switching,
   stopping or pausing during preparation cancels pending autoplay, not the queued download.
5. Route the callback input through VlcAudioPlayer. Skip full header Parse for progressive
   inputs or it can block until the tail. Exclude those inputs from standby/gapless staging
   initially, report buffering, retain metadata duration, and guard seeks until complete.
   Use an explicit source identity; do not mislabel these as a media server. Do not restore
   session-only buffer URLs after restart. Full-download fallback must not steal playback
   after the user has changed tracks.
6. Test slow delivery, underrun recovery, cancel before Open, skip while blocked, source
   replacement, format preference, final validation failure, cleanup and restored queue.
   Run full Noctis + Finder and a real synthetic app check before marking order 6 DONE.

No unbuffered seeking/range request feature is implemented. The foundations are deliberately
unwired until these lifecycle/quality checks are in place.
