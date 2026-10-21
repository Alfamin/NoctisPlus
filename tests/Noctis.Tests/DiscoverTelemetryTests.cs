using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using FreeMusicFinder;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// For you's learning data (Milestone B): cards actually in view, what became of played and saved recommendations, playback
/// evidence that must not read as taste (failed starts, empty finishes, duplicate events), and persistence through corrupt
/// files, Reset and reloads. Synthetic data only; no network.
/// </summary>
public class DiscoverTelemetryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "noctis-discover-b-" + Guid.NewGuid().ToString("N"));
    public DiscoverTelemetryTests() => Directory.CreateDirectory(_folder);
    public void Dispose() { try { Directory.Delete(_folder, true); } catch (IOException) { } }

    private string FeedbackPath => Path.Combine(_folder, "for-you-feedback.json");
    private static DiscoverShown Card(string title, int position, string artist = "Act") =>
        new(DiscoverIdentity.Key(title, artist), "deezer:" + title, DiscoverIdentity.Artist(artist), "Seed", false, position);
    private static void Act(DiscoverFeedback f, string batch, string title, DiscoverAction action, string artist = "Act") =>
        f.Record(batch, DiscoverIdentity.Key(title, artist), DiscoverIdentity.Artist(artist), title, action);

    [Fact]
    public void OnlyCardsInView_CountAsPassedOver()
    {
        var feedback = new DiscoverFeedback(FeedbackPath, () => Now);
        for (int page = 0; page < 3; page++)
        {
            feedback.Shown("p" + page, [Card("Top", 0), Card("Below Fold", 5)]);
            Assert.True(feedback.Seen("p" + page, [0]));
            Assert.False(feedback.Seen("p" + page, [0]), "nothing new in view: nothing written");
        }
        feedback.Shown("old", [Card("Legacy", 3)]); // a page with no view report keeps the old front-of-shelf rule
        var learning = feedback.Learn();
        Assert.Equal(0, learning.Rest(Card("Top", 0).Key, Now));
        Assert.Equal(1, learning.Rest(Card("Below Fold", 5).Key, Now));
        Assert.Equal(0.8, learning.Rest(Card("Legacy", 3).Key, Now));
        Assert.False(feedback.Seen("never-shown", [0]), "a page that was never on screen cannot be seen");
        Assert.True(feedback.Seen("p0", [5, 99, -1]));
        Assert.Equal(new[] { 0, 5 }, feedback.Batches.First(b => b.Id == "p0").Seen!.Order());
    }

    [Fact]
    public void Outcomes_ComeFromWhatTheListenerDidAfterwards()
    {
        var feedback = new DiscoverFeedback(FeedbackPath, () => Now);
        var at = Now.UtcDateTime;
        Act(feedback, "p", "Loved", DiscoverAction.Play);
        Act(feedback, "p", "Bounced", DiscoverAction.Play);
        Act(feedback, "p", "Broken Stream", DiscoverAction.Play);
        Act(feedback, "p", "Heard Before", DiscoverAction.Play);
        Act(feedback, "p", "Saved", DiscoverAction.Download, "Other");
        Act(feedback, "p", "Saved And Starred", DiscoverAction.Queue, "Other");
        Act(feedback, "p", "Imported Only", DiscoverAction.Download, "Other");
        var plays = new List<TastePlay>
        {
            new("Act", at.AddMinutes(1), 1.5, "Loved", true),
            new("Act", at.AddMinutes(5), -1.5, "Bounced", true),
            new("Act", at.AddDays(-30), 1.5, "Heard Before", true), // before the card: not its outcome
        };
        var starred = new LocalSong(Guid.NewGuid(), "Other", "Saved And Starred", "C:/m/a.flac", TimeSpan.FromMinutes(3), Favorite: true);
        var imported = new LocalSong(Guid.NewGuid(), "Other", "Imported Only", "C:/m/b.flac", TimeSpan.FromMinutes(3));
        var learning = feedback.Learn(plays, [starred, imported], [new TastePlaylist(true, [imported.Id])]);
        DiscoverOutcome Of(string title, string artist = "Act") => learning.Outcomes.GetValueOrDefault(DiscoverIdentity.Key(title, artist));
        Assert.Equal(DiscoverOutcome.Enjoyed, Of("Loved"));
        Assert.Equal(DiscoverOutcome.Skipped, Of("Bounced"));
        Assert.Equal(DiscoverOutcome.None, Of("Broken Stream"));
        Assert.Equal(DiscoverOutcome.None, Of("Heard Before"));
        Assert.Equal(DiscoverOutcome.Saved, Of("Saved", "Other"));
        Assert.Equal(DiscoverOutcome.Kept, Of("Saved And Starred", "Other"));
        Assert.Equal(DiscoverOutcome.Saved, Of("Imported Only", "Other"));
        // One good and one early skip of "Act": the skip is that song's business; the artist is not lowered.
        Assert.True(learning.ArtistFactor(DiscoverIdentity.Artist("Act")) >= 1);
        Assert.True(learning.ArtistFactor(DiscoverIdentity.Artist("Other")) > 1, "a new artist the listener saved and starred is worth more of");
        Assert.Contains("played 4", feedback.Summary(learning));
    }

    [Fact]
    public void RepeatedEarlySkips_LowerAnArtistGently_AndTwoGoodDaysAreKept()
    {
        var feedback = new DiscoverFeedback(FeedbackPath, () => Now);
        var plays = new List<TastePlay>();
        foreach (var t in new[] { "One", "Two", "Three" })
        {
            Act(feedback, "p", t, DiscoverAction.Play, "Misfit");
            plays.Add(new("Misfit", Now.UtcDateTime.AddMinutes(1), -1.5, t, true));
        }
        Act(feedback, "p", "Grower", DiscoverAction.Play, "Fit");
        plays.Add(new("Fit", Now.UtcDateTime.AddHours(1), 1.5, "Grower", true));
        plays.Add(new("Fit", Now.UtcDateTime.AddDays(2), 1.0, "Grower", true));
        var learning = feedback.Learn(plays, []);
        Assert.InRange(learning.ArtistFactor(DiscoverIdentity.Artist("Misfit")), 0.4, 0.99);
        Assert.Equal(DiscoverOutcome.Kept, learning.Outcomes[DiscoverIdentity.Key("Grower", "Fit")]);
        Assert.Equal(1, feedback.Learn().ArtistFactor(DiscoverIdentity.Artist("Misfit")));
    }

    [Fact]
    public void PlaybackFailures_EmptyFinishes_AndDuplicateEvents_AreNotTaste()
    {
        var shuffle = Path.Combine(_folder, "plugin-data", "dev.moshi.trueshuffle"); Directory.CreateDirectory(shuffle);
        string Line(string title, string end, double coverage, int ms, string at = "2026-10-15T10:00:00Z") =>
            $"{{\"trackId\":\"{title}\",\"title\":\"{title}\",\"artist\":\"A\",\"startUtc\":\"{at}\",\"end\":\"{end}\",\"coverage\":{coverage},\"listenedMs\":{ms}}}";
        File.WriteAllLines(Path.Combine(shuffle, "listens.jsonl"), [
            Line("Failed Start", "Skipped", 0, 800),
            Line("Unknown Length", "Finished", 0, 1200, "2026-10-15T10:01:00Z"),
            Line("Real", "Finished", 1, 200000, "2026-10-15T10:05:00Z"),
            Line("Real", "Finished", 1, 200000, "2026-10-15T10:05:01Z"),
        ]);
        var plays = TasteSources.Load(_folder);
        Assert.DoesNotContain(plays, p => p.Title == "Failed Start");
        Assert.DoesNotContain(plays, p => p.Title == "Unknown Length");
        Assert.Single(plays, p => p.Title == "Real");
        Assert.DoesNotContain(DiscoverIdentity.Key("Unknown Length", "A"), TasteSources.Familiar(plays));
    }

    [Fact]
    public async Task Persistence_SurvivesCorruption_ResetAndReload()
    {
        File.WriteAllText(FeedbackPath, "{\"Version\":1,\"Batches\":[{\"Id\":\"x\",");
        var feedback = new DiscoverFeedback(FeedbackPath, () => Now);
        Assert.Empty(feedback.Batches);
        feedback.Shown("before", [Card("A", 0)]);
        feedback.Reset();
        feedback.Shown("after", [Card("B", 0)]);
        feedback.Seen("after", [0]);
        Act(feedback, "after", "B", DiscoverAction.Dismiss);
        await feedback.Flushed;
        var reloaded = new DiscoverFeedback(FeedbackPath, () => Now);
        Assert.Equal(new[] { "after" }, reloaded.Batches.Select(b => b.Id));
        Assert.Equal(new[] { 0 }, reloaded.Batches[0].Seen);
        Assert.Single(reloaded.Interactions);
        reloaded.Retract("after", Card("B", 0).Key, DiscoverAction.Dismiss);
        await reloaded.Flushed;
        Assert.Empty(new DiscoverFeedback(FeedbackPath, () => Now).Interactions);
        File.WriteAllText(FeedbackPath, "{\"Version\":9,\"Batches\":[],\"Interactions\":[]}");
        Assert.Empty(new DiscoverFeedback(FeedbackPath, () => Now).Batches);
    }

    [Fact]
    public void SavedPagesFrom220_StillLoad_WithoutCountingAsShown()
    {
        var path = Path.Combine(_folder, "for-you-last.json");
        var song = new DiscoverSong(new(new CatalogSong("deezer:1", "Song", [new("deezer:9", "Act")], 180, true), null), 1, "Because you play X");
        var cache = new ForYouCache(path);
        cache.Write(new(1, Now, Now.AddDays(1), new([song], [], "")));
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Result"]!.AsObject().Remove("Batch");
        foreach (var s in json["Result"]!["Songs"]!.AsArray()) { s!.AsObject().Remove("Seed"); s.AsObject().Remove("Own"); }
        File.WriteAllText(path, json.ToJsonString());
        var page = cache.Read(Now);
        Assert.NotNull(page);
        Assert.True(string.IsNullOrEmpty(page!.Result.Batch));
        Assert.False(new DiscoverFeedback(FeedbackPath).Shown(page.Result.Batch ?? "", [Card("Song", 0)]));
        cache.Delete();
        Assert.False(File.Exists(path));
    }

    [AvaloniaFact]
    public void InView_IsTheCardsAtLeastHalfOnScreen()
    {
        var list = new ItemsControl
        {
            ItemsSource = Enumerable.Range(0, 12).ToList(),
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal }),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<int>((_, _) => new Border { Width = 90, Height = 100 }),
        };
        // 140 px tall: the second row shows 40 % of each card, which is not "seen". 90 px cards: four to a row with or without a scroll bar.
        var scroller = new ScrollViewer { Content = list, Height = 140, Width = 400 };
        var window = new Window { Content = scroller, Width = 400, Height = 140, SizeToContent = SizeToContent.Manual };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new[] { 0, 1, 2, 3 }, ForYouShelfView.InView(list, scroller));
        scroller.Offset = new Vector(0, 100);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Assert.Equal(new[] { 4, 5, 6, 7 }, ForYouShelfView.InView(list, scroller));
        window.Close();
    }
}
