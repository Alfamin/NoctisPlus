using Noctis.Models;

namespace Noctis.Services;

public interface IUnisonLyricsService
{
    /// <summary>
    /// Searches Unison (Better Lyrics' community lyrics) for the track and returns the best-voted
    /// matching version — TTML as ELRC, or its LRC / plain text — or null on a definitive miss.
    /// Throws <see cref="LyricsProviderException"/> when the provider could not answer.
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
