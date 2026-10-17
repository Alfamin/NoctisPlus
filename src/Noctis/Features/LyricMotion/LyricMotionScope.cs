using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Features.LyricMotion;

/// <summary>One native controller per lyrics surface, riding the VM's existing word frame clock.</summary>
internal sealed class LyricMotionScope
{
    private readonly Control _root;
    private readonly bool _small;
    private readonly Func<object?, LyricsViewModel?> _select;
    private readonly Dictionary<LyricLine, Group> _groups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MotionWordCell, Group> _members = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Group> _moving = [];
    private readonly List<Group> _retired = [];
    private LyricsViewModel? _vm;
    private bool _attached;
    private sealed class Group(LyricLine line)
    {
        internal LyricLine Line { get; } = line;
        internal List<MotionWordCell> Cells { get; } = [];
    }
    private static double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    internal LyricMotionScope(Control root, bool small = false, Func<object?, LyricsViewModel?>? select = null)
    {
        _root = root; _small = small; _select = select ?? (data => data as LyricsViewModel);
        root.SetValue(MotionWordCell.ScopeProperty, this);
        root.AttachedToVisualTree += (_, _) => Attach();
        root.DetachedFromVisualTree += (_, _) => Detach();
        root.DataContextChanged += (_, _) => { if (_attached) Hook(); };
        // A window is its own visual root: it never gets an attach event after it is created, so
        // the mini player's scope starts at once and ends when the window closes.
        if (root is TopLevel top)
        {
            Attach();
            if (top is Window window) window.Closed += (_, _) => Detach();
        }
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        LyricMotionFeature.Changed += OnSettingsChanged;
        Hook();
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        LyricMotionFeature.Changed -= OnSettingsChanged;
        Unhook();
    }

    private void Hook()
    {
        var vm = _select(_root.DataContext);
        if (ReferenceEquals(vm, _vm)) return;
        Unhook();
        _vm = vm;
        if (vm is not null)
        {
            vm.NativeLyricFrame += OnFrame;
            vm.NativeMotionReset += Reset;
            vm.NativeMotionFreeze += Freeze;
            vm.PropertyChanged += OnLyricsChanged;
            vm.LyricLines.CollectionChanged += OnLinesChanged;
        }
        OnSettingsChanged();
    }

    private void Unhook()
    {
        if (_vm is not null)
        {
            _vm.NativeLyricFrame -= OnFrame;
            _vm.NativeMotionReset -= Reset;
            _vm.NativeMotionFreeze -= Freeze;
            _vm.PropertyChanged -= OnLyricsChanged;
            _vm.LyricLines.CollectionChanged -= OnLinesChanged;
        }
        _vm = null;
        Reset();
    }

    internal void Register(MotionWordCell cell)
    {
        if (cell.Line is not { } line || cell.Word is null) return;
        if (!_groups.TryGetValue(line, out var group))
        {
            _groups.Add(line, group = new(line));
            line.PropertyChanged += OnLineChanged;
        }
        group.Cells.Add(cell);
        SetNeighbors(cell, line);
        _members[cell] = group;
        if (line.IsActive) _moving.Add(group);
        ApplyMode(cell);
    }

    internal void Unregister(MotionWordCell cell)
    {
        if (!_members.Remove(cell, out var group)) return;
        group.Cells.Remove(cell);
        if (group.Cells.Count == 0)
        {
            group.Line.PropertyChanged -= OnLineChanged;
            _groups.Remove(group.Line);
            _moving.Remove(group);
        }
    }

    private void ApplyMode(MotionWordCell cell)
    {
        var settings = LyricMotionFeature.Current;
        cell.SetMode(LyricMotionFeature.IsEnabled && settings.Motion && (!_small || settings.SidePanel),
            LyricMotionFeature.IsEnabled && settings.RightToLeft);
    }

    private void OnSettingsChanged()
    {
        UpdateLayoutContext();
        Reset();
        foreach (var cell in _members.Keys) ApplyMode(cell);
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLyricLines)) UpdateLayoutContext();
    }
    private void OnLinesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateLayoutContext();

    private void UpdateLayoutContext()
    {
        var settings = LyricMotionFeature.Current;
        var enabled = LyricMotionFeature.IsEnabled && settings.Motion && settings.RightToLeft && (!_small || settings.SidePanel);
        int rtl = 0, ltr = 0;
        if (enabled && _vm is not null)
            foreach (var line in _vm.LyricLines) Script.Count(line.Text, ref rtl, ref ltr);
        MotionWordCell.SetRightToLeftEnabled(_root, enabled);
        MotionWordCell.SetRightToLeftSong(_root, enabled && rtl > ltr);
    }

    private void OnLineChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LyricLine.IsActive) || sender is not LyricLine line || !_groups.TryGetValue(line, out var group)) return;
        if (line.IsActive)
        {
            _moving.Add(group);
            foreach (var cell in group.Cells) cell.Presenter.SetLineActive(true);
        }
        else
        {
            foreach (var cell in group.Cells) cell.Presenter.BeginHandover(NowMs);
        }
    }

    private void Reset()
    {
        _moving.Clear();
        foreach (var group in _groups.Values)
        {
            foreach (var cell in group.Cells)
            {
                cell.Presenter.ResetPose();
                cell.Presenter.SetLineActive(group.Line.IsActive);
            }
            if (group.Line.IsActive) _moving.Add(group);
        }
    }

    private void Freeze()
    {
        foreach (var cell in _members.Keys) cell.Presenter.Freeze();
    }

    private void OnFrame(TimeSpan position)
    {
        var settings = LyricMotionFeature.Current;
        if (!_root.IsEffectivelyVisible || !LyricMotionFeature.IsEnabled || !settings.Motion || (_small && !settings.SidePanel)) return;
        var started = MotionDiagnostics.Enabled ? Stopwatch.GetTimestamp() : 0;
        var allocated = MotionDiagnostics.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var now = NowMs;
        _retired.Clear();
        foreach (var group in _moving)
        {
            var active = group.Line.IsActive;
            var settling = false;
            foreach (var cell in group.Cells)
            {
                if (!cell.IsEffectivelyVisible) { cell.Presenter.ResetPose(); continue; }
                if (active && cell.Word is { } word)
                {
                    // The plugin's rules: a word's length is its held time (320 ms when it has none),
                    // and its neighbours shape its growth only in the flowing mode.
                    double? before = settings.Flow && cell.BeforeWord is { } b ? settings.Grow(DurationOf(b), false) : null;
                    double? after = settings.Flow && cell.AfterWord is { } a ? settings.Grow(DurationOf(a), cell.AfterIsLast) : null;
                    cell.Presenter.Step(position.TotalMilliseconds, now, DurationOf(word), cell.IsLastWord, settings, before, after, cell.IsBackground);
                }
                else cell.Presenter.Settle(now);
                settling |= cell.Presenter.IsSettling;
            }
            if (!active && !settling) _retired.Add(group);
        }
        foreach (var group in _retired) _moving.Remove(group);
        if (!_rehearsed && _moving.Count == 0) Rehearse(settings);
        if (MotionDiagnostics.Enabled) MotionDiagnostics.Frame(started, allocated);
    }

    internal static double DurationOf(WordTiming word) => word.HeldDurationMs > 1 ? word.HeldDurationMs : 320;

    private bool _rehearsed;
    // While nothing is sung (an intro, the page just opened): warm the redraw on one waiting word.
    private void Rehearse(Tuning settings)
    {
        foreach (var cell in _members.Keys)
        {
            if (cell.Line?.IsActive == true || !cell.IsEffectivelyVisible || cell.Presenter.Bounds.Width <= 0) continue;
            _rehearsed = true;
            cell.Presenter.Rehearse(settings);
            return;
        }
    }

    private static void SetNeighbors(MotionWordCell cell, LyricLine line)
    {
        cell.BeforeWord = cell.AfterWord = null; cell.IsLastWord = false; cell.IsBackground = false;
        foreach (var layer in new[] { line.Words, line.BackgroundWords, line.TransliterationWords })
        {
            if (layer is null) continue;
            cell.IsBackground = !ReferenceEquals(layer, line.Words);
            for (var i = 0; i < layer.Count; i++)
            {
                if (!ReferenceEquals(layer[i], cell.Word)) continue;
                cell.BeforeWord = i > 0 ? layer[i - 1] : null;
                cell.AfterWord = i + 1 < layer.Count ? layer[i + 1] : null;
                cell.IsLastWord = i == layer.Count - 1;
                cell.AfterIsLast = i == layer.Count - 2;
                return;
            }
        }
    }
}
