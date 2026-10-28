using Noctis.Features.WordLyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>Owner #193: a friend's word timing kept flashing "1 of 2" and "1 of 200" on screen. Automatic timing works in
/// the background: retries, waits and failures say nothing, a big batch is said once, and what only the user can fix is
/// said once.</summary>
public class WordTimingQuietTests
{
    private static ProgressState Working(int completed, int total, bool asked = false, bool retry = false, bool listening = false)
        => new("Timing words", "", completed, total, Asked: asked, Retry: retry, Listening: listening);

    [Fact]
    public void AutomaticRetries_AndFailures_StayOffTheScreen_UntilSomethingIsTimed()
    {
        var policy = new PillPolicy();
        // New downloads, first try: a short run shows while it lasts.
        Assert.Equal("Timing words · 1 of 2", policy.Decide(Working(0, 2), null)!.Text);
        // It failed: nothing timed, tried again later; none of that is shown.
        Assert.Null(policy.Decide(new("No new word timestamps were written", "", 2, 2, Finished: true, Error: true, Asked: false), null));
        Assert.Null(policy.Decide(new("Retry scheduled", "", 0, 2, Finished: true, Asked: false), null));
        Assert.Null(policy.Decide(new("Waiting for another WordLyrics run", "", Total: 2, Asked: false), null));
        Assert.Null(policy.Decide(Working(0, 2, retry: true), null)); // the quiet retry
        // The retry worked: that is said.
        var done = policy.Decide(new("2 songs received word timestamps", "", 2, 2, Finished: true, Asked: false, Retry: true), null)!;
        Assert.Equal("Words timed · 2 songs", done.Text);
        Assert.True(done.Lingers);
    }

    [Fact]
    public void TheSongsPlaying_AreTimedWithoutAPill()
    {
        var policy = new PillPolicy();
        Assert.Null(policy.Decide(Working(0, 1, listening: true), null));
        Assert.Null(policy.Decide(new("1 songs received word timestamps", "", 1, 1, Finished: true, Asked: false, Listening: true), null));
    }

    [Fact]
    public void ABigBatch_IsSaidOnceAsItStarts_NotAgainWhenItMakesRoomForTheSongPlaying()
    {
        var policy = new PillPolicy();
        var start = policy.Decide(Working(0, 200, asked: true), null)!;
        Assert.Equal("Timing words · 200 songs", start.Text);
        Assert.True(start.Lingers); // fades after a few seconds instead of staying for hours
        Assert.Same(start, policy.Decide(Working(5, 200, asked: true), start)); // nothing new to say
        // It stepped aside for the song playing, which was timed quietly; the batch starting again says nothing.
        Assert.Null(policy.Decide(Working(0, 1, listening: true), start));
        Assert.Null(policy.Decide(new("1 songs received word timestamps", "", 1, 1, Finished: true, Asked: false, Listening: true), null));
        Assert.Null(policy.Decide(Working(0, 195, asked: true), null));
        Assert.Equal("Words timed · 190 songs", policy.Decide(new("190 songs received word timestamps", "", 190, 195, Finished: true, Asked: true), null)!.Text);
        Assert.Equal("Timing words · 40 songs", policy.Decide(Working(0, 40, asked: true), null)!.Text); // the next request is said again
    }

    [Fact]
    public void WhatOnlyTheUserCanFix_IsSaidOncePerRun_ForAutomaticTiming()
    {
        var policy = new PillPolicy();
        ProgressState Setup() => new("Setup is needed", "reason", Error: true, Finished: true, Asked: false);
        Assert.Equal("WordLyrics needs setup", policy.Decide(Setup(), null)!.Text);
        Assert.Null(policy.Decide(Setup(), null)); // not on every try
        ProgressState Blocked() => new("WordLyrics could not start", "reason", Finished: true, Error: true, Asked: false, Retry: true);
        Assert.NotNull(policy.Decide(Blocked(), null));
        Assert.Null(policy.Decide(Blocked(), null));
        // A failed song or a locked file is not something to tell about automatic timing at all.
        Assert.Null(policy.Decide(new("Waiting for file access", "reason", Error: true, Finished: true, Asked: false), null));
        // One timed, one to be tried again later: the timed one is said, the failure is not.
        Assert.Equal("Words timed · 1 song", policy.Decide(new("1 songs received word timestamps", "", 2, 2, Finished: true, Error: true, Asked: false), null)!.Text);
    }

    [Fact]
    public void WhatTheUserAskedFor_IsShown_AndWaitsFade()
    {
        var policy = new PillPolicy();
        Assert.Equal("Timing words", policy.Decide(Working(0, 1, asked: true), null)!.Text);
        Assert.Equal("WordLyrics needs setup", policy.Decide(new("Setup is needed", "", Error: true, Finished: true), null)!.Text);
        Assert.Equal("WordLyrics needs setup", policy.Decide(new("Setup is needed", "", Error: true, Finished: true), null)!.Text);
        var retry = policy.Decide(new("Retry scheduled", "", 0, 1, Finished: true, Asked: true), null)!;
        Assert.True(retry.Lingers); // "will retry" no longer stays on screen
        var paused = policy.Decide(new("Paused", "", 1, 5, Paused: true), null)!;
        Assert.False(paused.Lingers); // a pause the user pressed stays
    }
}
