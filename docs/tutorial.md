# HearSay tutorial

This tutorial takes you from a first comparison of two sounds to a working closed loop that finds synth parameters by
itself. It is hands-on: every step is a method in the companion program
[`samples/HearSay.Tutorial`](../samples/HearSay.Tutorial/Program.cs), and all output shown here was produced by it.

```bash
dotnet run -c Release --project samples/HearSay.Tutorial          # all steps
dotnet run -c Release --project samples/HearSay.Tutorial -- 9     # only step 9
```

The [README](../README.md) is the reference (what each metric means, how confidences are computed, limitations). This
document is about *how to use it*.

**Contents**

| Part | Steps |
| --- | --- |
| A. Comparing two sounds | [0 Setup](#step-0--setup) · [1 First comparison](#step-1--a-first-comparison) · [2 Differences](#step-2--reading-the-differences) · [3 Detail](#step-3--looking-at-the-detail) · [4 Options](#step-4--options) · [5 WAV and JSON](#step-5--wav-files-and-json) |
| B. Many candidates | [6 Prepared targets](#step-6--one-target-many-candidates) · [7 Feature vectors](#step-7--feature-vectors) |
| C. Closing the loop | [8 Learning the parameters](#step-8--a-session-learning-what-the-parameters-do) · [9 The loop](#step-9--closing-the-loop) · [10 Your own optimizer](#step-10--your-own-optimizer) · [11 Checkpoints](#step-11--checkpoints) |
| D. In practice | [Connecting a real synth](#connecting-a-real-synth) · [Driving it with an agent](#driving-it-with-an-agent) · [Troubleshooting](#troubleshooting) |

Two rules to keep in mind throughout:

* **`delta = candidate - target`.** Positive means the candidate has *more* (louder, brighter, longer).
* **Distances: 0 is perfect.** Only `Similarity` runs the other way (1 is perfect).

---

## Part A — Comparing two sounds

### Step 0 — Setup

Create a console project and reference the library (it has no dependencies of its own):

```bash
git clone https://github.com/shimpe/HearSay.git
dotnet new console -n MyMatcher
dotnet add MyMatcher reference HearSay/src/HearSay.Modeling/HearSay.Modeling.csproj
```

The namespaces you will need:

```csharp
using HearSay;                          // HearSayJson
using HearSay.Audio;                    // AudioNote, WavFile
using HearSay.Comparison;               // ToneComparer, ToneComparisonOptions, PreparedTarget
using HearSay.Features;                 // ToneFeatureIds, FeatureSchemaBuilder
using HearSay.Models;                   // results
using HearSay.Modeling;             // ToneOptimizationSession
using HearSay.Modeling.Guidance;    // suggestions, advisor
using HearSay.Modeling.History;     // observations, progress
using HearSay.Modeling.Parameters;  // SynthParameter, SynthParameterState
using HearSay.Modeling.Sensitivity; // probes, Jacobian
```

The tutorial needs a synth. `TutorialSynth` is a 70-line stand-in with three parameters in native units — a filter
cutoff (200–12000 Hz), an amp attack (1–300 ms) and a waveform switch (saw/square). HearSay never looks inside it;
it only sees the audio it renders. Wherever the tutorial calls `TutorialSynth.Render(state)`, you call your synth.

Throughout, the sound we **want** is `cutoff 5000, attack 10, square`, and our first **attempt** is
`cutoff 1500, attack 80, square`: too dark and too slow.

### Step 1 — A first comparison

```csharp
var comparer = new ToneComparer();

AudioNote target = TutorialSynth.Render(Wanted);        // or: new AudioNote(float[] samples, sampleRate, f0)
AudioNote candidate = TutorialSynth.Render(Attempt);

ToneComparisonResult result = comparer.Compare(target, candidate);
```

```text
overall distance 0.261  (0 = perfect)
similarity       0.46   (1 = perfect)
spectral 0.195  perceptual 0.392  harmonic 0.325  envelope 0.173  loudness 0.087
```

An `AudioNote` is mono `float` samples, a sample rate and, if you know it, the fundamental of the note. **Tell it the
fundamental when you can** (`ExpectedFundamentalHz` on the note or in the options): harmonic analysis is more reliable
than with an estimated pitch. The two notes may differ in length and even in sample rate.

How to read the scores: below about 0.02 the sounds are practically identical, 0.1 is clearly audible, 0.5 and up is a
different sound. The components tell you *where* the distance comes from — here mostly the spectrum
(perceptual/harmonic), with a share for the envelope.

### Step 2 — Reading the differences

A score tells you how far off you are. `result.Differences` tells you what to fix, most important first:

```csharp
foreach (ToneDifference d in result.Differences.Take(6))
    Console.WriteLine($"{d.Feature} {d.Direction} {d.SignedDelta:+0.0;-0.0} {d.Unit} ...");
```

```text
envelope.attack            TooLong      +68.8 ms  importance 0.91  confidence 0.90
spectral.detail.air        TooWeak      -19.7 dB [6000-20000 Hz]  importance 0.82  confidence 1.00
spectral.detail.uppermid   TooWeak      -13.1 dB [2500-6000 Hz]  importance 0.75  confidence 1.00
spectral.centroid          TooLow       -93.0 Hz  importance 0.59  confidence 1.00
mel.band                   TooWeak      -12.9 dB [1197-9475 Hz]  importance 0.53  confidence 1.00
spectral.rolloff           TooLow      -430.7 Hz  importance 0.49  confidence 1.00
```

That is exactly what we did wrong: the attack is ~70 ms too long and everything above 2.5 kHz is missing. Each entry is
data, not prose:

| Field | Use |
| --- | --- |
| `Feature` | a stable id from `ToneFeatureIds` — safe to switch on and to store |
| `Direction`, `SignedDelta`, `Unit` | what is wrong, by how much (`candidate - target`) |
| `Importance` (0..1) | ranking; combines size, perceptual weight, how long the difference lasts, and confidence |
| `Confidence` (0..1) | how reliable the measurement is |
| `FrequencyStartHz` / `FrequencyEndHz` | set for bands, harmonics and resonances |
| `TimeRegion` | `Attack`, `Decay`, `Sustain`, `Release`, or `null` for the whole note |

`result.ParameterHints` adds generic synth knowledge:

```text
hint: envelope.attack -> Shorten via AmpEnvelopeAttack, FilterEnvelopeAttack  (confidence 0.49)
hint: spectral.detail.air -> IncreaseBrightness via FilterCutoff, OscillatorBrightness, DistortionAmount, FmAmount  (confidence 0.44)
hint: spectral.detail.air -> Investigate via Waveshape  (confidence 0.25)
```

Treat hints as a starting point for a human or an agent, nothing more. They name parameter *categories*, never your
parameters, and their confidence is capped at 0.5: a dull sound can be caused by a filter, a wavetable position, FM
depth or an oscillator mix. Part C shows how to *measure* what your parameters really do.

### Step 3 — Looking at the detail

Everything behind the differences is in the result too.

```csharp
EnvelopeStageComparison attack = result.Envelope.Attack;   // also Decay, Sustain, Release
result.Harmonics          // per harmonic: level in target and candidate, difference
result.FrequencyBands     // whole-note energy per band
result.TimeSegments       // the same bands plus brightness, per attack/decay/sustain/release
result.Descriptors        // centroid, rolloff, flatness, HNR, inharmonicity, ... for both sounds
result.Spectral, result.Mel
result.Quality            // warnings, confidences, fundamental, alignment
```

```text
attack: target 11.4 ms, candidate 80.2 ms, delta +68.8 ms, confidence 0.90
release delta: +0.0 ms
harmonics (relative level, dB):
  h1     220 Hz  target   -0.8  candidate   -0.6  delta   +0.2
  h3     660 Hz  target  -10.4  candidate  -10.3  delta   +0.0
  h9    1981 Hz  target  -20.0  candidate  -25.8  delta   -5.7
  h15   3301 Hz  target  -25.1  candidate  -38.0  delta  -12.9
bands:
  sub          20-   80 Hz  delta   -3.5 dB
  ...
  uppermid   2500- 6000 Hz  delta  -13.1 dB
  air        6000-20000 Hz  delta  -19.7 dB
brightness per time segment:
  Attack   centroid delta     -92 Hz
  Sustain  centroid delta     -93 Hz
```

Things worth knowing:

* **Envelope values are nullable.** A plucked sound has no sustain and no release; a recording cut off at full level
  has no measurable release. You get `null` and confidence 0, never a made-up number. Always check
  `Difference is double d` before using it.
* **Harmonic levels are relative** to the total harmonic power, so they describe timbre, not loudness. The low
  harmonics above match; the high ones fall away — the signature of a low-pass filter that is too closed.
* **Time segments** show *when* a difference exists. Here brightness is equally wrong everywhere (a static filter).
  If only the attack were too bright, you would look at a filter envelope instead of the cutoff.
* **`result.Quality.Warnings`** reports clipping, resampling, an uncertain fundamental, a pitch mismatch and so on,
  with stable codes (`AnalysisWarningCodes`).

### Step 4 — Options

`ToneComparisonOptions` is an immutable record; everything has a sensible default. The option you will actually think
about is **how loudness is treated**. Comparing a sound with a copy that is 4 dB louder:

```text
SeparateLoudness overall 0.010  spectral 0.000  loudness delta +4.1 dB  differences 1
TimbreOnly       overall 0.000  spectral 0.000  loudness delta +4.1 dB  differences 0
Absolute         overall 0.152  spectral 0.329  loudness delta +4.1 dB  differences 8
```

* `SeparateLoudness` (default): timbre is compared after gain-matching; loudness is reported as its own difference.
  Use it when your synth has an output level you also want to match.
* `TimbreOnly`: loudness is measured but does not count. Use it when level is irrelevant or handled elsewhere.
* `Absolute`: nothing is normalized; a louder copy looks different everywhere. Rarely what you want for matching.

Other common adjustments:

```csharp
var options = new ToneComparisonOptions
{
    ExpectedFundamentalHz = 220.0,
    // what "close" means to you: here the envelope matters most
    Weights = new ScoreWeights { Spectral = 0.15, Perceptual = 0.25, Harmonic = 0.15, Envelope = 0.40, Loudness = 0.05 },
    // your own frequency bands (the names become feature ids: spectral.detail.body, ...)
    Bands = new BandOptions { Bands = [new("body", 80, 1000), new("presence", 1000, 5000), new("top", 5000, 20000)] },
    // fewer, larger differences
    Differences = new DifferenceOptions { MinimumLevelDifferenceDb = 2.0, MaximumDifferences = 5 },
};
```

```text
custom options: overall 0.250; bands body +0.2, presence -3.0, top -18.7
```

Nested option records (`Preprocessing`, `Stft`, `Mel`, `Harmonic`, `Envelope`, `FeatureVector`) cover the rest; their
XML documentation explains every value. One caution: **keep the options fixed during an optimization run.** Changing
weights or bands halfway changes the meaning of the loss and of the feature vector.

### Step 5 — WAV files and JSON

```csharp
AudioNote target = WavFile.Read("target.wav", expectedFundamentalHz: 220.0);
AudioNote candidate = WavFile.Read("candidate.wav", expectedFundamentalHz: 220.0);
// stereo is downmixed; or pick a channel: WavFile.Read(path, WavChannelMode.SingleChannel, channelIndex: 1)

ToneComparisonResult result = comparer.Compare(target, candidate);

string json = HearSayJson.Serialize(result);                       // camelCase, enums as strings
var restored = HearSayJson.Deserialize<ToneComparisonResult>(json);
```

```text
wrote ...\hearsay-tutorial\comparison.json (48 KiB)
round trip: 31 differences, overall 0.261
```

The JSON contains the complete analysis and round-trips losslessly. It is the format to hand to an agent or to store
next to a patch. For a quick look without writing code, the `HearSay.Demo` sample does exactly this from the command
line: `dotnet run --project samples/HearSay.Demo -- target.wav candidate.wav --f0 220 --json out.json`.

---

## Part B — Many candidates

### Step 6 — One target, many candidates

In a matching loop the target never changes. Analyse it once:

```csharp
PreparedTarget prepared = comparer.PrepareTarget(TutorialSynth.Render(Wanted) /*, options */);

Parallel.For(0, cutoffs.Length, i =>
{
    AudioNote candidate = TutorialSynth.Render(TutorialSynth.State(cutoffs[i], attack: 10, waveform: 1));
    losses[i] = comparer.Compare(prepared, candidate).Scores.Overall;
});
```

```text
cutoff   1000 Hz -> distance 0.297
cutoff   2000 Hz -> distance 0.141
cutoff   3000 Hz -> distance 0.068
cutoff   4000 Hz -> distance 0.026
cutoff   5000 Hz -> distance 0.000
cutoff   6000 Hz -> distance 0.017
cutoff   8000 Hz -> distance 0.035
cutoff  10000 Hz -> distance 0.043
```

This halves the cost per comparison (about 7 ms per second of audio on one core). `PreparedTarget` and `ToneComparer`
are immutable, so parallel use is safe and results stay deterministic. The options are fixed at preparation time and
apply to every candidate.

The sweep also shows something you will meet again in step 10: the distance is smooth and has its minimum in the right
place, but it is **asymmetric and flat on one side** — above the right cutoff the missing harmonics are already weak,
so too bright costs little. A loss alone is a weak guide there; the signed differences are not.

### Step 7 — Feature vectors

For numeric optimizers and learning algorithms the result carries the differences as vectors:

| Property | What it is |
| --- | --- |
| `result.DifferenceVector` | typed record: `AttackDelta`, `HighBandDelta`, `HarmonicDeltas[]`, ... |
| `result.FeatureVector` | dense **raw** values (ms, dB, Hz, cents) in schema order |
| `result.NormalizedDifferenceVector` | `raw / typicalScale` — unitless, 1 ≈ "clearly audible" |
| `prepared.Schema` | id, unit, typical scale and default weight of every entry; fixed order, fixed length |

```text
49 features; the first six:
  loudness           raw    +1.74 dB    / scale    6 =  +0.29   default weight 1.00
  pitch              raw    +0.01 cents / scale   10 =   0.00   default weight 1.00
  envelope.attack    raw   +68.85 ms    / scale   10 =  +6.88   default weight 0.50
  envelope.decay     raw     0.00 ms    / scale   50 =   0.00   default weight 0.50
  envelope.sustain   raw     0.00 dB    / scale    6 =   0.00   default weight 0.50
  envelope.release   raw    +0.01 ms    / scale   50 =   0.00   default weight 0.50
typed: AttackDelta +68.8 ms; by id: +68.8 ms
error norm: unweighted 12.28, weighted 7.68
```

An optimizer's job in one line: **drive this vector to zero.** Unlike the scalar loss it says in which direction every
aspect is off.

Weights let you steer what matters (`e_weighted = W · e`):

```csharp
var weights = prepared.Schema.DefaultWeights().ToList();      // 1/sqrt(group size): no group dominates by count
weights.RemoveAll(w => w.FeatureId is ToneFeatureIds.AttackTime or ToneFeatureIds.ReleaseTime);
weights.Add(new OptimizationFeatureWeight(ToneFeatureIds.AttackTime, 1.0));   // attack counts double
weights.Add(new OptimizationFeatureWeight(ToneFeatureIds.ReleaseTime, 0.0));  // ignore the release for now
NormalizedFeatureVector weighted = FeatureSchemaBuilder.ApplyWeights(result.NormalizedDifferenceVector, weights);
```

Values that could not be measured are 0 and listed in `result.Quality.UnreliableFeatures`. Change a typical scale with
`FeatureVectorOptions.TypicalScaleOverrides` if, say, 10 ms of attack is not a big deal for your material.

---

## Part C — Closing the loop

HearSay does not render audio and is not an optimizer. What it adds to a loop is **memory and understanding**: it
records every render, learns what each parameter does to each feature *on your synth*, and turns that into predictions
and suggestions. The entry point is `ToneOptimizationSession`.

### Step 8 — A session: learning what the parameters do

Describe your parameters once, in native units:

```csharp
IReadOnlyList<SynthParameter> parameters =
[
    new("cutoff", "Filter cutoff", Value: 2000, MinValue: 200, MaxValue: 12000,
        RecommendedMinStep: 100, RecommendedMaxStep: 2500,
        Categories: [SynthParameterCategories.FilterCutoff]),          // optional
    new("attack", "Amp attack", Value: 20, MinValue: 1, MaxValue: 300, RecommendedMinStep: 3,
        Categories: [SynthParameterCategories.AmpEnvelopeAttack]),
    new("waveform", "Waveform (0 = saw, 1 = square)", Value: 0, MinValue: 0, MaxValue: 1,
        Kind: SynthParameterKind.Categorical),
];

var session = new ToneOptimizationSession(comparer.PrepareTarget(targetNote), parameters);
```

* `Kind` matters. `Continuous` and `Integer` parameters have slopes. `Boolean` and `Categorical` ones are never
  differentiated; each value is its own experiment.
* `RecommendedMinStep` is the smallest change worth rendering; `RecommendedMaxStep` caps suggested steps.
* `Categories` are optional. They only allow low-confidence, prior-based suggestions before anything has been learned.

Evaluate a state, then **probe** each ordered parameter: change one thing, by a little, and render again.

```csharp
session.Evaluate(start, synth.Render(start), OptimizationStepType.Validation, "start");

var analyzer = new ParameterSensitivityAnalyzer();
foreach (SynthParameter p in session.ParameterSpace.Parameters.Where(p => p.IsOrdered))
{
    ParameterProbe probe = analyzer.PlanProbe(p, start.Values[p.Id], normalizedStep: 0.05);   // respects bounds
    SynthParameterState probed = start.With(p.Id, probe.PositiveValue);
    session.Evaluate(new OptimizationStep(OptimizationStepType.Exploration, start, probed, p.Id), synth.Render(probed));
}

ParameterFeatureJacobian jacobian = session.EstimateJacobian(start);
ParameterSensitivity s = jacobian.GetSensitivity("cutoff", ToneFeatureIds.SpectralCentroid)!;
```

```text
probed cutoff: 1500 -> 2090
probed attack: 80 -> 95

what one full range of each parameter does (near the start state):
  d(spectral.centroid) / d(cutoff) =    +500.0 Hz  confidence 0.64
  d(spectral.band.high) / d(cutoff) =    +106.8 dB  confidence 0.64
  d(envelope.attack) / d(cutoff) =      -0.1 ms  confidence 0.64
  d(envelope.attack) / d(attack) =    +299.4 ms  confidence 0.64
  d(spectral.centroid) / d(attack) =      -0.3 Hz  confidence 0.64
```

Three renders, and the session knows that on this synth the cutoff drives brightness and leaves the attack alone, that
the attack knob does the reverse, and how strongly. Nothing was assumed; it was measured.

**Reading a sensitivity.** Units are *feature units per full parameter range*. `+299.4 ms` for attack means: moving the
attack knob by 10 % of its range changes the measured attack time by about 30 ms. The huge `+106.8 dB` for the high
band is a *local* slope at cutoff 1500 Hz, where the band is nearly empty — the filter is steep there. It will be much
smaller at 5000 Hz. That is why every estimate is tied to a reference state and why observations near that state count
most (details in the README).

**Reading a confidence.** 0.64 after a single probe is "measured once, cleanly". It rises with more observations that
agree, and falls when parameters were changed together (their effects cannot be separated), when the step was tiny,
when the fit is noisy, or when you ask about a state far from anything measured. An entry of `0` with confidence `0`
means *unknown*, not *no effect*.

Once you have a Jacobian you can **predict a step without rendering it**:

```csharp
var prediction = jacobian.Predict(start, start.With("attack", 50), session.ParameterSpace);
double attackChange = prediction.PredictedFeatureDelta.Get(ToneFeatureIds.AttackTime)!.Value;
```

```text
attack 80 -> 50 ms: predicted change of the attack delta -30.0 ms, measured -30.0 ms (prediction confidence 0.46)
```

Use predictions to rank candidate steps before spending renders on them. The confidence drops with step size: a
linear model is only good nearby.

### Step 9 — Closing the loop

Now let the session propose the steps. Two calls do the work:

* `session.SuggestAdjustments()` — **exploitation**. Solves for the parameter changes that the measured sensitivities
  say will shrink the current error, within bounds and a maximum step. Returns per-parameter suggestions with
  confidence, plus the predicted error.
* `session.SuggestExperiments()` — **exploration**. Says which parameter is least understood *with respect to the
  current error*, and which values of a switch or selector have never been tried.

A complete strategy in about thirty lines — explore what is unknown, otherwise exploit, shrink the step when a step
fails. This time the start state also has the **wrong waveform**:

```csharp
double trust = 0.15;                                   // largest step, as a fraction of each parameter range
for (int i = 0; i < 25 && session.History.Best!.OverallLoss > 0.01; i++)
{
    OptimizationObservation best = session.History.Best!;

    // 1. explore what is poorly understood
    ExperimentSuggestion? experiment = session.SuggestExperiments()
        .FirstOrDefault(e => e.StepType == OptimizationStepType.Exploration && e.ExpectedInformationGain >= 0.5);
    if (experiment is not null)
    {
        SynthParameterState probe = best.Parameters.With(experiment.ParameterId, experiment.SuggestedValue);
        session.Evaluate(new OptimizationStep(OptimizationStepType.Exploration, best.Parameters, probe, experiment.ParameterId),
                         synth.Render(probe));
        continue;
    }

    // 2. otherwise exploit
    GuidanceResult guidance = session.SuggestAdjustments(best, session.Options.Guidance with { MaximumNormalizedStep = trust });
    var empirical = guidance.Suggestions.Where(s => s.Source == SuggestionSource.Empirical).ToList();
    if (empirical.Count == 0 || trust < 0.005) break;

    SynthParameterState next = best.Parameters;
    foreach (ParameterAdjustmentSuggestion s in empirical)
        next = next.With(s.ParameterId, s.SuggestedValue);

    session.Evaluate(next, synth.Render(next));
    bool improved = ReferenceEquals(session.History.Best, session.History.Latest);
    trust = improved ? Math.Min(0.15, trust * 1.5) : trust * 0.5;      // 3. trust region
}
```

```text
start: loss 0.502, top difference envelope.attack TooLong
explore attack = 95 (gain 1.00) -> loss 0.508
explore cutoff = 2090 (gain 1.00) -> loss 0.476
explore waveform = 1 (gain 0.50) -> loss 0.196
explore attack = 95 (gain 0.89) -> loss 0.207
explore cutoff = 2680 (gain 0.56) -> loss 0.153
exploit cutoff +851, attack -45 -> loss 0.081
explore cutoff = 4121 (gain 0.56) -> loss 0.062
explore cutoff = 4711 (gain 0.53) -> loss 0.048
exploit attack -34 -> loss 0.036
...
exploit attack +9, cutoff +443 -> loss 0.003
best after 15 renders: loss 0.003; cutoff 5154 (wanted 5000), attack 10 (wanted 10), waveform 1 (wanted 1)
```

What happened:

1. With nothing known, the advisor asked for a probe of each parameter (information gain 1.00).
2. It then proposed the untried waveform value. Switching to the square wave more than halved the loss, so that render
   became the new best — a selector is "optimized" simply by trying its values.
3. After the switch the advisor wanted the ordered parameters probed *again*: what was learned with the saw is
   down-weighted for the square, because a different switch setting may behave differently.
4. From there it alternated between confident steps and re-probing the cutoff whenever the best state had moved into
   territory where the old slope no longer applied (remember the steep-then-flat filter response).

A few practical notes:

* **Always continue from `History.Best`**, not from the latest render, so a bad step costs one render and nothing else.
* **The trust region is yours to manage.** `SuggestAdjustments` takes per-call options, so you can shrink the maximum
  step after a rejected step and grow it after a success.
* **Filter on `Source`.** `Empirical` suggestions rest on measurements of your synth. `GenericPrior` suggestions
  (confidence ≤ 0.3) exist only for parameters that have `Categories` and no measurements yet; use them as a first
  guess or ignore them, as above.
* **Watch the diagnostics**: `session.GetProgress()` (`IsStagnating`, improvement rates) and
  `session.DetectOscillations()`. A parameter that keeps reversing calls for smaller steps or a freeze; stagnation
  calls for exploration or a restart elsewhere.
* **Emphasize what matters** by passing `GuidanceOptions.FeatureWeights`, for example weight 0 on
  `ToneFeatureIds.ReleaseTime` while your synth's release is not part of the search.

### Step 10 — Your own optimizer

You do not have to use the guidance. HearSay works as a plain objective function for any optimizer — CMA-ES, Bayesian
optimization, a genetic algorithm. Keep using the session so that the run is recorded:

```csharp
SynthParameterState trial = Mutate(current);                           // your optimizer's proposal
ToneComparisonResult r = session.Evaluate(trial, synth.Render(trial));

double loss = r.Scores.Overall;                                        // scalar objective
ReadOnlyMemory<double> error = r.NormalizedDifferenceVector.Values;    // or a vector-valued objective
```

Work in normalized space (`p.Normalize` / `p.Denormalize`) so one step size fits all parameters. The tutorial runs a
tiny (1+1) evolution strategy for 40 renders:

```text
evolution strategy: loss 0.044 after 41 renders; cutoff 10372, attack 10
strongest correlation: attack ~ envelope.attack  r = +1.00 (n = 41; correlation, not causation)
learned d(attack time)/d(attack) = +276 ms per full range (true value 299), confidence 0.96
```

Two lessons:

* **A blind optimizer needs far more renders.** After 41 renders it is at 0.044 with the cutoff stuck on the flat side
  of the loss seen in step 6; the guided loop reached 0.003 in 15. The scalar loss hides the direction; the difference
  vector and the learned sensitivities do not. A good hybrid: let your optimizer propose, use
  `jacobian.Predict(...)` to discard proposals that are predicted to make things worse, and inject
  `SuggestAdjustments()` steps as extra candidates.
* **The session learns from any run.** Even though every random step moved both parameters at once, the local
  regression separated their effects (slope 276 versus the true 299). `session.GetRelationships()` gives the raw
  correlations over the run — useful for a quick overview, but they are correlations: if your optimizer always moves
  two parameters together, both will correlate with the same feature. Trust the Jacobian's confidences, or probe.

### Step 11 — Checkpoints

Runs against a real synth can take hours. Save and resume:

```csharp
File.WriteAllText("run.json", session.ExportState());

// later, possibly in another process
var session = ToneOptimizationSession.ImportState(File.ReadAllText("run.json"), targetAudio);
```

```text
checkpoint: 22 KiB; restored run continues at iteration 2 with 3 observations
wrong target is refused: The target audio differs from the target this run was recorded with.
```

The state holds every observation, the parameter definitions, the full comparison options, the feature schema with
its scales and the library version — enough to reproduce and interpret the run later. The analysed target is not
stored (it is large and recomputed exactly from the audio with the stored options), so you pass the target audio
again; a different target is refused. The effect model is rebuilt from the observations on import.

---

## Part D — In practice

### Connecting a real synth

`TutorialSynth.Render` is the only thing to replace. Whatever you use — a plug-in host, an offline renderer, a hardware
synth recorded through an interface — aim for this:

* **One note, fixed pitch, fixed velocity, fixed length**, the same for target and candidates. Hold the note long
  enough to reach the sustain (a few hundred ms) and keep recording until the release has died away; otherwise the
  release cannot be measured.
* **Render short.** Cost is linear in length; 0.5–1.5 s is usually plenty.
* **Make renders repeatable.** Free-running LFOs, random phase, unison drift and analog noise make two renders of the
  same patch differ. Reset or disable them if you can. To see your noise floor, render the same patch twice and compare:
  the distance you get is the smallest improvement that means anything, and a sensible stopping threshold.
* **Mono.** Downmix stereo (`WavFile.Read` does), or compare one channel.
* **Report parameters in native units** with honest ranges; the session normalizes them. If a knob is very non-linear
  (frequency in Hz), consider exposing its position (0..1) instead of the value, so that equal steps are roughly equally
  audible.
* **Silence is fine.** A patch that renders nothing yields `Quality.IsUsable == false` and a large fixed distance, not
  an exception. Such observations are recorded but not used for learning.

### Driving it with an agent

An LLM agent can take the place of the strategy in step 9. Give it, per iteration:

1. `HearSayJson.Serialize(result.Differences)` — what is wrong, ranked;
2. `HearSayJson.Serialize(session.SuggestAdjustments())` and `session.SuggestExperiments()` — what the measurements
   recommend and what is still unknown;
3. `HearSayJson.Serialize(session.GetProgress())` and `DetectOscillations()` — whether the run is healthy;

and let it answer with a `SynthParameterState`. Feature ids, directions, units, importance and confidence are all
machine-readable, so the agent never has to parse prose; the only free text in the output is `Reason` and the warning
messages, which are diagnostic. Tag the agent's steps with `OptimizationStepType` so the history shows why each render
was made.

### Troubleshooting

| Symptom | Likely cause and fix |
| --- | --- |
| `PrepareTarget` throws `ArgumentException` | The target is silent, shorter than 30 ms after trimming, or mostly NaN. Check the file and the channel. |
| `Quality.IsUsable == false` | Same, for the candidate; `Quality.Warnings` has the code (`input.silent`, `input.too_short`, ...). |
| Harmonic differences look random | Wrong or missing fundamental. Pass `ExpectedFundamentalHz`; check `Quality.FundamentalHz` and `FundamentalConfidence`, and the `f0.mismatch` warning (candidate more than 50 cents off). |
| `Envelope.Release.Difference` is `null` | One of the sounds has no sustain plateau (plucked, or strong tremolo), or the recording stops before the release. Look at `TargetHasSustain` / `CandidateHasSustain` and record a longer tail. |
| Attack differences have low confidence | The attack is shorter than the envelope's time resolution (about two periods of the note). Compare at a higher note, or accept the lower weight it gets. |
| A large distance for sounds that seem alike | Look at the component scores and the top differences. Usual suspects: loudness (try `TimbreOnly`), a different note length, leading silence longer than the alignment can absorb, noise. |
| `input.clipping` warning | The file is clipped; harmonic levels are distorted. Lower the render level. |
| Suggestions are empty | No parameter has enough measured confidence yet (`GuidanceOptions.MinimumConfidence`). Probe first, or follow `SuggestExperiments()`. |
| Suggested steps keep being rejected | The response is strongly non-linear around the best state. Shrink `MaximumNormalizedStep`, probe again there, and check `DetectOscillations()`. |
| Sensitivities differ wildly between runs | Non-repeatable renders (see above) or probe steps below the synth's resolution; raise `RecommendedMinStep`. |
| Loss stalls above zero | Your synth may simply be unable to make the target. The remaining `Differences` tell you what it lacks. |

Where to go next: the [README](../README.md) for definitions and formulas, `samples/HearSay.OptimizationDemo` for a
larger five-parameter loop with noise, and the XML documentation on the types for every option.
