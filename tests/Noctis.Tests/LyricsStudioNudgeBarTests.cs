using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>The review's nudge bar on screen: its steps, tooltips with their keys, and the keys themselves.</summary>
public class LyricsStudioNudgeBarTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-nudgebar-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioNudgeBarTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("ChevronDownThinIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
    }

    private static void Settle(Window win)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();
        }
    }

    private (Window win, LyricsStudioPanel panel, LyricsStudioViewModel vm) Mount()
    {
        EnsureAppResources();
        var path = Path.Combine(_root, "song.mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[00:05.00]First line here\n[00:12.50]Second line here\n");
        var track = new Track { Title = "song", Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
        var vm = new LyricsStudioViewModel(new[] { track }, new NoRunEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), null,
            () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = false };
        var win = new Window { Width = 1200, Height = 800, Content = panel };
        win.Show();
        vm.Selected = vm.Queue[0];
        Settle(win);
        return (win, panel, vm);
    }

    private static WrapPanel Bar(LyricsStudioPanel panel) =>
        panel.GetVisualDescendants().OfType<WrapPanel>().Single(w => w.Classes.Contains("review-nudge"));

    private static void Press(Window win, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        win.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = win });

    [AvaloniaFact]
    public void TheBar_HasFourStepsWithTheirKeys_AndIsOffUntilALineIsPicked()
    {
        var (win, panel, vm) = Mount();
        try
        {
            var bar = Bar(panel);
            Assert.True(bar.IsEffectivelyVisible);
            var steps = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nudge-step")).ToList();
            Assert.Equal(new[] { "-10", "10", "-100", "100" }, steps.Select(b => (string)b.CommandParameter!));
            Assert.All(steps, b => Assert.Same(vm.NudgeSelectionCommand, b.Command));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeFineEarlier"), ToolTip.GetTip(steps[0]));
            Assert.Contains(",", (string)ToolTip.GetTip(steps[0])!);
            // Screen readers read the glyph ("−"/"+") without a name; each step says what it does.
            Assert.All(steps, b => Assert.Equal(ToolTip.GetTip(b), Avalonia.Automation.AutomationProperties.GetName(b)));
            Assert.All(steps, b => Assert.False(b.IsEffectivelyEnabled));
            Assert.Equal(Noctis.Localization.Loc.T("LyricsStudio.NudgeNoTarget"), vm.NudgeTargetText);

            vm.PlayFromLineCommand.Execute(vm.ReviewLines[1]);
            Settle(win);
            Assert.All(steps, b => Assert.True(b.IsEffectivelyEnabled));
            var row = panel.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("review-line")).ElementAt(1);
            Assert.Contains("selected", row.Classes);

            steps[3].Command!.Execute(steps[3].CommandParameter);
            Assert.Equal(TimeSpan.FromSeconds(12.6), vm.ReviewLines[1].Start);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void CommaAndPeriod_NudgeThePickedLine_ShiftForTheLargeStep_ButNotWhileTyping()
    {
        var (win, panel, vm) = Mount();
        try
        {
            Press(win, Key.OemPeriod);
            Assert.Equal(TimeSpan.FromSeconds(5), vm.ReviewLines[0].Start); // nothing picked: nothing moves

            vm.PlayFromLineCommand.Execute(vm.ReviewLines[0]);
            Press(win, Key.OemPeriod);
            Press(win, Key.OemPeriod, KeyModifiers.Shift);
            Press(win, Key.OemComma);
            Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);

            var box = panel.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("line-edit"));
            box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemComma, Source = box });
            Assert.Equal(TimeSpan.FromSeconds(5.1), vm.ReviewLines[0].Start);
        }
        finally { win.Close(); }
    }

    private sealed class NoRunEngine : ILyricsStudioEngine
    {
        public NoRunEngine(string root) => Models = StudioTestModel.Installed(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => throw new InvalidOperationException("no runs here");
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, System.Threading.CancellationToken ct) =>
            throw new InvalidOperationException("no runs here");
    }
}
