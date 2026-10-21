using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HearSay.Editing;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>A sideways drag of a number on the block diagram, from where it was pressed.</summary>
/// <param name="Id">The id of the <see cref="EditableValue"/> dragged, for example <c>op1.frequency.ratio</c>.</param>
/// <param name="Pixels">How far the pointer moved since the press; negative to the left.</param>
/// <param name="Fine">True while Shift is held, for ten times finer steps.</param>
/// <param name="Snap">True while Ctrl is held, to snap to the musical ratios.</param>
public sealed record DiagramNudge(string Id, double Pixels, bool Fine, bool Snap);

/// <summary>
/// The editing mode of the block diagram: a click on an operator box, a connection's amount, the NOISE box or the PITCH
/// box selects that part (outlined), a click on empty space clears the selection, and the numbers drawn for a frequency,
/// a connection amount or an output level, underlined with dashes, can be dragged sideways to change them. The diagram
/// only reports what the pointer does; the host makes the edits.
/// </summary>
public sealed partial class FmDiagram
{
    /// <summary>
    /// True to edit: every operator in use is drawn (greyed when it is not heard), the PITCH box always, the NOISE box
    /// always on a profile with shared noise, and parts can be selected and numbers dragged.
    /// </summary>
    public static readonly StyledProperty<bool> IsEditingProperty = AvaloniaProperty.Register<FmDiagram, bool>(nameof(IsEditing));

    /// <summary>The part selected, outlined in the diagram; null for none. A click changes it.</summary>
    public static readonly StyledProperty<PatchPart?> SelectedPartProperty =
        AvaloniaProperty.Register<FmDiagram, PatchPart?>(nameof(SelectedPart), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>
    /// True when the numbers can be dragged (false, for example, while the original of an edited sound is shown): then
    /// they are underlined, and a press on one selects its part without a drag.
    /// </summary>
    public static readonly StyledProperty<bool> CanNudgeProperty = AvaloniaProperty.Register<FmDiagram, bool>(nameof(CanNudge), true);

    /// <summary>
    /// The operators muted to listen without them (zero-based): drawn pale with their connections and output, their mute
    /// button lit. Every operator box has the button while editing; a click on it raises <see cref="MuteClicked"/>.
    /// </summary>
    public static readonly StyledProperty<IReadOnlySet<int>?> MutedOperatorsProperty = AvaloniaProperty.Register<FmDiagram, IReadOnlySet<int>?>(nameof(MutedOperators));

    /// <summary>
    /// The original of the patch shown when the patch is an edit of it; null for none. The parts the edit changed
    /// (<see cref="EditMarks"/>) are outlined in the edited colour, and so are the amounts and output levels it changed.
    /// </summary>
    public static readonly StyledProperty<OperatorMatrixPatch?> OriginalProperty = AvaloniaProperty.Register<FmDiagram, OperatorMatrixPatch?>(nameof(Original));

    /// <summary>Pixels the pointer moves before a press on a number becomes a drag.</summary>
    private const double DragThreshold = 3;

    /// <summary>How much of its colour a part keeps that is not heard.</summary>
    private const double UnheardOpacity = 0.4;

    private static readonly Pen SelectionPen = new(new SolidColorBrush(Color.Parse("#22D3EE")), 2);

    // What the pointer can pick in the last render: the parts, and the numbers that can be dragged with their part.
    private readonly List<(Rect Box, PatchPart Part)> _parts = [];
    private readonly List<(Rect Box, string Id, PatchPart Part)> _numbers = [];
    private readonly List<(Rect Box, int Op)> _mutes = [];

    private static readonly IBrush MuteFill = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush MuteInk = new SolidColorBrush(Color.Parse("#0B1220"));
    private static readonly IBrush MuteGlyph = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly Pen MuteOutline = new(new SolidColorBrush(Color.Parse("#475569")), 1);

    private bool _pressed;
    private Point _pressAt;
    private (Rect Box, string Id, PatchPart Part)? _pressedNumber;
    private bool _dragging;
    private IPointer? _pointer;
    private TopLevel? _escapeSource;

    /// <summary>
    /// A part was picked: clicked, or one of its numbers began to be dragged; null when empty space was clicked.
    /// <see cref="SelectedPart"/> is already set.
    /// </summary>
    public event EventHandler<PatchPart?>? PartClicked;

    /// <summary>The mute button of an operator was clicked: its index, zero-based. The selection does not change.</summary>
    public event EventHandler<int>? MuteClicked;

    /// <summary>A drag of a number began, with the id of its value; its part is selected.</summary>
    public event EventHandler<string>? NudgeStarted;

    /// <summary>The pointer of a drag moved.</summary>
    public event EventHandler<DiagramNudge>? Nudged;

    /// <summary>A drag of a number ended with the pointer let go.</summary>
    public event EventHandler<string>? NudgeEnded;

    /// <summary>A drag of a number was cancelled with Escape: the value goes back to where it was.</summary>
    public event EventHandler<string>? NudgeCancelled;

    /// <inheritdoc cref="IsEditingProperty"/>
    public bool IsEditing
    {
        get => GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    /// <inheritdoc cref="SelectedPartProperty"/>
    public PatchPart? SelectedPart
    {
        get => GetValue(SelectedPartProperty);
        set => SetValue(SelectedPartProperty, value);
    }

    /// <inheritdoc cref="MutedOperatorsProperty"/>
    public IReadOnlySet<int>? MutedOperators
    {
        get => GetValue(MutedOperatorsProperty);
        set => SetValue(MutedOperatorsProperty, value);
    }

    /// <inheritdoc cref="CanNudgeProperty"/>
    public bool CanNudge
    {
        get => GetValue(CanNudgeProperty);
        set => SetValue(CanNudgeProperty, value);
    }

    /// <inheritdoc cref="OriginalProperty"/>
    public OperatorMatrixPatch? Original
    {
        get => GetValue(OriginalProperty);
        set => SetValue(OriginalProperty, value);
    }

    /// <summary>
    /// Stops a drag without <see cref="NudgeEnded"/>, for a host whose gesture already ended underneath it (an undo, a
    /// redo, the Original ⇄ Edited switch).
    /// </summary>
    public void StopNudge() => EndPress(cancel: false, report: false);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditing || Patch is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        EndPress(cancel: false);
        _pressAt = e.GetPosition(this);
        if (_mutes.FindLastIndex(m => m.Box.Contains(_pressAt)) is var hit and >= 0)
        {
            MuteClicked?.Invoke(this, _mutes[hit].Op);
            e.Handled = true;
            return;
        }

        _pressedNumber = NumberAt(_pressAt);
        _pressed = true;
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
        {
            return;
        }

        bool clicked = !_dragging;
        PatchPart? part = _pressedNumber?.Part ?? PartAt(_pressAt);
        EndPress(cancel: false);
        e.Handled = true;
        if (clicked)
        {
            Pick(part);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPress(cancel: false);
    }

    /// <summary>Follows the pointer of a press on a number: past the threshold it is a drag. True while dragging.</summary>
    private bool MoveDrag(PointerEventArgs e)
    {
        if (!_pressed || _pressedNumber is not { } number || !CanNudge)
        {
            return false;
        }

        double pixels = e.GetPosition(this).X - _pressAt.X;
        if (!_dragging)
        {
            if (Math.Abs(pixels) < DragThreshold)
            {
                return false;
            }

            _dragging = true;
            HideTip();
            _escapeSource = TopLevel.GetTopLevel(this);
            _escapeSource?.AddHandler(KeyDownEvent, OnEscape, RoutingStrategies.Tunnel);
            Pick(number.Part);
            NudgeStarted?.Invoke(this, number.Id);
            if (!_dragging)
            {
                // The host stopped it.
                return true;
            }
        }

        Nudged?.Invoke(this, new DiagramNudge(number.Id, pixels, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Control)));
        e.Handled = true;
        return true;
    }

    private void OnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragging)
        {
            EndPress(cancel: true);
            e.Handled = true;
        }
    }

    /// <summary>Stops a press or a drag; a drag is reported as ended or cancelled, unless the host stopped it.</summary>
    private void EndPress(bool cancel, bool report = true)
    {
        bool dragging = _dragging;
        string? id = _pressedNumber?.Id;
        _pressed = false;
        _dragging = false;
        _pressedNumber = null;
        _escapeSource?.RemoveHandler(KeyDownEvent, OnEscape);
        _escapeSource = null;
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        if (dragging && report && id is not null)
        {
            (cancel ? NudgeCancelled : NudgeEnded)?.Invoke(this, id);
        }
    }

    private void Pick(PatchPart? part)
    {
        SelectedPart = part;
        PartClicked?.Invoke(this, part);
    }

    private (Rect Box, string Id, PatchPart Part)? NumberAt(Point at)
    {
        for (int k = _numbers.Count - 1; k >= 0; k--)
        {
            if (_numbers[k].Box.Contains(at))
            {
                return _numbers[k];
            }
        }

        return null;
    }

    /// <summary>The part under a point, the one drawn last first: amounts lie over boxes.</summary>
    private PatchPart? PartAt(Point at)
    {
        for (int k = _parts.Count - 1; k >= 0; k--)
        {
            if (_parts[k].Box.Contains(at))
            {
                return _parts[k].Part;
            }
        }

        return null;
    }

    private void ClearTargets()
    {
        _parts.Clear();
        _numbers.Clear();
        _mutes.Clear();
    }

    /// <summary>
    /// The mute button of an operator box, in its top-right corner: a small speaker, struck through and amber while the
    /// operator is muted. Only while editing.
    /// </summary>
    private void DrawMuteButton(DrawingContext context, Rect box, int op, bool muted)
    {
        if (!IsEditing)
        {
            return;
        }

        var button = new Rect(box.Right - 19, box.Y + 3, 16, 13);
        _mutes.Add((button, op));
        context.DrawRectangle(muted ? MuteFill : null, muted ? null : MuteOutline, button, 3, 3);
        IBrush ink = muted ? MuteInk : MuteGlyph;
        double x = button.X + 3, y = button.Y + 3.5;
        var speaker = new StreamGeometry();
        using (StreamGeometryContext g = speaker.Open())
        {
            g.BeginFigure(new Point(x, y + 1.5), true);
            g.LineTo(new Point(x + 2, y + 1.5));
            g.LineTo(new Point(x + 5, y - 1));
            g.LineTo(new Point(x + 5, y + 7));
            g.LineTo(new Point(x + 2, y + 4.5));
            g.LineTo(new Point(x, y + 4.5));
            g.EndFigure(true);
        }

        context.DrawGeometry(ink, null, speaker);
        var stroke = new Pen(ink, 1.2);
        if (muted)
        {
            context.DrawLine(stroke, new Point(x + 6.5, y + 0.5), new Point(x + 10, y + 5.5));
            context.DrawLine(stroke, new Point(x + 10, y + 0.5), new Point(x + 6.5, y + 5.5));
        }
        else
        {
            context.DrawLine(stroke, new Point(x + 7, y + 1), new Point(x + 7.8, y + 3));
            context.DrawLine(stroke, new Point(x + 7.8, y + 3), new Point(x + 7, y + 5));
        }
    }

    private void AddPart(Rect box, PatchPart part)
    {
        if (IsEditing)
        {
            _parts.Add((box, part));
        }
    }

    /// <summary>A number that can be dragged, drawn at <paramref name="text"/>: underlined while it can be.</summary>
    private void AddNumber(DrawingContext context, Rect text, string id, PatchPart part, IBrush brush)
    {
        if (!IsEditing)
        {
            return;
        }

        // A little larger than the digits, so that it is easy to grab.
        _numbers.Add((text.Inflate(new Thickness(3, 2)), id, part));
        if (CanNudge)
        {
            double y = text.Bottom - 0.5;
            context.DrawLine(new Pen(brush, 1, new DashStyle([2, 2], 0)), new Point(text.X, y), new Point(text.Right, y));
        }
    }

    /// <summary>The outline of the selected part, over everything else.</summary>
    private void DrawSelection(DrawingContext context)
    {
        if (!IsEditing || SelectedPart is not { } selected)
        {
            return;
        }

        foreach ((Rect box, PatchPart part) in _parts)
        {
            if (part == selected)
            {
                // Clear of the box's own outline, which shows whether the edit changed the part.
                context.DrawRectangle(null, SelectionPen, box.Inflate(3.5), 8, 8);
            }
        }
    }

    /// <summary>Draws what follows inside the returned scope paler, for a part that is not heard; nothing when it is.</summary>
    private static IDisposable? Unheard(DrawingContext context, bool unheard) => unheard ? context.PushOpacity(UnheardOpacity) : null;

    /// <summary>
    /// The second line of an operator box, its frequency and waveform; while editing, the frequency is a number to drag.
    /// </summary>
    private void DrawFrequencyLine(DrawingContext context, OperatorPatch op, int index, Point at, double maxWidth)
    {
        string line = Describe(op);
        if (!IsEditing)
        {
            DrawText(context, line, at, 10, Muted, maxWidth: maxWidth);
            return;
        }

        string frequency = DescribeFrequency(op);
        var formatted = new FormattedText(frequency, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Muted);
        double width = Math.Min(formatted.Width, maxWidth);
        DrawText(context, frequency, at, 10, Muted, maxWidth: maxWidth);
        string id = "op" + (index + 1).ToString(CultureInfo.InvariantCulture) + (op.Frequency.Mode == OperatorFrequencyMode.Fixed ? ".frequency.fixedHz" : ".frequency.ratio");
        AddNumber(context, new Rect(at.X, at.Y, width, formatted.Height), id, new PatchPart.OperatorPart(index), Muted);
        if (line.Length > frequency.Length && formatted.Width < maxWidth)
        {
            DrawText(context, line[frequency.Length..], new Point(at.X + formatted.Width, at.Y), 10, Muted, maxWidth: maxWidth - formatted.Width);
        }
    }
}
