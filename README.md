# HearSay

HearSay is a C#/.NET library that compares two recordings of **the same musical note** — a `target` (the sound you
want) and a `candidate` (the sound a synthesizer produced) — and explains *how* they differ.

It does not stop at a similarity score. The main output is a structured, machine-readable diagnosis: "attack is 22 ms
too long", "the 2.5–6 kHz band has 8.8 dB too much energy", "harmonic 3 is 4 dB too weak", "the candidate is noisier".
That makes it usable as the **measurement and feedback layer of a closed-loop system** that adjusts synthesizer
parameters iteratively, driven by a classic optimizer or by an agent.

```text
target audio ─┐
              ▼
synth parameters → synth render → candidate audio → HearSay comparison → difference vector
      ▲                                                                        │
      └──────────────── optimizer / agent ◄── sensitivities, suggestions ◄─────┘
```

* No UI, no platform-specific code, no native dependencies, **no NuGet dependencies** (the FFT, resampler and WAV
  reader are part of the library).
* .NET 10, nullable reference types, XML documentation on every public API, warnings as errors.
* Deterministic: the same input and configuration always give the same output.
* Thread-safe core: all caches are immutable after initialization.

<img src="docs/images/hearsay-logo.png" alt="HearSay: six operators on a ring, joined by the chords of a modulation matrix, with a wave through them" width="96" align="right" />

**New here? Start with the [tutorial](docs/tutorial.md)**: eleven hands-on steps from a first comparison to a closed
loop that finds synth parameters, backed by a runnable sample. This README is the reference.

**HearSay can also close the loop by itself.** Given a monophonic target note it searches a playable six-operator
phase-modulation patch that approximates it, renders a preview offline and exports a SuperCollider `FM7` SynthDef:
`target.wav → patch.json + preview.wav + patch.scd`. The architecture is a generic *operator-matrix synth* solver;
FM7 is its first synthesis profile, and the [FMX6 UGen](https://github.com/shimpe/fmx6) with band-limited
triangle, saw, square, pulse and noise operators is the second (`--profile supercollider-fmx6`), and the Nome6 UGen
with Jacobi elliptic operators and a shared noise source the third (`--profile supercollider-nome6`); the target
selector in HearSay Studio picks one or all of them. See [Patch matching](#patch-matching) and the full
[patch-matching documentation](docs/patch-matching.md). The [expert manual](docs/expert-manual.md) is the
practical guide: preparing targets, target sets, choosing FM7, FMX6 or Nome6, budget and seeds, reading results, Studio,
the command line, the API, every search option, hand-editing patches and troubleshooting.

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [Conventions](#conventions)
- [What is measured](#what-is-measured)
- [Actionable differences and hints](#actionable-differences-and-hints)
- [Feature vectors and normalization](#feature-vectors-and-normalization)
- [Using HearSay in a synth parameter optimizer](#using-hearsay-in-a-synth-parameter-optimizer)
- [Patch matching](#patch-matching)
- [Architecture](#architecture)
- [Performance](#performance)
- [Demos](#demos)
- [Tests](#tests)
- [Limitations](#limitations)
- [License](#license)

## Installation

The library is not published to NuGet yet. Reference the project:

```bash
git clone https://github.com/shimpe/HearSay.git
# comparison only
dotnet add <your project> reference HearSay/src/HearSay.Audio/HearSay.Audio.csproj
# comparison + closed-loop modelling (history, sensitivities, guidance)
dotnet add <your project> reference HearSay/src/HearSay.Modeling/HearSay.Modeling.csproj
# everything, including the patch matcher
dotnet add <your project> reference HearSay/src/HearSay/HearSay.csproj
```

Build and test everything:

```bash
dotnet build HearSay.sln -c Release
dotnet test HearSay.sln
```

## Quick start

```csharp
using HearSay;
using HearSay.Audio;
using HearSay.Comparison;
using HearSay.Models;

var comparer = new ToneComparer();

ToneComparisonResult result = comparer.Compare(
    new AudioNote(targetSamples, sampleRate: 44100),
    new AudioNote(candidateSamples, sampleRate: 44100),
    new ToneComparisonOptions { ExpectedFundamentalHz = 440.0 });

Console.WriteLine(result.Scores.Overall);            // distance, 0 = perfect
foreach (ToneDifference d in result.Differences)     // sorted by importance
{
    Console.WriteLine($"{d.Feature} {d.Direction} {d.SignedDelta:+0.0;-0.0} {d.Unit} (importance {d.Importance:F2})");
}

string json = HearSayJson.Serialize(result);         // the complete analysis, for an agent
```

Input is mono `float` samples. The two signals may differ in length and in sample rate (the candidate is resampled to
the rate of the target). `WavFile.Read` loads PCM/float WAV files; stereo is downmixed unless you select a channel.

When many candidates are compared with one target, prepare the target once:

```csharp
PreparedTarget prepared = comparer.PrepareTarget(targetNote, options);
foreach (AudioNote candidate in candidates)          // also fine from Parallel.ForEach
{
    ToneComparisonResult r = comparer.Compare(prepared, candidate);
}
```

`PreparedTarget` holds everything that depends only on the target: the cleaned audio, the spectrograms at every
resolution with their log magnitudes, the log-Mel representation, the harmonic profile, the envelope, descriptors,
band energies and the feature schema. It is immutable, so any number of threads can compare against it.

## Conventions

These hold everywhere in the library.

| Convention | Meaning |
| --- | --- |
| `delta = candidate - target` | A **positive** delta means the candidate has *more* of something than the target: `HighBandDelta = +4 dB` means the candidate has 4 dB more high-frequency energy; `AttackDelta = +18 ms` means its attack is 18 ms slower. A **negative** delta means less. An optimizer tries to drive the whole difference vector to zero. |
| distance: `0 = perfect` | All scores in `SimilarityScores` are distances. They are scaled so that values around 1 mean "grossly different" and they are not clamped. The only exception is `Similarity = exp(-3 * Overall)`, where `1 = perfect`. |
| `Importance`, `Confidence` in `0..1` | Continuous values, never labels. Importance ranks differences; confidence says how far a measurement or learned relation can be trusted. |
| Feature ids are stable | `ToneFeatureIds` holds every id (`envelope.attack`, `spectral.centroid`, `spectral.band.high`, `harmonic.3`, ...). Histories and external systems depend on them, so they never change. |
| Parameters are normalized | Sensitivities and the Jacobian are expressed per *normalized* parameter unit: the full range of a parameter is 1. |

### Phase

Nothing in the comparison looks at waveforms sample by sample. Spectral measures use STFT **magnitudes**, harmonic
levels are the **power** summed over a spectral peak, and envelopes are RMS curves. Two sines with the same frequency
and amplitude but a different start phase are therefore (almost) identical to HearSay, and a complex tone whose
partials have been phase-rotated matches its original even though the waveforms look nothing alike. Sample-wise error
is deliberately not offered as a metric.

### Loudness versus timbre

`ToneComparisonOptions.LoudnessMode` makes the distinction explicit instead of normalizing everything away:

| Mode | Behaviour |
| --- | --- |
| `SeparateLoudness` (default) | Both signals are gain-matched before timbre analysis, and loudness is measured separately on the original signals. A candidate that is only 6 dB louder yields exactly one difference (`loudness`, `TooHigh`, 6 dB) and no timbre differences. |
| `TimbreOnly` | As above, but loudness does not affect the score and is not listed as a difference (it is still in the vector). |
| `Absolute` | No gain matching. Every spectral measure includes the level difference; band energies are absolute. |

Loudness is the RMS level of the *active* part of the note (frames within 20 dB of the loudest frame), so it does not
change when only the tail of a note gets longer.

## What is measured

### Preprocessing

`AudioPreprocessor` (configurable through `PreprocessingOptions`): replaces NaN/infinite samples and reports them,
detects clipping, removes DC, resamples (Kaiser-windowed sinc), detects the onset, trims silence before and after the
note, measures loudness and optionally gain-matches. Onset detection uses a level threshold relative to the peak
(-40 dB) that is raised automatically above an audible noise floor. Input that is silent, too short or mostly invalid
is reported as unusable: an unusable **target** throws, an unusable **candidate** returns a result with
`Quality.IsUsable = false` and a fixed penalty distance — a synth patch that produces silence is a normal event in an
optimization run, not an exception.

Alignment is by onset. A small envelope cross-correlation (±5 ms) refines it for the time-frequency comparisons only;
envelope timings are always measured on each signal by itself, so alignment can never hide an attack difference.

### Multi-resolution STFT — `Scores.Spectral`, `result.Spectral`

Hann-windowed magnitude spectrograms at FFT sizes 256, 1024 and 4096 (hop = size / 4; all configurable). Per
resolution:

* **spectral convergence**: `‖T − C‖ / ‖T‖` (Frobenius norm);
* **log-magnitude distance**: mean `|20·log10 T − 20·log10 C|` in dB, within 80 dB of the loudest target bin so that
  inaudible differences in the noise floor do not dominate.

`Scores.Spectral` is the mean over resolutions of `0.5 · convergence + 0.5 · logDistance / 20 dB`. `TimeProfile` shows
the log distance through time; per-band and per-segment differences are described below.

### Log-Mel — `Scores.Perceptual`, `result.Mel`

40 triangular Mel bands from 30 Hz to 16 kHz on a 2048-point STFT. `DistanceDb` is the mean absolute log-Mel
difference; `Scores.Perceptual = DistanceDb / 20`. `ExcessBands` and `DeficientBands` list contiguous frequency
ranges **in Hz** where the time-averaged candidate energy is more than 3 dB above or below the target.

### Harmonics — `Scores.Harmonic`, `result.Harmonics`

Because both sounds are the same note, harmonic analysis carries a lot of weight. The fundamental comes from
`ExpectedFundamentalHz` (options or note) or else from a YIN-style estimator; it is then refined from the measured
partials. Each harmonic `k · f0` up to Nyquist (or 32) is located within ±50 cents and its level is the power summed
over the main lobe of the peak, which makes the measurement independent of phase and of where the partial falls
between FFT bins. The FFT size grows automatically for low notes. For notes with a sustain stage only frames inside
the sustain are used, so the spectral spreading of attack and release does not leak into the levels.

Levels are reported relative to the total harmonic power (timbre, not loudness), clamped at -80 dB. Also reported:
harmonic slope (dB/octave), even/odd balance, the power of harmonics 2–5 and 6+, inharmonicity (cents), pitch
difference (cents) and the harmonic-to-noise ratio. `Scores.Harmonic = 0.8 · salience-weighted mean |Δlevel| / 20 dB +
0.2 · |ΔHNR| / 40 dB`.

### Envelope — `Scores.Envelope`, `result.Envelope`

An RMS envelope (1 ms hop; the window is a whole number of fundamental periods, which removes ripple) is analysed with
simple, robust features rather than a fragile ADSR fit:

| Stage | Definition |
| --- | --- |
| Attack | 10 %–90 % rise time / 0.8 (equals the ramp time of a linear attack) |
| Sustain | median level of the longest stretch with a slope below 20 dB/s, relative to the peak |
| Decay | time from the peak until within 10 % of the sustain level (0 without overshoot); for notes without sustain, the time to fall 20 dB |
| Release | time to fall 20 dB below the sustain level after the envelope leaves the plateau |

Every stage has a confidence. A stage that cannot be measured is `null` with confidence 0 — a plucked note has no
sustain and no release, a recording that ends at full level has a release of 0 ms with confidence 0.3, an attack
shorter than the RMS window is flagged as resolution-limited. Nothing is invented. `Scores.Envelope` combines the mean
difference between the two dB envelope curves with confidence-weighted stage terms (log-ratios for times).

### Descriptors — `result.Descriptors`

Spectral centroid, rolloff (85 %), bandwidth, flatness (dB), slope (dB/octave over octave bands anchored on f0), flux,
zero-crossing rate, crest factor, plus the harmonic summary values above. Each entry has the target value, the
candidate value and the difference.

### Frequency bands and time segments — `result.FrequencyBands`, `result.TimeSegments`

Band energies (default bands: sub 20–80, bass 80–250, lowmid 250–800, mid 800–2500, uppermid 2500–6000, air 6000+ Hz;
all configurable) are reported for the whole note **and per time segment**: attack, decay, sustain and release. Each
signal is segmented by its own envelope, so "the attack" means the attack of each sound even when the attack times
differ. This is how a result can say *"high frequencies +4.1 dB during the attack, +0.3 dB during the sustain"*.

### Overall score

```text
Overall = ( 0.30·Spectral + 0.25·Perceptual + 0.20·Harmonic + 0.20·Envelope + 0.05·Loudness ) / sum of weights
```

The weights (`ScoreWeights`) are configurable and all components stay available.

## Actionable differences and hints

`result.Differences` is the list meant for automation, sorted by importance:

```csharp
public sealed record ToneDifference(
    string Feature,                 // stable id, e.g. "envelope.attack", "harmonic.3", "spectral.detail.air"
    DifferenceDirection Direction,  // TooLow, TooHigh, TooShort, TooLong, TooWeak, TooStrong, Different
    double Magnitude, string Unit,
    double Importance,              // 0..1
    double Confidence,              // 0..1
    double SignedDelta,             // candidate - target
    double? FrequencyStartHz = null, double? FrequencyEndHz = null,
    string? TimeRegion = null);     // "Attack", "Decay", "Sustain", "Release" or null for the whole note
```

`importance = (1 − exp(−size / scale)) · perceptualWeight · persistence · √confidence`, where size is in dB, or a
log-ratio for times and frequencies; persistence is lower for differences that only exist in a short segment (but
never low for the attack); prominent harmonics weigh more than harmonics near the floor. Segment entries that merely
repeat a whole-note difference are dropped. Narrow-band excesses are reported as `spectral.resonance` with their
frequency range.

`result.ParameterHints` adds **generic, low-confidence** hints: for a feature, which *categories* of synth parameters
commonly relate to it (`FilterCutoff`, `OscillatorBrightness`, `Waveshape`, `DistortionAmount`, `AmpEnvelopeAttack`,
...) and in which generic direction. Confidence is capped at 0.5. A hint never claims that a particular parameter is
the cause: brightness can come from a filter, a wavetable position, FM, distortion or an oscillator mix. What a
parameter really does on *your* synth is learned empirically — see below — and always takes priority.

## Feature vectors and normalization

Every result carries three forms of the same difference information:

| Property | Type | Use |
| --- | --- | --- |
| `DifferenceVector` | `ToneDifferenceVector` | strongly typed, the most important deltas |
| `FeatureVector` | `FeatureVector` | dense **raw** deltas in schema order (dB, ms, Hz, cents) |
| `NormalizedDifferenceVector` | `NormalizedFeatureVector` | `raw / typicalScale`, clipped to ±10 |

`PreparedTarget.Schema` (`FeatureVectorSchema`) describes each entry: id, unit, typical scale, whether it is
logarithmic, and a default weight. The order is deterministic: scalar features, aggregate bands (low/mid/high),
configured bands, segment centroids, then a fixed number of harmonics (16), so the layout does not depend on the note.
With default options the vector has 49 entries.

* **Typical scale** is the size of a clearly audible difference: 6 dB for loudness and harmonics, 3 dB for bands,
  10 ms for attack, 50 ms for decay and release, 500 Hz for the centroid, 10 cents for pitch. A normalized magnitude
  of 1 therefore means "clearly different" for every feature. Override scales with
  `FeatureVectorOptions.TypicalScaleOverrides`. Raw values are always kept.
* **Default weight** is `1 / √(group size)` for the groups loudness, pitch, envelope, shape, noise, harmonic shape,
  bands, segments and harmonics. Without it, sixteen correlated harmonic entries would drown out the single attack
  entry in a least-squares error. The zero-crossing rate has default weight 0: it is discontinuous for harmonic
  sounds, so it is reported but does not steer guidance.
* **Weighted error**: `FeatureSchemaBuilder.ApplyWeights(normalized, weights)` computes `e_weighted = W · e`. Use it
  to emphasize the attack, ignore the release for a while (weight 0) or prioritize a band.
* Entries that could not be measured are 0 and listed in `Quality.UnreliableFeatures`.

This vector is what you feed to CMA-ES, Bayesian optimization, a regression model or a neural network.

## Using HearSay in a synth parameter optimizer

HearSay does not render audio and is not an optimizer. It gives an optimizer everything it needs. The high-level
entry point is `ToneOptimizationSession`:

```csharp
using HearSay.Modeling;
using HearSay.Modeling.Parameters;

PreparedTarget target = comparer.PrepareTarget(targetNote);
var session = new ToneOptimizationSession(target, parameters);   // parameters: IEnumerable<SynthParameter>

while (!done)
{
    SynthParameterState p = optimizer.GetNextParameters();
    AudioNote audio = synth.Render(p);                            // yours
    ToneComparisonResult r = session.Evaluate(p, audio);          // analyse + record + learn
    optimizer.Observe(p, r.Scores.Overall, r.NormalizedDifferenceVector);
}
```

`Evaluate` may be called from several threads. It compares, stores an `OptimizationObservation` (parameters, scores,
vectors, timestamp, step type, render and analysis duration) in `session.History`, and updates the effect model.

### Parameters

```csharp
new SynthParameter("cutoff", "Filter cutoff", Value: 1200, MinValue: 20, MaxValue: 20000,
                   Kind: SynthParameterKind.Continuous,
                   RecommendedMinStep: 50, RecommendedMaxStep: 4000,
                   Categories: [SynthParameterCategories.FilterCutoff]);   // optional, enables prior-based suggestions
```

`Normalize` / `Denormalize` map to `0..1`. `Kind` is `Continuous`, `Integer`, `Boolean` or `Categorical`. Booleans and
categorical selectors (waveform, filter type, routing) are **never differentiated**: every change of such a parameter
is a separate experiment, its effect is learned per value pair, and observations taken in another switch setting are
down-weighted when estimating slopes. The DSP core knows nothing about parameters or synths.

### History, progress, stagnation, oscillation

```csharp
OptimizationProgress p = session.GetProgress();          // current, best, improvement from start / since previous,
                                                         // recent improvement rate, IsStagnating
IReadOnlyList<OscillationReport> o = session.DetectOscillations();
```

* **Stagnation**: the best loss improved by less than 1 % over the last 20 evaluations (`ProgressOptions`).
* **Oscillation**: a parameter keeps reversing direction without net movement (`0.42, 0.51, 0.43, 0.50, 0.44`), or a
  feature error keeps changing sign without shrinking. Typical reactions: smaller steps, freeze the parameter, or
  switch to exploration.
* `OptimizationHistoryAnalyzer.GetParameterChanges / GetFeatureChanges` give what changed between two observations.

### Correlation is not causation

`session.GetRelationships()` returns the Pearson correlation between every ordered parameter and every feature over
the whole run, with a confidence from the Fisher transformation and the observation count. It is reported as an
**observed relation** only. If an optimizer always moved cutoff and resonance together, both correlate with brightness
and the correlation cannot tell them apart. For causal estimates use isolated probes or the local effect model, both
of which account for confounding.

### Learning what a parameter does

Two complementary tools:

**Finite differences** — `ParameterEffectExtractor` turns consecutive observations into
`ParameterEffectObservation`s (parameter delta → feature deltas). The confidence of each is
`isolation² · stepFactor · measurementConfidence`: a step in which only one parameter changed has isolation 1; if two
parameters moved equally, each gets isolation 0.5 and therefore a quarter of the confidence — no parameter is credited
for an effect that five parameters may have caused together. `ParameterSensitivityAnalyzer.Estimate` averages the
quotients `Δfeature / Δparameter` weighted by confidence and locality, rejects outliers (median/MAD), ignores steps
that are too small to divide by, and reports a `ParameterSensitivity` with confidence and sample count.
`PlanProbe` / `AnalyzeProbe` support controlled ±δ probing with a central difference.

**Local linear model** — `LocalLinearEffectModel` (an `IParameterEffectModel`) fits a **locally weighted ridge
regression** of all feature deltas on all ordered parameters around a state of interest and returns the approximate
Jacobian:

```csharp
ParameterFeatureJacobian J = session.EstimateJacobian();      // around the best state so far
double slope = J.Values[featureIndex, parameterIndex];        // d(feature) / d(parameter)
double trust = J.Confidence[featureIndex, parameterIndex];    // 0..1
```

* **Interpretation**: `Values[f, p]` is in raw feature units per normalized parameter unit. `+36 dB` for
  (`spectral.band.high`, `brightness`) means: moving brightness by 0.1 of its range raises the high band by about
  3.6 dB — *near the reference state*. `predictedFeatureDelta = J · parameterDelta` (`J.Predict(...)`,
  `model.Predict(...)`), which lets an optimizer compare candidate steps without rendering them.
* **Locality**: synths are not linear. Observations are weighted with a Gaussian kernel (bandwidth 0.15 in normalized
  parameter distance), so a relation measured at cutoff 0.2 counts for little at cutoff 0.9. When too few observations
  are nearby, the kernel widens to the nearest few and all confidences are scaled down accordingly.
* **Confounding**: the confidence of an entry is based on the *unique* variation of its parameter — the part that is
  not shared with other parameters that moved at the same time.
* **Regularization**: ridge shrinkage, a minimum informative step, and residual-based standard errors keep a single
  noisy observation from producing a wild slope.
* **Confidence** combines unique variation (information), effective observation count, residual noise relative to the
  typical scale of the feature, and locality. An entry of 0 with confidence 0 means *unknown*, not *no effect*.

### Guidance: exploitation

```csharp
GuidanceResult g = session.SuggestAdjustments();
foreach (ParameterAdjustmentSuggestion s in g.Suggestions)   // ParameterId, CurrentValue, SuggestedDelta,
    next = next.With(s.ParameterId, s.SuggestedValue);       // SuggestedValue, Confidence, ExpectedAffectedFeatures, Source
```

`OptimizationGuidance` solves a damped, confidence-weighted least-squares step in normalized feature space
(`min ‖W(e + J·Δp)‖² + μ‖Δp‖²`, every Jacobian entry shrunk by its confidence), limits it with a trust region and the
parameter bounds, rounds integer parameters, and reports the predicted error norm. It never assumes that "too bright"
means "lower the cutoff": it only uses effects **measured on this synth**. Generic priors are used solely for
parameters about which nothing is known yet, are marked `SuggestionSource.GenericPrior`, and have a confidence of at
most 0.3. If the synth behaves unusually, the measurement wins as soon as there is one.

This is a suggestion layer, not an optimizer: accepting or rejecting steps, adapting the step size and deciding when
to explore is up to the caller. The closed-loop demo shows a 60-line strategy that does this.

### Exploration versus exploitation

* **Exploitation**: change parameters whose effect is known, to reduce an existing difference.
* **Exploration**: make a small, isolated change to learn what a parameter does.

Tag steps with `OptimizationStepType` (`Exploration`, `Exploitation`, `Validation`). `session.SuggestExperiments()`
(`NextExperimentAdvisor`) ranks exploration candidates by `ExpectedInformationGain`: the share of the *current* error
whose dependence on that parameter is still unknown. It never proposes a state that is already in the history: it
tries the other side of the current value, then wider steps. Untried values of boolean and categorical parameters are
proposed one by one. Exploitation steps from guidance follow. `Reason` is diagnostic text; decide on the numbers.

### Checkpointing and reproducibility

```csharp
string json = session.ExportState();
var restored = ToneOptimizationSession.ImportState(json, targetAudio);   // or an already prepared target
```

The state contains all observations, the parameter definitions, the complete comparison options (metric weights
included), the feature schema with its normalization scales, the analysis sample rate, the fundamental, the library
version and a fingerprint of the target audio. The analysed target itself (spectrograms and so on) is **not**
serialized: it is large, and re-analysing the target audio with the stored options reproduces it exactly. Importing
with a different target throws.

## Patch matching

```bash
dotnet run -c Release --project src/HearSay.Cli -- match target.wav --profile supercollider-fm7 --budget 20000 --output result/
```

```csharp
var matcher = new OperatorMatrixPatchMatcher();
PatchMatchResult result = await matcher.MatchAsync(
    AudioTarget.FromWaveFile("target.wav"),
    new PatchMatchOptions { EvaluationBudget = 20000, RandomSeed = 1234, SynthesisProfile = SynthesisProfiles.SuperColliderFm7 });
PatchMatchResultWriter.Write(result, "result");   // patch.json, preview.wav, patch.scd, analysis.json, …
```

What is behind it, in one paragraph: a canonical `OperatorMatrixPatch` (six operators, full 6×6 phase-modulation
matrix with self and circular feedback, independent output gains, ratio or fixed frequencies, breakpoint envelopes,
pitch model) is the single source of truth for search, rendering, JSON and export. A `SynthesisProfile` says what a
backend can represent and the search never leaves it. An offline renderer reproduces the SuperCollider `FM7` UGen,
including its one-sample modulation delay, closely enough to match the real UGen sample by sample. The target is
analysed without synthesis assumptions (inharmonic partials, partial tracks, spectral and band trajectories, pitch,
beating, long-term spectrum, multi-stage decay); structural hypotheses come from pluggable prior providers; structure
(operators, edges, waveforms, modes, coarse ratios) is searched as separate basins with topology mutation, continuous
parameters with CMA-ES on small active subsets, and the finalists are polished with the regime-specific Jacobians and
trust-region guidance described above. Everything is budgeted in renders and reproducible from a seed.

Several notes of the same sound (`file.wav@48 file.wav@60 file.wav@72`) are matched by one patch, which makes key
scaling, velocity sensitivity and fixed-frequency operators searchable.

Details, results, limitations and how a future custom UGen plugs in: **[docs/patch-matching.md](docs/patch-matching.md)**.

## Architecture

```text
HearSay.sln
├── src
│   ├── HearSay.Audio                     how does candidate audio differ from target audio?
│   │   ├── Audio/            AudioNote, WavFile
│   │   ├── Dsp/              FftPlan, HannWindow, Resampler, LinearAlgebra, MathUtil
│   │   ├── Preprocessing/    IAudioPreprocessor, AudioPreprocessor, PreprocessedAudio
│   │   ├── Spectral/         IStftAnalyzer, Spectrogram, MelFilterbank, SpectralTrajectories, LongTermAverageSpectrum
│   │   ├── Harmonics/        IHarmonicAnalyzer, HarmonicProfile, FundamentalEstimator
│   │   ├── Partials/         PartialDetector, PartialTracker, PartialSetDistance
│   │   ├── Pitch/            PitchTracker, PitchTrajectory, BeatDetector
│   │   ├── Envelope/         IEnvelopeAnalyzer, EnvelopeProfile, MultiStageDecay
│   │   ├── Features/         ISpectralFeatureAnalyzer, ToneFeatureIds, FeatureSchemaBuilder
│   │   ├── Comparison/       IToneComparer, ToneComparer, PreparedTarget, ToneAnalysis, GenericSynthPriors, options
│   │   ├── Analysis/         AudioTarget, TargetAnalysis, TargetAnalyzer
│   │   ├── Objective/        AudioObjective (composite loss, evaluation levels)
│   │   ├── Models/           result records, feature vectors
│   │   └── HearSayJson.cs    System.Text.Json options (camelCase, enums as strings, double[,] as rows)
│   ├── HearSay.Modeling                  what effects have parameter changes had on audio features?
│   │   ├── Parameters/       SynthParameter, SynthParameterState, SynthParameterSpace
│   │   ├── History/          OptimizationHistory, IOptimizationHistoryAnalyzer (progress, oscillation, correlation)
│   │   ├── Effects/          ParameterEffectExtractor, IParameterEffectModel, LocalLinearEffectModel
│   │   ├── Sensitivity/      IParameterSensitivityAnalyzer, ParameterFeatureJacobian, probes
│   │   ├── Prediction/       prediction records
│   │   ├── Guidance/         OptimizationGuidance, NextExperimentAdvisor
│   │   └── ToneOptimizationSession
│   ├── HearSay.Synthesis                 how is a canonical patch rendered?  (patch model, profiles, renderer abstractions)
│   ├── HearSay.Synthesis.Fm7Compatible   offline reference renderer with FM7 semantics
│   ├── HearSay.SuperCollider             FM7 exporter, SuperCollider NRT renderer, backend-equivalence validation
│   ├── HearSay.OperatorMatrix            which synthesis structure and parameters should be explored?
│   ├── HearSay                           facade: OperatorMatrixPatchMatcher, SynthesisBackends, PatchMatchResultWriter
│   ├── HearSay.Cli                       hearsay match | analyze | profiles
│   └── HearSay.Studio                    Avalonia desktop app: load WAVs, run the search, play the result on a keyboard, show patch.scd
├── tests                                 xUnit: Audio, Modeling, Synthesis, OperatorMatrix, SuperCollider, IntegrationTests
├── benchmarks/HearSay.Benchmarks         BenchmarkDotNet
├── docs
│   ├── tutorial.md                       step-by-step tutorial for the comparison and closed-loop API
│   └── patch-matching.md                 the audio-to-patch solver
└── samples
    ├── HearSay.Tutorial                  companion program of the tutorial, one method per step
    ├── HearSay.Demo                      compare two WAV files
    ├── HearSay.OptimizationDemo          closed loop with a built-in test synth
    └── HearSay.MatchDemo                 round trip: reference patches → targets → matcher → results table
```

The original single `HearSay` assembly was split: audio-side namespaces are unchanged, `HearSay.Optimization.*`
became `HearSay.Modeling.*`.

Every analyzer sits behind a small interface and can be used or replaced on its own; `ToneComparer` accepts custom
implementations through its constructor. There is no DI container requirement.

## Performance

The library is built for thousands of comparisons per run:

* everything that depends only on the target is computed once (`PrepareTarget`), including the log magnitudes;
* FFT plans, windows and Mel filterbanks are cached per size and shared between threads;
* inner loops work on arrays and spans, without LINQ or per-frame allocations; log magnitudes are only computed for
  bins that are above the comparison floor;
* one spectrogram per FFT size is shared between the STFT comparison, the Mel analysis, the band/segment analysis and
  the harmonic analysis.

Measured with `dotnet run -c Release --project benchmarks/HearSay.Benchmarks -- --filter "*"`
(BenchmarkDotNet ShortRun):

| Benchmark | 1 s note | 3 s note | 5 s note | Allocated (1 s) |
| --- | ---: | ---: | ---: | ---: |
| Preprocessing | 0.28 ms | 0.98 ms | 1.69 ms | 0.7 MB |
| STFT analysis (3 resolutions) | 3.19 ms | 10.2 ms | 17.5 ms | 1.1 MB |
| Full comparison (target + candidate) | 14.4 ms | 42.3 ms | 70.7 ms | 6.8 MB |
| **Prepared-target comparison** | **7.0 ms** | **21.1 ms** | **34.6 ms** | 3.0 MB |
| Dense feature vector (normalize + weight) | 1.0 µs | 1.0 µs | 1.0 µs | 7 KB |

| Benchmark (8 parameters, 49 features) | 50 observations | 500 observations |
| --- | ---: | ---: |
| Effect model update (all observations) | 1.0 µs | 7.3 µs |
| Jacobian estimation | 57 µs | 595 µs |
| Prediction from Jacobian | 0.6 µs | 0.6 µs |

Intel Core i7-13700KF, .NET 10.0.9, Windows 11, single thread, 44.1 kHz. A prepared-target comparison costs about
7 ms per second of audio, so one core handles roughly 140 one-second candidates per second, and comparisons scale
across cores because the prepared target is shared.

Practical notes: cost is linear in note length, so render short notes when you can; compare candidates in parallel
against one `PreparedTarget`; drop an STFT resolution (`StftOptions.FftSizes`) if you need more speed.

Patch matching: a one-second six-operator note renders offline in 1.2 ms, a candidate evaluation (render plus
comparison) runs at about 900 per second on all cores, and a complete search with 20000 renders takes 25 to 45
seconds. Details in [docs/patch-matching.md](docs/patch-matching.md#performance).

## Demos

```bash
# Compare two WAV files (prints a report and the full JSON analysis)
dotnet run -c Release --project samples/HearSay.Demo -- target.wav candidate.wav --f0 220 [--json out.json]

# Without arguments an example pair is generated and compared
dotnet run -c Release --project samples/HearSay.Demo

# Closed loop with a five-parameter test synth (Brightness, Attack, Release, NoiseAmount, Harmonic2Amount)
dotnet run -c Release --project samples/HearSay.OptimizationDemo [-- --verbose]

# The tutorial's companion program (all steps, or one step by number)
dotnet run -c Release --project samples/HearSay.Tutorial [-- 9]

# Find a patch for a target note (writes patch.json, preview.wav, patch.scd and diagnostics)
dotnet run -c Release --project src/HearSay.Cli -- match target.wav --budget 20000 --output result/

# Desktop app: load a WAV, run the search, read or copy the SuperCollider patch, save the result folder
dotnet run -c Release --project src/HearSay.Studio [-- target.wav]

# Render a reference patch to WAV targets (samples/targets has a ready-made three-octave set)
dotnet run -c Release --project src/HearSay.Cli -- reference electric-piano-keyed --notes 48,60,72 --output targets/

# Round trip over the reference patches; --supercollider also compares the result with the real FM7 UGen
dotnet run -c Release --project samples/HearSay.MatchDemo -- [--budget 20000] [--only bell,bass] [--supercollider]
```

`HearSay.Demo` on its generated example pair (the candidate is brighter, has a slower attack, a shorter release and a
weaker third harmonic):

```text
Overall distance: 0.256   (0 = perfect; similarity 0.46)

  Spectral:   0.229
  Perceptual: 0.364
  Harmonic:   0.254
  Envelope:   0.218
  Loudness:   0.035

Envelope (target -> candidate):
  Attack            12.6 ms -> 34.9 ms          delta +22.3 ms   confidence 0.90
  Decay            225.8 ms -> 221.5 ms         delta -4.3 ms    confidence 0.77
  Sustain           -2.6 dB -> -2.2 dB          delta +0.4 dB    confidence 0.90
  Release          124.0 ms -> 59.4 ms          delta -64.6 ms   confidence 0.85

Top differences (candidate relative to target):

 1. Spectral centroid (brightness): TooHigh
    +221.9 Hz   (importance 0.79, confidence 1.00)

 2. Attack time: TooLong
    +22.3 ms   (importance 0.76, confidence 0.90)

 3. Energy in the air band: TooStrong
    6 kHz-20 kHz: +10.8 dB   (importance 0.71, confidence 1.00)
 ...
```

`HearSay.OptimizationDemo` hides a parameter set, starts far away from it, probes every parameter once, then follows
the library's suggestions with a simple trust region and asks the advisor what to explore whenever a step is rejected:

```text
Target parameters (hidden from the optimizer): brightness=0.72, attack=0.12, release=0.55, noise=0.15, harmonic2=0.35
Start parameters:                              brightness=0.30, attack=0.50, release=0.15, noise=0.60, harmonic2=0.00

Iteration 0  loss 0.669  (spectral 0.648, perceptual 1.004, harmonic 0.494, envelope 0.559)
  Main errors:
    envelope.attack          TooLong      +79.7 ms  importance 0.92
    spectral.detail.uppermid TooWeak      -14.6 dB [2500-6000 Hz]  importance 0.78
    spectral.centroid        TooLow      -156.8 Hz  importance 0.77

Iteration 1  (exploration)  loss 0.646
  Observed: brightness +0.06
    -> spectral.detail.mid +1.7 dB
    -> spectral.detail.uppermid +1.5 dB
...
Learned local sensitivities (feature units per full parameter range):
  attack      -> envelope.attack          =    +259.3 ms / unit   confidence 0.69
  release     -> envelope.release         =    +502.9 ms / unit   confidence 0.69
  noise       -> spectral.band.high       =     +26.3 dB / unit   confidence 0.69
...
Iteration 6  loss 0.493
  Applied: release +0.112 (conf 0.69), attack -0.110 (conf 0.68), brightness +0.150 (conf 0.68), harmonic2 +0.032 (conf 0.68), noise -0.030 (conf 0.68)
...
=== Summary ===
Evaluations: 19
Loss: 0.669 -> 0.008 (iteration 18)
Found:  brightness=0.72, attack=0.12, release=0.55, noise=0.16, harmonic2=0.37
Hidden: brightness=0.72, attack=0.12, release=0.55, noise=0.15, harmonic2=0.35
```

(Exact numbers of later iterations can shift slightly when defaults are tuned; the run is deterministic for a given
version.)

## Tests

`dotnet test` runs 145 tests in six projects.

| Project | Tests | What they cover |
| --- | --- | --- |
| `HearSay.Audio.Tests` | 55 | the comparison scenarios listed below, the individual analyzers, and the new analyses: inharmonic partials, partial tracks, partial-set distance, spectral and band trajectories, flux, multi-stage decay, pitch offset, vibrato and drift, beating, note-off, long-term spectrum, the composite objective |
| `HearSay.Modeling.Tests` | 20 | sensitivities from real audio, correlation, confounding, stagnation, oscillation, locality, prediction, guidance, discrete parameters, the experiment advisor, checkpointing, a complete closed loop |
| `HearSay.Synthesis.Tests` | 25 | patch model, JSON and hashing, profiles and compatibility, the FM7-compatible renderer (previous-sample modulation, fixed versus ratio operators, output mix, envelopes, velocity, key scaling), the extended renderer |
| `HearSay.OperatorMatrix.Tests` | 26 | parameter hierarchy, structural versus continuous, conditional activity, transforms, ties, active sets, regime keys, canonicalization, complexity, CMA-ES, priors, mutation, screening, evaluator budget and caching, resumable runs |
| `HearSay.SuperCollider.Tests` | 8 | exporter output; native versus real FM7 for 30 random and 12 reference patches and an instrument check (skipped without SuperCollider) |
| `HearSay.IntegrationTests` | 11 | round-trip searches, target sets of several notes, reproducibility, budget, profile safety, the future-waveform proof, the result folder |

The comparison scenarios: identical signals, a
phase-shifted sine, a +6 dB copy, a stronger second harmonic, a brighter candidate, slower attack, longer release,
added noise, a small timing offset, a bright attack only, clipping, unusable input, different sample rates, JSON
round trips, determinism under parallel use — as well as the individual analyzers (FFT, STFT calibration, resampler,
preprocessing, envelope stages, harmonic levels, F0 estimation, Mel filterbank, WAV I/O) and the closed-loop
functionality: parameter sensitivities from real audio, correlation, confounding, stagnation, oscillation, locality
of the effect model, prediction, guidance with bounds and priors, discrete parameters, the experiment advisor,
checkpointing, and a complete loop that has to reduce the loss.

## Limitations

* The patch matcher can match one note or a set of notes of the same sound (several octaves, several velocities) with one patch; its limitations are listed in [docs/patch-matching.md](docs/patch-matching.md#limitations-of-this-version).
* **Monophonic, single notes with a stable pitch.** Chords, glides, strong vibrato and pitch envelopes are out of
  scope; vibrato and tremolo can prevent a sustain plateau from being found (the stage is then reported as missing,
  not guessed).
* **Same note assumed.** A pitch difference is measured (cents) and flagged above 50 cents, but harmonic comparison
  assumes both signals share a fundamental.
* **Time resolution of the envelope** is about one RMS window: two fundamental periods at 220 Hz (9 ms), more for low
  notes. Shorter attacks are reported with reduced confidence. The measured attack is the 10–90 % rise time / 0.8,
  which differs from the knob value of a synth with an exponential attack; differences remain monotonic and usable.
* **Percussive attack transients** are described by band energies and centroid per segment, not by a dedicated
  transient model.
* **Loudness is RMS based**, not a full perceptual loudness model (no equal-loudness weighting).
* **Typical scales and perceptual weights are engineering defaults**, chosen to be sensible for synth sound matching;
  they are not the result of listening tests. All of them are configurable.
* **The effect model is local and linear.** It deliberately does not extrapolate: far from observed states its
  confidence drops and exploration is needed again. Interactions between parameters are only captured in so far as a
  local linear model can.
* The **resampler** is an offline windowed-sinc design (about 80 dB stop band); prefer rendering at the target rate.
* WAV support is limited to uncompressed PCM (8/16/24/32 bit) and IEEE float (32/64 bit).

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
