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
profile, not its identity: nothing outside the FM7 profile, the FM7-compatible renderer, the FM7 exporter and the
sine phase-modulation prior provider knows about sine waves or about FM7.

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
- [Results](#results)
- [Performance](#performance)
- [Limitations of this version](#limitations-of-this-version)
- [How a future custom UGen plugs in](#how-a-future-custom-ugen-plugs-in)

## Quick start

Command line:

```bash
dotnet run -c Release --project src/HearSay.Cli -- match target.wav \
    --profile supercollider-fm7 --budget 20000 --output result/

dotnet run -c Release --project src/HearSay.Cli -- analyze target.wav   # target analysis as JSON
dotnet run -c Release --project src/HearSay.Cli -- profiles             # list synthesis profiles
```

`--note <midi>` or `--f0 <hz>` supply the pitch when it is known; otherwise it is estimated. `--seeds <n>` runs n seeds
and keeps the best, which is the simplest remedy against an unlucky run. `--sample-rate` should be
the rate the exported patch will run at, because the aliasing of the backend is part of its sound.

```text
result/
    patch.json            canonical OperatorMatrixPatch
    preview.wav           offline render of the best patch
    patch.scd             SuperCollider SynthDef (needs sc3-plugins for FM7, nothing of HearSay)
    analysis.json         what was measured on the target
    comparison.json       full HearSay diagnosis of preview against target
    search-summary.json   stages, basins, budget, seed
    alternatives/
        01-patch.json  01-preview.wav  01-patch.scd
        02-…
```

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
      Spectral/                  + spectral trajectories, band-energy trajectories, LTAS        (new)
      Envelope/                  + multi-stage decay                                            (new)
      Analysis/                  AudioTarget, TargetAnalysis, TargetAnalyzer                    (new)
      Objective/                 composite audio loss with evaluation levels                    (new)
  HearSay.Modeling/              what effects have parameter changes had on audio features?
      History, Effects, Sensitivity, Prediction, Guidance, Parameters, ToneOptimizationSession  (existing, moved)
  HearSay.Synthesis/             how is a canonical patch rendered?
      Patches/                   OperatorMatrixPatch, OperatorPatch, PhaseModulationMatrix, envelopes, pitch model
      Profiles/                  SynthesisCapabilities, SynthesisProfile(s), PatchCompatibilityValidator
      Rendering/                 IOperatorMatrixRenderer, RenderRequest, ExtendedOperatorMatrixRenderer
      Compilation/ Envelopes/ Oscillators/ Export/
  HearSay.Synthesis.Fm7Compatible/   reference renderer with FM7 semantics
  HearSay.SuperCollider/         FM7 exporter, SuperCollider NRT renderer, backend-equivalence validation
  HearSay.OperatorMatrix/        which synthesis structure and parameters should be explored?
      Parameters/                schema, hierarchy, conditional activity, transforms, ties, active sets
      Canonicalization/          regime keys, canonical operator order, complexity
      Priors/                    ISynthesisPriorProvider, sine-PM provider, temporal priors
      Optimization/              CMA-ES
      Basins/ Search/            basins, topology mutation, evaluator, coordinator, local refiner
  HearSay/                       facade: OperatorMatrixPatchMatcher, SynthesisBackends, result writer
  HearSay.Cli/                   command line
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

`OperatorMatrixPatch` is the single source of truth. The optimizer, the reference renderer, the JSON format and every
exporter read the same record; there is no second representation.

```csharp
public sealed record OperatorMatrixPatch(
    IReadOnlyList<OperatorPatch> Operators,
    PhaseModulationMatrix ModulationMatrix,
    IReadOnlyList<double> OutputLevels,
    PitchModel Pitch);

public sealed record OperatorPatch(
    bool Enabled,
    OperatorMode Mode,                  // PhaseModulation; ring modulation, wavefolder, filter… are reserved
    OperatorWaveform Waveform,          // Sine, Triangle, Saw, Square, Pulse, Noise, Additive, Wavetable
    OperatorFrequency Frequency,        // Ratio or Fixed
    double FineTuneCents,
    double DetuneHz,
    double InitialPhase,
    OperatorEnvelope AmplitudeEnvelope,
    double VelocitySensitivity,
    KeyScaling KeyScaling);
```

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
  The same type serves operator amplitudes, modulation-edge amounts (`M[i,j](t)`, control rate) and pitch. They are
  not limited to four rates and four levels.
- **Pitch model.** A pitch envelope in cents (initial offset, pitch decay, drift) and a sine vibrato with rate, depth,
  delay and fade-in.
- The amplitude envelope of an operator scales everything it sends, its modulation as well as its contribution to the
  mix. That is how FM7 works, and it is what makes a decaying modulator a decaying modulation index.

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
    double MaxModulationRadiansPerDestination = 24.0);
```

| Profile | Operators | Modes | Waveforms | Status |
| --- | --- | --- | --- | --- |
| `SynthesisProfiles.SuperColliderFm7` (`supercollider-fm7`) | exactly 6 | PhaseModulation | Sine | searchable, exportable; full matrix, self and circular feedback, ratio and fixed, independent outputs, dynamic matrix, backend-exact rendering |
| `SynthesisProfiles.ExperimentalSineTriangle` | 6 | PhaseModulation | Sine, Triangle | searchable with the extended renderer, no export backend; exists to prove the architecture |
| `SynthesisProfiles.HearSayExtended` | 6 | PhaseModulation | all | reserved for a future engine; no renderer yet, so the matcher refuses it |

Three mechanisms keep a search inside its profile:

1. Prior providers receive the capabilities and the coordinator drops every hypothesis the validator rejects.
2. `TopologyMutator` only proposes what the capabilities allow, and validates each proposal.
3. The parameter schema offers structural choices from the profile only. For FM7 the waveform parameter of an operator
   has exactly one choice; cells of the matrix the backend lacks do not exist in the schema.

`IPatchCompatibilityValidator` answers whether a backend can play a patch, with machine-readable issues
(`unsupported-waveform`, `self-feedback`, `modulation-range`, …). Renderers and exporters refuse incompatible patches
with a `PatchNotSupportedException`; nothing is approximated silently. A renderer that can do less than a profile asks
for is rejected before the search starts.

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
operators and live edges remain, all control signals are evaluated per block, and the loop itself is free of
allocations, LINQ, dictionaries and virtual calls.

## SuperCollider export

`SuperColliderFm7Exporter` (an `IPatchExporter`) writes a self-contained SynthDef. It uses the full matrix interface
`FM7.ar(ctlMatrix, modMatrix)`, never `FM7.arAlgo`. What was verified on the installed UGen before writing it:

| Question | Answer |
| --- | --- |
| `ctlMatrix` | 6 rows of `[frequency, phase, amplitude]`, all read at control rate |
| `modMatrix` orientation | `modMatrix[destination][source]`, the orientation of `PhaseModulationMatrix` |
| scaling | radians per unit of source output (source output includes its amplitude) |
| result | an array of the six operator outputs; mixed with `OutputLevels` |
| control-rate behaviour | all inputs are ramped linearly over each control block |
| self-feedback, circular feedback | both work, with the one-sample delay described above |

A complete generated file (the two-operator round trip from the results below):

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

### Native versus SuperCollider

`SuperColliderFm7Renderer` renders patches with a real scsynth in non-realtime mode, and
`BackendEquivalenceValidator` compares that with the reference renderer using HearSay itself
(`BackendExportValidation`: overall, spectral, envelope and partial distance, largest sample difference). This measures
reference renderer against backend, never target against candidate.

Measured with SuperCollider 3.14.1 and sc3-plugins 3.14.0 (`tests/HearSay.SuperCollider.Tests`):

| Test set | Patches | Equivalent | Sample-exact (< 0.1 % of peak) | Worst overall distance |
| --- | --- | --- | --- | --- |
| Random patches in six families: sparse, dense, self-feedback, circular, fixed and detuned, moving (edge envelopes, pitch envelope, vibrato); five notes each at different pitches, velocities and note lengths that do not fall on a block boundary | 30 | 30 | 29 | 0.00003 |
| The twelve reference patches | 12 | 12 | 12 | 0.00000 |
| Best patch of every round trip below (`--supercollider`) | 12 | 12 | — | 0.00000 |
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
            + wEnvelope·Envelope + wPitch·PitchTrajectory + wLtas·Ltas
  totalLoss = audioLoss + complexityWeight · patchComplexity
  ```

  The complexity term (operators, edges, tiny edges and outputs, extra envelope points, extreme detuning) is a
  tie-breaker with a default weight of 0.02; the audio loss dominates.
- **How does this patch differ?** The existing HearSay feature vector and `ToneComparisonResult`. Local refinement
  proposes steps from the Jacobian of that vector and lets the audio objective decide whether to accept them. A
  feature can steer the local model without being a term of the objective, and the other way round.

Evaluation is progressive: `Coarse` (long-term spectrum, rough spectrum and envelope) sorts out hypotheses, `Normal`
(everything except pitch, unless the target pitch moves) drives the optimization, `Detailed` adds the pitch trajectory
for finalists. Comparison is timbre-normalized by default; `ObjectiveOptions.TimbreNormalized = false` makes the
absolute level part of the loss.

## Parameters: structural and continuous

`PatchParameterSchema.Build(patch, capabilities)` lists every searchable property with an id, a kind, a transform, a
range, a position in the hierarchy and an activity rule.

```text
Patch
├─ Operator 1
│  ├─ op1.enabled · op1.mode · op1.waveform                       structural
│  ├─ Frequency: op1.frequency.mode · op1.frequency.ratio          structural
│  │             op1.frequency.fixedHz · op1.fineTune · op1.detune · op1.phase
│  ├─ Amplitude Envelope: op1.env.p1.time … op1.env.p4.level, op1.env.breakPosition
│  └─ Output: op1.output.enabled (structural) · op1.output.level
├─ Operator 2 … 6
├─ Modulation Matrix
│  └─ OP2 -> OP1: edge.2>1.enabled (structural) · edge.2>1.amount · optional amount envelope
└─ Pitch Model: pitch.env.* · pitch.vibrato.rate/depth/delay/fadeIn
```

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
  frequency, active edges and output topology. `PatchCanonicalizer` brings relabelled twins into one operator order.
  Local models are stored per regime (`RegimeModelStore`): a Jacobian learned with a sine in OP3 is never used once
  OP3 is a triangle, and one learned with an edge present is not used once the edge is gone.

## The search

`OperatorMatrixPatchSearchCoordinator` owns the flow and no DSP:

| Stage | What happens | Level | Budget (default 20000) |
| --- | --- | --- | --- |
| A. Target analysis | everything in the table above; render request from fundamental, note-off and tail | — | 0 |
| B. Structural hypotheses | prior providers propose up to 128 structures; each is screened at three modulation scales | Coarse | 5 % |
| Survivors | the best 32 get a short continuous optimization; duplicates by canonical regime, frequency structure and loss terms are dropped; 6 distinct basins remain, the best of every structural family first, because early losses are deceptive: an additive structure sounds roughly right at once, a deep stack only after its amounts have been found | Normal | 15 % |
| C–E. Deep optimization | three rounds of successive halving (6 → 4 → 3 basins). Per basin: CMA-ES over everything smooth at once for small patches, otherwise one connected operator group at a time. A basin keeps its run between rounds, so the adapted covariance is never thrown away; only a stagnated run is replaced, by one with twice the population that starts alternately from the best patch and from where the basin began. A basin that stagnates also tries three topology mutations; a better child replaces its parent | Normal | 35 % |
| Finalists | one long run each, the best finalist getting the longest; then a round of structural variants per finalist: every possible new edge and new modulator is screened at a few amounts (about a hundred renders), the most promising variants are optimized, and a variant that beats its parent takes its place | Normal | 75 % of the rest |
| F. Local refinement | probe each smooth parameter, regime-specific Jacobian of the HearSay feature vector, trust-region guidance steps accepted only when the objective agrees; then a narrow final polish | Detailed | the rest |

- **Sine-FM knowledge is a plug-in.** `SinePhaseModulationPriorProvider` is the only place with sideband arithmetic:
  it ranks carrier and modulator ratios by how well `|c ± k·m|` covers the partial cloud, estimates a starting index
  from Carson's rule `BW ≈ 2(I + 1)·fm`, and builds stacks, three-stacks, parallel modulators, greedy multi-stack
  covers, additive sets, feedback operators and detuned pairs for beating targets. These formulas only rank guesses;
  nothing treats them as an inverse of a six-operator network. Envelope and pitch starting values come from
  `TemporalPriors`, which knows nothing about synthesis. `WaveformVariantPriorProvider` adds waveform variants for
  profiles with a choice.
- **Structure is never searched by gradients.** Structures come from hypotheses and from `TopologyMutator`, whose new
  elements start almost silent so that a variant begins as good as its parent (add or
  remove an edge, add an operator as modulator or into the mix, remove an operator, move a coarse ratio to its
  neighbour, toggle an output, change waveform or mode where the profile has a choice, ratio to fixed for very low
  operators). Every structure is its own basin with its own continuous optimization.
- **CMA-ES** (`CmaEs`, ask and tell, box-constrained by reflection) only ever sees smooth parameters.
- **Stagnation**: a run ends after 25 generations without 1 % progress *of that run*; the basin then restarts or
  mutates, or its budget goes to other basins. Restart counts and the fate of every basin are in the summary.
- **Budget, caching, reproducibility**: every render counts, at any level. Evaluations are cached by patch hash and
  level. Batches are evaluated in parallel with the budget assigned in input order, and all randomness derives from
  the seed, so a search is reproducible and independent of the degree of parallelism.
- **Identifiability**: many patches sound alike. The search optimizes audio similarity plus a small simplicity bias,
  never parameter equality, and canonicalization keeps relabelled twins from taking two basin slots.

## Results

### Round trips

`samples/HearSay.MatchDemo` renders each of the twelve reference patches to a target, hides the patch and lets the
matcher search with the default budget of 20000 renders and seed 1234. Nothing asks for the original parameters back:
many patches sound alike, so the measure is how much of the audio loss disappears. *Initial* is the best untouched
hypothesis at the normal level, *final* the best patch; both are audio losses without the complexity term. *Similarity*
is the HearSay similarity of the full diagnosis (1 is identical). *SC distance* is the HearSay distance between the
offline preview and the same patch played by the real FM7 UGen.

| Target | Hidden structure | Initial | Final | Gain | Similarity | Renders | Seconds | SC distance | Found structure (ratios; `a>b` = OPa modulates OPb) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `pure-sine` | one operator | 0.0101 | 0.0000 | 224× | 0.994 | 19116 | 27.7 | 0.00000 | 1 → out |
| `simple-2op` | 1 ← 2 | 0.1336 | 0.0051 | 26× | 0.907 | 19179 | 28.5 | 0.00000 | 1 → out; 2>1 **(same)** |
| `bright-fm` | 1 ← 1 ← 3 | 0.2165 | 0.0348 | 6.2× | 0.755 | 18864 | 35.2 | 0.00000 | 1 → out; 2(r1)>1, 3(r3)>2 **(same)** |
| `bell` | 1 ← 3.5 and 2.76 ← 5.4 | 0.2680 | 0.0835 | 3.2× | 0.605 | 18984 | 41.3 | 0.00000 | 1 → out; 2(r3.5)>1, 2>2 |
| `metallic-inharmonic` | 1 ← 1.41 and 1 ← 2.73 | 0.3120 | 0.1569 | 2.0× | 0.637 | 19165 | 31.9 | 0.00000 | 1 → out; 2(r1.41)>1, 3(r2.82)>1 |
| `electric-piano` | 1 ← 1 and 1 ← 14 | 0.1800 | 0.0090 | 20× | 0.823 | 18714 | 43.3 | 0.00000 | two outputs at 1; 3(r1)>1, 4(r14)>2 **(same)** |
| `bass` | 1 ← 1 with modulator feedback | 0.1641 | 0.0060 | 27× | 0.894 | 19178 | 42.3 | 0.00000 | 1 → out; 2(r1)>1, 2>2 **(same)** |
| `brass-like` | 1 ← 1 | 0.0790 | 0.0074 | 11× | 0.874 | 19135 | 34.2 | 0.00000 | 1 → out; 2(r1)>1 **(same)** |
| `multi-output` | 1 ← 2, the modulator also heard; 3 → out | 0.1061 | 0.0101 | 11× | 0.838 | 18495 | 31.8 | 0.00000 | six outputs at 1, 2, 3, 5, 7, 9; 2>6 (additive) |
| `detuned` | two 1 ← 2 stacks, 2.4 Hz apart | 0.1052 | 0.0623 | 1.7× | 0.716 | 18718 | 43.2 | 0.00000 | two outputs at 1, two modulators at 2, cross-routed |
| `feedback-heavy` | 1 ← 2, both with self-feedback | 0.4401 | 0.0372 | 12× | 0.828 | 18847 | 35.6 | 0.00000 | 1 → out with 1>1; 2(r2)>1, 3(r4)>1 |
| `circular-routing` | 1 ← 2 ← 3 and 1 → 3 | 0.1264 | 0.0395 | 3.2× | 0.792 | 19201 | 37.3 | 0.00000 | 1 → out; 2(r1)>1, 3(r2)>1, 2>2 |

Machine: Intel Core i7-13700KF, 24 logical cores, .NET 10. Median gain 11×; about 35 seconds per target.

How to read this:

- **Exact parameter recovery is not required and not claimed.** Six of the twelve hidden structures come back as they
  were (marked *same*), among them the three-operator stack of `bright-fm` and both stacks of `electric-piano`
  including its modulator at ratio 14. Elsewhere another structure explains the sound: six sine operators straight
  into the mix for `multi-output`, a feedback operator with two modulators for `feedback-heavy`. A different routing
  with the same partials is a correct answer, and the simplicity bias prefers the smaller one.
- The loss that the hidden patch itself gets, rendered with the *estimated* note length, is between 0.000 and 0.03 for
  all twelve targets. That is the floor of the measurement; what remains above it is search.
- Simple stacks, basses, brass-like, electric-piano-like and feedback sounds are matched closely. Dense inharmonic
  spectra (`metallic-inharmonic`, `bell`) and beating pairs (`detuned`) are harder: the right family of structures is
  usually among the finalists, but the continuous landscape inside such a structure has many optima and the budget
  does not always find the good one.
- **Results vary with the seed, and with tiny changes of the input.** The electric-piano target read back from its
  16-bit WAV file instead of from memory gives 0.079 with seed 1234 and 0.021 to 0.045 with seeds 1 to 4; four of
  these five runs find the hidden structure. Every run is reproducible, so running several seeds and keeping the best
  is cheap to organise: `--seeds 4` on the command line, `MatchBestOf` in the API.
- Every one of these patches sounds the same in SuperCollider as in the preview (SC distance).

Example console output (`hearsay match electric-piano/target.wav --f0 262 --seed 2`):

```text
[   186/20000] hypothesis screening             best loss 0.1187
[  3363/20000] deep optimization round 1        best loss 0.0920
…
target      target.wav: f0 262.00 Hz, 2.10 s, 9 partials, inharmonicity 0.01
search      18776 renders in 44.2 s, profile supercollider-fm7, seed 2
audio loss  0.1803 (best untouched hypothesis) -> 0.0205
similarity  0.910 (HearSay overall distance 0.0314)
structure   op1:…/Sine/r1/out;op2:…/Sine/r1/out;op3:…/Sine/r1;op4:…/Sine/r14;|3>1;4>2;
origin      2 stacks 1:1 + 1:14

modulation matrix (rows: destination, columns: source, radians)
         OP1    OP2    OP3    OP4    OP5    OP6
  OP1      .      .    1.88     .      .      .          hidden patch: 1.60
  OP2      .      .      .    1.09     .      .          hidden patch: 1.20
```

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
      "velocitySensitivity": 0, "keyScaling": { "levelDbPerOctave": 0, "ratePerOctave": 0, "referenceMidiNote": 60 }
    },
    { "…": "OP2: ratio 2, its own envelope" }, { "enabled": false, "…": "OP3 to OP6" }
  ],
  "modulationMatrix": {
    "operatorCount": 6,
    "edges": [ { "sourceOperator": 1, "destinationOperator": 0, "enabled": true, "amount": 4.276, "amountEnvelope": null } ]
  },
  "outputLevels": [ 0.597, 0, 0, 0, 0, 0 ],
  "pitch": { "pitchEnvelope": null, "vibrato": null }
}
```

`preview.wav` is the offline render of that patch with the note of the target (f0, note-off and tail as analysed),
peak-normalized to -3 dBFS; `alternatives/` holds the other finalists in the same three formats.

### Round-trip tests

`tests/HearSay.IntegrationTests` run smaller searches (budget 9000) and assert a strong reduction of the audio loss,
never parameter equality: `simple-2op` 0.134 → 0.005, `brass-like` 0.079 → 0.007, `bell` 0.268 → 0.101. The same
project checks that a search is identical for the same seed whatever the degree of parallelism, that it differs for
another seed, that the budget is respected and accounted per stage, that a profile without feedback yields patches
without feedback even for a target that was made with it, and that unusable targets and reserved profiles are refused.

### Future-waveform proof

`FutureWaveformProof_WaveformIsStructuralAndBackendsRejectWhatTheyCannotPlay`: a target made with a triangle operator
is searched under `ExperimentalSineTriangle` with the unchanged coordinator, the extended renderer and the
waveform-variant provider. Sine and triangle structures end up in separate basins, the best patch contains a triangle
operator (`op1:PhaseModulation/Triangle/r1/out;…`, audio loss 0.031), and the FM7 validator, renderer and exporter
all reject that patch. Under the FM7 profile the same target is searched with sine operators only.

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

A complete search with the default budget of 20000 renders takes 25 to 45 seconds on this machine, depending on the
length of the target (see the table above), which is 430 to 690 evaluations per second end to end.

## Limitations of this version

The first version does **not** search:

- more than six operators (the FM7 profile has exactly six);
- non-sine waveforms in the production profile (triangle only in the experimental profile);
- wavefolders, ring-modulation operators, filter operators, filter FM, noise operators;
- audio-rate changes of the routing, or processing between modulation edges;
- backend-specific effects such as chorus, reverb or filters after the mix.

These are constraints of the synthesis profile, not of the architecture. Further limits worth knowing:

- One note is matched. Velocity sensitivity, key scaling and the choice between a ratio and a fixed operator cannot be
  told apart on a single note; they are part of the model, the renderer and the export, and the search leaves them
  neutral. Matching several notes at once is the natural next step.
- The target should be dry and monophonic. Reverb tails and chorus are not part of the model and will be approximated
  by envelopes and detuning as far as that goes.
- Search envelopes have an attack, two segments up to the sustain point and a release. The model and the export take
  any number of points.
- Strong feedback is chaotic: the reference renderer and FM7 stay the same sound, but the waveforms can drift apart
  sample by sample. The equivalence tests therefore judge such patches by distance, not by sample difference.
- Matching is an approximation. Expect a good structural and temporal likeness rather than a clone, especially for
  targets that were not made by phase modulation.

## How a future custom UGen plugs in

A backend such as `HearSayOperatorMatrix.ar(...)` with arbitrary waveforms, its own anti-aliasing and new operator
modes needs:

1. **A profile**: a `SynthesisProfile` whose capabilities list its waveforms and modes.
2. **A renderer** implementing `IOperatorMatrixRenderer` for those capabilities. The generic equation
   `output_i[n] = amplitude_i[n] · waveform_i(phase_i[n] + Σ_j M[i,j] · output_j[n−1])` and the control-rate compiler
   are already shared; `WaveformKernel` is where waveforms are added. Oversampling and band-limiting stay inside the
   renderer.
3. **An exporter** implementing `IPatchExporter` that writes the same `OperatorMatrixPatch` for the new UGen.
4. **Priors** for what is new: `ISynthesisPriorProvider` implementations such as a harmonic-waveform provider
   ("all harmonics falling evenly suggests a saw-like operator, odd harmonics only a square-like one"). The partial
   cloud, the tracks and the trajectories they need are already in `TargetAnalysis`.
5. One line in `SynthesisBackends` to connect the four.

What does **not** change: target analysis, objective, parameter schema, regime keys, basins, the coordinator, CMA-ES,
history and local modelling, the result model, the writer and the CLI. This is not a promise but a tested property:
the experimental sine-and-triangle profile runs through the unchanged coordinator, its basins separate by waveform,
its best patch for a triangle target contains a triangle operator, and the FM7 validator, renderer and exporter all
reject that patch (`FutureWaveformProof_…` in the integration tests).
