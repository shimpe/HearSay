using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Export;

/// <summary>Options shared by patch exporters.</summary>
public sealed record PatchExportOptions
{
    /// <summary>Name of the generated instrument.</summary>
    public string InstrumentName { get; init; } = "HearSayPatch";

    /// <summary>Append a short example that plays the instrument.</summary>
    public bool IncludeUsageExample { get; init; } = true;

    /// <summary>Free-text lines written as a comment at the top of the export, for example where the patch came from.</summary>
    public IReadOnlyList<string> HeaderComments { get; init; } = [];

    /// <summary>
    /// The gain HearSay plays the sound at (<c>PlaybackLevel.GainFor</c> the peak of its reference note), multiplied into
    /// the output so that the export sounds as loud as in HearSay, with the backend's own volume control on top; null
    /// for none, and the export is exactly as without it.
    /// </summary>
    public float? Gain { get; init; }
}

/// <summary>
/// Translates a canonical patch into the native format of one backend. An exporter never approximates: a patch the
/// backend cannot represent exactly is rejected.
/// </summary>
public interface IPatchExporter
{
    /// <summary>Capabilities of the backend this exporter writes for.</summary>
    SynthesisCapabilities Capabilities { get; }

    /// <summary>File extension of the export, including the dot.</summary>
    string FileExtension { get; }

    /// <summary>Exports a patch.</summary>
    /// <param name="patch">Patch to export.</param>
    /// <param name="options">Export options.</param>
    /// <exception cref="Rendering.PatchNotSupportedException">The backend cannot represent the patch.</exception>
    string Export(OperatorMatrixPatch patch, PatchExportOptions? options = null);
}
