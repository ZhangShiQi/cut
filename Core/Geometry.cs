using System;

namespace Cut.Core;

public readonly record struct PixelPoint(int X, int Y);

// All session coordinates are physical desktop pixels, including negative origins.
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public static PixelRect Between(PixelPoint a, PixelPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
}

[Flags]
public enum SelectionEdge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

public sealed class SelectionController
{
    private PixelPoint anchor;
    private PixelRect original;
    private SelectionEdge edge;
    private bool moving;
    public PixelRect Selection { get; private set; }
    public bool IsDragging { get; private set; }

    public SelectionEdge HitEdge(PixelPoint p, int tolerance)
    {
        var r = Selection;
        if (r.IsEmpty || p.X < r.X - tolerance || p.X > r.Right + tolerance || p.Y < r.Y - tolerance || p.Y > r.Bottom + tolerance)
            return SelectionEdge.None;
        var result = SelectionEdge.None;
        if (Math.Abs(p.X - r.X) <= tolerance) result |= SelectionEdge.Left;
        else if (Math.Abs(p.X - r.Right) <= tolerance) result |= SelectionEdge.Right;
        if (Math.Abs(p.Y - r.Y) <= tolerance) result |= SelectionEdge.Top;
        else if (Math.Abs(p.Y - r.Bottom) <= tolerance) result |= SelectionEdge.Bottom;
        return result;
    }

    public void Begin(PixelPoint p, int tolerance)
    {
        anchor = p;
        original = Selection;
        edge = HitEdge(p, tolerance);
        moving = edge == SelectionEdge.None && Selection.Contains(p);
        IsDragging = true;
        if (!moving && edge == SelectionEdge.None) Selection = new(p.X, p.Y, 0, 0);
    }

    public void Update(PixelPoint p, PixelRect bounds)
    {
        if (!IsDragging) return;
        p = new(Math.Clamp(p.X, bounds.X, bounds.Right), Math.Clamp(p.Y, bounds.Y, bounds.Bottom));
        if (moving)
        {
            Selection = new(Math.Clamp(original.X + p.X - anchor.X, bounds.X, bounds.Right - original.Width),
                Math.Clamp(original.Y + p.Y - anchor.Y, bounds.Y, bounds.Bottom - original.Height), original.Width, original.Height);
        }
        else if (edge != SelectionEdge.None)
        {
            int left = edge.HasFlag(SelectionEdge.Left) ? p.X : original.X;
            int right = edge.HasFlag(SelectionEdge.Right) ? p.X : original.Right;
            int top = edge.HasFlag(SelectionEdge.Top) ? p.Y : original.Y;
            int bottom = edge.HasFlag(SelectionEdge.Bottom) ? p.Y : original.Bottom;
            Selection = PixelRect.Between(new(left, top), new(right, bottom));
        }
        else Selection = PixelRect.Between(anchor, p);
    }
    public void End() => IsDragging = false;
    public void SetSelection(PixelRect rect) { IsDragging = false; Selection = rect; }
    public void Clear() { IsDragging = false; Selection = default; }
}
