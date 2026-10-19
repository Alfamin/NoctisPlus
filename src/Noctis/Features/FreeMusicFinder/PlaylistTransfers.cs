using System.Text.Json;

namespace FreeMusicFinder;

internal sealed class PlaylistTransfer
{
    public string Url { get; set; } = "";
    public string Folder { get; set; } = "";
    public PlaylistManifest? Manifest { get; set; }
    public HashSet<string> Enqueued { get; set; } = new(StringComparer.Ordinal);
    public int AlreadyOwned { get; set; }
    public bool Complete { get; set; }
    public DateTimeOffset RetryAt { get; set; }
    public string Status { get; set; } = "Queued metadata import";
}

/// <summary>A saved full metadata manifest feeds individual requests. The entire playlist is never sent to either bot.</summary>
internal sealed class PlaylistTransfers : IDisposable
{
    private readonly TelegramAccount _account;
    private readonly MusicSources _sources;
    private readonly Downloads _downloads;
    private readonly string _file;
    private readonly Func<BotTrack,bool> _owned;
    private PlaylistCatalog _catalog;
    private readonly List<PlaylistCatalog> _retiredCatalogs=new();
    private int _networkVersion;
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _importsStop = new();
    private readonly List<PlaylistTransfer> _jobs;
    private readonly object _gate=new();
    public event Action<string>? Changed;
    public IReadOnlyList<PlaylistTransfer> Jobs { get {lock(_gate) return _jobs.ToArray();} }
    public PlaylistTransfers(TelegramAccount account,MusicSources sources,Downloads downloads,string file,Func<BotTrack,bool> owned,PlaylistCatalog? catalog=null)
    {
        _account=account;_sources=sources;_downloads=downloads;_file=file;_owned=owned;_catalog=catalog??new();
        if(File.Exists(file))
        {
            if(new FileInfo(file).Length>16*1024*1024 || (File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("PLAYLIST_QUEUE_UNREADABLE: unsafe saved file.");
            try { _jobs=JsonSerializer.Deserialize<List<PlaylistTransfer>>(File.ReadAllText(file))??throw new InvalidDataException(); }
            catch(Exception ex) when(ex is JsonException or InvalidDataException) {throw new InvalidDataException("PLAYLIST_QUEUE_UNREADABLE: the original saved file was kept.",ex);}
            if(_jobs.Count>100 || _jobs.Any(j=>!MusicLink.TryParse(j.Url,out _) || !Path.IsPathFullyQualified(j.Folder))) throw new InvalidDataException("PLAYLIST_QUEUE_UNREADABLE: invalid saved playlist.");
        }
        else _jobs=new();
        _=Task.Run(Worker);
    }
    public PlaylistTransfer Start(string url,string folder)
    {
        if(!MusicLink.TryParse(url,out var link)) throw new FormatException("Paste a full Spotify or Deezer track, playlist or album link.");
        folder=Path.GetFullPath(folder);
        lock(_gate)
        {
            var old=_jobs.FirstOrDefault(j=>j.Url==link!.Url && j.Folder==folder);
            if(old is not null) return old;
            if(_jobs.Count>=100) throw new InvalidOperationException("PLAYLIST_QUEUE_FULL: finish the existing imports first.");
            var job=new PlaylistTransfer{Url=link!.Url,Folder=folder};_jobs.Add(job);
            try {Save();} catch {_jobs.Remove(job);throw;}
            return job;
        }
    }
    public void RetryIncomplete()
    {lock(_gate){foreach(var job in _jobs.Where(j=>!j.Complete))job.RetryAt=DateTimeOffset.MinValue;Save();}}
    public void Clear()
    {
        CancellationTokenSource old;
        lock(_gate)
        {
            var jobs=_jobs.ToArray();_jobs.Clear();
            try {Save();} catch {_jobs.AddRange(jobs);throw;}
            old=_importsStop;_importsStop=new();
        }
        old.Cancel();old.Dispose();
        try {Changed?.Invoke("Playlist imports cleared. Downloaded files are kept.");}catch(Exception){}
    }
    public void UpdateProxy(string setting,Action<string>? log=null)
    {
        var replacement=new PlaylistCatalog(PlaylistCatalog.MetadataHandler(setting,log));
        lock(_gate){_retiredCatalogs.Add(_catalog);_catalog=replacement;_networkVersion++;foreach(var job in _jobs.Where(j=>!j.Complete))job.RetryAt=DateTimeOffset.MinValue;Save();}
    }
    private void Save() {lock(_gate) AtomicJson.Write(_file,_jobs);}
    private void Notice(PlaylistTransfer job,string text)
    {lock(_gate){if(!_jobs.Contains(job))return;job.Status=text;Save();}try { Changed?.Invoke(text); } catch(Exception) {} }
    private async Task Worker()
    {
        while(!_stop.IsCancellationRequested)
        {
            PlaylistTransfer? job=null;
            try
            {
                if(!_downloads.Paused && _account.IsLoggedIn)
                {lock(_gate)job=_jobs.FirstOrDefault(j=>!j.Complete && j.RetryAt<=DateTimeOffset.UtcNow);if(job is not null)await Step(job,_stop.Token).ConfigureAwait(false);}
                await Task.Delay(TimeSpan.FromSeconds(2),_stop.Token).ConfigureAwait(false);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested) {break;}
            catch(OperationCanceledException) when(job is not null && !Jobs.Contains(job)) { }
            catch(Exception ex)
            {
                if(job is not null){job.RetryAt=DateTimeOffset.UtcNow.AddMinutes(10);try{Notice(job,"PLAYLIST_RETRY: "+ex.Message+" Metadata progress saved.");}catch(Exception){}}
                try {await Task.Delay(TimeSpan.FromSeconds(10),_stop.Token).ConfigureAwait(false);}catch(OperationCanceledException){break;}
            }
        }
    }
    internal async Task Step(PlaylistTransfer job,CancellationToken ct)
    {
        PlaylistCatalog catalog;int networkVersion;CancellationTokenSource scope;
        lock(_gate){if(!_jobs.Contains(job))return;catalog=_catalog;networkVersion=_networkVersion;scope=CancellationTokenSource.CreateLinkedTokenSource(ct,_importsStop.Token);}
        using var import=scope;ct=import.Token;
        if(!MusicLink.TryParse(job.Url,out var link))throw new InvalidDataException("Invalid saved playlist link.");
        try
        {
            Notice(job,"Reading the complete playlist metadata before requesting audio…");
            var manifest=await catalog.ReadAsync(link!,job.Manifest,
                m=>{lock(_gate){if(!_jobs.Contains(job))return;job.Manifest=m;Save();}},
                text=>Notice(job,text),ct).ConfigureAwait(false);
            if(!manifest.CompleteIds || manifest.Entries.Count!=manifest.Total)throw new InvalidDataException("METADATA_INCOMPLETE: no songs were requested.");
            foreach(var entry in manifest.Entries.OrderBy(e=>e.Position))
            {
                ct.ThrowIfCancellationRequested();
                if(_downloads.Paused) {Notice(job,"Metadata saved. Import paused; resume the queue to continue.");return;}
                if(entry.Request is not {} request || job.Enqueued.Contains(request.Identity))continue;
                var track=_sources.Track(request);
                var owned=_owned(track) || Downloader.FindExisting(job.Folder,track.FileName) is not null;
                lock(_gate)
                {
                    if(!_jobs.Contains(job))return;
                    if(owned)job.AlreadyOwned++;
                    else _downloads.Start(track,job.Folder);
                    // Commit the import's handled identity with the enqueue while holding the
                    // import gate, so clearing cannot race a later enqueue of a removed song.
                    job.Enqueued.Add(request.Identity);Save();
                }
            }
            var unresolved=manifest.Entries.Count(e=>e.Request is null);
            job.Complete=unresolved==0;
            job.RetryAt=unresolved>0?DateTimeOffset.UtcNow.AddHours(24):DateTimeOffset.MinValue;
            Notice(job,$"Full list saved: {manifest.Total} entries · {job.Enqueued.Count} unique recordings handled · {job.AlreadyOwned} already owned"+
                (unresolved>0?$" · {unresolved} unavailable metadata entries retained for retry.":". Individual downloads continue in the saved queue."));
        }
        catch(SourcesWaitingException ex){job.RetryAt=ex.Until;Notice(job,ex.Message);}
        catch(Exception ex) when(ex is IOException or HttpRequestException or TimeoutException)
        {job.RetryAt=DateTimeOffset.UtcNow.AddMinutes(10);Notice(job,"METADATA_RETRY: "+ex.Message+" No incomplete playlist was sent to a bot.");}
        finally
        {
            lock(_gate){if(_jobs.Contains(job) && networkVersion!=_networkVersion){job.RetryAt=DateTimeOffset.MinValue;Save();}if(!ReferenceEquals(catalog,_catalog)){catalog.Dispose();_retiredCatalogs.Remove(catalog);}}
        }
    }
    public void Dispose(){_stop.Cancel();_importsStop.Cancel();_catalog.Dispose();lock(_gate)foreach(var old in _retiredCatalogs)old.Dispose();}
}
