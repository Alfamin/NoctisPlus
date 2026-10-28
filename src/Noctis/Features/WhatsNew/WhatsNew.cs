using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.Features.WhatsNew;

/// <summary>One new feature: an icon (an app geometry key), a title and one short line (owner: minimal text).
/// <paramref name="Top"/>: one of the update's headlines (owner #149: shown first, before every other item).</summary>
public sealed record WhatsNewItem(string Icon, string Title, string Line, string? Setting = null, bool Top = false)
{
    public bool HasSetting => SettingShortcut.Resolve(Setting) is not null;
}

/// <summary>Allowlisted, native destinations for release-note links; no external URLs or commands.</summary>
public sealed record SettingShortcut(string Tab, string? Control = null, string? Feature = null, string? Key = null)
{
    public static SettingShortcut? Resolve(string? id) => id switch
    {
        "offline" => new("General", Control: "OfflineModeRow"),
        "wordTiming" => new("Features", Feature: "dev.moshi.wordlyrics", Key: "timePlayed"),
        "gaming" => new("Features", Feature: "dev.moshi.wordlyrics", Key: "lightWhileGaming"),
        "easeOff" => new("Features", Feature: "dev.moshi.wordlyrics", Key: "adaptToPc"),
        "cache" => new("Features", Feature: "dev.moshi.freemusicfinder", Key: "streamCacheDays"),
        "firstBot" => new("Features", Feature: "dev.moshi.freemusicfinder", Key: "firstBot"),
        "downloadAhead" => new("Features", Feature: "dev.moshi.freemusicfinder", Key: "downloadAhead"),
        "about" => new("About", Control: "WhatsNewButton"),
        _ => null,
    };
}

/// <summary>
/// What one or more releases added and fixed. The build ships every release's notes (Features/WhatsNew/whats-new.json,
/// newest first; add the new release at the top, important items only). Owner #149: someone who skipped updates sees all
/// they missed in one sheet, every release's headlines first.
/// </summary>
/// <param name="Version">The newest release in these notes.</param>
/// <param name="Updates">How many releases these notes cover.</param>
public sealed record WhatsNewNotes(string Version, IReadOnlyList<WhatsNewItem> Added, IReadOnlyList<string> Fixed, int Updates = 1)
{
    private sealed record ReleaseNotes(string? Version, List<WhatsNewItem>? Added, List<string>? Fixed);
    private sealed record NotesFile(List<ReleaseNotes>? Releases);

    /// <summary>Every release's notes, newest first. Empty when the file is missing or unreadable.</summary>
    public static IReadOnlyList<WhatsNewNotes> LoadReleases()
    {
        try
        {
            using var stream = typeof(WhatsNewNotes).Assembly.GetManifestResourceStream("Noctis.WhatsNew.json");
            if (stream is null) return [];
            var file = JsonSerializer.Deserialize<NotesFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var releases = new List<WhatsNewNotes>();
            foreach (var release in file?.Releases ?? [])
            {
                if (release?.Version is not { Length: > 0 } version || !System.Version.TryParse(version, out _)) continue;
                var added = (release.Added ?? []).Where(i => i is { Title.Length: > 0 }).ToList();
                var fixedOnes = (release.Fixed ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
                if (added.Count + fixedOnes.Count > 0) releases.Add(new(version, added, fixedOnes));
            }
            return [.. releases.OrderByDescending(r => WhatsNewGate.Parse(r.Version))];
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return []; }
    }

    /// <summary>Everything, as Settings → About shows it.</summary>
    public static WhatsNewNotes? Load() => Merge(LoadReleases());

    /// <summary>Several releases as one sheet: every headline first (newest release first), then the rest the same way.</summary>
    public static WhatsNewNotes? Merge(IReadOnlyList<WhatsNewNotes> releases)
    {
        if (releases.Count == 0) return null;
        var newestFirst = releases.OrderByDescending(r => WhatsNewGate.Parse(r.Version)).ToList();
        var added = newestFirst.SelectMany(r => r.Added).Where(i => i.Top)
            .Concat(newestFirst.SelectMany(r => r.Added).Where(i => !i.Top)).ToList();
        return new(newestFirst[0].Version, added, [.. newestFirst.SelectMany(r => r.Fixed)], newestFirst.Count);
    }
}

/// <summary>
/// Owner #97: the first start after an update shows what was added and fixed, once. Owner #150: a new install shows every
/// update once too. The notes show only for the version they were written for.
/// </summary>
public static class WhatsNewGate
{
    private const string SeenFile = "whats-new-seen.txt";

    /// <summary>Notes to show now, or null.</summary>
    public static WhatsNewNotes? ToShow(string dataDirectory, Version current, WhatsNewNotes? notes)
        => ToShow(dataDirectory, current, notes is null ? [] : [notes]);

    /// <summary>Owner #149: every release since the one last seen, up to this one, as one sheet. None seen (a new install,
    /// or a profile from before the sheet existed): all of them.</summary>
    public static WhatsNewNotes? ToShow(string dataDirectory, Version current, IReadOnlyList<WhatsNewNotes> releases)
    {
        var running = Short(current);
        var path = Path.Combine(dataDirectory, SeenFile);
        string seen;
        try { seen = File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (seen == running) return null;
        // Notes are shown only by the build they were written for (this release is among them).
        if (releases.All(r => Short(r.Version) != running)) { MarkSeen(dataDirectory, current); return null; }
        var now = Parse(running);
        var since = seen.Length > 0 ? Parse(seen) : new Version(0, 0, 0, 0);
        var missed = releases.Where(r => Parse(r.Version) is var v && v > since && v <= now).ToList();
        if (missed.Count == 0) { MarkSeen(dataDirectory, current); return null; }
        return WhatsNewNotes.Merge(missed);
    }

    /// <summary>A comparable version: "2.3" = "2.3.0" = "2.3.0.0"; unreadable text sorts first.</summary>
    internal static Version Parse(string text) => System.Version.TryParse(text, out var v)
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)) : new Version(0, 0, 0, 0);

    public static void MarkSeen(string dataDirectory, Version current)
    {
        try { Directory.CreateDirectory(dataDirectory); File.WriteAllText(Path.Combine(dataDirectory, SeenFile), Short(current)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // shown again next start at worst
    }

    /// <summary>"2.2.0" for 2.2.0.0 and "2.2.0"; a fourth part counts only when it is not 0 ("1.6.3.2").</summary>
    internal static string Short(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);
    internal static string Short(string text) => Version.TryParse(text, out var v) ? Short(v) : text.Trim();
}

/// <summary>The update sheet, its dismissal and the optional word timing settings shortcut.</summary>
public sealed partial class WhatsNewViewModel(WhatsNewNotes notes, Action close, Action? openWordTimingSettings = null, Action<string>? openSetting = null) : ObservableObject
{
    public string Heading => "What's new";
    public string VersionLabel => "Version " + WhatsNewGate.Short(notes.Version) + (notes.Updates > 1 ? $" · {notes.Updates} updates" : "");
    public IReadOnlyList<WhatsNewItem> Added => notes.Added;
    public IReadOnlyList<string> Fixed => notes.Fixed;
    public bool HasAdded => notes.Added.Count > 0;
    public bool HasFixed => notes.Fixed.Count > 0;
    public bool HasWordTimingSettings => openWordTimingSettings is not null;
    [RelayCommand] private void WordTimingSettings() { close(); openWordTimingSettings?.Invoke(); }
    [RelayCommand] private void OpenSetting(string? id)
    {
        if (SettingShortcut.Resolve(id) is null || openSetting is null) return;
        close(); openSetting(id!);
    }
    [RelayCommand] private void Close() => close();
}
