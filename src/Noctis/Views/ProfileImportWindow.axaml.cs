using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.Services.ProfileImport;

namespace Noctis.Views;

/// <summary>
/// Shown instead of the player on the first launch of a new Noctis Plus profile while <see cref="ProfileImporter"/>
/// copies the official Noctis profile in. Waits while Noctis is open; on a failure offers Try again or an empty start.
/// Closing it stops the copy, which resumes on the next launch.
/// </summary>
public partial class ProfileImportWindow : Window
{
    private readonly ProfileImporter? _importer;
    private readonly CancellationTokenSource _cancel = new();
    private TaskCompletionSource<bool>? _choice; // true = try again, false = start fresh
    private bool _finished;

    /// <summary>Raised once, on the UI thread, when the player may open (imported, or the user chose an empty start).</summary>
    public event EventHandler? Finished;

    /// <summary>For the XAML previewer.</summary>
    public ProfileImportWindow() => InitializeComponent();

    public ProfileImportWindow(ProfileImporter importer) : this()
    {
        _importer = importer;
        Opened += (_, _) => _ = RunAsync();
        Closing += (_, _) => { if (!_finished) _cancel.Cancel(); };
    }

    /// <summary>Polling interval while waiting for official Noctis to close; shortened by tests.</summary>
    internal TimeSpan WaitPoll { get; set; } = TimeSpan.FromSeconds(1);

    private async Task RunAsync()
    {
        var ct = _cancel.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ShowWorking();
                var progress = new Progress<ProfileImportProgress>(ShowProgress);
                var result = await _importer!.RunAsync(progress, ct);
                switch (result.Outcome)
                {
                    case ProfileImportOutcome.Completed:
                        Finish();
                        return;
                    case ProfileImportOutcome.Cancelled:
                        return;
                    case ProfileImportOutcome.SourceAppRunning:
                        ShowWaiting();
                        // Continues by itself once Noctis has closed; Start fresh ends the wait instead.
                        _choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        while (!ct.IsCancellationRequested && _importer.IsSourceAppRunning() && !_choice.Task.IsCompleted)
                            await Task.WhenAny(_choice.Task, Task.Delay(WaitPoll, ct));
                        if (_choice.Task.IsCompleted && !_choice.Task.Result) { StartFresh(); return; }
                        break;
                    case ProfileImportOutcome.Failed:
                        ShowFailed(result.Error ?? "Copying stopped.");
                        _choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        if (!await _choice.Task) { StartFresh(); return; }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* window closed: resumes next launch */ }
    }

    private void ShowWorking()
    {
        TitleText.Text = "Bringing over your Noctis library";
        MessageText.Text = "Settings, stats, playlists, history, sign-ins and plugin data. Noctis itself stays as it is.";
        ProgressPanel.IsVisible = true;
        Bar.IsIndeterminate = true;
        DetailText.Text = " ";
        Actions.IsVisible = false;
    }

    private void ShowProgress(ProfileImportProgress p)
    {
        if (p.Step == "finishing" || p.BytesTotal == 0)
        {
            Bar.IsIndeterminate = true;
            DetailText.Text = p.Step == "finishing" ? "Finishing up…" : " ";
            return;
        }
        Bar.IsIndeterminate = false;
        Bar.Value = (double)p.BytesDone / p.BytesTotal;
        DetailText.Text = $"{p.FilesDone:N0} of {p.FilesTotal:N0} files · " +
                          $"{ProfileImporter.FormatBytes(p.BytesDone)} of {ProfileImporter.FormatBytes(p.BytesTotal)}";
    }

    private void ShowWaiting()
    {
        TitleText.Text = "Close Noctis to continue";
        MessageText.Text = "Noctis is still open, maybe in the tray. Your library comes over as soon as it closes.";
        ProgressPanel.IsVisible = false;
        Actions.IsVisible = true;
        RetryButton.IsVisible = false;
    }

    private void ShowFailed(string error)
    {
        TitleText.Text = "Couldn't bring over your library";
        MessageText.Text = error;
        ProgressPanel.IsVisible = false;
        Actions.IsVisible = true;
        RetryButton.IsVisible = true;
    }

    private void StartFresh()
    {
        try { _importer!.MarkSkipped("the user chose to start fresh"); }
        catch (Exception ex) { Noctis.Services.DebugLog.Write("ProfileImport", $"could not record the skip: {ex.Message}"); }
        Finish();
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        Dispatcher.UIThread.Post(() => Finished?.Invoke(this, EventArgs.Empty));
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e) => _choice?.TrySetResult(true);

    private void OnStartFreshClick(object? sender, RoutedEventArgs e) => _choice?.TrySetResult(false);
}
