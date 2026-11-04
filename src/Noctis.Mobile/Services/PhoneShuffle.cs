using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Features.TrueShuffle;

namespace Noctis.Mobile.Services;

/// <summary>Phone shuffle modes (#255). "For you" is hidden on the phone for now.</summary>
internal enum PhoneShuffleMode { Off, NoctisShuffle, EqualChance, NoRepeats, Discovery, NeverPlayed }

/// <summary>
/// Builds a queue order from Noctis.Core's ShuffleEngine plan. Not wired into UI yet (#255 step 2).
/// Off and Noctis shuffle return null: the caller keeps today's behaviour.
/// </summary>
internal static class PhoneShuffle
{
    public static readonly PhoneShuffleMode[] Visible =
        { PhoneShuffleMode.Off, PhoneShuffleMode.NoctisShuffle, PhoneShuffleMode.EqualChance,
          PhoneShuffleMode.NoRepeats, PhoneShuffleMode.Discovery, PhoneShuffleMode.NeverPlayed };

    public static ShuffleMode? ToEngine(PhoneShuffleMode mode) => mode switch
    {
        PhoneShuffleMode.EqualChance => ShuffleMode.EqualChance,
        PhoneShuffleMode.NoRepeats => ShuffleMode.NoRepeats,
        PhoneShuffleMode.Discovery => ShuffleMode.Discovery,
        PhoneShuffleMode.NeverPlayed => ShuffleMode.NeverPlayed,
        _ => null,
    };

    /// <summary>Returns the items in the plan's order, or null for Off / Noctis shuffle.</summary>
    public static List<T>? Order<T>(IReadOnlyList<T> items, Func<T, Song> toSong, PhoneShuffleMode mode,
        IEnumerable<string>? seen = null, string? current = null, string? last = null,
        Func<int, int>? draw = null)
    {
        var engine = ToEngine(mode);
        if (engine is null) return null;
        var byKey = new Dictionary<string, T>();
        var songs = new List<Song>(items.Count);
        foreach (var item in items)
        {
            var s = toSong(item);
            if (byKey.TryAdd(s.Key, item)) songs.Add(s);
        }
        var plan = ShuffleEngine.Build(songs, engine.Value, seen ?? Array.Empty<string>(), current, last, draw: draw);
        return plan.Songs.Where(s => byKey.ContainsKey(s.Key)).Select(s => byKey[s.Key]).ToList();
    }
}
