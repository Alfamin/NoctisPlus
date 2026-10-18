using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Features.WordLyrics;
using Noctis.Models;

namespace Noctis.ViewModels;

public sealed partial class WordLyricsViewModel : ObservableObject, IDisposable
{
    private readonly WordLyricsService _service;
    private readonly Track? _track;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;
    internal WordLyricsViewModel(WordLyricsService service, bool library, Track? track, Func<string, Task<bool>> confirm)
    {
        _service = service; _track = track; _confirm = confirm; IsLibrary = library; IsSong = track is not null;
        Title = track is null ? "Word lyrics" : track.Title;
        Folders = new[] { "All library folders" }.Concat(service.Folders).ToArray();
        if (service.Folders.Length == 1) FolderIndex = 1;
        _service.Changed += Refresh; Refresh();
        if (track is not null) _ = LoadWordsAsync(track.FilePath);
        IsSetUp = service.BackendReady;
    }
    /// <summary>WordLyrics is installed with its models: the footer button opens its settings instead of setup
    /// (owner, 2026-10-18: it asked to download 1.33 GB that was already there).</summary>
    public bool IsSetUp { get; }
    public string SetupLabel => IsSetUp ? "WordLyrics settings" : "Set up WordLyrics";
    public string Title { get; }
    public bool IsLibrary { get; }
    public bool IsSong { get; }
    public bool CanQueue => IsSong || IsLibrary;
    public string[] Folders { get; }
    public string[] Actions { get; } = ["Time missing words", "Retime existing words", "Fetch fresh lyrics", "Use custom lyrics"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCustom))] private int _actionIndex;
    [ObservableProperty] private int _folderIndex;
    public bool IsCustom => IsSong && ActionIndex == 3;
    [ObservableProperty] private string _words = "";
    [ObservableProperty] private string _geniusPage = "";
    [ObservableProperty] private string _message = "Ready";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _counts = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _indeterminate = true;
    [ObservableProperty] private bool _paused;
    [ObservableProperty] private bool _controllable;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInteract)), NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(FindLyricsCommand), nameof(SetupCommand))] private bool _busy;
    public bool CanInteract => !Busy && !_disposed && _service.Enabled;
    public bool CanStart => CanInteract && CanQueue;
    public string PauseLabel => Paused ? "Resume" : "Pause";
    public ObservableCollection<string> SourceLabels { get; } = [];
    public bool HasSources => SourceLabels.Count > 0;
    private List<LyricChoice> _sources = [];
    [ObservableProperty] private int _sourceIndex = -1;
    partial void OnSourceIndexChanged(int value) { if (value >= 0 && value < _sources.Count) { Words = _sources[value].Text; ActionIndex = 3; } }
    partial void OnPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));
    partial void OnActionIndexChanged(int value) => _editGeneration++;
    private int _editGeneration;
    partial void OnWordsChanged(string value) => _editGeneration++;
    private async Task LoadWordsAsync(string path)
    {
        var generation = _editGeneration;
        try
        {
            var words = await Task.Run(() => LyricDrafts.Existing(path), _stop.Token);
            if (!_disposed && Words.Length == 0 && generation == _editGeneration) Words = words;
        }
        catch (Exception ex) { if (!_disposed) Error = ex.Message; }
    }
    private void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        if (_disposed) return;
        var state = _service.State;
        Message = state.Message; Detail = state.Detail;
        IsError = state.Error; HasReport = state.Report.Length > 0;
        Counts = state.Total > 0 ? $"{state.Completed} / {state.Total} · {_service.WaitingCount} waiting" : $"{_service.WaitingCount} waiting";
        Progress = state.Fraction ?? (state.Total > 0 ? Math.Clamp((double)state.Completed / state.Total, 0, 1) : 0);
        Indeterminate = !state.Finished && !state.Paused && !state.Error && state.Fraction is null;
        Paused = state.Paused; Controllable = state.Controllable && !state.Cancelling;
        OnPropertyChanged(nameof(CanInteract));
        StartCommand.NotifyCanExecuteChanged(); FindLyricsCommand.NotifyCanExecuteChanged(); SetupCommand.NotifyCanExecuteChanged();
    }
    private async Task Act(Func<Task> action)
    {
        if (!CanInteract) return;
        Busy = true; Error = "";
        try { await action(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) Error = ex.Message; }
        finally { Busy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Start() => Act(async () =>
    {
        var paths = _track is not null ? new[] { _track.FilePath } : _service.Tracks.Where(t => !t.IsRemoteStream &&
            (FolderIndex <= 0 || FolderIndex >= Folders.Length || WordLyricsService.Inside(t.FilePath, Folders[FolderIndex])))
            .Select(t => t.FilePath).ToArray();
        if (paths.Length == 0) { Error = "No local songs in this selection."; return; }
        if (paths.Length > 20 && !await _confirm($"Queue missing word timing for {paths.Length:N0} songs? Existing word timing is kept. This can take a long time; you can pause or stop it.")) return;
        var result = await _service.QueueAsync(paths, _track is null ? TimingAction.Missing : (TimingAction)Math.Clamp(ActionIndex, 0, 3), Words);
        if (!_disposed) { Refresh(); Detail = result; }
    });
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task FindLyrics() => Act(async () =>
    {
        if (_track is null) return;
        var sources = await LyricSources.FindAsync(_track, GeniusPage, _stop.Token, _service.MetadataProxy);
        if (_disposed) return;
        SourceIndex = -1; _sources = sources; SourceLabels.Clear();
        foreach (var row in sources) SourceLabels.Add(row.Label);
        OnPropertyChanged(nameof(HasSources));
        if (sources.Count > 0) SourceIndex = 0;
        else Error = "No usable lyrics found. You can paste custom words or use a Genius song page.";
    });
    [RelayCommand] private async Task TogglePause() { await _service.PauseAsync(!Paused); Refresh(); }
    [RelayCommand] private async Task Stop() { await _service.CancelAsync(); Refresh(); }
    [RelayCommand(CanExecute = nameof(CanInteract))]
    private Task Setup()
    {
        if (IsSetUp && _service.OpenSettings is { } open) { open(); return Task.CompletedTask; }
        return Act(() => _service.SetupAsync(_confirm));
    }
    [RelayCommand] private void OpenReport() { try { _service.OpenReport(); } catch (Exception ex) { Error = ex.Message; } }
    public void Dispose() { _disposed = true; _stop.Cancel(); _service.Changed -= Refresh; _stop.Dispose(); }
}
