using System.Globalization;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Editing;

/// <summary>
/// The structural edits of a patch: adding and removing operators, connections, filters, sweeps, envelopes, pitch
/// modulation and roughness, and switching waveform and frequency mode. Each refuses, with a message for the user, what
/// the profile cannot play; added parts get fixed defaults. Operator indexes are zero-based (OP1 is 0).
/// </summary>
public static class PatchEdits
{
    /// <summary>Amplitude envelope of a new modulator: a short decay to a low sustain.</summary>
    private static readonly OperatorEnvelope ModulatorEnvelope = OperatorEnvelope.Adsr(0.005, 0.3, 0.2, 0.2);

    /// <summary>Envelope of a new filter sweep: open at the attack, closed to the base frequency after the decay.</summary>
    private static readonly OperatorEnvelope SweepEnvelope = OperatorEnvelope.Adsr(0.005, 0.3, 0.0, 0.2);

    /// <summary>Envelope of a connection that gets one: a decay to half the amount.</summary>
    private static readonly OperatorEnvelope ConnectionEnvelope = OperatorEnvelope.Adsr(0.005, 0.3, 0.5, 0.2);

    /// <summary>Octaves a new filter sweep moves the frequency.</summary>
    private const double SweepOctaves = 2.0;

    /// <summary>Phase roughness an operator gets when it is roughened.</summary>
    private const double RoughnessPhase = 0.3;

    /// <summary>Elliptic-parameter roughness an elliptic operator gets as well.</summary>
    private const double RoughnessM = 0.1;

    /// <summary>The waveforms a profile has, in a fixed order with sine first.</summary>
    /// <param name="c">Capabilities of the profile.</param>
    public static IReadOnlyList<OperatorWaveform> WaveformsFor(SynthesisCapabilities c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return [.. c.SupportedWaveforms.Order()];
    }

    /// <summary>True when the profile has a filter per operator.</summary>
    /// <param name="c">Capabilities of the profile.</param>
    public static bool HasFilters(SynthesisCapabilities c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.SupportsOperatorFilters;
    }

    /// <summary>True when the profile has a shared noise source, and so roughness.</summary>
    /// <param name="c">Capabilities of the profile.</param>
    public static bool HasSharedNoise(SynthesisCapabilities c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.SupportsSharedNoise;
    }

    /// <summary>
    /// Adds a modulator to an operator: the first disabled slot becomes a sine (or the profile's first waveform) at
    /// ratio 1 with a short decaying envelope, not mixed into the output, modulating the target by 1 rad. Refused when no
    /// slot is free.
    /// </summary>
    /// <param name="target">The operator to modulate.</param>
    public static PatchEdit AddModulator(int target) => (patch, c) =>
    {
        if (MissingOrUnused(patch, target) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        int slot = -1;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!patch.Operators[i].Enabled)
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            return EditOutcome.Refused(patch, $"All {patch.OperatorCount} operators are in use; there is no free slot for a new modulator. Remove an operator first.");
        }

        if (RoutingRefusal(c, slot, target) is { } routing)
        {
            return EditOutcome.Refused(patch, routing);
        }

        IReadOnlyList<OperatorWaveform> waveforms = WaveformsFor(c);
        OperatorWaveform waveform = c.SupportedWaveforms.Contains(OperatorWaveform.Sine) ? OperatorWaveform.Sine : waveforms[0];
        OperatorFrequency frequency = c.SupportsRatioFrequency ? OperatorFrequency.AtRatio(1.0) : OperatorFrequency.AtFixed(440.0);
        OperatorPatch modulator = (OperatorPatch.Disabled with { Enabled = true, Frequency = frequency, AmplitudeEnvelope = ModulatorEnvelope })
            .WithWaveform(waveform);

        // A fresh operator in the slot, without whatever connections the unused slot still had.
        OperatorMatrixPatch edited = WithoutConnectionsOf(patch, slot)
            .WithOperator(slot, modulator)
            .WithOutputLevel(slot, 0.0)
            .WithEdge(new ModulationEdge(slot, target, true, 1.0));
        return LimitRefusal(edited, target, c) is { } limit ? EditOutcome.Refused(patch, limit) : EditOutcome.Done(edited);
    };

    /// <summary>
    /// Removes an operator: disables it, takes it out of the output and removes its own connections (the operators it
    /// leaves without a destination stay). Refused for the last operator heard at the output.
    /// </summary>
    /// <param name="op">The operator.</param>
    public static PatchEdit RemoveOperator(int op) => (patch, _) =>
    {
        if (MissingOrUnused(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (IsHeardCarrier(patch, op) && !Enumerable.Range(0, patch.OperatorCount).Any(i => i != op && IsHeardCarrier(patch, i)))
        {
            return EditOutcome.Refused(patch, $"OP{op + 1} is the last operator heard at the output; removing it would leave nothing to hear.");
        }

        OperatorMatrixPatch edited = WithoutConnectionsOf(patch, op)
            .WithOperator(op, patch.Operators[op] with { Enabled = false })
            .WithOutputLevel(op, 0.0);
        return EditOutcome.Done(edited);
    };

    /// <summary>
    /// Connects two operators: the source modulates the destination by an amount in radians; source and destination
    /// may be the same operator (feedback). Refused when they are already connected or when the destination would
    /// receive more modulation than the profile allows.
    /// </summary>
    /// <param name="source">The modulating operator.</param>
    /// <param name="destination">The modulated operator.</param>
    /// <param name="amount">Phase deviation in radians.</param>
    public static PatchEdit Connect(int source, int destination, double amount = 1.0) => (patch, c) =>
    {
        if ((MissingOrUnused(patch, source) ?? MissingOrUnused(patch, destination)) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (!double.IsFinite(amount))
        {
            return EditOutcome.Refused(patch, "The amount of a connection must be a finite number of radians.");
        }

        if (patch.ModulationMatrix.Find(source, destination) is { Enabled: true })
        {
            return EditOutcome.Refused(patch, $"{Route(source, destination)} is already connected.");
        }

        if (RoutingRefusal(c, source, destination) is { } routing)
        {
            return EditOutcome.Refused(patch, routing);
        }

        OperatorMatrixPatch edited = patch.WithEdge(new ModulationEdge(source, destination, true, amount));
        if (!c.SupportsCircularFeedback && source != destination && HasLoop(edited))
        {
            return EditOutcome.Refused(patch, $"{Route(source, destination)} would close a feedback loop through several operators, which this profile cannot play.");
        }

        return LimitRefusal(edited, destination, c) is { } limit ? EditOutcome.Refused(patch, limit) : EditOutcome.Done(edited);
    };

    /// <summary>Removes the connection from one operator to another. Refused when there is none.</summary>
    /// <param name="source">The modulating operator.</param>
    /// <param name="destination">The modulated operator.</param>
    public static PatchEdit Disconnect(int source, int destination) => (patch, _) =>
    {
        if ((Missing(patch, source) ?? Missing(patch, destination)) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        return patch.ModulationMatrix.Find(source, destination) is null
            ? EditOutcome.Refused(patch, $"{Route(source, destination)} is not connected.")
            : EditOutcome.Done(patch with { ModulationMatrix = patch.ModulationMatrix.Without(source, destination) });
    };

    /// <summary>
    /// Gives an operator another waveform, with a shape that is valid for it. Refused for a waveform the profile does not
    /// have.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="waveform">The new waveform.</param>
    public static PatchEdit SetWaveform(int op, OperatorWaveform waveform) => (patch, c) =>
    {
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (!c.SupportedWaveforms.Contains(waveform))
        {
            string available = string.Join(", ", WaveformsFor(c).Select(PatchDescription.WaveformName));
            return EditOutcome.Refused(patch, $"This profile has no {PatchDescription.WaveformName(waveform)} waveform; it has {available}.");
        }

        OperatorPatch changed = patch.Operators[op].WithWaveform(waveform);

        // Roughness of the elliptic parameter means nothing to another waveform.
        if (waveform != OperatorWaveform.Elliptic)
        {
            changed = changed with { NoiseM = 0.0 };
        }

        return EditOutcome.Done(patch.WithOperator(op, changed));
    };

    /// <summary>
    /// Switches an operator between following the note (a ratio) and a fixed frequency, keeping its pitch on A4
    /// (440 Hz): a ratio r with a fine tune of c cents becomes 440 r 2^(c/1200) Hz and f Hz becomes the ratio
    /// f / 440 / 2^(c/1200); the fine tune is kept, unheard while the frequency is fixed. Refused for a mode the profile
    /// does not have, and for a fixed frequency of 0 Hz, which is no multiple of the note.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="mode">The new frequency mode.</param>
    public static PatchEdit SetFrequencyMode(int op, OperatorFrequencyMode mode) => (patch, c) =>
    {
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        OperatorFrequency frequency = patch.Operators[op].Frequency;
        if (frequency.Mode == mode)
        {
            return EditOutcome.Done(patch);
        }

        if ((mode == OperatorFrequencyMode.Ratio && !c.SupportsRatioFrequency) || (mode == OperatorFrequencyMode.Fixed && !c.SupportsFixedFrequency))
        {
            return EditOutcome.Refused(patch, mode == OperatorFrequencyMode.Ratio
                ? "This profile has no operators that follow the note."
                : "This profile has no operators at a fixed frequency.");
        }

        if (mode == OperatorFrequencyMode.Ratio && !(frequency.FixedFrequencyHz > 0))
        {
            return EditOutcome.Refused(patch, $"OP{op + 1} runs at 0 Hz, which is no multiple of the note; give it a frequency first.");
        }

        // The fine tune stays on the operator: a fixed operator is heard without it, and a ratio takes it back, so that
        // switching there and back gives the same ratio again.
        double fine = Math.Pow(2.0, patch.Operators[op].FineTuneCents / 1200.0);
        OperatorFrequency changed = mode == OperatorFrequencyMode.Fixed
            ? OperatorFrequency.AtFixed(440.0 * frequency.Ratio * fine)
            : OperatorFrequency.AtRatio(frequency.FixedFrequencyHz / 440.0 / fine);
        return EditOutcome.Done(patch.WithOperator(op, patch.Operators[op] with { Frequency = changed }));
    };

    /// <summary>
    /// Adds an operator filter, changes its type or removes it (a null type). A new filter is a low-pass at 4 times the
    /// note; a change of type keeps the frequency where both types have it (the multiple of the note of low-pass,
    /// high-pass, band-pass and notch, the hertz of peak and formant) and the sweep. Refused on a profile without filters.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="type">The filter type, or null for no filter.</param>
    public static PatchEdit SetFilter(int op, OperatorFilterType? type) => (patch, c) =>
    {
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        OperatorFilter? old = patch.Operators[op].Filter;
        if (type is not { } newType)
        {
            return EditOutcome.Done(old is null ? patch : patch.WithOperator(op, patch.Operators[op] with { Filter = null }));
        }

        if (NoFilters(c) is { } noFilters)
        {
            return EditOutcome.Refused(patch, noFilters);
        }

        if (old?.Type == newType)
        {
            return EditOutcome.Done(patch);
        }

        OperatorFilter filter = NewFilter(newType, old);
        if (old is not null)
        {
            filter = filter with { EnvelopeOctaves = old.EnvelopeOctaves, Envelope = old.Envelope };
        }

        return EditOutcome.Done(patch.WithOperator(op, patch.Operators[op] with { Filter = filter }));
    };

    /// <summary>
    /// Sets an operator's filter to a vowel preset (<see cref="FormantPresets"/>): all five formants, with their levels
    /// and own Qs. The sweep of the filter it replaces stays; an operator without a filter, or with another type, gets a
    /// formant filter. Refused on a profile without filters, and for a speaker or vowel the set does not have.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="set">One of <see cref="FormantPresets.Sets"/>.</param>
    /// <param name="speaker">One of the set's speakers, such as "soprano" or "women".</param>
    /// <param name="vowelKey">The key of one of the set's vowels, such as "a" or "ie".</param>
    public static PatchEdit SetFormantPreset(int op, FormantPresetSet set, string speaker, string vowelKey) => (patch, c) =>
    {
        ArgumentNullException.ThrowIfNull(set);
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (NoFilters(c) is { } noFilters)
        {
            return EditOutcome.Refused(patch, noFilters);
        }

        if (FormantPresets.Problem(set, speaker, vowelKey) is { } problem)
        {
            return EditOutcome.Refused(patch, problem);
        }

        OperatorFilter filter = FormantPresets.Filter(set, speaker, vowelKey, patch.Operators[op].Filter);
        return EditOutcome.Done(patch.WithOperator(op, patch.Operators[op] with { Filter = filter }));
    };

    /// <summary>Duty cycle of a voice source: a pulse this narrow has many harmonics of nearly the same strength.</summary>
    public const double VoiceSourceWidth = 0.01;

    /// <summary>Harmonics of a voice source: enough for the formants of a vowel up to F5 on a low note.</summary>
    public const int VoiceSourceHarmonics = 64;

    /// <summary>
    /// Makes an operator a vowel source: a narrow pulse (<see cref="VoiceSourceWidth"/>, 1 %) with
    /// <see cref="VoiceSourceHarmonics"/> harmonics, as one step; its filter and everything else stay. A formant filter
    /// shapes the harmonics it is given, so a vowel needs many, equally strong ones. Refused on a profile without a pulse
    /// waveform.
    /// </summary>
    /// <param name="op">The operator.</param>
    public static PatchEdit SetVoiceSource(int op) => (patch, c) =>
    {
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (!c.SupportedWaveforms.Contains(OperatorWaveform.Pulse))
        {
            string available = string.Join(", ", WaveformsFor(c).Select(PatchDescription.WaveformName));
            return EditOutcome.Refused(patch, $"This profile has no pulse waveform for a voice source; it has {available}.");
        }

        OperatorPatch source = patch.Operators[op].WithWaveform(OperatorWaveform.Pulse) with
        {
            Shape = VoiceSourceWidth,
            HarmonicLimit = VoiceSourceHarmonics,
            NoiseM = 0.0,
        };
        return EditOutcome.Done(patch.WithOperator(op, source));
    };

    /// <summary>
    /// Adds or removes the sweep of an operator's filter: an envelope that moves its frequency, 2 octaves at the peak.
    /// Refused when the operator has no filter.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="on">True to add the sweep, false to remove it.</param>
    public static PatchEdit SetSweep(int op, bool on) => (patch, _) =>
    {
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (patch.Operators[op].Filter is not { } filter)
        {
            return on ? EditOutcome.Refused(patch, $"OP{op + 1} has no filter to sweep; add a filter first.") : EditOutcome.Done(patch);
        }

        if ((filter.Envelope is not null) == on)
        {
            return EditOutcome.Done(patch);
        }

        OperatorFilter changed = on
            ? filter with { EnvelopeOctaves = SweepOctaves, Envelope = SweepEnvelope }
            : filter with { EnvelopeOctaves = 0.0, Envelope = null };
        return EditOutcome.Done(patch.WithOperator(op, patch.Operators[op] with { Filter = changed }));
    };

    /// <summary>
    /// Adds or removes the envelope that moves the amount of a connection. Refused when the operators are not connected,
    /// on a profile with a static matrix, and when removing it would bring the destination over the modulation limit.
    /// </summary>
    /// <param name="source">The modulating operator.</param>
    /// <param name="destination">The modulated operator.</param>
    /// <param name="on">True to add the envelope, false to remove it.</param>
    public static PatchEdit SetConnectionEnvelope(int source, int destination, bool on) => (patch, c) =>
    {
        if ((Missing(patch, source) ?? Missing(patch, destination)) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        if (patch.ModulationMatrix.Find(source, destination) is not { } edge)
        {
            return EditOutcome.Refused(patch, $"{Route(source, destination)} is not connected.");
        }

        if ((edge.AmountEnvelope is not null) == on)
        {
            return EditOutcome.Done(patch);
        }

        if (on && !c.SupportsDynamicModulationMatrix)
        {
            return EditOutcome.Refused(patch, "This profile has a static modulation matrix; a connection cannot follow an envelope.");
        }

        OperatorMatrixPatch edited = patch.WithEdge(edge with { AmountEnvelope = on ? ConnectionEnvelope : null });
        return LimitRefusal(edited, destination, c) is { } limit ? EditOutcome.Refused(patch, limit) : EditOutcome.Done(edited);
    };

    /// <summary>Adds or removes the pitch envelope: a new one is flat, 0 to 0 cents over 0.1 s, and runs once.</summary>
    /// <param name="on">True to add the pitch envelope, false to remove it.</param>
    public static PatchEdit SetPitchEnvelope(bool on) => (patch, _) =>
    {
        if ((patch.Pitch.PitchEnvelope is not null) == on)
        {
            return EditOutcome.Done(patch);
        }

        OperatorEnvelope? envelope = on
            ? new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.1, 0)], SustainPoint: null, ReleasePoint: 2)
            : null;
        return EditOutcome.Done(patch with { Pitch = patch.Pitch with { PitchEnvelope = envelope } });
    };

    /// <summary>Adds or removes the vibrato: a new one is 5 Hz, 10 cents deep, from the start of the note.</summary>
    /// <param name="on">True to add the vibrato, false to remove it.</param>
    public static PatchEdit SetVibrato(bool on) => (patch, _) =>
    {
        if ((patch.Pitch.Vibrato is not null) == on)
        {
            return EditOutcome.Done(patch);
        }

        return EditOutcome.Done(patch with { Pitch = patch.Pitch with { Vibrato = on ? new LfoSettings(5, 10) : null } });
    };

    /// <summary>
    /// Roughens an operator by the shared noise, or makes it smooth again: 0.3 rad of phase noise, and for an elliptic
    /// operator 0.1 of noise on its elliptic parameter as well. Refused on a profile without shared noise and on an
    /// operator that plays the noise itself.
    /// </summary>
    /// <param name="op">The operator.</param>
    /// <param name="on">True to roughen it, false to make it smooth.</param>
    public static PatchEdit SetRoughness(int op, bool on) => (patch, c) =>
    {
        if (Missing(patch, op) is { } missing)
        {
            return EditOutcome.Refused(patch, missing);
        }

        OperatorPatch operatorPatch = patch.Operators[op];
        if (!on)
        {
            return EditOutcome.Done(operatorPatch is { NoisePhase: 0.0, NoiseM: 0.0 }
                ? patch
                : patch.WithOperator(op, operatorPatch with { NoisePhase = 0.0, NoiseM = 0.0 }));
        }

        if (!c.SupportsSharedNoise)
        {
            return EditOutcome.Refused(patch, "This profile has no shared noise to roughen an operator with.");
        }

        if (operatorPatch.Waveform.IsNoise())
        {
            return EditOutcome.Refused(patch, $"OP{op + 1} plays noise; roughness applies to the other waveforms.");
        }

        if (operatorPatch.NoisePhase != 0.0 || operatorPatch.NoiseM != 0.0)
        {
            return EditOutcome.Done(patch);
        }

        double noiseM = operatorPatch.Waveform == OperatorWaveform.Elliptic ? RoughnessM : 0.0;
        return EditOutcome.Done(patch.WithOperator(op, operatorPatch with { NoisePhase = RoughnessPhase, NoiseM = noiseM }));
    };

    private static OperatorFilter NewFilter(OperatorFilterType type, OperatorFilter? old)
    {
        bool keyTracked = type is OperatorFilterType.LowPass or OperatorFilterType.HighPass or OperatorFilterType.BandPass or OperatorFilterType.Notch;
        if (keyTracked)
        {
            OperatorFilter start = OperatorFilter.LowPass(4) with { Type = type };
            return old is { IsKeyTracked: true } ? start with { CutoffRatio = old.CutoffRatio } : start;
        }

        OperatorFilter fixedHz = type == OperatorFilterType.Peak
            ? OperatorFilter.PeakAt(1000, 6, 2)
            : OperatorFilter.FormantAt(700, 1.8, 0.5, 8);
        return old is { IsKeyTracked: false } ? fixedHz with { FixedHz = old.FixedHz } : fixedHz;
    }

    /// <summary>An enabled operator that is mixed into the output.</summary>
    private static bool IsHeardCarrier(OperatorMatrixPatch patch, int op)
        => patch.Operators[op].Enabled && patch.OutputLevels[op] > 0.0;

    private static OperatorMatrixPatch WithoutConnectionsOf(OperatorMatrixPatch patch, int op)
    {
        var edges = patch.ModulationMatrix.Edges.Where(e => e.SourceOperator != op && e.DestinationOperator != op).ToList();
        return edges.Count == patch.ModulationMatrix.Edges.Count
            ? patch
            : patch with { ModulationMatrix = new PhaseModulationMatrix(patch.OperatorCount, edges) };
    }

    /// <summary>Why the profile cannot give an operator a filter; null when it can.</summary>
    private static string? NoFilters(SynthesisCapabilities c)
        => HasFilters(c) ? null : "This profile has no operator filters.";

    private static string? Missing(OperatorMatrixPatch patch, int op)
        => (uint)op < (uint)patch.OperatorCount ? null : $"There is no OP{op + 1}; the patch has {patch.OperatorCount} operators.";

    private static string? MissingOrUnused(OperatorMatrixPatch patch, int op)
        => Missing(patch, op) ?? (patch.Operators[op].Enabled ? null : $"OP{op + 1} is not in use.");

    /// <summary>A connection in words, such as "OP2 -> OP1".</summary>
    internal static string Route(int source, int destination) => $"OP{source + 1} -> OP{destination + 1}";

    /// <summary>Why the profile cannot have a connection from one operator to another; null when it can.</summary>
    private static string? RoutingRefusal(SynthesisCapabilities c, int source, int destination)
    {
        if (source == destination)
        {
            return c.SupportsSelfFeedback ? null : $"OP{source + 1} cannot modulate itself: this profile has no feedback.";
        }

        return c.SupportsFullModulationMatrix || source > destination
            ? null
            : $"{Route(source, destination)} is not possible: in this profile an operator only modulates operators before it.";
    }

    /// <summary>
    /// Why the modulation that arrives at an operator is too much for the profile; null when it is within the limit. Every
    /// enabled connection from an enabled operator counts at its peak, heard or not, so that the limit still holds when an
    /// edit later makes it heard.
    /// </summary>
    private static string? LimitRefusal(OperatorMatrixPatch patch, int destination, SynthesisCapabilities c)
    {
        double incoming = IncomingModulation(patch, destination);
        double limit = c.MaxModulationRadiansPerDestination;
        return incoming > limit
            ? string.Create(CultureInfo.InvariantCulture, $"OP{destination + 1} would receive {incoming:0.0} rad of modulation; this profile allows at most {limit:0.0} rad.")
            : null;
    }

    /// <summary>
    /// The modulation that arrives at an operator, in radians at its peak, as the modulation limit counts it: every
    /// enabled connection from an enabled operator, heard or not, at its amount times the peak of its envelope.
    /// </summary>
    /// <param name="patch">The patch.</param>
    /// <param name="destination">The modulated operator.</param>
    /// <param name="exceptSource">A source whose connection is left out of the sum; null for none.</param>
    internal static double IncomingModulation(OperatorMatrixPatch patch, int destination, int? exceptSource = null)
    {
        double incoming = 0;
        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (edge.DestinationOperator != destination || edge.SourceOperator == exceptSource || !edge.Enabled || !patch.Operators[edge.SourceOperator].Enabled)
            {
                continue;
            }

            incoming += Math.Abs(edge.Amount) * (edge.AmountEnvelope is { } envelope ? EnvelopePeak(envelope) : 1.0);
        }

        return incoming;
    }

    /// <summary>The largest level of an envelope, in magnitude, as rendered (with the exponential floor).</summary>
    /// <param name="envelope">The envelope.</param>
    internal static double EnvelopePeak(OperatorEnvelope envelope)
    {
        double max = 0;
        for (int p = 0; p < envelope.Points.Count; p++)
        {
            max = Math.Max(max, Math.Abs(envelope.EffectiveLevel(p)));
        }

        return max;
    }

    /// <summary>True when the enabled connections between enabled operators, feedback aside, contain a loop.</summary>
    private static bool HasLoop(OperatorMatrixPatch patch)
    {
        int n = patch.OperatorCount;
        var next = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            next[i] = [];
        }

        foreach (ModulationEdge edge in patch.ModulationMatrix.Edges)
        {
            if (edge.Enabled && !edge.IsSelfFeedback && patch.Operators[edge.SourceOperator].Enabled && patch.Operators[edge.DestinationOperator].Enabled)
            {
                next[edge.SourceOperator].Add(edge.DestinationOperator);
            }
        }

        // 0 = not visited, 1 = on the current path, 2 = done.
        var state = new int[n];
        for (int start = 0; start < n; start++)
        {
            if (state[start] == 0 && Visit(start))
            {
                return true;
            }
        }

        return false;

        bool Visit(int node)
        {
            state[node] = 1;
            foreach (int to in next[node])
            {
                if (state[to] == 1 || (state[to] == 0 && Visit(to)))
                {
                    return true;
                }
            }

            state[node] = 2;
            return false;
        }
    }
}
