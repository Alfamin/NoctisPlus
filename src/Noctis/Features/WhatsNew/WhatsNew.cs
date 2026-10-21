using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Noctis.Features.WhatsNew;

/// <summary>One new feature: an icon (an app geometry key), a title and one short line (owner: minimal text).</summary>
public sealed record WhatsNewItem(string Icon, string Title, string Line, string? Setting = null)
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
        "cache" => new("Features", Feature: "dev.moshi.freemusicfinder", Key: "streamCacheDays"),
        "firstBot" => new("Features", Feature: "dev.moshi.freemusicfinder", Key: "firstBot"),
        "about" => new("About", Control: "WhatsNewButton"),
        _ => null,
    };
}

/// <summary>The notes shipped with this build (Features/WhatsNew/whats-new.json; edit it for each release).</summary>
public sealed record WhatsNewNotes(string Version, IReadOnlyList<WhatsNewItem> Added, IReadOnlyList<string> Fixed)
{
    private sealed record NotesFile(string? Version, List<WhatsNewItem>? Added, List<string>? Fixed);

    public static WhatsNewNotes? Load()
    {
        try
        {
            using var stream = typeof(WhatsNewNotes).Assembly.GetManifestResourceStream("Noctis.WhatsNew.json");
            if (stream is null) return null;
            var file = JsonSerializer.Deserialize<NotesFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (file?.Version is not { Length: > 0 } version) return null;
            var added = (file.Added ?? []).Where(i => i is { Title.Length: > 0 }).ToList();
            var fixedOnes = (file.Fixed ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            return added.Count + fixedOnes.Count == 0 ? null : new(version, added, fixedOnes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// Owner #97: the first start after an update shows what was added and fixed, once. A new install records the version
/// without showing it (there is nothing it updated from). The notes show only for the version they were written for.
/// </summary>
public static class WhatsNewGate
{
    private const string SeenFile = "whats-new-seen.txt";

    /// <summary>Notes to show now, or null. <paramref name="existingProfile"/>: the profile was used before this start.</summary>
    public static WhatsNewNotes? ToShow(string dataDirectory, Version current, bool existingProfile, WhatsNewNotes? notes)
    {
        var running = Short(current);
        var path = Path.Combine(dataDirectory, SeenFile);
        string seen;
        try { seen = File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (seen == running) return null;
        if (seen.Length == 0 && !existingProfile) { MarkSeen(dataDirectory, current); return null; }
        if (notes is null || Short(notes.Version) != running) { MarkSeen(dataDirectory, current); return null; }
        return notes;
    }

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
    public string VersionLabel => "Version " + WhatsNewGate.Short(notes.Version);
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
