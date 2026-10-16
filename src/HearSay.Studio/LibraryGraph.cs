using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HearSay.Studio;

/// <summary>A node of the library graph, in the unit square.</summary>
/// <param name="X">Centre, 0..1 from the left.</param>
/// <param name="Y">Centre, 0..1 from the top.</param>
/// <param name="Radius">Radius in the same units.</param>
/// <param name="Brightness">0 dark to 1 very bright; sets the colour.</param>
/// <param name="Caption">Short text under the node.</param>
/// <param name="Tip">Text shown when the pointer rests on the node.</param>
/// <param name="IsImported">An imported sound: drawn as a star.</param>
/// <param name="CanOpen">A family that can be opened.</param>
/// <param name="IsMarked">On the way to a placed imported sound.</param>
public sealed record GraphNode(double X, double Y, double Radius, double Brightness, string Caption, string Tip, bool IsImported, bool CanOpen, bool IsMarked);

/// <summary>A line between two nodes.</summary>
/// <param name="From">Index of one node.</param>
/// <param name="To">Index of the other.</param>
public sealed record GraphEdge(int From, int To);

/// <summary>
/// Sound families drawn as a graph: nodes coloured by brightness and sized by family, lines to the nearest neighbours,
/// a ring around the selected node with a plus to open it, a star for an imported sound. A click raises
/// <see cref="NodeClicked"/>, a double click or a click on the plus raises <see cref="NodeOpened"/>.
/// </summary>
public sealed class LibraryGraph : Control
{
    /// <summary>Nodes to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<GraphNode>?> NodesProperty = AvaloniaProperty.Register<LibraryGraph, IReadOnlyList<GraphNode>?>(nameof(Nodes));

    /// <summary>Lines to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<GraphEdge>?> EdgesProperty = AvaloniaProperty.Register<LibraryGraph, IReadOnlyList<GraphEdge>?>(nameof(Edges));

    /// <summary>Index of the selected node, −1 for none.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty = AvaloniaProperty.Register<LibraryGraph, int>(nameof(SelectedIndex), -1);

    /// <summary>Text shown instead of the graph, for example while the library is built.</summary>
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<LibraryGraph, string?>(nameof(Message));

    private const double Pad = 24;
    private static readonly Color Dark = Color.Parse("#1E3A8A");
    private static readonly Color Bright = Color.Parse("#FDE68A");
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B1220"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#22D3EE"));
    private static readonly Pen EdgePen = new(new SolidColorBrush(Color.Parse("#334155")), 1.2);
    private static readonly Pen NodePen = new(new SolidColorBrush(Color.Parse("#0F172A")), 1);
    private static readonly Pen SelectedPen = new(Accent, 2.5);
    private static readonly Pen MarkedPen = new(Amber, 1.8, new DashStyle([3, 2], 0));

    private int _hover = -1;

    static LibraryGraph()
    {
        AffectsRender<LibraryGraph>(NodesProperty, EdgesProperty, SelectedIndexProperty, MessageProperty);
    }

    /// <summary>A node was clicked.</summary>
    public event EventHandler<int>? NodeClicked;

    /// <summary>A node was double-clicked, or its plus was clicked.</summary>
    public event EventHandler<int>? NodeOpened;

    /// <inheritdoc cref="NodesProperty"/>
    public IReadOnlyList<GraphNode>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    /// <inheritdoc cref="EdgesProperty"/>
    public IReadOnlyList<GraphEdge>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    /// <inheritdoc cref="SelectedIndexProperty"/>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <inheritdoc cref="MessageProperty"/>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        IReadOnlyList<GraphNode>? nodes = Nodes;
        if (!string.IsNullOrEmpty(Message) || nodes is null || nodes.Count == 0)
        {
            DrawText(context, Message ?? string.Empty, new Point(14, 14), 12, Muted);
            return;
        }

        foreach (GraphEdge edge in Edges ?? [])
        {
            if (edge.From < nodes.Count && edge.To < nodes.Count)
            {
                context.DrawLine(EdgePen, Centre(nodes[edge.From]), Centre(nodes[edge.To]));
            }
        }

        // Nodes first, then their captions, then the rings and the plus, so that no node covers them.
        foreach (GraphNode node in nodes)
        {
            Point c = Centre(node);
            double r = Radius(node);
            if (node.IsImported)
            {
                context.DrawGeometry(Amber, NodePen, Star(c, r + 3));
            }
            else
            {
                context.DrawEllipse(new SolidColorBrush(Lerp(Dark, Bright, node.Brightness)), NodePen, c, r, r);
            }
        }

        foreach (GraphNode node in nodes.Where(static n => n.Caption.Length > 0))
        {
            Point c = Centre(node);
            var text = new FormattedText(node.Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Muted);
            context.DrawText(text, new Point(c.X - (text.Width / 2), c.Y + Radius(node) + 3));
        }

        foreach (GraphNode node in nodes.Where(static n => n.IsMarked))
        {
            context.DrawEllipse(null, MarkedPen, Centre(node), Radius(node) + 4, Radius(node) + 4);
        }

        if (SelectedIndex >= 0 && SelectedIndex < nodes.Count)
        {
            GraphNode node = nodes[SelectedIndex];
            context.DrawEllipse(null, SelectedPen, Centre(node), Radius(node) + 5, Radius(node) + 5);
            if (node.CanOpen)
            {
                Point badge = Badge(node);
                context.DrawEllipse(Accent, null, badge, 7, 7);
                DrawText(context, "+", new Point(badge.X - 4, badge.Y - 9), 13, Background, FontWeight.Bold);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        IReadOnlyList<GraphNode>? nodes = Nodes;
        if (nodes is null || !string.IsNullOrEmpty(Message))
        {
            return;
        }

        Point p = e.GetPosition(this);
        if (SelectedIndex >= 0 && SelectedIndex < nodes.Count && nodes[SelectedIndex].CanOpen && Distance(p, Badge(nodes[SelectedIndex])) <= 8)
        {
            NodeOpened?.Invoke(this, SelectedIndex);
            return;
        }

        int hit = HitTest(p);
        if (hit < 0)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            NodeOpened?.Invoke(this, hit);
        }
        else
        {
            NodeClicked?.Invoke(this, hit);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int hit = HitTest(e.GetPosition(this));
        if (hit == _hover)
        {
            return;
        }

        _hover = hit;
        ToolTip.SetIsOpen(this, false);
        if (hit >= 0 && Nodes is { } nodes)
        {
            ToolTip.SetTip(this, nodes[hit].Tip);
            ToolTip.SetIsOpen(this, true);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        ToolTip.SetIsOpen(this, false);
    }

    private double Side => Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - (2 * Pad));

    private Point Centre(GraphNode node) => new(
        ((Bounds.Width - Side) / 2) + (node.X * Side),
        ((Bounds.Height - Side) / 2) + (node.Y * Side));

    private double Radius(GraphNode node) => Math.Max(4, node.Radius * Side);

    private Point Badge(GraphNode node)
    {
        Point c = Centre(node);
        double r = Radius(node) + 5;
        return new Point(c.X + (r * 0.75), c.Y - (r * 0.75));
    }

    private int HitTest(Point p)
    {
        IReadOnlyList<GraphNode>? nodes = Nodes;
        if (nodes is null)
        {
            return -1;
        }

        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < nodes.Count; i++)
        {
            double d = Distance(p, Centre(nodes[i]));
            if (d <= Math.Max(Radius(nodes[i]) + 3, 8) && d < bestDistance)
            {
                (best, bestDistance) = (i, d);
            }
        }

        return best;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + ((y - x) * t));
        return Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    private static StreamGeometry Star(Point c, double r)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        for (int k = 0; k < 10; k++)
        {
            double angle = (-Math.PI / 2) + (k * Math.PI / 5);
            double reach = k % 2 == 0 ? r : r * 0.45;
            var p = new Point(c.X + (reach * Math.Cos(angle)), c.Y + (reach * Math.Sin(angle)));
            if (k == 0)
            {
                g.BeginFigure(p, true);
            }
            else
            {
                g.LineTo(p);
            }
        }

        g.EndFigure(true);
        return geometry;
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, weight), size, brush);
        context.DrawText(formatted, at);
    }
}
