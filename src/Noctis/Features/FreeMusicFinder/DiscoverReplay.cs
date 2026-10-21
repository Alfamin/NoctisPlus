namespace FreeMusicFinder;

/// <summary>How well one way of choosing seed artists did over a replayed listening history.</summary>
internal sealed record ReplayScore(string Name, int Cutoffs, double Recall, double ReturnRecall, double SkipShare, double Artists)
{
    public override string ToString() =>
        $"{Name,-10} recall {Recall:P1} · returning favourites {ReturnRecall:P1} · seeds skipped next {SkipShare:P1} · artists/seed list {Artists:0.0} ({Cutoffs} cutoffs)";
}

/// <summary>
/// Offline, chronological check of For you's taste step. At each cutoff only the plays before it are used; the next
/// fortnight's listening says which artists the listener went on to enjoy (two or more good plays) and which they only
/// skipped. Catalog lookups are not replayed (no network), so this measures the seeds that candidate retrieval starts
/// from, not the songs. Read only; nothing leaves the computer.
/// </summary>
internal static class DiscoverReplay
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(14);

    public static List<ReplayScore> Run(IReadOnlyList<TastePlay> plays, IReadOnlyList<LocalSong> library, IReadOnlyList<TastePlaylist>? playlists = null,
        int seeds = Discover.SeedArtistsUsed)
    {
        var ordered = plays.Where(p => Taste.MainArtist(p.Artist).Length > 0).OrderBy(p => p.At).ToList();
        var methods = new (string Name, Func<List<TastePlay>, DateTime, List<string>> Pick)[]
        {
            ("before", (history, at) => Baseline(history, library, at, seeds)),
            ("now", (history, at) => Taste.Seeds(history, library, at, seeds, playlists).Select(s => BotText.Key(s.Name)).ToList()),
        };
        var totals = methods.Select(_ => (Cutoffs: 0, Hit: 0, Liked: 0, ReturnHit: 0, Returning: 0, Skipped: 0, Seeds: 0)).ToArray();
        if (ordered.Count == 0) return methods.Select(m => new ReplayScore(m.Name, 0, 0, 0, 0, 0)).ToList();
        for (var cutoff = ordered[0].At.Date.AddDays(60); cutoff < ordered[^1].At; cutoff += Window)
        {
            var history = ordered.TakeWhile(p => p.At < cutoff).ToList();
            var next = ordered.SkipWhile(p => p.At < cutoff).TakeWhile(p => p.At < cutoff + Window).ToList();
            var byArtist = next.GroupBy(p => BotText.Key(Taste.MainArtist(p.Artist))).ToList();
            var liked = byArtist.Where(g => g.Count(p => p.Weight >= 1) >= 2).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            var skipped = byArtist.Where(g => g.All(p => p.Weight < 0)).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            if (liked.Count == 0) continue;
            // Favourites of old: good plays on five or more days between a year and two months before the cutoff.
            var lasting = history.Where(p => p.Weight >= 1 && p.At >= cutoff.AddDays(-365) && p.At < cutoff.AddDays(-60))
                .GroupBy(p => BotText.Key(Taste.MainArtist(p.Artist))).Where(g => g.Select(p => p.At.Date).Distinct().Count() >= 5).Select(g => g.Key);
            var returning = lasting.Where(liked.Contains).ToHashSet(StringComparer.Ordinal);
            for (int m = 0; m < methods.Length; m++)
            {
                var chosen = methods[m].Pick(history, cutoff).ToHashSet(StringComparer.Ordinal);
                var t = totals[m];
                totals[m] = (t.Cutoffs + 1, t.Hit + chosen.Count(liked.Contains), t.Liked + liked.Count, t.ReturnHit + chosen.Count(returning.Contains),
                    t.Returning + returning.Count, t.Skipped + chosen.Count(skipped.Contains), t.Seeds + chosen.Count);
            }
        }
        return methods.Select((m, i) =>
        {
            var t = totals[i];
            return new ReplayScore(m.Name, t.Cutoffs, Ratio(t.Hit, t.Liked), Ratio(t.ReturnHit, t.Returning), Ratio(t.Skipped, t.Seeds), t.Cutoffs == 0 ? 0 : (double)t.Seeds / t.Cutoffs);
        }).ToList();
    }

    private static double Ratio(int a, int b) => b == 0 ? 0 : (double)a / b;

    /// <summary>For you's seed step as it was in 2.2.0 (one 30-day horizon, every replay counted in full), kept for comparison.</summary>
    internal static List<string> Baseline(IEnumerable<TastePlay> plays, IReadOnlyList<LocalSong> library, DateTime nowUtc, int count)
    {
        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var play in plays)
        {
            var key = BotText.Key(Taste.MainArtist(play.Artist));
            if (key.Length == 0) continue;
            var age = nowUtc - play.At;
            weight[key] = weight.GetValueOrDefault(key) + play.Weight * Math.Pow(0.5, (age < TimeSpan.Zero ? TimeSpan.Zero : age) / TimeSpan.FromDays(30));
        }
        foreach (var group in library.GroupBy(s => BotText.Key(Taste.MainArtist(s.Artist.Length > 0 ? s.Artist : s.AlbumArtist))).Where(g => g.Key.Length > 0))
            weight[group.Key] = weight.GetValueOrDefault(group.Key) + 0.4 * Math.Log(1 + group.Count());
        return weight.Where(w => w.Value > 0.2).OrderByDescending(w => w.Value).Take(count).Select(w => w.Key).ToList();
    }
}
