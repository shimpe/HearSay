using System.Globalization;
using HearSay.OperatorMatrix.Parameters;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Editing;

/// <summary>
/// One number of a patch the user can type or drag: what it is called, its unit, its range and scale, and how it is read
/// and written. Ranges and scales follow the search's parameter schema where it has the value, widened to the value the
/// patch has, so that touching a value never makes it jump.
/// </summary>
/// <param name="Id">The schema's parameter id, for example <c>op1.fineTune</c> or <c>edge.2&gt;1.amount</c>.</param>
/// <param name="Label">A short name for the user, for example "fine tune".</param>
/// <param name="Unit">The unit shown after the number, for example "cents"; empty for none.</param>
/// <param name="Minimum">The smallest value.</param>
/// <param name="Maximum">The largest value.</param>
/// <param name="Logarithmic">True when equal steps of a drag are equal ratios; false when they are equal differences.</param>
/// <param name="SnapPoints">The values a drag can snap to; null for none.</param>
public sealed record EditableValue(string Id, string Label, string Unit, double Minimum, double Maximum, bool Logarithmic, IReadOnlyList<double>? SnapPoints)
{
    /// <summary>Pixels a drag takes to cross the whole range; ten times as many when fine.</summary>
    private const double PixelsPerRange = 200.0;

    /// <summary>
    /// The scale a drag moves along, the schema's transform where it has the value; null for the scale
    /// <see cref="Logarithmic"/> names (logarithmic or linear).
    /// </summary>
    internal IParameterTransform? Transform { get; init; }

    /// <summary>Reads the value; null when the patch does not have it.</summary>
    internal Func<OperatorMatrixPatch, double?> Reader { get; init; } = static _ => null;

    /// <summary>Writes a value that has been checked and clamped.</summary>
    internal Func<OperatorMatrixPatch, double, OperatorMatrixPatch> Writer { get; init; } = static (p, _) => p;

    /// <summary>
    /// The largest magnitude the rest of the patch leaves for the value, besides its range (the modulation limit for a
    /// connection amount); null when only the range limits it.
    /// </summary>
    internal Func<OperatorMatrixPatch, SynthesisCapabilities, double>? Limit { get; init; }

    /// <summary>Reads the value from a patch; NaN when the patch does not have it (any more).</summary>
    /// <param name="p">The patch.</param>
    public double Get(OperatorMatrixPatch p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return Reader(p) ?? double.NaN;
    }

    /// <summary>
    /// Sets the value: one that is not a number is refused; others are clamped to the range and, for a connection amount,
    /// to what the modulation limit of its destination leaves. Refused when the patch no longer has the value.
    /// </summary>
    /// <param name="value">The new value.</param>
    public PatchEdit Set(double value) => (patch, c) =>
    {
        if (!double.IsFinite(value))
        {
            return EditOutcome.Refused(patch, $"The {Label} must be a number.");
        }

        if (Reader(patch) is not double current)
        {
            return EditOutcome.Refused(patch, $"This part of the patch has no {Label} (any more).");
        }

        double clamped = Math.Clamp(value, Minimum, Maximum);
        if (Limit is { } limit)
        {
            double most = limit(patch, c);
            clamped = Math.Clamp(clamped, -most, most);
        }

        return EditOutcome.Done(clamped == current ? patch : Writer(patch, clamped));
    };

    /// <summary>
    /// The value a sideways drag reaches: each pixel moves it 1/200 of the range (1/2000 when fine) along its scale (the
    /// schema's transform: in ratios when logarithmic, finer near zero when soft-logarithmic); the result stays within
    /// the range and, with <paramref name="snap"/>, goes to the nearest snap point. No movement leaves the value as it
    /// was.
    /// </summary>
    /// <param name="from">The value when the drag began.</param>
    /// <param name="pixels">How far the pointer moved since; negative to the left.</param>
    /// <param name="fine">True for a ten times finer drag.</param>
    /// <param name="snap">True to snap to the nearest of <see cref="SnapPoints"/>.</param>
    public double Nudge(double from, double pixels, bool fine, bool snap)
    {
        from = double.IsFinite(from) ? Math.Clamp(from, Minimum, Maximum) : Minimum;
        if (pixels == 0 || double.IsNaN(pixels))
        {
            return from;
        }

        IParameterTransform scale = Transform ?? (Logarithmic ? ParameterTransforms.Logarithmic : ParameterTransforms.Linear);
        double low = scale.ToSearchSpace(Minimum);
        double high = scale.ToSearchSpace(Maximum);
        double fraction = pixels / (fine ? 10.0 * PixelsPerRange : PixelsPerRange);
        double value = scale.ToNativeSpace(scale.ToSearchSpace(from) + (fraction * (high - low)));
        value = Math.Clamp(double.IsNaN(value) ? from : value, Minimum, Maximum);
        return snap ? Snapped(value) : value;
    }

    private double Snapped(double value)
    {
        if (SnapPoints is null)
        {
            return value;
        }

        bool ratios = Logarithmic && value > 0;
        double best = value;
        double bestDistance = double.PositiveInfinity;
        foreach (double point in SnapPoints)
        {
            if (point < Minimum || point > Maximum || (ratios && point <= 0))
            {
                continue;
            }

            double distance = ratios ? Math.Abs(Math.Log(point / value)) : Math.Abs(point - value);
            if (distance < bestDistance)
            {
                best = point;
                bestDistance = distance;
            }
        }

        return best;
    }
}

/// <summary>
/// The numbers of each part of a patch: an operator, a connection, the shared noise or the pitch model. Only the values
/// that exist on that part are listed: a ratio only in ratio mode, a pulse width only on a pulse, the values of a filter
/// only on an operator that has one, and so on. Envelopes are edited with <see cref="EnvelopeEdits"/>.
/// </summary>
public static class EditableValues
{
    /// <summary>Smallest ratio an operator can be given.</summary>
    private const double MinRatio = 1.0 / 16.0;

    /// <summary>Largest ratio an operator can be given.</summary>
    private const double MaxRatio = 32.0;

    /// <summary>
    /// The scale of an output level: logarithmic above the schema's smallest level (0.001) and linear below it, so that
    /// a drag reaches 0, where the operator is no longer heard, smoothly.
    /// </summary>
    private static readonly IParameterTransform OutputScale = ParameterTransforms.SoftLogarithmic(0.001);

    /// <summary>The schema's scale of a connection amount, for a connection the schema does not offer.</summary>
    private static readonly IParameterTransform AmountScale = ParameterTransforms.SoftLogarithmic(0.25);

    /// <summary>The values of a part of a patch that exist on it; empty when the patch has no such part.</summary>
    /// <param name="patch">The patch.</param>
    /// <param name="c">Capabilities of the sound's profile.</param>
    /// <param name="part">The part.</param>
    public static IReadOnlyList<EditableValue> For(OperatorMatrixPatch patch, SynthesisCapabilities c, PatchPart part)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(part);
        PatchParameterSchema schema = PatchParameterSchema.Build(patch, c);
        var values = new List<EditableValue>();
        switch (part)
        {
            case PatchPart.OperatorPart { Index: var op } when (uint)op < (uint)patch.OperatorCount:
                AddOperator(values, schema, patch, c, op);
                break;
            case PatchPart.ConnectionPart { Source: var source, Destination: var destination }
                when (uint)source < (uint)patch.OperatorCount && (uint)destination < (uint)patch.OperatorCount && patch.ModulationMatrix.Find(source, destination) is not null:
                values.Add(Amount(schema, patch, c, source, destination));
                break;
            case PatchPart.NoisePart when c.SupportsSharedNoise:
                values.Add(Make(schema, patch, "noise.color", "colour", "", (-1.0, 1.0, false), static p => p.Noise.Color, static (p, v) => p with { Noise = p.Noise with { Color = v } }));
                values.Add(Make(schema, patch, "noise.cutoff", "cutoff", "Hz", (5.0, 20000.0, true), static p => p.Noise.CutoffHz, static (p, v) => p with { Noise = p.Noise with { CutoffHz = v } }));
                break;
            case PatchPart.PitchPart when patch.Pitch.Vibrato is not null:
                values.Add(Vibrato(schema, patch, "rate", "vibrato rate", "Hz", (0.5, 12.0, true), static l => l.RateHz, static (l, v) => l with { RateHz = v }));
                values.Add(Vibrato(schema, patch, "depth", "vibrato depth", "cents", (0.0, 100.0, false), static l => l.DepthCents, static (l, v) => l with { DepthCents = v }));
                values.Add(Vibrato(schema, patch, "delay", "vibrato delay", "s", (0.0, 2.0, false), static l => l.DelaySeconds, static (l, v) => l with { DelaySeconds = v }));
                values.Add(Vibrato(schema, patch, "fadeIn", "vibrato fade-in", "s", (0.0, 2.0, false), static l => l.FadeInSeconds, static (l, v) => l with { FadeInSeconds = v }));
                break;
        }

        return values;
    }

    private static void AddOperator(List<EditableValue> values, PatchParameterSchema schema, OperatorMatrixPatch patch, SynthesisCapabilities c, int op)
    {
        string prefix = "op" + (op + 1).ToString(CultureInfo.InvariantCulture) + ".";
        OperatorPatch present = patch.Operators[op];

        // A value of this operator, when the operator, as it is in the patch being edited, has it.
        void Add(string name, string label, string unit, (double, double, bool) model, Func<OperatorPatch, bool> has, Func<OperatorPatch, double> get, Func<OperatorPatch, double, OperatorPatch> set, bool useSchema = true, IReadOnlyList<double>? snaps = null)
        {
            if (!has(present))
            {
                return;
            }

            values.Add(Make(
                schema,
                patch,
                prefix + name,
                label,
                unit,
                model,
                p => (uint)op < (uint)p.OperatorCount && has(p.Operators[op]) ? get(p.Operators[op]) : null,
                (p, v) => p.WithOperator(op, set(p.Operators[op], v)),
                useSchema,
                snaps));
        }

        // A value of this operator's filter, for the filter types that use it.
        void AddFilter(string name, string label, string unit, (double, double, bool) model, Func<OperatorFilter, bool> uses, Func<OperatorFilter, double> get, Func<OperatorFilter, double, OperatorFilter> set, bool useSchema = true)
            => Add(
                "operatorFilter." + name,
                label,
                unit,
                model,
                o => o.Filter is { } f && uses(f),
                o => get(o.Filter!),
                (o, v) => o with { Filter = set(o.Filter!, v) },
                useSchema);

        // A formant F2..F5 (n = 2..5): its ratio to F1, its level and its own Q. The model's ranges, not the search's:
        // the search keeps the ratios lower, keeps F3's level above a floor where the editor can turn it off (0), and has
        // the own Qs of F2 and F3 relative to Q (own Q / Q) where the editor shows the Q itself. An own Q that is "same as Q" (0) shows the Q the formant plays at; setting it stores it.
        void AddFormant(int n, string ratioName, string levelName, string qName, Func<OperatorFilter, FormantPeak> get, Func<OperatorFilter, FormantPeak, OperatorFilter> set)
        {
            string label = "F" + n.ToString(CultureInfo.InvariantCulture);
            static bool IsFormant(OperatorFilter f) => f.Type == OperatorFilterType.Formant;
            AddFilter(ratioName, label + " ratio", "", (1.0, OperatorFilter.MaxFormantRatio, true), IsFormant, f => get(f).Ratio, (f, v) => set(f, get(f) with { Ratio = v }), useSchema: false);
            AddFilter(levelName, label + " level", "", (0.0, 1.0, false), IsFormant, f => get(f).Level, (f, v) => set(f, get(f) with { Level = v }), useSchema: false);
            AddFilter(qName, label + " Q", "", (OperatorFilter.MinQ, OperatorFilter.MaxFormantQ, true), IsFormant, f => get(f).Q > 0.0 ? get(f).Q : f.Q, (f, v) => set(f, get(f) with { Q = v }), useSchema: false);
        }

        static bool AtRatio(OperatorPatch o) => o.Frequency.Mode == OperatorFrequencyMode.Ratio;
        static bool Always(OperatorPatch _) => true;

        // The ratio is continuous here, not the search's list of ratios, which become its snap points.
        Add("frequency.ratio", "ratio", "", (MinRatio, MaxRatio, true), AtRatio, static o => o.Frequency.Ratio, static (o, v) => o with { Frequency = o.Frequency with { Ratio = v } }, useSchema: false, snaps: PatchParameterSchema.DefaultRatios);
        Add("frequency.fixedHz", "frequency", "Hz", (1.0, 12000.0, true), static o => !AtRatio(o), static o => o.Frequency.FixedFrequencyHz, static (o, v) => o with { Frequency = o.Frequency with { FixedFrequencyHz = v } });
        Add("fineTune", "fine tune", "cents", (-50.0, 50.0, false), AtRatio, static o => o.FineTuneCents, static (o, v) => o with { FineTuneCents = v });
        Add("detune", "detune", "Hz", (-8.0, 8.0, false), Always, static o => o.DetuneHz, static (o, v) => o with { DetuneHz = v });
        Add("phase", "phase", "rad", (0.0, 2.0 * Math.PI, false), Always, static o => o.InitialPhase, static (o, v) => o with { InitialPhase = v });
        Add("shape", "pulse width", "", (0.01, 0.99, false), static o => o.Waveform == OperatorWaveform.Pulse, static o => o.Shape, static (o, v) => o with { Shape = v });
        Add("filter", "response", "", (0.0, 1.0, false), static o => o.Waveform == OperatorWaveform.FilteredNoise, static o => o.Shape, static (o, v) => o with { Shape = v });
        Add("elliptic", "elliptic m", "", (0.0, OperatorPatch.MaxEllipticM, false), static o => o.Waveform == OperatorWaveform.Elliptic, static o => o.Shape, static (o, v) => o with { Shape = v });
        Add("morph", "morph", "", (0.0, 1.0, false), static o => o.Waveform == OperatorWaveform.Elliptic, static o => o.Morph, static (o, v) => o with { Morph = v });

        // The output level reaches 0, where the operator is no longer heard: the editor's one row for the schema's
        // output.enabled and output.level.
        values.Add(Make(
            schema,
            patch,
            prefix + "output.level",
            "output",
            "",
            (0.0, 1.5, true),
            p => (uint)op < (uint)p.OperatorCount ? p.OutputLevels[op] : null,
            (p, v) => p.WithOutputLevel(op, v),
            minimum: 0.0,
            transform: OutputScale));

        Add("velocity", "velocity", "", (0.0, 1.0, false), Always, static o => o.VelocitySensitivity, static (o, v) => o with { VelocitySensitivity = v });
        Add("keyScaling.level", "key level", "dB/oct", (-12.0, 12.0, false), Always, static o => o.KeyScaling.LevelDbPerOctave, static (o, v) => o with { KeyScaling = o.KeyScaling with { LevelDbPerOctave = v } });
        Add("keyScaling.rate", "key rate", "/oct", (-1.0, 1.0, false), Always, static o => o.KeyScaling.RatePerOctave, static (o, v) => o with { KeyScaling = o.KeyScaling with { RatePerOctave = v } });

        AddFilter("cutoff", "cutoff", "x note", (0.25, 64.0, true), static f => f.IsKeyTracked, static f => f.CutoffRatio, static (f, v) => f with { CutoffRatio = v });
        bool formant = present.Filter?.Type == OperatorFilterType.Formant;
        AddFilter("fixedHz", formant ? "F1" : "centre", "Hz", (10.0, 20000.0, true), static f => !f.IsKeyTracked, static f => f.FixedHz, static (f, v) => f with { FixedHz = v });
        AddFilter("q", formant ? "Q1" : "Q", "", (OperatorFilter.MinQ, formant ? OperatorFilter.MaxFormantQ : OperatorFilter.MaxQ, true), static _ => true, static f => f.Q, static (f, v) => f with { Q = v });
        AddFormant(2, "formantRatio", "formantLevel", "formantQ2", static f => f.FormantPeaks[0], static (f, p) => f with { FormantRatio = p.Ratio, FormantLevel = p.Level, FormantQ2 = p.Q });
        AddFormant(3, "formant3Ratio", "formant3Level", "formant3Q", static f => f.FormantPeaks[1], static (f, p) => f with { Formant3Ratio = p.Ratio, Formant3Level = p.Level, Formant3Q = p.Q });
        AddFormant(4, "formant4Ratio", "formant4Level", "formant4Q", static f => f.FormantPeaks[2], static (f, p) => f with { Formant4Ratio = p.Ratio, Formant4Level = p.Level, Formant4Q = p.Q });
        AddFormant(5, "formant5Ratio", "formant5Level", "formant5Q", static f => f.FormantPeaks[3], static (f, p) => f with { Formant5Ratio = p.Ratio, Formant5Level = p.Level, Formant5Q = p.Q });
        AddFilter("peakGain", "gain", "dB", (-24.0, 24.0, false), static f => f.Type == OperatorFilterType.Peak, static f => f.PeakGainDb, static (f, v) => f with { PeakGainDb = v });
        AddFilter("octaves", "sweep", "oct", (-4.0, 4.0, false), static f => f.Envelope is not null, static f => f.EnvelopeOctaves, static (f, v) => f with { EnvelopeOctaves = v });

        // Roughness is listed once the operator has some (PatchEdits.SetRoughness gives it); it can then be set to 0.
        if (c.SupportsSharedNoise && present.NoisePhase != 0.0)
        {
            Add("noisePhase", "roughness", "rad", (0.0, 4.0, false), static o => o.Waveform != OperatorWaveform.Noise, static o => o.NoisePhase, static (o, v) => o with { NoisePhase = v });
        }

        if (c.SupportsSharedNoise && present.NoiseM != 0.0)
        {
            Add("noiseM", "m roughness", "", (-1.0, 1.0, false), static o => o.Waveform == OperatorWaveform.Elliptic, static o => o.NoiseM, static (o, v) => o with { NoiseM = v });
        }
    }

    /// <summary>
    /// The amount of a connection: 0 up to the profile's modulation limit, and no further than what the other
    /// connections into the same operator leave.
    /// </summary>
    private static EditableValue Amount(PatchParameterSchema schema, OperatorMatrixPatch patch, SynthesisCapabilities c, int source, int destination)
    {
        // The range is the profile's limit, not the schema's narrower search bound; the scale is the schema's.
        string id = string.Create(CultureInfo.InvariantCulture, $"edge.{source + 1}>{destination + 1}.amount");
        return Make(
            null,
            patch,
            id,
            "amount",
            "rad",
            (0.0, c.MaxModulationRadiansPerDestination, false),
            p => (uint)source < (uint)p.OperatorCount && (uint)destination < (uint)p.OperatorCount ? p.ModulationMatrix.Find(source, destination)?.Amount : null,
            (p, v) => p.WithEdge(p.ModulationMatrix.Find(source, destination)! with { Amount = v }),
            transform: schema.Find(id)?.Transform ?? AmountScale) with
        {
            Limit = (p, caps) => AmountLimit(p, caps, source, destination),
        };
    }

    /// <summary>
    /// The largest amount a connection can have under the modulation limit of its destination, counted as
    /// <see cref="PatchEdits"/> counts it; never less than the amount it has, so that the limit does not move a value
    /// that the patch already has.
    /// </summary>
    private static double AmountLimit(OperatorMatrixPatch patch, SynthesisCapabilities c, int source, int destination)
    {
        ModulationEdge edge = patch.ModulationMatrix.Find(source, destination)!;
        double peak = edge.AmountEnvelope is { } envelope ? PatchEdits.EnvelopePeak(envelope) : 1.0;
        if (!edge.Enabled || !patch.Operators[source].Enabled || peak <= 0.0)
        {
            // A connection that sends nothing does not count towards the limit.
            return double.PositiveInfinity;
        }

        double left = c.MaxModulationRadiansPerDestination - PatchEdits.IncomingModulation(patch, destination, exceptSource: source);
        return Math.Max(left / peak, Math.Abs(edge.Amount));
    }

    private static EditableValue Vibrato(PatchParameterSchema schema, OperatorMatrixPatch patch, string name, string label, string unit, (double, double, bool) model, Func<LfoSettings, double> get, Func<LfoSettings, double, LfoSettings> set)
        => Make(
            schema,
            patch,
            "pitch.vibrato." + name,
            label,
            unit,
            model,
            p => p.Pitch.Vibrato is { } vibrato ? get(vibrato) : null,
            (p, v) => p with { Pitch = p.Pitch with { Vibrato = set(p.Pitch.Vibrato!, v) } });

    /// <summary>
    /// A value with the schema's range and scale when it has the id (else the model's own range), widened to the value
    /// the patch has.
    /// </summary>
    /// <param name="schema">The search's schema; null to use the model's range.</param>
    /// <param name="patch">The patch whose value widens the range.</param>
    /// <param name="id">The parameter id.</param>
    /// <param name="label">A short name.</param>
    /// <param name="unit">The unit.</param>
    /// <param name="model">The range and scale from the model's own validation, for values the schema does not have.</param>
    /// <param name="read">Reads the value; null when the patch does not have it.</param>
    /// <param name="write">Writes a checked value.</param>
    /// <param name="useSchema">False to ignore the schema, as for the ratio, a list of choices there.</param>
    /// <param name="snaps">Snap points.</param>
    /// <param name="minimum">A minimum that replaces the schema's.</param>
    /// <param name="transform">A scale that replaces the schema's.</param>
    private static EditableValue Make(
        PatchParameterSchema? schema,
        OperatorMatrixPatch patch,
        string id,
        string label,
        string unit,
        (double Minimum, double Maximum, bool Logarithmic) model,
        Func<OperatorMatrixPatch, double?> read,
        Func<OperatorMatrixPatch, double, OperatorMatrixPatch> write,
        bool useSchema = true,
        IReadOnlyList<double>? snaps = null,
        double? minimum = null,
        IParameterTransform? transform = null)
    {
        (double min, double max, bool logarithmic) = model;
        IParameterTransform scale = logarithmic ? ParameterTransforms.Logarithmic : ParameterTransforms.Linear;
        if (useSchema && schema?.Find(id) is { IsSmooth: true } parameter)
        {
            min = parameter.Minimum;
            max = parameter.Maximum;
            logarithmic = ReferenceEquals(parameter.Transform, ParameterTransforms.Logarithmic);
            scale = parameter.Transform;
        }

        min = minimum ?? min;
        scale = transform ?? scale;
        if (read(patch) is double current && double.IsFinite(current))
        {
            min = Math.Min(min, current);
            max = Math.Max(max, current);
        }

        return new EditableValue(id, label, unit, min, max, logarithmic, snaps) { Reader = read, Writer = write, Transform = scale };
    }
}
