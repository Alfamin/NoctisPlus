using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Automation.Peers;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Features.LyricMotion;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

public class NativeLyricMotionTests
{
    [Theory]
    [InlineData("Word by word (smooth)", "Smooth")]
    [InlineData("Word by word (crisp)", "Crisp")]
    [InlineData("Flowing", "Flowing")]
    [InlineData("Off", "Off")]
    public void ExistingModeKeysKeepTheirMeaning(string label, string expected)
    {
        var settings = new Values(); settings.Strings["animation"] = label;
        Assert.Equal(expected, Tuning.From(settings).Animation.ToString());
    }

    [Theory]
    [InlineData(0, 320)]
    [InlineData(1, 320)]
    [InlineData(450, 450)]
    [InlineData(2400, 2400)]
    public void WordLengthFollowsThePluginRule(double held, double expected)
    {
        // Lyric Motion 1.4.3 WordRig.DurationOf: a word without a held time counts as an ordinary 320 ms one.
        Assert.Equal(expected, LyricMotionScope.DurationOf(new WordTiming { HeldDurationMs = held }));
    }

    [Theory]
    [InlineData("سلام دنیا", true)]
    [InlineData("שלום עולם", true)]
    [InlineData("Hello world", false)]
    [InlineData("123 !", false)]
    public void RtlDecisionKeepsJoinedScriptsWhole(string text, bool rtl)
    {
        Assert.Equal(rtl, Script.FlowsRightToLeft(text, false));
        if (rtl) Assert.False(Script.CanSplit(text));
    }

    [Fact]
    public void SettingsClampCorruptNumbers_AndQuickWordsDoNotFlicker()
    {
        var values = new Values { Held = double.NaN };
        Assert.Equal(600, Tuning.From(values).HeldMs);
        values.Held = 1; Assert.Equal(300, Tuning.From(values).HeldMs);
        Assert.Equal(0, Tuning.From(null).Grow(120, false));
    }

    [AvaloniaFact]
    public void NativeCellUnloadsFallback_AndOffRestoresTheStockTemplate()
    {
        var values = new Values(); var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(values));
        var line = Line("Held", 6000); var root = new StackPanel(); _ = new LyricMotionScope(root);
        var builds = 0;
        var cell = new MotionWordCell
        {
            Word = line.Words![0], Line = line, Foreground = Brushes.White,
            FallbackTemplate = new FuncDataTemplate<WordTiming>((word, _) => { builds++; return new TextBlock { Text = word?.Text }; }),
        };
        root.Children.Add(cell); var window = new Window { Content = root, Width = 700, Height = 300 };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Single(cell.Children); // no hidden stock binding stack
            var nativeWidth = cell.DesiredSize.Width;
            values.Set("animation", "Off"); window.UpdateLayout();
            Assert.Equal(2, cell.Children.Count); Assert.True(builds >= 1);
            Assert.True(cell.Children[1].IsVisible);
            values.Set("animation", "Word by word (smooth)"); window.UpdateLayout();
            Assert.Single(cell.Children); Assert.Equal(nativeWidth, cell.DesiredSize.Width, 1);
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    [AvaloniaFact]
    public void NextLineStopsHeldAnimationEvenIfItsWordStillClaimsToBeCurrent_SeekDropsState()
    {
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(new Values()));
        var line = Line("Holding", 6000); line.IsActive = true;
        var cell = new MotionWordCell { Word = line.Words![0], Line = line, Foreground = Brushes.White };
        var root = new StackPanel(); _ = new LyricMotionScope(root); root.Children.Add(cell);
        var window = new Window { Content = root, Width = 700, Height = 300 };
        try
        {
            window.Show(); window.UpdateLayout();
            var word = line.Words[0]; word.IsCurrent = true; word.Progress = 0.5;
            cell.Presenter.Step(2000, 1000, 6000, true, LyricMotionFeature.Current);
            Assert.False(cell.Presenter.IsAtRest);
            var width = cell.DesiredSize.Width;
            line.IsActive = false;
            Assert.True(cell.Presenter.IsSettling);
            var now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            cell.Presenter.Settle(now + 141);
            Assert.True(cell.Presenter.IsAtRest); Assert.False(cell.Presenter.IsSettling);
            Assert.True(word.IsCurrent); Assert.Equal(0.5, word.Progress); // no timeline mutation used to hide the bug
            window.UpdateLayout(); Assert.Equal(width, cell.DesiredSize.Width);
            cell.Presenter.Step(3000, now + 150, 6000, true, LyricMotionFeature.Current);
            Assert.False(cell.Presenter.IsAtRest);
            cell.Presenter.ResetPose(); Assert.True(cell.Presenter.IsAtRest); Assert.False(cell.Presenter.IsSettling);
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    [AvaloniaFact]
    public void CachedGlyphLayoutsStayStableAcrossFrames_IncludingLongAndRtlWords()
    {
        foreach (var text in new[] { "Holding ", new string('a', 80), "فارسی" })
        {
            var presenter = new MotionWordPresenter { Word = new WordTiming { Text = text, Progress = 0.5 }, Text = text, Foreground = Brushes.White, FontSize = 46 };
            presenter.Measure(Size.Infinity); presenter.Arrange(new Rect(presenter.DesiredSize));
            for (var i = 0; i < 180; i++) presenter.Step(i * 16, i * 16, 4000, true, Tuning.From(null));
            Assert.Equal(1, presenter.LayoutBuilds);
            presenter.Release();
        }
    }

    [AvaloniaFact]
    public void NativeWordKeepsTextAccessibility_WhenItIsCustomDrawn()
    {
        var word = new MotionWordPresenter { Text = "Holding " };
        var peer = ControlAutomationPeer.CreatePeerForElement(word);
        Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
        Assert.Equal("Holding", peer.GetName());
        word.Text = "Next"; Assert.Equal("Next", peer.GetName());
    }

    [AvaloniaFact]
    public void NativeRtlWrapKeepsEnglishRunsOrdered_AndWrappedRowsAgainstTheRightEdge()
    {
        var panel = new LyricWordWrapPanel { Line = new LyricLine { Text = "سلام hello world دنیا" } };
        MotionWordCell.SetRightToLeftEnabled(panel, true); MotionWordCell.SetRightToLeftSong(panel, true);
        foreach (var text in new[] { "سلام", "hello", "world", "دنیا" })
            panel.Children.Add(new Border { Width = 40, Height = 20, DataContext = new WordTiming { Text = text } });
        panel.Measure(new Size(200, 100)); panel.Arrange(new Rect(0, 0, 200, 100));
        Assert.Equal(160, panel.Children[0].Bounds.X);
        Assert.True(panel.Children[1].Bounds.X < panel.Children[2].Bounds.X); // hello world, not world hello
        Assert.Equal(40, panel.Children[3].Bounds.X);
        panel.Measure(new Size(90, 100)); panel.Arrange(new Rect(0, 0, 90, 100));
        Assert.Equal(90, panel.Children[0].Bounds.Right);
        Assert.True(panel.Children[2].Bounds.Y > panel.Children[0].Bounds.Y);
        MotionWordCell.SetRightToLeftEnabled(panel, false); panel.Arrange(new Rect(0, 0, 90, 100));
        Assert.Equal(0, panel.Children[0].Bounds.X);
    }

    [AvaloniaFact]
    public void RealLyricsTemplatesBindTypedWordsAndLines_WithoutOverlayRigging()
    {
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(new Values()));
        using var vm = MakeLyrics();
        var line = Line("Holding", 6000); line.BackgroundWords = [new() { Text = "adlib", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(3) }];
        line.TransliterationWords = [new() { Text = "roman", Start = TimeSpan.Zero, End = TimeSpan.FromSeconds(3) }];
        vm.LyricLines.ReplaceAll([line]);
        var page = new LyricsView { DataContext = vm }; var panel = new LyricsPanelView { DataContext = vm };
        var root = new Grid { ColumnDefinitions = new("*,400") }; root.Children.Add(page); Grid.SetColumn(panel, 1); root.Children.Add(panel);
        var window = new Window { Content = root, Width = 1400, Height = 850 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var cells = root.GetVisualDescendants().OfType<MotionWordCell>().ToArray();
            Assert.NotEmpty(cells);
            Assert.All(cells, c => { Assert.Same(line, c.Line); Assert.NotNull(c.Word); Assert.Single(c.Children); });
            Assert.Contains(cells, c => ReferenceEquals(c.Word, line.Words![0]));
            Assert.Contains(cells, c => ReferenceEquals(c.Word, line.BackgroundWords![0]));
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    [AvaloniaFact]
    public void RtlPageUsesNativeAnchors_AndSidePanelSettingKeepsStockRendering()
    {
        var values = new Values(); values.Bools["sidePanel"] = false;
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(values));
        using var vm = MakeLyrics(); var line = Line("سلام دنیا", 3000); vm.LyricLines.ReplaceAll([line]);
        var page = new LyricsView { DataContext = vm }; var panel = new LyricsPanelView { DataContext = vm };
        var root = new Grid { ColumnDefinitions = new("*,400") }; root.Children.Add(page); Grid.SetColumn(panel, 1); root.Children.Add(panel);
        var window = new Window { Content = root, Width = 1400, Height = 850 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var button = page.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("lyric-line-btn"));
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, button.HorizontalAlignment);
            var column = page.FindControl<ItemsControl>("LyricsItemsControl")!;
            Assert.Equal(column.MaxWidth, column.MinWidth);
            var pageCell = Assert.Single(page.GetVisualDescendants().OfType<MotionWordCell>());
            Assert.Single(pageCell.Children);
            Assert.Equal(column.FontSize, pageCell.Presenter.FontSize);
            Assert.False(pageCell.Presenter.HasMirrorTransform); // shaping handles RTL; don't mirror Arabic glyphs
            var panelCell = Assert.Single(panel.GetVisualDescendants().OfType<MotionWordCell>());
            Assert.Equal(2, panelCell.Children.Count);
            Assert.False(MotionWordCell.GetRightToLeftEnabled(panel));
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    [AvaloniaFact]
    public void RtlLineInALeftToRightSong_StaysLeftWithItsWordsSideBySide()
    {
        // Lyric Motion 1.4.3: only a right-to-left song moves its lines to the right edge; a Persian line
        // among English ones keeps the left anchor and runs its words from the right, without overlap.
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(new Values()));
        using var vm = MakeLyrics();
        var english = Line("Holding on tight", 3000);
        var persian = new LyricLine
        {
            Text = "سلام دنیا", Timestamp = TimeSpan.FromSeconds(3), EndTimestamp = TimeSpan.FromSeconds(5),
            Words = [new() { Text = "سلام ", Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4) },
                     new() { Text = "دنیا", Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(5) }],
        };
        var more = new LyricLine { Text = "Letters move gently", Timestamp = TimeSpan.FromSeconds(6), EndTimestamp = TimeSpan.FromSeconds(8),
            Words = [new() { Text = "Letters move gently", Start = TimeSpan.FromSeconds(6), End = TimeSpan.FromSeconds(8) }] };
        vm.LyricLines.ReplaceAll([english, persian, more]);
        var page = new LyricsView { DataContext = vm };
        var window = new Window { Content = page, Width = 1400, Height = 850 };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(MotionWordCell.GetRightToLeftSong(page.FindControl<ItemsControl>("LyricsItemsControl")!));
            var buttons = page.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("lyric-line-btn")).ToArray();
            Assert.All(buttons, b => Assert.Equal(Avalonia.Layout.HorizontalAlignment.Left, b.HorizontalAlignment));
            var cells = page.GetVisualDescendants().OfType<MotionWordCell>().Where(c => c.Line == persian).ToArray();
            Assert.Equal(2, cells.Length);
            var first = cells.Single(c => c.Word == persian.Words[0]); var second = cells.Single(c => c.Word == persian.Words[1]);
            var start = buttons[0].TranslatePoint(default, window)!.Value.X;
            var a = first.TranslatePoint(default, window)!.Value.X; var b2 = second.TranslatePoint(default, window)!.Value.X;
            var info = $"line0 x={start:0.#} first x={a:0.#} w={first.Bounds.Width:0.#} second x={b2:0.#} w={second.Bounds.Width:0.#}";
            Assert.True(b2 + second.Bounds.Width <= a + 0.5, "words overlap or are in reading order left to right: " + info);
            Assert.True(b2 >= start - 0.5, "the line is pushed past the left edge: " + info);
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    [AvaloniaFact]
    public void MiniPlayerRtlWords_GetTheRtlSettingAndKeepTheFillOverTheWord()
    {
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(new Values()));
        using var lyrics = MakeLyrics();
        var english = Line("Holding on tight", 3000);
        var persian = new LyricLine
        {
            Text = "سلام دنیا", Timestamp = TimeSpan.FromSeconds(3), EndTimestamp = TimeSpan.FromSeconds(5),
            Words = [new() { Text = "سلام ", Start = TimeSpan.FromSeconds(3), End = TimeSpan.FromSeconds(4) },
                     new() { Text = "دنیا", Start = TimeSpan.FromSeconds(4), End = TimeSpan.FromSeconds(5) }],
        };
        lyrics.LyricLines.ReplaceAll([english, persian]);
        var library = new FakeLibraryService();
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoHistory());
        var mini = new MiniPlayerViewModel(lyrics.Player, lyrics, settings, library);
        var window = new MiniPlayerWindow { DataContext = mini, Width = 720, Height = 460 };
        var page = new LyricsView { DataContext = lyrics };
        var main = new Window { Content = page, Width = 1400, Height = 850 };
        try
        {
            window.Show(); mini.Form = MiniPlayerForm.Lyrics; main.Show();
            window.UpdateLayout(); main.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); main.UpdateLayout();
            string Dump(Visual root) => string.Join("\n", root.GetVisualDescendants().OfType<MotionWordCell>().Where(c => c.Line == persian).Select(c =>
            {
                var p = c.Presenter;
                return $"'{c.Word!.Text}' cell={c.Bounds} pres={p.Bounds} base={p.BaseText.Bounds} pad={p.BaseText.Padding} align={p.BaseText.TextAlignment} fd={p.BaseText.FlowDirection} sweep={p.SweepText.Bounds} sfd={p.SweepText.FlowDirection} sha={p.SweepText.HorizontalAlignment} layoutW={p.BaseText.TextLayout.WidthIncludingTrailingWhitespace:0.#} rtlEnabled={MotionWordCell.GetRightToLeftEnabled(c)} song={MotionWordCell.GetRightToLeftSong(c)} native={c.Children.Count}";
            }));
            // The mini window is its own visual root: its scope must still start (2026-10-17: it never did,
            // so its words got the left-to-right spacing and the bright fill sat 24 px off the word).
            foreach (var root in new Visual[] { window, page })
                foreach (var c in root.GetVisualDescendants().OfType<MotionWordCell>().Where(c => c.Line == persian))
                {
                    Assert.True(MotionWordCell.GetRightToLeftEnabled(c), Dump(root));
                    var p = c.Presenter;
                    Assert.True(Math.Abs(p.SweepText.Bounds.Right - (p.BaseText.Bounds.Right - p.BaseText.Padding.Right)) < 0.5, Dump(root));
                }
        }
        finally { window.Close(); main.Close(); feature.Shutdown(); }
    }

    private static LyricLine Line(string text, int duration) => new()
    {
        Text = text, Timestamp = TimeSpan.Zero, EndTimestamp = TimeSpan.FromMilliseconds(duration),
        Words = [new() { Text = text, Start = TimeSpan.Zero, End = TimeSpan.FromMilliseconds(duration) }],
    };

    [AvaloniaFact]
    public void MiniPlayerUsesTheSameNativePresenterAndTypedLineBinding()
    {
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(new Values()));
        using var lyrics = MakeLyrics(); var line = Line("Mini held", 4000); lyrics.LyricLines.ReplaceAll([line]);
        var library = new FakeLibraryService();
        var settings = new SettingsViewModel(new TestPersistenceService(), library, new NoHistory());
        var mini = new MiniPlayerViewModel(lyrics.Player, lyrics, settings, library);
        var window = new MiniPlayerWindow { DataContext = mini, Width = 900, Height = 650 };
        try
        {
            window.Show(); mini.Form = MiniPlayerForm.Lyrics;
            window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var cells = window.GetVisualDescendants().OfType<MotionWordCell>().ToArray();
            Assert.NotEmpty(cells);
            Assert.All(cells, c => { Assert.Same(line, c.Line); Assert.Single(c.Children); });
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    private sealed class NoHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => [];
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void PauseAndRapidResumeKeepTheOriginalLetterPictureAlive()
    {
        var feature = new LyricMotionFeature(); feature.Initialize(new SettingsHost(new Values()));
        using var vm = MakeLyrics(); var line = Line("Holding", 6000); line.IsActive = true;
        line.Words![0].Progress = 0.5;
        var root = new StackPanel { DataContext = vm }; _ = new LyricMotionScope(root);
        var cell = new MotionWordCell { Word = line.Words[0], Line = line, Foreground = Brushes.White };
        root.Children.Add(cell); var window = new Window { Content = root, Width = 700, Height = 300 };
        try
        {
            window.Show(); window.UpdateLayout();
            cell.Presenter.Step(3000, 1000, 6000, true, LyricMotionFeature.Current);
            Assert.False(cell.Presenter.IsAtRest);
            var builds = cell.Presenter.LayoutBuilds;
            for (var i = 0; i < 12; i++)
            {
                vm.Player.State = PlaybackState.Paused;
                Assert.False(cell.Presenter.IsAtRest);
                vm.Player.State = PlaybackState.Playing;
                cell.Presenter.Step(3000, 2000 + i * 500, 6000, true, LyricMotionFeature.Current);
                Assert.Equal(builds, cell.Presenter.LayoutBuilds);
            }
        }
        finally { window.Close(); feature.Shutdown(); }
    }

    [AvaloniaFact]
    public void NativeTextUsesTheSameTextBlockFontStyleAsStockLyrics()
    {
        var native = new MotionWordPresenter { Text = "Stock typography", FontSize = 46 };
        var stock = new TextBlock { Text = "Stock typography", FontSize = 46, FontWeight = FontWeight.Bold };
        var root = new StackPanel(); root.Children.Add(native); root.Children.Add(stock);
        // Noctis applies Inter through this TextBlock selector; a custom Control misses it.
        var font = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
        root.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<TextBlock>())
        { Setters = { new Setter(TextBlock.FontFamilyProperty, font) } });
        var window = new Window { Content = root, Width = 800, Height = 300 };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal(stock.FontFamily, native.BaseText.FontFamily);
            Assert.Equal(stock.FontFamily, native.SweepText.FontFamily);
            Assert.Equal(stock.FontWeight, native.BaseText.FontWeight);
            Assert.Equal(stock.DesiredSize, native.BaseText.DesiredSize);
        }
        finally { window.Close(); }
    }

    private static LyricsViewModel MakeLyrics()
    {
        var library = new FakeLibraryService(); var persistence = new TestPersistenceService();
        return new(new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService()),
            new NoLyrics(), new NoNetEase(), new MetadataService(), persistence, library);
    }

    private sealed class NoLyrics : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string title, double seconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string title, CancellationToken ct = default) => Task.FromResult(new List<LrcLibResult>());
    }
    private sealed class NoNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string title, double seconds, CancellationToken ct = default) => Task.FromResult<LrcLibResult?>(null);
    }
    private sealed class Values : IPluginSettings
    {
        internal Dictionary<string, string> Strings { get; } = [];
        internal Dictionary<string, bool> Bools { get; } = [];
        internal double Held = 600;
        public string? GetString(string key) => Strings.GetValueOrDefault(key);
        public bool GetBool(string key, bool fallback = false) => Bools.GetValueOrDefault(key, fallback);
        public double GetNumber(string key, double fallback = 0) => key == "heldMs" ? Held : fallback;
        public event EventHandler<string>? Changed;
        internal void Set(string key, string value) { Strings[key] = value; Changed?.Invoke(this, key); }
    }
    private sealed class SettingsHost(Values values) : IPluginHost
    {
        public IPluginSettings Settings => values;
        public string AppVersion => "1.6.3";
        public string DataDirectory => throw new NotSupportedException();
        public INowPlaying NowPlaying => throw new NotSupportedException();
        public IBeatSource Beat => throw new NotSupportedException();
        public ISpectrumSource Spectrum => throw new NotSupportedException();
        public void Log(string message) { }
        public void RegisterVisualLayer(IVisualLayerProvider provider) => throw new InvalidOperationException("Native motion must never register overlay layers.");
    }
}
