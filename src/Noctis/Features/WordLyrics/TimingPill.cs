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
    /// <summary>Finished states fade out on their own; working and paused ones stay while they last.</summary>
    public bool Lingers => Kind is TimingPillKind.Done or TimingPillKind.Problem;

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
