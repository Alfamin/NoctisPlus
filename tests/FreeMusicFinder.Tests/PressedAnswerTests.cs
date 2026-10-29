namespace FreeMusicFinder.Tests;

/// <summary>Owner #205: HUGEL "Movin' To The Sun" from For you: Music Hunters sent the file for the result pressed, tagged
/// "I´m Moving To The Sun", and the app refused it three times (pressing again each time) before DeezLoad.</summary>
internal static class PressedAnswerTests
{
    public static void Run()
    {
        Check.Section("The file answering a pressed result, tagged another way (#205)");
        var wanted = new MusicRequest("Movin' To The Sun", "HUGEL", 196);
        var length = TimeSpan.FromSeconds(197);
        Check.True(!RecordingMatch.Fits(wanted, "HUGEL, Imael Angel, Ultra Nate", "I´m Moving To The Sun", length), "the usual check refuses the other spelling");
        Check.True(RecordingMatch.SameAnswer(wanted, "HUGEL, Imael Angel, Ultra Nate", "I´m Moving To The Sun", length), "the pressed result's file is taken: shared artist, same length, the same words");
        Check.True(!RecordingMatch.SameAnswer(wanted, "HUGEL", "I´m Moving To The Sun", TimeSpan.FromSeconds(230)), "another length is another recording");
        Check.True(!RecordingMatch.SameAnswer(wanted, "Ultra Nate", "Free", length), "another song of a shared artist is not it");
        Check.True(!RecordingMatch.SameAnswer(wanted, "Someone Else", "I´m Moving To The Sun", length), "no shared artist: not it");
        Check.True(!RecordingMatch.SameAnswer(wanted, "HUGEL", "I´m Moving To The Sun (Remix)", length), "a remix is never the original");
        Check.True(!RecordingMatch.SameAnswer(wanted, "HUGEL", "Sun", length), "one word in common is not enough");
        Check.True(!RecordingMatch.SameAnswer(new MusicRequest("Movin' To The Sun", "HUGEL", null), "HUGEL", "I´m Moving To The Sun", length), "without the wanted length: not taken");
    }
}
