using Avalonia;
using Avalonia.Controls;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>A patch as SuperCollider code and as a block diagram, in two tabs; used by the Search and Library tabs.</summary>
public sealed partial class PatchView : UserControl
{
    /// <summary>The generated code.</summary>
    public static readonly StyledProperty<string> CodeProperty = AvaloniaProperty.Register<PatchView, string>(nameof(Code), string.Empty);

    /// <summary>Shown while there is no code.</summary>
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<PatchView, string?>(nameof(Watermark));

    /// <summary>The patch drawn as a block diagram.</summary>
    public static readonly StyledProperty<OperatorMatrixPatch?> PatchProperty = AvaloniaProperty.Register<PatchView, OperatorMatrixPatch?>(nameof(Patch));

    /// <summary>Header of the code tab.</summary>
    public static readonly StyledProperty<string> CodeHeaderProperty = AvaloniaProperty.Register<PatchView, string>(nameof(CodeHeader), "SuperCollider · patch.scd");

    public PatchView()
    {
        InitializeComponent();
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

    /// <inheritdoc cref="CodeHeaderProperty"/>
    public string CodeHeader
    {
        get => GetValue(CodeHeaderProperty);
        set => SetValue(CodeHeaderProperty, value);
    }
}
