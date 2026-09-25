using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>
/// The operator routing of a patch drawn as an FM block diagram: carriers on the bottom row feeding the output bus,
/// every modulator one row above the highest operator it modulates. Edges going down are drawn straight; edges that
/// go up or sideways (feedback through other operators) are dashed curves; self-feedback is a loop beside the operator.
/// Only operators that can be heard are drawn.
/// </summary>
public sealed class FmDiagram : Control
{
    /// <summary>Patch to draw; nothing is drawn without one.</summary>
    public static readonly StyledProperty<OperatorMatrixPatch?> PatchProperty = AvaloniaProperty.Register<FmDiagram, OperatorMatrixPatch?>(nameof(Patch));

    private static readonly IBrush BoxFill = new SolidColorBrush(Color.Parse("#1E293B"));
    private static readonly IBrush CarrierFill = new SolidColorBrush(Color.Parse("#164E63"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#22D3EE"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E2E8F0"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly Pen BoxPen = new(new SolidColorBrush(Color.Parse("#475569")), 1.2);
    private static readonly Pen EdgePen = new(Accent, 1.5);
    private static readonly Pen FeedbackPen = new(Amber, 1.5, new DashStyle([4, 3], 0));
    private static readonly Pen LoopPen = new(Amber, 1.5);
    private static readonly Pen OutputPen = new(Muted, 1.5);

    static FmDiagram()
    {
        AffectsRender<FmDiagram>(PatchProperty);
    }

    /// <inheritdoc cref="PatchProperty"/>
    public OperatorMatrixPatch? Patch
    {
        get => GetValue(PatchProperty);
        set => SetValue(PatchProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        OperatorMatrixPatch? patch = Patch;
        if (patch is null)
        {
            DrawText(context, "Run a search to see the block diagram of the patch here.", new Point(12, 12), 12, Muted);
            return;
        }

        int n = patch.OperatorCount;
        bool[] audible = patch.FindAudibleOperators();
        bool[] carrier = [.. Enumerable.Range(0, n).Select(i => audible[i] && patch.OutputLevels[i] != 0.0)];
        var edges = patch.ModulationMatrix.Edges.Where(e => e.Enabled && e.Amount != 0.0 && audible[e.SourceOperator] && audible[e.DestinationOperator]).ToList();

        // Feedback edges close a cycle: found by a depth-first walk from the carriers up through their modulators, as the
        // edges that lead back to an operator still on the walk. The others form a stack of layers.
        var feedback = new HashSet<ModulationEdge>();
        var state = new int[n]; // 0 unvisited, 1 on the walk, 2 done
        void Walk(int op)
        {
            state[op] = 1;
            foreach (ModulationEdge e in edges.Where(e => e.DestinationOperator == op && !e.IsSelfFeedback))
            {
                if (state[e.SourceOperator] == 1 || carrier[e.SourceOperator])
                {
                    feedback.Add(e);
                }
                else if (state[e.SourceOperator] == 0)
                {
                    Walk(e.SourceOperator);
                }
            }

            state[op] = 2;
        }

        foreach (int c in Enumerable.Range(0, n).Where(i => carrier[i]))
        {
            Walk(c);
        }

        // Rows: carriers at level 0, every modulator one above the highest operator it modulates.
        var level = new int[n];
        var forward = edges.Where(e => !e.IsSelfFeedback && !feedback.Contains(e)).ToList();
        for (int pass = 0; pass < n; pass++)
        {
            foreach (ModulationEdge e in forward)
            {
                level[e.SourceOperator] = Math.Max(level[e.SourceOperator], level[e.DestinationOperator] + 1);
            }
        }

        int[] shown = [.. Enumerable.Range(0, n).Where(i => audible[i])];
        if (shown.Length == 0)
        {
            DrawText(context, "The patch is silent.", new Point(12, 12), 12, Muted);
            return;
        }

        int top = shown.Max(i => level[i]);
        var rows = Enumerable.Range(0, top + 1).Select(l => shown.Where(i => level[i] == l).ToList()).ToList();
        int widest = Math.Max(1, rows.Max(static r => r.Count));

        double width = Bounds.Width, height = Bounds.Height;
        double slot = width / widest;
        double boxW = Math.Min(96, slot * 0.7), boxH = 46;
        double busY = height - 28;
        // Room above the top row for self-feedback loops and their amounts.
        double rowH = top == 0 ? 0 : Math.Min(130, (busY - 40 - boxH - 44) / top);
        var centers = new Point[n];
        for (int l = 0; l <= top; l++)
        {
            List<int> row = rows[l];
            if (l > 0)
            {
                // Above the operators each one modulates, so that stacks stand straight.
                row.Sort((a, b) => Barycenter(a) != Barycenter(b) ? Barycenter(a).CompareTo(Barycenter(b)) : a.CompareTo(b));
            }

            double y = busY - 40 - (l * rowH) - (boxH / 2);
            for (int k = 0; k < row.Count; k++)
            {
                centers[row[k]] = new Point((width / 2) + ((k - ((row.Count - 1) / 2.0)) * slot), y);
            }
        }

        double Barycenter(int op)
        {
            var targets = edges.Where(e => e.SourceOperator == op && !e.IsSelfFeedback && level[e.DestinationOperator] < level[op]).Select(e => centers[e.DestinationOperator].X).ToList();
            return targets.Count > 0 ? targets.Average() : double.MaxValue;
        }

        // Output bus and the carriers feeding it.
        double[] carrierX = [.. shown.Where(i => carrier[i]).Select(i => centers[i].X)];
        double busLeft = carrierX.Min() - (boxW / 2), busRight = carrierX.Max() + (boxW / 2);
        context.DrawLine(OutputPen, new Point(busLeft, busY), new Point(busRight, busY));
        DrawText(context, "OUT", new Point(busRight + 8, busY - 8), 11, Muted);
        foreach (int i in shown.Where(i => carrier[i]))
        {
            var from = new Point(centers[i].X, centers[i].Y + (boxH / 2));
            var to = new Point(centers[i].X, busY);
            context.DrawLine(OutputPen, from, to);
            DrawArrowHead(context, OutputPen.Brush!, from, to);
            DrawText(context, patch.OutputLevels[i].ToString("0.##", CultureInfo.InvariantCulture), new Point(centers[i].X + 5, ((from.Y + to.Y) / 2) - 7), 10, Muted);
        }

        // Edges under the boxes.
        int rightCurves = 0, leftCurves = 0, arcs = 0;
        foreach (ModulationEdge e in edges)
        {
            Point s = centers[e.SourceOperator], d = centers[e.DestinationOperator];
            string amount = e.Amount.ToString("0.##", CultureInfo.InvariantCulture) + (e.AmountEnvelope is null ? string.Empty : " ~");
            if (e.IsSelfFeedback)
            {
                // A loop out of the right side and back into the top-right corner.
                var start = new Point(s.X + (boxW / 2), s.Y);
                var end = new Point(s.X + (boxW / 2) - 10, s.Y - (boxH / 2));
                var geometry = new StreamGeometry();
                using (StreamGeometryContext g = geometry.Open())
                {
                    g.BeginFigure(start, false);
                    g.CubicBezierTo(new Point(start.X + 28, start.Y), new Point(end.X + 10, end.Y - 28), end);
                    g.EndFigure(false);
                }

                context.DrawGeometry(null, LoopPen, geometry);
                DrawArrowHead(context, Amber, new Point(end.X + 4, end.Y - 12), end);
                DrawText(context, amount, new Point(start.X + 16, end.Y - 26), 10, Amber);
            }
            else if (level[e.SourceOperator] == level[e.DestinationOperator])
            {
                // Within one row: an arc under the row, from the bottom of the one box to the bottom of the other.
                bool sideways = carrier[e.SourceOperator] && carrier[e.DestinationOperator];
                double shift = s.X < d.X ? boxW / 4 : -boxW / 4;
                var from = new Point(s.X + shift, s.Y + (boxH / 2));
                var to = new Point(d.X - shift, d.Y + (boxH / 2));
                double dip = from.Y + 12 + (10 * arcs++);
                var geometry = new StreamGeometry();
                using (StreamGeometryContext g = geometry.Open())
                {
                    g.BeginFigure(from, false);
                    g.CubicBezierTo(new Point(from.X, dip), new Point(to.X, dip), to);
                    g.EndFigure(false);
                }

                IBrush brush = sideways ? Accent : Amber;
                context.DrawGeometry(null, sideways ? EdgePen : FeedbackPen, geometry);
                DrawArrowHead(context, brush, new Point(to.X, to.Y + 10), to);
                DrawText(context, amount, new Point(from.X + ((to.X - from.X) * 0.3) - 10, dip - 12), 10, brush);
            }
            else if (!feedback.Contains(e) && level[e.SourceOperator] == level[e.DestinationOperator] + 1)
            {
                var from = new Point(s.X, s.Y + (boxH / 2));
                var to = new Point(d.X, d.Y - (boxH / 2));
                context.DrawLine(EdgePen, from, to);
                DrawArrowHead(context, Accent, from, to);
                DrawText(context, amount, new Point(((from.X + to.X) / 2) + 5, ((from.Y + to.Y) / 2) - 7), 10, Accent);
            }
            else if (!feedback.Contains(e))
            {
                // Down past other rows: a curve around the right side, wider the more rows it skips.
                int span = level[e.SourceOperator] - level[e.DestinationOperator];
                double offset = Math.Min(18, 4 + (5 * span));
                var from = new Point(s.X + (boxW / 2), s.Y + offset);
                var to = new Point(d.X + (boxW / 2), d.Y - offset);
                int stacked = rightCurves++;
                double bulge = Math.Max(from.X, to.X) + 14 + (18 * span) + (8 * stacked);
                bulge = Math.Clamp(bulge, 40, width - 40);
                DrawCurve(context, EdgePen, from, to, bulge);
                DrawArrowHead(context, Accent, new Point(to.X + 12, to.Y), to);
                DrawText(context, amount, new Point(bulge + 3, ((from.Y + to.Y) / 2) - 7 + (12 * (stacked % 3))), 10, Accent);
            }
            else
            {
                // Feedback up through other operators: a dashed curve around the side the source is on.
                int span = Math.Abs(level[e.SourceOperator] - level[e.DestinationOperator]);
                double offset = Math.Min(18, 4 + (5 * span));
                double side = s.X > d.X + 1 ? 1 : -1;
                int stacked = side > 0 ? rightCurves++ : leftCurves++;
                var from = new Point(s.X + (side * boxW / 2), s.Y + offset);
                var to = new Point(d.X + (side * boxW / 2), d.Y - offset);
                double bulge = (side > 0 ? Math.Max(from.X, to.X) : Math.Min(from.X, to.X)) + (side * (14 + (18 * (span + 1)) + (8 * stacked)));
                bulge = Math.Clamp(bulge, 40, width - 40);
                DrawCurve(context, FeedbackPen, from, to, bulge);
                DrawArrowHead(context, Amber, new Point(to.X + (side * 12), to.Y), to);
                DrawText(context, amount, new Point(side > 0 ? bulge + 3 : bulge - 34, ((from.Y + to.Y) / 2) - 7 + (12 * (stacked % 3))), 10, Amber);
            }
        }

        // Operator boxes on top.
        foreach (int i in shown)
        {
            var rect = new Rect(centers[i].X - (boxW / 2), centers[i].Y - (boxH / 2), boxW, boxH);
            context.DrawRectangle(carrier[i] ? CarrierFill : BoxFill, BoxPen, rect, 6, 6);
            DrawText(context, "OP" + (i + 1), new Point(rect.X + 7, rect.Y + 5), 12, Text, FontWeight.SemiBold);
            DrawText(context, Describe(patch.Operators[i]), new Point(rect.X + 7, rect.Y + 25), 10, Muted, maxWidth: boxW - 10);
        }
    }

    /// <summary>Frequency, and waveform when it is not a sine, in a few characters.</summary>
    private static string Describe(OperatorPatch op)
    {
        string frequency = op.Frequency.Mode == OperatorFrequencyMode.Fixed
            ? op.Frequency.FixedFrequencyHz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz"
            : "×" + op.Frequency.Ratio.ToString("0.###", CultureInfo.InvariantCulture);
        if (op.FineTuneCents != 0)
        {
            frequency += (op.FineTuneCents > 0 ? " +" : " ") + op.FineTuneCents.ToString("0", CultureInfo.InvariantCulture) + "c";
        }

        return op.Waveform == OperatorWaveform.Sine ? frequency : frequency + " " + op.Waveform.ToString().ToLowerInvariant();
    }

    /// <summary>A curve from <paramref name="from"/> to <paramref name="to"/> that swings out to <paramref name="bulgeX"/>.</summary>
    private static void DrawCurve(DrawingContext context, Pen pen, Point from, Point to, double bulgeX)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(from, false);
            g.CubicBezierTo(new Point(bulgeX, from.Y), new Point(bulgeX, to.Y), to);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawArrowHead(DrawingContext context, IBrush brush, Point from, Point to)
    {
        Vector direction = to - from;
        double length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        if (length < 1e-6)
        {
            return;
        }

        Vector u = direction / length;
        var normal = new Vector(-u.Y, u.X);
        Point back = to - (u * 8);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(back + (normal * 4));
            g.LineTo(back - (normal * 4));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, FontWeight weight = FontWeight.Normal, double maxWidth = double.PositiveInfinity)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, weight), size, brush);
        if (double.IsFinite(maxWidth))
        {
            formatted.MaxTextWidth = maxWidth;
            formatted.Trimming = TextTrimming.CharacterEllipsis;
            formatted.MaxLineCount = 1;
        }

        context.DrawText(formatted, at);
    }
}
