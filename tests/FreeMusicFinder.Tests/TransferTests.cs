using System.Net;
using System.Text;
using System.Text.Json;
using FreeMusicFinder;

namespace FreeMusicFinder.Tests;

internal static class TransferTests
{
    internal sealed class Source(string id) : IMusicSource
    {
        public string Id=>id;
        public int Calls;
        public int Searches;
        public Action<IProgress<double>?>? Progress;
        public Func<MusicRequest,Stream,Task<string>> Fetch=async(_,output)=>{await output.WriteAsync(Encoding.ASCII.GetBytes("fLaCtest audio"));return "flac";};
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query,CancellationToken ct){Searches++;return Task.FromResult<IReadOnlyList<MusicRequest>>([new("Song","Artist",180)]);}
        public Task<string> FetchAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct){Calls++;Progress?.Invoke(progress);return Fetch(request,output);}
    }
    private sealed class Http(Func<HttpRequestMessage,HttpResponseMessage> response) : HttpMessageHandler
    {
        public readonly List<string> Urls=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Urls.Add(request.RequestUri!.AbsoluteUri);return Task.FromResult(response(request));}
    }
    private static string Id(int number)=>number.ToString("D22");
    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value))};
    private static string Page(int count)=>"<script id=\"__NEXT_DATA__\" type=\"application/json\">"+JsonSerializer.Serialize(new{props=new{pageProps=new{state=new{settings=new{session=new{accessToken="test-anonymous-token"}},data=new{entity=new{name="Test list",trackList=Enumerable.Range(0,count).Select(i=>new{uri="spotify:track:"+Id(i),title="Song "+i,subtitle="Artist",duration=180000,isExplicit=true})}}}}}})+"</script>";
    private static Http FullList(int count)=>new(request=>
    {
        if(request.RequestUri!.Host=="spclient.wg.spotify.com")return Json(new{length=count,contents=new{items=Enumerable.Range(0,count).Select(i=>new{uri="spotify:track:"+Id(i)}),truncated=false}});
        if(request.RequestUri.AbsolutePath.StartsWith("/embed/track/"))
        {
            var id=request.RequestUri.Segments.Last();var index=int.Parse(id);
            var single="<script id=\"__NEXT_DATA__\">"+JsonSerializer.Serialize(new{props=new{pageProps=new{state=new{data=new{entity=new{uri="spotify:track:"+id,title="Song "+index,subtitle="Artist",duration=180000,isExplicit=true}}}}}})+"</script>";
            return new(HttpStatusCode.OK){Content=new StringContent(single)};
        }
        var response=new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Page(Math.Min(count,100)))};return response;
    });
    public static async Task RunAsync()
    {
        LinksAndMatching();Limits();await Routing();await DurableHandoff();await Catalogs();await QueueFailures();
        await Mp3WhenNoFlac();await SearchBesideDownload();await CompletedBytesAreNotASavedFile();
    }
    private static async Task CompletedBytesAreNotASavedFile()
    {
        Check.Section("100% bytes while checking the backup is not download completion");
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Source("Music Hunters") { Progress = p => p?.Report(1), Fetch = async (_, s) => { await s.WriteAsync(Encoding.ASCII.GetBytes("ID3 mp3 audio")); return "mp3"; } };
        var backup = new Source("DeezLoad") { Fetch = async (_, _) => { checking.TrySetResult(); await release.Task; throw new TrackUnavailableException("not there"); } };
        var sources = new MusicSources(first, backup, _ => { }, allowMp3: () => true);
        using var downloads = new Downloads(_ => { });
        var folder = Check.NewFolder("truthful-download-progress");
        var runningShares = new List<double>();
        downloads.Changed += d => { if (d.State == DownloadState.Running) lock(runningShares) runningShares.Add(d.Progress); };
        var item = downloads.Start(sources.Track(new("Leak", "Artist", 180)), folder);
        try
        {
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var row = new FinderDownloadQueue(downloads).Snapshot().Single();
            Check.True(row.Detail.Contains("MP3 ready") && row.Detail.Contains("DeezLoad"), "row tells the user the backup is being checked while MP3 is ready");
            Check.True(item.Progress < 1 && item.State == DownloadState.Running, "backup waiting cannot remain at 100%");
            lock(runningShares) Check.True(runningShares.All(p => p < 1), "no running notification claims the file is saved");
            Check.False(Directory.EnumerateFiles(folder, "*.mp3").Any(), "unfinished download is not published to the library");
        }
        finally { release.TrySetResult(); }
        await Check.UntilAsync(() => downloads.Active.Count == 0, "fallback download completes after the backup answers");
        Check.True(item.State == DownloadState.Saved && item.Progress == 1 && File.Exists(item.SavedPath), "100% means a saved playable file, with its final path");
    }
    private static async Task Mp3WhenNoFlac()
    {
        Check.Section("an MP3 is saved only when no bot has the recording in FLAC, and only when allowed");
        var request=new MusicRequest("Leak","Artist",180);
        Source Mp3(string id)=>new(id){Fetch=async(_,output)=>{await output.WriteAsync(Encoding.ASCII.GetBytes("ID3 mp3 audio"));return "mp3";}};
        Source Missing(string id)=>new(id){Fetch=(_,_)=>throw new TrackUnavailableException("not there")};
        var notices=new List<string>();
        foreach (var reverse in new[] { false, true })
        {
            var mp3 = Mp3(reverse ? "DeezLoad" : "Music Hunters"); var spare = Missing("backup");
            using var immediate = new MemoryStream();
            Check.Equal("mp3", await new MusicSources(mp3, spare, _ => { }, allowMp3: () => true, preferFlac: () => false)
                .FetchAsync(request, immediate, null, default), "save-immediately preference accepts MP3 from either provider");
            Check.Equal(0, spare.Calls, "save-immediately preference makes no unnecessary backup request");
        }
        Check.Equal("mp3", DeezLoadSource.TransferExtension(new TL.Document { mime_type = "audio/mpeg" }), "DeezLoad accepts MP3 even without a filename");
        Check.Equal("flac", DeezLoadSource.TransferExtension(new TL.Document { mime_type = "audio/flac" }), "DeezLoad still accepts FLAC without a filename");
        using(var output=new MemoryStream())
        {
            var hunters=Mp3("Music Hunters");var deez=Missing("DeezLoad");
            Check.Equal("mp3",await new MusicSources(hunters,deez,notices.Add,allowMp3:()=>true).FetchAsync(request,output,null,default),"no FLAC anywhere: the MP3 is saved");
            Check.Equal("ID3 mp3 audio",Encoding.ASCII.GetString(output.ToArray()),"the MP3 itself is what is written, not the other bot's leftovers");
            Check.Equal(1,deez.Calls,"the other bot was asked for FLAC first");
            Check.True(notices.Any(n=>n.Contains("MP3")),"the user is told an MP3 was saved");
        }
        {
            // The real path: Downloader's part file, not a MemoryStream. The MP3 is read back from it while the other
            // bot is asked, so a write-only file failed every MP3 fallback with "Stream does not support reading".
            var folder=Check.NewFolder("mp3-on-disk");
            var sources=new MusicSources(Mp3("Music Hunters"),Missing("DeezLoad"),_=>{},allowMp3:()=>true);
            var onDisk=new BotTrack("Leak","Artist",TimeSpan.FromSeconds(180),"Artist - Leak",(output,progress,ct)=>sources.FetchAsync(request,output,progress,ct));
            Check.Equal(DownloadOutcome.Saved,await Downloader.DownloadAsync(onDisk,folder,null,default),"the MP3 fallback works on a real download file");
            Check.Equal("ID3 mp3 audio",File.ReadAllText(Path.Combine(folder,"Artist - Leak.mp3")),"and the saved file is the MP3 itself");
        }
        using(var output=new MemoryStream())
        {
            var flac=new Source("DeezLoad");
            Check.Equal("flac",await new MusicSources(Mp3("Music Hunters"),flac,_=>{},allowMp3:()=>true).FetchAsync(request,output,null,default),"FLAC from the other bot wins over the MP3");
            Check.True(Encoding.ASCII.GetString(output.ToArray()).StartsWith("fLaC"),"and only the FLAC is written");
        }
        using(var output=new MemoryStream())
            await Check.ThrowsAsync<TrackUnavailableException>(()=>new MusicSources(Mp3("Music Hunters"),Missing("DeezLoad"),_=>{},allowMp3:()=>false).FetchAsync(request,output,null,default),"setting off: FLAC only, as before");
        using(var output=new MemoryStream())
        {
            var now=DateTimeOffset.UtcNow;
            await Check.ThrowsAsync<SourcesWaitingException>(()=>new MusicSources(Mp3("Music Hunters"),new Source("DeezLoad"){Fetch=(_,_)=>throw new SourceLimitException("DeezLoad","limit",now.AddHours(1))},_=>{},now:()=>now,allowMp3:()=>true)
                .FetchAsync(request,output,null,default),"a bot that is waiting out a limit may still have FLAC: the song waits instead of taking the MP3");
        }
    }
    private static async Task SearchBesideDownload()
    {
        Check.Section("a search does not wait for the download in progress");
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow=new Source("DeezLoad"){Fetch=async(_,output)=>{entered.SetResult();await release.Task;await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC"));return "flac";}};
        var sources=new MusicSources(new Source("Music Hunters"),slow,_=>{});
        using var output=new MemoryStream();
        var download=sources.FetchAsync(new MusicRequest("Song","Artist",180,PreferredSource:"DeezLoad"),output,null,default);
        await entered.Task;
        var search=sources.SearchAsync("Artist Song",default);
        Check.True(await Task.WhenAny(search,Task.Delay(2000))==search,"the search answers while the file is still coming");
        Check.False(download.IsCompleted,"the download was not disturbed");
        release.SetResult();
        Check.Equal("flac",await download,"and the download still finishes");
    }
    private static void LinksAndMatching()
    {
        Check.Section("safe music links and recording identity");
        Check.True(MusicLink.TryParse("https://open.spotify.com/intl-en/playlist/"+Id(1)+"?si=ignored",out var parsed),"locale-prefixed link accepted");
        Check.Equal("https://open.spotify.com/playlist/"+Id(1),parsed!.Url,"tracking query removed");
        foreach(var url in new[]{"http://open.spotify.com/playlist/"+Id(1),"https://open.spotify.com.attacker/playlist/"+Id(1),"https://user:pass@open.spotify.com/playlist/"+Id(1),"https://127.0.0.1/x","file:///C:/x","https://open.spotify.com:444/playlist/"+Id(1),"https://open.spotify.com/playlist/%2e%2e"}) Check.False(MusicLink.TryParse(url,out _),"unsafe/unsupported link rejected");
        var wanted=new MusicRequest("Runaway","Kanye West",548,Explicit:true);
        Check.True(RecordingMatch.Fits(wanted,"Kanye West","Runaway",TimeSpan.FromSeconds(549)),"minor encoding duration difference accepted");
        Check.False(RecordingMatch.Fits(wanted,"Other Artist","Runaway",TimeSpan.FromSeconds(548)),"same title/duration cannot hide another artist");
        Check.False(RecordingMatch.Fits(wanted,"Kanye West","Runaway (Live)",TimeSpan.FromSeconds(548)),"live version rejected even at same duration");
        Check.False(RecordingMatch.Fits(wanted,"Kanye West","Runaway (Clean)",TimeSpan.FromSeconds(548)),"clean version rejected for explicit request");
        Check.False(RecordingMatch.Fits(wanted,"Kanye West","Runaway",TimeSpan.FromSeconds(300)),"large duration mismatch rejected");
        Check.False(RecordingMatch.Fits(wanted,"","",null),"unidentified files rejected");
        Check.False(RecordingMatch.Fits(new("Same Song","Drake",180),"Drake Bell","Same Song",TimeSpan.FromSeconds(180)),"artist substring does not accept another performer");
        Check.True(RecordingMatch.Fits(new("Song","Artist feat. Guest",180),"Artist","Song",TimeSpan.FromSeconds(180)),"guest-credit differences retain the primary artist");
        Check.True(RecordingMatch.Fits(new("Scared Of Love (with instrumental by Ghost Loft)","Juice WRLD",170),"Juice Wrld","Scared Of Love",TimeSpan.FromSeconds(170)),"production credits are not mistaken for an instrumental recording");
        // The same recording as Spotify writes it and as the files the bots send are tagged.
        static TimeSpan s(int seconds)=>TimeSpan.FromSeconds(seconds);
        Check.True(RecordingMatch.Fits(new("Night Drive (with Guest) - Remix","Singer, Guest",222.4),"Singer, Guest","Night Drive (Remix)",s(222)),"\"(with Guest) - Remix\" is \"(Remix)\"");
        Check.True(RecordingMatch.Fits(new("Night Drive - Bonus","Singer"),"Singer","Night Drive (Bonus)",null),"\"- Bonus\" is \"(Bonus)\", also when no length is known");
        Check.True(RecordingMatch.Fits(new("HEART. FEAT. GUEST.","Singer, Guest",213.4),"Singer, Guest","HEART.",s(213)),"a guest named in the title without brackets");
        Check.True(RecordingMatch.Fits(new("Night Drive","Singer"),"Singer","Night Drive (prod. Somebody)",null),"a producer named in the title");
        Check.True(RecordingMatch.Fits(new("Night Drive","Dawid Podsiadło",243),"Dawid Podsiadlo","Night Drive",s(243)),"a letter Unicode does not take apart (ł) written plainly in the file");
        Check.True(RecordingMatch.Fits(new("Night Drive","Guest, Singer",186),"Singer; Guest","Night Drive",s(186)),"artists joined by a semicolon in the file's tag");
        Check.True(RecordingMatch.Fits(new("Night Drive - Remastered 2011","Singer",200),"Singer","Night Drive",s(201)),"an addition one side leaves out, with the lengths agreeing");
        Check.False(RecordingMatch.Fits(new("Night Drive - Remastered 2011","Singer"),"Singer","Night Drive",null),"but not when no length says so");
        Check.False(RecordingMatch.Fits(new("Night Drive - Pt. 1","Singer",200),"Singer","Night Drive - Pt. 2",s(200)),"two different additions are two recordings, even at the same length");
        Check.False(RecordingMatch.Fits(new("Night Drive - Remix","Singer",200),"Singer","Night Drive",s(200)),"a remix written Spotify's way is still not the original");
        Check.False(RecordingMatch.Fits(new("Night Drive (with Guest)","Singer",200),"Singer","Night Drive Home",s(200)),"another title stays another song");
        Check.False(RecordingMatch.Fits(new("Dancing with a Stranger","Singer",200),"Singer","Dancing",s(200)),"\"with\" inside a title is part of the title");
        var local=PlaylistCatalog.ParseLocal("spotify:local:::Juice+WRLD+-+Unreleased+Song:248",["Juice WRLD"]);
        Check.Equal("Juice WRLD",local!.Artist,"local-file artist/title delimiter is read from metadata");
        Check.Equal("Unreleased Song",local.Title,"local title preserved");
        Check.Equal(248.0,local.Seconds,"local duration preserved");
        Check.True(local.Identity.StartsWith("spotify:local:"),"local identity remains stable across providers");
        Check.Equal("Bandit OG",PlaylistCatalog.ParseLocal("spotify:local:::Juice+WRLD+++Bandit+OG:215",["Juice WRLD"])!.Title,"an explicit known artist prefix can repair a filename-only entry");
        Check.Equal<MusicRequest?>(null,PlaylistCatalog.ParseLocal("spotify:local:::Unknown+Name:215",["Juice WRLD"]),"ambiguous local artists are not guessed");
        var inline=DeezLoadSource.InlineTrack("Lucid Dreams","Artist: Juice Wrld\nAlbum: Goodbye & Good Riddance","\u3164\nhttps://www.deezer.com/track/601837422")!;
        Check.Equal("Juice Wrld",inline.Artist,"live DeezLoad artist label is parsed, not stored as part of the name");
        Check.Equal("Lucid Dreams",inline.Title,"live DeezLoad result title preserved");
        Check.Equal<MusicRequest?>(null,DeezLoadSource.InlineTrack("Truncated…","Artist: B Lou","https://www.deezer.com/track/1"),"truncated bot titles cannot silently choose a different recording");
    }
    private static void Limits()
    {
        Check.Section("bot quota replies and Telegram-wide waits");
        var now=DateTimeOffset.UtcNow;
        foreach(var text in new[]{"Your position in the queue is: 237","Donate for ZIP downloads","Total tracks: 500","Downloading track 50 of 100"}) Check.Equal<SourceLimitException?>(null,BotLimits.Read("DeezLoad",text,now),"progress and advertisements are not quotas");
        Check.Equal<SourceLimitException?>(null,BotLimits.Read("DeezLoad","Daily limit: 500; remaining 250",now),"an allowance description is not exhaustion");
        Check.Equal(now.AddSeconds(7),BotLimits.Read("Music Hunters","Please wait 5 seconds",now)!.Until,"provider-specific short rate waits are respected");
        var quota=BotLimits.Read("DeezLoad","Daily limit reached, retry after 45 minutes",now)!;
        Check.False(quota.Global,"a bot quota is provider-specific");
        Check.Equal(now.AddSeconds(45*60+2),quota.Until,"bot's explicit wait used");
        Check.Equal(now.AddHours(24),BotLimits.Read("DeezLoad","Daily limit reached",now)!.Until,"unspecified reset is a conservative recheck, not invented midnight");
        var flood=BotLimits.Read("DeezLoad","FLOOD_WAIT_60",now)!;
        Check.True(flood.Global,"Telegram flood wait blocks both providers");
        Check.Equal(now.AddSeconds(62),flood.Until,"Telegram wait includes a small margin");
        Check.True(BotLimits.Read("DeezLoad","FLOOD_PREMIUM_WAIT_60",now)!.Global,"Telegram media flood waits are account-wide too");
    }
    private static async Task Routing()
    {
        Check.Section("fallback keeps partial files, quotas and recordings separate");
        var primary=new Source("DeezLoad");var backup=new Source("Music Hunters");var notices=new List<string>();
        primary.Fetch=async(_,stream)=>{await stream.WriteAsync(Encoding.ASCII.GetBytes("partial primary bytes"));throw new TimeoutException("Unavailable");};
        var sources=new MusicSources(primary,backup,notices.Add);
        using var output=new MemoryStream();
        Check.Equal("flac",await sources.FetchAsync(new("Song","Artist",180),output,null,default),"backup succeeds after a primary failure");
        Check.Equal("fLaCtest audio",Encoding.ASCII.GetString(output.ToArray()),"partial primary payload discarded before backup writes");
        Check.Equal(1,backup.Calls,"exactly one fallback request");
        primary.Fetch=(_,_)=>throw new SourceLimitException("DeezLoad","Daily quota",DateTimeOffset.UtcNow.AddHours(24));
        await sources.FetchAsync(new("Song 2","Artist"),output,null,default);
        var calls=primary.Calls;
        await sources.FetchAsync(new("Song 3","Artist"),output,null,default);
        Check.Equal(calls,primary.Calls,"limited provider is skipped on later tracks");
        Check.Equal(3,backup.Calls,"backup continues while primary is limited");
        var first=new Source("DeezLoad"){Fetch=(_,_)=>throw new SourceLimitException("DeezLoad","Telegram wait",DateTimeOffset.UtcNow.AddMinutes(1),true)};
        var other=new Source("Music Hunters");var global=new MusicSources(first,other,_=>{});
        await Check.ThrowsAsync<SourcesWaitingException>(()=>global.FetchAsync(new("Song","Artist"),output,null,default),"account-wide flood wait is deferred");
        Check.Equal(0,other.Calls,"backup is not used to bypass a Telegram-wide wait");
        var brokenBackup=new Source("Music Hunters"){Fetch=(_,_)=>throw new TrackUnavailableException("No recording")};
        var blocked=new MusicSources(new Source("DeezLoad"){Fetch=(_,_)=>throw new SourceLimitException("DeezLoad","Quota",DateTimeOffset.UtcNow.AddMinutes(1))},brokenBackup,_=>{});
        await Check.ThrowsAsync<SourcesWaitingException>(()=>blocked.FetchAsync(new("Song","Artist"),output,null,default),"backup miss still preserves a track for the primary's reset");
    }
    private static async Task DurableHandoff()
    {
        Check.Section("100-song split across two 50-song allowances, restart and 1,000-song manifests");
        var folder=Check.NewFolder("durable-handoff");var store=Path.Combine(folder,"queue.json");var state=Path.Combine(folder,"limits.json");
        var now=DateTimeOffset.UtcNow;var primary=new Source("DeezLoad");var backup=new Source("Music Hunters");var servedPrimary=new List<string>();var servedBackup=new List<string>();
        primary.Fetch=async(request,stream)=>{if(servedPrimary.Count==50)throw new SourceLimitException("DeezLoad","50 allowance used",now.AddHours(24));servedPrimary.Add(request.Identity);await stream.WriteAsync(Encoding.ASCII.GetBytes("fLaC primary"));return "flac";};
        backup.Fetch=async(request,stream)=>{if(servedBackup.Count==50)throw new SourceLimitException("Music Hunters","50 allowance used",now.AddHours(24));servedBackup.Add(request.Identity);await stream.WriteAsync(Encoding.ASCII.GetBytes("fLaC backup"));return "flac";};
        var sources=new MusicSources(primary,backup,_=>{},state,()=>now);
        using(var queue=new Downloads(_=>{},store,sources.Track))
        {
            for(var i=0;i<101;i++)queue.Start(sources.Track(new("Song "+i,"Artist",180,"https://open.spotify.com/track/"+Id(i))),Path.Combine(folder,"audio"));
            await Check.UntilAsync(()=>queue.History.Count(d=>d.State==DownloadState.Saved)==100 && queue.Active.Count==1,"100 saved and only the last track waiting");
            Check.Equal(50,servedPrimary.Count,"primary used its 50 slots");Check.Equal(50,servedBackup.Count,"backup used 50 other songs");
            Check.Equal(0,servedPrimary.Intersect(servedBackup).Count(),"backup did not repeat the first 50");
            Check.True(File.ReadAllText(store).Contains("Song 100"),"remaining track stored before shutdown");
            queue.Dispose();await Task.Delay(100);
        }
        var nextPrimary=new Source("DeezLoad");var nextBackup=new Source("Music Hunters");
        var later=new MusicSources(nextPrimary,nextBackup,_=>{},state,()=>now.AddDays(1));
        // Persisted retry is normally a real future date; emulate the next day in the saved queue for this clock test.
        var saved=File.ReadAllText(store).Replace(now.AddHours(24).ToString("O"),now.AddSeconds(-1).ToString("O"));
        using(var json=JsonDocument.Parse(saved))
        {
            var rows=json.RootElement.GetProperty("Items").EnumerateArray().Select(item=>new{Request=JsonSerializer.Deserialize<MusicRequest>(item.GetProperty("Request").GetRawText()),Folder=item.GetProperty("Folder").GetString(),State=item.GetProperty("State").GetInt32(),Error="",RetryAt=(DateTimeOffset?)null}).ToArray();
            AtomicJson.Write(store,new{Version=1,Paused=false,Items=rows});
        }
        using(var restarted=new Downloads(_=>{},store,later.Track))
        {
            await Check.UntilAsync(()=>restarted.Active.Count==0,"remaining track completes after restart/reset");
            Check.Equal(1,nextPrimary.Calls,"only the unfinished song was requested again");
            Check.Equal(0,nextBackup.Calls,"no unnecessary backup requests after primary recovers");
            Check.Equal(101,Directory.GetFiles(Path.Combine(folder,"audio"),"*.flac").Length,"all recordings saved once");
        }
        var huge=new PlaylistManifest();
        using var large=JsonDocument.Parse(JsonSerializer.Serialize(new{length=1000,contents=new{items=Enumerable.Range(0,1000).Select(i=>new{uri="spotify:track:"+Id(i)})}}));
        PlaylistCatalog.ReadIds(large.RootElement,huge,0);
        Check.Equal(1000,huge.Entries.Count,"full 1,000-entry manifest retained independently of any bot cap");
        Check.Equal(1000,huge.Entries[^1].Position,"playlist positions survive large imports");
    }
    private static async Task Catalogs()
    {
        Check.Section("metadata is complete before audio; private links, truncation and changed lists");
        MusicLink.TryParse("https://open.spotify.com/playlist/"+Id(1),out var link);
        using var handler=FullList(125);using var catalog=new PlaylistCatalog(handler);var checkpoints=new List<int>();
        var manifest=await catalog.ReadAsync(link!,null,m=>checkpoints.Add(m.Entries.Count),_=>{},default);
        Check.True(manifest.CompleteIds,"authoritative playlist IDs confirmed");Check.Equal(125,manifest.Entries.Count,"all 125 entries imported");
        Check.True(manifest.Entries.All(e=>e.Request?.Explicit==true),"explicit flags retained");
        Check.Equal(125,checkpoints[0],"full ID list saved at the first checkpoint");
        Check.False(JsonSerializer.Serialize(manifest).Contains("test-anonymous-token"),"temporary public token never enters the manifest");
        var before=handler.Urls.Count;await catalog.ReadAsync(link!,manifest,_=>{},_=>{},default);
        Check.Equal(before,handler.Urls.Count,"complete saved metadata resumes without network refetch");
        using var denied=new PlaylistCatalog(new Http(_=>new(HttpStatusCode.Forbidden)));
        await Check.ThrowsAsync<InvalidDataException>(()=>denied.ReadAsync(link!,null,_=>{},_=>{},default),"private/blocked metadata cannot be treated as a successful empty import");
        using var shortCatalog=new PlaylistCatalog(new Http(req=>req.RequestUri!.Host=="spclient.wg.spotify.com"?Json(new{length=200,contents=new{items=Enumerable.Range(0,50).Select(i=>new{uri="spotify:track:"+Id(i)}),truncated=true}}):new(HttpStatusCode.OK){Content=new StringContent(Page(50))}));
        await Check.ThrowsAsync<InvalidDataException>(()=>shortCatalog.ReadAsync(link!,null,_=>{},_=>{},default),"truncated/ignored pagination is refused, instead of importing only the first 50");
        using var tooLarge=new PlaylistCatalog(new Http(_=>Json(new{length=10001,contents=new{items=Array.Empty<object>()}})));
        await Check.ThrowsAsync<InvalidDataException>(()=>tooLarge.ReadAsync(link!,null,_=>{},_=>{},default),"unsupported metadata response refused");
        var root=Check.NewFolder("manifest-first");using var account=new TelegramAccount(Path.Combine(root,"test.dat"),()=>"direct");
        var a=new Source("DeezLoad");var b=new Source("Music Hunters");var sources=new MusicSources(a,b,_=>{});using var downloads=new Downloads(_=>{});
        using var manager=new PlaylistTransfers(account,sources,downloads,Path.Combine(root,"playlists.json"),_=>false,new PlaylistCatalog(FullList(100)));
        var job=manager.Start(link!.Url,Path.Combine(root,"audio"));await manager.Step(job,default);
        await Check.UntilAsync(()=>downloads.Active.Count==0,"metadata-first transfer completes through fake sources");
        Check.Equal(100,job.Manifest!.Entries.Count,"all identities stored independently of provider results");
        Check.Equal(100,job.Enqueued.Count,"each individual recording queued once");
        Check.True(job.Complete,"complete metadata collection identified truthfully");
        await manager.Step(job,default);Check.Equal(100,a.Calls+b.Calls,"resuming an existing manifest does not ask either bot for completed songs");
    }
    private static async Task QueueFailures()
    {
        Check.Section("queue persistence, unsafe names and corrupt state");
        var root=Check.NewFolder("queue-safety");var queuePath=Path.Combine(root,"queue.json");File.WriteAllText(queuePath,"{broken");
        Check.Throws<InvalidDataException>(()=>new Downloads(_=>{},queuePath,_=>throw new Exception()),"corrupt queue stops safely instead of overwriting history");
        Check.Equal("{broken",File.ReadAllText(queuePath),"damaged queue retained unchanged");
        var bad=new BotTrack("Song","Artist",null,"../escape",async(s,_,_)=>{await s.WriteAsync(new byte[]{1});return "mp3";});
        await Check.ThrowsAsync<InvalidDataException>(()=>Downloader.DownloadAsync(bad,Path.Combine(root,"audio"),null,default),"path traversal is rejected before writing");
        var source=new Source("DeezLoad"){Fetch=async(_,s)=>{await s.WriteAsync(Encoding.UTF8.GetBytes("<html>not audio</html>"));return "flac";}};
        var sources=new MusicSources(source,new Source("Music Hunters"),_=>{});
        await Check.ThrowsAsync<InvalidDataException>(()=>Downloader.DownloadAsync(sources.Track(new("Spoof","Artist")),Path.Combine(root,"audio"),null,default),"a false FLAC extension cannot smuggle HTML into the library");
        Check.False(Directory.GetFiles(Path.Combine(root,"audio"),"*.flac").Any(),"invalid audio leaves no finished file");
    }
}
