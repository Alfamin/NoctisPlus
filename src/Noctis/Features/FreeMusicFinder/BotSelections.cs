namespace FreeMusicFinder;

/// <summary>An opaque numbered search button, bound to its originating bot and message.</summary>
internal sealed record BotSelection(string Provider,string Bot,int MessageId,string Number,string Data);
internal sealed class StaleSelectionException(string message) : Exception(message);

internal static class BotSelections
{
    public const string Hunters="Music Hunters";
    public static MusicRequest ForSource(MusicRequest request,string source) => request with
    {
        Selection=request.Selection?.Provider==source?request.Selection:null,
        MessageId=request.CachedFrom==source?request.MessageId:null,
        CachedFrom=request.CachedFrom==source?request.CachedFrom:null,
    };
    public static byte[] DecodeHunter(BotSelection selection)
    {
        if(selection.Provider!=Hunters || !string.Equals(selection.Bot,TelegramBotSource.Bot,StringComparison.OrdinalIgnoreCase)
            || selection.MessageId<=0 || string.IsNullOrEmpty(selection.Number) || selection.Number.Length>8
            || !selection.Number.All(char.IsAsciiDigit) || string.IsNullOrEmpty(selection.Data) || selection.Data.Length>88)
            throw new StaleSelectionException("Music Hunters' saved selection is invalid; finding a fresh matching result.");
        byte[] data;
        try {data=Convert.FromBase64String(selection.Data);}
        catch(FormatException){throw new StaleSelectionException("Music Hunters' saved button is unreadable; finding a fresh matching result.");}
        if(data.Length is <1 or >64)throw new StaleSelectionException("Music Hunters' saved button has an invalid size; finding a fresh matching result.");
        return data;
    }
}

/// <summary>Music Hunters' protocol is independent of DeezLoad's inline/track-link protocol.</summary>
internal interface IHunterBotSource
{
    Task<IReadOnlyList<BotTrack>> SearchAsync(string query,CancellationToken ct);
    Task<string> FetchSelectedAsync(MusicRequest request,Stream output,IProgress<double>? progress,CancellationToken ct);
}
