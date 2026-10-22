using System;

namespace Cut.Core;

internal enum ScrollSpeed { Slow, Standard, Fast }
internal enum AutoScrollAction { Wait, Scroll, Stop }

// All times are monotonic milliseconds. The caller must validate target and generation
// immediately before sending input; this class never injects input itself.
internal sealed class AutoScrollPacer
{
    public ScrollSpeed Speed { get; }
    public int WheelMagnitude { get; private set; }
    public int AttemptsWithoutProgress { get; private set; }
    public long LastInput { get; private set; }
    private long? unchangedSince;
    private bool pending;
    private int sentMagnitude;
    private bool measured;
    public int SettleMilliseconds => Speed switch { ScrollSpeed.Slow => 650, ScrollSpeed.Fast => 200, _ => 350 };
    public double TargetFraction => Speed switch { ScrollSpeed.Slow => .45, ScrollSpeed.Fast => .65, _ => .60 };

    public AutoScrollPacer(ScrollSpeed speed)
    {
        Speed = speed;
        WheelMagnitude = speed switch { ScrollSpeed.Slow => 60, ScrollSpeed.Fast => 120, _ => 90 };
    }
    public AutoScrollAction Stable(long now, int distance, int viewportHeight)
    {
        if (distance > 0)
        {
            // Calibrate using the input that actually produced this displacement, once.
            // Grow gradually; a small final remainder must not cause a sudden large jump.
            if (pending && !measured)
            {
                double desired = viewportHeight * TargetFraction;
                double ratio = desired / distance;
                if (distance >= 16 || ratio < 1)
                    WheelMagnitude = Math.Clamp((int)Math.Round(sentMagnitude * Math.Clamp(ratio, .25, 1.5)), 15, 720);
                measured = true;
            }
            unchangedSince = null; AttemptsWithoutProgress = 0;
        }
        if (pending && now - LastInput < SettleMilliseconds) return AutoScrollAction.Wait;
        if (unchangedSince is long since && now - since >= 5000 && AttemptsWithoutProgress >= 3)
            return AutoScrollAction.Stop;
        return AutoScrollAction.Scroll;
    }
    public void InputSent(long now)
    {
        pending = true; measured = false; sentMagnitude = WheelMagnitude; LastInput = now;
        unchangedSince ??= now; AttemptsWithoutProgress++;
    }
}
