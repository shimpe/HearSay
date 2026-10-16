using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

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
    /// <returns>Paths of all files written.</returns>
    public static IReadOnlyList<string> Write(OperatorMatrixPatch patch, SynthesisProfile profile, string directory, IReadOnlyList<string> headerComments)
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
        SynthesisBackends.RendererFor(profile).Render(patch, StructureLibrary.ReferenceRequest).WriteWav(preview, normalizePeakTo: 0.7f);
        written.Add(preview);
        if (SynthesisBackends.ExporterFor(profile) is { } exporter)
        {
            WriteText(Path.Combine(directory, "patch" + exporter.FileExtension), exporter.Export(patch, new PatchExportOptions { HeaderComments = headerComments }));
        }

        WriteText(Path.Combine(directory, "search-summary.json"), HearSayJson.Serialize(new { SearchSummary = new { Profile = profile.Name } }));
        return written;
    }
}
