using System.Globalization;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Parameters;

/// <summary>
/// All searchable parameters of a patch under a synthesis profile, as a flat list and as a hierarchy. Structural
/// parameters offer only what the profile allows, so a search that goes through this schema cannot leave the space
/// the backend can represent. Immutable and thread-safe.
/// </summary>
public sealed class PatchParameterSchema
{
    /// <summary>Coarse frequency ratios a structural search may choose from, besides ratios inferred from a target.</summary>
    public static IReadOnlyList<double> DefaultRatios { get; } =
        [0.5, 0.75, 1, 1.25, 1.5, 2, 2.5, 3, 3.5, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16];

    // Search range of a formant peak's own Q, as a multiple of the filter's Q.
    private const double MinRelativeQ = 0.125;
    private const double MaxRelativeQ = 8.0;

    private readonly Dictionary<string, PatchParameter> _byId;

    private PatchParameterSchema(List<PatchParameter> parameters)
    {
        Parameters = parameters;
        _byId = parameters.ToDictionary(static p => p.Id, StringComparer.Ordinal);
        Root = BuildGroup("Patch", "Patch", parameters);
    }

    /// <summary>All parameters, structural and continuous.</summary>
    public IReadOnlyList<PatchParameter> Parameters { get; }

    /// <summary>Root of the hierarchy: Patch, then operators, modulation matrix and pitch model.</summary>
    public ParameterGroup Root { get; }

    /// <summary>Finds a parameter by id; null when unknown.</summary>
    /// <param name="id">Parameter id.</param>
    public PatchParameter? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Smooth parameters that can be heard in a patch: the candidates for continuous optimization.</summary>
    /// <param name="patch">Patch whose structure decides what is active.</param>
    public IReadOnlyList<PatchParameter> ActiveSmooth(OperatorMatrixPatch patch) =>
        [.. Parameters.Where(p => p.IsSmooth && p.IsActive(patch))];

    /// <summary>Builds the schema for the structure of a patch.</summary>
    /// <param name="patch">
    /// Patch that supplies the shape: the number of operators and the number of points of every envelope. Values are
    /// not captured; every parameter reads and writes whatever patch it is given.
    /// </param>
    /// <param name="capabilities">Profile limits for structural choices.</param>
    /// <param name="ratios">Coarse ratio choices; <see cref="DefaultRatios"/> when null.</param>
    /// <param name="timeLimits">Longest useful envelope times; unlimited when null.</param>
    public static PatchParameterSchema Build(OperatorMatrixPatch patch, SynthesisCapabilities capabilities, IReadOnlyList<double>? ratios = null, EnvelopeTimeLimits? timeLimits = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(capabilities);
        ratios ??= DefaultRatios;
        EnvelopeTimeLimits limits = timeLimits ?? EnvelopeTimeLimits.Unlimited;
        var list = new List<PatchParameter>();
        int n = patch.OperatorCount;
        double[] modes = [.. capabilities.SupportedModes.Order().Select(static m => (double)(int)m)];
        double[] waveforms = [.. capabilities.SupportedWaveforms.Order().Select(static w => (double)(int)w)];
        var frequencyModes = new List<double>();
        if (capabilities.SupportsRatioFrequency)
        {
            frequencyModes.Add((int)OperatorFrequencyMode.Ratio);
        }

        if (capabilities.SupportsFixedFrequency)
        {
            frequencyModes.Add((int)OperatorFrequencyMode.Fixed);
        }

        for (int i = 0; i < n; i++)
        {
            int op = i;
            string id = "op" + Number(op);
            string group = "Patch/Operator " + Number(op);
            bool Enabled(OperatorMatrixPatch p) => p.Operators[op].Enabled;
            bool Audible(OperatorMatrixPatch p) => p.Operators[op].Enabled && p.FindAudibleOperators()[op];

            list.Add(Structural(id + ".enabled", group, [0, 1], p => p.Operators[op].Enabled ? 1 : 0, (p, v) => p.WithOperator(op, p.Operators[op] with { Enabled = v >= 0.5 }), static _ => true));
            list.Add(Structural(id + ".mode", group, modes, p => (int)p.Operators[op].Mode, (p, v) => p.WithOperator(op, p.Operators[op] with { Mode = (OperatorMode)(int)v }), Enabled));
            list.Add(Structural(id + ".waveform", group, waveforms, p => (int)p.Operators[op].Waveform, (p, v) => p.WithOperator(op, p.Operators[op].WithWaveform((OperatorWaveform)(int)v)), Enabled));
            if (capabilities.SupportedWaveforms.Contains(OperatorWaveform.Pulse))
            {
                // The duty cycle only shapes a pulse; the other waveforms ignore it.
                list.Add(Smooth(id + ".shape", group, 0.01, 0.99, ParameterTransforms.Linear, p => p.Operators[op].Shape, (p, v) => p.WithOperator(op, p.Operators[op] with { Shape = v }), p => Audible(p) && p.Operators[op].Waveform == OperatorWaveform.Pulse));
            }

            if (capabilities.SupportedWaveforms.Contains(OperatorWaveform.FilteredNoise))
            {
                // The response of filtered noise, from low-pass through band-pass to high-pass; its cutoff is the frequency.
                list.Add(Smooth(id + ".filter", group, 0.0, 1.0, ParameterTransforms.Linear, p => p.Operators[op].Shape, (p, v) => p.WithOperator(op, p.Operators[op] with { Shape = v }), p => Audible(p) && p.Operators[op].Waveform == OperatorWaveform.FilteredNoise));
            }

            if (capabilities.SupportedWaveforms.Contains(OperatorWaveform.Elliptic))
            {
                // The elliptic parameter m, searched along its nome, and the morph from sn to the peak-normalized sd.
                list.Add(Smooth(id + ".elliptic", group, 0.0, OperatorPatch.MaxEllipticM, ParameterTransforms.EllipticNome, p => p.Operators[op].Shape, (p, v) => p.WithOperator(op, p.Operators[op] with { Shape = Math.Clamp(v, 0.0, OperatorPatch.MaxEllipticM) }), p => Audible(p) && p.Operators[op].Waveform == OperatorWaveform.Elliptic));
                list.Add(Smooth(id + ".morph", group, 0.0, 1.0, ParameterTransforms.Linear, p => p.Operators[op].Morph, (p, v) => p.WithOperator(op, p.Operators[op] with { Morph = Math.Clamp(v, 0.0, 1.0) }), p => Audible(p) && p.Operators[op].Waveform == OperatorWaveform.Elliptic));
            }

            if (capabilities.SupportsSharedNoise)
            {
                // Roughness by the shared noise exists once a mutation or a prior has given an operator some; the
                // search then tunes it, and leaves the many clean operators out of its space.
                list.Add(Smooth(id + ".noisePhase", group, 0.0, 4.0, ParameterTransforms.SoftLogarithmic(0.1), p => p.Operators[op].NoisePhase, (p, v) => p.WithOperator(op, p.Operators[op] with { NoisePhase = Math.Max(v, 1e-4) }), p => Audible(p) && p.Operators[op].NoisePhase != 0.0 && p.Operators[op].Waveform != OperatorWaveform.Noise));
                list.Add(Smooth(id + ".noiseM", group, -1.0, 1.0, ParameterTransforms.SoftLogarithmic(0.05), p => p.Operators[op].NoiseM, (p, v) => p.WithOperator(op, p.Operators[op] with { NoiseM = Math.Clamp(v, -1.0, 1.0) is var m && m != 0.0 ? m : 1e-4 }), p => Audible(p) && p.Operators[op].NoiseM != 0.0 && p.Operators[op].Waveform == OperatorWaveform.Elliptic));
            }

            if (capabilities.SupportsOperatorFilters)
            {
                // The filter of an operator, once a mutation or a prior has given it one: only the values its type uses.
                string filterGroup = group + "/Filter";
                string filterId = id + ".operatorFilter";
                OperatorMatrixPatch WithFilter(OperatorMatrixPatch p, Func<OperatorFilter, OperatorFilter> change) =>
                    p.Operators[op].Filter is { } f ? p.WithOperator(op, p.Operators[op] with { Filter = change(f) }) : p;
                bool Has(OperatorMatrixPatch p, Func<OperatorFilter, bool> uses) => Audible(p) && p.Operators[op].Filter is { } f && uses(f);
                double Read(OperatorMatrixPatch p, Func<OperatorFilter, double> value, double fallback) => p.Operators[op].Filter is { } f ? value(f) : fallback;

                // The search ranges, widened to a value the patch already has (a hand-made F1 at 50 Hz), so that the
                // first write does not move it. F2 is searched up to 8 times F1 and F3 up to 16 times (the vowels of
                // the formant table reach about 8 and 11), widened up to the model's 32.
                OperatorFilter? present = patch.Operators[op].Filter;
                double lowHz = Math.Max(10.0, Math.Min(80.0, present?.FixedHz ?? 80.0));
                double highHz = Math.Min(20000.0, Math.Max(8000.0, present?.FixedHz ?? 8000.0));
                double highRatio = Math.Min(OperatorFilter.MaxFormantRatio, Math.Max(8.0, present?.FormantRatio ?? 8.0));
                double highRatio3 = Math.Min(OperatorFilter.MaxFormantRatio, Math.Max(16.0, present?.Formant3Ratio ?? 16.0));

                // A formant filter's peaks can be much narrower than the other filters' resonance.
                double highQ = present?.Type == OperatorFilterType.Formant ? OperatorFilter.MaxFormantQ : OperatorFilter.MaxQ;
                static double MaxQOf(OperatorFilter f) => f.Type == OperatorFilterType.Formant ? OperatorFilter.MaxFormantQ : OperatorFilter.MaxQ;
                static bool IsFormant(OperatorFilter f) => f.Type == OperatorFilterType.Formant;

                // A third formant is searched once a prior, a mutation or the library has given the filter one: while
                // it is off, its ratio and Q would be directions that do nothing.
                static bool HasF3(OperatorFilter f) => IsFormant(f) && f.Formant3Level > 0.0;

                // The own Qs of F2 and F3 are searched relative to the filter's Q: the coordinate is own Q / Q, and 1
                // means "same as Q" (stored as 0). Moving Q alone then moves a peak without an own Q with it, as
                // before; Q is written before these in every parameter set, as the schema lists it first. The range is
                // 1/8..8 times Q (the table's F2 and F3 are up to about 6 times as narrow as F1), widened to a
                // hand-made own Q, and the own Q itself always stays within 0.5..50.
                (double lowQ2, double highQ2) = RelativeRange(present, static f => f.FormantQ2);
                (double lowQ3, double highQ3) = RelativeRange(present, static f => f.Formant3Q);

                list.Add(Smooth(filterId + ".cutoff", filterGroup, 0.25, 64.0, ParameterTransforms.Logarithmic, p => Read(p, static f => f.CutoffRatio, 4.0), (p, v) => WithFilter(p, f => f with { CutoffRatio = Math.Clamp(v, 0.25, 64.0) }), p => Has(p, static f => f.IsKeyTracked)));
                list.Add(Smooth(filterId + ".fixedHz", filterGroup, lowHz, highHz, ParameterTransforms.Logarithmic, p => Read(p, static f => f.FixedHz, 1000.0), (p, v) => WithFilter(p, f => f with { FixedHz = Math.Clamp(v, lowHz, highHz) }), p => Has(p, static f => !f.IsKeyTracked)));
                list.Add(Smooth(filterId + ".q", filterGroup, OperatorFilter.MinQ, highQ, ParameterTransforms.Logarithmic, p => Read(p, static f => f.Q, OperatorFilter.ButterworthQ), (p, v) => WithFilter(p, f => f with { Q = Math.Clamp(v, OperatorFilter.MinQ, MaxQOf(f)) }), p => Has(p, static _ => true)));
                list.Add(Smooth(filterId + ".formantRatio", filterGroup, 1.0, highRatio, ParameterTransforms.Logarithmic, p => Read(p, static f => f.FormantRatio, 2.0), (p, v) => WithFilter(p, f => f with { FormantRatio = Math.Clamp(v, 1.0, highRatio) }), p => Has(p, IsFormant)));
                list.Add(Smooth(filterId + ".formantLevel", filterGroup, 0.0, 1.0, ParameterTransforms.Linear, p => Read(p, static f => f.FormantLevel, 0.5), (p, v) => WithFilter(p, f => f with { FormantLevel = Math.Clamp(v, 0.0, 1.0) }), p => Has(p, IsFormant)));
                list.Add(Smooth(filterId + ".formantQ2", filterGroup, lowQ2, highQ2, ParameterTransforms.Logarithmic, p => Read(p, static f => RelativeQ(f.FormantQ2, f.Q), 1.0), (p, v) => WithFilter(p, f => f with { FormantQ2 = OwnQ(v, f.Q) }), p => Has(p, IsFormant)));
                list.Add(Smooth(filterId + ".formant3Ratio", filterGroup, 1.0, highRatio3, ParameterTransforms.Logarithmic, p => Read(p, static f => f.Formant3Ratio, 4.0), (p, v) => WithFilter(p, f => f with { Formant3Ratio = Math.Clamp(v, 1.0, highRatio3) }), p => Has(p, HasF3)));
                list.Add(Smooth(filterId + ".formant3Level", filterGroup, 0.0, 1.0, ParameterTransforms.Linear, p => Read(p, static f => f.Formant3Level, 0.3), (p, v) => WithFilter(p, f => f with { Formant3Level = Math.Clamp(v, 0.0, 1.0) }), p => Has(p, HasF3)));
                list.Add(Smooth(filterId + ".formant3Q", filterGroup, lowQ3, highQ3, ParameterTransforms.Logarithmic, p => Read(p, static f => RelativeQ(f.Formant3Q, f.Q), 1.0), (p, v) => WithFilter(p, f => f with { Formant3Q = OwnQ(v, f.Q) }), p => Has(p, HasF3)));
                list.Add(Smooth(filterId + ".peakGain", filterGroup, -24.0, 24.0, ParameterTransforms.Linear, p => Read(p, static f => f.PeakGainDb, 6.0), (p, v) => WithFilter(p, f => f with { PeakGainDb = Math.Clamp(v, -24.0, 24.0) }), p => Has(p, static f => f.Type == OperatorFilterType.Peak)));
                list.Add(Smooth(filterId + ".octaves", filterGroup, -4.0, 4.0, ParameterTransforms.Linear, p => Read(p, static f => f.EnvelopeOctaves, 0.0), (p, v) => WithFilter(p, f => f with { EnvelopeOctaves = Math.Clamp(v, -4.0, 4.0) }), p => Has(p, static f => f.Envelope is not null)));
                if (patch.Operators[op].Filter?.Envelope is { } filterShape)
                {
                    AddEnvelope(
                        list,
                        limits,
                        filterId + ".env",
                        filterGroup + "/Envelope",
                        filterShape,
                        1.0,
                        ParameterTransforms.Linear,
                        p => p.Operators[op].Filter?.Envelope,
                        (p, e) => WithFilter(p, f => f with { Envelope = e }),
                        p => Has(p, static f => f.Envelope is not null));
                }
            }

            string frequency = group + "/Frequency";
            list.Add(Structural(id + ".frequency.mode", frequency, [.. frequencyModes], p => (int)p.Operators[op].Frequency.Mode, (p, v) => p.WithOperator(op, p.Operators[op] with { Frequency = p.Operators[op].Frequency with { Mode = (OperatorFrequencyMode)(int)v } }), Enabled));
            list.Add(Structural(id + ".frequency.ratio", frequency, ratios, p => p.Operators[op].Frequency.Ratio, (p, v) => p.WithOperator(op, p.Operators[op] with { Frequency = p.Operators[op].Frequency with { Ratio = v } }), p => Enabled(p) && p.Operators[op].Frequency.Mode == OperatorFrequencyMode.Ratio));
            list.Add(Smooth(id + ".frequency.fixedHz", frequency, 1.0, 12000.0, ParameterTransforms.Logarithmic, p => p.Operators[op].Frequency.FixedFrequencyHz, (p, v) => p.WithOperator(op, p.Operators[op] with { Frequency = p.Operators[op].Frequency with { FixedFrequencyHz = v } }), p => Audible(p) && p.Operators[op].Frequency.Mode == OperatorFrequencyMode.Fixed));
            list.Add(Smooth(id + ".fineTune", frequency, -50.0, 50.0, ParameterTransforms.Linear, p => p.Operators[op].FineTuneCents, (p, v) => p.WithOperator(op, p.Operators[op] with { FineTuneCents = v }), p => Audible(p) && p.Operators[op].Frequency.Mode == OperatorFrequencyMode.Ratio));
            list.Add(Smooth(id + ".detune", frequency, -8.0, 8.0, ParameterTransforms.SoftLogarithmic(0.5), p => p.Operators[op].DetuneHz, (p, v) => p.WithOperator(op, p.Operators[op] with { DetuneHz = v }), Audible));
            list.Add(Smooth(id + ".phase", frequency, 0.0, 2.0 * Math.PI, ParameterTransforms.Linear, p => p.Operators[op].InitialPhase, (p, v) => p.WithOperator(op, p.Operators[op] with { InitialPhase = v }), Audible));

            AddEnvelope(
                list,
                limits,
                id + ".env",
                group + "/Amplitude Envelope",
                patch.Operators[op].AmplitudeEnvelope,
                1.0,
                ParameterTransforms.SoftLogarithmic(0.01),
                p => p.Operators[op].AmplitudeEnvelope,
                (p, e) => p.WithOperator(op, p.Operators[op] with { AmplitudeEnvelope = e }),
                Audible);

            list.Add(Smooth(id + ".velocity", group, 0.0, 1.0, ParameterTransforms.Linear, p => p.Operators[op].VelocitySensitivity, (p, v) => p.WithOperator(op, p.Operators[op] with { VelocitySensitivity = v }), Audible));
            list.Add(Smooth(id + ".keyScaling.level", group, -12.0, 12.0, ParameterTransforms.Linear, p => p.Operators[op].KeyScaling.LevelDbPerOctave, (p, v) => p.WithOperator(op, p.Operators[op] with { KeyScaling = p.Operators[op].KeyScaling with { LevelDbPerOctave = v } }), Audible));
            list.Add(Smooth(id + ".keyScaling.rate", group, -1.0, 1.0, ParameterTransforms.Linear, p => p.Operators[op].KeyScaling.RatePerOctave, (p, v) => p.WithOperator(op, p.Operators[op] with { KeyScaling = p.Operators[op].KeyScaling with { RatePerOctave = v } }), Audible));

            string output = group + "/Output";
            list.Add(Structural(id + ".output.enabled", output, [0, 1], p => p.OutputLevels[op] != 0.0 ? 1 : 0, (p, v) => p.WithOutputLevel(op, v >= 0.5 ? (p.OutputLevels[op] != 0.0 ? p.OutputLevels[op] : 0.5) : 0.0), Enabled));
            list.Add(Smooth(id + ".output.level", output, 0.001, 1.5, ParameterTransforms.Logarithmic, p => p.OutputLevels[op], (p, v) => p.WithOutputLevel(op, v), p => Enabled(p) && p.OutputLevels[op] != 0.0));
        }

        for (int d = 0; d < n; d++)
        {
            for (int s = 0; s < n; s++)
            {
                int source = s, destination = d;
                bool self = source == destination;
                if (self ? !capabilities.SupportsSelfFeedback : !capabilities.SupportsFullModulationMatrix && source < destination)
                {
                    // The backend has no such cell, so the schema does not offer it.
                    continue;
                }

                string id = "edge." + Number(source) + ">" + Number(destination);
                string group = "Patch/Modulation Matrix/OP" + Number(source) + " -> OP" + Number(destination);
                bool BothEnabled(OperatorMatrixPatch p) => p.Operators[source].Enabled && p.Operators[destination].Enabled;
                bool Live(OperatorMatrixPatch p) => BothEnabled(p) && p.ModulationMatrix.Find(source, destination) is { Enabled: true } && p.FindAudibleOperators()[destination];

                list.Add(Structural(
                    id + ".enabled",
                    group,
                    [0, 1],
                    p => p.ModulationMatrix.Find(source, destination) is { Enabled: true } ? 1 : 0,
                    (p, v) =>
                    {
                        ModulationEdge edge = p.ModulationMatrix.Find(source, destination) ?? new ModulationEdge(source, destination, false, 0.5);
                        return p.WithEdge(edge with { Enabled = v >= 0.5 });
                    },
                    BothEnabled));
                list.Add(Smooth(
                    id + ".amount",
                    group,
                    0.0,
                    Math.Min(12.0, capabilities.MaxModulationRadiansPerDestination),
                    ParameterTransforms.SoftLogarithmic(0.25),
                    p => p.ModulationMatrix.Find(source, destination)?.Amount ?? 0.0,
                    (p, v) => p.ModulationMatrix.Find(source, destination) is { } edge ? p.WithEdge(edge with { Amount = v }) : p,
                    Live));

                if (capabilities.SupportsDynamicModulationMatrix && patch.ModulationMatrix.Find(source, destination)?.AmountEnvelope is { } shape)
                {
                    AddEnvelope(
                        list,
                        limits,
                        id + ".env",
                        group + "/Amount Envelope",
                        shape,
                        1.0,
                        ParameterTransforms.SoftLogarithmic(0.01),
                        p => p.ModulationMatrix.Find(source, destination)?.AmountEnvelope,
                        (p, e) => p.ModulationMatrix.Find(source, destination) is { } edge ? p.WithEdge(edge with { AmountEnvelope = e }) : p,
                        p => Live(p) && p.ModulationMatrix.Find(source, destination)?.AmountEnvelope is not null);
                }
            }
        }

        const string pitchGroup = "Patch/Pitch Model";
        if (patch.Pitch.PitchEnvelope is { } pitchShape)
        {
            AddEnvelope(
                list,
                limits,
                "pitch.env",
                pitchGroup + "/Pitch Envelope",
                pitchShape,
                1200.0,
                ParameterTransforms.SoftLogarithmic(10.0),
                static p => p.Pitch.PitchEnvelope,
                static (p, e) => p with { Pitch = p.Pitch with { PitchEnvelope = e } },
                static p => p.Pitch.PitchEnvelope is not null,
                signedLevels: true);
        }

        if (patch.Pitch.Vibrato is not null)
        {
            string vibrato = pitchGroup + "/Vibrato";
            static bool HasVibrato(OperatorMatrixPatch p) => p.Pitch.Vibrato is not null;
            list.Add(Smooth("pitch.vibrato.rate", vibrato, 0.5, 12.0, ParameterTransforms.Logarithmic, static p => p.Pitch.Vibrato?.RateHz ?? 5.0, static (p, v) => p with { Pitch = p.Pitch with { Vibrato = p.Pitch.Vibrato! with { RateHz = v } } }, HasVibrato));
            list.Add(Smooth("pitch.vibrato.depth", vibrato, 0.0, 100.0, ParameterTransforms.SoftLogarithmic(5.0), static p => p.Pitch.Vibrato?.DepthCents ?? 0.0, static (p, v) => p with { Pitch = p.Pitch with { Vibrato = p.Pitch.Vibrato! with { DepthCents = v } } }, HasVibrato));
            list.Add(Smooth("pitch.vibrato.delay", vibrato, 0.0, 2.0, ParameterTransforms.SoftLogarithmic(0.05), static p => p.Pitch.Vibrato?.DelaySeconds ?? 0.0, static (p, v) => p with { Pitch = p.Pitch with { Vibrato = p.Pitch.Vibrato! with { DelaySeconds = v } } }, HasVibrato));
            list.Add(Smooth("pitch.vibrato.fadeIn", vibrato, 0.0, 2.0, ParameterTransforms.SoftLogarithmic(0.05), static p => p.Pitch.Vibrato?.FadeInSeconds ?? 0.0, static (p, v) => p with { Pitch = p.Pitch with { Vibrato = p.Pitch.Vibrato! with { FadeInSeconds = v } } }, HasVibrato));
        }

        if (capabilities.SupportsSharedNoise)
        {
            // The colour of the shared noise matters once an operator plays it or is roughened by it.
            const string noiseGroup = "Patch/Shared Noise";
            list.Add(Smooth("noise.color", noiseGroup, -1.0, 1.0, ParameterTransforms.Linear, static p => p.Noise.Color, static (p, v) => p with { Noise = p.Noise with { Color = Math.Clamp(v, -1.0, 1.0) } }, UsesSharedNoise));
            list.Add(Smooth("noise.cutoff", noiseGroup, 20.0, 20000.0, ParameterTransforms.Logarithmic, static p => p.Noise.CutoffHz, static (p, v) => p with { Noise = p.Noise with { CutoffHz = Math.Clamp(v, 20.0, 20000.0) } }, UsesSharedNoise));
        }

        return new PatchParameterSchema(list);
    }

    /// <summary>True when an audible operator plays the shared noise or is roughened by it.</summary>
    /// <param name="patch">Patch.</param>
    public static bool UsesSharedNoise(OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool[] audible = patch.FindAudibleOperators();
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            OperatorPatch op = patch.Operators[i];
            if (audible[i] && (op.Waveform == OperatorWaveform.Noise || op.NoisePhase != 0.0 || (op.Waveform == OperatorWaveform.Elliptic && op.NoiseM != 0.0)))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddEnvelope(
        List<PatchParameter> list,
        EnvelopeTimeLimits limits,
        string id,
        string group,
        OperatorEnvelope shape,
        double maxLevel,
        IParameterTransform levelTransform,
        Func<OperatorMatrixPatch, OperatorEnvelope?> get,
        Func<OperatorMatrixPatch, OperatorEnvelope, OperatorMatrixPatch> set,
        Func<OperatorMatrixPatch, bool> isActive,
        bool signedLevels = false)
    {
        // Sustaining envelopes with two segments between the attack and the sustain point also get a derived axis:
        // where in the held note the first of the two hands over to the second. Moving it keeps the pair exactly as
        // long as the held note, so no segment can outgrow the note and silence the points after it.
        if (!ReferenceEquals(limits, EnvelopeTimeLimits.Unlimited) && shape.SustainPoint == 3 && shape.Points.Count >= 5)
        {
            double held = limits.HeldSeconds;
            list.Add(Smooth(
                id + ".breakPosition",
                group,
                0.02,
                0.98,
                ParameterTransforms.Linear,
                p =>
                {
                    if (get(p) is not { } e || e.Points.Count < 4)
                    {
                        return 0.5;
                    }

                    double total = e.Points[2].TimeSeconds + e.Points[3].TimeSeconds;
                    return total > 0 ? e.Points[2].TimeSeconds / total : 0.5;
                },
                (p, v) =>
                {
                    if (get(p) is not { } e || e.Points.Count < 4)
                    {
                        return p;
                    }

                    double total = Math.Max(0.01, held - e.Points[1].TimeSeconds);
                    OperatorEnvelope moved = WithPoint(e, 2, e.Points[2] with { TimeSeconds = v * total });
                    return set(p, WithPoint(moved, 3, moved.Points[3] with { TimeSeconds = (1.0 - v) * total }));
                },
                p => isActive(p) && get(p) is { SustainPoint: 3 }));
        }

        for (int k = 0; k < shape.Points.Count; k++)
        {
            int point = k;
            string pointId = id + ".p" + k.ToString(CultureInfo.InvariantCulture);
            bool Present(OperatorMatrixPatch p) => isActive(p) && get(p) is { } e && e.Points.Count > point;
            if (k > 0)
            {
                list.Add(Smooth(
                    pointId + ".time",
                    group,
                    0.0005,
                    Math.Max(0.002, shape.SustainPoint is int sustain && point > sustain ? limits.ReleaseSeconds : limits.HeldSeconds),
                    ParameterTransforms.Logarithmic,
                    p => get(p) is { } e && e.Points.Count > point ? e.Points[point].TimeSeconds : 0.0005,
                    (p, v) => get(p) is { } e && e.Points.Count > point ? set(p, WithPoint(e, point, e.Points[point] with { TimeSeconds = v })) : p,
                    Present));
            }

            list.Add(Smooth(
                pointId + ".level",
                group,
                signedLevels ? -maxLevel : 0.0,
                maxLevel,
                levelTransform,
                p => get(p) is { } e && e.Points.Count > point ? e.Points[point].Level : 0.0,
                (p, v) => get(p) is { } e && e.Points.Count > point ? set(p, WithPoint(e, point, e.Points[point] with { Level = v })) : p,
                Present));
        }
    }

    /// <summary>The smallest and largest relative own Q to search: 1/8..8, widened to the one the filter has.</summary>
    private static (double Low, double High) RelativeRange(OperatorFilter? present, Func<OperatorFilter, double> own)
    {
        double relative = present is { Type: OperatorFilterType.Formant } f && own(f) > 0.0 ? own(f) / f.Q : 1.0;
        return (Math.Min(MinRelativeQ, relative), Math.Max(MaxRelativeQ, relative));
    }

    /// <summary>A peak's own Q as a multiple of the filter's Q; 1 for a peak without one (0).</summary>
    private static double RelativeQ(double own, double shared) => own > 0.0 ? own / shared : 1.0;

    /// <summary>
    /// The own Q of a formant peak to store for a searched multiple of the filter's Q: "same as Q" (0) at 1, so that
    /// reading and writing back a peak without an own Q leaves the patch as it was; the own Q within 0.5..50 otherwise.
    /// </summary>
    private static double OwnQ(double relative, double shared) =>
        Math.Abs(relative - 1.0) <= 1e-9 ? 0.0 : Math.Clamp(relative * shared, OperatorFilter.MinQ, OperatorFilter.MaxFormantQ);

    private static OperatorEnvelope WithPoint(OperatorEnvelope envelope, int index, EnvelopePoint point)
    {
        EnvelopePoint[] points = [.. envelope.Points];
        points[index] = point;
        return envelope with { Points = points };
    }

    private static PatchParameter Smooth(
        string id,
        string group,
        double minimum,
        double maximum,
        IParameterTransform transform,
        Func<OperatorMatrixPatch, double> get,
        Func<OperatorMatrixPatch, double, OperatorMatrixPatch> set,
        Func<OperatorMatrixPatch, bool> isActive) =>
        new(id, group, ReferenceEquals(transform, ParameterTransforms.Linear) ? ParameterSearchKind.Continuous : ParameterSearchKind.WarpedContinuous, minimum, maximum, transform, get, set, isActive);

    private static PatchParameter Structural(
        string id,
        string group,
        IReadOnlyList<double> choices,
        Func<OperatorMatrixPatch, double> get,
        Func<OperatorMatrixPatch, double, OperatorMatrixPatch> set,
        Func<OperatorMatrixPatch, bool> isActive)
    {
        double min = choices.Count > 0 ? choices.Min() : 0;
        double max = choices.Count > 0 ? choices.Max() : 0;
        return new PatchParameter(id, group, ParameterSearchKind.StructuralDiscrete, min, max, ParameterTransforms.Linear, get, set, isActive, choices);
    }

    private static string Number(int zeroBased) => (zeroBased + 1).ToString(CultureInfo.InvariantCulture);

    private static ParameterGroup BuildGroup(string name, string path, List<PatchParameter> all)
    {
        var own = new List<PatchParameter>();
        var childNames = new List<string>();
        string prefix = path + "/";
        foreach (PatchParameter parameter in all)
        {
            if (parameter.GroupPath == path)
            {
                own.Add(parameter);
            }
            else if (parameter.GroupPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = parameter.GroupPath[prefix.Length..];
                int slash = rest.IndexOf('/', StringComparison.Ordinal);
                string child = slash < 0 ? rest : rest[..slash];
                if (!childNames.Contains(child))
                {
                    childNames.Add(child);
                }
            }
        }

        var children = new List<ParameterGroup>(childNames.Count);
        foreach (string child in childNames)
        {
            children.Add(BuildGroup(child, prefix + child, all));
        }

        return new ParameterGroup(name, path, own, children);
    }
}

/// <summary>
/// Longest envelope times that still make a difference for the note being matched. A segment that outlasts the held
/// note silences every point after it, which leaves a continuous optimizer with directions that do nothing; bounding
/// the times by the note removes those dead regions without losing any shape the note could reveal.
/// </summary>
/// <param name="HeldSeconds">Longest duration of a segment before the release.</param>
/// <param name="ReleaseSeconds">Longest duration of a release segment.</param>
public sealed record EnvelopeTimeLimits(double HeldSeconds, double ReleaseSeconds)
{
    /// <summary>No note-specific limits: 20 seconds for every segment.</summary>
    public static EnvelopeTimeLimits Unlimited { get; } = new(20.0, 20.0);
}
