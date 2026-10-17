using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FreeMusicFinder;

internal sealed class CatalogEntry
{
    public int Position { get; set; }
    public string Uri { get; set; } = "";
    public MusicRequest? Request { get; set; }
    public string Issue { get; set; } = "";
}
internal sealed class PlaylistManifest
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public int Total { get; set; }
    public bool CompleteIds { get; set; }
    public List<CatalogEntry> Entries { get; set; } = new();
}

/// <summary>Metadata only. A full ordered list is obtained before any bot is asked for audio.</summary>
internal sealed class PlaylistCatalog : IDisposable
{
    private readonly HttpClient _http;
    private readonly MetadataRoute? _route;
    public PlaylistCatalog(HttpMessageHandler? handler=null)
    {
        _route=handler as MetadataRoute;
        _http=new HttpClient(handler??new HttpClientHandler {AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(25)};
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctis-FreeMusicFinder/2.5");
    }
    /// <summary>Spotify and Deezer are reached the way the "Proxy" setting says, as Telegram is (<see cref="MetadataRoute"/>).</summary>
    internal static HttpMessageHandler MetadataHandler(string setting,Action<string>? log=null)=>new MetadataRoute(setting,log);
    public async Task<PlaylistManifest> ReadAsync(MusicLink link,PlaylistManifest? saved,Action<PlaylistManifest> checkpoint,Action<string> progress,CancellationToken ct)
    {
        var manifest=saved is {CompleteIds:true} && saved.Url==link.Url ? saved : new PlaylistManifest{Url=link.Url};
        if(link.Service=="deezer") return await Deezer(link,manifest,checkpoint,progress,ct).ConfigureAwait(false);
        Dictionary<string,MusicRequest> known=new();
        string? token=null;
        if(!manifest.CompleteIds)
        {
            var html=await Get("https://open.spotify.com/embed/"+link.Kind+"/"+link.Id,null,ct).ConfigureAwait(false);
            using var data=Embed(html);
            var entity=Entity(data.RootElement);
            manifest.Name=String(entity,"name",String(entity,"title","Spotify import"));
            if(entity.TryGetProperty("trackList",out var tracks) && tracks.ValueKind==JsonValueKind.Array)
                foreach(var track in tracks.EnumerateArray())
                    if(ParseTrack(track) is {} item && item.Url is {} url) known[url]=item;
            if(link.Kind=="track")
            {
                var request=ParseTrack(entity,link.Id)??throw new InvalidDataException("METADATA_UNAVAILABLE: Spotify did not provide this track's title and artist.");
                manifest.Entries=[new(){Position=1,Uri="spotify:track:"+link.Id,Request=request}];manifest.Total=1;
            }
            else if(link.Kind=="album")
            {
                manifest.Entries=known.Values.Select((item,index)=>new CatalogEntry{Position=index+1,Uri="spotify:track:"+new Uri(item.Url!).Segments.Last(),Request=item}).ToList();
                manifest.Total=manifest.Entries.Count;
                if(manifest.Total==0) throw new InvalidDataException("METADATA_UNAVAILABLE: Spotify returned an empty album or an unsupported page.");
            }
            else
            {
                token=Token(data.RootElement);
                if(string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("METADATA_INCOMPLETE: Spotify's preview cannot establish the full playlist. No audio requested; make the playlist accessible and retry.");
                var raw=await Get("https://spclient.wg.spotify.com/playlist/v2/playlist/"+link.Id,token,ct).ConfigureAwait(false);
                using var full=JsonDocument.Parse(raw);
                ReadIds(full.RootElement,manifest,0);
                while(manifest.Entries.Count<manifest.Total)
                {
                    var offset=manifest.Entries.Count;
                    using var next=JsonDocument.Parse(await Get("https://spclient.wg.spotify.com/playlist/v2/playlist/"+link.Id+"?from="+offset+"&length=500",token,ct).ConfigureAwait(false));
                    ReadIds(next.RootElement,manifest,offset);
                    if(manifest.Entries.Count<=offset) throw new InvalidDataException("METADATA_INCOMPLETE: Spotify did not return the next page. No audio requested.");
                }
                if(manifest.Entries.Count!=manifest.Total) throw new InvalidDataException("METADATA_INCOMPLETE: Spotify's playlist count changed or does not match the returned entries. No audio requested.");
                foreach(var entry in manifest.Entries)
                    if(known.TryGetValue(TrackUrl(entry.Uri),out var request)) entry.Request=request;
            }
            manifest.CompleteIds=true;checkpoint(manifest);
        }
        var pending=manifest.Entries.Where(e=>e.Request is null && e.Uri.StartsWith("spotify:track:",StringComparison.Ordinal)).ToArray();
        var done=manifest.Entries.Count(e=>e.Request is not null);var checkpointGate=new object();
        await Parallel.ForEachAsync(pending,new ParallelOptions{MaxDegreeOfParallelism=4,CancellationToken=ct},async(entry,cancel)=>
        {
            try
            {
                var id=entry.Uri["spotify:track:".Length..];
                using var data=Embed(await Get("https://open.spotify.com/embed/track/"+id,null,cancel).ConfigureAwait(false));
                entry.Request=ParseTrack(Entity(data.RootElement),id);
                entry.Issue=entry.Request is null?"METADATA_UNAVAILABLE: this track has no usable title/artist.":"";
            }
            catch(Exception ex) when(ex is InvalidDataException or HttpRequestException or TimeoutException)
            {entry.Issue=ex.Message;}
            lock(checkpointGate) {done++;checkpoint(manifest);progress($"Reading track metadata: {done} of {manifest.Total}");}
        }).ConfigureAwait(false);
        foreach(var entry in manifest.Entries)
        {
            if(entry.Request is null && entry.Uri.StartsWith("spotify:local:"))
            {
                entry.Request=ParseLocal(entry.Uri,manifest.Entries.Where(e=>e.Request is not null).SelectMany(e=>e.Request!.Artist.Split(',',StringSplitOptions.TrimEntries)));
                entry.Issue=entry.Request is null?"LOCAL_METADATA_MISSING: this local-file entry has no unambiguous artist/title; no substitute will be guessed.":"";
            }
            if(entry.Request is null && !entry.Uri.StartsWith("spotify:track:") && !entry.Uri.StartsWith("spotify:local:")) entry.Issue="UNSUPPORTED_ENTRY: this is a podcast/episode, not a catalog music track. Its identifier is retained; no song substitute will be guessed.";
            if(entry.Request is {} request) entry.Request=request with {Playlist=manifest.Url,Position=entry.Position};
        }
        checkpoint(manifest);return manifest;
    }
    internal static MusicRequest? ParseLocal(string uri,IEnumerable<string> knownArtists)
    {
        var parts=uri.Split(':');if(parts.Length!=6 || parts[0]!="spotify" || parts[1]!="local")return null;
        var artist=WebUtility.UrlDecode(parts[2]);var title=WebUtility.UrlDecode(parts[4]);
        if(artist.Length==0)
        {
            var delimiter=title.IndexOf(" - ",StringComparison.Ordinal);
            if(delimiter>0){artist=title[..delimiter].Trim();title=title[(delimiter+3)..].Trim();}
            else
            {
                var candidate=knownArtists.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(a=>a.Length)
                    .FirstOrDefault(a=>a.Length>=3 && title.StartsWith(a+" ",StringComparison.OrdinalIgnoreCase));
                if(candidate is not null){artist=candidate;title=title[candidate.Length..].Trim();}
            }
        }
        if(artist.Length==0 || title.Length==0 || artist.Length>1000 || title.Length>1000 || artist.Any(char.IsControl) || title.Any(char.IsControl))return null;
        double? duration=double.TryParse(parts[5],System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var seconds)&&seconds>0&&seconds<=86400?seconds:null;
        return new(title,artist,duration,OriginUri:uri);
    }
    internal static void ReadIds(JsonElement root,PlaylistManifest manifest,int offset)
    {
        if(!root.TryGetProperty("length",out var length) || !length.TryGetInt32(out var total) || total<0 || total>10000) throw new InvalidDataException("METADATA_INCOMPLETE: invalid playlist count.");
        if(offset>0 && total!=manifest.Total) throw new InvalidDataException("PLAYLIST_CHANGED: its size changed while reading metadata. Retry before requesting audio.");
        if(!root.TryGetProperty("contents",out var contents) || !contents.TryGetProperty("items",out var items) || items.ValueKind!=JsonValueKind.Array) throw new InvalidDataException("METADATA_INCOMPLETE: no ordered track list.");
        if(offset>0 && (!contents.TryGetProperty("pos",out var pos) || !pos.TryGetInt32(out var actual) || actual!=offset)) throw new InvalidDataException("METADATA_INCOMPLETE: Spotify did not confirm the requested page position.");
        manifest.Total=total;
        foreach(var item in items.EnumerateArray())
        {
            var uri=String(item,"uri","");
            if(uri.Length>1000 || uri.Length==0) throw new InvalidDataException("METADATA_INCOMPLETE: a playlist entry has no identifier.");
            if(uri.StartsWith("spotify:track:") && !Regex.IsMatch(uri,@"^spotify:track:[A-Za-z0-9]{22}$")) throw new InvalidDataException("METADATA_INCOMPLETE: invalid Spotify track identifier.");
            manifest.Entries.Add(new(){Position=manifest.Entries.Count+1,Uri=uri});
        }
    }
    internal static JsonDocument Embed(string html)
    {
        var match=Regex.Match(html,@"<script\b[^>]*\bid=[""']__NEXT_DATA__[""'][^>]*>(.*?)</script>",RegexOptions.Singleline);
        if(!match.Success) throw new InvalidDataException("METADATA_UNAVAILABLE: Spotify returned a blocked or unsupported page.");
        try{return JsonDocument.Parse(match.Groups[1].Value,new JsonDocumentOptions{MaxDepth=96});}
        catch(JsonException ex){throw new InvalidDataException("METADATA_UNAVAILABLE: Spotify's embedded data could not be read.",ex);}
    }
    private static JsonElement Entity(JsonElement root)
    {
        foreach(var path in new[]{new[]{"props","pageProps","state","data","entity"},new[]{"props","pageProps","data","entity"},new[]{"props","pageProps","entity"}})
            if(Path(root,path) is {} value && value.ValueKind==JsonValueKind.Object) return value;
        throw new InvalidDataException("METADATA_UNAVAILABLE: Spotify did not provide the requested item; it may be private, removed or blocked in this region.");
    }
    private static string? Token(JsonElement root)
    {
        foreach(var path in new[]{new[]{"props","pageProps","state","settings","session","accessToken"},new[]{"props","pageProps","settings","session","accessToken"},new[]{"props","pageProps","session","accessToken"}})
            if(Path(root,path) is {} value && value.ValueKind==JsonValueKind.String) return value.GetString();
        return null;
    }
    private static JsonElement? Path(JsonElement root,string[] path)
    {foreach(var key in path) {if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty(key,out root))return null;}return root;}
    internal static MusicRequest? ParseTrack(JsonElement track,string? id=null)
    {
        var uri=String(track,"uri",id is null?"":"spotify:track:"+id);
        if(!Regex.IsMatch(uri,@"^spotify:track:[A-Za-z0-9]{22}$")) return null;
        var title=String(track,"title",String(track,"name",""));var artist=String(track,"subtitle","");
        if(track.TryGetProperty("artists",out var artists) && artists.ValueKind==JsonValueKind.Array)
            artist=string.Join(", ",artists.EnumerateArray().Select(a=>String(a,"name","")).Where(s=>s.Length>0));
        if(title.Length==0 || artist.Length==0 || title.Length>1000 || artist.Length>1000 || title.Any(char.IsControl) || artist.Any(char.IsControl)) return null;
        double? seconds=track.TryGetProperty("duration",out var duration) && duration.TryGetDouble(out var ms) && ms>0 && ms<=86400000?ms/1000:null;
        bool? explicitFlag=track.TryGetProperty("isExplicit",out var explicitValue) && explicitValue.ValueKind is JsonValueKind.True or JsonValueKind.False?explicitValue.GetBoolean():null;
        return new(title,artist,seconds,TrackUrl(uri),explicitFlag);
    }
    private static string TrackUrl(string uri)=>uri.StartsWith("spotify:track:")?"https://open.spotify.com/track/"+uri["spotify:track:".Length..]:"";
    private static string String(JsonElement element,string key,string fallback)
        =>element.ValueKind==JsonValueKind.Object && element.TryGetProperty(key,out var value) && value.ValueKind==JsonValueKind.String?value.GetString()??fallback:fallback;
    private async Task<string> Get(string url,string? token,CancellationToken ct)
    {
        var uri=new Uri(url);
        if(uri.Scheme!="https" || uri.Host is not ("open.spotify.com" or "spclient.wg.spotify.com" or "api.deezer.com")) throw new InvalidDataException("Unapproved metadata host.");
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        if(token is not null)
        {if(uri.Host!="spclient.wg.spotify.com")throw new InvalidDataException("Unapproved token destination.");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);request.Headers.Accept.ParseAdd("application/json");}
        HttpResponseMessage response;
        try { response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) {throw new TimeoutException("METADATA_TIMEOUT: "+(_route?.Unanswered(uri)??"the metadata service did not respond in 25 seconds."));}
        using var responseScope=response;
        if(response.StatusCode==(HttpStatusCode)429)
        {var until=response.Headers.RetryAfter?.Date??DateTimeOffset.UtcNow.Add(response.Headers.RetryAfter?.Delta??TimeSpan.FromMinutes(10));throw new SourcesWaitingException((uri.Host=="api.deezer.com"?"Deezer":"Spotify")+" metadata requests are limited; progress saved until its retry time.",until);}
        if(!response.IsSuccessStatusCode) throw new InvalidDataException($"METADATA_HTTP_{(int)response.StatusCode}: cannot read the full list. Make the playlist accessible; if region-blocked, check your VPN/proxy and retry.");
        if(response.Content.Headers.ContentLength>4*1024*1024) throw new InvalidDataException("METADATA_TOO_LARGE: response exceeded 4 MB.");
        // The client's timeout ends when the headers arrive. Reading the rest gets 25 seconds of its own, so a reply
        // that stalls after its headers (a VPN that drops the connection without closing it) cannot hang an import.
        using var reading=CancellationTokenSource.CreateLinkedTokenSource(ct);reading.CancelAfter(_http.Timeout);
        try
        {
            await using var body=await response.Content.ReadAsStreamAsync(reading.Token).ConfigureAwait(false);using var buffer=new MemoryStream();var bytes=new byte[16384];
            while(true){var count=await body.ReadAsync(bytes,reading.Token).ConfigureAwait(false);if(count==0)break;if(buffer.Length+count>4*1024*1024)throw new InvalidDataException("METADATA_TOO_LARGE");buffer.Write(bytes,0,count);}
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested)
        {throw new TimeoutException("METADATA_TIMEOUT: "+(_route?.Unanswered(uri)??"the metadata service did not finish its answer in 25 seconds."));}
    }
    private async Task<PlaylistManifest> Deezer(MusicLink link,PlaylistManifest manifest,Action<PlaylistManifest> checkpoint,Action<string> progress,CancellationToken ct)
    {
        if(manifest.CompleteIds)return manifest;
        using var first=JsonDocument.Parse(await Get("https://api.deezer.com/"+link.Kind+"/"+link.Id,null,ct).ConfigureAwait(false));
        var root=first.RootElement;manifest.Name=String(root,"title","Deezer import");
        manifest.Total=link.Kind=="track"?1:root.TryGetProperty("nb_tracks",out var n)&&n.TryGetInt32(out var count)?count:0;
        if(manifest.Total<0 || manifest.Total>10000) throw new InvalidDataException("METADATA_INCOMPLETE: invalid Deezer count.");
        if(link.Kind=="track")Add(root);else
        {
            if(!root.TryGetProperty("tracks",out var page))throw new InvalidDataException("METADATA_UNAVAILABLE: Deezer did not provide a track list.");
            JsonDocument? nextDoc=null;
            try
            {
                for(var pages=0;pages<200;pages++)
                {
                    if(!page.TryGetProperty("data",out var items) || items.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("METADATA_INCOMPLETE: invalid Deezer page.");
                    foreach(var track in items.EnumerateArray())Add(track);
                    var next=String(page,"next","");if(next.Length==0)break;
                    var nextUri=new Uri(next);if(nextUri.Host!="api.deezer.com" || !nextUri.AbsolutePath.Contains("/"+link.Id+"/tracks"))throw new InvalidDataException("METADATA_INCOMPLETE: unsafe next page.");
                    nextDoc?.Dispose();nextDoc=JsonDocument.Parse(await Get(new UriBuilder(nextUri){Scheme="https",Port=-1}.Uri.AbsoluteUri,null,ct).ConfigureAwait(false));page=nextDoc.RootElement;
                }
            }
            finally{nextDoc?.Dispose();}
        }
        if(manifest.Entries.Count!=manifest.Total)throw new InvalidDataException("METADATA_INCOMPLETE: Deezer returned only part of the list. No audio requested.");
        manifest.CompleteIds=true;checkpoint(manifest);progress($"Full metadata list saved: {manifest.Total} entries");return manifest;
        void Add(JsonElement item)
        {
            if(!item.TryGetProperty("id",out var id)||!id.TryGetInt64(out var number)||number<=0)throw new InvalidDataException("METADATA_INCOMPLETE: invalid Deezer track ID.");
            var title=String(item,"title","");var artist=item.TryGetProperty("artist",out var a)?String(a,"name",""):"";
            if(title.Length==0 || artist.Length==0)throw new InvalidDataException("METADATA_INCOMPLETE: missing title/artist.");
            double? seconds=item.TryGetProperty("duration",out var d)&&d.TryGetDouble(out var sec)?sec:null;
            bool? explicitFlag=item.TryGetProperty("explicit_lyrics",out var e)&&e.ValueKind is JsonValueKind.True or JsonValueKind.False?e.GetBoolean():null;
            var position=manifest.Entries.Count+1;manifest.Entries.Add(new(){Position=position,Uri="deezer:track:"+number,Request=new(title,artist,seconds,"https://www.deezer.com/track/"+number,explicitFlag,manifest.Url,position)});
        }
    }
    public void Dispose()=>_http.Dispose();
}
