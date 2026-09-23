# HearSay expert manual

For people who already know what phase modulation is and want the most out of the library, the command line and
HearSay Studio. The [tutorial](tutorial.md) teaches the ideas; [patch-matching.md](patch-matching.md) documents
the design and the evidence. This manual is about getting good patches: what to feed in, which knob does what, how to
read what comes out, and what to do when it is not good enough.

Contents

1. [What HearSay does and does not do](#1-what-hearsay-does-and-does-not-do)
2. [Preparing a target](#2-preparing-a-target)
3. [Target sets: several notes, one patch](#3-target-sets-several-notes-one-patch)
4. [Choosing the synthesis target](#4-choosing-the-synthesis-target)
5. [Budget, seeds and time](#5-budget-seeds-and-time)
6. [Reading a result](#6-reading-a-result)
7. [The result folder](#7-the-result-folder)
8. [Playing the export in SuperCollider](#8-playing-the-export-in-supercollider)
9. [HearSay Studio](#9-hearsay-studio)
10. [The command line](#10-the-command-line)
11. [The library](#11-the-library)
12. [Search options in depth](#12-search-options-in-depth)
13. [The objective and the loss numbers](#13-the-objective-and-the-loss-numbers)
14. [Editing patches by hand](#14-editing-patches-by-hand)
15. [Verifying against the real UGen](#15-verifying-against-the-real-ugen)
16. [Troubleshooting](#16-troubleshooting)
17. [Recipes](#17-recipes)

## 1. What HearSay does and does not do

HearSay takes one dry, monophonic note (or several notes of the same sound) and searches a six-operator
phase-modulation patch that sounds like it, for a synthesis profile it can also export to. It renders every candidate
with a renderer that reproduces the target UGen sample by sample, so what you hear in the preview is what
SuperCollider will play.

It finds a *likeness*, not a clone. The objective compares spectra, partials, envelopes, pitch and long-term
spectrum; two patches that differ in phase or in the exact noise sequence can score the same. For targets that were
made by phase modulation, the search regularly recovers the structure; for acoustic or sampled sounds it finds the
closest thing six operators can do, which is often good and rarely identical.

It does not: model reverb or chorus, handle polyphony or chords, search filters or wavefolders, or use more than six
operators. Those are limits of the profiles, not of the architecture (see the last section of patch-matching.md).

## 2. Preparing a target

The single most effective thing you can do is to give HearSay a clean target.

- **One note, dry.** No reverb tail, no delay, no chorus. A tail is matched by envelopes and detuning, which wastes
  operators and budget.
- **Mono, or a mono-compatible stereo file.** Stereo is mixed down; a wide stereo effect will smear the analysis.
- **Full note, from the attack to silence or to a clear note-off.** The note-off is estimated from the envelope;
  a file that is cut in the middle of the sustain gives a patch with an endless sustain, which is fine if that is what
  you want.
- **Start at the attack.** Leading silence is trimmed, but a fade-in that is not part of the sound will be matched.
- **Loud enough, not clipped.** The analysis is loudness-normalized (reference −20 dBFS RMS), so level does not matter
  as long as the file is not clipped and not buried in noise.
- **Length.** Everything beyond `MaxSeconds` (default 4 s) is ignored. For pads and long decays, 2 to 4 seconds is
  enough; the render length grows with the target, and so does the cost of every evaluation.
- **Sample rate.** Analysis and rendering run at `SampleRate` (default 48000). Use the rate SuperCollider will run at.
  Aliasing of deep modulation is part of a backend-exact sound and it depends on the rate.
- **Pitch.** The fundamental is estimated. When you know it, say so: `file.wav@60` (MIDI note) or `file.wav@261.6`
  (Hz) on the command line, `AudioTarget(samples, rate, ExpectedFundamentalHz: 261.6)` or `MidiNote: 60` in the API,
  the pitch box in Studio. A wrong fundamental makes every ratio wrong; an octave error is the classic case with
  bass sounds and with sounds whose second harmonic is stronger than the first.
- **Velocity.** `file.wav@60:v0.5` records that the note was played at half velocity. It matters only in a target set
  (next section), where it makes velocity sensitivity searchable.

`hearsay analyze target.wav` prints the analysis as JSON: fundamental and its confidence, partials with their
ratios, envelope, note-off, inharmonicity, beating, and a list of issues (for example "no stable fundamental").
Look at it before a long search when the sound is unusual.

## 3. Target sets: several notes, one patch

A single note cannot tell a ratio operator from a fixed-frequency one, nor say how the sound changes with pitch or
velocity. A target set can. Give HearSay the same sound at different pitches and velocities and it searches one
patch that plays all of them:

```
hearsay match piano-c3.wav@48 piano-c4.wav@60 piano-c5.wav@72 piano-c4-soft.wav@60:v0.4 --budget 20000
```

What becomes searchable with a set:

- **Key scaling**: level and envelope rate per octave.
- **Velocity sensitivity** of every operator, when at least two velocities are given.
- **Fixed-frequency operators**: a partial that does not move with pitch across the set is proposed as a fixed
  operator, both in the hypotheses and as a topology mutation.

The cost: one evaluation renders every note, so a set of four notes costs four renders per evaluation. The coarse
screening of hypotheses uses the reference note only (the first target), everything after that uses the whole set.
The reference note also drives the preview and the comparison shown in Studio and in the result folder.

Rules of thumb: three pitches spanning two octaves and one softer velocity cover a keyboard instrument well; the
notes must really be the same sound (same preset, same articulation), otherwise the search settles for a compromise
that fits none of them.

## 4. Choosing the synthesis target

| Profile | Operators | Extra per operator | Needs in SuperCollider |
| --- | --- | --- | --- |
| `supercollider-fm7` (default) | 6 sine, full matrix, self and circular feedback, ratio or fixed frequency, independent outputs, dynamic matrix | – | sc3-plugins (`FM7`) |
| `supercollider-fmx6` | the same, plus triangle, saw, square, pulse, white noise and sample-and-hold noise | harmonic limit 1..128 (brightness of a band-limited wave), pulse duty cycle (`Shape`) | the [FMX6 plugin](https://github.com/shimpe/fmx6) |

When to pick FMX6: the target has a dense harmonic series that falls slowly (saw-like), odd harmonics only
(square or triangle-like), a pulse-like spectrum with notches, or a noisy attack. The harmonic-waveform prior reads
the partial cloud and starts the search from the matching waveform, which sine stacks cannot reach in a comparable
budget. On the synthetic waveform targets in patch-matching.md the loss halves or better.

When to stay with FM7: the target is itself an FM sound, or you need sc3-plugins compatibility only. Every FM7 patch
is also an FMX6 patch (sine is waveform 0), so a patch searched under FM7 can be exported for FMX6 later by
searching again under FMX6 or by editing the profile of the export (section 14).

## 5. Budget, seeds and time

The budget is a number of candidate evaluations, not seconds. One evaluation renders the candidate at every target
note and scores it. The search spends the budget in stages (screening 5 %, survivors 15 %, deep optimization 35 %,
finalists and refinement the rest) and stops when it is used up, so a search is reproducible from its seed and its
budget.

| Budget | Typical use | One note at 48 kHz, 1.5 s, 8 cores |
| --- | --- | --- |
| 2 000 – 4 000 | quick look, is the structure findable at all | 20 – 60 s |
| 20 000 (default) | a serious single-seed search | 3 – 6 min |
| 50 000 – 100 000 | final search on a target you care about | 10 – 30 min |

Time scales with target length, with the number of notes in the set, and with the number of enabled operators in the
candidates (an all-six-operator patch renders slower than a two-operator one). `--parallel` changes speed only,
never the result.

**Seeds.** A search is deterministic; another seed is another search. The best remedy for an unlucky run is
`--seeds 3` (Studio: the Seeds box), which runs three seeds with the full budget each and returns the result of the
best one, with the finalists of every seed as alternatives. Two seeds at budget N usually beat one seed at 2N, because
the second seed starts from other hypotheses; beyond four seeds the gain flattens.

**When to raise the budget instead:** when the search summary shows the loss still falling in the last stages
(deep optimization and finalists improve markedly on the survivors). When the loss stops early and the finalists all
have the same structure, another seed or another profile helps more than budget.

## 6. Reading a result

Every result reports the same numbers, in Studio, on the console and in `search-summary.json`.

- **Audio loss** is the objective value of the best patch (section 13). Lower is better, 0 is identical. Rough scale
  for a single note: below 0.05 very close; 0.05 – 0.15 the same instrument, audibly different in detail; above 0.25
  a different sound. Noise-bearing targets keep a floor near 0.08 because the noise sequence never matches.
- **Initial loss** is the best untouched hypothesis, before any optimization. The ratio initial/final says how much
  the continuous optimization contributed; a ratio near 1 with a high loss means the structure was never right.
- **Similarity** is HearSay's own comparison of preview and target (0..1), the same number the tone comparer gives
  anywhere else. It weights loudness and timbre differently from the loss and is the better number to show a
  musician.
- **Structure** (`op1:PhaseModulation/Sine/r1/out;op2:…|2>1;2>2;|pitch`) is the regime key: operator by operator
  its mode, waveform and ratio or fixed frequency (`f` plus semitone), `/out` for operators that are heard, then the
  edges `source>destination` (`~` marks an enveloped edge), then `|pitch` when the patch has pitch modulation.
- **Origin** is the hypothesis and the mutations that produced the patch, with the seed when several were run:
  `seed 1235: stack 1:3 with modulator feedback; add OP3 at 3.5 modulating OP1`. It tells you which prior found the
  structure and how far it travelled from it.
- **Stages** list, per stage, the evaluations spent and the best loss at the end, with the evaluation level: coarse
  (short render, few features), normal, detailed (full objective). A patch that looked great at the coarse level and
  fell back at the normal level is a typical screening artefact; the survivors stage corrects it.
- **Basins** list every structure the search tried, with its fate: survivor, finalist, replaced by mutation, retired.
  A finalist that was "replaced by mutation" three times in a row is a structure that kept finding improvements by
  adding operators; consider a higher budget.

The alternatives are the other finalists, one per structure, best first. Listen to them: the loss orders patches by
spectrum and envelope, and the second-best is sometimes the more musical one.

## 7. The result folder

`hearsay match … --output dir` and Studio's "Save result folder" write:

| File | Content |
| --- | --- |
| `patch.json` | the best patch in the canonical model, editable and re-exportable |
| `patch.scd` | the SuperCollider SynthDef with a usage example; needs only SuperCollider and the UGen |
| `preview.wav` | the best patch rendered at the reference note by the backend-exact renderer |
| `analysis.json` | analysis of the reference note (fundamental, partials, envelope, issues) |
| `comparison.json` | HearSay's full diagnosis of preview against target: scores, differences, hints |
| `preview-note2-262Hz.wav`, `analysis-note2-262Hz.json`, `comparison-note2-262Hz.json` | the same per note of a target set |
| `search-summary.json` | reproducibility data, stages, basins, and all finalists with their losses |
| `alternatives/01-patch.json`, `01-preview.wav`, `01-patch.scd`, `02-…` | the other finalists, best first, each with its own SynthDef name (`HearSayAlternative1`, …) |

`comparison.json` is the file to read when a match is close but not right: it names the frequency bands and time
segments that differ most and gives hints in the vocabulary of the comparer ("the candidate decays faster above
2 kHz", "the second harmonic is 6 dB too strong").

## 8. Playing the export in SuperCollider

`patch.scd` defines a SynthDef with the controls `freq`, `gate`, `velocity` (0..1), `amp` and `out`, followed by two
commented examples (a single note, a `Pbind`). Ratio operators follow `freq`; fixed operators do not. Key scaling is
computed from `freq.cpsmidi` inside the SynthDef, so the patch behaves across the keyboard the way the target set
taught it.

The generated code uses the full matrix interface (`FM7.ar(ctlMatrix, modMatrix)` or
`FMX6.ar(ctlMatrix, modMatrix, waves:, shapes:, harmonicLimits:)`), never the algorithm helpers. All operator
controls are control-rate envelopes; the server's block size must be 64 (the default) for the export to be
sample-exact with the preview.

The synth frees itself when its longest envelope has finished after `gate` goes to 0 (`Done.freeSelf`); a patch with
one-shot envelopes only frees itself after the longest one, so it can be played without a gate.

## 9. HearSay Studio

Studio is the library with a window around it. Everything it does is available in the API.

**Header.** Target (FM7 or FMX6), Budget, Seed, Seeds, Run, Stop. Stop returns the best patch found so far; the
progress bar shows the run within the seeds and the stage within the run.

**Target notes.** Add one or more WAV files; each is analysed at once and shows its fundamental, length, note-off,
partial count and inharmonicity. Override the pitch (MIDI note or Hz) and the velocity per note when known. The play
button plays the file as it is. Several notes make a target set (section 3).

**Result.** Four tiles (initial loss, final loss, similarity, renders and time with the seed), a similarity bar per
note, the list of finalists, the keyboard, the modulation matrix and the routing summary.

- **Finalists**: rank, audio loss, operators in use, origin with seed. Select one to inspect it: matrix, summary and
  code follow the selection, and the keyboard plays it. The header of the code says whether it is the best patch or
  an alternative. The tiles and "Save result folder" always describe the whole result.
- **Keyboard**: MIDI 0..127, scrollable, opens centred on C4 for every new result. Amber dots mark the target notes.
  Pressing lower on a key plays louder (velocity 0.3..1.0). A patch without velocity sensitivity is played at a
  volume that follows the key velocity instead, and the caption says so. One gain per shown patch, taken from the
  reference note at full velocity, so level differences between keys are real.
- **Matrix**: rows are destinations, columns sources, values in radians; the diagonal is self-feedback.
  Off operators leave their cells empty.

**SuperCollider patch.** The code of the shown finalist. Copy puts it on the clipboard; Save result folder writes
the whole folder of section 7 for the result.

Studio plays through OpenAL Soft. Without an audio device the keyboard is silent and a note says so; everything else
works.

The logo in the header and the window icon are one vector drawing (`HearSayLogo`): six operators on a ring, the
chords of a modulation matrix, a wave through them. `HearSay.Studio --logo out.png [size]` writes it as a PNG and
exits, for packaging.

## 10. The command line

```
hearsay match <target.wav>... [--profile p] [--budget n] [--seed n] [--seeds n] [--output dir]
              [--note midi | --f0 hz] [--sample-rate hz] [--max-seconds s] [--parallel n] [--quiet]
hearsay analyze <target.wav>
hearsay profiles
hearsay reference [name] [--notes 48,60,72] [--velocities 1,0.5] [--seconds 1.0] [--release 0.3] [--output dir]
```

Targets carry their pitch and velocity in the file argument: `file.wav@60`, `file.wav@261.6`, `file.wav@60:v0.5`.
`--note` and `--f0` apply to targets without one.

`hearsay reference` renders a built-in reference patch to WAV files, which is the quickest way to get known targets
for experiments: `bass`, `bell`, `brass-like`, `bright-fm`, `circular-routing`, `detuned`, `electric-piano`,
`electric-piano-keyed`, `feedback-heavy`, `metallic-inharmonic`, `multi-output`, `pure-sine`, `simple-2op`. The
folder also gets `hidden-patch.json`, the patch that made the files, to compare against.

The exit code is 0 on success, 1 on a usage error or an unusable target; progress goes to standard error.

## 11. The library

```csharp
using HearSay;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Profiles;

AudioNote note = WavFile.Read("target.wav");
var target = new AudioTarget(note.Samples, note.SampleRate, MidiNote: 60);

var options = new PatchMatchOptions
{
    SynthesisProfile = SynthesisProfiles.SuperColliderFmx6,
    EvaluationBudget = 20000,
    RandomSeed = 1,
};
var progress = new Progress<SearchProgress>(p => Console.Error.WriteLine($"{p.Stage}: {p.EvaluationsUsed}/{p.EvaluationBudget} best {p.BestLoss:F4}"));

PatchMatchResult result = new OperatorMatrixPatchMatcher().MatchBestOf(target, seedCount: 2, options, progress);

OperatorMatrixPatch patch = result.BestPatch;                       // canonical model
string scd = SynthesisBackends.ExporterFor(result.Profile)!.Export(patch);
IReadOnlyList<string> files = PatchMatchResultWriter.Write(result, "out");
```

Entry points:

- `OperatorMatrixPatchMatcher.Match(target | targets, options, progress, token)`: one search.
  `MatchAsync` for a UI thread; `MatchBestOf(…, seedCount, …)` for several seeds. Cancellation returns the best
  patch so far, never throws.
- `PatchMatchResult`: `BestPatch`, `AlternativePatches` (all finalists, best first, `Loss`, `AudioLoss`,
  `Complexity`, `Terms`, `Regime`, `Origin`), `Comparison` (the tone comparison of preview against the reference
  note), `PreviewAudio`, `Notes` (per target note: analysis, request, preview, comparison), `SearchSummary`,
  `Analysis`, `Request`, `Profile`.
- `SynthesisBackends.RendererFor(profile)`, `ExporterFor(profile)`, `PriorProvidersFor(profile)`: the one place
  that ties a profile to its implementations.
- `PatchMatchResultWriter.Write(result, directory)`: the folder of section 7.
- `TargetAnalyzer.Analyze(target)`: the analysis alone, for inspection or for your own priors.
- `OperatorMatrixPatchSearchCoordinator(renderer, providers).Search(target, options, progress)`: the search
  without the facade, for experiments with other renderers and prior providers.

Rendering a patch yourself: `SynthesisBackends.RendererFor(profile).Render(patch, RenderRequest.Create(hz,
noteSeconds, releaseSeconds))` returns samples at the requested rate. `RenderRequest` also takes a MIDI note (for key
scaling), a velocity and a sample rate.

Threading: the matcher is thread-safe and stateless; evaluations run in parallel up to
`MaxParallelEvaluations`. Results are immutable records.

## 12. Search options in depth

`PatchMatchOptions` is a record with `init` properties; use `with` to change a few. Defaults in brackets.

| Option | Effect | When to change |
| --- | --- | --- |
| `EvaluationBudget` [20000] | total candidate evaluations | see section 5 |
| `RandomSeed` [1234] | every random decision | another search of the same target |
| `MaxParallelEvaluations` [cores] | speed only | limit CPU use |
| `SynthesisProfile` [FM7] | what may be searched and how it is rendered | FMX6 for non-sine or noisy targets |
| `SampleRate` [48000] | analysis and render rate | the rate SuperCollider will run at |
| `Velocity` [1.0] | velocity of rendered candidates for a target without one | rarely |
| `MaxSeconds` [4] | longest matched stretch | shorter for speed on long targets |
| `HypothesisCount` [128] | structures screened | more when a prior produces many good structures (watch the screening stage) |
| `SurvivorCount` [32] | structures that get a short optimization | more for very varied targets |
| `BasinCount` [6] | structures optimized deeply | more to keep unusual structures alive; costs budget per basin |
| `FinalistCount` [3] | patches refined and reported | more alternatives |
| `ComplexityWeight` [0.02] | tie-breaker that prefers simpler patches | raise to 0.05 for cleaner patches at a small loss cost; 0 to ignore complexity |
| `Complexity` | per-operator, per-edge, per-tiny-contribution and per-envelope-point weights | shape what "simpler" means |
| `Objective` | objective weights and analysis options | section 13 |
| `StagnationGenerations` [25], `MinimumRelativeImprovement` [0.01] | when a continuous run counts as stuck | lower for faster restarts on rough landscapes |
| `MaxRestartsPerBasin` [3] | topology-mutation rounds for a stuck basin | more for structures that need several additions |
| `MaxActiveParameters` [32] | largest CMA-ES dimension; bigger patches are optimized group by group | rarely |
| `ScreeningShare` [0.05], `SurvivorShare` [0.15], `DeepShare` [0.35], `FinalistShare` [0.75] | budget split | move budget towards the stage that still improves in the summary |

The search keeps a hypothesis cut per prior provider (each provider's best in turn), so a provider with modest scores
still reaches screening. Structural families (heard/shaping operators, feedback or not, waveforms in use) each keep
their best basin after the survivor round.

## 13. The objective and the loss numbers

The audio loss is a weighted sum of seven terms, each 0 for identical audio:

| Term | Weight | Measures |
| --- | --- | --- |
| `Stft` | 0.25 | multi-resolution log-magnitude spectrogram distance |
| `Mel` | 0.15 | log-mel spectrogram distance (perceptual balance) |
| `Partial` | 0.20 | partial cloud: which harmonics exist and how strong |
| `Trajectory` | 0.15 | how partials and bands evolve over time |
| `Envelope` | 0.15 | amplitude envelope in dB |
| `Pitch` | 0.05 | pitch trajectory |
| `Ltas` | 0.05 | long-term average spectrum |

`ObjectiveOptions.Weights` changes them; `TimbreNormalized` (default true) compares timbre at equal loudness;
`UnusableLoss` (10) is the value of a render that clipped, went silent or became NaN.

Practical readings of the terms (`AlternativePatches[i].Terms`):

- high `Envelope`, low `Partial`: right sound, wrong dynamics; the envelope priors were off, budget or a target with a
  cleaner note-off helps;
- high `Partial`, low `Envelope`: right shape, wrong harmonics; consider FMX6 or more seeds;
- high `Trajectory` only: the brightness evolves differently; usually a modulator envelope, often fixed by more budget;
- high `Pitch`: the target has vibrato, a pitch envelope or an unstable fundamental; check the analysis.

Evaluation levels: **coarse** uses the long-term spectrum, a rough spectrum and a rough envelope, enough to sort
out hypotheses that are obviously wrong; **normal** adds the multi-resolution STFT, mel, the partial set, spectral
and band trajectories and the envelope; **detailed** adds the pitch trajectory and the decay stages and is used for
the final ranking. Losses are comparable within a level only.

## 14. Editing patches by hand

`patch.json` is the canonical model, and everything accepts it: `OperatorMatrixPatch.FromJson` /
`ToJson`, the renderers, the exporters, the validators. Edit it to nudge a result, then export again:

```csharp
OperatorMatrixPatch patch = OperatorMatrixPatch.FromJson(File.ReadAllText("patch.json"));
patch = patch.WithOperator(1, patch.Operators[1] with { VelocitySensitivity = 0.7 })
             .WithEdge(new ModulationEdge(1, 0, true, 1.8));
IReadOnlyList<string> problems = patch.Validate();      // empty when the model is consistent
File.WriteAllText("patch.scd", new SuperColliderFmx6Exporter().Export(patch));
```

Per operator: `Enabled`, `Mode`, `Waveform`, `Frequency` (`AtRatio(r)` or `AtFixed(hz)`), `FineTuneCents`,
`DetuneHz`, `InitialPhase`, `AmplitudeEnvelope` (points with level, time, curve and an optional sustain point;
`Adsr`, `TwoStageDecay` and `Constant` build common ones), `VelocitySensitivity`, `KeyScaling` (level dB per octave,
rate per octave, reference note), and for FMX6 `Shape` (pulse duty) and `HarmonicLimit`. Per patch: `OutputLevels`,
`ModulationMatrix` (edges `SourceOperator`, `DestinationOperator`, `Enabled`, `Amount` in radians, optional
`AmountEnvelope`), `Pitch` (envelope and vibrato).

Validation refuses what no backend can play (amounts beyond the profile's radians per destination, shapes outside
0.01..0.99, harmonic limits outside 1..128); the exporter of a profile refuses what that UGen lacks (a saw operator
under FM7) with a `PatchNotSupportedException` that lists the reasons. Nothing is approximated silently.

Canonical form: `PatchCanonicalizer.Canonicalize(patch)` puts the operators that feed the output first, then orders
by frequency and place in the routing, resets inaudible operators and removes edges that cannot be heard; the result
renders the same audio, and two patches that are the same structure get the same regime key. The search works in
canonical form; hand-edited patches need not be canonical.

## 15. Verifying against the real UGen

`HearSay.SuperCollider` can render through scsynth in non-realtime mode and compare with the offline renderer:

```csharp
SuperColliderInstallation sc = SuperColliderInstallation.Find()!;   // or set HEARSAY_SCLANG to sclang
var backend = new SuperColliderFm7Renderer(sc, new SuperColliderFmx6Exporter(), SynthesisProfiles.SuperColliderFmx6.Capabilities);
var validator = new BackendEquivalenceValidator(new Fmx6CompatibleOperatorMatrixRenderer(), backend);
BackendExportValidation v = validator.Validate([(patch, RenderRequest.Create(196, 0.7, 0.25))])[0];
// v.IsEquivalent, v.OverallDistance, v.MaxSampleDifference
```

Patches without feedback loops are sample-exact (differences around 1e-6). Loops are chaotic: the two renders stay
the same sound but part ways sample by sample, sooner with band-limited waveforms than with sines, because a
rounding difference can change the harmonic count of a sample. Judge looped patches by `OverallDistance`, not by
sample difference. The test suite runs this on random patches of both profiles whenever SuperCollider is installed.

## 16. Troubleshooting

**"Target cannot be analysed"** or issues in the analysis: no stable fundamental (noise, chords, too short), silence,
clipping. Check `hearsay analyze`; give the pitch explicitly; trim the file to one note.

**The fundamental is an octave off.** Give it (`@60`, `--f0`, the pitch box). Bass sounds and sounds with a weak
first harmonic are the usual cases.

**All finalists have the same structure and the loss is high.** The hypotheses did not contain the right structure.
Another seed changes the mutations; FMX6 changes the waveforms; a target set can reveal a fixed operator. Look at
`search-summary.json`: if the screening stage already stood at the final loss, the priors are the limit.

**Loss keeps falling until the end.** Budget. Double it, or move `DeepShare`/`FinalistShare` up.

**The preview is right, SuperCollider sounds different.** Block size not 64; a different sample rate than the one
searched at; sc3-plugins or FMX6 not installed or old (the header comment of `patch.scd` says which). Run the
equivalence check of section 15.

**The patch has an endless sustain.** The target had no note-off in the file; cut the file where the note ends, or
edit the envelopes' sustain points.

**Noisy targets under FMX6 score around 0.08 at best.** That is the realization floor: the noise operator is found,
its sequence never matches. Judge by ear and by `Similarity`.

**Studio's keyboard is silent.** No audio device could be opened (OpenAL Soft). The note under the keyboard says so.
Everything else works; save the folder and play `preview.wav`.

**Git or file operations fail while a search runs.** Unrelated to HearSay; a virus scanner holding files is the
usual cause on Windows.

## 17. Recipes

**A DX-style electric piano from a sample.** Three pitches and two velocities from the same preset, FM7, budget
20000, seeds 2. Expect a 2:1 or 14:1 stack with velocity on the modulator and a fixed-frequency "tine" operator when
the set spans two octaves.

**A synth brass or string patch.** FM7, one note of 2 s with the release, budget 20000. Brightness that grows during
the attack comes from an enveloped edge (`~` in the regime key); if the finalists lack it, raise `MaxRestartsPerBasin`.

**A saw or square lead, or a chip-tune pulse.** FMX6, budget 4000 is enough to land on the waveform; the harmonic
limit sets the brightness (the search halves and doubles it; edit it by hand for fine steps).

**A percussive hit with a noisy attack.** FMX6; the parallel white-noise burst hypothesis with a short envelope is
usually the winner. The tail is a sine or a short stack.

**A bell.** FM7, one note of 3 s, budget 30000 or more: inharmonic partials need ratios like 3.5 or 1.41 and
several modulators; alternatives are worth a listen because two very different structures often tie.

**Reproducing a result later.** Keep `search-summary.json`: profile, seed, budget and the options give the same
search on the same target; the matcher is deterministic and independent of the degree of parallelism.
