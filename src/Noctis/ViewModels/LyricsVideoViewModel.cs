using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;
using Noctis.Services.LyricsVideo;

namespace Noctis.ViewModels;

/// <summary>
/// Owner #243: the Reel maker for the song on the lyrics page — which lines, intro and outro, a live preview drawn by
/// the same renderer as the export, and the export itself (rendered on every core, encoded by ffmpeg).
/// </summary>
public sealed partial class LyricsVideoViewModel : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<LyricsVideoLine> _lines;
    private readonly string _title;
    private readonly string _artist;
    private readonly bool _explicit;
    private readonly string? _cover;
    private readonly string? _audio;
    private readonly string? _ffmpeg;
    private readonly int _bpm;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _export;
    private int _specVersion;

    public IReadOnlyList<string> LineLabels { get; }
    public string Subtitle { get; }
    public IReadOnlyList<string> LayoutLabels { get; } = ["Side by side", "Compact header", "Full page"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowReflection))] private int _layoutIndex;
    public bool ShowReflection => LayoutIndex == 0;

    [ObservableProperty] private int _fromIndex;
    [ObservableProperty] private int _toIndex;
    [ObservableProperty] private bool _intro = true;
    [ObservableProperty] private bool _outro = true;
    /// <summary>Owner #243: how strong the reflection under the page is, 0-100 (50 = the designed look).</summary>
    [ObservableProperty] private int _reflection = 50;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _savedPath;
    [ObservableProperty] private WriteableBitmap? _preview;

    public LyricsVideoViewModel(IReadOnlyList<LyricsVideoLine> lines, int startLine, string title, string artist, bool isExplicit,
        string? coverPath, string? audioPath, string? ffmpegPath, int bpm)
    {
        _lines = lines;
        _title = title;
        _artist = artist;
        _explicit = isExplicit;
        _cover = coverPath;
        _audio = audioPath;
        _ffmpeg = ffmpegPath;
        _bpm = bpm;
        Subtitle = string.IsNullOrWhiteSpace(artist) ? title : $"{title} · {artist}";
        LineLabels = lines.Select(l => $"{TimeSpan.FromSeconds(l.Start):m\\:ss}   {l.Text}").ToList();
        _fromIndex = Math.Clamp(startLine, 0, Math.Max(0, lines.Count - 1));
        // About fifteen seconds of lyrics to start with: long enough to feel like the song, short enough for a Reel.
        var to = _fromIndex;
        while (to + 1 < lines.Count && lines[to + 1].Start - lines[_fromIndex].Start < 15) to++;
        _toIndex = to;
    }

    public bool CanExport => !IsExporting && !HasProblem;

    /// <summary>Why the Reel cannot be made here, or empty.</summary>
    public string Problem => _lines.Count == 0 ? "This song has no synced lyrics."
        : _lines.Any(l => l.Text.Any(c => c is >= '֐' and <= 'ࣿ')) ? "Right-to-left lyrics can't be made into a Reel yet."
        : _audio is null ? "Wait until the song has finished downloading."
        : _ffmpeg is null ? "ffmpeg is missing: install it from Settings › Tools."
        : "";

    public bool HasProblem => Problem.Length > 0;

    public string LengthText
    {
        get
        {
            var spec = Spec();
            return spec is null ? "" : $"{TimeSpan.FromSeconds(Math.Ceiling(spec.Duration)):m\\:ss}";
        }
    }

    public string SuggestedFileName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(_artist) ? _title : $"{_artist} - {_title}";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return $"{name} (Reel).mp4";
        }
    }

    partial void OnFromIndexChanged(int value)
    {
        if (ToIndex < value) ToIndex = value;
        ClampLength(fromStart: true);
        SpecChanged();
    }

    partial void OnToIndexChanged(int value)
    {
        if (value < FromIndex) FromIndex = value;
        ClampLength(fromStart: false);
        SpecChanged();
    }

    partial void OnIntroChanged(bool value) => SpecChanged();
    partial void OnOutroChanged(bool value) => SpecChanged();
    partial void OnReflectionChanged(int value) => SpecChanged();
    partial void OnLayoutIndexChanged(int value) => SpecChanged();

    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    /// <summary>Reels run up to 90 s: a pick past it is pulled back to the last line that fits.</summary>
    private void ClampLength(bool fromStart)
    {
        while (ToIndex > FromIndex && Spec() is { Duration: > LyricsVideoSpec.MaxSeconds })
        {
            if (fromStart) ToIndex--;
            else FromIndex++;
        }
    }

    private void SpecChanged()
    {
        Interlocked.Increment(ref _specVersion);
        SavedPath = null;
        Status = "";
        OnPropertyChanged(nameof(LengthText));
    }

    internal LyricsVideoSpec? Spec()
    {
        if (_lines.Count == 0) return null;
        return new LyricsVideoSpec
        {
            Title = _title,
            Artist = _artist,
            IsExplicit = _explicit,
            CoverPath = _cover,
            Lines = _lines,
            FirstLine = Math.Clamp(FromIndex, 0, _lines.Count - 1),
            LastLine = Math.Clamp(Math.Max(FromIndex, ToIndex), 0, _lines.Count - 1),
            Intro = Intro,
            Outro = Outro,
            Reflection = Math.Clamp(Reflection, 0, 100) / 100.0 * 0.44,
            Layout = (LyricsVideoLayout)Math.Clamp(LayoutIndex, 0, LayoutLabels.Count - 1),
            Bpm = _bpm,
        };
    }

    /// <summary>
    /// The live preview: the clip on a loop, drawn by the export's own renderer on a background thread (about 20 frames
    /// a second, silent) into <see cref="Preview"/>. Restarts when the pick changes; pauses while exporting.
    /// </summary>
    public void StartPreview()
    {
        var ct = _closing.Token;
        _ = Task.Run(async () =>
        {
            LyricsVideoScene? scene = null;
            LyricsVideoRenderer? renderer = null;
            var version = -1;
            var clock = Stopwatch.StartNew();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (IsExporting) { await Task.Delay(200, ct); continue; }
                    var current = Volatile.Read(ref _specVersion);
                    if (current != version || scene is null)
                    {
                        renderer?.Dispose();
                        scene?.Dispose();
                        renderer = null;
                        scene = null;
                        var spec = Spec();
                        if (spec is null) return;
                        scene = new LyricsVideoScene(spec);
                        renderer = new LyricsVideoRenderer(scene);
                        version = current;
                        clock.Restart();
                    }
                    var started = clock.Elapsed.TotalSeconds;
                    var t = started % scene.Spec.Duration;
                    var frame = (int)(t * LyricsVideoSpec.Fps);
                    var pulse = BeatAt(scene.Spec, scene.Spec.ClipStart + t);
                    var pixels = renderer!.RenderPixels(frame, _ => pulse);
                    await Push(pixels.ToArray(), ct);
                    var wait = 50 - (clock.Elapsed.TotalSeconds - started) * 1000;
                    if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:PreviewFailed", ex.Message);
            }
            finally
            {
                renderer?.Dispose();
                scene?.Dispose();
            }
        }, ct);
    }

    private double BeatAt(LyricsVideoSpec spec, double t) =>
        Helpers.BeatPulseSource.FromBpmGrid(new Helpers.BeatContext(spec.Bpm, t * 1000, true));

    private Task Push(byte[] bgra, CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (ct.IsCancellationRequested) return;
        var bitmap = Preview;
        if (bitmap is null)
        {
            bitmap = new WriteableBitmap(new PixelSize(LyricsVideoSpec.Width, LyricsVideoSpec.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        using (var fb = bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, fb.RowBytes * fb.Size.Height));
        }
        if (Preview is null) Preview = bitmap;
        else OnPreviewFrame?.Invoke();
    }, DispatcherPriority.Background).GetTask();

    /// <summary>A new preview frame landed in <see cref="Preview"/> (the view redraws its image).</summary>
    public event Action? OnPreviewFrame;

    /// <summary>Renders and saves the Reel to <paramref name="path"/>.</summary>
    public async Task ExportAsync(string path)
    {
        if (!CanExport || Spec() is not { } spec) return;
        _export = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        IsExporting = true;
        Progress = 0;
        SavedPath = null;
        Status = "Making your Reel…";
        var progress = new Progress<double>(p => Progress = p);
        try
        {
            var failure = await LyricsVideoExporter.ExportAsync(spec, _audio!, _ffmpeg!, path, progress, _export.Token);
            if (failure is null)
            {
                SavedPath = path;
                Status = "Saved";
            }
            else Status = failure == "Cancelled" ? "" : $"Couldn't make the Reel: {failure}";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't make the Reel: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            _export.Dispose();
            _export = null;
        }
    }

    [RelayCommand]
    private void CancelExport() => _export?.Cancel();

    [RelayCommand]
    private void ShowInFolder()
    {
        if (SavedPath is not { } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { DebugLogger.Warn(DebugLogger.Category.Lyrics, "LyricsVideo:ShowInFolder", ex.Message); }
    }

    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }
}
