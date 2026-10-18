using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HearSay.Editing;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>
/// An envelope of the selected part as a graph to edit: its points and segments, with time on the square-root scale of
/// the block diagram's sketch, so that a 5 ms attack and a 2 s decay both show, and a held stretch before the release.
/// Dragging a point moves it (one undo step per drag; Escape cancels); a double-click on a segment adds a point in its
/// middle; a right-click on a point offers Remove, Sustain here, One-shot and the curve of the segment that ends at it.
/// </summary>
public sealed class EnvelopeEditor : Control
{
    /// <summary>The envelope drawn; nothing is drawn without one.</summary>
    public static readonly StyledProperty<OperatorEnvelope?> EnvelopeProperty = AvaloniaProperty.Register<EnvelopeEditor, OperatorEnvelope?>(nameof(Envelope));

    /// <summary>Which envelope of the part it is, which sets the range of its levels.</summary>
    public static readonly StyledProperty<EnvelopeKind> KindProperty = AvaloniaProperty.Register<EnvelopeEditor, EnvelopeKind>(nameof(Kind));

    /// <summary>The part of the patch the envelope belongs to.</summary>
    public static readonly StyledProperty<PatchPart?> PartProperty = AvaloniaProperty.Register<EnvelopeEditor, PatchPart?>(nameof(Part));

    /// <summary>The editing the changes go to; without one the graph only shows.</summary>
    public static readonly StyledProperty<PatchEditorViewModel?> EditorProperty = AvaloniaProperty.Register<EnvelopeEditor, PatchEditorViewModel?>(nameof(Editor));

    /// <summary>Width, in units of the square-root time scale, of the stretch where the note is held.</summary>
    private const double HoldWidth = 0.6;

    /// <summary>Pixels within which the pointer takes a point.</summary>
    private const double HitRadius = 8;

    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B1220"));
    private static readonly IBrush HeldFill = new SolidColorBrush(Color.Parse("#22D3EE"), 0.07);
    private static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#22D3EE"), 0.15);
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#22D3EE"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly Pen Line = new(Accent, 1.5);
    private static readonly Pen HeldLine = new(Accent, 1.2, new DashStyle([3, 3], 0));
    private static readonly Pen Frame = new(new SolidColorBrush(Color.Parse("#334155")), 1);
    private static readonly Pen ZeroLine = new(Muted, 1, new DashStyle([1.5, 3], 0));
    private static readonly Pen PointRing = new(new SolidColorBrush(Color.Parse("#0B1220")), 1.5);

    /// <summary>The point being dragged; -1 for none.</summary>
    private int _dragged = -1;

    /// <summary>The time scale and level range when the drag began, kept so that the graph does not move under the pointer.</summary>
    private (double Total, double Low, double High)? _frozen;

    private TopLevel? _escapeSource;
    private IPointer? _pointer;
    private bool _overPoint;

    static EnvelopeEditor()
    {
        AffectsRender<EnvelopeEditor>(EnvelopeProperty, KindProperty);
        FocusableProperty.OverrideDefaultValue<EnvelopeEditor>(false);
    }

    /// <inheritdoc cref="EnvelopeProperty"/>
    public OperatorEnvelope? Envelope
    {
        get => GetValue(EnvelopeProperty);
        set => SetValue(EnvelopeProperty, value);
    }

    /// <inheritdoc cref="KindProperty"/>
    public EnvelopeKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <inheritdoc cref="PartProperty"/>
    public PatchPart? Part
    {
        get => GetValue(PartProperty);
        set => SetValue(PartProperty, value);
    }

    /// <inheritdoc cref="EditorProperty"/>
    public PatchEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    private EnvelopeRef? Reference => Part is { } part ? new EnvelopeRef(part, Kind) : null;

    /// <summary>Where a point is drawn, in the control's coordinates.</summary>
    /// <param name="index">The point.</param>
    public Point PointPosition(int index)
    {
        OperatorEnvelope envelope = Envelope ?? throw new InvalidOperationException("No envelope is shown.");
        Layout layout = Measure(envelope);
        return new Point(layout.X[index], layout.Y(envelope.Points[index].Level));
    }

    protected override Size MeasureOverride(Size availableSize) => new(double.IsFinite(availableSize.Width) ? availableSize.Width : 240, 96);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Background, Frame, bounds.Deflate(0.5), 5, 5);
        if (Envelope is not { } envelope || envelope.Points.Count < 2)
        {
            return;
        }

        Layout layout = Measure(envelope);
        IReadOnlyList<EnvelopePoint> points = envelope.Points;
        double baseline = layout.Y(Math.Clamp(0, layout.Low, layout.High));
        if (Kind == EnvelopeKind.Pitch)
        {
            context.DrawLine(ZeroLine, new Point(layout.Left, baseline), new Point(layout.Right, baseline));
        }

        // The stretch where the note is held, shaded, with its level dashed across it.
        if (envelope.SustainPoint is int held && layout.HoldStart is double holdStart)
        {
            double holdEnd = layout.X[held + 1] - layout.SegmentWidth(held + 1);
            context.DrawRectangle(HeldFill, null, new Rect(holdStart, layout.Top, Math.Max(0, holdEnd - holdStart), layout.Bottom - layout.Top));
            double y = layout.Y(points[held].Level);
            context.DrawLine(HeldLine, new Point(holdStart, y), new Point(holdEnd, y));
            DrawText(context, "held", new Point(holdStart + 3, layout.Top + 1), Muted);
        }

        // The segments, each on its curve.
        var outline = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext line = outline.Open())
        using (StreamGeometryContext fill = area.Open())
        {
            var start = new Point(layout.X[0], layout.Y(points[0].Level));
            line.BeginFigure(start, false);
            fill.BeginFigure(new Point(start.X, baseline), true);
            fill.LineTo(start);
            for (int i = 1; i < points.Count; i++)
            {
                double from = layout.X[i] - layout.SegmentWidth(i);
                if (from > layout.X[i - 1])
                {
                    // Across the held stretch: the level stays.
                    var flat = new Point(from, layout.Y(points[i - 1].Level));
                    line.LineTo(flat);
                    fill.LineTo(flat);
                }

                foreach (Point p in Segment(envelope, i, from, layout))
                {
                    line.LineTo(p);
                    fill.LineTo(p);
                }
            }

            fill.LineTo(new Point(layout.X[^1], baseline));
            line.EndFigure(false);
            fill.EndFigure(true);
        }

        context.DrawGeometry(Fill, null, area);
        context.DrawGeometry(null, Line, outline);

        // The points, the sustain point in amber.
        for (int i = 0; i < points.Count; i++)
        {
            var at = new Point(layout.X[i], layout.Y(points[i].Level));
            double radius = i == _dragged ? 5.5 : 4.5;
            context.DrawEllipse(i == envelope.SustainPoint ? Amber : Accent, PointRing, at, radius, radius);
        }

        string range = Kind == EnvelopeKind.Pitch
            ? string.Create(CultureInfo.InvariantCulture, $"±{layout.High:0} ct")
            : string.Create(CultureInfo.InvariantCulture, $"{layout.High:0.##}");
        DrawText(context, range, new Point(bounds.Right - 6 - Text(range, Muted).Width, layout.Top + 1), Muted);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Envelope is not { } envelope || Editor is not { HasSound: true } editor || Reference is not { } reference)
        {
            return;
        }

        Point at = e.GetPosition(this);
        Layout layout = Measure(envelope);
        int hit = Hit(envelope, layout, at);
        PointerPointProperties pointer = e.GetCurrentPoint(this).Properties;
        if (pointer.IsRightButtonPressed)
        {
            // The menu opens when the button comes up; away from a point there is none.
            ContextMenu = hit >= 0 ? PointMenu(envelope, editor, reference, hit) : null;
            return;
        }

        if (!pointer.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            if (hit < 0 && layout.SegmentAt(at.X) is int segment)
            {
                editor.Apply(EnvelopeEdits.AddPoint(reference, segment));
            }

            e.Handled = true;
            return;
        }

        if (hit < 0)
        {
            return;
        }

        _dragged = hit;
        _frozen = (layout.Total, layout.Low, layout.High);
        editor.BeginGesture();
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        WatchEscape(true);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragged < 0 || Envelope is not { } envelope || Editor is not { } editor || Reference is not { } reference)
        {
            bool over = Envelope is { } shown && Hit(shown, Measure(shown), e.GetPosition(this)) >= 0;
            if (over != _overPoint)
            {
                _overPoint = over;
                Cursor = over ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            }

            return;
        }

        // Time from where the segment into the point starts, level from the height, both on the scale of the drag's start.
        Layout layout = Measure(envelope);
        Point at = e.GetPosition(this);
        double time = 0;
        if (_dragged > 0)
        {
            double from = layout.X[_dragged] - layout.SegmentWidth(_dragged);
            double units = Math.Max(0, (at.X - from) / layout.Width * layout.Total);
            time = Math.Pow(Math.Max(0, units - 0.02), 2);
        }

        editor.Preview(EnvelopeEdits.MovePoint(reference, _dragged, time, layout.Level(at.Y)));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragged >= 0)
        {
            EndDrag(cancel: false);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag(cancel: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        EndDrag(cancel: false);
        base.OnDetachedFromVisualTree(e);
    }

    private void EndDrag(bool cancel)
    {
        if (_dragged < 0)
        {
            return;
        }

        _dragged = -1;
        _frozen = null;
        WatchEscape(false);
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        if (cancel)
        {
            Editor?.CancelGesture();
        }
        else
        {
            Editor?.EndGesture();
        }

        InvalidateVisual();
    }

    /// <summary>Escape cancels a drag wherever the keyboard focus is.</summary>
    private void WatchEscape(bool on)
    {
        _escapeSource?.RemoveHandler(KeyDownEvent, OnEscape);
        _escapeSource = on ? TopLevel.GetTopLevel(this) : null;
        _escapeSource?.AddHandler(KeyDownEvent, OnEscape, RoutingStrategies.Tunnel);
    }

    private void OnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragged >= 0)
        {
            EndDrag(cancel: true);
            e.Handled = true;
        }
    }

    /// <summary>The menu of a point: remove it, hold at it, run once, and the curve of the segment that ends at it.</summary>
    private static ContextMenu PointMenu(OperatorEnvelope envelope, PatchEditorViewModel editor, EnvelopeRef reference, int index)
    {
        MenuItem Item(string header, bool enabled, PatchEdit edit)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => editor.Apply(edit);
            return item;
        }

        int count = envelope.Points.Count;
        var curve = new MenuItem { Header = "Curve", IsEnabled = index > 0 };
        foreach ((string name, EnvelopeCurve shape) in new[] { ("Linear", EnvelopeCurve.Linear), ("Exponential", EnvelopeCurve.Exponential), ("Step", EnvelopeCurve.Step) })
        {
            MenuItem choice = Item(name, true, EnvelopeEdits.SetCurve(reference, index, shape));
            choice.ToggleType = MenuItemToggleType.Radio;
            choice.IsChecked = envelope.Points[index].Curve == shape;
            curve.Items.Add(choice);
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("Remove", index > 0 && count > 2, EnvelopeEdits.RemovePoint(reference, index)));
        menu.Items.Add(Item("Sustain here", index >= 1 && index <= count - 2 && envelope.SustainPoint != index, EnvelopeEdits.SetSustain(reference, index)));
        menu.Items.Add(Item("One-shot", envelope.SustainPoint is not null, EnvelopeEdits.SetSustain(reference, null)));
        menu.Items.Add(curve);
        return menu;
    }

    /// <summary>The nearest point within reach of the pointer; -1 for none.</summary>
    private static int Hit(OperatorEnvelope envelope, Layout layout, Point at)
    {
        int best = -1;
        double bestDistance = HitRadius;
        for (int i = 0; i < envelope.Points.Count; i++)
        {
            double dx = layout.X[i] - at.X, dy = layout.Y(envelope.Points[i].Level) - at.Y;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance <= bestDistance)
            {
                (best, bestDistance) = (i, distance);
            }
        }

        return best;
    }

    /// <summary>
    /// The points of the segment that ends at point <paramref name="i"/>, from <paramref name="fromX"/>: straight, an
    /// exponential curve (between the levels as rendered), or a jump to the level of its end that then stays.
    /// </summary>
    private static IEnumerable<Point> Segment(OperatorEnvelope envelope, int i, double fromX, Layout layout)
    {
        EnvelopePoint end = envelope.Points[i];
        double a = envelope.EffectiveLevel(i - 1), b = envelope.EffectiveLevel(i);
        double toX = layout.X[i];
        switch (end.Curve)
        {
            case EnvelopeCurve.Step:
                yield return new Point(fromX, layout.Y(end.Level));
                yield return new Point(toX, layout.Y(end.Level));
                break;
            case EnvelopeCurve.Exponential when a > 0 && b > 0 && toX - fromX > 2:
                for (int k = 1; k <= 16; k++)
                {
                    double t = k / 16.0;
                    yield return new Point(fromX + (t * (toX - fromX)), layout.Y(a * Math.Pow(b / a, t)));
                }

                break;
            default:
                yield return new Point(toX, layout.Y(end.Level));
                break;
        }
    }

    /// <summary>Where the points go: x of each point, and the level range, frozen while a point is dragged.</summary>
    private Layout Measure(OperatorEnvelope envelope)
    {
        IReadOnlyList<EnvelopePoint> points = envelope.Points;
        var units = new double[points.Count];
        double? holdUnits = null;
        for (int i = 1; i < points.Count; i++)
        {
            units[i] = units[i - 1];
            if (i == envelope.ReleasePoint && envelope.SustainPoint is not null)
            {
                holdUnits = units[i];
                units[i] += HoldWidth;
            }

            units[i] += Math.Sqrt(Math.Max(0.0, points[i].TimeSeconds)) + 0.02;
        }

        // Some room after the last point, so that it can be dragged to a longer time.
        (double total, double low, double high) = _frozen ?? (Math.Max(0.1, units[^1] * 1.15), 0, 0);
        if (_frozen is null)
        {
            (low, high) = LevelRange(envelope);
        }

        var bounds = new Rect(Bounds.Size).Deflate(new Thickness(8, 14, 8, 8));
        double[] x = [.. units.Select(u => bounds.Left + (u / total * bounds.Width))];
        double? holdStart = holdUnits is double h ? bounds.Left + (h / total * bounds.Width) : null;
        return new Layout(x, total, low, high, bounds, holdStart, envelope);
    }

    /// <summary>The levels the graph spans: 0 to 1 (or to a higher level the envelope has); for the pitch, ± a round number of cents above its largest swing.</summary>
    private (double Low, double High) LevelRange(OperatorEnvelope envelope)
    {
        double largest = envelope.Points.Max(static p => Math.Abs(p.Level));
        if (Kind != EnvelopeKind.Pitch)
        {
            return (Math.Min(0, envelope.Points.Min(static p => p.Level)), Math.Max(1, largest));
        }

        double reach = 50;
        while (reach < 1200 && reach < largest * 1.25)
        {
            reach *= 2;
        }

        reach = Math.Min(Math.Max(reach, largest), Math.Max(1200, largest));
        return (-reach, reach);
    }

    private static FormattedText Text(string text, IBrush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9.5, brush);

    private static void DrawText(DrawingContext context, string text, Point at, IBrush brush) => context.DrawText(Text(text, brush), at);

    /// <summary>The placement of an envelope's points in the control.</summary>
    private sealed record Layout(double[] X, double Total, double Low, double High, Rect Area, double? HoldStart, OperatorEnvelope Envelope)
    {
        public double Left => Area.Left;

        public double Right => Area.Right;

        public double Top => Area.Top;

        public double Bottom => Area.Bottom;

        public double Width => Area.Width;

        /// <summary>The height of a level, clamped to the graph.</summary>
        public double Y(double level) => Area.Bottom - (Math.Clamp((level - Low) / (High - Low), 0, 1) * Area.Height);

        /// <summary>The level at a height.</summary>
        public double Level(double y) => Low + (Math.Clamp((Area.Bottom - y) / Area.Height, 0, 1) * (High - Low));

        /// <summary>The width of the segment that ends at a point, without the held stretch before it.</summary>
        public double SegmentWidth(int i) => (Math.Sqrt(Math.Max(0.0, Envelope.Points[i].TimeSeconds)) + 0.02) / Total * Area.Width;

        /// <summary>The segment under an x position, by the point it starts at; null outside every segment.</summary>
        public int? SegmentAt(double x)
        {
            for (int i = 0; i + 1 < X.Length; i++)
            {
                if (x >= X[i] && x <= X[i + 1])
                {
                    return i;
                }
            }

            return null;
        }
    }
}
