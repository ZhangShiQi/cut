using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cut.Core;

namespace Cut.Controls;

internal enum TimelineHandle { Start, End, Playhead }

internal sealed class TimelineInteractionEventArgs(TimelineHandle handle) : EventArgs
{
    internal TimelineHandle Handle { get; } = handle;
}

// One video track, two constrained trim handles, and an independent playhead.
// The control owns editing geometry, not MediaElement playback or seeking.
internal sealed class RecordingTimeline : UserControl
{
    private const double Inset = 20, RailTop = 29, RailHeight = 48, HandleWidth = 12;
    private readonly Canvas canvas = new() { Background = Brushes.Transparent };
    private readonly TimelineSurface surface;
    private readonly TimelineThumb startThumb = new(TimelineHandle.Start), endThumb = new(TimelineHandle.End), headThumb = new(TimelineHandle.Playhead);
    private double position, savedStart, savedEnd, savedPosition;
    private bool editing;
    private TimelineHandle editingHandle;
    private ImageSource? filmstrip;
    internal RecordingTrimRange Range { get; }
    internal double Position => position;
    internal bool HasFilmstrip => filmstrip is not null;
    internal event EventHandler? SelectionChanged, SeekRequested, PlaybackToggleRequested;
    internal event EventHandler<TimelineInteractionEventArgs>? InteractionStarted, InteractionCompleted;
    private double RailWidth => Math.Max(1, ActualWidth - Inset * 2);
    private double X(double seconds) => Inset + Math.Clamp(seconds / Range.Duration, 0, 1) * RailWidth;
    private double Time(double x) => (x - Inset) / RailWidth * Range.Duration;

    internal RecordingTimeline(double duration, int fps)
    {
        Range = new(duration, fps); Height = 116; MinWidth = 240; Focusable = false;
        surface = new(this); canvas.Children.Add(surface);
        foreach (var thumb in new[] { startThumb, endThumb, headThumb })
        {
            canvas.Children.Add(thumb);
            thumb.DragStarted += (_, _) => BeginEdit(thumb.Kind);
            thumb.DragDelta += (_, e) =>
            {
                double offset = thumb.Kind == TimelineHandle.Start ? HandleWidth : thumb.Kind == TimelineHandle.Playhead ? 7 : 0;
                ApplyEdit(thumb.Kind, Time(Canvas.GetLeft(thumb) + offset + e.HorizontalChange));
            };
            thumb.DragCompleted += (_, e) => CompleteEdit(e.Canceled);
            thumb.PreviewKeyDown += (_, e) => HandleKey(thumb, e);
        }
        Content = canvas;
        SizeChanged += (_, _) => UpdateVisuals();
        IsEnabledChanged += (_, _) => { if (!IsEnabled && editing) Thumb(editingHandle).CancelDrag(); UpdateVisuals(); };
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled || !IsEnabled) return;
            BeginEdit(TimelineHandle.Playhead);
            ApplyEdit(TimelineHandle.Playhead, Time(e.GetPosition(canvas).X));
            CompleteEdit(false); headThumb.Focus(); e.Handled = true;
        };
        ToolTip = "拖动两端裁剪，拖动白色播放头定位；左右键逐帧调整，Shift + 左右键调整 10 帧；空格播放 / 暂停。";
    }
    private TimelineThumb Thumb(TimelineHandle handle) => handle switch { TimelineHandle.Start => startThumb, TimelineHandle.End => endThumb, _ => headThumb };
    private void BeginEdit(TimelineHandle handle)
    {
        if (editing) return;
        editing = true; editingHandle = handle;
        savedStart = Range.Start; savedEnd = Range.End; savedPosition = position;
        Thumb(handle).Focus(); InteractionStarted?.Invoke(this, new(handle));
    }
    private void ApplyEdit(TimelineHandle handle, double value)
    {
        if (handle == TimelineHandle.Start) { Range.SetStart(value); position = Range.Start; }
        else if (handle == TimelineHandle.End) { Range.SetEnd(value); position = Range.LastPreviewPosition; }
        else position = Range.ConstrainPosition(Math.Round(Math.Clamp(value, 0, Range.Duration) * Range.Fps) / Range.Fps);
        UpdateVisuals();
        if (handle != TimelineHandle.Playhead) SelectionChanged?.Invoke(this, EventArgs.Empty);
        SeekRequested?.Invoke(this, EventArgs.Empty);
    }
    private void CompleteEdit(bool cancelled)
    {
        if (!editing) return;
        if (cancelled)
        {
            Range.Restore(savedStart, savedEnd); position = Range.ConstrainPosition(savedPosition);
            UpdateVisuals(); SelectionChanged?.Invoke(this, EventArgs.Empty); SeekRequested?.Invoke(this, EventArgs.Empty);
        }
        editing = false; InteractionCompleted?.Invoke(this, new(editingHandle));
    }
    private void HandleKey(TimelineThumb thumb, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && editing) { thumb.CancelDrag(); e.Handled = true; return; }
        if (e.Key == Key.Space) { PlaybackToggleRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        double current = thumb.Kind == TimelineHandle.Start ? Range.Start : thumb.Kind == TimelineHandle.End ? Range.End : position;
        double step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1) / (double)Range.Fps;
        double target = e.Key switch { Key.Home => 0, Key.End => Range.Duration, Key.Left => current - step, _ => current + step };
        BeginEdit(thumb.Kind); ApplyEdit(thumb.Kind, target); CompleteEdit(false); e.Handled = true;
    }
    internal void SetSelection(double start, double end)
    {
        Range.SetRange(start, end); position = Range.Start; UpdateVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty); SeekRequested?.Invoke(this, EventArgs.Empty);
    }
    internal void ResetSelection()
    {
        Range.Reset(); position = 0; UpdateVisuals();
        SelectionChanged?.Invoke(this, EventArgs.Empty); SeekRequested?.Invoke(this, EventArgs.Empty);
    }
    internal void SetPlaybackPosition(double value) { position = Math.Clamp(double.IsFinite(value) ? value : 0, 0, Range.Duration); UpdateVisuals(); }
    internal void SetFilmstrip(ImageSource value) { filmstrip = value; surface.InvalidateVisual(); }
    private void UpdateVisuals()
    {
        if (surface is null) return;
        surface.Width = Math.Max(0, ActualWidth); surface.Height = Height;
        startThumb.Width = endThumb.Width = HandleWidth; startThumb.Height = endThumb.Height = RailHeight + 4;
        headThumb.Width = 14; headThumb.Height = 18;
        Canvas.SetLeft(startThumb, X(Range.Start) - HandleWidth); Canvas.SetTop(startThumb, RailTop - 2);
        Canvas.SetLeft(endThumb, X(Range.End)); Canvas.SetTop(endThumb, RailTop - 2);
        Canvas.SetLeft(headThumb, X(position) - 7); Canvas.SetTop(headThumb, RailTop - 18);
        startThumb.ToolTip = "起点 " + Code(Range.Start); endThumb.ToolTip = "终点 " + Code(Range.End); headThumb.ToolTip = "播放位置 " + Code(position);
        canvas.Opacity = IsEnabled ? 1 : .45; surface.InvalidateVisual();
    }
    internal static string Code(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private sealed class TimelineSurface(RecordingTimeline owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            double width = owner.ActualWidth; if (width <= Inset * 2) return;
            var rail = new Rect(Inset, RailTop, owner.RailWidth, RailHeight);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(35, 45, 59)), null, rail, 4, 4);
            if (owner.filmstrip is not null) dc.DrawImage(owner.filmstrip, rail);
            else for (int i = 0; i < 10; i++) dc.DrawRectangle(new SolidColorBrush(i % 2 == 0 ? Color.FromRgb(42, 55, 72) : Color.FromRgb(49, 64, 82)), null,
                new Rect(Inset + i * owner.RailWidth / 10, RailTop, owner.RailWidth / 10, RailHeight));
            double left = owner.X(owner.Range.Start), right = owner.X(owner.Range.End);
            var dim = new SolidColorBrush(Color.FromArgb(170, 10, 16, 24));
            dc.DrawRectangle(dim, null, new Rect(Inset, RailTop, Math.Max(0, left - Inset), RailHeight));
            dc.DrawRectangle(dim, null, new Rect(right, RailTop, Math.Max(0, Inset + owner.RailWidth - right), RailHeight));
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(24, 163, 142)), 2), new Rect(left, RailTop, Math.Max(.5, right - left), RailHeight));
            dc.DrawLine(new Pen(Brushes.White, 2), new Point(owner.X(owner.position), RailTop - 1), new Point(owner.X(owner.position), RailTop + RailHeight + 3));
            double step = owner.Range.Duration <= 5 ? .5 : owner.Range.Duration <= 15 ? 1 : 5;
            for (double t = 0; t <= owner.Range.Duration + .000001; t += step)
            {
                double x = owner.X(t);
                dc.DrawLine(new Pen(Brushes.SlateGray, 1), new Point(x, 22), new Point(x, 27));
                Text(dc, t.ToString("0.#", CultureInfo.InvariantCulture) + "s", x, 1, Brushes.SlateGray, true);
            }
            Text(dc, "起点 " + Code(owner.Range.Start), Inset, 88, Brushes.DarkSlateGray);
            Text(dc, "保留 " + owner.Range.Length.ToString("0.000", CultureInfo.InvariantCulture) + " 秒", width / 2, 88, Brushes.Teal, true);
            Text(dc, "终点 " + Code(owner.Range.End), width - Inset, 88, Brushes.DarkSlateGray, right: true);
        }
        private void Text(DrawingContext dc, string text, double x, double y, Brush brush, bool center = false, bool right = false)
        {
            var value = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(value, new Point(x - (center ? value.Width / 2 : right ? value.Width : 0), y));
        }
    }
    private sealed class TimelineThumb : Thumb
    {
        internal TimelineHandle Kind { get; }
        internal TimelineThumb(TimelineHandle kind)
        {
            Kind = kind; Focusable = true; Cursor = kind == TimelineHandle.Playhead ? Cursors.Hand : Cursors.SizeWE;
            Template = new ControlTemplate(typeof(Thumb)) { VisualTree = new FrameworkElementFactory(typeof(Border)) };
            AutomationProperties.SetName(this, kind switch { TimelineHandle.Start => "裁剪起点", TimelineHandle.End => "裁剪终点", _ => "播放位置" });
            IsKeyboardFocusedChanged += (_, _) => InvalidateVisual(); MouseEnter += (_, _) => InvalidateVisual(); MouseLeave += (_, _) => InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            if (Kind == TimelineHandle.Playhead)
            {
                var geometry = new StreamGeometry();
                using (var shape = geometry.Open()) { shape.BeginFigure(new(1, 1), true, true); shape.LineTo(new(13, 1), true, false); shape.LineTo(new(13, 10), true, false); shape.LineTo(new(7, 17), true, false); shape.LineTo(new(1, 10), true, false); }
                dc.DrawGeometry(IsKeyboardFocused ? Brushes.Teal : Brushes.White, new Pen(Brushes.SlateGray, 1), geometry);
            }
            else
            {
                var fill = new SolidColorBrush(IsMouseOver || IsKeyboardFocused ? Color.FromRgb(17, 131, 115) : Color.FromRgb(24, 163, 142));
                dc.DrawRoundedRectangle(fill, IsKeyboardFocused ? new Pen(Brushes.White, 1) : null, new Rect(0, 0, ActualWidth, ActualHeight), 3, 3);
                dc.DrawLine(new Pen(Brushes.White, 1), new Point(4, 19), new Point(4, 33));
                dc.DrawLine(new Pen(Brushes.White, 1), new Point(7, 19), new Point(7, 33));
            }
        }
    }
}
