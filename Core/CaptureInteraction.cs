using System;

namespace Cut.Core;

// Preview is separate from Selection: hovering must never move/resize a window
// rectangle when the user actually intends to start a freehand selection.
public sealed class CaptureInteraction
{
    private readonly SelectionController selection;
    private readonly PixelRect desktop;
    private readonly Func<PixelPoint, PixelRect> findTarget;
    private PixelPoint anchor;
    private PixelRect clickTarget;
    private bool pendingClick;
    private int thresholdX, thresholdY;
    public bool IsPointerDown { get; private set; }
    public PixelRect Preview { get; private set; }
    public bool IsPreview => selection.Selection.IsEmpty && !selection.IsDragging && !Preview.IsEmpty;
    public PixelRect DisplaySelection => IsPreview ? Preview : selection.Selection;

    public CaptureInteraction(SelectionController selection, PixelRect desktop, Func<PixelPoint, PixelRect> findTarget)
    {
        this.selection = selection; this.desktop = desktop; this.findTarget = findTarget;
    }
    public void Hover(PixelPoint point)
    {
        if (!IsPointerDown && selection.Selection.IsEmpty && !selection.IsDragging) Preview = findTarget(point).Intersect(desktop);
    }
    public void Press(PixelPoint point, int tolerance, int dragThresholdX, int dragThresholdY)
    {
        Hover(point);
        anchor = point;
        clickTarget = Preview;
        thresholdX = Math.Max(1, dragThresholdX); thresholdY = Math.Max(1, dragThresholdY);
        pendingClick = selection.Selection.IsEmpty;
        IsPointerDown = true;
        if (!pendingClick) { Preview = default; selection.Begin(point, tolerance); }
    }
    public void Move(PixelPoint point)
    {
        if (!IsPointerDown) { Hover(point); return; }
        if (pendingClick)
        {
            if (Math.Abs(point.X - anchor.X) < thresholdX && Math.Abs(point.Y - anchor.Y) < thresholdY) return;
            pendingClick = false;
            Preview = default;
            selection.Begin(anchor, 0);
        }
        selection.Update(point, desktop);
    }
    public void Release(PixelPoint point)
    {
        if (!IsPointerDown) return;
        Move(point);
        if (pendingClick) selection.SetSelection(clickTarget);
        else selection.End();
        pendingClick = false;
        IsPointerDown = false;
        Preview = default;
    }
    public void LoseCapture()
    {
        pendingClick = false;
        IsPointerDown = false;
        selection.End();
    }
    public bool ConfirmPreview()
    {
        if (IsPointerDown) return false;
        if (IsPreview) { selection.SetSelection(Preview); Preview = default; }
        return !selection.Selection.IsEmpty;
    }
    public void Reset(PixelPoint point)
    {
        LoseCapture(); selection.Clear(); Preview = default; Hover(point);
    }
}
