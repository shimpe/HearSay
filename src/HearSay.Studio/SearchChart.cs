using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HearSay.Objective;
using HearSay.OperatorMatrix.Search;

namespace HearSay.Studio;

/// <summary>A finalist as the chart marks it: its basin in the trace, and the label and origin the finalist list shows.</summary>
/// <param name="Basin">Its basin (<see cref="SearchTracePoint.Basin"/>).</param>
/// <param name="Label">Its label in the finalist list, such as #1.</param>
/// <param name="Origin">How its structure came about.</param>
public sealed record SearchChartFinalist(int Basin, string Label, string Origin);

/// <summary>
/// The course of a search: the loss (on a logarithmic scale) against the evaluations used, with a band per stage. Every
/// screened hypothesis is a dot; every basin is a step line that falls where its best loss improved. Losses compare
/// within a stage only (screening is coarse, refinement detailed, and on a target set the first stages judge the
/// reference note alone), so a line breaks where its stage changes. While the search runs the best line so far is
/// bright; afterwards the finalists have the colours of their place in the list, the selected one thickest, and a click
/// on one selects it.
/// </summary>
public sealed class SearchChart : Control
{
    /// <summary>The points of the search; nothing is drawn without any.</summary>
    public static readonly StyledProperty<IReadOnlyList<SearchTracePoint>?> TraceProperty = AvaloniaProperty.Register<SearchChart, IReadOnlyList<SearchTracePoint>?>(nameof(Trace));

    /// <summary>The evaluation budget: the width of the chart.</summary>
    public static readonly StyledProperty<int> BudgetProperty = AvaloniaProperty.Register<SearchChart, int>(nameof(Budget));

    /// <summary>The finalists in list order, once the search is done; null while it runs.</summary>
    public static readonly StyledProperty<IReadOnlyList<SearchChartFinalist>?> FinalistsProperty = AvaloniaProperty.Register<SearchChart, IReadOnlyList<SearchChartFinalist>?>(nameof(Finalists));

    /// <summary>Index of the selected finalist in <see cref="Finalists"/>; -1 for none.</summary>
    public static readonly StyledProperty<int> SelectedFinalistProperty = AvaloniaProperty.Register<SearchChart, int>(nameof(SelectedFinalist), -1);

    /// <summary>Colours of the finalists by their place in the list.</summary>
    internal static readonly Color[] FinalistColors =
    [
        Color.Parse("#22D3EE"), Color.Parse("#F59E0B"), Color.Parse("#A78BFA"), Color.Parse("#34D399"), Color.Parse("#60A5FA"), Color.Parse("#FB923C"),
    ];

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#334155"));
    private static readonly IBrush[] Bands = [new SolidColorBrush(Color.Parse("#0B1220")), new SolidColorBrush(Color.Parse("#111A2C"))];
    private static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#64748B"), 0.55);
    private static readonly Pen OtherLine = new(new SolidColorBrush(Color.Parse("#94A3B8"), 0.35), 1);

    private const double Left = 42, Right = 8, Top = 16, Bottom = 18;

    /// <summary>Where each finalist's line runs, from the last render, for clicks: segments in screen coordinates.</summary>
    private readonly List<(int Finalist, Point From, Point To)> _hits = [];

    private int _hover = -1;

    static SearchChart()
    {
        AffectsRender<SearchChart>(TraceProperty, BudgetProperty, FinalistsProperty, SelectedFinalistProperty);
    }

    /// <summary>A finalist was clicked: its index in <see cref="Finalists"/>.</summary>
    public event EventHandler<int>? FinalistClicked;

    /// <inheritdoc cref="TraceProperty"/>
    public IReadOnlyList<SearchTracePoint>? Trace
    {
        get => GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    /// <inheritdoc cref="BudgetProperty"/>
    public int Budget
    {
        get => GetValue(BudgetProperty);
        set => SetValue(BudgetProperty, value);
    }

    /// <inheritdoc cref="FinalistsProperty"/>
    public IReadOnlyList<SearchChartFinalist>? Finalists
    {
        get => GetValue(FinalistsProperty);
        set => SetValue(FinalistsProperty, value);
    }

    /// <inheritdoc cref="SelectedFinalistProperty"/>
    public int SelectedFinalist
    {
        get => GetValue(SelectedFinalistProperty);
        set => SetValue(SelectedFinalistProperty, value);
    }

    /// <summary>The short name of a stage, as its band is labelled.</summary>
    internal static string StageLabel(string stage) => stage switch
    {
        "hypothesis screening" => "screening",
        "survivor optimization" => "survivors",
        "deep optimization" => "deep",
        "finalist optimization" => "finalists",
        "local refinement" => "refinement",
        _ => stage,
    };

    /// <summary>
    /// Which losses compare: deep and finalist optimization judge the same thing at the same level, every other stage its
    /// own. A line is broken between groups.
    /// </summary>
    public static int ScaleGroup(SearchTracePoint point) => point.Stage switch
    {
        "hypothesis screening" => 0,
        "survivor optimization" => 1,
        "deep optimization" or "finalist optimization" => point.Level == EvaluationLevel.Normal ? 2 : 3,
        _ => 4,
    };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _hits.Clear();
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        IReadOnlyList<SearchTracePoint>? trace = Trace;
        if (trace is not { Count: > 0 })
        {
            return;
        }

        var plot = new Rect(Left, Top, Math.Max(10, Bounds.Width - Left - Right), Math.Max(10, Bounds.Height - Top - Bottom));
        double width = Math.Max(Budget, trace[^1].Evaluations);
        double X(int evaluations) => plot.Left + (plot.Width * evaluations / Math.Max(1, width));

        // The loss range: everything the basins reached, and most of the screening (its worst hypotheses would squash
        // the rest; they are drawn at the top edge).
        List<double> lines = [.. trace.Where(static p => p.Basin != 0 && p.Loss > 0 && double.IsFinite(p.Loss)).Select(static p => p.Loss)];
        List<double> screened = [.. trace.Where(static p => p.Basin == 0 && p.Loss > 0 && double.IsFinite(p.Loss)).Select(static p => p.Loss).Order()];
        double low = Math.Min(lines.DefaultIfEmpty(double.MaxValue).Min(), screened.DefaultIfEmpty(double.MaxValue).Min());
        double high = Math.Max(lines.DefaultIfEmpty(0).Max(), screened.Count > 0 ? screened[(int)(0.75 * (screened.Count - 1))] : 0);
        if (low >= double.MaxValue || high <= 0)
        {
            return;
        }

        double logLow = Math.Log10(low * 0.9), logHigh = Math.Log10(Math.Max(high * 1.1, low * 1.5));
        double Y(double loss) => plot.Bottom - (plot.Height * Math.Clamp((Math.Log10(Math.Max(loss, 1e-12)) - logLow) / (logHigh - logLow), 0, 1));

        // Stage bands, labelled, from where each stage began to where the next one did.
        var stages = new List<(string Stage, int Start)>();
        foreach (SearchTracePoint point in trace)
        {
            if (stages.Count == 0 || stages[^1].Stage != point.Stage)
            {
                stages.Add((point.Stage, stages.Count == 0 ? 0 : point.Evaluations));
            }
        }

        for (int s = 0; s < stages.Count; s++)
        {
            double from = X(stages[s].Start), to = s + 1 < stages.Count ? X(stages[s + 1].Start) : plot.Right;
            context.FillRectangle(Bands[s % 2], new Rect(from, plot.Top, Math.Max(0, to - from), plot.Height));
            DrawText(context, StageLabel(stages[s].Stage), new Point(from + 3, 1), 9, Muted, Math.Max(0, to - from - 4));
        }

        // Ticks of 1, 2 and 5 on the loss axis (decades alone when they are enough), and the budget on the other.
        foreach (double tick in Ticks(logLow, logHigh))
        {
            double y = Y(tick);
            context.DrawLine(new Pen(Grid, 0.5), new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(context, tick.ToString("0.####", CultureInfo.InvariantCulture), new Point(2, y - 6), 9, Muted, Left - 4);
        }

        DrawText(context, "loss", new Point(2, 1), 9, Muted, Left - 4);
        DrawText(context, "0", new Point(plot.Left, plot.Bottom + 3), 9, Muted, 40);
        string end = ((int)width).ToString("N0", CultureInfo.InvariantCulture) + " evaluations";
        DrawText(context, end, new Point(plot.Right - 110, plot.Bottom + 3), 9, Muted, 110, TextAlignment.Right);

        // The screened hypotheses.
        foreach (SearchTracePoint point in trace.Where(static p => p.Basin == 0))
        {
            context.DrawEllipse(Dot, null, new Point(X(point.Evaluations), Y(point.Loss)), 1.6, 1.6);
        }

        // Every basin as a step line per scale group; the finalists (or, while running, the best basin) on top.
        IReadOnlyList<SearchChartFinalist>? finalists = Finalists;
        var finalistOf = new Dictionary<int, int>();
        for (int f = 0; f < (finalists?.Count ?? 0); f++)
        {
            finalistOf.TryAdd(finalists![f].Basin, f);
        }

        var courses = trace.Where(static p => p.Basin != 0).GroupBy(static p => p.Basin).ToDictionary(static g => g.Key, static g => g.ToList());
        int leader = finalists is null ? Leader(courses) : -1;
        foreach ((int basin, List<SearchTracePoint> course) in courses.OrderBy(b => finalistOf.ContainsKey(b.Key) || b.Key == leader ? 1 : 0))
        {
            Pen pen;
            int finalist = finalistOf.GetValueOrDefault(basin, -1);
            if (finalist >= 0)
            {
                pen = new Pen(new SolidColorBrush(FinalistColors[finalist % FinalistColors.Length]), finalist == SelectedFinalist ? 2.6 : 1.5);
            }
            else if (basin == leader)
            {
                pen = new Pen(new SolidColorBrush(FinalistColors[0]), 2);
            }
            else
            {
                pen = OtherLine;
            }

            DrawCourse(context, course, pen, X, Y, finalist, finalist >= 0 ? plot.Right : double.NaN);
        }

        if (_hover >= 0 && finalists is not null && _hover < finalists.Count)
        {
            SearchChartFinalist shown = finalists[_hover];
            double best = courses.TryGetValue(shown.Basin, out List<SearchTracePoint>? points) ? points[^1].Loss : double.NaN;
            DrawText(context, string.Create(CultureInfo.InvariantCulture, $"{shown.Label} · loss {best:0.####} · {shown.Origin}"), new Point(plot.Left + 4, plot.Top + 2), 10, new SolidColorBrush(FinalistColors[_hover % FinalistColors.Length]), plot.Width - 8);
        }
    }

    /// <summary>The values to mark between two powers of ten: the decades when there are at least two, else 1, 2 and 5 of each.</summary>
    public static IReadOnlyList<double> Ticks(double logLow, double logHigh)
    {
        var decades = new List<double>();
        var all = new List<double>();
        for (int decade = (int)Math.Floor(logLow); decade <= Math.Ceiling(logHigh); decade++)
        {
            foreach (double step in new[] { 1.0, 2.0, 5.0 })
            {
                double value = step * Math.Pow(10, decade);
                double log = Math.Log10(value);
                if (log >= logLow && log <= logHigh)
                {
                    all.Add(value);
                    if (step == 1.0)
                    {
                        decades.Add(value);
                    }
                }
            }
        }

        return decades.Count >= 2 ? decades : all;
    }

    /// <summary>The basin with the lowest loss in the latest scale group any basin has reached: the best so far.</summary>
    private static int Leader(Dictionary<int, List<SearchTracePoint>> courses)
    {
        int latest = courses.Values.Select(static c => ScaleGroup(c[^1])).DefaultIfEmpty(-1).Max();
        return courses.Where(c => ScaleGroup(c.Value[^1]) == latest).OrderBy(static c => c.Value[^1].Loss).Select(static c => c.Key).DefaultIfEmpty(-1).First();
    }

    /// <summary>
    /// One basin: a step line within each scale group (flat until the loss improves, then down), a small dot where a group
    /// begins. A finalist's line runs on to <paramref name="extendTo"/>, the end of the search; others end where they
    /// last improved.
    /// </summary>
    private void DrawCourse(DrawingContext context, List<SearchTracePoint> course, Pen pen, Func<int, double> x, Func<double, double> y, int finalist, double extendTo)
    {
        for (int i = 0; i < course.Count; i++)
        {
            SearchTracePoint point = course[i];
            var at = new Point(x(point.Evaluations), y(point.Loss));
            bool first = i == 0 || ScaleGroup(course[i - 1]) != ScaleGroup(point);
            if (first)
            {
                context.DrawEllipse(pen.Brush, null, at, pen.Thickness + 0.6, pen.Thickness + 0.6);
            }
            else
            {
                SearchTracePoint previous = course[i - 1];
                var corner = new Point(at.X, y(previous.Loss));
                Line(context, pen, new Point(x(previous.Evaluations), y(previous.Loss)), corner, finalist);
                Line(context, pen, corner, at, finalist);
            }

            bool last = i == course.Count - 1 || ScaleGroup(course[i + 1]) != ScaleGroup(point);
            if (last && i == course.Count - 1 && !double.IsNaN(extendTo) && extendTo > at.X)
            {
                Line(context, pen, at, new Point(extendTo, at.Y), finalist);
            }
        }
    }

    private void Line(DrawingContext context, Pen pen, Point from, Point to, int finalist)
    {
        context.DrawLine(pen, from, to);
        if (finalist >= 0)
        {
            _hits.Add((finalist, from, to));
        }
    }

    /// <summary>The finalist whose line runs nearest to a point, within a few pixels; -1 for none.</summary>
    private int FinalistAt(Point at)
    {
        int best = -1;
        double nearest = 6;
        foreach ((int finalist, Point from, Point to) in _hits)
        {
            double distance = Distance(at, from, to);
            if (distance < nearest)
            {
                nearest = distance;
                best = finalist;
            }
        }

        return best;
    }

    private static double Distance(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double length = (ab.X * ab.X) + (ab.Y * ab.Y);
        double t = length == 0 ? 0 : Math.Clamp((((p.X - a.X) * ab.X) + ((p.Y - a.Y) * ab.Y)) / length, 0, 1);
        Point nearest = a + (ab * t);
        return Math.Sqrt(((p.X - nearest.X) * (p.X - nearest.X)) + ((p.Y - nearest.Y) * (p.Y - nearest.Y)));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int hover = FinalistAt(e.GetPosition(this));
        Cursor = hover >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int finalist = FinalistAt(e.GetPosition(this));
        if (finalist >= 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            FinalistClicked?.Invoke(this, finalist);
            e.Handled = true;
        }
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, double maxWidth, TextAlignment alignment = TextAlignment.Left)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        context.DrawText(formatted, at);
    }
}
