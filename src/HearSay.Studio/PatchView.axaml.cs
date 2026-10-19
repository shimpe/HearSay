using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HearSay.Editing;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>
/// A patch as SuperCollider code and as a block diagram, in two tabs, with the toolbar of its editing (undo, redo, Match
/// loudness, Original ⇄ Edited) and, beside the diagram, the inspector of the part selected; a click on the diagram
/// selects a part and a sideways drag of one of its numbers changes it. Used by the Search and Library tabs.
/// </summary>
public sealed partial class PatchView : UserControl
{
    /// <summary>The generated code.</summary>
    public static readonly StyledProperty<string> CodeProperty = AvaloniaProperty.Register<PatchView, string>(nameof(Code), string.Empty);

    /// <summary>Shown while there is no code.</summary>
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<PatchView, string?>(nameof(Watermark));

    /// <summary>The patch drawn as a block diagram.</summary>
    public static readonly StyledProperty<OperatorMatrixPatch?> PatchProperty = AvaloniaProperty.Register<PatchView, OperatorMatrixPatch?>(nameof(Patch));

    /// <summary>Whether the profile of the patch has a shared noise source; see <see cref="FmDiagram.SharedNoise"/>.</summary>
    public static readonly StyledProperty<bool> SharedNoiseProperty = AvaloniaProperty.Register<PatchView, bool>(nameof(SharedNoise));

    /// <summary>Header of the code tab.</summary>
    public static readonly StyledProperty<string> CodeHeaderProperty = AvaloniaProperty.Register<PatchView, string>(nameof(CodeHeader), "SuperCollider · patch.scd");

    /// <summary>The editing of the shown sound: undo, redo and the Original ⇄ Edited switch of the toolbar.</summary>
    public static readonly StyledProperty<PatchEditorViewModel?> EditorProperty = AvaloniaProperty.Register<PatchView, PatchEditorViewModel?>(nameof(Editor));

    /// <summary>The value a drag on the diagram changes, from the sound it began on, and where it began.</summary>
    private EditableValue? _nudged;
    private PatchEditor? _nudgedSound;
    private double _nudgeFrom;

    public PatchView()
    {
        InitializeComponent();
        Diagram.PartClicked += OnPartClicked;
        Diagram.NudgeStarted += OnNudgeStarted;
        Diagram.Nudged += OnNudged;
        Diagram.NudgeEnded += (_, _) => EndNudge(cancel: false);
        Diagram.NudgeCancelled += (_, _) => EndNudge(cancel: true);
        LayoutUpdated += (_, _) => PlaceToolbar();
    }

    /// <summary>Space kept between the last tab header and the toolbar beside it.</summary>
    private const double ToolbarGap = 8;

    /// <summary>
    /// Puts the editing toolbar beside the tab headers, on the right of the tab strip, where it fits there, and else in a
    /// row of its own above them, so that it never covers a header. Its width does not depend on the row, so the choice is
    /// stable from one layout pass to the next.
    /// </summary>
    private void PlaceToolbar()
    {
        if (!EditToolbar.IsVisible || Tabs.Items.OfType<TabItem>().LastOrDefault() is not { } last || last.Bounds.Width <= 0
            || last.TranslatePoint(new Point(last.Bounds.Width, 0), this) is not { } headersEnd)
        {
            return;
        }

        bool beside = headersEnd.X + ToolbarGap + EditToolbar.DesiredSize.Width <= Bounds.Width;
        int row = beside ? 1 : 0;
        if (Grid.GetRow(EditToolbar) != row)
        {
            Grid.SetRow(EditToolbar, row);
            EditToolbar.Margin = beside ? new Thickness(0, 4, 0, 0) : new Thickness(0, 0, 0, 4);
        }
    }

    /// <inheritdoc cref="EditorProperty"/>
    public PatchEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => Editor?.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => Editor?.Redo();

    private async void OnMatchLoudness(object? sender, RoutedEventArgs e) => await (Editor?.MatchLoudnessAsync() ?? Task.CompletedTask);

    private void OnPartClicked(object? sender, PatchPart? part)
    {
        if (Editor is { HasSound: true } edits)
        {
            edits.Selected = part;
        }
    }

    /// <summary>A drag of a number on the diagram begins one gesture, on the value of the part it selected.</summary>
    private void OnNudgeStarted(object? sender, string id)
    {
        if (Editor is not { Editor: { } editor, DisplayedPatch: { } patch, ShowingOriginal: false } edits
            || Diagram.SelectedPart is not { } part
            || EditableValues.For(patch, editor.Profile.Capabilities, part).FirstOrDefault(v => v.Id == id) is not { } value)
        {
            Diagram.StopNudge();
            return;
        }

        _nudged = value;
        _nudgedSound = editor;
        _nudgeFrom = value.Get(patch);
        edits.BeginGesture();
    }

    /// <summary>Shows the value the drag reaches; a drag whose gesture ended underneath (an undo, the switch) stops.</summary>
    private void OnNudged(object? sender, DiagramNudge nudge)
    {
        if (_nudged is not { } value || value.Id != nudge.Id)
        {
            return;
        }

        if (Editor is not { InGesture: true } edits || !ReferenceEquals(edits.Editor, _nudgedSound))
        {
            _nudged = null;
            _nudgedSound = null;
            Diagram.StopNudge();
            return;
        }

        edits.Preview(value.Set(value.Nudge(_nudgeFrom, nudge.Pixels, nudge.Fine, nudge.Snap)));
    }

    /// <summary>The drag becomes one step, or goes back to where it began; nothing when its gesture already ended.</summary>
    private void EndNudge(bool cancel)
    {
        PatchEditor? sound = _nudgedSound;
        _nudged = null;
        _nudgedSound = null;
        if (sound is null || Editor is not { InGesture: true } edits || !ReferenceEquals(edits.Editor, sound))
        {
            return;
        }

        if (cancel)
        {
            edits.CancelGesture();
        }
        else
        {
            edits.EndGesture();
        }
    }

    /// <inheritdoc cref="CodeProperty"/>
    public string Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <inheritdoc cref="WatermarkProperty"/>
    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <inheritdoc cref="PatchProperty"/>
    public OperatorMatrixPatch? Patch
    {
        get => GetValue(PatchProperty);
        set => SetValue(PatchProperty, value);
    }

    /// <inheritdoc cref="SharedNoiseProperty"/>
    public bool SharedNoise
    {
        get => GetValue(SharedNoiseProperty);
        set => SetValue(SharedNoiseProperty, value);
    }

    /// <inheritdoc cref="CodeHeaderProperty"/>
    public string CodeHeader
    {
        get => GetValue(CodeHeaderProperty);
        set => SetValue(CodeHeaderProperty, value);
    }
}
