# Patch matching: from a target sound to an operator-matrix patch

HearSay can take a monophonic target note and search a playable synthesizer patch that approximates it.

```text
target.wav
    ↓  audio analysis                      HearSay.Audio
    ↓  structural hypotheses               HearSay.OperatorMatrix (prior providers)
    ↓  operator / routing / ratio search   HearSay.OperatorMatrix (basins, topology mutation)
    ↓  software synthesis                  HearSay.Synthesis (+ .Fm7Compatible)
    ↓  candidate comparison                HearSay.Audio (objective)
    ↓  global multi-basin optimization     HearSay.OperatorMatrix (CMA-ES with restarts)
    ↓  local refinement                    HearSay.Modeling (regime-specific Jacobians, trust region)
best OperatorMatrixPatch
    ├─ patch.json      canonical patch
    ├─ preview.wav     offline render
    ├─ patch.scd       SuperCollider SynthDef around FM7
    └─ diagnostics     analysis.json, comparison.json, search-summary.json, alternatives/
```

HearSay is an *audio-to-operator-matrix solver*. The SuperCollider `FM7` UGen is its first synthesis and export
profile, not its identity: what FM7 can play is known only to its profile, its renderer and its exporter (FMX6 and
Nome6 have theirs), and the sideband arithmetic of sine FM only to the sine phase-modulation prior provider and the
`FmSpectrumModel` it uses.

This document is the design and the evidence. For day-to-day use (preparing targets, budget and seeds, reading
results, the CLI, the API, hand-editing, troubleshooting) see the [expert manual](expert-manual.md); for the desktop
app, its keyboard, block diagram, editor and library, see the [Studio manual](studio-manual.md).

- [Quick start](#quick-start)
- [Assemblies and what was reused](#assemblies-and-what-was-reused)
- [The canonical patch model](#the-canonical-patch-model)
- [Synthesis profiles](#synthesis-profiles)
- [The FM7-compatible renderer](#the-fm7-compatible-renderer)
- [SuperCollider export](#supercollider-export)
- [Target analysis](#target-analysis)
- [Objective versus diagnosis](#objective-versus-diagnosis)
- [Parameters: structural and continuous](#parameters-structural-and-continuous)
- [The search](#the-search)
- [Target sets: several notes, one patch](#target-sets-several-notes-one-patch)
- [Results](#results)
- [Performance](#performance)
- [Limitations of this version](#limitations-of-this-version)
- [How a custom UGen plugs in: FMX6](#how-a-custom-ugen-plugs-in-fmx6)
- [A second custom UGen: Nome6](#a-second-custom-ugen-nome6)

## Quick start

Desktop app (Avalonia, Windows/macOS/Linux): `dotnet run -c Release --project src/HearSay.Studio`. Load WAV files, set budget
and seeds, run, and play the result on the screen keyboard or a MIDI controller at any pitch (OpenAL Soft, up to 16
voices, velocity from where the key is hit or from the controller). Copy or save the SuperCollider code, look at the
patch as a block diagram and edit it there, keep it as a favourite, or save the result folder. The Target selector
picks FM7, FMX6, Nome6 or All (the three one after the other); the last result of each target is kept, so switching
the selector compares them. The [Studio manual](studio-manual.md) describes the app. Command line:

```bash
dotnet run -c Release --project src/HearSay.Cli -- match target.wav \
    --profile supercollider-fm7 --budget 20000 --output result/

dotnet run -c Release --project src/HearSay.Cli -- analyze target.wav   # target analysis as JSON
dotnet run -c Release --project src/HearSay.Cli -- profiles             # list synthesis profiles
```

`--note <midi>` or `--f0 <hz>` supply the pitch when it is known; otherwise it is estimated. `--seeds <n>` runs n seeds
and keeps the best, which is the simplest remedy against an unlucky run; the finalists of every seed are kept as
alternatives, one per structure, each marked with its seed. `--library <n>` sets the size of the
[structure library](#structure-library) (300 000 by default, 0 turns it off). `--sample-rate` should be
the rate the exported patch will run at, because the aliasing of the backend is part of its sound. `hearsay reference
[name]` renders a built-in reference patch to WAV files to use as test targets; `hearsay` with no arguments lists every
option (`--seed`, `--max-seconds`, `--parallel`, `--quiet` and the rest).

```text
result/
    patch.json            canonical OperatorMatrixPatch
    preview.wav           offline render of the best patch
    patch.scd             SuperCollider SynthDef (needs sc3-plugins for FM7, nothing of HearSay)
    analysis.json         what was measured on the target
    comparison.json       full HearSay diagnosis of preview against target
    search-summary.json   stages, basins, budget, seed, the course of the search (improvements, trace), candidates
    alternatives/
        01-patch.json  01-preview.wav  01-patch.scd
        02-…
```

A target set also gets a preview, an analysis and a comparison per note (`preview-note2-262Hz.wav`, …).

API:

```csharp
var matcher = new OperatorMatrixPatchMatcher();

PatchMatchResult result = await matcher.MatchAsync(
    AudioTarget.FromWaveFile("target.wav"),
    new PatchMatchOptions
    {
        EvaluationBudget = 20000,
        RandomSeed = 1234,
        SynthesisProfile = SynthesisProfiles.SuperColliderFm7,
    });

OperatorMatrixPatch patch = result.BestPatch;           // canonical patch
ToneComparisonResult diagnosis = result.Comparison;      // how the preview still differs from the target
RenderedAudio preview = result.PreviewAudio;
PatchMatchResultWriter.Write(result, "result");          // the folder shown above
```

The same target, options, seed and profile give the same search and the same patch. `MaxParallelEvaluations` changes
how fast that happens, never what is found.

## Assemblies and what was reused

```text
src/
  HearSay.Audio/                 how does candidate audio differ from target audio?
      Audio, Dsp, Preprocessing, Spectral, Harmonics, Envelope, Features, Comparison, Models   (existing)
      Partials/                  inharmonic partial detection, tracking, partial-set distance   (new)
      Pitch/                     pitch trajectory, vibrato, drift, beat detection               (new)
      Spectral/                  + spectral trajectories, band-energy trajectories, LTAS, noise profile   (new)
      Envelope/                  + multi-stage decay                                            (new)
      Analysis/                  AudioTarget, TargetAnalysis, TargetAnalyzer                    (new)
      Objective/                 composite audio loss with evaluation levels                    (new)
  HearSay.Modeling/              what effects have parameter changes had on audio features?
      History, Effects, Sensitivity, Prediction, Guidance, Parameters, ToneOptimizationSession  (existing, moved)
  HearSay.Synthesis/             how is a canonical patch rendered?
      Patches/                   OperatorMatrixPatch, OperatorPatch, PhaseModulationMatrix, envelopes, pitch model
      Profiles/                  SynthesisCapabilities, SynthesisProfile(s), PatchCompatibilityValidator
      Rendering/                 IOperatorMatrixRenderer, RenderRequest, voices (IVoiceRenderer, ControlBlockVoice), ExtendedOperatorMatrixRenderer
      Compilation/ Envelopes/ Filters/ Oscillators/ Export/
  HearSay.Synthesis.Fm7Compatible/   reference renderer with FM7 semantics
  HearSay.Synthesis.Fmx6Compatible/  reference renderer with FMX6 semantics (band-limited waveforms, noise, operator filters)
  HearSay.Synthesis.Nome6Compatible/ reference renderer with Nome6 semantics (Jacobi elliptic operators, shared noise)
  HearSay.SuperCollider/         FM7, FMX6 and Nome6 exporters, SuperCollider NRT renderer, backend-equivalence validation
  HearSay.OperatorMatrix/        which synthesis structure and parameters should be explored?
      Parameters/                schema, hierarchy, conditional activity, transforms, ties, active sets
      Canonicalization/          regime keys, canonical operator order, complexity
      Priors/                    ISynthesisPriorProvider, sine-PM provider and FM spectrum model, partial-track,
                                 waveform, noise, formant and subtractive providers, temporal priors, structure library
      Optimization/              CMA-ES
      Basins/ Search/            basins, topology mutation, evaluator, coordinator, local refiner
  HearSay/                       facade: OperatorMatrixPatchMatcher, SynthesisBackends, result writer, favourites
      Playing/                   PlaybackLevel, Sound, VoiceMixer, NoteSession, MIDI
      Editing/                   PatchEditor, EditSessions, PatchEdits, EditableValues, EnvelopeEdits, EditMarks, FormantPresets
  HearSay.Cli/                   command line
  HearSay.Studio/                desktop app (Avalonia)
```

The original `HearSay` assembly was split into `HearSay.Audio` and `HearSay.Modeling`. Namespaces on the audio side
are unchanged; `HearSay.Optimization.*` became `HearSay.Modeling.*`. Everything that existed keeps working and is used
by the matcher:

| Existing component | Role in patch matching |
| --- | --- |
| `AudioPreprocessor` | DC removal, trimming, onset, clipping and invalid-sample checks, resampling, loudness matching for target and every candidate |
| `StftAnalyzer`, `MelFilterbank`, `FftPlan` | spectrograms of the objective and of all new trajectory and partial analyses |
| `EnvelopeAnalyzer`, `FundamentalEstimator` | envelope stages and the fundamental of the target |
| `ToneComparer`, feature vector and schema | the diagnosis in `comparison.json`, and the feature vector of local refinement |
| `LocalLinearEffectModel`, `ParameterFeatureJacobian`, `OptimizationGuidance`, `OptimizationHistory` | regime-specific local refinement of the finalists |
| `HearSayJson` | `patch.json` and all reports |

## The canonical patch model

`OperatorMatrixPatch` is the single source of truth. The optimizer, the reference renderers, the editor, the JSON
format and every exporter read the same record; there is no second representation.

```csharp
public sealed record OperatorMatrixPatch(
    IReadOnlyList<OperatorPatch> Operators,
    PhaseModulationMatrix ModulationMatrix,
    IReadOnlyList<double> OutputLevels,
    PitchModel Pitch);

public sealed record OperatorPatch(
    bool Enabled,
    OperatorMode Mode,                  // PhaseModulation; ring modulation, wavefolder, filter… are reserved
    OperatorWaveform Waveform,          // Sine, Triangle, Saw, Square, Pulse, Noise, SampleHoldNoise, FilteredNoise, Elliptic, Additive, Wavetable
    OperatorFrequency Frequency,        // Ratio or Fixed
    double FineTuneCents,
    double DetuneHz,
    double InitialPhase,
    OperatorEnvelope AmplitudeEnvelope,
    double VelocitySensitivity,
    KeyScaling KeyScaling);
```

Besides these, an operator has init properties that only some backends use: `Shape` (pulse duty cycle, filtered-noise
response or elliptic m), `Morph` (elliptic sn-to-sd), `HarmonicLimit` (1..128 harmonics of a band-limited waveform),
`NoisePhase` and `NoiseM` (roughening by the shared noise), and `Filter`, an optional
[operator filter](#operator-filters). The patch has `Noise`, the colour and cutoff of the noise all operators share.
`Filter` is left out of the JSON when an operator has none, so a patch without filters keeps the serialized form and
the hash it had before filters existed.

- **Frequency.** Ratio operators run at `note × ratio × 2^(fineTune/1200) + detuneHz`, multiplied by the pitch model.
  Fixed operators run at `fixedHz + detuneHz` and ignore both the note and the pitch model. Fine tune transposes;
  detune is additive, so a detuned pair beats at the same rate on every key.
- **Matrix.** `ModulationMatrix[destination, source]` is the phase deviation in radians per unit of source output. It
  is stored as a sparse list of `ModulationEdge(Source, Destination, Enabled, Amount, AmountEnvelope?)` and
  materialized densely for rendering. Whether an edge exists is structural, its amount is continuous. Feed-forward
  stacks, branches, parallel stacks, self-feedback (`M[i,i]`, no global feedback parameter) and loops through several
  operators are all ordinary entries.
- **Roles are inferred, not declared.** Every operator has its own output gain. An operator can modulate others,
  receive modulation and feed the mix at the same time. "Carrier" and "modulator" are descriptions of what the matrix
  and the output levels say.
- **Envelopes** are breakpoint lists with linear, exponential or step segments, a sustain point and a release section.
  The same type serves operator amplitudes, modulation-edge amounts (`M[i,j](t)`, control rate), filter sweeps and
  pitch. They are not limited to four rates and four levels.
- **Pitch model.** A pitch envelope in cents (initial offset, pitch decay, drift) and a sine vibrato with rate, depth,
  delay and fade-in.
- The amplitude envelope of an operator scales everything it sends, its modulation as well as its contribution to the
  mix. That is how FM7 works, and it is what makes a decaying modulator a decaying modulation index.
- **Operator filters.** An operator may have a `Filter` between its waveform and its amplitude: low-pass or high-pass
  with 2 or 4 poles, band-pass or notch, all following the note; a peak at a fixed frequency; or a formant filter with
  up to five peaks. Everything the operator sends, to the mix and as modulation, is the filtered signal. Only profiles
  with `SupportsOperatorFilters` (FMX6) may use it; see [Operator filters](#operator-filters) in the FMX6 section. The
  reserved `OperatorMode.Filter` above is something else: a filter as an operator of its own.

`patch.ToJson()`, `OperatorMatrixPatch.FromJson`, `patch.Validate()` and the deterministic content hash
`patch.ComputeHash()` (the cache key of the search) complete the model.

## Synthesis profiles

A profile says what may be searched and how it must be rendered. Backend restrictions never live in the patch model
or in the search; they are data:

```csharp
public sealed record SynthesisCapabilities(
    int OperatorCount,
    IReadOnlySet<OperatorMode> SupportedModes,
    IReadOnlySet<OperatorWaveform> SupportedWaveforms,
    bool SupportsFullModulationMatrix,
    bool SupportsSelfFeedback,
    bool SupportsCircularFeedback,
    bool SupportsDynamicModulationMatrix,
    bool SupportsRatioFrequency,
    bool SupportsFixedFrequency,
    bool SupportsIndependentOperatorOutputs,
    double MaxModulationRadiansPerDestination = 24.0,
    bool SupportsSharedNoise = false,
    bool SupportsOperatorFilters = false);
```

| Profile | Operators | Modes | Waveforms | Status |
| --- | --- | --- | --- | --- |
| `SynthesisProfiles.SuperColliderFm7` (`supercollider-fm7`) | exactly 6 | PhaseModulation | Sine | searchable, exportable; full matrix, self and circular feedback, ratio and fixed, independent outputs, dynamic matrix, backend-exact rendering |
| `SynthesisProfiles.SuperColliderFmx6` (`supercollider-fmx6`) | exactly 6 | PhaseModulation | Sine, Triangle, Saw, Square, Pulse, Noise, SampleHoldNoise, FilteredNoise | searchable, exportable; everything FM7 has, plus band-limited waveforms with a harmonic limit (1..128), a pulse duty cycle (`Shape`), three noise sources, one of them filtered (`Shape` then selects low-, band- or high-pass), and a filter per operator (`SupportsOperatorFilters`: low-pass, high-pass, band-pass, notch, peak, or formant with up to five formants); needs the [FMX6 plugin](https://github.com/shimpe/fmx6), with operator filters for patches that use them, and with five formants for a formant filter with more than F1 and F2 or with an own Q on one of them |
| `SynthesisProfiles.SuperColliderNome6` (`supercollider-nome6`) | exactly 6 | PhaseModulation | Sine, Elliptic, Noise | searchable, exportable; the FM7 matrix with Jacobi elliptic operators (`Shape` is the elliptic parameter m, `Morph` the sn-to-sd crossfade), rendered at twice the sample rate, and one shared noise source (`SupportsSharedNoise`): noise operators play it, and it roughens the phase (`NoisePhase`) or the m (`NoiseM`) of the others, with one colour per patch (`OperatorMatrixPatch.Noise`); needs the Nome6 plugin with noise |
| `SynthesisProfiles.ExperimentalSineTriangle` (`experimental-sine-triangle`) | 6 | PhaseModulation | Sine, Triangle | searchable with the extended renderer; no export backend, because that renderer is not backend-exact (FMX6 could play its patches, but not with the same anti-aliasing); exists to prove the architecture |
| `SynthesisProfiles.HearSayExtended` (`hearsay-extended`) | 6 | PhaseModulation | all | reserved for a future engine; no renderer yet, so the matcher refuses it |

Three mechanisms keep a search inside its profile:

1. Prior providers receive the capabilities and the coordinator drops every hypothesis the validator rejects.
2. `TopologyMutator` only proposes what the capabilities allow, and validates each proposal.
3. The parameter schema offers structural choices from the profile only. For FM7 the waveform parameter of an operator
   has exactly one choice; cells of the matrix the backend lacks do not exist in the schema.

Hand edits obey the same limits: every edit of `HearSay.Editing.PatchEdits` receives the capabilities of the sound's
profile and refuses, with a reason, what the backend cannot play, and `PatchEditor` validates every result, so an
edited patch is always playable by its profile. The ranges of the editable numbers (`EditableValues`) are those of the
parameter schema, widened to the value a patch already has.

`IPatchCompatibilityValidator` answers whether a backend can play a patch, with machine-readable issues
(`unsupported-waveform`, `self-feedback`, `operator-filter`, `shared-noise`, `modulation-range`, …). Renderers and
exporters refuse incompatible patches with a `PatchNotSupportedException`; nothing is approximated silently. A
renderer that can do less than a profile asks for is rejected before the search starts.

## The FM7-compatible renderer

`Fm7CompatibleOperatorMatrixRenderer` implements `IOperatorMatrixRenderer` offline, deterministically, in mono and
without an audio device. Its semantics were taken from the FM7 source code and then verified against the real UGen:

- **Previous-sample modulation.** In every sample, all phase deviations are computed first, from the operator outputs
  of the previous sample; only then are the six outputs updated:

  ```text
  operatorOutputs[n - 1] → modulation matrix → operator phases[n] → operatorOutputs[n]
  ```

  Every edge therefore has a delay of exactly one sample. That is what makes self-feedback and feedback loops through
  several operators well defined, and it is audibly different from evaluating a stack top-down within one sample,
  especially at high frequencies and large amounts. A unit test pins both: the render follows the one-sample
  recurrence, and the same-sample formula is measurably different.
- The output of an operator includes its amplitude, so an amplitude envelope shapes the modulation an operator sends.
- 32-bit fixed-point phase with truncated increments, an 8192-point linearly interpolated sine table, single-precision
  arithmetic in the order of the UGen.
- Frequency, phase, amplitude and all 36 matrix inputs are control-rate signals that FM7 ramps linearly across each
  control block of 64 samples. Envelopes are stepped with the stage logic of `EnvGen.kr`, including the carried
  rounding remainder of segment lengths and the jump into the release from the level reached so far.
- FM7 computes one sample while it is constructed, so the audible output starts one sample into the cycle.
- A gate-off takes effect in the control block that contains it (measured on scsynth).
- **No hidden oversampling.** `RenderProfile.BackendExact` reproduces the aliasing of the backend, because a patch that
  is optimized here must sound the same when `patch.scd` is played. `RenderProfile.ExperimentalHighQuality` is where
  oversampling and band-limited oscillators belong; `ExtendedOperatorMatrixRenderer` uses it for non-sine waveforms
  and keeps that detail to itself.

Patches are compiled before the sample loop (`PatchCompiler` → `CompiledOperatorMatrixPatch`): only audible
operators and live edges remain, all control signals are evaluated per block, and the sample loop itself is free of
allocations, LINQ, dictionaries and virtual calls; control values change once per block.

The kernel is also a voice (`IVoiceRenderer.StartVoice`, `ControlBlockVoice`) that renders one control block at a time
and reads its gate once per block, so a note can be held for as long as a key is down. Offline rendering compiles the
whole note by running the same `ControlStream` to its end, and a voice held for a duration and then released plays
exactly the samples of the offline render of that note (`VoiceEquivalenceTests`, for FM7, FMX6 and Nome6). The Studio
plays its keyboard and MIDI notes this way.

## SuperCollider export

Three exporters share one base, `SuperColliderOperatorMatrixExporter`: `SuperColliderFm7Exporter` for the `FM7` UGen
of the sc3-plugins, `SuperColliderFmx6Exporter` for the [FMX6 UGen](https://github.com/shimpe/fmx6) and
`SuperColliderNome6Exporter` for [Nome6](#a-second-custom-ugen-nome6). All are `IPatchExporter`s and
`ISynthDefExporter`s; the profile of a search decides which one `SynthesisBackends.ExporterFor` hands out. The
SynthDefs differ only in the statement that calls the UGen. FMX6 takes three extra arrays, one entry per operator:
`waves:` (0 sine, 1 triangle, 2 saw, 3 square, 4 pulse, 5 sample-and-hold noise, 6 white noise, 7 filtered noise),
`shapes:` (pulse duty cycle or filtered-noise response) and `harmonicLimits:`. When an audible operator has a filter
it takes a fourth, `filters:`, one row per operator: `[type, freq, octaves, q, formantRatio, formantLevel, gainDb]`
(type 1 LP2, 2 LP4, 3 HP2, 4 HP4, 5 band-pass, 6 notch, 7 peak, 8 formant), with ten more fields (the Q of F2, and
ratio, level and Q of F3 to F5) only when an audible formant filter needs them. A patch without filters therefore runs
on FMX6 builds without operator filters, and one whose formant filters have only F1 and F2 at one Q runs on builds
without five formants. FMX6 plays every FM7 patch; FM7 refuses a patch with any other waveform or with a filter.
Nome6 extends every operator row to the nine fields of its UGen and passes the colour of the shared noise.

`SuperColliderFm7Exporter` writes a self-contained SynthDef. It uses the full matrix interface
`FM7.ar(ctlMatrix, modMatrix)`, never `FM7.arAlgo`. What was verified on the installed UGen before writing it:

| Question | Answer |
| --- | --- |
| `ctlMatrix` | 6 rows of `[frequency, phase, amplitude]`, all read at control rate |
| `modMatrix` orientation | `modMatrix[destination][source]`, the orientation of `PhaseModulationMatrix` |
| scaling | radians per unit of source output (source output includes its amplitude) |
| result | an array of the six operator outputs; mixed with `OutputLevels` |
| control-rate behaviour | all inputs are ramped linearly over each control block |
| self-feedback, circular feedback | both work, with the one-sample delay described above |

A complete generated file (the two-operator round trip from the results below), as it was written before the export
carried HearSay's level; today's ends with the level line described after it:

```supercollider
// Generated by HearSay. Needs SuperCollider with sc3-plugins (FM7 UGen); HearSay itself is not needed.
// Matched to a target at 220.00 Hz; audio loss 0.0048, HearSay similarity 0.907.
// Profile supercollider-fm7, seed 1234, 19179 renders.
//
// Controls: freq (Hz), gate, velocity (0..1), amp, out.
// Ratio operators follow freq, fixed operators do not.
// modMatrix[destination][source] is in radians; the diagonal is self-feedback.
(
SynthDef(\HearSayPatch, { |out = 0, freq = 440, gate = 1, velocity = 1, amp = 0.2|
	var ctlMatrix, modMatrix, operators, signal;

	// One row per operator: [frequency, phase, amplitude].
	ctlMatrix = [
		[(freq * 1.0), 0.0, EnvGen.kr(Env([0.00001, 1.0, 0.3450432, 0.12217694, 0.00001], [0.0017415001, 0.30092677, 0.89833397, 0.19673148], [\lin, \exp, \exp, \exp], 3), gate)], // OP1
		[(freq * 2.0), 0.0, EnvGen.kr(Env([0.00001, 1.0, 0.22347899, 0.0342056, 0.00001], [0.0017885608, 0.14979427, 1.0494152, 0.038904097], [\lin, \exp, \exp, \exp], 3), gate)], // OP2
		[0, 0, 0], // OP3 (off)
		[0, 0, 0], // OP4 (off)
		[0, 0, 0], // OP5 (off)
		[0, 0, 0] // OP6 (off)
	];

	// modMatrix[destination][source], radians. Columns: OP1 .. OP6.
	modMatrix = [
		[0, 4.27584, 0, 0, 0, 0], // into OP1
		[0, 0, 0, 0, 0, 0], // into OP2
		[0, 0, 0, 0, 0, 0], // into OP3
		[0, 0, 0, 0, 0, 0], // into OP4
		[0, 0, 0, 0, 0, 0], // into OP5
		[0, 0, 0, 0, 0, 0] // into OP6
	];

	operators = FM7.ar(ctlMatrix, modMatrix);
	signal = (operators * [0.597099, 0.0, 0, 0, 0, 0]).sum;

	// Frees the synth when the note is over.
	EnvGen.kr(Env.asr(0, 1, 0.24673147, \lin), gate, doneAction: Done.freeSelf);
	Out.ar(out, (signal * amp) ! 2);
}).add;
)

// Play a note:
(
fork {
	var note = Synth(\HearSayPatch, [\freq, 60.midicps, \velocity, 0.8, \amp, 0.2]);
	1.5.wait;
	note.set(\gate, 0);
};
)

// Or play it as an instrument:
(
Pbind(
	\instrument, \HearSayPatch,
	\midinote, Pseq([48, 55, 60, 64, 67, 72], 2),
	\velocity, Pwhite(0.5, 1.0),
	\dur, 0.5,
	\legato, 0.8,
	\amp, 0.2
).play;
)
```

The SynthDef is an instrument, not a recording: `freq` transposes ratio operators and leaves fixed operators alone,
`gate` runs the envelopes and frees the synth after the release, `velocity` acts through the velocity sensitivities,
`amp` and `out` do what they always do. It needs SuperCollider and sc3-plugins and nothing of HearSay.

The code of a result folder, of the Studio (code view, Copy, Save…) and of a favourite carries HearSay's level, so
that it sounds as loud in SuperCollider as in the Studio: the output is multiplied by the gain HearSay plays the sound
at (`Out.ar(out, (signal * amp * 3.2109) ! 2)`, after a `// Level: x 3.2109, …` comment). For a result, each of its
alternatives and a library sound that is `PlaybackLevel.GainFor` the peak of its reference note at full velocity, the
gain that brings that peak to 0.5; a favourite keeps the gain it was kept with, and an edit plays and exports at the
gain of its original until *Match loudness* gives it its own. `amp` (default 0.2) scales on top of it.
`PatchExportOptions.Gain` sets it; without it, and always in `ExportSynthDef`, which the non-realtime validation
renders, the output is the raw sound.

### Native versus SuperCollider

`SuperColliderFm7Renderer` renders patches with a real scsynth in non-realtime mode, by default through the FM7
exporter and, given another exporter, through FMX6 or Nome6 (see their sections), and
`BackendEquivalenceValidator` compares that with the reference renderer using HearSay itself
(`BackendExportValidation`: overall, spectral, envelope and partial distance, largest sample difference). This measures
reference renderer against backend, never target against candidate.

Measured with SuperCollider 3.14.1 and sc3-plugins 3.14.0 (`tests/HearSay.SuperCollider.Tests`):

| Test set | Patches | Equivalent | Sample-exact (< 0.1 % of peak) | Worst overall distance |
| --- | --- | --- | --- | --- |
| Random patches in six families: sparse, dense, self-feedback, circular, fixed and detuned, moving (edge envelopes, pitch envelope, vibrato); five notes each at different pitches, velocities and note lengths that do not fall on a block boundary | 30 | 30 | 29 | 0.00003 |
| The thirteen reference patches | 13 | 13 | 13 | 0.00000 |
| Best patch of every round trip below (`samples/HearSay.MatchDemo --supercollider`) | 13 | 13 | — | 0.00000 |
| Instrument check: another pitch, another velocity, early note-off, fixed-frequency operator | 2 notes | 2 | 2 (difference 0) | — |

Typical largest sample difference is 1e-5 (-100 dB): the reference renderer holds the -100 dB floor of an exponential
envelope where SuperCollider has already freed the synth. The one random patch that is not sample-exact differs by
0.6 % of its peak while its spectral distance is 0.00008: strong feedback amplifies rounding differences until the
waveforms part, and the sound stays the same. Two things only showed up against the real UGen and are now part of
the renderer: a gate-off takes effect in the control block that contains it, and operator frequencies are computed
in single precision in the order of the SynthDef.

The tests are skipped on machines without SuperCollider (`HEARSAY_SCLANG` overrides the search for `sclang`).

## Target analysis

`TargetAnalyzer` measures everything before the search starts, and none of it assumes a synthesis method:

| Measurement | Type | Used for |
| --- | --- | --- |
| fundamental, given or estimated; lowest strong partial as a fallback for inharmonic notes | `FundamentalHz` | note of all renders |
| note-off and release tail, from the final stretch that falls much faster than the note usually does, sharpened on the 1 ms envelope | `NoteOffSeconds` | gate time of all renders |
| arbitrary spectral peaks, not only `n × f0` | `SpectralPartial(FrequencyHz, RelativeFrequency, LevelDb, Confidence)` | structure hypotheses, partial-set loss |
| partial tracks: peak and initial level, birth, death, fast and slow decay, frequency drift | `PartialTrack` | diagnosis, priors |
| spectral centroid, bandwidth, slope, flatness and flux per frame | `SpectralTrajectories` | trajectory loss, brightness envelope |
| band energies over time: initial level, attack rate, fast and slow decay, late level | `BandTrajectory` | trajectory loss, modulator envelopes |
| fast and slow decay stage of the amplitude | `DecayModel` | output envelopes |
| `F0(t)`: attack offset, pitch decay, vibrato rate and depth, drift | `PitchTrajectory` | pitch model, pitch loss |
| slow amplitude beating | `BeatAnalysis` | detuned-pair hypotheses |
| long-term average spectrum in third-octave bands | `LongTermAverageSpectrum` | coarse screening; robust to oscillator, beat and vibrato phase |
| noise: the share of the energy between the partials (running median across frequency) and its centroid | `NoiseProfile` | noise priors and mutations |

Formants, the roll-off of the harmonics and brightness sweeps for operator filters are read from these measurements
by `FilterPriors`, in the prior providers and the filter mutations; they are not stored in the analysis.

A stereo file is mixed down to mono, but a channel that is a delayed copy of the first (cross-correlation of at least
0.9 within 2 ms) is aligned before averaging; averaging it as it was would comb-filter the note.

`PartialSetDistance` compares two partial clouds by assignment (a monotone alignment over log-frequency and level with
penalties for unmatched partials), which separates "right frequencies, wrong amplitudes" from "wrong spectral
structure". Example output of `hearsay analyze`:

```jsonc
// hearsay analyze bell/target.wav --f0 330        (abridged)
{
  "fundamentalHz": 330,
  "durationSeconds": 2.359,
  "noteOffSeconds": 2.005,                  // the hidden patch was played for 2.000 s
  "releaseTailSeconds": 0.354,
  "amplitudeDecay": {
    "fastRateDbPerSecond": -24.1,           // struck sound: fast first stage …
    "slowRateDbPerSecond": -10.0,           // … slow second stage
    "breakSeconds": 0.37,
    "breakLevelDb": -10.6,
    "isTwoStage": true
  },
  "partials": [                              // relative frequencies, not forced onto n × f0
    { "relativeFrequency": 1.000, "levelDb": -29.8 },
    { "relativeFrequency": 2.500, … }, { "relativeFrequency": 2.641, … }, { "relativeFrequency": 2.760, … },
    { "relativeFrequency": 4.500, … }, { "relativeFrequency": 6.000, … }, { "relativeFrequency": 8.000, … },
    { "relativeFrequency": 8.161, … }, …
  ],
  "inharmonicity": 0.57,
  "partialTracks": [
    { "relativeFrequency": 1.0005, "peakLevelDb": -20.1, "birthSeconds": 0.02, "deathSeconds": 2.33,
      "decay": { "fastRateDbPerSecond": -17.2, … }, "frequencyDriftCents": … },
    …
  ],
  "centroidHz": [ 1574, 404, 339 ],          // start, middle and end of the held note: brightness dies quickly
  "bands": [ { "name": "mid", "initialDb": …, "decay": { … }, "lateDb": … }, … ],
  "pitch": { "hasMovement": … },             // ignored for this target: an inharmonic note has no pitch to track
  "beating": { "rateHz": …, "isPresent": … }
}
```

## Objective versus diagnosis

Two questions, two tools:

- **How close does this patch sound?** `AudioObjective`, one scalar:

  ```text
  audioLoss = wStft·MultiResolutionStft + wMel·LogMel + wPartial·PartialSet + wTrajectory·Trajectories
            + wEnvelope·Envelope + wPitch·PitchTrajectory + wLtas·Ltas + wBody·BodyPartial
  totalLoss = audioLoss + complexityWeight · patchComplexity
  ```

  The default weights are 0.25 (STFT), 0.15 (Mel), 0.20 (partial set), 0.15 (trajectories), 0.15 (envelope), 0.05
  (pitch), 0.05 (LTAS) and 0.3 (body partials). The audio loss is the weighted mean of the terms a level computes: it
  divides by the sum of their weights. At `Coarse` the Mel and partial weights go to the LTAS.

  `PartialSet` compares the partials of the average spectrum of the whole note, which weighs short-lived attack
  partials as much as those that ring on. `BodyPartial` (weight 0.3) compares the partials of the body only: the
  average spectrum from 0.1 s after the peak, or 15 % of the note if later, to the note-off, in the same window for
  target and candidate; notes whose body would be shorter than 0.2 s have none. On an FM cymbal, 26 sideband peaks of
  the attack otherwise outweighed the 7 bell partials the ear follows.

  The complexity term (operators, edges, tiny edges and outputs, extra envelope points, extreme detuning, operator
  filters and their sweeps) is a
  tie-breaker with a default weight of 0.02; the audio loss dominates.
- **How does this patch differ?** The existing HearSay feature vector and `ToneComparisonResult`. Local refinement
  proposes steps from the Jacobian of that vector and lets the audio objective decide whether to accept them. A
  feature can steer the local model without being a term of the objective, and the other way round. The comparer
  smooths the noise of the spectrograms it compares (partial bins keep their magnitude, the others get the local noise
  floor), so another realization of the same noise scores a similarity of 0.90 instead of 0.73.

Evaluation is progressive: `Coarse` (long-term spectrum, rough spectrum and envelope) sorts out hypotheses, `Normal`
(everything except pitch, unless the target pitch moves) drives the optimization, `Detailed` adds the pitch trajectory
for finalists of notes that have a pitch (inharmonicity below 0.15). Comparison is timbre-normalized by default;
`ObjectiveOptions.TimbreNormalized = false` makes the absolute level part of the loss.

## Parameters: structural and continuous

`PatchParameterSchema.Build(patch, capabilities)` lists every searchable property with an id, a kind, a transform, a
range, a position in the hierarchy and an activity rule.

```text
Patch
├─ Operator 1
│  ├─ op1.enabled · op1.mode · op1.waveform                       structural
│  ├─ Waveform: op1.shape · op1.filter · op1.elliptic · op1.morph · op1.noisePhase · op1.noiseM   (where the profile has them)
│  ├─ Filter: op1.operatorFilter.cutoff · .fixedHz · .q · .formantRatio · .formantLevel · .formantQ2
│  │          .formant3Ratio · .formant3Level · .formant3Q · .peakGain · .octaves · .env.*      (FMX6)
│  ├─ Frequency: op1.frequency.mode · op1.frequency.ratio          structural
│  │             op1.frequency.fixedHz · op1.fineTune · op1.detune · op1.phase
│  ├─ Amplitude Envelope: op1.env.p1.time … op1.env.p4.level, op1.env.breakPosition
│  ├─ op1.velocity · op1.keyScaling.level · op1.keyScaling.rate   (searched only on target sets with several velocities or pitches)
│  └─ Output: op1.output.enabled (structural) · op1.output.level
├─ Operator 2 … 6
├─ Modulation Matrix
│  └─ OP2 -> OP1: edge.2>1.enabled (structural) · edge.2>1.amount · optional amount envelope
├─ Shared Noise: noise.color · noise.cutoff                       (Nome6)
└─ Pitch Model: pitch.env.* · pitch.vibrato.rate/depth/delay/fadeIn
```

A waveform parameter is active only for the waveform that uses it (`.shape` for a pulse, `.filter`, the response, for
filtered noise, `.elliptic` and `.morph` for an elliptic operator). Filter type and slope, the harmonic limit and
whether a filter has a sweep are structural and change by mutation only; a filter's numbers enter the schema once a
prior or a mutation has given the operator a filter (see [Operator filters](#operator-filters)).

| `ParameterSearchKind` | Examples | Who moves it |
| --- | --- | --- |
| `StructuralDiscrete` | operator enabled, mode, waveform, edge enabled, ratio or fixed, coarse ratio, output enabled | hypotheses and topology mutation only; defines the basin |
| `WarpedContinuous` | amounts (soft-log), levels, times and fixed frequencies (log), detune | CMA-ES, local refinement |
| `Continuous` | fine tune, phase, velocity, key scaling | CMA-ES, local refinement |
| `OrderedDiscrete`, `Categorical` | reserved for backends with stepped controls | — |

- **Waveform, mode and coarse ratio are structural.** `Sine → Triangle → Saw` is not an axis, and
  `2.0 → 2.01 → 2.02` is not a smooth path either: a coarse ratio is chosen from a candidate set (simple ratios plus
  ratios inferred from the partials of the target), and only a small fine-tune offset is optimized continuously.
- **Conditional activity** is part of the schema: a disabled operator takes its frequency, waveform, outgoing edges
  and output gain out of play; a disabled edge takes its amount and its envelope with it; a modulator with nothing
  left to modulate goes inactive as a whole.
- **Transforms** (`IParameterTransform`): linear, logarithmic and soft-logarithmic, so that equal steps sound equally
  large. All optimizers work in the unit cube of the transformed axes.
- **Ties** (`ParameterTie`) are temporary macros, for example "attack of all heard operators" or "decay of all shaping
  operators"; early stages optimize a few macros, later stages split them.
- **Active and frozen sets** (`ActiveParameterSet`): a continuous optimization sees at most
  `MaxActiveParameters` (32) parameters, typically 10 to 25: one connected operator group with its amounts, output
  levels and envelope shape. Everything else is frozen.
- Envelope times are bounded by the note that is being matched, and the two segments between attack and sustain share
  a derived *break position* axis. Both remove regions where a parameter does nothing.
- **Regimes.** `OperatorMatrixRegimeKey` captures audible operators with mode, waveform, frequency mode and coarse
  frequency (fixed frequencies to the semitone), filter type and slope and whether the filter sweeps, active edges and
  whether they have an amount envelope, output topology, and whether the pitch moves. `PatchCanonicalizer` brings
  relabelled twins into one operator order. Local models are stored per regime (`RegimeModelStore`): a Jacobian
  learned with a sine in OP3 is never used once OP3 is a triangle, and one learned with an edge present is not used
  once the edge is gone.
- **Hand editing** uses the same ids, and the same ranges and scales where the schema has the value
  (`HearSay.Editing.EditableValues`), widened to the value a patch already has, so a number typed or dragged in the
  Studio moves on the axis the search moves on.

## The search

`OperatorMatrixPatchSearchCoordinator` owns the flow and no DSP:

| Stage | What happens | Level | Budget (default 20000) |
| --- | --- | --- | --- |
| A. Target analysis | everything in the table above; render request from fundamental, note-off and tail | — | 0 |
| B. Structural hypotheses | prior providers propose structures; at least 128 are kept, more when the screening share of the budget can afford three modulation scales for them (all of them from about 8000 evaluations on), taking each provider's best in turn since prior scores compare only within a provider; each is screened at three modulation scales when the screening share affords it (from about 7700 evaluations on), otherwise at one | Coarse | 5 % |
| Survivors | 32 get a short continuous optimization (plus up to four neighbours from the [structure library](#structure-library)), half of them the best of every provider group in turn and the rest by rank, so that one group whose untuned hypotheses screen well (noise carriers, say) cannot take every slot; duplicates by canonical regime, frequency structure and loss terms are dropped; 6 distinct basins remain, plus up to two library neighbours (`LibraryBasinMode.Extra`), the best of every structural family first (heard and shaping operators, feedback, waveforms, filtered or not), as long as it is within 1.6 times the best loss, because early losses are deceptive: an additive structure sounds roughly right at once, a deep stack only after its amounts have been found | Normal | 15 % |
| C–E. Deep optimization | three rounds of successive halving (6 → 4 → 3 basins, or 8 → 6 → 3 with two library basins). Per basin: CMA-ES over everything smooth at once for small patches, otherwise one connected operator group at a time. A basin keeps its run between rounds, so the adapted covariance is never thrown away; only a stagnated run is replaced, by one with twice the population that starts alternately from the best patch and from where the basin began. A basin that stagnates also tries three topology mutations; a better child replaces its parent | Normal | 35 % |
| Finalists | one long run each, the best finalist getting the longest; then a round of structural variants per finalist: every possible new edge and new modulator is screened at a few amounts (at most 160 renders and at most a quarter of the finalist's share, so a small budget still leaves local refinement its part), the most promising variants are optimized, and a variant that beats its parent takes its place | Normal | 75 % of the rest |
| F. Local refinement | probe each smooth parameter, regime-specific Jacobian of the HearSay feature vector, trust-region guidance steps accepted only when the objective agrees; then a narrow final polish | Detailed | the rest |

- **Sine-FM knowledge is a plug-in.** `SinePhaseModulationPriorProvider` is the only place with sideband arithmetic:
  it ranks carrier and modulator ratios by how well `|c ± k·m|` covers the partial cloud, estimates a starting index
  from Carson's rule `BW ≈ 2(I + 1)·fm`, and builds stacks, three- and four-operator stacks, parallel modulators,
  greedy multi-stack covers, additive sets, feedback operators and detuned pairs for beating targets (see
  [Reading stacks](#reading-stacks) below). These formulas only rank guesses; nothing treats them as an inverse of a
  six-operator network. Envelope and pitch starting values come from `TemporalPriors`, which knows nothing about
  synthesis. The other providers: `PartialTrackPriorProvider` (operators exactly on the stable partial tracks, each
  with the decay of its own partial, for sounds that sideband arithmetic does not explain), `WaveformVariantPriorProvider`
  and `ModulatorWaveformPriorProvider` (the best sine structures with an output or a modulator in another waveform, for
  profiles with a choice), `HarmonicWaveformPriorProvider` and `NoisePriorProvider` (see [FMX6](#how-a-custom-ugen-plugs-in-fmx6));
  on profiles with operator filters `FormantPriorProvider` (a saw or a pulse through a formant filter on the formants
  of the target) and `SubtractivePriorProvider` (a saw or square through a low-pass read from the harmonics, with the
  target's brightness sweep); and `NearestStructurePriorProvider`, which does not read the target but looks it up (see
  [Structure library](#structure-library)).
- **Structure is never searched by gradients.** Structures come from hypotheses and from `TopologyMutator`, whose new
  elements start almost silent so that a variant begins as good as its parent (add or
  remove an edge, add an operator as modulator or into the mix, remove an operator, move a coarse ratio to its
  neighbour, toggle an output, change waveform, mode, harmonic limit or elliptic m where the profile has a choice,
  roughen an operator with the shared noise, give an edge an amount envelope, add a wash of noise, add, change or
  remove an operator filter or its sweep, ratio to fixed for very low operators or, on a target set, for partials that
  do not transpose). An operator with a formant filter stays a rich source through every step (see
  [Operator filters](#operator-filters)). Every structure is its own basin with its own continuous optimization.
- **CMA-ES** (`CmaEs`, ask and tell, box-constrained by reflection) only ever sees smooth parameters.
- **Stagnation**: a run ends after 25 generations without 1 % progress *of that run*; the basin then restarts or
  mutates, or its budget goes to other basins. Restart counts and the fate of every basin are in the summary.
- **The course of the search** is in the summary too: `Improvements`, every new best loss with the evaluation that
  found it, and `Trace`, every screened hypothesis and every improvement of every basin with its stage and level.
  Progress reports carry the trace while the search runs, and the Studio draws it as a chart. Losses compare only
  within a level, and within a stage: screening, and on a target set the survivor round too, judge the reference note
  alone.
- **Budget, caching, reproducibility**: every evaluation counts, at any level; one evaluation renders every note of a
  target set, and a cache hit costs nothing. Evaluations are cached by patch hash and level. Batches are evaluated in
  parallel with the budget assigned in input order, and all randomness derives from the seed, so a search is
  reproducible and independent of the degree of parallelism.
- **Identifiability**: many patches sound alike. The search optimizes audio similarity plus a small simplicity bias,
  never parameter equality, and canonicalization keeps relabelled twins from taking two basin slots.

### Reading stacks

Sideband arithmetic tells which lines a structure produces. That is enough for a pair, but a grid of lines has
several readings once more operators are involved: a DX7-style stack `1 ← 5 ← 2.73` puts its lines on the same grid as
a carrier with parallel modulators at 2.27 and 7.27. The sine prior provider therefore works in steps:

1. **Candidate ratios.** Differences between strong partials, snapped to simple values and as measured, plus the
   *sum* of a carrier and a partial: a modulator above its carrier folds the lower sideband over zero, so a
   1321 Hz carrier with a 2089 Hz modulator is heard at 768 Hz. A sideband that lands exactly counts slightly more
   than one at the edge of the tolerance.
2. **Canonical carrier.** A carrier at `c` and one at a sideband `|c + k·m|` predict much the same lines, and neither
   frequencies nor levels tell them apart. Decay does: when the modulation dies away, only the carrier rings on. So
   among the lines of a reading, a line on a partial whose track rings at least 1.5 times longer becomes the carrier,
   repeated until none does, and readings that coincide are merged.
3. **Second modulators** (driving the first, or beside it on the carrier) put lines at `|c + a·m1 + b·m2|` and are
   chosen from all candidate ratios, but proposed only when they explain the target clearly better than the pair
   alone. These stacked readings form a provider group of their own, so that a stack, which sounds right only once
   its amounts are found, keeps a basin; the group stays empty when the plain hypotheses already explain 90 % of the
   spectrum.
4. **Line strengths.** Which reading is right shows in how strong the lines are. `FmSpectrumModel` predicts the line
   levels and fits the modulation indices to the measured partials; the error is the level mismatch in dB of the
   partials within 40 dB of the strongest, matched within 35 cents. Three-operator readings (every carrier on a strong
   partial with any two candidate modulators, the forty best by line coverage) use a Bessel expansion, fitted on a 9 × 9
   index grid; lines below zero fold over with the opposite sign. Four-operator stacks `c ← m1 ← m2 ← m3` are
   enumerated directly (carriers on the four strongest partials, `m1` at the distance to another strong partial, any
   two candidates above it) and *rendered*: the chain of sines is synthesized over 16 periods, windowed, transformed
   and peak-picked, which is exact to every order and a fraction of a millisecond per evaluation. Every chain is scored
   at the eight index sets of 1 and 2, the best twelve get their indices refined by coordinate descent, and the best
   four are proposed at the fitted indices, with and without feedback on the top operator.

On the DX7-style stack above, the fit error is 0.27 for the truth and 0.48 to 0.59 for the other readings, and the
search goes from 0.297 to 0.077. An affordable Bessel expansion of a four-deep chain misranks it (0.459 at the true
indices of a test stack against 0.039 rendered), and counting lines favours large ratios whose lines fall above the
spectrum. Rendering the three-operator fits as well, with fixed or with decaying indices, was tried and was not better
within the noise of the benchmark: the Bessel model's truncation happens to resemble the time-averaged spectrum of
indices that follow envelopes.

| Random targets, mean audio loss of the best patch | pairs only | + second modulators | + line strengths | + four-stacks |
| --- | --- | --- | --- | --- |
| DX7-style stacks | 0.175 | 0.168 | 0.141 | 0.127 |
| random FM7 patches | 0.190 | 0.183 | 0.176 | 0.172 |

## Target sets: several notes, one patch

One note cannot tell a fixed-frequency operator from a ratio operator, nor reveal key scaling or velocity
sensitivity, and a structure that happens to fit one octave may be wrong everywhere else. A target set fixes that:

```bash
hearsay match piano-48.wav@48 piano-60.wav@60 piano-72.wav@72 piano-60-soft.wav@60:v0.4 --output result/
```

```csharp
PatchMatchResult result = matcher.Match(
    [AudioTarget.FromWaveFile("piano-48.wav", midiNote: 48), AudioTarget.FromWaveFile("piano-60.wav", midiNote: 60), AudioTarget.FromWaveFile("piano-72.wav", midiNote: 72)],
    new PatchMatchOptions { EvaluationBudget = 20000 });
foreach (NoteMatch note in result.Notes) { /* per-note preview and diagnosis */ }
```

`file.wav@60` gives the MIDI note, `@261.6` a frequency (any value above 127 is hertz), `:v0.4` the velocity. A
ready-made three-octave set is in `samples/targets/electric-piano-keyed`, and `hearsay reference <patch> --notes 48,60,72`
renders any reference patch at any notes. In
the desktop app, add several files and type pitch and velocity per row
([Studio manual, section 3](studio-manual.md#3-matching-a-sound)).

What changes inside the search:

- Every candidate is rendered at every note and the losses are averaged, every note with the same weight
  (`TargetNote.Weight` sets other shares for code that builds a `PatchEvaluator` itself; the matcher does not).
  The budget counts candidate evaluations; `RendersUsed` in the summary tells how many renders that took.
- Structural stages (hypothesis screening, survivor optimization) judge on the *reference note* alone, the one in
  the middle of the pitch range: which structures deserve optimization is decided as well by one note as by all,
  at a third of the renders. From deep optimization on, everything is judged on the whole set; the basins are
  re-evaluated at the hand-over and their optimizer runs start afresh.
- Partials that stay at the same frequency in hertz across notes of different pitch (`TopologyMutator.FindFixedPartials`)
  become candidate fixed-frequency operators: the best hypotheses get a variant with one, and every structural
  screening round tries them, as well as pinning each ratio operator to its current frequency.
- Key scaling (level and rate per octave) joins the continuous search when the set has several pitches, velocity
  sensitivity when it has several velocities. On a single note they are left neutral, because nothing could tell them apart.
- The result carries a preview and a full diagnosis per note (`PatchMatchResult.Notes`, in the order the targets were
  given; in the result folder `preview-note2-440Hz.wav`, `analysis-note2-440Hz.json` and `comparison-note2-440Hz.json`
  per note, besides the plain files of the reference note), and the SynthDef is one instrument for all of them.

Measured (`tests/HearSay.IntegrationTests/MultiNoteTests.cs`, with the structure library off, because its patches have
no key scaling and it is looked up from one note): the `electric-piano-keyed` reference patch, the electric piano with
a fixed 60 Hz operator and key scaling on the tine (-6 dB and half the envelope time per octave), rendered at MIDI
48, 60 and 72. Matching the set with 20000 evaluations (49 000 renders, 224 s) brings the set loss from 0.218 to
0.107, with a similarity of 0.72 to 0.76 on the three notes. Judged on an unseen note (MIDI 55), the set patch scores
0.102 and the patch matched to the middle note alone with the same budget 0.097: in this run the single-note patch
does slightly better, and the test only requires the set patch to be within 10 % of it. The set search found neither
the tine's 1:14 stack nor the fixed operator; its best patch is a 1:1 stack with both operators heard. The same
variance across seeds applies as for single notes.

## Results

### Round trips

`samples/HearSay.MatchDemo` renders each of the thirteen reference patches to a target, gives the matcher the true
fundamental, hides the patch and lets the matcher search under FM7 with the default options: a budget of 20000
evaluations, seed 1234 and the [structure library](#structure-library) on. `electric-piano-keyed` is matched here as a
single note; it is measured as a target set under [Target sets](#target-sets-several-notes-one-patch). Every target
folder is a result folder, so its `patch.scd` carries the gain HearSay plays the sound at. Nothing asks for the
original parameters back: many patches sound alike, so the measure is how much of the audio loss disappears.
*Initial* is the best untouched hypothesis at the normal level, *final* the best patch; both are audio losses without
the complexity term. *Similarity* is the HearSay similarity of the full diagnosis (1 is identical). *SC distance* is
the HearSay distance between the offline preview and the same patch played by the real FM7 UGen (`--supercollider`;
without it the column is empty).

| Target | Hidden structure | Initial | Final | Gain | Similarity | Renders | Seconds | SC distance | Found structure (ratios; `a>b` = OPa modulates OPb) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `pure-sine` | one operator | 0.0077 | 0.0000 | 222× | 0.945 | 19001 | 67.1 | 0.00000 | 1 → out **(same)** |
| `simple-2op` | 1 ← 2 | 0.0788 | 0.0038 | 21× | 0.925 | 19203 | 84.8 | 0.00000 | 1 → out; 2>1 **(same)** |
| `bright-fm` | 1 ← 1 ← 3 | 0.1485 | 0.0131 | 11× | 0.828 | 19244 | 91.3 | 0.00000 | 1 → out; 2(r1)>1, 3(r3)>2 **(same)** |
| `bell` | 1 ← 3.5 and 2.76 ← 5.4 | 0.1802 | 0.0427 | 4.2× | 0.766 | 19039 | 117.2 | 0.00000 | outputs at 1 and 2.76; 3(r3.5)>1, 4(r5.4)>2 **(same)** |
| `metallic-inharmonic` | 1 ← 1.41 and 1 ← 2.73 | 0.2830 | 0.1572 | 1.8× | 0.530 | 19135 | 102.9 | 0.00000 | 1 → out; 2(r1.41)>1, 3(r4.14)>1 |
| `electric-piano` | 1 ← 1 and 1 ← 14 | 0.1840 | 0.0436 | 4.2× | 0.782 | 19199 | 110.6 | 0.00000 | two outputs at 1; 3(r1)>1, 4(r14)>2 **(same)** |
| `electric-piano-keyed` | as `electric-piano`, with key scaling and a fixed 60 Hz operator | 0.2201 | 0.0873 | 2.5× | 0.781 | 19227 | 73.6 | 0.00000 | two outputs at 1; 3(r1)>1, 4(r1.25)>1, 5(r14)>2 |
| `bass` | 1 ← 1 with modulator feedback | 0.1256 | 0.0052 | 24× | 0.895 | 19135 | 86.4 | 0.00000 | 1 → out; 2(r1)>1, 2>2 **(same)** |
| `brass-like` | 1 ← 1 | 0.0565 | 0.0071 | 8.0× | 0.916 | 19207 | 80.1 | 0.00000 | 1 → out; 2(r1)>1, 3(r1)>2 |
| `multi-output` | 1 ← 2, the modulator also heard; 3 → out | 0.0503 | 0.0116 | 4.4× | 0.831 | 18691 | 77.2 | 0.00000 | six outputs at 1, 2, 3, 5, 7, 9; 2>6 (additive) |
| `detuned` | two 1 ← 2 stacks, 2.4 Hz apart | 0.1192 | 0.0679 | 1.8× | 0.670 | 19170 | 115.0 | 0.00000 | 1 (r1.002) → out; 2(r2.003)>1, 2>2 |
| `feedback-heavy` | 1 ← 2, both with self-feedback | 0.1494 | 0.0474 | 3.2× | 0.750 | 19217 | 88.3 | 0.00000 | 1 → out with 1>1; 2(r1)>1, 3(r2)>1 with 3>3, 4(r3)>2 |
| `circular-routing` | 1 ← 2 ← 3 and 1 → 3 | 0.0741 | 0.0445 | 1.7× | 0.832 | 19216 | 89.7 | 0.00000 | 1 → out with 1>1; 2(r2)>1, 3(r2)>2 with 3>3 |

Machine: Intel Core i7-13700KF, 24 logical cores, .NET 10; measured on 2026-10-20 on an otherwise idle machine. Median
gain 4.2×; 67 to 117 seconds per target. Against the first measurement of September 2026 (twelve targets, no
structure library, an earlier objective: the initial losses differ too, so the numbers do not compare one to one),
`bell` and `bright-fm` came out clearly better (0.0427 against 0.0835, 0.0131 against 0.0348), `electric-piano`
clearly worse (0.0436 against 0.0090), and a search takes two to three times as long (it was 28 to 43 seconds).
How to read this:

- **Exact parameter recovery is not required and not claimed.** Six of the thirteen hidden structures come back as
  they were (marked *same*), among them the three-operator stack of `bright-fm`, both stacks of `bell` and both stacks
  of `electric-piano` including its modulator at ratio 14. Elsewhere another structure explains the sound: six sine operators straight
  into the mix for `multi-output`, a feedback carrier with three modulators for `feedback-heavy`. A different routing
  with the same partials is a correct answer, and the simplicity bias prefers the smaller one.
- The loss that the hidden patch itself gets, rendered with the *estimated* note length, was between 0.000 and 0.03 for
  the twelve targets of September 2026. That is the floor of the measurement; what remains above it is search.
- Simple stacks, basses and brass-like sounds are matched closely; electric-piano and feedback sounds less so in this
  run (the structure is often right, the values are not). Dense inharmonic
  spectra (`metallic-inharmonic`, `bell`) and beating pairs (`detuned`) are harder: the right family of structures is
  usually among the finalists, but the continuous landscape inside such a structure has many optima and the budget
  does not always find the good one.
- **Results vary with the seed, and with tiny changes of the input.** The electric-piano target read back from its
  16-bit WAV file instead of from memory gives 0.079 with seed 1234 and 0.021 to 0.045 with seeds 1 to 4; four of
  these five runs find the hidden structure. Every run is reproducible, so running several seeds and keeping the best
  is cheap to organise: `--seeds 4` on the command line, `MatchBestOf` in the API. The result is that of the winning
  seed, and its alternatives are the finalists of all seeds merged, the best patch of every structure, best first.
- **A higher budget usually helps too.** On 26 sounds (the FM7 demo sounds and FMX6 sung vowels, seed 1), three to
  four times the budget lowered the audio loss in 24: by a median of 23 % from 3000 to 12 000 evaluations, and of
  12 % from 20 000 to 60 000. Neither a loss still falling at the end nor a plateau predicted which searches gained:
  at 20 000 the searches that had stood still for half the run gained 9 to 59 %, the others 3 to 15 %. A larger
  budget gives every stage more, so it also screens more hypotheses and keeps more structures. See the
  [expert manual, section 5](expert-manual.md#5-budget-seeds-and-time).
- Every one of these patches sounds the same in SuperCollider as in the preview (SC distance).

Example console output (`hearsay match electric-piano/target.wav --f0 262 --seed 2`):

```text
[   240/20000] hypothesis screening             best loss 0.1187
[   357/20000] survivor optimization            best loss 0.1609
[  3237/20000] deep optimization round 1        best loss 0.0645
…
[ 17869/20000] local refinement                 best loss 0.0335
target      f0 262.00 Hz, velocity 1.00, 2.10 s, 9 partials, inharmonicity 0.01, similarity 0.837
search      19222 evaluations, 19222 renders in 54.3 s, profile supercollider-fm7, seed 2
audio loss  0.1843 (best untouched hypothesis) -> 0.0260
similarity  0.837 (HearSay overall distance 0.0595)
structure   op1:PhaseModulation/Sine/r1/out;op2:PhaseModulation/Sine/r1/out;op3:PhaseModulation/Sine/r1;op4:PhaseModulation/Sine/r14;|3>1;4>2;
origin      2 stacks 1:1 + 1:14

OP1  Sine     ratio 1          fine    0.0 ct  detune  0.00 Hz  output 0.582
OP2  Sine     ratio 1          fine    0.0 ct  detune  0.00 Hz  output 0.262
OP3  Sine     ratio 1          fine    0.0 ct  detune  0.00 Hz  output 0.000
OP4  Sine     ratio 14         fine    0.0 ct  detune  0.00 Hz  output 0.000

modulation matrix (rows: destination, columns: source, radians)
         OP1    OP2    OP3    OP4    OP5    OP6
  OP1      .      .    1.52     .      .      .
  OP2      .      .      .    1.41     .      .
  …
12 files written to …\result
```

The search, with the structure library on, finds both stacks of the hidden patch, which has 1.60 and 1.20 on these
two edges. A profile's first search prints the build of its structure library before the search lines.

`patch.json` of the two-operator round trip, abridged (the complete SynthDef of the same patch is shown under
[SuperCollider export](#supercollider-export)):

```jsonc
{
  "operators": [
    {
      "enabled": true, "mode": "PhaseModulation", "waveform": "Sine",
      "frequency": { "mode": "Ratio", "ratio": 1, "fixedFrequencyHz": 440 },
      "fineTuneCents": 0, "detuneHz": 0, "initialPhase": 0,
      "amplitudeEnvelope": {
        "points": [
          { "timeSeconds": 0,       "level": 0,     "curve": "Linear" },
          { "timeSeconds": 0.0017,  "level": 1,     "curve": "Linear" },
          { "timeSeconds": 0.3009,  "level": 0.345, "curve": "Exponential" },   // hidden patch: 0.3 s to 0.4
          { "timeSeconds": 0.8983,  "level": 0.122, "curve": "Exponential" },   // reached at note-off
          { "timeSeconds": 0.1967,  "level": 0,     "curve": "Exponential" }    // hidden patch: 0.2 s release
        ],
        "sustainPoint": 3, "releasePoint": 4
      },
      "velocitySensitivity": 0, "keyScaling": { "levelDbPerOctave": 0, "ratePerOctave": 0, "referenceMidiNote": 60 },
      "shape": 0.5, "morph": 0, "noisePhase": 0, "noiseM": 0, "harmonicLimit": 32   // waveform fields a sine ignores
    },
    { "…": "OP2: ratio 2, its own envelope" }, { "enabled": false, "…": "OP3 to OP6" }
  ],
  "modulationMatrix": {
    "operatorCount": 6,
    "edges": [ { "sourceOperator": 1, "destinationOperator": 0, "enabled": true, "amount": 4.276, "amountEnvelope": null } ]
  },
  "outputLevels": [ 0.597, 0, 0, 0, 0, 0 ],
  "pitch": { "pitchEnvelope": null, "vibrato": null },
  "noise": { "color": 0, "cutoffHz": 8000 }
}
```

`filter` appears only on an operator that has one. `preview.wav` is the offline render of that patch with the note of
the target (f0, note-off and tail as analysed), peak-normalized to 0.7 (-3 dBFS). `patch.scd` plays at the level
HearSay plays the sound at: its output is multiplied by the gain that brings the reference note at full velocity to a
peak of 0.5, with `amp` on top. `alternatives/` holds the other finalists in the same three formats, each with its own
gain. `search-summary.json` also records the course of the search (`improvements` and `trace`, see
[The search](#the-search)), which the Studio draws as a chart.

### Round-trip tests

`tests/HearSay.IntegrationTests` run smaller searches (budget 9000, structure library off, so that the gain is that of
the search from the read hypotheses) and assert a strong reduction of the audio loss, never parameter equality: at
least 5 times for `simple-2op`, 4 times for `brass-like` and 2 times for `bell` (in September 2026, before the
library, they went 0.134 → 0.005, 0.079 → 0.007 and 0.268 → 0.101). The same project checks that a search is
identical for the same seed whatever the degree of parallelism, that it differs for another seed, that the budget is
respected and accounted per stage, that a profile without feedback yields patches without feedback even for a target
that was made with it, that unusable targets and reserved profiles are refused, that the structure library finds one
of its own patches back and that its build reports progress, that FMX6 and Nome6 find the waveforms FM7 cannot play,
that `MatchBestOf` merges the finalists of every seed, and that the result folder and the gain in its code are as
documented. The patch editor and the Studio's playing logic have their tests there too.

### Future-waveform proof

`FutureWaveformProof_WaveformIsStructuralAndBackendsRejectWhatTheyCannotPlay`: a target made with a triangle operator
is searched under `ExperimentalSineTriangle` with the unchanged coordinator, the extended renderer and the
waveform-variant provider. Sine and triangle structures end up in separate basins, the best patch contains a triangle
operator (`op1:PhaseModulation/Triangle/r1/out;…`, audio loss 0.031), and the FM7 validator, renderer and exporter
all reject that patch. Under the FM7 profile the same target is searched with sine operators only.

### FMX6 versus FM7 on waveform targets

Synthetic targets rendered by the FMX6-compatible renderer at 196 Hz, each searched under both profiles with the
same budget (4000 evaluations) and seed, measured in September 2026, before the structure library and the operator
filters. Audio loss of the best patch, lower is better:

| Target | FM7 | FMX6 | FMX6 structure found |
| --- | --- | --- | --- |
| saw (48 harmonics) | 0.167 | 0.054 | saw carrier, two sine modulators |
| square | 0.179 | 0.036 | square carrier, two sine modulators at 2 |
| pulse, duty 0.2 | 0.292 | 0.079 | one pulse operator |
| saw lightly modulated by a sine at 2 | 0.157 | 0.052 | one saw operator |
| sine modulated by sample-and-hold noise | 0.059 | 0.025 | sine with a sample-and-hold noise modulator |
| sine with a white-noise burst | 0.078 | 0.057 | sine with a parallel white-noise burst, one sine modulator |

Every target is matched with the waveform that made it. The band-limited ones come from the harmonic-waveform
prior; the noise ones from its noise hypotheses (a noise modulator and a parallel burst at several burst lengths and
levels) together with the basin selection after the survivor round, which keeps the best basin of every structural
family, and a family includes the waveforms in use. Noise never matches sample by sample: the search never sees the
noise sequence of the target, so the correct structure with another sequence scores about 0.08 on the white-noise
burst, and the objective judges it by spectrum and envelope.

On 20 random FMX6 patches (two seeds each) the tuned duty cycle and filter response, the modulator-waveform prior and
the per-operator waveform mutations bring the loss from 0.221 to 0.204, on the sparse ones from 0.220 to 0.172; a
pulse at duty 0.34 that used to end at 0.30 with an extra sine (0.109) is now found exactly (0.003). On a cymbal of
inharmonic FM with a noise wash (`fm_cymbal`), filtered noise takes FMX6 from 0.44 to 0.34, where FM7 ends at 0.45.

### What limits dense patches

Twenty dense FM7 patches as a musician would make them (four to six operators, a carrier on the note, every heard
operator at or above it, no loops, no fixed operators, modulation up to 2 radians) end at a mean audio loss of 0.196
(budget 4000, two seeds). Oracle hypotheses added to the normal ones show where that comes from:

| Added hypothesis | Loss |
| --- | --- |
| the hidden patch itself | 0.008 |
| its structure (ratios and edges) with default amounts, levels and envelopes | 0.168 |
| that structure alone, one basin, no mutations, the whole budget | 0.097 |
| that structure without its last modulator | 0.182 |
| only its carriers | 0.193 |

The objective recognises the right patch, and the right structure halves the loss once it is tuned, but the priors
find the carriers and miss the modulator structure, and the mutations do not recover even one missing modulator.
Untuned, the right structure scores worse than wrong ones (0.52 against 0.30 on one target), and among the variants
of a tuned parent the true missing modulator, added back, beats the parent in seven of ten targets but loses the
screening to edges that help a little more at once. Changes that measured as no better than the noise of the
benchmark (paired, ± one standard error of about 0.004 to 0.009): capping the renders spent screening variants in the
deep stage (they take 30 to 65 % of it), interleaving the kinds of variant under the cap so that new modulators are
screened at all, trying new modulators at the spacings between strong partials, screening at three modulation
scales, 16 survivors instead of 32, six or ten mutation rounds instead of three. Worse: two basins and one finalist
(+0.019), no mutations (+0.034), and making the short deep-stage cycles effective (a group of 20 parameters gets two
CMA-ES generations there and does not move; the low-dimensional stages do move it, but then the basins no longer
stagnate and fewer mutations are tried: +0.010). Five times the budget takes the losses of these dense patches down
only a little (on other sounds a higher budget helps more, see [Round trips](#round-trips)). Dense
patches need a structure search that judges a variant after tuning it, not a retuning of this one, or starting points
that come with fitting amounts, which is what the structure library below provides.

### Structure library

Instead of reading a structure from the partials, look it up: `StructureLibrary` holds many random patches of a profile
with the `SpectralFingerprint` of their sound, and `NearestStructurePriorProvider` proposes the eight whose fingerprints
are closest to the target, each as it is and with the envelopes of the target, in a hypothesis group of its own.

- **Fingerprint.** Band powers on relative frequency, a quarter of the note to 64 times it in semitone bands, averaged
  over three slices (attack to 0.1 s, body to 0.4 s, decay to 1 s), in dB under the loudest band, floored at -60 dB and
  stored at a quarter of a decibel per byte: 288 bytes, independent of the note, with sideband spacings as fixed
  patterns and the movement of the brightness in the slices. Nearest is squared Euclidean distance.
- **Patches.** Musical random patches of the profile: a carrier on the note, every heard operator at or above it, no
  loops through several operators, no fixed operators, modulation up to 2 radians, in three densities (two or three,
  three or four, four to six operators). Only the seeds are stored; a patch is regenerated from its seed when needed.
  A profile with noise (FMX6, Nome6) has as many noise-free patches again, from seeds of their own: no noise waveform
  and no roughening (see *Noise-free sounds* below). Under FMX6 every enabled operator gets a filter with probability
  0.3, from a generator of its own, so that the rest of the patch is the one its seed gives without filters; an
  operator with a formant filter is a rich source (see [Operator filters](#operator-filters)). Every patch is rendered
  and fingerprinted at 220 Hz, held 1 s and released 0.3 s. Only profiles with a backend-exact renderer get a library.
- **Building.** `PatchMatchOptions.StructureLibrarySize` (300 000 by default, 0 turns it off) regular patches are
  rendered once per profile and kept in `%LOCALAPPDATA%\HearSay\structure-libraries` (88 MB per 300 000 patches, so
  175 MB for FMX6 and Nome6 with their noise-free sounds, plus a family file of a few MB once the Library tab has
  grouped them):
  under a minute for FM7; for FMX6 about 8 minutes for the 300 000 regular patches and 12 for the 300 000 noise-free
  ones, whose band-limited waveforms cost more to render than noise (measured on library version 3); for Nome6 5 and 6 minutes (24 threads). The CLI
  and the Studio opt out of the Windows efficiency mode (`ProcessSpeed`), which otherwise runs a process that is not in
  the foreground on the efficiency cores: the noise-free FMX6 patches took 36 minutes there.
  The version of a profile's library (`SynthesisProfile.StructureLibraryVersion`: FM7 1, FMX6 8, Nome6 2) is part of
  the file name, so a change of the random patches rebuilds that library once. A profile whose regular sounds are
  those of an older version names it (`StructureLibraryReusedVersion`): Nome6 v2 reads the sounds of v1 when that file
  is on disk and renders only the noise-free ones. FMX6 v8 reuses nothing, because its formant sounds differ from
  those of every earlier version. The first search of a profile reports the build as its first progress stages, with
  the share done and the time left (`RemainingTime`, from the pace since the first report); the Library tab does the
  same in its graph.

Results, budget 4000, two seeds, against the same targets without the library:

| Targets | Without | With | Paired difference | Better / worse |
| --- | --- | --- | --- | --- |
| 20 dense musical FM7 patches | 0.196 | 0.163 | −0.033 ± 0.010 | 13 / 3 |
| 20 sparse musical FM7 patches | 0.100 | 0.079 | −0.021 ± 0.007 | 13 / 2 |
| 14 dense musical Nome6 patches | 0.235 | 0.207 | −0.028 ± 0.011 | 9 / 1 |

A library of only dense patches gives 0.174, 0.170 and 0.149 at 20 000, 100 000 and 300 000 patches on the dense FM7
targets: larger libraries give closer neighbours. The neighbours are almost never the hidden structure itself (none
of the 20 dense targets had it among its eight), yet the best patch descends from one of them in 15 of 20: a patch
that sounds alike, with amounts that fit it, is a better start than the right structure with default amounts (0.168).
The targets above are random patches of the same kind as the library, so these numbers are the favourable case. On
the 13 hand-made reference patches, competing freely, the neighbours cost a little (+0.005 ± 0.003, worse on 5,
better on 3; at a budget of 9000 brass-like ended at 0.038 instead of 0.016 and bell at 0.094 instead of 0.043):
they screen better than read structures with default amounts, take the survivor and basin slots that go to the best
of the rest, and leave out a read structure that would have won once tuned.

So the default is a hybrid, `LibraryBasinMode.Extra`: survivors and basins are chosen as if there were no library,
and the best neighbours come on top, four extra survivors and up to `LibraryBasinCount` (2) extra basins.
`Capped` lets the neighbours compete for at most two basins; `Free` lets them compete for all.

| Mode, budget 4000 | Dense | Sparse | Reference |
| --- | --- | --- | --- |
| Free, eight neighbours | −0.033 ± 0.010 | −0.021 ± 0.007 | +0.005 ± 0.003 |
| Capped at two basins | −0.029 ± 0.009 | −0.019 ± 0.007 | +0.005 ± 0.005 |
| Extra two basins | −0.019 ± 0.008 | −0.016 ± 0.006 | +0.005 ± 0.003 |

Two extra basins on top of six take a quarter of the budget of the others. At an equal budget of 5333 for both,
on the reference patches, Extra is level with searching without the library (−0.002 ± 0.003, better on 4, worse on
1) and Capped still loses (+0.004 ± 0.003, worse on 7). The library is worth its cost where a target has neighbours,
and costs only budget where it has none.

#### Noise-free sounds

A random FMX6 patch draws each operator's waveform from all eight, three of them noise, so 77 % of the 300 000 regular
FMX6 sounds have a noise operator that is heard (Nome6: 73 %, FM7: none). The search already models the noise of a
target separately (`NoisePriorProvider` adds the measured noise to the structures read from the partials), and a
tonal target found its neighbours mostly among noisy sounds. Library variants on FMX6, budget 4000, two seeds, paired
against the 300 000 regular sounds (Δ mean loss ± one standard error, better / worse at 0.005):

| Library | 16 tonal targets | 16 noisy targets | 13 reference patches |
| --- | --- | --- | --- |
| none | +0.017 ± 0.005 (2 / 9) | +0.018 ± 0.011 (4 / 8) | −0.001 ± 0.005 |
| 300 000 noise-free sounds | −0.013 ± 0.010 (6 / 5) | +0.017 ± 0.011 (3 / 8) | −0.000 ± 0.002 |
| the same, with the measured noise added to the neighbours | −0.015 ± 0.011 (6 / 5) | +0.012 ± 0.012 (4 / 8) | −0.000 ± 0.002 |
| 300 000 sounds, 10 % noise waveforms (30 % noisy) | −0.016 ± 0.008 (8 / 4) | +0.010 ± 0.010 (3 / 8) | −0.003 ± 0.003 |
| **the 300 000 regular sounds and 300 000 noise-free ones** | **−0.014 ± 0.010 (8 / 3)** | **−0.003 ± 0.003 (1 / 1)** | **−0.003 ± 0.002** |

The targets are held-out random FMX6 patches, tonal and noisy, and the reference patches. Fewer noisy sounds help the
tonal targets and lose the noisy ones; a library's own noise operators fit a noisy target better than measured noise
added afterwards. Adding noise-free sounds keeps both, so the FMX6 and Nome6 libraries have them (measured on FMX6
library version 3; Nome6 not measured; the FMX6 library is at version 8 now, whose formant sounds are rich sources).
Part of the gain may be the larger library alone, but a 300 000-sound library with fewer noisy sounds gains as much
on the tonal targets. The noisy targets come from the generator of the regular sounds, the favourable case for them.

## Performance

BenchmarkDotNet (`benchmarks/HearSay.Benchmarks`, classes `RenderBenchmarks` and `EvaluationBenchmarks`, short-run
job) on an Intel Core i7-13700KF (16 cores, 24 threads), .NET 10, 48 kHz:

| Offline render (FM7-compatible) | Mean | Renders per second, one thread | Allocated |
| --- | --- | --- | --- |
| 1 s, 1 operator | 0.23 ms | 4270 | 192 KB |
| 1 s, 6 operators, sparse matrix (3 edges) | 1.20 ms | 835 | 209 KB |
| 3 s, 6 operators, sparse matrix (3 edges) | 3.55 ms | 282 | 619 KB |
| 1 s, 6 operators, dense matrix (36 edges) | 2.46 ms | 406 | 215 KB |
| 1 s, 6 operators, feedback-heavy (15 edges) | 1.60 ms | 627 | 211 KB |

The allocation of a render is its output buffer (192 KB per second of audio) plus the per-block control arrays; the
sample loop allocates nothing. A one-second note renders about 800 times faster than real time.

| Candidate comparison, 1 s note | Mean | Allocated |
| --- | --- | --- |
| coarse (long-term spectrum, rough spectrum, envelope) | 1.2 ms | 1.1 MB |
| normal (two STFT resolutions, Mel, partial set, trajectories, envelope) | 5.9 ms | 2.7 MB |
| detailed (normal plus pitch trajectory) | 11.2 ms | 2.7 MB |

| Population of 16 candidates: render and normal comparison | Mean | Evaluations per second |
| --- | --- | --- |
| one thread | 114 ms | 140 |
| all cores | 17.6 ms | 910 |

A complete search with the default budget of 20000 evaluations takes 67 to 117 seconds on this machine, depending on
the target (see the table above), which is 165 to 285 evaluations per second end to end; in September 2026, before the
structure library and the later search stages, it took 25 to 45 seconds.

These numbers are for the FM7-compatible renderer (September 2026). The FMX6 renderer adds Fourier series and
filters, and Nome6 renders at twice the rate, so both are slower per render; their library builds
([Structure library](#structure-library)) show by how much. A target set costs one render per note: three notes took
224 s for 20 000 evaluations ([Target sets](#target-sets-several-notes-one-patch)).

## Limitations of this version

This version does **not** search:

- more than six operators (all three SuperCollider profiles have exactly six);
- non-sine waveforms under the FM7 profile (FMX6 has band-limited triangle, saw, square and pulse, white,
  sample-and-hold and filtered noise; Nome6 has Jacobi elliptic operators and a shared noise);
- wavefolders, ring-modulation operators, filter operators (a filter as an operator of its own) and filter FM (a
  filter frequency moved at audio rate);
- audio-rate changes of the routing, or processing between modulation edges other than the FMX6 operator filters;
- backend-specific effects such as chorus, reverb or filters after the mix. Under FMX6 every operator may have a
  filter of its own, which the search proposes and tunes (see [Operator filters](#operator-filters)).

These are constraints of the synthesis profile, not of the architecture. Further limits worth knowing:

- With a single note, velocity sensitivity, key scaling and the choice between a ratio and a fixed operator cannot be
  told apart; the search leaves them neutral. A target set of several pitches or velocities makes them searchable, at
  the price of one render per note per candidate.
- The target should be dry and monophonic. Reverb tails and chorus are not part of the model and will be approximated
  by envelopes and detuning as far as that goes.
- Search envelopes have an attack, two segments up to the sustain point and a release. The model and the export take
  any number of points.
- Strong feedback is chaotic: the reference renderer and FM7 stay the same sound, but the waveforms can drift apart
  sample by sample. The equivalence tests therefore judge such patches by distance, not by sample difference.
- Matching is an approximation. Expect a good structural and temporal likeness rather than a clone, especially for
  targets that were not made by phase modulation.
- Noise is matched by spectrum and envelope, never by waveform: the objective is sensitive to the noise sequence, so
  even the correct noise structure keeps a loss floor (about 0.08 on a white-noise burst). Within that floor the
  search does find the noise operator (see the FMX6 results above).
- Filters (FMX6): the search uses F1 to F3 of a formant filter; F4 and F5 come only from the editor. Type and slope
  change only by a structural step, and a sweep is a control-rate envelope, not audio-rate modulation. An export with
  filters needs the FMX6 plugin with operator filters, and one with more than two formants or an own Q on one of them
  needs the five-formant build (the header of the export says so).
- The structure library is looked up from the reference note alone, and its patches have no fixed operators, no loops
  through several operators and no key scaling. For a target set it proposes the sound of one note; key scaling,
  velocity and fixed operators come from the search.

## How a custom UGen plugs in: FMX6

The [FMX6 UGen](https://github.com/shimpe/fmx6) is FM7 with a waveform, a shape and a harmonic limit per operator:
band-limited triangle, saw, square and pulse as additive Fourier series whose harmonic count follows the instantaneous
frequency (including the phase modulation) so that nothing lands above 45 % of the sample rate, plus white noise and
sample-and-hold noise from a deterministic xorshift generator seeded by operator slot, and filtered noise (white
noise through a TPT state-variable filter whose cutoff is the operator frequency). It plugged in as described
below: a profile (`supercollider-fmx6`), the `Fmx6CompatibleOperatorMatrixRenderer` (a port of the UGen's inner loop,
without FM7's warm-up sample), the `SuperColliderFmx6Exporter`, and a `.shape` parameter in the schema that is active
for pulse operators (and a `.filter` parameter, the response of a filtered-noise operator, not to be confused with the
`.operatorFilter.*` parameters of the operator filters); both are tuned by the optimizer like any other
spectral parameter. Several prior providers seed the waveforms: the waveform-variant provider re-colours the output
operators of the sine hypotheses (never into noise, which would turn every structure into the same noise);
`ModulatorWaveformPriorProvider` gives one modulator at a time of the best sine structures another waveform, since a
modulator with harmonics puts its sidebands on the same grid as a sine at the same ratio; and
`HarmonicWaveformPriorProvider` reads the partial cloud (all harmonics falling as 1/n: saw; odd ones only: square or
triangle; the notches of a duty cycle: pulse) and proposes the matching waveform alone at the fundamental with several
harmonic limits, then with a light sine modulator, plus noise hypotheses (a noise modulator and a parallel noise burst,
at several burst lengths and levels). For targets with at least 10 % noise, `NoisePriorProvider` adds to the twelve
best sine structures, and to the twelve best partial-track structures, one filtered-noise operator at the measured
colour (band-pass at the centroid, plus high-pass below a high one or low-pass above a low one), or white noise, at up
to three times the loudest operator. The topology mutator swaps waveforms, halves or doubles the harmonic limit of a
band-limited operator and can add a noise wash in one step during the search. The random-patch equivalence test
(`Fmx6Tests`) and the filter test (`Fmx6FilterTests`) run against the real UGen: patches without feedback loops are
sample-exact; looped ones part ways a little sooner than with sine operators, because a one-ulp difference in a
control signal can change the harmonic count of a sample, but stay the same sound. Search it from the CLI with
`--profile supercollider-fmx6` or pick **FMX6** as the target in HearSay Studio. FMX6 also has a filter per operator,
which plugged in the same way (see [Operator filters](#operator-filters) below).

A backend with other waveforms, its own anti-aliasing or new operator modes needs:

1. **A profile**: a `SynthesisProfile` in `SynthesisProfiles.All` whose capabilities list its waveforms and modes
   (and a capability flag for anything that is neither, as `SupportsSharedNoise` and `SupportsOperatorFilters` are),
   with a `StructureLibraryVersion` of its own.
2. **A renderer** implementing `IOperatorMatrixRenderer` for those capabilities, and `IVoiceRenderer` if it is to be
   played live from the Studio's keyboard. The generic equation
   `output_i[n] = amplitude_i[n] · waveform_i(phase_i[n] + Σ_j M[i,j] · output_j[n−1])` and the control-rate compiler
   are shared. A backend-exact renderer ports the UGen's inner loop (as `Fmx6Voice` and `Nome6Voice` do), while the
   experimental renderer adds waveforms in `WaveformKernel`. Oversampling and band-limiting stay inside the renderer.
3. **An exporter** implementing `IPatchExporter` that writes the same `OperatorMatrixPatch` for the new UGen.
4. **Priors** for what is new: `ISynthesisPriorProvider` implementations such as a harmonic-waveform provider
   ("all harmonics falling evenly suggests a saw-like operator, odd harmonics only a square-like one"). The partial
   cloud, the tracks and the trajectories they need are already in `TargetAnalysis`.
5. A branch in `SynthesisBackends.RendererFor` and `ExporterFor`, and in `ReadingProvidersFor` for its priors, to
   connect the four; and an entry in the Studio's lists of synthesis targets (Search, Library, live playing) and in the
   usage text of the CLI.

What does **not** change: target analysis, objective, regime keys, basins, the coordinator, CMA-ES, history and local
modelling, the result model, the writer and the command-line parsing; the parameter schema only gains the parameters
of what is new, guarded by its capability. This is not a promise but a tested property:
the experimental sine-and-triangle profile runs through the unchanged coordinator, its basins separate by waveform,
its best patch for a triangle target contains a triangle operator, and the FM7 validator, renderer and exporter all
reject that patch (`FutureWaveformProof_…` in the integration tests).

### Operator filters

Every FMX6 operator may have a filter between its waveform and its amplitude (`OperatorPatch.Filter`, an
`OperatorFilter`), a TPT state-variable filter in double precision as in the UGen. What the operator sends, to the mix
and to the operators it modulates, is the filtered signal: a low-pass on a saw modulator softens the modulation, a
filter on a carrier shapes the heard sound. The [expert manual](expert-manual.md#operator-filters-fmx6) describes them
for use, with the vowel presets of the editor; this section is the design.

| Type (code in `filters:`) | Frequency | Other values |
| --- | --- | --- |
| low-pass, high-pass, 2 or 4 poles (1 to 4) | `CutoffRatio` × the note, 0.25..64 | `Q` 0.5..20 |
| band-pass, notch (5, 6) | `CutoffRatio` × the note, 0.25..64 | `Q` 0.5..20 |
| peak (7) | `FixedHz`, in hertz | `Q` 0.5..20, `PeakGainDb` −24..24 |
| formant (8) | F1 = `FixedHz`; F2 to F5 = F1 × their ratio, 1..32 | `Q` 0.5..50 for F1 and every formant without a Q of its own; per formant a level relative to F1 (0..1) and its own Q (0.5..50, 0 for the filter's `Q`) |

A formant filter has up to five band-pass peaks of unity gain. F3 to F5 at level 0 are off, and their fields (and the
own Q of F2) are left out of the JSON at their defaults, so a two-peak filter keeps the serialized form it had before
there were five. An optional `Envelope` moves the frequency, `frequency × 2^(EnvelopeOctaves × env)` with
`EnvelopeOctaves` −4..4, at control rate. Key-tracked frequencies follow the note; peak and formant frequencies stay
fixed in hertz, as formants and body resonances do.

The filters plugged in like a waveform: a capability (`SupportsOperatorFilters`, refused elsewhere with the
`operator-filter` issue), `OperatorFilterKernel` in the renderer, `filters:` in the export only when an audible
operator has a filter (rows of 7 fields, or 17 when a formant filter has an own Q on F2 or F3 to F5 on), the
`.operatorFilter.*` parameters in the schema, the filter type, slope and sweep in the regime key, and whether a filter
is heard in the structural family of a basin. A filter adds 0.05 to the complexity and its envelope another 0.03, so between two
patches that sound alike the one without wins.

How the search gets filters:

- **Proposed.** `FilterPriors` reads the spectral envelope from the harmonic levels. `FormantPriorProvider` takes its
  lowest three maxima (at most 36 dB below the strongest) as F1 to F3, each with a resonance fitted through its
  harmonic and the two next to it, and puts a saw and a 20 % pulse, both with 64 harmonics, through formant filters on
  F1 to F3, on the two strongest, on the strongest alone and on F1 and F3. `SubtractivePriorProvider` puts a saw or a
  square through a low-pass at the roll-off of the harmonics, with 4 poles when they fall 18 dB per octave or faster,
  and with a sweep when the brightness moves during the note.
- **Mutated.** Structural steps, screened like the others: add a low-pass, high-pass, band-pass, peak or formant
  filter with starting values from the target, remove a filter, switch 2 and 4 poles or low-pass and band-pass, move a
  formant filter's F3 to the target's third formant, add the sweep read from the brightness. Type and slope change in
  no other way.
- **Tuned.** The `.operatorFilter.*` parameters are active only on an audible operator with a filter, and only those
  its type uses: cutoff ratio, F1 or the peak frequency (80..8000 Hz, widened to a value the patch already has), `Q`,
  the peak gain, the sweep depth and the sweep envelope, and on a formant filter F2 (ratio up to 8 × F1, level, own Q)
  and F3 (ratio up to 16 × F1, level from 0.001, own Q), widened up to 32 for a patch that has more. The own Qs are
  searched relative to `Q`, 1/8 to 8 times it, so that moving `Q` alone moves a formant without a Q of its own with it.
  A formant filter with F3 off starts from a default F3 (3 × F1, or 1.4 × F2 when that is higher, at level 0.001) that
  is switched on only once the search moves it. The search never sets F4 or F5; they come from the editor.

A formant filter only shapes the harmonics it is given: through a sine or a triangle a vowel is hardly audible. In the
library and in every structural step of the search (`VoiceSource.AsRichSource`) an operator with a formant filter is
therefore a *rich source*: a saw, or a pulse at its own width, keeps its waveform with at least 64 harmonics; a sine,
triangle, square or elliptic operator becomes the voice source; noise stays noise, a whisper. The voice source
(`VoiceSource.AsVoiceSource`) is a pulse 1 % wide with at least 64 harmonics and no m noise, whose harmonics are all
about as strong. The search does not use it for a saw or a pulse because a benchmark of eleven vowel targets matched
32 % worse when every formant operator was the 1 % pulse; the editor gives it, as the vowel presets' formant levels
assume a flat source. Filters are sample-exact against the UGen (`Fmx6FilterTests`).

## A second custom UGen: Nome6

The Nome6 UGen keeps the FM7 matrix and makes every operator a Jacobi elliptic oscillator: an odd-harmonic Fourier
series in the nome q of the elliptic parameter m, at most 24 terms, cut below 47.5 % of the internal sample rate and
renormalized to a peak of one. m = 0 is a sine; towards m = 0.999999 the harmonics get stronger (harmonic 2n + 1 of sn
has the level q^(n + 1/2) / (1 − q^(2n + 1)), so q is about 0.04 at m = 0.5, 0.26 at m = 0.99 and 0.55 at the top),
and a family control crossfades from sn to the peak-normalized sd, whose harmonics alternate in sign. The whole matrix
runs at twice the host rate (one internal sample of delay per edge) and is decimated by a 31-tap half-band filter. A
noise source shared by all operators (xorshift white noise through a one-pole crossover, coloured from low-passed
through white to high-passed) can be added to an operator output, jitter its phase, or move its m around the set value.

It plugged in along the five steps above, with these choices:

- **Patch model.** One new waveform, `Elliptic`, whose `Shape` is m (0..`OperatorPatch.MaxEllipticM`) and whose new
  `Morph` is the sn-to-sd family. A sine operator is exported with m = 0, so every FM7 patch is also a Nome6 patch
  (it sounds a little different: 2x oversampling, controls held per block instead of ramped). The shared noise is a
  patch-level `SharedNoise` (`Color` −1..1, `CutoffHz`); a `Noise` operator plays it with its amplitude envelope (the
  exporter moves the amplitude into the row's noiseAmp field), and two new operator fields roughen the others:
  `NoisePhase` (radians per unit of noise) and `NoiseM` (m excursion, −1..1, elliptic operators only). A capability,
  `SupportsSharedNoise`, keeps those fields away from FM7 and FMX6 (`shared-noise` compatibility issue).
- **Profile, renderer, exporter.** `supercollider-nome6` (Sine, Elliptic, Noise, shared noise);
  `Nome6CompatibleOperatorMatrixRenderer`, a port of `Nome6DSP.hpp` (the eleventh-order sine polynomial, the
  coefficient tables, the recurrence, the m interpolation of the noise, the decimator); `SuperColliderNome6Exporter`,
  which writes the nine-field rows (`bandwidth` is always 0, the anti-alias ceiling of the UGen) and `noiseColor:` and
  `noiseCutoff:`.
- **Search.** `.elliptic` is searched along the nome (`ParameterTransforms.EllipticNome`): equal steps change the
  fall-off of the harmonics equally, where m crowds all audible change into its last thousandths. `.morph` is linear.
  `noise.color` and `noise.cutoff` are active once an audible operator uses the noise; `.noisePhase` and `.noiseM` only
  on an operator that a mutation or a prior has roughened, so clean operators do not widen the search space. The
  topology mutator turns operators elliptic, steps an elliptic one brighter or darker by a factor 1.6 in q, adds a
  wash of shared noise at the measured colour, and roughens an operator of a noisy target.
- **Priors.** `HarmonicWaveformPriorProvider` fits a ladder of nomes to an odd-only partial cloud and proposes the best
  one alone, in sn and sd, and with its neighbours; `ModulatorWaveformPriorProvider` tries elliptic modulators;
  `NoisePriorProvider` adds the shared noise coloured towards the centroid of the noise of the target (a fully
  low-passed one-pole noise has its centroid near three times the cutoff; white noise at 48 kHz near 11 kHz).

Against the real UGen (`Nome6Tests`): tonal patches are sample-exact (sine, sn, sd and a modulated pair with feedback
to within 1e-17; 24 of 25 random patches to within 1e-3 of their peak, the 25th has a chaotic feedback loop and stays
the same sound). Noise cannot match sample by sample: the plugin seeds its generator from the address of the unit,
so every synth plays another sequence, and the renderer uses the default seed of the engine. Pure noise agrees in level
and centroid within a few percent over five colours (the test allows 8 %: the centroid of a dark noise wanders most
from one sequence to the next), and tones with a noise burst, a noise modulator, or phase or m roughness come out at
an overall distance of 0.004 to 0.025. Search it from the CLI with `--profile supercollider-nome6` or pick
**Nome6** (or **All**) as the target in HearSay Studio.

On 30 random Nome6 patches as hidden targets (budget 4000, two seeds, each searched under Nome6 and under FM7),
Nome6 ends at a mean audio loss of 0.129 (median 0.107) against 0.177 (median 0.169) for FM7, better on 25, worse on 3:

| Family | Nome6 | FM7 | Nome6 better / worse / equal |
| --- | --- | --- | --- |
| default: all waveforms, 30 % of patches roughened | 0.106 | 0.161 | 9 / 0 / 1 |
| sparse: at most three operators | 0.085 | 0.157 | 10 / 0 / 0 |
| tonal: sine and elliptic only | 0.194 | 0.212 | 6 / 3 / 1 |

Noise and a single elliptic operator are found reliably (a noise operator plus a sine at 0.004, an elliptic carrier
with a sine modulator at 0.028). Dense tonal patches of four to six operators are the hard case, for both profiles:

- The random generator makes many of them chaotic (feedback loops through two operators at several radians, fixed
  operators at 35 Hz or 4 kHz, carriers at half the note). Both profiles end between 0.25 and 0.37 there, and five
  times the budget only takes four of them from about 0.35 to 0.30: the search settles on another structure, it does
  not run out of evaluations (on other sounds a higher budget helps more, see [Round trips](#round-trips)).
- On 20 dense patches as a musician would make them (four to six operators, sine and elliptic, no loops, no fixed
  operators, every heard operator at or above the note, a carrier on it, modulation up to 2 radians) Nome6 ends at
  0.202 and FM7 at 0.195; paired per target the difference is +0.007 ± 0.006, so the two are level. Searching these
  with the Nome6 renderer and sine operators only gives 0.191, with the FM7 priors 0.191, without the shared noise
  0.198. The waveform priors cost at most about 0.01 here, but they are what finds an elliptic carrier on a sparse
  tonal patch (0.035 against 0.085 without them, 0.041 against 0.070), so they stay. Screening 256 instead of 128
  hypotheses, merging the waveform providers into one group, and a prior for an odd series on any base (an elliptic
  operator at ratio b puts its partials on b, 3b, 5b, …) changed nothing beyond the noise of the benchmark.
