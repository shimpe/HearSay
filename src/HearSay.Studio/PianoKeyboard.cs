using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HearSay.Studio;

/// <summary>A note played on the <see cref="PianoKeyboard"/>.</summary>
/// <param name="MidiNote">MIDI note number.</param>
/// <param name="Velocity">0..1, from where on the key it was hit: the bottom of a key is loud, the top soft.</param>
public sealed record KeyPress(int MidiNote, double Velocity);

/// <summary>
/// A piano keyboard drawn from <see cref="LowestNote"/> over <see cref="Octaves"/> octaves. Pressing a key raises
/// <see cref="NotePressed"/>; the pressed key is highlighted until the pointer is released.
/// </summary>
public sealed class PianoKeyboard : Control
{
    /// <summary>Lowest note of the keyboard, a C.</summary>
    public static readonly StyledProperty<int> LowestNoteProperty = AvaloniaProperty.Register<PianoKeyboard, int>(nameof(LowestNote), 48);

    /// <summary>Number of octaves.</summary>
    public static readonly StyledProperty<int> OctavesProperty = AvaloniaProperty.Register<PianoKeyboard, int>(nameof(Octaves), 3);

    /// <summary>Notes to mark, for example the pitches of the target notes.</summary>
    public static readonly StyledProperty<IReadOnlyList<int>?> MarkedNotesProperty = AvaloniaProperty.Register<PianoKeyboard, IReadOnlyList<int>?>(nameof(MarkedNotes));

    private static readonly int[] WhiteOffsets = [0, 2, 4, 5, 7, 9, 11];
    private static readonly int[] BlackOffsets = [1, 3, 6, 8, 10];
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private int _pressed = -1;

    static PianoKeyboard()
    {
        AffectsRender<PianoKeyboard>(LowestNoteProperty, OctavesProperty, MarkedNotesProperty);
    }

    /// <summary>Raised when a key is pressed.</summary>
    public event EventHandler<KeyPress>? NotePressed;

    /// <summary>Raised when the key is released.</summary>
    public event EventHandler? NoteReleased;

    /// <inheritdoc cref="LowestNoteProperty"/>
    public int LowestNote
    {
        get => GetValue(LowestNoteProperty);
        set => SetValue(LowestNoteProperty, value);
    }

    /// <inheritdoc cref="OctavesProperty"/>
    public int Octaves
    {
        get => GetValue(OctavesProperty);
        set => SetValue(OctavesProperty, value);
    }

    /// <inheritdoc cref="MarkedNotesProperty"/>
    public IReadOnlyList<int>? MarkedNotes
    {
        get => GetValue(MarkedNotesProperty);
        set => SetValue(MarkedNotesProperty, value);
    }

    /// <summary>Name of a MIDI note, for example C4.</summary>
    /// <param name="midi">MIDI note number.</param>
    public static string NoteName(int midi) => Names[((midi % 12) + 12) % 12] + ((midi / 12) - 1);

    private int WhiteKeyCount => (Octaves * 7) + 1;

    private double WhiteWidth => Bounds.Width / WhiteKeyCount;

    public override void Render(DrawingContext context)
    {
        double w = WhiteWidth;
        double h = Bounds.Height;
        var whiteFill = new SolidColorBrush(Color.Parse("#E2E8F0"));
        var whitePressed = new SolidColorBrush(Color.Parse("#22D3EE"));
        var blackFill = new SolidColorBrush(Color.Parse("#1E293B"));
        var blackPressed = new SolidColorBrush(Color.Parse("#0E7490"));
        var mark = new SolidColorBrush(Color.Parse("#F59E0B"));
        var outline = new Pen(new SolidColorBrush(Color.Parse("#0F172A")), 1);
        var label = new SolidColorBrush(Color.Parse("#475569"));
        var marked = new HashSet<int>(MarkedNotes ?? []);

        // White keys first, black keys on top.
        for (int i = 0; i < WhiteKeyCount; i++)
        {
            int midi = WhiteMidi(i);
            var rect = new Rect(i * w, 0, w, h);
            context.DrawRectangle(midi == _pressed ? whitePressed : whiteFill, outline, rect, 3, 3);
            if (marked.Contains(midi))
            {
                context.DrawEllipse(mark, null, new Point(rect.Center.X, h - 14), 4, 4);
            }

            if (midi % 12 == 0)
            {
                var text = new FormattedText(NoteName(midi), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, label);
                context.DrawText(text, new Point(rect.X + 4, h - 30));
            }
        }

        double bw = w * 0.6;
        double bh = h * 0.62;
        for (int i = 0; i < WhiteKeyCount - 1; i++)
        {
            int midi = WhiteMidi(i);
            int offset = (midi - LowestNote) % 12;
            if (Array.IndexOf(BlackOffsets, offset + 1) < 0)
            {
                continue;
            }

            int black = midi + 1;
            var rect = new Rect(((i + 1) * w) - (bw / 2), 0, bw, bh);
            context.DrawRectangle(black == _pressed ? blackPressed : blackFill, outline, rect, 2, 2);
            if (marked.Contains(black))
            {
                context.DrawEllipse(mark, null, new Point(rect.Center.X, bh - 10), 4, 4);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Point p = e.GetPosition(this);
        int midi = HitTest(p);
        if (midi < 0)
        {
            return;
        }

        _pressed = midi;
        InvalidateVisual();
        double velocity = Math.Clamp(0.3 + (0.7 * (p.Y / Math.Max(1.0, Bounds.Height))), 0.1, 1.0);
        NotePressed?.Invoke(this, new KeyPress(midi, Math.Round(velocity, 2)));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed >= 0)
        {
            _pressed = -1;
            InvalidateVisual();
            NoteReleased?.Invoke(this, EventArgs.Empty);
        }

        e.Pointer.Capture(null);
    }

    private int WhiteMidi(int index) => LowestNote + (12 * (index / 7)) + WhiteOffsets[index % 7];

    private int HitTest(Point p)
    {
        double w = WhiteWidth;
        if (p.X < 0 || p.X >= Bounds.Width || p.Y < 0 || p.Y > Bounds.Height)
        {
            return -1;
        }

        // Black keys sit on the upper part between two white keys.
        if (p.Y < Bounds.Height * 0.62)
        {
            double bw = w * 0.6;
            for (int i = 0; i < WhiteKeyCount - 1; i++)
            {
                int midi = WhiteMidi(i);
                if (Array.IndexOf(BlackOffsets, ((midi - LowestNote) % 12) + 1) < 0)
                {
                    continue;
                }

                double centre = (i + 1) * w;
                if (Math.Abs(p.X - centre) <= bw / 2)
                {
                    return midi + 1;
                }
            }
        }

        return WhiteMidi(Math.Min(WhiteKeyCount - 1, (int)(p.X / w)));
    }
}
