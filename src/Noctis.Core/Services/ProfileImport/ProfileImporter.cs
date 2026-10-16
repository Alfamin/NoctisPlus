using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Noctis.Services.ProfileImport;

/// <summary>Where an import stands, kept in the destination's <c>.noctis-import\state.json</c>.</summary>
public enum ProfileImportPhase { Staging, Committing, Done, Skipped }

public enum ProfileImportDecisionKind { NotNeeded, Start, Resume }

public sealed record ProfileImportDecision(ProfileImportDecisionKind Kind, string Reason);

public enum ProfileImportOutcome { Completed, SourceAppRunning, Failed, Cancelled }

/// <summary><see cref="Step"/> is "copying" while files are copied and "finishing" for the rest.</summary>
public sealed record ProfileImportProgress(string Step, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal);

public sealed record ProfileImportResult(ProfileImportOutcome Outcome, string? Error, IReadOnlyList<string> Warnings);

/// <summary>
/// Carries a whole Noctis profile (settings, library, stats, playlists, history, plugin data, sign-ins and caches)
/// into a new, empty Noctis Plus profile. The source is only ever read: files are opened shared, never locked,
/// moved or changed. Everything is copied into a staging folder inside the destination first and only moved into
/// place once complete, so an interrupted run resumes and a profile is never left half-imported. SQLite databases
/// are carried as a consistent snapshot (their -wal/-journal replayed), never as loose db/wal/shm files.
/// <para>
/// Adjusted on the way: official Noctis's default web-remote/local-API ports become Plus's own, the sync device
/// id is renewed so the two apps never pose as one device, and paths into the source profile point at the new
/// one. Left behind: the official app's update bookkeeping (it would hide Plus's own updates) and a running
/// session's lock and scratch files.
/// </para>
/// </summary>
public sealed class ProfileImporter
{
    public const string WorkFolderName = ".noctis-import";

    /// <summary>Any of these means the folder is somebody's working profile: never imported over.</summary>
    public static readonly IReadOnlyList<string> ProfileMarkers =
        new[] { "settings.json", "library.json", "library.db", "playlists.json", "play_history.json", "queue.json" };

    /// <summary>Carried or the import fails (and can be retried): without them the profile is not the user's.</summary>
    private static readonly HashSet<string> EssentialFiles = new(
        new[] { "settings.json", "library.json", "library.db", "playlists.json", "play_history.json" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The official app's own logs and update bookkeeping: carried, but into <see cref="ImportedLogsFolder"/>, where Plus
    /// neither reports Noctis's crashes as its own nor reads Noctis's update choices as its own.
    /// </summary>
    public const string ImportedLogsFolder = "imported-from-noctis";

    /// <summary>A running session's lock file: meaningless anywhere else.</summary>
    private const string InstanceLock = ".instance.lock";

    private const int OfficialWebRemotePort = 9420, PlusWebRemotePort = 9430;
    private const int OfficialLocalApiPort = 9421, PlusLocalApiPort = 9431;
    private const long FreeSpaceMargin = 256L * 1024 * 1024;
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions RewriteJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<bool> _isSourceAppRunning;
    private readonly Func<string, long?> _freeSpace;
    private readonly List<string> _warnings = new();

    public ProfileImporter(string sourceRoot, string destinationRoot,
        Func<bool>? isSourceAppRunning = null, Func<string, long?>? freeSpace = null)
    {
        SourceRoot = Normalize(sourceRoot);
        DestinationRoot = Normalize(destinationRoot);
        _isSourceAppRunning = isSourceAppRunning ?? (() => false);
        _freeSpace = freeSpace ?? FreeBytes;
    }

    public string SourceRoot { get; }
    public string DestinationRoot { get; }

    /// <summary>Whether the app that owns the source profile is open right now.</summary>
    public bool IsSourceAppRunning() => _isSourceAppRunning();

    private string WorkDir => Path.Combine(DestinationRoot, WorkFolderName);
    private string StagingDir => Path.Combine(WorkDir, "staging");
    private string StatePath => Path.Combine(WorkDir, "state.json");
    private string JournalPath => Path.Combine(WorkDir, "journal.log");

    public ProfileImportDecision Evaluate() => Evaluate(SourceRoot, DestinationRoot);

    /// <summary>Whether a destination profile should be filled from <paramref name="sourceRoot"/>. Reads only.</summary>
    public static ProfileImportDecision Evaluate(string sourceRoot, string destinationRoot)
    {
        var source = Normalize(sourceRoot);
        var destination = Normalize(destinationRoot);
        var state = TryReadState(Path.Combine(destination, WorkFolderName, "state.json"));
        if (state != null)
        {
            return state.Phase switch
            {
                ProfileImportPhase.Done => new(ProfileImportDecisionKind.NotNeeded, "already imported"),
                ProfileImportPhase.Skipped => new(ProfileImportDecisionKind.NotNeeded, "import was skipped"),
                ProfileImportPhase.Committing => new(ProfileImportDecisionKind.Resume, "finishing an interrupted import"),
                _ => new(ProfileImportDecisionKind.Resume, "continuing an interrupted import"),
            };
        }
        if (PathsOverlap(source, destination))
            return new(ProfileImportDecisionKind.NotNeeded, "source and destination overlap");
        if (!Directory.Exists(source) || !ProfileMarkers.Any(m => File.Exists(Path.Combine(source, m))))
            return new(ProfileImportDecisionKind.NotNeeded, "no Noctis profile to import");
        if (ProfileMarkers.Any(m => File.Exists(Path.Combine(destination, m))))
            return new(ProfileImportDecisionKind.NotNeeded, "Noctis Plus already has its own profile");
        return new(ProfileImportDecisionKind.Start, "new profile");
    }

    /// <summary>
    /// Runs or resumes the import. Returns <see cref="ProfileImportOutcome.SourceAppRunning"/> without copying anything
    /// while the source app is open (its live database and sign-in files would be caught mid-write).
    /// </summary>
    public Task<ProfileImportResult> RunAsync(IProgress<ProfileImportProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Run(progress, cancellationToken), CancellationToken.None);

    /// <summary>The user chose to start with an empty profile: remembered, so the import is not offered again.</summary>
    public void MarkSkipped(string reason)
    {
        Directory.CreateDirectory(WorkDir);
        var state = TryReadState(StatePath) ?? new ImportState { Source = SourceRoot, StartedUtc = DateTime.UtcNow };
        state.Phase = ProfileImportPhase.Skipped;
        state.FinishedUtc = DateTime.UtcNow;
        WriteState(state);
        Journal($"skipped: {reason}");
    }

    private ProfileImportResult Run(IProgress<ProfileImportProgress>? progress, CancellationToken ct)
    {
        _warnings.Clear();
        try
        {
            var state = TryReadState(StatePath);
            if (state?.Phase is ProfileImportPhase.Done or ProfileImportPhase.Skipped)
                return Result(ProfileImportOutcome.Completed);

            if (state?.Phase != ProfileImportPhase.Committing)
            {
                if (_isSourceAppRunning())
                    return Result(ProfileImportOutcome.SourceAppRunning);
                if (state == null)
                {
                    var decision = Evaluate();
                    if (decision.Kind == ProfileImportDecisionKind.NotNeeded)
                        return Result(ProfileImportOutcome.Completed);
                }
                if (!Directory.Exists(SourceRoot))
                    return Fail($"The Noctis profile folder is no longer there ({SourceRoot}).");

                Directory.CreateDirectory(StagingDir);
                state ??= new ImportState { Source = SourceRoot, StartedUtc = DateTime.UtcNow };
                state.Phase = ProfileImportPhase.Staging;
                WriteState(state);

                var plan = Plan();
                Journal($"staging: {plan.Count} files, {plan.Sum(e => e.Length)} bytes from {SourceRoot}");
                if (CheckFreeSpace(plan) is { } spaceError)
                    return Fail(spaceError);

                if (!Stage(plan, progress, ct, out var stageError))
                    return Fail(stageError!);
                // Opened while copying: its library and sign-ins may have moved on mid-copy. Wait, then top up.
                if (_isSourceAppRunning())
                    return Result(ProfileImportOutcome.SourceAppRunning);
                progress?.Report(new("finishing", plan.Count, plan.Count, plan.Sum(e => e.Length), plan.Sum(e => e.Length)));
                Adjust();

                state.Files = plan.Count;
                state.Bytes = plan.Sum(e => e.Length);
                state.Phase = ProfileImportPhase.Committing;
                state.Warnings = _warnings.ToList();
                WriteState(state);
                Journal("staged; moving into place");
            }
            else
            {
                progress?.Report(new("finishing", state.Files, state.Files, state.Bytes, state.Bytes));
            }

            ct.ThrowIfCancellationRequested();
            Commit(StagingDir, DestinationRoot, "");
            RemoveEmptyDirectories(StagingDir);
            RemoveEmptyDirectories(Path.Combine(WorkDir, "sqlite"));

            state.Phase = ProfileImportPhase.Done;
            state.FinishedUtc = DateTime.UtcNow;
            state.Warnings = state.Warnings.Concat(_warnings).Distinct().ToList();
            WriteState(state);
            Journal($"done ({_warnings.Count} notes)");
            return Result(ProfileImportOutcome.Completed);
        }
        catch (OperationCanceledException)
        {
            TryJournal("cancelled; resumes next launch");
            return Result(ProfileImportOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            TryJournal($"failed: {ex.GetType().Name}: {ex.Message}");
            return Fail(Describe(ex));
        }
    }

    // ── Planning ────────────────────────────────────────────────────────────────────────────

    private enum EntryKind { File, Sqlite }

    /// <summary><see cref="Target"/> is where the copy goes, relative to the new profile.</summary>
    private sealed record PlanEntry(string Relative, string Target, long Length, EntryKind Kind);

    private enum CopyResult { Copied, Missing, Unstable, Damaged }

    private List<PlanEntry> Plan()
    {
        var plan = new List<PlanEntry>();
        var pending = new Stack<string>();
        pending.Push(SourceRoot);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _warnings.Add($"Could not read the folder {Relative(dir)} ({ex.GetType().Name}).");
                continue;
            }
            foreach (var entry in entries)
            {
                var rel = Relative(entry.FullName);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    // A junction or link can lead anywhere (even back into this profile); never followed.
                    _warnings.Add($"Left out the link {rel}.");
                    continue;
                }
                if (entry is DirectoryInfo)
                {
                    if (!rel.Equals(WorkFolderName, StringComparison.OrdinalIgnoreCase)) pending.Push(entry.FullName);
                    continue;
                }
                var file = (FileInfo)entry;
                if (!IsCarried(file, rel)) continue;
                plan.Add(new PlanEntry(rel, TargetOf(rel), file.Length,
                    IsSqlite(file.FullName) ? EntryKind.Sqlite : EntryKind.File));
            }
        }
        plan.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return plan;
    }

    /// <summary>Where a source file lands in the new profile: its own place, or the imported-logs folder.</summary>
    internal static string TargetOf(string relative)
    {
        if (relative.Contains(Path.DirectorySeparatorChar) || relative.Contains(Path.AltDirectorySeparatorChar)) return relative;
        var officialState = relative.Equals("crash.log", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("session.log", StringComparison.OrdinalIgnoreCase)
            || relative.Equals("auto-update.json", StringComparison.OrdinalIgnoreCase)
            || (relative.StartsWith("crashlog-", StringComparison.OrdinalIgnoreCase)
                && relative.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        return officialState ? Path.Combine(ImportedLogsFolder, relative) : relative;
    }

    private static bool IsCarried(FileInfo file, string rel)
    {
        var topLevel = !rel.Contains(Path.DirectorySeparatorChar);
        if (topLevel && file.Name.Equals(InstanceLock, StringComparison.OrdinalIgnoreCase)) return false;
        var name = file.Name;
        // A save caught half-written (the app writes "<file>.tmp" and renames it over the original).
        if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        // SQLite's shared-memory index is rebuilt by SQLite; -wal/-journal travel inside the database snapshot.
        foreach (var suffix in new[] { "-shm", "-wal", "-journal" })
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var database = file.FullName[..^suffix.Length];
            if (File.Exists(database)) return false;
        }
        return true;
    }

    private string? CheckFreeSpace(List<PlanEntry> plan)
    {
        long needed = 0;
        foreach (var entry in plan)
        {
            var staged = new FileInfo(Path.Combine(StagingDir, entry.Target));
            if (!staged.Exists || staged.Length != entry.Length) needed += entry.Length;
        }
        var free = _freeSpace(DestinationRoot);
        if (free is null || free.Value >= needed + FreeSpaceMargin) return null;
        var drive = Path.GetPathRoot(DestinationRoot) ?? DestinationRoot;
        return $"Not enough free space on {drive.TrimEnd('\\', '/')}. Copying your Noctis profile needs about " +
               $"{FormatBytes(needed + FreeSpaceMargin)}; {FormatBytes(free.Value)} is free.";
    }

    // ── Copying ─────────────────────────────────────────────────────────────────────────────

    private bool Stage(List<PlanEntry> plan, IProgress<ProfileImportProgress>? progress, CancellationToken ct, out string? error)
    {
        error = null;
        var total = plan.Sum(e => e.Length);
        long done = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.MinValue;
        void Report(int files, long bytes, bool force = false)
        {
            if (progress == null || (!force && clock.Elapsed - lastReport < TimeSpan.FromMilliseconds(80))) return;
            lastReport = clock.Elapsed;
            progress.Report(new("copying", files, plan.Count, Math.Min(bytes, total), total));
        }
        Report(0, 0, force: true);

        for (var i = 0; i < plan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = plan[i];
            var source = Path.Combine(SourceRoot, entry.Relative);
            var staged = Path.Combine(StagingDir, entry.Target);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            var before = done;
            var result = entry.Kind == EntryKind.Sqlite
                ? SnapshotDatabase(source, staged, ct)
                : CopyStable(source, staged, ct, b => Report(i, before + b));
            done = before + entry.Length;
            var essential = entry.Relative == entry.Target && EssentialFiles.Contains(entry.Relative);
            switch (result)
            {
                case CopyResult.Unstable when essential:
                    error = $"Couldn't copy {entry.Relative}: it kept changing or stayed locked. Close Noctis and try again.";
                    return false;
                case CopyResult.Unstable:
                    _warnings.Add($"Couldn't copy {entry.Relative} (it kept changing or stayed locked); it was left out.");
                    break;
                case CopyResult.Damaged:
                    _warnings.Add(entry.Relative.Equals("library.db", StringComparison.OrdinalIgnoreCase)
                        ? "library.db was damaged and was left out; Noctis Plus rebuilds it from your library."
                        : $"{entry.Relative} was damaged and was left out.");
                    break;
                case CopyResult.Missing:
                    break; // removed by its owner after the plan was made: nothing left to carry
            }
            Report(i + 1, done, force: true); // per file; only the byte count inside a big file is throttled
        }
        Report(plan.Count, total, force: true);
        return true;
    }

    /// <summary>
    /// Copies a file without ever locking it against its owner, and keeps the copy only when the source did not
    /// change while it was read. A copy already staged by an interrupted run is reused when it still matches.
    /// </summary>
    private static CopyResult CopyStable(string source, string staged, CancellationToken ct, Action<long>? onBytes)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var before = Stat(source);
            if (before == null) return CopyResult.Missing;
            var existing = Stat(staged);
            if (existing == before) return CopyResult.Copied;

            var part = staged + ".part";
            try
            {
                long copied = 0;
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, read);
                        copied += read;
                        onBytes?.Invoke(copied);
                    }
                }
                var after = Stat(source);
                if (after == before && copied == before.Value.Length)
                {
                    File.SetLastWriteTimeUtc(part, before.Value.LastWriteUtc);
                    File.Move(part, staged, overwrite: true);
                    return CopyResult.Copied;
                }
            }
            catch (FileNotFoundException) { return CopyResult.Missing; }
            catch (DirectoryNotFoundException) { return CopyResult.Missing; }
            catch (IOException ex) when (IsDiskFull(ex)) { throw; }
            catch (IOException) { /* locked for a moment by its owner: try again */ }
            catch (UnauthorizedAccessException) { /* same */ }
            Thread.Sleep(200 * (attempt + 1));
        }
        return CopyResult.Unstable;
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27;

    /// <summary>
    /// A consistent copy of a SQLite database: the file and its -wal/-journal are copied together (and only kept
    /// when none changed meanwhile), opened privately so SQLite replays them, checked, and written out whole.
    /// </summary>
    private CopyResult SnapshotDatabase(string source, string staged, CancellationToken ct)
    {
        var scratch = Path.Combine(WorkDir, "sqlite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var copy = Path.Combine(scratch, "snapshot.db");
        try
        {
            var companions = new[] { "-wal", "-journal" };
            for (var attempt = 0; attempt < 5; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var before = companions.Select(s => Stat(source + s)).Prepend(Stat(source)).ToList();
                if (before[0] == null) return CopyResult.Missing;
                var ok = CopyStable(source, copy, ct, null) == CopyResult.Copied;
                for (var c = 0; ok && c < companions.Length; c++)
                    ok = before[c + 1] == null || CopyStable(source + companions[c], copy + companions[c], ct, null) == CopyResult.Copied;
                var after = companions.Select(s => Stat(source + s)).Prepend(Stat(source)).ToList();
                if (ok && before.SequenceEqual(after)) break;
                if (attempt == 4) return CopyResult.Unstable;
                foreach (var f in companions.Select(s => copy + s).Prepend(copy)) TryDeleteOwn(f);
                Thread.Sleep(250 * (attempt + 1));
            }

            var part = staged + ".part";
            TryDeleteOwn(part);
            var builder = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false };
            using (var connection = new SqliteConnection(builder.ToString()))
            {
                connection.Open();
                using (var check = connection.CreateCommand())
                {
                    check.CommandText = "PRAGMA integrity_check;";
                    var verdict = check.ExecuteScalar() as string;
                    if (!string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase))
                        return CopyResult.Damaged;
                }
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO $target;";
                vacuum.Parameters.AddWithValue("$target", part);
                vacuum.ExecuteNonQuery();
            }
            File.Move(part, staged, overwrite: true);
            return CopyResult.Copied;
        }
        catch (SqliteException)
        {
            return CopyResult.Damaged;
        }
        finally
        {
            foreach (var f in new[] { copy, copy + "-wal", copy + "-journal", copy + "-shm" }) TryDeleteOwn(f);
            RemoveEmptyDirectories(scratch);
        }
    }

    // ── Adjusting the copy ──────────────────────────────────────────────────────────────────

    private void Adjust()
    {
        // settings.json: if the copy is unreadable, the app's own backup stands in (as official Noctis would do).
        var settings = Path.Combine(StagingDir, "settings.json");
        var backup = Path.Combine(StagingDir, "settings.json.bak");
        if (File.Exists(settings) && TryParse(settings) == null)
        {
            if (File.Exists(backup) && TryParse(backup) != null)
            {
                File.Copy(backup, settings + ".part", overwrite: true);
                File.Move(settings + ".part", settings, overwrite: true);
                _warnings.Add("settings.json was damaged; its backup was used.");
            }
            else
            {
                File.Move(settings, settings + ".damaged", overwrite: true);
                _warnings.Add("settings.json was damaged and had no usable backup; Noctis Plus starts with default settings.");
            }
        }
        foreach (var file in new[] { settings, backup })
            if (File.Exists(file) && TryParse(file) is JsonObject root)
            {
                AdjustSettings(root);
                RewritePaths(root);
                WriteJson(file, root);
            }

        // Any other JSON naming the old profile folder (album index, similar-artist caches, plugin data).
        foreach (var file in Directory.EnumerateFiles(StagingDir, "*.json", SearchOption.AllDirectories))
        {
            if (file.Equals(settings, PathComparison)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            // "\u" too: a writer that escapes non-ASCII hides a path like C:\Users\<non-Latin name> from a text search.
            if (!MentionsSource(text) && !text.Contains("\\u", StringComparison.Ordinal)) continue;
            if (TryParse(file) is not { } node)
            {
                if (MentionsSource(text))
                    _warnings.Add($"{RelativeToStaging(file)} names the old folder but isn't valid JSON; copied as it was.");
                continue;
            }
            if (RewritePaths(node)) WriteJson(file, node);
        }
    }

    private static void AdjustSettings(JsonObject settings)
    {
        // Official Noctis's defaults would collide with it on the same PC; a port the user chose stays.
        ReplaceInt(settings, "webRemotePort", OfficialWebRemotePort, PlusWebRemotePort);
        ReplaceInt(settings, "localApiPort", OfficialLocalApiPort, PlusLocalApiPort);
        // Two installs sharing one sync identity would overwrite each other's device state.
        if (FindKey(settings, "syncDeviceId") is { } key && settings[key] is JsonValue id
            && id.TryGetValue<string>(out var value) && !string.IsNullOrWhiteSpace(value))
            settings[key] = Guid.NewGuid().ToString("N")[..12];
    }

    private static void ReplaceInt(JsonObject settings, string name, int from, int to)
    {
        if (FindKey(settings, name) is { } key && settings[key] is JsonValue v && v.TryGetValue<int>(out var port) && port == from)
            settings[key] = to;
    }

    private static string? FindKey(JsonObject o, string name)
        => o.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Points every string that is a path inside the source profile at the same place in the new one.</summary>
    private bool RewritePaths(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && MapPath(s) is { } mapped)
                    { o[key] = mapped; changed = true; }
                    else if (o[key] is { } child) changed |= RewritePaths(child);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && MapPath(s) is { } mapped)
                    { a[i] = mapped; changed = true; }
                    else if (a[i] is { } child) changed |= RewritePaths(child);
                }
                break;
        }
        return changed;
    }

    internal string? MapPath(string value)
    {
        foreach (var separator in new[] { '\\', '/' })
        {
            var root = SourceRoot.Replace('\\', separator).Replace('/', separator);
            if (value.Equals(root, PathComparison))
                return DestinationRoot.Replace('\\', separator).Replace('/', separator);
            if (value.Length > root.Length && value.StartsWith(root, PathComparison) && value[root.Length] is '\\' or '/')
                return DestinationRoot.Replace('\\', separator).Replace('/', separator) + value[root.Length..];
        }
        return null;
    }

    private bool MentionsSource(string text)
    {
        var lower = text.ToLowerInvariant();
        var root = SourceRoot.ToLowerInvariant();
        return lower.Contains(root.Replace("\\", "\\\\")) || lower.Contains(root.Replace('\\', '/')) || lower.Contains(root);
    }

    private static JsonNode? TryParse(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return null; }
    }

    private static void WriteJson(string file, JsonNode node)
    {
        var part = file + ".part";
        File.WriteAllText(part, node.ToJsonString(RewriteJson), new UTF8Encoding(false));
        File.Move(part, file, overwrite: true);
    }

    // ── Moving into place ───────────────────────────────────────────────────────────────────

    /// <summary>Moves staged entries into the profile; anything the profile already has is kept, never overwritten.</summary>
    private void Commit(string stagedDir, string targetDir, string relative)
    {
        if (!Directory.Exists(stagedDir)) return;
        Directory.CreateDirectory(targetDir);
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagedDir).ToList())
        {
            var name = Path.GetFileName(entry);
            var rel = relative.Length == 0 ? name : Path.Combine(relative, name);
            var target = Path.Combine(targetDir, name);
            var targetExists = File.Exists(target) || Directory.Exists(target);
            if (Directory.Exists(entry))
            {
                if (!targetExists) Directory.Move(entry, target);
                else if (Directory.Exists(target)) Commit(entry, target, rel);
                else _warnings.Add($"Kept Noctis Plus's own {rel}; the imported one is in {WorkFolderName}.");
            }
            else if (entry.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteOwn(entry); // this importer's unfinished copy from an interrupted run, never a profile file
            }
            else if (!targetExists) File.Move(entry, target);
            else _warnings.Add($"Kept Noctis Plus's own {rel}; the imported one is in {WorkFolderName}.");
        }
    }

    // ── State and helpers ───────────────────────────────────────────────────────────────────

    private sealed class ImportState
    {
        public int Version { get; set; } = 1;
        public ProfileImportPhase Phase { get; set; }
        public string Source { get; set; } = "";
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    private static ImportState? TryReadState(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ImportState>(File.ReadAllText(path), StateJson) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return null; }
    }

    private void WriteState(ImportState state)
    {
        Directory.CreateDirectory(WorkDir);
        var part = StatePath + ".part";
        File.WriteAllText(part, JsonSerializer.Serialize(state, StateJson), new UTF8Encoding(false));
        File.Move(part, StatePath, overwrite: true);
    }

    private void Journal(string line)
    {
        Directory.CreateDirectory(WorkDir);
        File.AppendAllText(JournalPath, $"{DateTime.UtcNow:O} {line}{Environment.NewLine}", new UTF8Encoding(false));
        DebugLog.Write("ProfileImport", line);
    }

    private void TryJournal(string line)
    {
        try { Journal(line); } catch { /* the outcome is still returned */ }
    }

    private ProfileImportResult Result(ProfileImportOutcome outcome) => new(outcome, null, _warnings.ToList());

    private ProfileImportResult Fail(string error)
    {
        TryJournal($"failed: {error}");
        return new(ProfileImportOutcome.Failed, error, _warnings.ToList());
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Windows refused access to a file or folder. Your Noctis profile was not changed.",
        IOException io when IsDiskFull(io) => "The disk is full. Free some space and try again.",
        _ => $"Copying stopped: {ex.Message} Your Noctis profile was not changed.",
    };

    private readonly record struct FileStat(long Length, DateTime LastWriteUtc);

    private static FileStat? Stat(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStat(info.Length, info.LastWriteTimeUtc) : null;
    }

    private static bool IsSqlite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[16];
            return stream.Read(header) == 16 && header.SequenceEqual(SqliteHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private string Relative(string fullPath) => Path.GetRelativePath(SourceRoot, fullPath);

    private string RelativeToStaging(string fullPath) => Path.GetRelativePath(StagingDir, fullPath);

    /// <summary>Removes only folders that are already empty (the import's own scaffolding); never a file.</summary>
    private static void RemoveEmptyDirectories(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var child in Directory.EnumerateDirectories(dir).ToList()) RemoveEmptyDirectories(child);
        try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, recursive: false); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Removes a temporary file this importer created in its own work folder.</summary>
    private static void TryDeleteOwn(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)));

    /// <summary>True when the two folders are the same or one lies inside the other (links at the roots resolved).</summary>
    internal static bool PathsOverlap(string a, string b)
    {
        static string Resolve(string p)
        {
            try { return Normalize(new DirectoryInfo(p).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? p); }
            catch (IOException) { return p; }
        }
        foreach (var (x, y) in new[] { (a, b), (Resolve(a), Resolve(b)) })
        {
            if (x.Equals(y, PathComparison)) return true;
            if (y.StartsWith(x + Path.DirectorySeparatorChar, PathComparison)) return true;
            if (x.StartsWith(y + Path.DirectorySeparatorChar, PathComparison)) return true;
        }
        return false;
    }

    private static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    /// <summary>
    /// Official Noctis holds this per-user mutex while it runs (Windows); its profile is then live and the import waits.
    /// </summary>
    public static bool IsOfficialNoctisRunning() =>
        OperatingSystem.IsWindows() && IsMutexHeld($"Noctis-SingleInstance-{Environment.UserName}");

    internal static bool IsMutexHeld(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException) { return true; }
    }
}
