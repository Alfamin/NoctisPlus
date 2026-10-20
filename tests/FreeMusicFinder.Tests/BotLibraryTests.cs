using System.Text;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Owner #105: the songs the bots already sent are found in a local index and taken from the chat, never asked
/// for again. A fake Telegram only.</summary>
internal static class BotLibraryTests
{
    internal sealed class Chats : IBotChats
    {
        public bool Ready { get; set; } = true;
        public readonly Dictionary<string, List<Message>> History = new() { [TelegramBotSource.Bot] = [], [DeezLoadSource.Bot] = [] };
        public int Pages, Reads, Downloads;
        public bool ReferenceExpired;
        public Task<IReadOnlyList<Message>> SongsAsync(string bot, int olderThan, CancellationToken ct)
        {
            Pages++;
            return Task.FromResult<IReadOnlyList<Message>>(History[bot].Where(m => olderThan == 0 || m.id < olderThan)
                .Where(m => m.media is MessageMediaDocument).OrderByDescending(m => m.id).Take(100).ToArray());
        }
        public Task<Message?> MessageAsync(string bot, int id, CancellationToken ct)
        { Reads++; return Task.FromResult(History[bot].FirstOrDefault(m => m.id == id)); }
        public async Task DownloadAsync(Document document, Stream output, IProgress<double>? progress, CancellationToken ct)
        {
            Downloads++;
            if (ReferenceExpired && document.file_reference is [1]) throw new RpcException(400, "FILE_REFERENCE_EXPIRED");
            await output.WriteAsync(Encoding.ASCII.GetBytes("fLaC file " + document.id), ct);
        }
        public void Send(string bot, int id, string artist, string title, int seconds = 200, string ext = "flac", byte reference = 1)
            => History[bot].Add(new Message
            {
                id = id,
                media = new MessageMediaDocument
                {
                    document = new Document
                    {
                        id = id * 10, access_hash = id, file_reference = [reference], size = 1000, mime_type = ext == "flac" ? "audio/flac" : "audio/mpeg",
                        attributes = [new DocumentAttributeAudio { flags = DocumentAttributeAudio.Flags.has_title | DocumentAttributeAudio.Flags.has_performer, title = title, performer = artist, duration = seconds },
                            new DocumentAttributeFilename { file_name = artist + " - " + title + "." + ext }],
                    },
                },
            });
    }

    public static async Task RunAsync()
    {
        Check.Section("the songs the bots already sent (owner #105)");
        BotLibrary.PagePause = TimeSpan.Zero;
        var folder = Check.NewFolder("bot-songs"); var file = Path.Combine(folder, "bot-songs.json");
        var chats = new Chats();
        for (var i = 1; i <= 250; i++) chats.Send(TelegramBotSource.Bot, i * 2, "Artist " + i, "Song " + i);
        chats.History[TelegramBotSource.Bot].Add(new Message { id = 1000, message = "Please wait for the current download" });
        chats.Send(TelegramBotSource.Bot, 1002, "Drake", "Look Alive (feat. Drake)", 181);
        chats.Send(DeezLoadSource.Bot, 7, "J. Cole", "Photograph", 230);
        chats.Send(DeezLoadSource.Bot, 9, "J. Cole", "The Cut Off", 180, ext: "mp3");
        chats.Send(DeezLoadSource.Bot, 11, "A$AP Rocky", "I Smoked Away My Brain (I'm God x Demons Mashup) (feat. Imogen H", 248);

        var library = new BotLibrary(file, chats);
        await library.SyncAsync();
        Check.Equal(254, library.Count, "every song file of both chats is indexed, the whole history (more than one page)");
        Check.True(library.UpToDate(TelegramBotSource.Bot) && library.UpToDate(DeezLoadSource.Bot), "and both chats are up to date");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++) library.Find(new MusicRequest("Song 137", "Artist 137", 200));
        Check.True(clock.ElapsedMilliseconds < 2000, $"a lookup takes microseconds ({clock.Elapsed.TotalMilliseconds * 100:0} ns each, 10,000 in {clock.ElapsedMilliseconds} ms)");
        Check.Equal("Look Alive (feat. Drake)", library.Find(new MusicRequest("Look Alive", "BlocBoy JB, Drake", 181)).Single().Title, "a song is found whatever credits its title carries");
        Check.Equal(1, library.Find(new MusicRequest("I Smoked Away My Brain (I'm God x Demons Mashup) (feat. Imogen Heap & Clams Casino)", "A$AP Rocky", 248)).Count,
            "a title Telegram cut short is found by its words");
        Check.Equal(0, library.Find(new MusicRequest("Photograph", "J. Cole", 230), ext => ext == "mp3").Count, "a format the request does not take is not offered");
        Check.Equal(0, library.Find(new MusicRequest("The Cut Off", "J. Cole", 180), ext => ext == "flac").Count, "an MP3 is no FLAC");
        Check.Equal(0, library.Find(new MusicRequest("Photograph", "Someone Else", 230)).Count, "another artist's song of the same name is not this one");

        // An ask ahead for a song the chat has: nothing is sent, nothing is read.
        var reads = chats.Reads; var downloads = chats.Downloads;
        var asked = new AskOnlyStream();
        Check.Equal("flac", await library.FetchAsync(new MusicRequest("Photograph", "J. Cole", 230), _ => true, asked, null, default), "an ask ahead for a song in a chat is answered at once");
        Check.True(asked.Found?.id == 70 && chats.Reads == reads && chats.Downloads == downloads, "with no Telegram call at all");

        using (var output = new MemoryStream())
        {
            Check.Equal("flac", await library.FetchAsync(new MusicRequest("Photograph", "J. Cole", 230), _ => true, output, null, default), "a song in a chat is taken from there");
            Check.True(chats.Reads == reads && chats.Downloads == downloads + 1 && Encoding.ASCII.GetString(output.ToArray()) == "fLaC file 70", "with the saved reference: one transfer, no other call");
        }
        using (var output = new MemoryStream())
            Check.Equal((string?)null, await library.FetchAsync(new MusicRequest("Not There", "Nobody", 200), _ => true, output, null, default), "a song no chat has is left to the bots");

        // A message deleted since it was indexed, once its reference is no longer fresh.
        BotLibrary.FreshFor = TimeSpan.Zero;
        chats.History[DeezLoadSource.Bot].RemoveAll(m => m.id == 7);
        using (var output = new MemoryStream())
        {
            Check.Equal((string?)null, await library.FetchAsync(new MusicRequest("Photograph", "J. Cole", 230), _ => true, output, null, default), "a deleted message is found out");
            Check.Equal(0, library.Find(new MusicRequest("Photograph", "J. Cole", 230)).Count, "and leaves the index; the bots are asked as before");
        }
        // A reference that ran out: the message is read again and the file transferred.
        BotLibrary.FreshFor = TimeSpan.FromHours(1);
        chats.ReferenceExpired = true;
        chats.History[TelegramBotSource.Bot].RemoveAll(m => m.id == 1002);
        chats.Send(TelegramBotSource.Bot, 1002, "Drake", "Look Alive (feat. Drake)", 181, reference: 2);
        using (var output = new MemoryStream())
        {
            reads = chats.Reads;
            Check.Equal("flac", await library.FetchAsync(new MusicRequest("Look Alive", "BlocBoy JB, Drake", 181), _ => true, output, null, default), "an expired reference is renewed");
            Check.True(chats.Reads == reads + 1 && output.Length > 0, "with one read of that message");
            chats.ReferenceExpired = false;
        }

        // New files: read on the next start (asked while Noctis was closed) or as soon as a bot sends one.
        chats.Send(DeezLoadSource.Bot, 20, "Kendrick Lamar", "N95", 195);
        var pages = chats.Pages;
        library.Touch(DeezLoadSource.Bot);
        await library.SyncAsync(DeezLoadSource.Bot);
        Check.Equal(1, library.Find(new MusicRequest("N95", "Kendrick Lamar", 195)).Count, "a file a bot just sent is in the index");
        Check.True(chats.Pages - pages <= 2, "read from the newest message down to the known ones only");

        // The bots send the same file again for every ask: one entry per recording, its newest copy.
        var before = library.Count;
        chats.Send(TelegramBotSource.Bot, 1050, "Future", "Mask Off", 204);
        chats.Send(TelegramBotSource.Bot, 1052, "Future", "Mask Off", 204);
        chats.Send(TelegramBotSource.Bot, 1054, "Future", "Mask Off", 204);
        chats.Send(DeezLoadSource.Bot, 30, "Future", "Mask Off", 204);
        chats.Send(TelegramBotSource.Bot, 1056, "Future", "Mask Off (Remix)", 210);
        await library.SyncAsync(TelegramBotSource.Bot); await library.SyncAsync(DeezLoadSource.Bot); // the Hunters copies first
        var masks = library.Find(new MusicRequest("Mask Off", "Future", 204));
        Check.True(masks.Count == 1 && masks[0].Message == 1054, "a recording sent several times is listed once, as its newest copy");
        Check.Equal(before + 2, library.Count, "another version (the remix) is its own entry");

        var reopened = new BotLibrary(file, chats);
        Check.Equal(library.Count, reopened.Count, "the index is kept on this PC");
        Check.Equal(1, reopened.Find(new MusicRequest("Look Alive", "Drake", 181)).Count, "and works before Telegram is read again");
        chats.Send(TelegramBotSource.Bot, 1100, "SZA", "Saturn", 186);
        await reopened.SyncAsync();
        Check.Equal(1, reopened.Find(new MusicRequest("Saturn", "SZA", 186)).Count, "the next start adds what was sent meanwhile");

        await Routing(chats, reopened);
    }

    // The queue's next songs: some in a chat, some not. Hits never reach a bot; misses are asked as before.
    private static async Task Routing(Chats chats, BotLibrary library)
    {
        var hunters = new TransferTests.Source("Music Hunters"); var deezLoad = new TransferTests.Source("DeezLoad");
        var sources = new MusicSources(hunters, deezLoad, _ => { }) { Chats = library };
        using (var hit = new MemoryStream())
            Check.Equal("flac", await sources.FetchAsync(new MusicRequest("Saturn", "SZA", 186), hit, null, default), "a song in a chat downloads from there");
        Check.Equal(0, hunters.Calls + deezLoad.Calls, "no bot is asked for it");
        using (var miss = new MemoryStream())
            Check.Equal("flac", await sources.FetchAsync(new MusicRequest("Brand New", "Someone", 200), miss, null, default), "a song in no chat is asked for");
        Check.Equal(1, hunters.Calls + deezLoad.Calls, "from one bot, as before");
        var asked = new AskOnlyStream();
        await sources.FetchAsync(new MusicRequest("N95", "Kendrick Lamar", 195), asked, null, default);
        Check.True(asked.Found is not null && hunters.Calls + deezLoad.Calls == 1, "an ask ahead for a song in a chat sends nothing");
        chats.Ready = false;
        using (var offline = new MemoryStream())
            await sources.FetchAsync(new MusicRequest("Saturn", "SZA", 186), offline, null, default);
        Check.Equal(2, hunters.Calls + deezLoad.Calls, "signed out, the bots are asked as before");
        chats.Ready = true;
    }
}
