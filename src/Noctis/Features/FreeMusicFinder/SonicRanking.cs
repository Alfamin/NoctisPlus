namespace FreeMusicFinder;

/// <summary>A recording's vector in one explicitly versioned model/preprocessing space. Never mix model spaces.</summary>
internal sealed record SonicVector(string Model, string Domain, float[] Values)
{
    public static SonicVector? Create(string model, string domain, IEnumerable<float> values)
    {
        var v = values.Take(4097).ToArray();
        if (string.IsNullOrWhiteSpace(model) || domain is not ("preview" or "local") || v.Length is < 8 or > 4096 || v.Any(x => !float.IsFinite(x))) return null;
        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm < 1e-9) return null;
        return new(model, domain, v.Select(x => (float)(x / norm)).ToArray());
    }

    public double? Similarity(SonicVector other)
    {
        if (Model != other.Model || Domain != other.Domain || Values.Length != other.Values.Length) return null;
        double dot = 0;
        for (int i = 0; i < Values.Length; i++) dot += (double)Values[i] * other.Values[i];
        return double.IsFinite(dot) ? Math.Clamp(dot, -1, 1) : null;
    }
}

internal sealed record SonicExample(string Key, SonicVector Vector, double Weight);

/// <summary>Bounded, song-level nearest-neighbour evidence; no artist ban, no whole-library average.</summary>
internal static class SonicRanking
{
    // Similar negatives must be very close; one skipped song never suppresses a whole sound/artist.
    public static double? Affinity(string key, SonicVector vector, IReadOnlyList<SonicExample> taste)
    {
        var neighbors = taste.Where(t => t.Key != key && t.Weight != 0 && double.IsFinite(t.Weight))
            .Select(t => (t.Weight, Similarity: vector.Similarity(t.Vector)))
            .Where(t => t.Similarity.HasValue).Select(t => (t.Weight, Similarity: t.Similarity!.Value)).ToList();
        var positive = neighbors.Where(t => t.Weight > 0).OrderByDescending(t => t.Similarity).Take(3).ToList();
        if (positive.Count < 2) return null; // Insufficient taste is not a reason to change the page.
        double total = positive.Sum(t => Math.Min(3, t.Weight));
        double score = positive.Sum(t => t.Similarity * Math.Min(3, t.Weight)) / total;
        var negative = neighbors.Where(t => t.Weight < 0 && t.Similarity > 0.8).OrderByDescending(t => t.Similarity).Take(2).ToList();
        score -= negative.Sum(t => Math.Min(1, -t.Weight) * (t.Similarity - 0.8)) * 0.25;
        return score;
    }

    /// <summary>Conservative relative adjustment. Missing vectors retain exactly their baseline score. Calibration must
    /// choose the blend on earlier chronological folds, never on the reported holdout. Identity filters stay upstream.</summary>
    public static Dictionary<string, double> Multipliers(IReadOnlyList<CatalogSong> songs,
        IReadOnlyDictionary<string, SonicVector> vectors, IReadOnlyList<SonicExample> taste, double blend)
    {
        blend = double.IsFinite(blend) ? Math.Clamp(blend, 0, 0.5) : 0;
        var scored = songs.Select(s => DiscoverIdentity.Key(s)).Distinct(StringComparer.Ordinal)
            .Where(vectors.ContainsKey).Select(k => (Key: k, Value: Affinity(k, vectors[k], taste)))
            .Where(x => x.Value.HasValue).Select(x => (x.Key, Value: x.Value!.Value)).ToList();
        if (blend == 0 || scored.Count < 3) return [];
        var values = scored.Select(x => x.Value).Order().ToArray();
        double median = values[values.Length / 2];
        double spread = Math.Max(0.02, values.Select(x => Math.Abs(x - median)).Order().ElementAt(values.Length / 2));
        return scored.ToDictionary(x => x.Key, x => Math.Exp(blend * Math.Clamp((x.Value - median) / (2 * spread), -1, 1)), StringComparer.Ordinal);
    }
}

/// <summary>The optional sonic stage is called only for For you, after identity/ownership filtering and before diversity.
/// Its implementation has its own deadline; an unavailable model/network is the unchanged metadata ranking.</summary>
internal interface IDiscoverSonic
{
    Task<IReadOnlyDictionary<string, double>> RankAsync(IReadOnlyList<CatalogSong> candidates,
        IReadOnlyList<TasteRecording> liked, IReadOnlyList<TastePlay> plays, CancellationToken ct);
}
