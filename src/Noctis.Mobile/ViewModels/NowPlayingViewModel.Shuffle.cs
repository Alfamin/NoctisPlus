using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Features.TrueShuffle;
using Noctis.Mobile.Services;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

public sealed partial class NowPlayingViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleLabel))]
    private PhoneShuffleMode _trueShuffleMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleLabel))]
    private bool _isShuffleBusy;
    internal ShuffleScope TrueShuffleScope { get; private set; } = ShuffleScope.CurrentQueue;
    private IReadOnlyList<Track> _shuffleSource = Array.Empty<Track>();
    private int _shuffleGeneration;
    private CycleStore? _shuffleStore;
    private Task? _shuffleLoad;
    private CycleData _shuffleCycle = new(1, [], null);
    private bool _shuffleHistoryWritable = true;
    private bool _shuffleQueueCleared;
    private readonly Dictionary<Guid, int> _manualShuffle = new();

    private void MarkManualShuffle(Track track)
    {
        if (PhoneShuffle.ToEngine(TrueShuffleMode) != null)
            _manualShuffle[track.Id] = _manualShuffle.GetValueOrDefault(track.Id) + 1;
    }

    private bool ConsumeManualShuffle(Track track)
    {
        if (!_manualShuffle.TryGetValue(track.Id, out var count)) return false;
        if (count == 1) _manualShuffle.Remove(track.Id);
        else _manualShuffle[track.Id] = count - 1;
        return true;
    }

    private bool WantedByShuffle(Track track) => _manualShuffle.ContainsKey(track.Id)
        || PhoneShuffle.ToEngine(TrueShuffleMode) is not { } mode
        || ShuffleEngine.StillWanted(ShuffleSong(track), mode, _shuffleCycle.Seen.ToHashSet(StringComparer.Ordinal),
            explicitAllowed: true, skipDisliked: true, DateTime.UtcNow);

    public string ShuffleLabel => IsShuffleBusy ? "Shuffling…" : ModeName(TrueShuffleMode);
    internal static string ModeName(PhoneShuffleMode mode) => mode switch
    {
        PhoneShuffleMode.Off => "Off",
        PhoneShuffleMode.NoctisShuffle => "Noctis shuffle",
        PhoneShuffleMode.EqualChance => "Equal chance",
        PhoneShuffleMode.NoRepeats => "No repeats",
        PhoneShuffleMode.Discovery => "Discovery",
        PhoneShuffleMode.NeverPlayed => "Never played",
        _ => "Off",
    };

    private void InvalidateShufflePlan()
    {
        _shuffleGeneration++;
        IsShuffleBusy = false;
    }

    private async Task LoadShuffleHistoryAsync()
    {
        _shuffleStore = new CycleStore(Path.Combine(_persistence.DataDirectory, "plugin-data", "dev.moshi.trueshuffle"));
        try { _shuffleCycle = await _shuffleStore.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Do not overwrite malformed or unreadable history with an empty replacement.
            _shuffleHistoryWritable = false;
            DebugLog.Write("Shuffle", $"History unavailable; existing file kept: {ex.GetType().Name}");
        }
    }

    private Task EnsureShuffleHistoryAsync() => _shuffleLoad ??= LoadShuffleHistoryAsync();

    private bool TryRefillTrueShuffle()
    {
        if (_shuffleQueueCleared || PhoneShuffle.ToEngine(TrueShuffleMode) is null || TrueShuffleMode == PhoneShuffleMode.NeverPlayed
            || RepeatMode != RepeatMode.All || _queue.UpNext.Count > 0) return false;
        var pool = TrueShuffleScope == ShuffleScope.WholeLibrary ? _library.Tracks.ToArray() : _shuffleSource;
        if (pool.Count == 0) return false;
        var mode = TrueShuffleMode; var scope = TrueShuffleScope; var session = _player.CurrentSessionId;
        _ = RefillAsync();
        return true;
        async Task RefillAsync()
        {
            var expected = _shuffleGeneration + 1;
            await ChooseShuffleAsync(mode, scope, pool, SourceLabel);
            if (!_disposed && _shuffleGeneration == expected && _player.CurrentSessionId == session && _queue.UpNext.Count == 0 && TrueShuffleMode == mode)
                StopPlayback();
        }
    }

    private static Song ShuffleSong(Track track)
    {
        // No per-song file opens. SAF document addresses and library IDs are stable across rescans.
        var identity = track.IsRemoteStream ? "id:" + track.Id : track.FilePath;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new(track.Id.ToString(), track.FilePath, track.Title, track.Artist, track.PlayCount,
            track.SkipWhenShuffling, track.IsExplicit, track.IsDisliked, track.SnoozedUntil,
            track.IsRemoteStream, key);
    }

    /// <summary>Native mode selection. Only the latest request may change the queue. File identity,
    /// planning and history loading never block the UI; the loaded song is kept on mode changes.</summary>
    internal async Task ChooseShuffleAsync(PhoneShuffleMode mode, ShuffleScope? scope = null,
        IReadOnlyList<Track>? newSource = null, string? sourceLabel = null)
    {
        var generation = ++_shuffleGeneration;
        var selectedScope = scope ?? TrueShuffleScope;
        if (mode is PhoneShuffleMode.Off or PhoneShuffleMode.NoctisShuffle)
        {
            IsShuffleBusy = false;
            _queue.SetShuffle(false);
            if (mode == PhoneShuffleMode.NoctisShuffle) _queue.SetShuffle(true);
            TrueShuffleMode = mode; TrueShuffleScope = selectedScope;
            _manualShuffle.Clear();
            _shuffleQueueCleared = false;
            IsShuffleEnabled = mode != PhoneShuffleMode.Off;
            QueueChanged();
            return;
        }
        IsShuffleBusy = true;
        try
        {
            await EnsureShuffleHistoryAsync();
            if (_disposed || generation != _shuffleGeneration) return;
            var pool = newSource ?? (selectedScope switch
            {
                ShuffleScope.WholeLibrary => _library.Tracks.ToArray(),
                ShuffleScope.OpenPlaylist => _shuffleSource,
                _ => _queue.UpNext.Prepend(_queue.Current).OfType<Track>().ToArray(),
            });
            var tracks = pool.ToArray();
            var manualRemaining = new Dictionary<Guid, int>(_manualShuffle);
            var manual = newSource is null ? _queue.UpNext.Where(t =>
            {
                if (!manualRemaining.TryGetValue(t.Id, out var count) || count == 0) return false;
                manualRemaining[t.Id] = count - 1;
                return true;
            }).ToArray() : [];
            var manualKeys = manual.Select(t => ShuffleSong(t).Key).ToHashSet(StringComparer.Ordinal);
            var current = newSource is null && CurrentTrack != null ? ShuffleSong(CurrentTrack).Key : null;
            var seen = _shuffleCycle.Seen.ToArray(); var last = _shuffleCycle.Last;
            var result = await Task.Run(() =>
            {
                var entries = tracks.Select(t => (Track: t, Song: ShuffleSong(t))).DistinctBy(e => e.Song.Key).ToArray();
                var byKey = entries.ToDictionary(e => e.Song.Key, e => e.Track);
                var plan = ShuffleEngine.Build(entries.Select(e => e.Song), PhoneShuffle.ToEngine(mode)!.Value,
                    seen, current, last, skipDisliked: true);
                return (Plan: plan, Tracks: manual.Concat(plan.Songs.Where(s => !manualKeys.Contains(s.Key)).Select(s => byKey[s.Key])).ToList());
            });
            if (_disposed || generation != _shuffleGeneration) return;
            if (result.Tracks.Count == 0)
            {
                Notice = mode == PhoneShuffleMode.NeverPlayed ? "No unplayed songs in this source" : "No eligible songs in this source";
                return; // no fallback to excluded songs
            }
            _shuffleCycle = _shuffleCycle with { Seen = result.Plan.Seen.ToArray() };
            _shuffleQueueCleared = false;
            if (newSource != null || CurrentTrack == null)
            {
                _manualShuffle.Clear();
                _shuffleSource = tracks;
                SourceLabel = sourceLabel ?? SourceLabel;
                var first = _queue.ReplaceAll(result.Tracks, 0)!;
                _queue.SetShuffleOrder(_queue.UpNext.ToArray());
                TrueShuffleMode = mode; TrueShuffleScope = selectedScope; IsShuffleEnabled = true;
                IsShuffleBusy = false;
                _consecutiveErrors = 0; _computerUnreachable = false;
                StartTrack(first, null);
            }
            else
            {
                _queue.SetShuffleOrder(result.Tracks);
                TrueShuffleMode = mode; TrueShuffleScope = selectedScope; IsShuffleEnabled = true;
                IsShuffleBusy = false;
                QueueChanged();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation == _shuffleGeneration) Notice = "Couldn't shuffle. Your queue was kept";
            DebugLog.Write("Shuffle", $"Planning failed: {ex.GetType().Name}");
        }
        finally { if (generation == _shuffleGeneration) IsShuffleBusy = false; }
    }

    private async Task RememberShufflePlayAsync(Track track)
    {
        try
        {
            await EnsureShuffleHistoryAsync();
            var key = ShuffleSong(track).Key;
            _shuffleCycle = _shuffleCycle with { Seen = _shuffleCycle.Seen.Append(key).Distinct().TakeLast(50000).ToArray(), Last = key };
            if (_shuffleHistoryWritable) await _shuffleStore!.SaveAsync(_shuffleCycle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DebugLog.Write("Shuffle", $"History save failed: {ex.GetType().Name}"); }
    }
}
