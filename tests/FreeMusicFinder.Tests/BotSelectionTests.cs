using System.Text;
using System.Text.Json;
using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

internal static class BotSelectionTests
{
    private static Message Results(int id=40)=>new()
    {
        id=id,message="1. Artist - Rottweiler (3:20)\n2. Other Artist - Rottweiler (3:20)\n3. Artist - Rottweiler (Remix) (3:21)",
        reply_markup=new ReplyInlineMarkup {rows=[new KeyboardInlineButtonRow {buttons=Enumerable.Range(1,3)
            .Select(i=>new KeyboardInlineButton {text=i.ToString(),type=new InlineButtonTypeCallback {data=[0,(byte)i,255,17]}}).ToArray()}]},
    };
    private sealed class Hunter : IHunterBotSource
    {
        public Message Current=Results();public Message Fresh=Results(50);
        public List<string> Operations=new();
        public Exception? Failure;
        public Task<IReadOnlyList<BotTrack>> SearchAsync(string query,CancellationToken ct)
        {
            Operations.Add("search:"+query);Current=Fresh;
            return Task.FromResult(TelegramBotSource.ParseResults(Current,FetchSelectedAsync));
        }
        public async Task<string> FetchSelectedAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var data=TelegramBotSource.ValidateSelection(request,Current);
            Operations.Add("press:"+request.Selection!.MessageId+":"+request.Selection.Number+":"+data[1]);
            if(Failure is not null)throw Failure;
            await output.WriteAsync(Encoding.ASCII.GetBytes("fLaCselected "+request.Selection.Number),ct);return "flac";
        }
    }
    private sealed class Source(string id,IReadOnlyList<MusicRequest> results) : IMusicSource
    {
        public string Id=>id;public int Fetches;
        public Task<IReadOnlyList<MusicRequest>> SearchAsync(string query,CancellationToken ct)=>Task.FromResult(results);
        public async Task<string> FetchAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct)
        {Fetches++;await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC source"),ct);return "flac";}
    }
    public static async Task RunAsync()
    {
        await ExactSelection();Validation();await StaleSelection();await Routing();await SavedSelection();DeezMatching();
    }
    private static async Task ExactSelection()
    {
        Check.Section("Music Hunters preserves each selected numbered callback without a second search");
        var bot=new Hunter();var hunter=new HunterMusicSource(bot);
        var found=await hunter.SearchAsync("Rottweiler",default);
        Check.Equal(3,found.Count,"all three numbered results retained");
        Check.Equal("3",found[2].Selection!.Number,"third result retains third button");
        Check.Equal(BotSelections.Hunters,found[2].Selection!.Provider,"selection carries its provider");
        Check.Equal(TelegramBotSource.Bot,found[2].Selection!.Bot,"selection carries its bot username");
        Check.Equal("Music Hunters",found[2].PreferredSource,"search results prefer originating source");
        using var output=new MemoryStream();
        Check.Equal("flac",await hunter.FetchAsync(found[2],output,null,default),"selected result downloads automatically");
        Check.Equal("search:Rottweiler,press:50:3:3",string.Join(",",bot.Operations),"one search followed by exact third callback, no repeat search");
        Check.Equal("fLaCselected 3",Encoding.ASCII.GetString(output.ToArray()),"selected third recording supplied");
        bot.Operations.Clear();output.SetLength(0);
        await hunter.FetchAsync(found[0],output,null,default);
        Check.Equal("press:50:1:1",string.Join(",",bot.Operations),"first result presses first callback, not the last selected option");
    }
    private static void Validation()
    {
        Check.Section("callbacks are bound to incoming message, bot, number, bytes and recording");
        var message=Results();var request=TelegramBotSource.ParseResults(message,(_,_,_,_)=>Task.FromResult("flac"))[0].Request!;
        Check.Equal(4,TelegramBotSource.ValidateSelection(request,message).Length,"binary callback bytes preserved including zero and 255");
        Check.Throws<StaleSelectionException>(()=>TelegramBotSource.ValidateSelection(request,null),"deleted message cannot be pressed");
        Check.Throws<StaleSelectionException>(()=>TelegramBotSource.ValidateSelection(request,Results(41)),"another message cannot share callback by coincidence");
        var outgoing=Results();outgoing.flags|=Message.Flags.out_;
        Check.Throws<StaleSelectionException>(()=>TelegramBotSource.ValidateSelection(request,outgoing),"outgoing message cannot act as bot result");
        var changed=Results();changed.message="1. Other Artist - Rottweiler (3:20)";
        Check.Throws<StaleSelectionException>(()=>TelegramBotSource.ValidateSelection(request,changed),"edited first option cannot download a different artist");
        var swapped=Results();((InlineButtonTypeCallback)((ReplyInlineMarkup)swapped.reply_markup).rows[0].buttons[0].type).data=[9];
        Check.Throws<StaleSelectionException>(()=>TelegramBotSource.ValidateSelection(request,swapped),"changed callback cannot be blindly pressed");
        foreach(var selection in new[]{request.Selection! with {Provider="DeezLoad"},request.Selection! with {Bot=DeezLoadSource.Bot},
            request.Selection! with {MessageId=0},request.Selection! with {Number="GET ALL"},request.Selection! with {Data="not base64"},
            request.Selection! with {Data=Convert.ToBase64String(new byte[65])}})
            Check.Throws<StaleSelectionException>(()=>BotSelections.DecodeHunter(selection),"invalid/foreign selection refused");
        Check.True(TelegramBotSource.IsStaleCallback("MESSAGE_ID_INVALID"),"expired Telegram selection requests fresh result");
        Check.False(TelegramBotSource.IsStaleCallback("FLOOD_WAIT_60"),"rate limit does not trigger extra searches");
        Check.False(TelegramBotSource.IsStaleCallback("AUTH_KEY_UNREGISTERED"),"login failure does not trigger extra searches");
    }
    private static async Task StaleSelection()
    {
        Check.Section("stale selection refreshes once, selects exact metadata and never loops");
        var bot=new Hunter();var request=TelegramBotSource.ParseResults(bot.Current,bot.FetchSelectedAsync)[2].Request!;
        bot.Current=Results(41);var hunter=new HunterMusicSource(bot);
        using var output=new MemoryStream();
        await hunter.FetchAsync(request,output,null,default);
        Check.Equal("search:Artist Rottweiler (Remix),press:50:3:3",string.Join(",",bot.Operations),"expired third result refreshes once and presses matching third result");
        var missing=new Hunter();missing.Fresh=Results(50);missing.Fresh.message="1. Other Artist - Rottweiler (3:20)";
        missing.Current=Results(42);var noMatch=new HunterMusicSource(missing);
        await Check.ThrowsAsync<TrackUnavailableException>(()=>noMatch.FetchAsync(request,output,null,default),"another artist is not chosen when exact result vanished");
        Check.Equal(1,missing.Operations.Count,"missing match makes one search and zero button presses");
        var quota=new Hunter {Failure=new SourceLimitException("Music Hunters","quota",DateTimeOffset.UtcNow.AddHours(1))};
        var quotaRequest=TelegramBotSource.ParseResults(quota.Current,quota.FetchSelectedAsync)[0].Request!;
        await Check.ThrowsAsync<SourceLimitException>(()=>new HunterMusicSource(quota).FetchAsync(quotaRequest,output,null,default),"callback quota passed to source router");
        Check.Equal(1,quota.Operations.Count,"quota is one press with no repeated search");
    }
    private static async Task Routing()
    {
        Check.Section("provider-specific results stay separate and fallback receives metadata only");
        var bot=new Hunter();var hunter=new HunterMusicSource(bot);var deez=new TransferTests.Source("DeezLoad");
        var sources=new MusicSources(deez,hunter,_=>{});
        // Default source order is deliberately opposite the selected result's provider.
        deez.Fetch=(_,_)=>throw new Exception("Wrong provider used before selected source");
        var selected=(await hunter.SearchAsync("Rottweiler",default))[1];
        using var output=new MemoryStream();await sources.FetchAsync(selected,output,null,default);
        Check.Equal(0,deez.Calls,"Music Hunters callback stays with Music Hunters despite source order");
        Check.Equal("press:50:2:2",bot.Operations.Last(),"second selected option pressed at correct source");
        MusicRequest? received=null;
        deez.Fetch=async(r,s)=>{received=r;await s.WriteAsync(Encoding.ASCII.GetBytes("fLaCbackup"));return "flac";};
        bot.Failure=new TrackUnavailableException("Missing selected file");
        await sources.FetchAsync(selected with {MessageId=90,CachedFrom="Music Hunters"},output,null,default);
        Check.True(received is not null && received.Selection is null && received.MessageId is null && received.CachedFrom is null,"fallback never receives another bot's callback or message ID");
        Check.Equal(selected.Title,received!.Title,"fallback retains wanted title");
        Check.Equal(selected.Artist,received.Artist,"fallback retains wanted artist");
        var hunterPrimary=new TransferTests.Source("Music Hunters");
        hunterPrimary.Fetch=(_,_)=>throw new Exception("Should not fetch a DeezLoad result first");
        var searchRouter=new MusicSources(hunterPrimary,deez,_=>{});
        var bound=new MusicRequest("Rottweiler","Artist",Url:"https://www.deezer.com/track/5",PreferredSource:"DeezLoad");
        await searchRouter.FetchAsync(bound,output,null,default);
        Check.Equal(0,hunterPrimary.Calls,"DeezLoad result uses its track-link handler before Music Hunters");
        var filtered=BotSelections.ForSource(selected with {CachedFrom="DeezLoad",MessageId=7},"Music Hunters");
        Check.True(filtered.Selection is not null && filtered.MessageId is null,"owned selection kept but foreign cached message removed");
        var unavailable=new Source("Music Hunters",[]);
        var backupSearch=new Source("DeezLoad",[new MusicRequest("Rottweiler","Artist",Url:"https://www.deezer.com/track/5")]);
        var backupRouter=new MusicSources(unavailable,backupSearch,_=>{});
        var fromBackup=(await backupRouter.SearchAsync("Rottweiler",default)).Single();
        Check.Equal("DeezLoad",fromBackup.Request!.PreferredSource,"backup search stamps provider on raw result without a prior preference");
        await fromBackup.Fetch(output,null,default);
        Check.Equal(0,unavailable.Fetches,"downloading backup search result does not start another primary search flow");
        Check.Equal(1,backupSearch.Fetches,"originating backup handler fetches the selection");
    }
    private static async Task SavedSelection()
    {
        Check.Section("saved queue retains exact selection across restart without another search");
        var folder=Check.NewFolder("selected-button-restart");var file=Path.Combine(folder,"queue.json");
        var bot=new Hunter();var hunter=new HunterMusicSource(bot);var sources=new MusicSources(hunter,new TransferTests.Source("DeezLoad"),_=>{});
        var request=(await hunter.SearchAsync("Rottweiler",default))[1];
        var roundtrip=JsonSerializer.Deserialize<MusicRequest>(JsonSerializer.Serialize(request));
        Check.Equal(request.Selection,roundtrip!.Selection,"selection metadata serializes without loss");
        using(var queue=new Downloads(_=>{},file,sources.Track))
        {queue.SetPaused(true);queue.Start(sources.Track(request),folder);queue.Dispose();await Task.Delay(100);}
        bot.Operations.Clear();
        using(var restored=new Downloads(_=>{},file,sources.Track))
        {
            Check.Equal("2",restored.Active.Single().Track.Request!.Selection!.Number,"second option remains selected after restart");
            restored.SetPaused(false);await Check.UntilAsync(()=>restored.Active.Count==0,"saved selected song completes");
            Check.Equal("press:50:2:2",string.Join(",",bot.Operations),"restart presses stored option without a new search");
        }
        var legacy=new MusicRequest("Rottweiler","Artist");bot.Operations.Clear();
        using var output=new MemoryStream();await hunter.FetchAsync(legacy,output,null,default);
        Check.Equal("search:Artist Rottweiler,press:50:1:1",string.Join(",",bot.Operations),"old queued metadata requests search once and select automatically");
    }
    private static void DeezMatching()
    {
        Check.Section("DeezLoad inline search chooses matching artist/version instead of first link");
        static BotInlineResult Inline(string title,string artist,int id)=>new()
        {title=title,description="Artist: "+artist,send_message=new BotInlineMessageText {message="https://www.deezer.com/track/"+id}};
        var wanted=new MusicRequest("Rottweiler","Artist",200);
        var results=new[]{Inline("Rottweiler","Wrong artist",1),Inline("Rottweiler (Live)","Artist",2),Inline("Rottweiler","Artist",3)};
        Check.Equal("https://www.deezer.com/track/3",DeezLoadSource.MatchingInlineLink(wanted,results),"third matching track link selected automatically");
        Check.Equal<string?>(null,DeezLoadSource.MatchingInlineLink(wanted,results.Take(2)),"no unrelated first-link fallback");
        Check.Equal<string?>(null,DeezLoadSource.MatchingInlineLink(wanted,[Inline("Rottweiler…","Artist",4)]),"truncated titles are not selected");
    }
}
