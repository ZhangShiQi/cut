using System;

namespace Cut.Core;

// End is exclusive. Endpoint edits are frame-snapped, then constrained against
// the other endpoint, including recordings with a partial final frame.
internal sealed class RecordingTrimRange
{
    internal double Duration { get; }
    internal int Fps { get; }
    internal double MinimumLength => Math.Min(Duration, 1d / Fps);
    internal double Start { get; private set; }
    internal double End { get; private set; }
    internal double Length => End - Start;
    internal double LastPreviewPosition => Math.Max(Start, End - MinimumLength);

    internal RecordingTrimRange(double duration, int fps)
    {
        if (!double.IsFinite(duration) || duration <= 0 || fps <= 0) throw new ArgumentOutOfRangeException(nameof(duration));
        Duration = End = duration; Fps = fps;
    }
    private double Snap(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        if (value <= 0) return 0;
        if (value >= Duration) return Duration;
        return Math.Round(Math.Clamp(value, 0, Duration) * Fps) / Fps;
    }
    internal void SetStart(double value) => Start = Math.Clamp(Snap(value), 0, Math.Max(0, End - MinimumLength));
    internal void SetEnd(double value) => End = Math.Clamp(Snap(value), Math.Min(Duration, Start + MinimumLength), Duration);
    internal void SetRange(double start, double end)
    {
        // Normalize before committing either boundary so observers never see an
        // inverted or empty interval.
        double nextStart = Math.Clamp(Snap(start), 0, Math.Max(0, Duration - MinimumLength));
        double nextEnd = Math.Clamp(Snap(end), Math.Min(Duration, nextStart + MinimumLength), Duration);
        Start = nextStart; End = nextEnd;
    }
    internal double ConstrainPosition(double value) => Math.Clamp(double.IsFinite(value) ? value : Start, Start, LastPreviewPosition);
    internal void Restore(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end > Duration || end - start < MinimumLength - 1e-9)
            throw new ArgumentOutOfRangeException(nameof(start));
        Start = start; End = end;
    }
    internal void Reset() { Start = 0; End = Duration; }
}
