using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay;

/// <summary>
/// Writes a single sound, from the library browser, as a folder that <see cref="ExportedSounds"/> reads again:
/// <c>patch.json</c>, <c>preview.wav</c> of the reference note, the backend export and a <c>search-summary.json</c>
/// holding only the profile.
/// </summary>
public static class LibrarySoundWriter
{
    /// <summary>Writes the folder.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile it plays on.</param>
    /// <param name="directory">Output folder; created when missing.</param>
    /// <param name="headerComments">Comment lines at the top of the export.</param>
    /// <param name="gain">
    /// The gain HearSay plays the sound at, which the export carries; null for the gain of the library's reference note
    /// (<see cref="StructureLibrary.ReferenceRequest"/> at full velocity), the gain the Library plays a sound at that
    /// keeps none.
    /// </param>
    /// <returns>Paths of all files written.</returns>
    public static IReadOnlyList<string> Write(OperatorMatrixPatch patch, SynthesisProfile profile, string directory, IReadOnlyList<string> headerComments, float? gain = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(headerComments);
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        void WriteText(string path, string text)
        {
            File.WriteAllText(path, text);
            written.Add(path);
        }

        WriteText(Path.Combine(directory, "patch.json"), patch.ToJson());
        string preview = Path.Combine(directory, "preview.wav");

        // The reference request is at full velocity: the preview is the render the gain of the sound comes from
        // (Sound.ComputeGain), so its peak is reused.
        RenderedAudio reference = SynthesisBackends.RendererFor(profile).Render(patch, StructureLibrary.ReferenceRequest);
        reference.WriteWav(preview, normalizePeakTo: 0.7f);
        written.Add(preview);
        if (SynthesisBackends.ExporterFor(profile) is { } exporter)
        {
            var options = new PatchExportOptions { HeaderComments = headerComments, Gain = gain ?? PlaybackLevel.GainFor(reference.Peak) };
            WriteText(Path.Combine(directory, "patch" + exporter.FileExtension), exporter.Export(patch, options));
        }

        WriteText(Path.Combine(directory, "search-summary.json"), HearSayJson.Serialize(new { SearchSummary = new { Profile = profile.Name } }));
        return written;
    }
}
