using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace HearSay.Studio;

/// <summary>
/// The HearSay mark: six operators on a ring, joined by the chords of a modulation matrix, with a wave running
/// through them. Drawn as vectors at any size; <see cref="RenderPng"/> rasterizes it for the window icon.
/// </summary>
public sealed class HearSayLogo : Control
{
    /// <summary>Whether the rounded dark tile is drawn behind the mark.</summary>
    public static readonly StyledProperty<bool> ShowTileProperty = AvaloniaProperty.Register<HearSayLogo, bool>(nameof(ShowTile), true);

    private static readonly Color Cyan = Color.Parse("#22D3EE");
    private static readonly Color Amber = Color.Parse("#F59E0B");
    private static readonly Color Ink = Color.Parse("#0F172A");
    private static readonly Color TileTop = Color.Parse("#1E3A5F");

    static HearSayLogo()
    {
        AffectsRender<HearSayLogo>(ShowTileProperty);
    }

    /// <inheritdoc cref="ShowTileProperty"/>
    public bool ShowTile
    {
        get => GetValue(ShowTileProperty);
        set => SetValue(ShowTileProperty, value);
    }

    /// <summary>Rasterizes the mark with its tile as a PNG.</summary>
    /// <param name="size">Width and height in pixels.</param>
    public static byte[] RenderPng(int size)
    {
        var logo = new HearSayLogo { Width = size, Height = size };
        logo.Measure(new Size(size, size));
        logo.Arrange(new Rect(0, 0, size, size));
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(logo);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double s = Math.Min(
            double.IsInfinity(availableSize.Width) ? 40 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 40 : availableSize.Height);
        return new Size(s, s);
    }

    public override void Render(DrawingContext context)
    {
        double s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0)
        {
            return;
        }

        double ox = (Bounds.Width - s) / 2, oy = (Bounds.Height - s) / 2;
        Point At(double x, double y) => new(ox + (x * s), oy + (y * s));

        if (ShowTile)
        {
            var tile = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = [new GradientStop(TileTop, 0), new GradientStop(Ink, 1)],
            };
            context.DrawRectangle(tile, null, new RoundedRect(new Rect(ox, oy, s, s), s * 0.22));
        }

        // Six operators on a ring, the first one at the top.
        var nodes = new Point[6];
        for (int k = 0; k < 6; k++)
        {
            double angle = (-Math.PI / 2) + (k * Math.PI / 3);
            nodes[k] = At(0.5 + (0.34 * Math.Cos(angle)), 0.5 + (0.34 * Math.Sin(angle)));
        }

        // The modulation matrix as two triangles: every operator reaches two others.
        var chord = new Pen(new SolidColorBrush(Amber, 0.85), s * 0.035, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (int[] triangle in new[] { new[] { 0, 2, 4 }, new[] { 1, 3, 5 } })
        {
            var figure = new PathFigure { StartPoint = nodes[triangle[0]], IsClosed = true };
            figure.Segments!.Add(new LineSegment { Point = nodes[triangle[1]] });
            figure.Segments.Add(new LineSegment { Point = nodes[triangle[2]] });
            var geometry = new PathGeometry();
            geometry.Figures!.Add(figure);
            context.DrawGeometry(null, chord, geometry);
        }

        // The sound: one and a half cycles of a wave across the ring.
        var wave = new PathFigure { StartPoint = At(0.24, 0.5), IsClosed = false };
        const int steps = 48;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double x = 0.24 + (0.52 * t);
            double y = 0.5 - (0.11 * Math.Sin(t * 3.0 * Math.PI));
            wave.Segments!.Add(new LineSegment { Point = At(x, y) });
        }

        var waveGeometry = new PathGeometry();
        waveGeometry.Figures!.Add(wave);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Ink, 0.9), s * 0.095, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), waveGeometry);
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Cyan), s * 0.05, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), waveGeometry);

        // The operators on top of everything.
        var fill = new SolidColorBrush(Cyan);
        var outline = new Pen(new SolidColorBrush(Ink), s * 0.025);
        foreach (Point node in nodes)
        {
            context.DrawEllipse(fill, outline, node, s * 0.065, s * 0.065);
        }
    }
}
