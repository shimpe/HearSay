using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using HearSay.Synthesis.Oscillators;
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
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.Parse("#0B1220"));
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
        // The output label needs room right of the bus.
        double usable = width - 36;
        double slot = usable / widest;

        // Wide enough gaps between neighbours for an arrow and its amount.
        double boxW = Math.Min(96, Math.Max(56, slot - 44)), boxH = 46;
        double busY = height - 28;

        // Room above the top row for self-feedback loops and their amounts.
        double rowH = top == 0 ? 0 : Math.Min(130, (busY - 40 - boxH - 44) / top);
        var centers = new Point[n];
        var position = new int[n];
        for (int l = 0; l <= top; l++)
        {
            List<int> row = Arrange(rows[l], l);
            double y = busY - 40 - (l * rowH) - (boxH / 2);
            for (int k = 0; k < row.Count; k++)
            {
                centers[row[k]] = new Point((usable / 2) + ((k - ((row.Count - 1) / 2.0)) * slot), y);
                position[row[k]] = k;
            }
        }

        // The order of a row with the fewest long edges: operators modulating each other within the row next to each
        // other, and every modulator above the operators it modulates, so that stacks stand straight. Rows hold at
        // most six operators, so every order is tried; the first best one wins, which keeps operator order on ties.
        List<int> Arrange(List<int> row, int l)
        {
            var within = edges.Where(e => !e.IsSelfFeedback && level[e.SourceOperator] == l && level[e.DestinationOperator] == l).ToList();
            double Cost(List<int> order)
            {
                double cost = 0;
                foreach (ModulationEdge e in within)
                {
                    cost += 2.0 * (Math.Abs(order.IndexOf(e.SourceOperator) - order.IndexOf(e.DestinationOperator)) - 1);
                }

                for (int k = 0; k < order.Count; k++)
                {
                    double x = (usable / 2) + ((k - ((order.Count - 1) / 2.0)) * slot);
                    foreach (ModulationEdge e in edges.Where(e => e.SourceOperator == order[k] && !e.IsSelfFeedback && level[e.DestinationOperator] < l))
                    {
                        cost += Math.Abs(x - centers[e.DestinationOperator].X) / slot;
                    }
                }

                return cost;
            }

            List<int> best = [.. row.Order()];
            double bestCost = Cost(best);
            foreach (List<int> order in Permutations(best))
            {
                double cost = Cost(order);
                if (cost < bestCost - 1e-9)
                {
                    (best, bestCost) = (order, cost);
                }
            }

            return best;
        }

        // Amounts and levels are drawn last, over everything else, so that no edge runs through them.
        var labels = new List<(string Text, Point At, IBrush Brush)>();

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
            labels.Add((patch.OutputLevels[i].ToString("0.##", CultureInfo.InvariantCulture), new Point(centers[i].X + 5, busY - 15), Muted));
        }

        // Straight lines the curves should stay off: edges to the row below and the output lines.
        Rect BoxAt(int op) => new(centers[op].X - (boxW / 2), centers[op].Y - (boxH / 2), boxW, boxH);
        var straight = new List<(Point From, Point To)>();
        foreach (ModulationEdge e in edges.Where(e => !e.IsSelfFeedback && !feedback.Contains(e) && level[e.SourceOperator] == level[e.DestinationOperator] + 1))
        {
            straight.Add((new Point(centers[e.SourceOperator].X, centers[e.SourceOperator].Y + (boxH / 2)), new Point(centers[e.DestinationOperator].X, centers[e.DestinationOperator].Y - (boxH / 2))));
        }

        foreach (int i in shown.Where(i => carrier[i]))
        {
            straight.Add((new Point(centers[i].X, centers[i].Y + (boxH / 2)), new Point(centers[i].X, busY)));
        }

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
                DrawArrowCurve(context, LoopPen, Amber, start, new Point(start.X + 28, start.Y), new Point(end.X + 10, end.Y - 28), end);
                labels.Add((amount, new Point(start.X + 16, end.Y - 26), Amber));
            }
            else if (level[e.SourceOperator] == level[e.DestinationOperator])
            {
                bool sideways = carrier[e.SourceOperator] && carrier[e.DestinationOperator];
                IBrush brush = sideways ? Accent : Amber;
                Pen pen = sideways ? EdgePen : FeedbackPen;
                double direction = s.X < d.X ? 1 : -1;
                if (Math.Abs(position[e.SourceOperator] - position[e.DestinationOperator]) == 1)
                {
                    // Neighbours: a straight arrow across the gap, left to right above the middle, back below it.
                    double y = s.Y - (direction * 8);
                    var from = new Point(s.X + (direction * boxW / 2), y);
                    var to = new Point(d.X - (direction * boxW / 2), y);
                    context.DrawLine(pen, from, to);
                    DrawArrowHead(context, brush, from, to);
                    labels.Add((amount, new Point(((from.X + to.X) / 2) - 10, direction > 0 ? y - 15 : y + 2), brush));
                }
                else
                {
                    // Further apart: an arc over the row, clear of the output lines below it.
                    var from = new Point(s.X + (direction * boxW / 4), s.Y - (boxH / 2));
                    var to = new Point(d.X - (direction * boxW / 4), d.Y - (boxH / 2));
                    double peak = from.Y - 26 - (10 * arcs++);
                    DrawArrowCurve(context, pen, brush, from, new Point(from.X, peak), new Point(to.X, peak), to);
                    double highest = from.Y + (0.75 * (peak - from.Y));
                    labels.Add((amount, new Point(((from.X + to.X) / 2) - 10, highest - 15), brush));
                }
            }
            else if (!feedback.Contains(e) && level[e.SourceOperator] == level[e.DestinationOperator] + 1)
            {
                var from = new Point(s.X, s.Y + (boxH / 2));
                var to = new Point(d.X, d.Y - (boxH / 2));
                context.DrawLine(EdgePen, from, to);
                DrawArrowHead(context, Accent, from, to);
                labels.Add((amount, new Point(((from.X + to.X) / 2) + 5, ((from.Y + to.Y) / 2) - 7), Accent));
            }
            else
            {
                // Past other rows, down (solid) or back up as feedback (dashed): a curve around whichever side it
                // disturbs least, wider the more rows it spans.
                bool back = feedback.Contains(e);
                int span = Math.Abs(level[e.SourceOperator] - level[e.DestinationOperator]);
                double offset = Math.Min(18, 4 + (5 * span));
                double reach = 14 + (18 * (back ? span + 1 : span));
                (Point From, Point To, double Bulge) Route(double side, int stacked)
                {
                    var from = new Point(s.X + (side * boxW / 2), s.Y + offset);
                    var to = new Point(d.X + (side * boxW / 2), d.Y - offset);
                    double edge = side > 0 ? Math.Max(from.X, to.X) : Math.Min(from.X, to.X);
                    return (from, to, Math.Clamp(edge + (side * (reach + (8 * stacked))), 40, width - 40));
                }

                double Disturbance(double side)
                {
                    (Point from, Point to, double bulge) = Route(side, side > 0 ? rightCurves : leftCurves);
                    var c1 = new Point(bulge, from.Y);
                    var c2 = new Point(bulge, to.Y);
                    double cost = 0;
                    Point previous = from;
                    for (int k = 1; k <= 24; k++)
                    {
                        Point p = Bezier(from, c1, c2, to, k / 24.0);
                        cost += shown.Count(i => i != e.SourceOperator && i != e.DestinationOperator && BoxAt(i).Inflate(4).Contains(p));
                        cost += straight.Count(line => Cross(previous, p, line.From, line.To));
                        previous = p;
                    }

                    return cost;
                }

                double toward = s.X < d.X - 1 ? -1 : 1;
                // Least disturbance first, then the side with fewer curves, then the side the source is on.
                double Cost(double side) => (10 * Disturbance(side)) + (side > 0 ? rightCurves : leftCurves);
                double chosen = Cost(toward) <= Cost(-toward) ? toward : -toward;
                int stacked = chosen > 0 ? rightCurves++ : leftCurves++;
                (Point start, Point end, double apex) = Route(chosen, stacked);
                IBrush brush = back ? Amber : Accent;
                DrawArrowCurve(context, back ? FeedbackPen : EdgePen, brush, start, new Point(apex, start.Y), new Point(apex, end.Y), end);
                // Beside the middle of the curve, on its outer side.
                Point middle = Bezier(start, new Point(apex, start.Y), new Point(apex, end.Y), end, 0.5);
                labels.Add((amount, new Point(chosen > 0 ? middle.X + 4 : middle.X - 30, middle.Y - 7 + (12 * (stacked % 3))), brush));
            }
        }

        // Operator boxes over the edges.
        foreach (int i in shown)
        {
            var rect = new Rect(centers[i].X - (boxW / 2), centers[i].Y - (boxH / 2), boxW, boxH);
            context.DrawRectangle(carrier[i] ? CarrierFill : BoxFill, BoxPen, rect, 6, 6);
            // The third line: fine tuning and detune, and the operator filter.
            OperatorPatch shownOp = patch.Operators[i];
            string tuning = string.Join(" ", new[] { DescribeTuning(shownOp), shownOp.Filter is null ? string.Empty : PatchDescription.FilterLabel(shownOp.Filter) }.Where(static s => s.Length > 0));
            if (tuning.Length == 0)
            {
                DrawText(context, "OP" + (i + 1), new Point(rect.X + 7, rect.Y + 5), 12, Text, FontWeight.SemiBold);
                DrawText(context, Describe(patch.Operators[i]), new Point(rect.X + 7, rect.Y + 25), 10, Muted, maxWidth: boxW - 10);
            }
            else
            {
                DrawText(context, "OP" + (i + 1), new Point(rect.X + 7, rect.Y + 2), 12, Text, FontWeight.SemiBold);
                DrawText(context, Describe(patch.Operators[i]), new Point(rect.X + 7, rect.Y + 18), 10, Muted, maxWidth: boxW - 10);
                DrawText(context, tuning, new Point(rect.X + 7, rect.Y + 31), 10, Muted, maxWidth: boxW - 10);
            }
        }

        // Labels on a patch of background, so that the lines under them do not cut through the digits.
        foreach ((string text, Point at, IBrush brush) in labels)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
            context.DrawRectangle(LabelBackground, null, new Rect(at.X - 2, at.Y, formatted.Width + 4, formatted.Height), 3, 3);
            context.DrawText(formatted, at);
        }
    }

    /// <summary>Point at <paramref name="t"/> on a cubic Bézier curve.</summary>
    private static Point Bezier(Point from, Point control1, Point control2, Point to, double t)
    {
        double u = 1 - t;
        return new Point(
            (u * u * u * from.X) + (3 * u * u * t * control1.X) + (3 * u * t * t * control2.X) + (t * t * t * to.X),
            (u * u * u * from.Y) + (3 * u * u * t * control1.Y) + (3 * u * t * t * control2.Y) + (t * t * t * to.Y));
    }

    /// <summary>Whether segments <c>a–b</c> and <c>c–d</c> cross.</summary>
    private static bool Cross(Point a, Point b, Point c, Point d)
    {
        static double Turn(Point p, Point q, Point r) => ((q.X - p.X) * (r.Y - p.Y)) - ((q.Y - p.Y) * (r.X - p.X));
        return Turn(a, b, c) * Turn(a, b, d) < 0 && Turn(c, d, a) * Turn(c, d, b) < 0;
    }

    /// <summary>All orders of <paramref name="items"/>, in lexicographic order of positions.</summary>
    private static IEnumerable<List<int>> Permutations(List<int> items)
    {
        if (items.Count <= 1)
        {
            yield return [.. items];
            yield break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            List<int> rest = [.. items.Take(i), .. items.Skip(i + 1)];
            foreach (List<int> tail in Permutations(rest))
            {
                yield return [items[i], .. tail];
            }
        }
    }

    /// <summary>Frequency, and waveform when it is not a sine, in a few characters; a tilde marks noise roughness.</summary>
    private static string Describe(OperatorPatch op)
    {
        string frequency = op.Frequency.Mode == OperatorFrequencyMode.Fixed
            ? op.Frequency.FixedFrequencyHz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz"
            : "×" + op.Frequency.Ratio.ToString("0.###", CultureInfo.InvariantCulture);
        if (op.Waveform == OperatorWaveform.Elliptic)
        {
            // The nome is the brightness; the morph shows how far towards sd, when it is not plain sn.
            string q = JacobiNome.FromParameter(op.Shape).ToString("0.##", CultureInfo.InvariantCulture);
            string sd = op.Morph >= 0.005 ? " sd" + op.Morph.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
            frequency = $"{frequency} ell q{q}{sd}";
        }
        else if (op.Waveform != OperatorWaveform.Sine)
        {
            frequency += " " + op.Waveform.ToString().ToLowerInvariant();
        }

        // Roughened by the shared noise, in phase or in m.
        bool rough = (op.NoisePhase != 0.0 && op.Waveform != OperatorWaveform.Noise) || (op.NoiseM != 0.0 && op.Waveform == OperatorWaveform.Elliptic);
        return rough ? frequency + " ~" : frequency;
    }

    /// <summary>
    /// Fine tune and detune, empty when both are zero, at the precision of the patch description: 0.1 cent and
    /// 0.01 Hz.
    /// </summary>
    private static string DescribeTuning(OperatorPatch op)
    {
        var parts = new List<string>(2);
        double cents = Math.Round(op.FineTuneCents, 1);
        if (cents != 0)
        {
            parts.Add((cents > 0 ? "+" : string.Empty) + cents.ToString("F1", CultureInfo.InvariantCulture) + " ct");
        }

        double detune = Math.Round(op.DetuneHz, 2);
        if (detune != 0)
        {
            parts.Add((detune > 0 ? "+" : string.Empty) + detune.ToString("F2", CultureInfo.InvariantCulture) + " Hz");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// A cubic Bézier curve with an arrowhead at its end, pointing along the curve: the head is aimed from the point
    /// a head's length back along the curve, not along the last control point, which a strongly bent curve leaves far
    /// behind.
    /// </summary>
    private static void DrawArrowCurve(DrawingContext context, Pen pen, IBrush brush, Point from, Point control1, Point control2, Point to)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(from, false);
            g.CubicBezierTo(control1, control2, to);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);

        // Walk back along the curve until a head's length away from its end.
        Point back = from;
        for (double t = 0.98; t > 0; t -= 0.02)
        {
            back = Bezier(from, control1, control2, to, t);
            if (Math.Sqrt(((to.X - back.X) * (to.X - back.X)) + ((to.Y - back.Y) * (to.Y - back.Y))) >= 8)
            {
                break;
            }
        }

        DrawArrowHead(context, brush, back, to);
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
