using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Cut.Core;

public enum MarkTool { Select, Rectangle, Ellipse, Arrow, Pen, Text, Mosaic, Cover }
public sealed record MarkStyle(Color Color, double Width = 4, double FontSize = 28);
public sealed record Annotation(MarkTool Tool, Point Start, Point End, MarkStyle Style, Point[] Points, string Text = "", double TextWidth = 420);

public sealed class AnnotationDocument
{
    private Annotation[] items = Array.Empty<Annotation>();
    private readonly Stack<Annotation[]> undo = new(), redo = new();
    private Annotation? original;
    private Point anchor;
    private int handle = -1;
    private bool adding;
    public AnnotationDocument(int width, int height) { Width = width; Height = height; }
    public int Width { get; }
    public int Height { get; }
    public MarkTool Tool { get; set; }
    public MarkStyle Style { get; set; } = new(Colors.Red);
    public IReadOnlyList<Annotation> Items => items;
    public int SelectedIndex { get; private set; } = -1;
    public Annotation? Selected => SelectedIndex >= 0 && SelectedIndex < items.Length ? items[SelectedIndex] : null;
    public Annotation? Draft { get; private set; }
    public bool EditingText { get; private set; }
    public bool IsDragging { get; private set; }
    public bool Locked { get; private set; }
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public IEnumerable<Annotation> VisibleItems
    {
        get
        {
            for (int i = 0; i < items.Length; i++)
                yield return Draft is not null && !adding && i == SelectedIndex ? Draft : items[i];
            if (Draft is not null && adding) yield return Draft;
        }
    }
    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, Width), Math.Clamp(p.Y, 0, Height));
    public bool Begin(Point point, double tolerance)
    {
        CancelDraft(); point = Clamp(point); anchor = point; handle = -1;
        if (Tool == MarkTool.Select)
        {
            if (Selected is not null)
            {
                var handles = AnnotationDrawing.Handles(Selected);
                for (int i = 0; i < handles.Length; i++) if ((handles[i] - point).Length <= tolerance) { handle = i; break; }
            }
            if (handle < 0) SelectedIndex = Hit(point, tolerance);
            if (Selected is null) return false;
            Style = Selected.Style; original = Selected; Draft = original; adding = false;
        }
        else
        {
            if (Tool == MarkTool.Text) return false;
            SelectedIndex = -1; original = null; adding = true;
            Draft = new(Tool, point, point, Style, new[] { point });
        }
        IsDragging = true; return true;
    }
    public int Hit(Point point, double tolerance)
    {
        for (int i = items.Length - 1; i >= 0; i--)
        {
            if (items[i].Tool == MarkTool.Mosaic) continue;
            if (AnnotationDrawing.Hit(items[i], point, tolerance)) return i;
        }
        return -1;
    }
    public void Move(Point point)
    {
        if (!IsDragging || Draft is null) return;
        point = Clamp(point);
        if (adding)
        {
            if (Draft.Tool is MarkTool.Pen or MarkTool.Mosaic)
            {
                if ((Draft.Points[^1] - point).Length < 1 || Draft.Points.Length >= 20000) return;
                Draft = Draft with { End = point, Points = Draft.Points.Append(point).ToArray() };
            }
            else Draft = Draft with { End = point };
        }
        else if (original is not null)
        {
            if (handle >= 0 && original.Tool == MarkTool.Arrow)
                Draft = handle == 0 ? original with { Start = point } : original with { End = point };
            else if (handle >= 0)
            {
                var b = AnnotationDrawing.Bounds(original);
                Point opposite = handle switch { 0 => b.BottomRight, 1 => b.BottomLeft, 2 => b.TopLeft, _ => b.TopRight };
                Draft = original with { Start = opposite, End = point };
            }
            else
            {
                var b = AnnotationDrawing.Bounds(original);
                double dx = Math.Clamp(point.X - anchor.X, -b.Left, Math.Max(-b.Left, Width - b.Right));
                double dy = Math.Clamp(point.Y - anchor.Y, -b.Top, Math.Max(-b.Top, Height - b.Bottom));
                var offset = new Vector(dx, dy);
                Draft = original with { Start = original.Start + offset, End = original.End + offset, Points = original.Points.Select(p => p + offset).ToArray() };
            }
        }
    }
    public void End(Point point)
    {
        Move(point);
        if (!IsDragging || Draft is null) return;
        if (adding && Draft.Tool is MarkTool.Rectangle or MarkTool.Ellipse or MarkTool.Cover &&
            (Math.Abs(Draft.Start.X - Draft.End.X) < 1 || Math.Abs(Draft.Start.Y - Draft.End.Y) < 1)) { CancelDraft(); return; }
        if (adding && Draft.Tool == MarkTool.Arrow && (Draft.End - Draft.Start).Length < 2) { CancelDraft(); return; }
        if (!adding && original is not null && Draft.Start == original.Start && Draft.End == original.End) { CancelDraft(); return; }
        Commit();
    }
    public void BeginText(Point point, int existing = -1)
    {
        CancelDraft();
        if (existing >= 0 && existing < items.Length && items[existing].Tool == MarkTool.Text)
        { SelectedIndex = existing; original = items[existing]; Draft = original; Style = original.Style; adding = false; }
        else
        {
            point = Clamp(point); SelectedIndex = -1; adding = true; original = null;
            Draft = new(MarkTool.Text, point, point, Style, Array.Empty<Point>(), "", Math.Max(1, Math.Min(420, Width - point.X)));
        }
        EditingText = true;
    }
    public void PreviewText(string text) { if (EditingText && Draft is not null) Draft = Draft with { Text = text }; }
    public void CommitText(string text)
    {
        if (!EditingText || Draft is null) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            bool remove = !adding; CancelDraft(); if (remove) DeleteSelected(); return;
        }
        Draft = Draft with { Text = text };
        if (!adding && Draft == original) { CancelDraft(); return; }
        Commit();
    }
    private void Remember()
    {
        // Snapshots share immutable annotations/point arrays. Keep the latest 100 edits.
        if (undo.Count >= 100)
        {
            var keep = undo.Take(99).Reverse().ToArray(); undo.Clear(); foreach (var value in keep) undo.Push(value);
        }
        undo.Push(items); redo.Clear(); Locked = true;
    }
    private void Commit()
    {
        if (Draft is null) return;
        Remember();
        if (adding) { items = items.Append(Draft).ToArray(); SelectedIndex = Draft.Tool == MarkTool.Mosaic ? -1 : items.Length - 1; }
        else { var next = (Annotation[])items.Clone(); next[SelectedIndex] = Draft; items = next; }
        Draft = null; original = null; IsDragging = EditingText = false;
    }
    public void CancelDraft() { Draft = null; original = null; IsDragging = EditingText = false; }
    public void Deselect() { CancelDraft(); SelectedIndex = -1; }
    public void DeleteSelected()
    {
        CancelDraft();
        if (Selected is null || Selected.Tool == MarkTool.Mosaic) return;
        Remember(); items = items.Where((_, i) => i != SelectedIndex).ToArray(); SelectedIndex = -1;
    }
    public void ChangeStyle(MarkStyle style)
    {
        Style = style;
        if (Selected is null || Selected.Tool == MarkTool.Mosaic || Selected.Style == style) return;
        Remember(); var next = (Annotation[])items.Clone(); next[SelectedIndex] = Selected with { Style = style }; items = next;
    }
    public void Undo()
    {
        CancelDraft(); if (!CanUndo) return;
        redo.Push(items); items = undo.Pop(); SelectedIndex = -1;
    }
    public void Redo()
    {
        CancelDraft(); if (!CanRedo) return;
        undo.Push(items); items = redo.Pop(); SelectedIndex = -1;
    }
}
