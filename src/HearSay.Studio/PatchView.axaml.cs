using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>
/// A patch as SuperCollider code and as a block diagram, in two tabs, with the toolbar of its editing (undo, redo,
/// Original ⇄ Edited) and, beside the diagram, the inspector of the part selected; used by the Search and Library tabs.
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

    public PatchView()
    {
        InitializeComponent();
    }

    /// <inheritdoc cref="EditorProperty"/>
    public PatchEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => Editor?.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => Editor?.Redo();

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
