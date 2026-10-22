namespace Cut.Core;

// Preserve the strategy learned for this selection. A wallpaper page should not
// pay for a failed whole-image template search on every subsequent frame.
internal sealed class ScrollMatchingSession
{
    private bool layered;
    internal ScrollMatch Match(ScrollFrame previous, ScrollFrame current, ScrollDirection direction, bool allowUnreliable = false)
    {
        if (ScrollMatcher.Stable(previous, current)) return new(ScrollMatchKind.Same);
        ScrollMatch result;
        if (layered)
        {
            result = ScrollMatcher.MatchFeatures(previous, current, direction, featureCount: 1200);
            if (result.Kind != ScrollMatchKind.Matched) result = ScrollMatcher.Match(previous, current, direction);
        }
        else result = ScrollMatcher.Match(previous, current, direction);
        if (result.Kind == ScrollMatchKind.Matched) layered = result.Layered;
        if (result.Kind == ScrollMatchKind.Uncertain && allowUnreliable)
            result = ScrollMatcher.MatchFeatures(previous, current, direction, allowUnreliable: true);
        return result;
    }
}
