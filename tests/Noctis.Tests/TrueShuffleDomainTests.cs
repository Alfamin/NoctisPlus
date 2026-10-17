using Noctis.Features.TrueShuffle;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// True Shuffle's domain logic, now compiled into Noctis Plus (src/Noctis/Features/TrueShuffle). The checks are the
/// plugin's own (Alfamin/TrueShuffle tests/ShuffleTests.cs engine + cycle-store part, and ForYouTests.cs), unchanged,
/// so the native player integration starts from logic that is already proven.
/// </summary>
public class TrueShuffleDomainTests
{
    private static Song S(int i, int plays = 0, bool disliked = false) => new(i.ToString(), "", "Song " + i, "Artist", plays, false, false, disliked, null);
    private static void Check(bool value, string name) => Assert.True(value, name);
    private static void Throws(Action a, string name)
    {
        try { a(); } catch { return; }
        Assert.Fail("Did not reject: " + name);
    }
    private static Func<int, int> R(int seed) { var r = new Random(seed); return r.Next; }

    [Fact]
    public async Task Engine_Modes_Eligibility_AndCycleStore_BehaveAsInThePlugin()
    {
        string? temp = null;
        try
        {
            var pool = Enumerable.Range(1, 2000).Select(i => S(i, i % 7, i % 2 == 0)).ToArray();
            var random = ShuffleEngine.Build(pool, ShuffleMode.EqualChance, pool.Select(s => s.Key), null, null, draw: R(3));
            Check(random.Songs.Count == 2000 && random.Songs.Select(s => s.Key).Distinct().Count() == 2000, "Complete nonduplicate permutation");
            Check(random.Songs.Select(s => s.Id).Order().SequenceEqual(pool.Select(s => s.Id).Order()), "No track lost or invented");
            var noDislikes = pool.Select(s => s with { Disliked = false }).ToArray();
            Check(ShuffleEngine.Build(pool, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: R(7)).Songs.Select(s => s.Id)
                .SequenceEqual(ShuffleEngine.Build(noDislikes, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: R(7)).Songs.Select(s => s.Id)), "Disliked flag cannot bias equal-chance order");
            Check(!random.Songs.Select(s => s.Id).SequenceEqual(ShuffleEngine.Build(pool, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: R(4)).Songs.Select(s => s.Id)), "Different random draws produce different orders");
            var small = Enumerable.Range(1, 4).Select(i => S(i)).ToArray();
            var seen = new[] { small[0].Key, small[2].Key };
            var remaining = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, seen, null, null, draw: R(1));
            Check(remaining.Songs.Select(s => s.Id).Order().SequenceEqual(new[] { "2", "4" }), "Previously started songs excluded");
            Check(!remaining.NewCycle && seen.All(k => remaining.Seen.Contains(k)), "Building does not consume unstarted queued songs");
            var reset = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, small.Select(s => s.Key), small[0].Key, small[3].Key, draw: R(2));
            Check(reset.NewCycle && reset.Songs.Count == 3 && reset.Songs.All(s => s.Key != small[0].Key), "Exhaustion starts a new cycle without restarting current");
            Check(reset.Songs[0].Key != small[3].Key, "No immediate repeat at cycle boundary");
            var foreign = S(99).Key;
            var resetSubset = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, small.Select(s => s.Key).Append(foreign), null, null, draw: R(2));
            Check(resetSubset.Seen.Contains(foreign), "Subset reset preserves progress outside this source");
            var added = small.Append(S(5));
            Check(ShuffleEngine.Build(added, ShuffleMode.NoRepeats, small.Select(s => s.Key), null, null).Songs.Single().Id == "5", "New library additions immediately eligible");
            var discovery = ShuffleEngine.Build(new[] { S(1, 20), S(2, 0), S(3, 1), S(4, 0) }, ShuffleMode.Discovery, Array.Empty<string>(), null, null, draw: R(2));
            Check(discovery.Songs.Select(s => s.PlayCount).SequenceEqual(new[] { 0, 0, 1, 20 }), "Discovery gives least played songs priority");
            var boundaryDiscovery = ShuffleEngine.Build(new[] { S(1, 0), S(2, 20) }, ShuffleMode.Discovery,
                new[] { S(1).Key, S(2).Key }, null, S(1).Key, draw: R(2));
            Check(boundaryDiscovery.Songs.Select(s => s.PlayCount).SequenceEqual(new[] { 0, 20 }), "Discovery boundary avoidance cannot overtake unplayed songs");
            var never = ShuffleEngine.Build(new[] { S(1, 0), S(2, 1), S(3, 0) }, ShuffleMode.NeverPlayed, new[] { S(1).Key }, null, null);
            Check(never.Songs.Count == 1 && never.Songs[0].Id == "3", "Never played excludes both played and remembered starts");
            var emptyNever = ShuffleEngine.Build(small, ShuffleMode.NeverPlayed, small.Select(s => s.Key), null, null);
            Check(emptyNever.Songs.Count == 0 && !emptyNever.NewCycle, "Never played never silently falls back");
            var waiting = new HashSet<string> { small[1].Key };
            var withoutWaiting = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, Array.Empty<string>(), null, null, draw: R(5), upcoming: waiting);
            Check(withoutWaiting.Songs.Count == 3 && withoutWaiting.Songs.All(s => s.Key != small[1].Key), "A later plan leaves out the songs that are already upcoming");
            var cycled = ShuffleEngine.Build(small, ShuffleMode.NoRepeats, small.Select(s => s.Key), small[0].Key, null, draw: R(5), upcoming: waiting);
            Check(cycled.NewCycle && cycled.Songs.Count == 2 && cycled.Songs.All(s => s.Key != small[1].Key && s.Key != small[0].Key), "A fresh cycle also keeps out what is upcoming and what is playing");
            var heard = new HashSet<string> { S(2).Key }; var rightNow = DateTime.UtcNow;
            Check(ShuffleEngine.StillWanted(S(1), ShuffleMode.NoRepeats, heard, true, false, rightNow) && !ShuffleEngine.StillWanted(S(2), ShuffleMode.NoRepeats, heard, true, false, rightNow)
                && ShuffleEngine.StillWanted(S(2), ShuffleMode.EqualChance, heard, true, false, rightNow) && !ShuffleEngine.StillWanted(S(3, 1), ShuffleMode.NeverPlayed, heard, true, false, rightNow)
                && !ShuffleEngine.StillWanted(S(4) with { Skip = true }, ShuffleMode.EqualChance, heard, true, false, rightNow),
                "A song taken from a plan later is checked against the rules of the mode again");
            var gated = new[] { S(1), S(2) with { Skip = true }, S(3) with { Explicit = true }, S(4) with { SnoozedUntil = DateTime.UtcNow.AddHours(1) }, S(5, disliked: true) };
            Check(ShuffleEngine.Build(gated, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, explicitAllowed: false, skipDisliked: true).Songs.Single().Id == "1", "Eligibility respects skips, snoozes, explicit toggle and chosen dislike exclusion");
            Check(ShuffleEngine.Build(new[] { S(1), S(1) }, ShuffleMode.EqualChance, Array.Empty<string>(), null, null).Songs.Count == 1, "Duplicate ids deduplicated");
            Check(ShuffleEngine.Build(new[] { S(1) }, ShuffleMode.NoRepeats, Array.Empty<string>(), S(1).Key, null).Songs.Count == 0, "Single current song is not requeued");
            Check(ShuffleEngine.Build(Array.Empty<Song>(), ShuffleMode.EqualChance, Array.Empty<string>(), null, null).Songs.Count == 0, "Empty source harmless");
            // Enumerate every bounded draw combination for three tracks: all six permutations once.
            var permutations = new HashSet<string>();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++)
            {
                var draws = new Queue<int>(new[] { i, j });
                var p = ShuffleEngine.Build(small.Take(3), ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: _ => draws.Dequeue());
                permutations.Add(string.Join(",", p.Songs.Select(s => s.Id)));
            }
            Check(permutations.Count == 6, "Every permutation has one equally likely draw sequence");
            Throws(() => ShuffleEngine.Build(small, ShuffleMode.EqualChance, Array.Empty<string>(), null, null, draw: _ => -1), "Invalid RNG");

            temp = Path.Combine(Path.GetTempPath(), "true-shuffle-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            var store = new CycleStore(temp);
            Check((await store.LoadAsync()).Seen.Length == 0, "Missing private history starts empty");
            await store.SaveAsync(new(1, seen, seen[1]));
            var loaded = await new CycleStore(temp).LoadAsync();
            Check(loaded.Seen.SequenceEqual(seen) && loaded.Last == seen[1], "No-repeat progress survives restart");
            Check(ShuffleEngine.Build(small, ShuffleMode.NoRepeats, loaded.Seen, null, loaded.Last).Songs.All(s => !seen.Contains(s.Key)), "Restart excludes previous starts");
            Check(!Directory.EnumerateFiles(temp, ".cycle-*.tmp").Any(), "No staged history debris");
            string old = await File.ReadAllTextAsync(Path.Combine(temp, "cycle.json"));
            await File.WriteAllTextAsync(Path.Combine(temp, "cycle.json"), "invalid history");
            bool rejected = false;
            try { await store.LoadAsync(); } catch { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(temp, "cycle.json")) == "invalid history", "Corrupt history preserved rather than silently reset");
            await File.WriteAllTextAsync(Path.Combine(temp, "cycle.json"), old);
        }
        finally { if (temp is not null) try { Directory.Delete(temp, true); } catch { } }
    }

    [Fact]
    public void ForYou_ListeningLog_AndPlanner_BehaveAsInThePlugin() => TrueShuffleForYouChecks.Run((ok, name) => Assert.True(ok, name));
}
