namespace FreeMusicFinder;

/// <summary>
/// Finds the Spotify or Deezer track link a bot takes for a song that has none (Apple-catalog songs). Each way is
/// asked in turn until one returns the same recording; a way that fails or finds nothing hands over to the next.
/// </summary>
internal static class TrackLinkLookup
{
    public static async Task<string?> FirstAsync(MusicRequest request,
        IEnumerable<Func<MusicRequest, CancellationToken, Task<IEnumerable<MusicRequest>>>> ways, CancellationToken ct)
    {
        foreach (var way in ways)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var found = (await way(request, ct).ConfigureAwait(false))
                    .FirstOrDefault(s => HunterMusicSource.IsTrackLink(s.Url) && RecordingMatch.Fits(request, s.Artist, s.Title, s.Duration));
                if (found is not null) return found.Url;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { } // the next way is asked
        }
        return null;
    }
}