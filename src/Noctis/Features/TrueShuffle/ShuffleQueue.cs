using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;

namespace Noctis.Features.TrueShuffle;

/// <summary>The three places a shuffle can take its songs from.</summary>
internal enum ShuffleScope { CurrentQueue, WholeLibrary, OpenPlaylist }

/// <summary>
/// True Shuffle's access to Noctis's own player and library, typed. Replaces the plugin's reflection QueueBridge with
/// the same behaviour: nothing restarts the playing song, Noctis's weighted shuffle is never used for a custom order,
/// a failed change puts the upcoming songs back, and no file is opened except to work out a song's identity.
/// Call it on the UI thread (Noctis's collections may only be touched there).
/// </summary>
internal sealed class ShuffleQueue
{
    private readonly PlayerViewModel _player;
    private readonly ILibraryService _library;
    private readonly Func<bool> _explicitAllowed;
    private readonly Func<PlaylistViewModel?> _openPlaylist;

    public ShuffleQueue(PlayerViewModel player, ILibraryService library, Func<bool> explicitAllowed, Func<PlaylistViewModel?> openPlaylist)
    {
        _player = player; _library = library; _explicitAllowed = explicitAllowed; _openPlaylist = openPlaylist;
    }

    public PlayerViewModel Player => _player;
    public Track? Current => _player.CurrentTrack;
    public IReadOnlyList<Track> Upcoming => _player.UpNext.ToList();
    public bool ExplicitAllowed => _explicitAllowed();
    public Guid? OpenPlaylistId => _openPlaylist()?.PlaylistId;

    /// <summary>The domain view of a track. <paramref name="identities"/> remembers keys already worked out, so a
    /// track met twice opens its file once (the identity read is cheap: ~30 ms for 2,134 files, measured).</summary>
    public static Song SongOf(Track track, Dictionary<string, string>? identities = null)
    {
        var id = track.Id.ToString();
        var cacheKey = (track.IsRemoteStream ? "r|" : "l|") + id + "|" + track.FilePath;
        if (identities == null || !identities.TryGetValue(cacheKey, out var key))
        {
            key = Song.StableKey(id, track.FilePath, track.IsRemoteStream);
            if (identities != null) identities[cacheKey] = key;
        }
        return new Song(id, track.FilePath, track.Title, track.Artist, track.PlayCount, track.SkipWhenShuffling,
            track.IsExplicit, track.IsDisliked, track.SnoozedUntil, track.IsRemoteStream, key);
    }

    /// <summary>The path-based key, worked out without opening the file.</summary>
    public static string LegacyKeyOf(Track track) => new Song(track.Id.ToString(), track.FilePath, track.Title, track.Artist,
        track.PlayCount, track.SkipWhenShuffling, track.IsExplicit, track.IsDisliked, track.SnoozedUntil, track.IsRemoteStream).LegacyKey;

    /// <summary>The songs of a source, in their order; no file is opened.</summary>
    public List<Track> Snapshot(ShuffleScope scope) => scope switch
    {
        ShuffleScope.CurrentQueue => (_player.CurrentTrack is { } current ? new[] { current } : []).Concat(_player.UpNext).ToList(),
        ShuffleScope.WholeLibrary => _library.Tracks.ToList(),
        ShuffleScope.OpenPlaylist => _openPlaylist()?.Tracks.ToList()
            ?? throw new InvalidOperationException("Open a playlist in Noctis first, or choose Current queue / Whole library."),
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>Whether a source still has the same songs. Play counts, ratings and other tags may change while a
    /// shuffle is prepared (Noctis saves a play count as a song plays); only membership and order matter.</summary>
    public static bool SameSongs(IReadOnlyList<Track> before, IReadOnlyList<Track> after)
        => before.Count == after.Count && before.Select(t => t.Id).SequenceEqual(after.Select(t => t.Id));

    /// <summary>
    /// Plays this order. With <paramref name="keepCurrent"/> the playing song carries on and these become the upcoming
    /// songs; otherwise the first of them starts. Noctis's own shuffle and Track Radio are switched off first.
    /// </summary>
    public void Apply(IReadOnlyList<Track> ordered, bool keepCurrent)
    {
        if (ordered.Count == 0) return;
        var original = _player.UpNext.ToList();
        var previousCurrent = _player.CurrentTrack;
        // Never Noctis's weighted shuffle here; its command also restores its own order bookkeeping.
        if (_player.IsShuffleEnabled) _player.ToggleShuffleCommand.Execute(null);
        _player.IsRadioActive = false;
        if (keepCurrent && previousCurrent != null)
        {
            try
            {
                _player.ClearShuffleUpcoming();
                _player.AppendShuffleGenerated(ordered.ToList());
            }
            catch (Exception failed)
            {
                // A failed append must not silently erase the user's upcoming songs.
                if (!ReferenceEquals(_player.CurrentTrack, previousCurrent)) throw;
                try { _player.ClearShuffleUpcoming(); _player.AppendShuffleGenerated(original); }
                catch (Exception recovery) { throw new AggregateException("Queue application and recovery failed. No music files were changed.", failed, recovery); }
                throw new InvalidOperationException("Shuffle could not be applied. Previous upcoming songs were restored; built-in Shuffle is off.", failed);
            }
        }
        else _player.PlayShuffleOrder(ordered.ToList());
    }

    /// <summary>
    /// Starts the queue rule for a session: from now on songs the user adds go ahead of <paramref name="generated"/>
    /// (and of everything the shuffle appends later), behind the user's earlier ones, in order.
    /// </summary>
    public void BeginSession(IEnumerable<Track> generated)
        => _player.ShuffleGenerated = new HashSet<Track>(generated, ReferenceEqualityComparer.Instance);

    /// <summary>Back to Noctis's normal queue: Add to Queue appends at the end again.</summary>
    public void EndSession() => _player.ShuffleGenerated = null;

    public bool InSession => _player.ShuffleGenerated != null;

    /// <summary>Adds songs behind what is already upcoming (as the shuffle's own). Nothing is cleared, moved or restarted.</summary>
    public void AppendUpcoming(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count > 0) _player.AppendShuffleGenerated(tracks.ToList());
    }

    /// <summary>Puts these songs in the upcoming queue instead of what is there; the playing song is not touched. If that
    /// fails the upcoming songs are put back. Previous then returns to the song that played before.</summary>
    public void ReplaceUpcoming(IReadOnlyList<Track> tracks)
    {
        var original = _player.UpNext.ToList();
        _player.ClearShuffleUpcoming();
        try { _player.AppendShuffleGenerated(tracks.ToList()); }
        catch (Exception failed)
        {
            try { _player.ClearShuffleUpcoming(); _player.AppendShuffleGenerated(original); }
            catch (Exception recovery) { throw new AggregateException("The queue could not be rearranged or put back. No music files were changed.", failed, recovery); }
            throw new InvalidOperationException("The queue could not be rearranged; it was left as Noctis made it.", failed);
        }
        _player.ForgetPrecedingInQueue();
    }

    /// <summary>The whole list Noctis was last asked to play from: the songs above the one picked, the playing song and
    /// what is upcoming. Read it before <see cref="ReplaceUpcoming"/>, which forgets the songs above.</summary>
    public List<Track> PlayedList()
    {
        var list = _player.PrecedingInQueue.ToList();
        if (_player.CurrentTrack is { } current) list.Add(current);
        list.AddRange(_player.UpNext);
        return list;
    }

    /// <summary>The tags For you uses; none of them opens a file.</summary>
    public static TrackFacts Facts(Track t)
    {
        static DateTime? Utc(DateTime? value) => value is { } v && v > DateTime.MinValue
            ? (v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;
        return new TrackFacts(t.Id.ToString(), LegacyKeyOf(t), t.Title, t.Artist, t.AlbumArtist, t.Album,
            TrackFacts.SplitGenres(t.Genre), t.Year, t.Bpm, t.MusicalKey,
            (int)Math.Clamp(t.Duration.TotalMilliseconds, 0, int.MaxValue), t.PlayCount, t.Rating, t.IsFavorite, t.IsDisliked,
            Utc(t.LastPlayed), Utc(t.DateAdded));
    }

    /// <summary>Tags of every library track (see <see cref="Facts"/>).</summary>
    public List<TrackFacts> LibraryFacts() => _library.Tracks.Select(Facts).ToList();
}
