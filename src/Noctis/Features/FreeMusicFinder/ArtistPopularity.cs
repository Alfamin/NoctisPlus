using System.Collections.Concurrent;
using System.Text.Json;

namespace FreeMusicFinder;

internal sealed partial class ArtistCatalog
{
    // Metadata by provider ID, separate from CatalogArtist's value equality. Ordinary Search
    // consumes nb_fan in its existing response: no per-artist HTTP calls in the ranking path.
    private readonly ConcurrentDictionary<string, long> _artistFans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _fanRefreshes = new(StringComparer.Ordinal);
    private bool _fansLoaded;
    internal long? FansOf(CatalogArtist artist) => _artistFans.TryGetValue(artist.Id, out var fans) ? fans : null;

    private async Task LoadArtistFansAsync(CancellationToken ct)
    {
        using (await _locks.EnterAsync("artist-fans", ct).ConfigureAwait(false))
        {
            if (_fansLoaded) return;
            var saved = await ReadAsync<Dictionary<string, long>>("artist-fans", ct).ConfigureAwait(false);
            if (saved is not null)
                foreach (var (id, fans) in saved.Value.Take(4096))
                    if (System.Text.RegularExpressions.Regex.IsMatch(id, @"^deezer:[1-9][0-9]{0,17}$") && fans >= 0)
                        _artistFans.TryAdd(id, fans);
            _fansLoaded = true;
        }
    }

    private async Task RememberArtistFansAsync(IEnumerable<JsonElement> rows, CancellationToken ct)
    {
        await LoadArtistFansAsync(ct).ConfigureAwait(false);
        var changed = false;
        foreach (var row in rows)
        {
            if (Id(row, "id", "deezer") is not { } id || !row.TryGetProperty("nb_fan", out var field) ||
                field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var fans) || fans < 0) continue;
            if (!_artistFans.TryGetValue(id, out var previous) || previous != fans)
            { _artistFans[id] = fans; changed = true; }
        }
        if (!changed) return;
        using (await _locks.EnterAsync("artist-fans", ct).ConfigureAwait(false))
        {
            var saved = new Cache<Dictionary<string, long>>(1, _now(), _artistFans.OrderByDescending(p => p.Value).Take(4096).ToDictionary());
            try { await Task.Run(() => AtomicJson.Write(SafeFile(Key("artist-fans"))!, saved), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { /* session ranking still works */ }
            Remember("artist-fans", saved);
        }
    }

    /// <summary>Album evidence and manual choices have already had first say. Owner #140: after that the page never
    /// asks which namesake is meant; it opens the most likely one at once: the most followed, or, with no counts at all,
    /// the catalog's own first result. <paramref name="decisive"/> (For you's songs, where a wrong guess shows another
    /// artist's songs): only a clear popularity gap picks one, else null.</summary>
    internal async Task<CatalogArtist?> PopularNamesakeAsync(string name, IReadOnlyList<CatalogArtist> candidates, CancellationToken ct, bool decisive = false)
    {
        await LoadArtistFansAsync(ct).ConfigureAwait(false);
        var exact = candidates.Where(a => a.Id.StartsWith("deezer:", StringComparison.Ordinal) && BotText.Key(a.Name) == BotText.Key(name)).ToArray();
        if (decisive ? exact.Length < 2 || exact.Length != candidates.Count : exact.Length == 0) return null;
        if (exact.Length == 1) return exact[0];
        // A widely followed artist (Post Malone: 4.2 million) needs no wait for its unknown namesakes' counts.
        bool famous = !decisive && exact.Any(a => FansOf(a) >= 100_000);
        if (!famous && exact.Any(a => FansOf(a) is null) && _fanRefreshes.TryAdd(BotText.Key(name), 0))
        {
            // Old artist caches omitted fan counts. One bounded artist-search refresh hydrates all candidates;
            // ordinary Search never waits for this repair, and offline the best known guess opens.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            try { await SearchAsync(name, true, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { _fanRefreshes.TryRemove(BotText.Key(name), out _); throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception ex) when (Recoverable(ex)) { }
        }
        ct.ThrowIfCancellationRequested();
        if (decisive)
        {
            if (exact.Any(a => FansOf(a) is null)) return null;
            var ranked = exact.OrderByDescending(a => FansOf(a)!.Value).ToArray();
            var top = FansOf(ranked[0])!.Value;
            return top >= 1000 && top >= 10d * Math.Max(1, FansOf(ranked[1])!.Value) ? ranked[0] : null;
        }
        // Unknown counts rank below every known one; ties keep the catalog's own order.
        return exact.Select((artist, place) => (artist, place)).OrderByDescending(c => FansOf(c.artist) ?? -1).ThenBy(c => c.place).First().artist;
    }
}
