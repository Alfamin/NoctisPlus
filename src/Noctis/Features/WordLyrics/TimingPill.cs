namespace Noctis.Features.WordLyrics;

public enum TimingPillKind { Working, Paused, Done, Problem }

/// <summary>
/// The small status pill for word timing: an icon (progress ring, pause, check or info) and a few words. The full
/// sentences stay in the progress window, which the pill opens (owner, 2026-10-18: the long notices that popped up at
/// the top read badly; a short text with an icon says the same).
/// </summary>
public sealed record TimingPill(TimingPillKind Kind, string Text, double? Fraction = null)
{
    public bool IsWorking => Kind == TimingPillKind.Working;
    public bool IsDeterminate => Kind == TimingPillKind.Working && Fraction is not null;
    public bool IsIndeterminate => Kind == TimingPillKind.Working && Fraction is null;
    public double Percent => Math.Clamp(Fraction ?? 0, 0, 1);
    public bool IsPaused => Kind == TimingPillKind.Paused;
    public bool IsDone => Kind == TimingPillKind.Done;
    public bool IsProblem => Kind == TimingPillKind.Problem;
    /// <summary>Owner #193: a pill that says something once and goes (a big batch starting, a retry or a wait).</summary>
    public bool Fades { get; init; }
    /// <summary>Finished states fade out on their own; working and paused ones stay while they last.</summary>
    public bool Lingers => Kind is TimingPillKind.Done or TimingPillKind.Problem || Fades;

    /// <summary>What the pill shows for a progress update; null when there is nothing worth showing.</summary>
    internal static TimingPill? From(ProgressState state)
    {
        if (state.Cancelling) return new(TimingPillKind.Working, "Stopping word timing");
        if (state.Finished)
        {
            if (state.Error)
                return new(TimingPillKind.Problem, state.Message == "Setup is needed" ? "WordLyrics needs setup" : "Word timing needs attention");
            return state.Message switch
            {
                "Ready" => null,
                "Cancelled" => new(TimingPillKind.Done, "Word timing stopped"),
                "Retry scheduled" => new(TimingPillKind.Paused, "Word timing will retry"),
                "No new word timestamps were written" => new(TimingPillKind.Done, "No new word timing"),
                _ => new(TimingPillKind.Done, state.Completed == 1 ? "Words timed · 1 song" : $"Words timed · {state.Completed} songs"),
            };
        }
        if (state.Paused) return new(TimingPillKind.Paused, "Word timing paused");
        if (state.Message.StartsWith("Waiting", StringComparison.Ordinal)) return new(TimingPillKind.Paused, "Word timing waiting");
        var fraction = state.Fraction ?? (state.Total > 0 ? (double)state.Completed / state.Total : null);
        return state.Total > 1
            ? new(TimingPillKind.Working, $"Timing words · {Math.Min(state.Completed + 1, state.Total)} of {state.Total}", fraction)
            : new(TimingPillKind.Working, "Timing words", fraction);
    }
}

/// <summary>
/// Owner #193: word timing works in the background. The pill shows work the user asked for, a short run while it lasts, a
/// big batch once as it starts, what got timed, and what only the user can fix, once. Retries, waits and failures of
/// automatic timing stay off the screen; the songs are tried again quietly.
/// </summary>
internal sealed class PillPolicy
{
    private bool _bigShown;
    private readonly HashSet<string> _told = new(StringComparer.Ordinal);

    /// <summary>A batch of more songs than this is said once, as it starts, instead of for hours.</summary>
    internal const int ShortRun = 3;

    // Problems nothing else can fix: said once per app run when the work was automatic.
    private static bool NeedsYou(ProgressState state) => state.Message is "Setup is needed" or "WordLyrics stopped responding" or "WordLyrics could not start";

    /// <param name="shown">The pill shown now; kept when there is nothing new to say.</param>
    internal TimingPill? Decide(ProgressState state, TimingPill? shown)
    {
        if (state.Finished && state.Total > ShortRun || state.Message is "Cancelled" or "Restarted") _bigShown = false;
        var pill = TimingPill.From(state);
        if (pill is null) return null;
        var automatic = !state.Asked;
        switch (pill.Kind)
        {
            case TimingPillKind.Problem:
                if (!automatic || NeedsYou(state) && _told.Add(state.Message)) return pill;
                // Some songs timed, others to be tried again later: only the timed ones are news.
                return int.TryParse(state.Message.Split(' ')[0], out var timed) && timed > 0 && state.Message.EndsWith("received word timestamps", StringComparison.Ordinal)
                    ? new TimingPill(TimingPillKind.Done, timed == 1 ? "Words timed · 1 song" : $"Words timed · {timed} songs") : null;
            case TimingPillKind.Done:
                // The songs playing show their words on the lyrics page; an automatic run that timed nothing says nothing.
                return automatic && (state.Listening || pill.Text == "No new word timing") ? null : pill;
            case TimingPillKind.Paused when state.Paused && !state.Finished:
                return pill; // the user pressed Pause
            case TimingPillKind.Paused:
                return automatic ? null : pill with { Fades = true }; // a retry or a wait
        }
        if (state.Cancelling) return pill;
        if (automatic && (state.Retry || state.Listening)) return null;
        if (state.Total <= ShortRun) return pill;
        // A big batch: said once as it starts; a restart in the middle (to time the song playing) says nothing.
        if (_bigShown) return shown;
        _bigShown = true;
        return new TimingPill(TimingPillKind.Working, $"Timing words · {state.Total} songs") { Fades = true };
    }
}
