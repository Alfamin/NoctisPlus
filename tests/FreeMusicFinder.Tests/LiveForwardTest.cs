using System.Text.Json;
using FreeMusicFinder;
using TL;

namespace FreeMusicFinder.Tests;

/// <summary>Owner request 2026-10-29: forward songs the bots already sent this account to a contact, in a given order.
/// FMF_LIVE_FORWARD = plan.json ([{n, bot, message, ...}]), FMF_LIVE_FORWARD_TO = the contact's name as saved,
/// FMF_LIVE_FORWARD_FIND = "artist|title;..." songs to look up in the bot chats first. Without FMF_LIVE_FORWARD_SEND=1
/// it only checks (contact, messages) and sends nothing.</summary>
internal static class LiveForwardTest
{
    private sealed record Step(int N, string Bot, int Message, string Name, string? File = null);
    private static string Key(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static async Task RunAsync(string planPath, string? to, string? find, bool send, string? store, string? proxy)
    {
        Console.WriteLine("\nForward songs, " + (send ? "SENDING" : "dry run"));
        if (string.IsNullOrWhiteSpace(store) || !File.Exists(store)) throw new ArgumentException("FMF_TELEGRAM_STORE is required.");
        using var account = new TelegramAccount(store, () => proxy ?? "", way => Console.WriteLine("  " + way));
        // A login made with the built-in Telegram app: its file is read where that app copy keeps it (never printed).
        if (Environment.GetEnvironmentVariable("FMF_TELEGRAM_APP_FILE") is { Length: > 0 } packed)
            typeof(TelegramAccount).GetField("_defaults", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(account, TelegramDefaults.ReadPacked(packed));
        if (!account.IsLoggedIn) { Console.WriteLine("  no login in this file"); return; }
        using var patience = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = patience.Token;
        var client = await account.ConnectAsync(ct);
        Console.WriteLine("  signed in as " + account.SignedInAs);

        // The contact, by the name it is saved under; exactly one must match.
        var contacts = await client.Contacts_GetContacts();
        var wanted = (to ?? "").Trim();
        var matches = contacts.users.Values.Where(u => string.Equals($"{u.first_name} {u.last_name}".Trim(), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"  contacts: {contacts.users.Count}; named exactly that: {matches.Count}");
        if (matches.Count != 1)
        {
            foreach (var u in contacts.users.Values.Where(u => wanted.Split(' ').Any(w => w.Length > 2 && $"{u.first_name} {u.last_name}".Contains(w, StringComparison.OrdinalIgnoreCase))))
                Console.WriteLine($"    close: \"{u.first_name} {u.last_name}\"");
            Console.WriteLine("  RESULT: no single contact matched; nothing sent."); return;
        }
        var friend = matches[0];

        var steps = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(planPath))!
            .Select(e => new Step(e.GetProperty("n").GetInt32(), e.GetProperty("bot").GetString()!, e.GetProperty("message").GetInt32(), e.GetProperty("name").GetString() ?? "")).ToList();
        var bots = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
        foreach (var bot in steps.Select(s => s.Bot).Concat([TelegramBotSource.Bot]).Distinct(StringComparer.OrdinalIgnoreCase))
            bots[bot] = (await client.Contacts_ResolveUsername(bot)).User;

        // Songs not in the saved index: looked up in the bot chats by title, the newest file whose title and artist fit.
        foreach (var item in (find ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('|'); var artist = parts[0].Trim(); var title = parts[1].Trim(); var n = int.Parse(parts[2]);
            Message? hit = null; string? from = null;
            foreach (var (name, user) in bots)
            {
                var found = await client.Messages_Search(user, title, new InputMessagesFilterMusic(), limit: 30);
                hit = found.Messages.OfType<Message>().Where(m => m.media is MessageMediaDocument { document: Document d } &&
                        d.attributes.OfType<DocumentAttributeAudio>().Any(a => Key(System.Text.RegularExpressions.Regex.Replace(a.title ?? "", @"s*[([].*$", "")) == Key(title) && Key(a.performer ?? "").Contains(Key(artist))))
                    .OrderByDescending(m => m.id).FirstOrDefault();
                if (hit is not null) { from = name; break; }
                await Task.Delay(800, ct);
            }
            Console.WriteLine(hit is null ? $"  {n}: {artist} - {title}: not in the bot chats" : $"  {n}: {artist} - {title}: found in @{from} #{hit.id}");
            if (hit is not null) steps.Add(new Step(n, from!, hit.id, (hit.media as MessageMediaDocument)?.document is Document doc ? doc.Filename ?? title : title));
        }
        // Songs no bot chat has any more: sent from the owner's own file (FMF_LIVE_FORWARD_UPLOAD = "n|path;...").
        foreach (var item in (Environment.GetEnvironmentVariable("FMF_LIVE_FORWARD_UPLOAD") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('|', 2);
            if (File.Exists(parts[1])) steps.Add(new Step(int.Parse(parts[0]), "", 0, Path.GetFileName(parts[1]), parts[1]));
            else Console.WriteLine($"  {parts[0]}: {parts[1]} is not there");
        }
        steps = steps.OrderBy(s => s.N).ToList();

        // Every message is still there and is a song file.
        var ok = new List<Step>();
        ok.AddRange(steps.Where(s => s.File is not null));
        foreach (var group in steps.Where(s => s.File is null).GroupBy(s => s.Bot))
        {
            var ids = group.Select(s => s.Message).ToArray();
            var got = await client.Messages_GetMessages(ids.Select(i => (InputMessage)new InputMessageID { id = i }).ToArray());
            var present = got.Messages.OfType<Message>().Where(m => m.media is MessageMediaDocument).Select(m => m.id).ToHashSet();
            foreach (var s in group) if (present.Contains(s.Message)) ok.Add(s); else Console.WriteLine($"  {s.N}: #{s.Message} is gone from @{s.Bot}");
            await Task.Delay(500, ct);
        }
        ok = ok.OrderBy(s => s.N).ToList();
        Console.WriteLine($"  ready to forward {ok.Count} songs to the contact (from {string.Join(", ", ok.GroupBy(s => s.Bot).Select(g => $"@{g.Key} {g.Count()}"))})");
        if (!send) { Console.WriteLine("  RESULT: dry run; nothing sent."); return; }

        // In playlist order: consecutive songs from the same bot go in one forward, at a calm pace.
        var sent = 0;
        for (var i = 0; i < ok.Count;)
        {
            if (ok[i].File is { } path)
            {
                await using var stream = File.OpenRead(path);
                var upload = await client.UploadFileAsync(stream, Path.GetFileName(path));
                await client.SendMediaAsync(friend, null, upload, path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ? "audio/flac" : "audio/mpeg");
                sent++; i++;
                Console.WriteLine($"  sent {sent}/{ok.Count} (from the file)");
                await Task.Delay(1500, ct);
                continue;
            }
            var bot = ok[i].Bot; var run = new List<Step>();
            while (i < ok.Count && ok[i].File is null && ok[i].Bot == bot && run.Count < 20) run.Add(ok[i++]);
            await client.Messages_ForwardMessages(bots[bot], run.Select(s => s.Message).ToArray(), run.Select(_ => WTelegram.Helpers.RandomLong()).ToArray(), friend);
            sent += run.Count;
            Console.WriteLine($"  forwarded {sent}/{ok.Count}");
            await Task.Delay(1500, ct);
        }
        Console.WriteLine($"  RESULT: {sent} songs forwarded.");
    }
}
