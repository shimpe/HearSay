using System.Globalization;
using HearSay.Analysis;
using HearSay.Envelope;
using HearSay.OperatorMatrix.Search;
using HearSay.Partials;
using HearSay.Pitch;
using HearSay.Playing;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay;

/// <summary>Compact, serializable view of a target analysis: the measurements without the audio and the per-frame arrays.</summary>
public sealed record TargetAnalysisReport
{
    /// <summary>Fundamental in hertz.</summary>
    public required double FundamentalHz { get; init; }

    /// <summary>Confidence of the fundamental, 0..1.</summary>
    public required double FundamentalConfidence { get; init; }

    /// <summary>Length of the trimmed note in seconds.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Estimated note-off in seconds.</summary>
    public required double NoteOffSeconds { get; init; }

    /// <summary>Length of the release tail in seconds.</summary>
    public required double ReleaseTailSeconds { get; init; }

    /// <summary>Amplitude envelope stages.</summary>
    public required EnvelopeProfile Envelope { get; init; }

    /// <summary>Amplitude decay stages.</summary>
    public required DecayModel AmplitudeDecay { get; init; }

    /// <summary>Partial cloud.</summary>
    public required IReadOnlyList<SpectralPartial> Partials { get; init; }

    /// <summary>Inharmonicity, 0..1.</summary>
    public required double Inharmonicity { get; init; }

    /// <summary>Tracked partials without their individual points.</summary>
    public required IReadOnlyList<PartialTrackReport> PartialTracks { get; init; }

    /// <summary>Band energy summaries.</summary>
    public required IReadOnlyList<BandReport> Bands { get; init; }

    /// <summary>Spectral centroid at the start, in the middle and at the end of the held note, in hertz.</summary>
    public required IReadOnlyList<double> CentroidHz { get; init; }

    /// <summary>Long-term average spectrum: band centres in hertz.</summary>
    public required IReadOnlyList<double> LtasCenterHz { get; init; }

    /// <summary>Long-term average spectrum: band levels in dB relative to the total.</summary>
    public required IReadOnlyList<double> LtasLevelsDb { get; init; }

    /// <summary>Noise between the partials: its share of the energy and its colour.</summary>
    public required HearSay.Spectral.NoiseProfile Noise { get; init; }

    /// <summary>Pitch features.</summary>
    public required PitchReport Pitch { get; init; }

    /// <summary>Amplitude beating.</summary>
    public required BeatAnalysis Beating { get; init; }

    /// <summary>Issues found while preparing the audio.</summary>
    public required IReadOnlyList<string> Issues { get; init; }

    /// <summary>Builds the report.</summary>
    /// <param name="analysis">Full analysis.</param>
    public static TargetAnalysisReport From(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        double held = analysis.NoteOffSeconds;
        return new TargetAnalysisReport
        {
            FundamentalHz = analysis.FundamentalHz,
            FundamentalConfidence = analysis.FundamentalConfidence,
            DurationSeconds = analysis.DurationSeconds,
            NoteOffSeconds = analysis.NoteOffSeconds,
            ReleaseTailSeconds = analysis.ReleaseTailSeconds,
            Envelope = analysis.Envelope,
            AmplitudeDecay = analysis.AmplitudeDecay,
            Partials = analysis.Partials,
            Inharmonicity = analysis.Inharmonicity,
            PartialTracks = [.. analysis.PartialTracks.Select(static t => new PartialTrackReport(t.FrequencyHz, t.RelativeFrequency, t.PeakLevelDb, t.InitialLevelDb, t.BirthSeconds, t.DeathSeconds, t.Decay, t.FrequencyDriftCents, t.Confidence))],
            Bands = [.. analysis.Trajectories.Bands.Select(static b => new BandReport(b.Band.Name, b.InitialDb, b.PeakDb, b.PeakSeconds, b.AttackRateDbPerSecond, b.Decay, b.LateDb))],
            CentroidHz =
            [
                analysis.Trajectories.Mean(analysis.Trajectories.CentroidHz, 0.0, held * 0.1),
                analysis.Trajectories.Mean(analysis.Trajectories.CentroidHz, held * 0.45, held * 0.55),
                analysis.Trajectories.Mean(analysis.Trajectories.CentroidHz, held * 0.9, held),
            ],
            LtasCenterHz = analysis.Ltas.CenterHz,
            LtasLevelsDb = analysis.Ltas.LevelsDb,
            Noise = analysis.Noise,
            Pitch = new PitchReport(
                analysis.Pitch.ReferenceHz,
                analysis.Pitch.AttackOffsetCents,
                analysis.Pitch.PitchDecaySeconds,
                analysis.Pitch.VibratoRateHz,
                analysis.Pitch.VibratoDepthCents,
                analysis.Pitch.VibratoConfidence,
                analysis.Pitch.DriftCentsPerSecond,
                analysis.Pitch.OverallConfidence,
                analysis.Pitch.HasMovement),
            Beating = analysis.Beating,
            Issues = analysis.Issues,
        };
    }
}

/// <summary>Summary of one tracked partial.</summary>
/// <param name="FrequencyHz">Mean frequency.</param>
/// <param name="RelativeFrequency">Frequency relative to the fundamental.</param>
/// <param name="PeakLevelDb">Highest level.</param>
/// <param name="InitialLevelDb">Level at birth.</param>
/// <param name="BirthSeconds">First observation.</param>
/// <param name="DeathSeconds">Last observation.</param>
/// <param name="Decay">Decay after the peak.</param>
/// <param name="FrequencyDriftCents">Frequency drift over the life of the track.</param>
/// <param name="Confidence">0..1.</param>
public sealed record PartialTrackReport(
    double FrequencyHz,
    double RelativeFrequency,
    double PeakLevelDb,
    double InitialLevelDb,
    double BirthSeconds,
    double DeathSeconds,
    DecayModel Decay,
    double FrequencyDriftCents,
    double Confidence);

/// <summary>Summary of one band-energy trajectory.</summary>
/// <param name="Name">Band name.</param>
/// <param name="InitialDb">Level over the first 30 ms.</param>
/// <param name="PeakDb">Highest level.</param>
/// <param name="PeakSeconds">Time of the highest level.</param>
/// <param name="AttackRateDbPerSecond">Mean rise to the peak.</param>
/// <param name="Decay">Decay after the peak.</param>
/// <param name="LateDb">Level over the last third.</param>
public sealed record BandReport(string Name, double InitialDb, double PeakDb, double PeakSeconds, double AttackRateDbPerSecond, DecayModel Decay, double LateDb);

/// <summary>Pitch features of a target.</summary>
/// <param name="ReferenceHz">Median fundamental of the steady part.</param>
/// <param name="AttackOffsetCents">Pitch at the start relative to the reference.</param>
/// <param name="PitchDecaySeconds">Time until the pitch settles.</param>
/// <param name="VibratoRateHz">Vibrato rate.</param>
/// <param name="VibratoDepthCents">Vibrato depth.</param>
/// <param name="VibratoConfidence">0..1.</param>
/// <param name="DriftCentsPerSecond">Slow drift.</param>
/// <param name="OverallConfidence">Mean periodicity, 0..1.</param>
/// <param name="HasMovement">Whether the pitch moves enough to matter.</param>
public sealed record PitchReport(
    double ReferenceHz,
    double AttackOffsetCents,
    double PitchDecaySeconds,
    double VibratoRateHz,
    double VibratoDepthCents,
    double VibratoConfidence,
    double DriftCentsPerSecond,
    double OverallConfidence,
    bool HasMovement);

/// <summary>Writes a match result as a folder of files.</summary>
public static class PatchMatchResultWriter
{
    /// <summary>
    /// Writes <c>patch.json</c>, <c>preview.wav</c>, the backend export (for example <c>patch.scd</c>),
    /// <c>analysis.json</c>, <c>comparison.json</c>, <c>search-summary.json</c> and the alternatives. A target set also
    /// gets a preview, analysis and comparison per note.
    /// </summary>
    /// <param name="result">Match result.</param>
    /// <param name="directory">Output folder; created when missing.</param>
    /// <param name="renderer">Renderer for the previews of the alternatives and the gains of the exports; the profile default when null.</param>
    /// <param name="exporter">Backend exporter; the profile default when null. Profiles without an exporter get no export file.</param>
    /// <returns>Paths of all files written.</returns>
    public static IReadOnlyList<string> Write(PatchMatchResult result, string directory, IOperatorMatrixRenderer? renderer = null, IPatchExporter? exporter = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        exporter ??= SynthesisBackends.ExporterFor(result.Profile);
        var written = new List<string>();

        void WriteText(string path, string text)
        {
            File.WriteAllText(path, text);
            written.Add(path);
        }

        string[] header =
        [
            FormattableString.Invariant($"Matched to a target at {result.Analysis.FundamentalHz:F2} Hz; audio loss {result.AlternativePatches[0].AudioLoss:F4}, HearSay similarity {result.Comparison.Scores.Similarity:F3}."),
            FormattableString.Invariant($"Profile {result.Profile.Name}, seed {result.SearchSummary.RandomSeed}, {result.SearchSummary.EvaluationsUsed} renders."),
        ];

        // The gain HearSay plays a finalist at: that of the result's reference note at full velocity (Sound.ComputeGain),
        // from the preview when the preview is that render, else rendered once.
        RenderRequest gainReference = result.Request with { Velocity = 1.0 };
        IOperatorMatrixRenderer Renderer() => renderer ??= SynthesisBackends.RendererFor(result.Profile);
        float GainOf(OperatorMatrixPatch patch, RenderedAudio preview) =>
            PlaybackLevel.GainFor((gainReference == result.Request ? preview : Renderer().Render(patch, gainReference)).Peak);

        WriteText(Path.Combine(directory, "patch.json"), result.BestPatch.ToJson());
        string preview = Path.Combine(directory, "preview.wav");
        result.PreviewAudio.WriteWav(preview, normalizePeakTo: 0.7f);
        written.Add(preview);
        if (exporter is not null)
        {
            var options = new PatchExportOptions { HeaderComments = header, Gain = GainOf(result.BestPatch, result.PreviewAudio) };
            WriteText(Path.Combine(directory, "patch" + exporter.FileExtension), exporter.Export(result.BestPatch, options));
        }

        WriteText(Path.Combine(directory, "analysis.json"), HearSayJson.Serialize(TargetAnalysisReport.From(result.Analysis)));
        WriteText(Path.Combine(directory, "comparison.json"), HearSayJson.Serialize(result.Comparison));

        // Target sets: one preview, analysis and comparison per note besides the reference files above.
        if (result.Notes.Count > 1)
        {
            for (int i = 0; i < result.Notes.Count; i++)
            {
                NoteMatch note = result.Notes[i];
                string suffix = FormattableString.Invariant($"-note{i + 1}-{note.Request.FrequencyHz:0}Hz");
                string wav = Path.Combine(directory, "preview" + suffix + ".wav");
                note.Preview.WriteWav(wav, normalizePeakTo: 0.7f);
                written.Add(wav);
                WriteText(Path.Combine(directory, "analysis" + suffix + ".json"), HearSayJson.Serialize(TargetAnalysisReport.From(note.Analysis)));
                WriteText(Path.Combine(directory, "comparison" + suffix + ".json"), HearSayJson.Serialize(note.Comparison));
            }
        }
        WriteText(Path.Combine(directory, "search-summary.json"), HearSayJson.Serialize(new { result.SearchSummary, Candidates = result.AlternativePatches.Select(static c => new { c.Loss, c.AudioLoss, c.Complexity, c.Terms, Regime = c.Regime.Value, c.Origin }) }));

        if (result.AlternativePatches.Count > 1)
        {
            string alternatives = Path.Combine(directory, "alternatives");
            Directory.CreateDirectory(alternatives);
            for (int i = 1; i < result.AlternativePatches.Count; i++)
            {
                PatchCandidateResult candidate = result.AlternativePatches[i];
                string prefix = Path.Combine(alternatives, i.ToString("00", CultureInfo.InvariantCulture));
                WriteText(prefix + "-patch.json", candidate.Patch.ToJson());
                string wav = prefix + "-preview.wav";
                RenderedAudio audio = Renderer().Render(candidate.Patch, result.Request);
                audio.WriteWav(wav, normalizePeakTo: 0.7f);
                written.Add(wav);
                if (exporter is not null)
                {
                    WriteText(prefix + "-patch" + exporter.FileExtension, exporter.Export(candidate.Patch, new PatchExportOptions
                    {
                        InstrumentName = "HearSayAlternative" + i.ToString(CultureInfo.InvariantCulture),
                        HeaderComments = [FormattableString.Invariant($"Alternative {i}: audio loss {candidate.AudioLoss:F4}. {candidate.Origin}")],
                        Gain = GainOf(candidate.Patch, audio),
                    }));
                }
            }
        }

        return written;
    }
}
