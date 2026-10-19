using Noctis.Models;

namespace Noctis.Services;

public interface ILrcRedLyricsService
{
    /// <summary>
    /// Looks the track up on lrc.red and returns its TTML lyrics as ELRC (word-synced, with duet
    /// voices and background vocals), LRC (line-synced) or plain text, or null on a definitive miss.
    /// Throws <see cref="LyricsProviderException"/> when the provider could not answer.
    /// </summary>
    Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default);
}
