using Noctis.Models;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// What the playing file is, for Now Playing's quality badge and the card it opens: Core's
/// classification (<see cref="Track.IsHiResLossless"/>, <see cref="Track.IsLossless"/>,
/// <see cref="Track.CodecShortName"/>, <see cref="Track.QualityDetailLine"/>), Apple Music's
/// tiers - Lossless up to 24-bit/48 kHz, Hi-Res Lossless beyond it - and a compressed file's
/// codec spelt as the codec spells itself ("Opus", "Vorbis", not Core's OPUS/OGG badge). Null
/// for a file with no codec to name.
/// </summary>
public sealed record TrackQuality(string Label, string Detail, string Meaning, bool IsLossless)
{
    /// <summary>The Hi-Res line: Core's lossless sentence says nothing about the resolution.</summary>
    public const string HiResMeaning = "Beyond 24-bit/48 kHz: more resolution than a CD or standard Lossless holds.";

    public bool IsLossy => !IsLossless;

    public static TrackQuality? Of(Track? track)
    {
        if (track == null) return null;
        var label = track.AudioQualityBadge;
        if (label.Length == 0) return null;
        var detail = track.QualityDetailLine;
        var codec = track.CodecShortName;
        label = label switch { "OPUS" => "Opus", "OGG" => "Vorbis", _ => label };
        // The detail ends with Core's codec badge; a compressed file's reads as the label does.
        if (!track.IsLossless && codec.Length > 0 && detail.EndsWith(codec, StringComparison.Ordinal))
            detail = detail[..^codec.Length] + label;
        var meaning = track.IsHiResLossless ? HiResMeaning : track.AudioQualityDescription;
        return new TrackQuality(label, detail, meaning, track.IsLossless);
    }
}
