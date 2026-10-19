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
| `supercollider-fmx6` | the same, plus triangle, saw, square, pulse, white noise and sample-and-hold noise | harmonic limit 1..128 (brightness of a band-limited wave), pulse duty cycle (`Shape`), an optional filter (`Filter`) | the [FMX6 plugin](https://github.com/shimpe/fmx6); with operator filters for patches that use them |
| `supercollider-nome6` | the FM7 matrix with Jacobi elliptic operators, 2x oversampled, plus noise operators on one shared noise source | elliptic parameter m (`Shape`, 0 = sine), sn-to-sd morph (`Morph`), phase and m roughness by the noise (`NoisePhase`, `NoiseM`); per patch the noise colour and cutoff (`Noise`) | the Nome6 plugin with noise |

When to pick Nome6: odd harmonics that fall geometrically (a reed, a clarinet-like or hollow tone), where one elliptic
operator replaces a stack, or a tone with noisy roughness rather than a separate noise wash. Its noise is seeded per
synth in SuperCollider, so a noisy Nome6 export sounds alike, not sample-identical, to the preview.

When to pick FMX6: the target has a dense harmonic series that falls slowly (saw-like), odd harmonics only
(square or triangle-like), a pulse-like spectrum with notches, or a noisy attack. The harmonic-waveform prior reads
the partial cloud and starts the search from the matching waveform, which sine stacks cannot reach in a comparable
budget. On the synthetic waveform targets in patch-matching.md the loss halves or better.

When to stay with FM7: the target is itself an FM sound, or you need sc3-plugins compatibility only. Every FM7 patch
is also an FMX6 patch (sine is waveform 0), so a patch searched under FM7 can be exported for FMX6 later by
searching again under FMX6 or by editing the profile of the export (section 14).

### Operator filters (FMX6)

Every FMX6 operator can have a filter between its waveform and its amplitude. What the operator sends, to the output
and to the operators it modulates, is the filtered signal: a low-pass on a saw modulator softens the modulation, a
filter on a carrier shapes the heard sound. All filters are TPT state-variable filters in double precision, rendered
sample-exactly like the UGen.

| Type | Frequency | Parameters the search tunes (and fine-tuning moves) |
| --- | --- | --- |
| low-pass, high-pass (2 or 4 poles) | `CutoffRatio` x the note, 0.25..64 | `Q` 0.5..20 |
| band-pass, notch | `CutoffRatio` x the note, 0.25..64 | `Q` 0.5..20 |
| peak | `FixedHz` 80..8000 Hz | `Q` 0.5..20, `PeakGainDb` -24..24 |
| formant | F1 = `FixedHz` 80..8000 Hz, F2 = F1 x `FormantRatio` 1..8 by search (up to 32 by hand or if the patch already has more), F3 = F1 x `Formant3Ratio` 1..16 by search (32 by hand) | `Q` 0.5..50 (F1, and every formant without a Q of its own), F2: `FormantLevel` 0..1 and its own Q; F3: `Formant3Level` 0.001..1 by search (0, off, by hand) and its own Q |

The formant filter has up to five formants, each a band-pass peak of unity gain: F1 in hertz (`FixedHz`) with the
filter's `Q`, and F2..F5 at a ratio to F1 (1..32 by hand; the search keeps F2 within 8 and F3 within 16, so that moving F1 moves the whole vowel), each with a level
relative to F1 (0..1; F3, F4 and F5 at 0 are off) and its own Q (0.5..50; 0, the default, plays at the filter's `Q`).
The search uses F1–F3 on every formant filter: it gives F2 its own Q and F3 its ratio, level and own Q (both own Qs
relative to `Q`, so that moving `Q` alone moves a formant without a Q of its own with it), and never sets F4 or F5. On
a filter with F3 off it starts from a default F3 (3 x F1, or 1.4 x F2 when that is higher, at the level floor 0.001
with the filter's `Q`); the filter stays as it is until the search moves one of those values, and from then on F3's
level stays at 0.001 or more. Those come from the vowel
presets of the Studio editor (section 9, *Editing a sound*) or from typing them. A formant filter with only F1 and F2
at one Q exports the filter rows of before; with an own Q on F2 or F3..F5 on, its rows carry the extra formants and
need the FMX6 plugin with five formants (the header comment of the export says so).

An optional envelope moves the frequency, `frequency x 2^(EnvelopeOctaves x env)` with `EnvelopeOctaves` -4..4 and
levels 0..1; it uses the operator's key scaling of time. Key-tracked frequencies follow the note, not the pitch
envelope or the vibrato; peak and formant frequencies stay fixed in hertz, as formants and body resonances do. The
type and the number of poles are structural: the search changes them only by mutation. Fine-tuning, the last stage of a search, moves the filter values too: cutoff, frequencies, Q, the formant ratios, levels and Qs, the peak gain and the sweep depth (`EnvelopeOctaves`).

Where filters come from:

- **Priors.** The formant prior reads the target's formants from the harmonic levels, compared with a sawtooth,
  without smoothing them: the lowest three maxima (at most 36 dB below the strongest), F1, F2 and F3, each with a
  resonance fitted through its harmonic and the two next to it, which gives its centre between the harmonics and its
  own width (Q up to 50); the widths come out broad, though, as the harmonics hardly show how narrow a peak is, so
  the search then narrows them. It proposes a saw or pulse carrier through a formant filter on them (and variants with the
  two strongest, with one peak and with F1 + F3); every one of these filters has F3, the heard one or the search's
  default. Formants less than about two harmonics apart merge into one peak, so at high notes (a soprano at 220 Hz
  and up) F1 and F2 of "a", "o" and "u" are read as one. The subtractive prior proposes a saw or square through a
  low-pass at the roll-off of the harmonics, 4 poles when they fall 18 dB per octave or faster, with a sweep when the
  brightness moves during the note.
- **Mutations.** Screened like the other structural steps: add a low-pass, high-pass, band-pass, peak or formant
  filter with starting values from the target; remove a filter; switch 2 and 4 poles or low-pass and band-pass; move
  a formant filter's F3 to the target's third formant (when that lies above the filter's F2); add the sweep read from
  the target's brightness.
- **Structure library.** About 30 % of the FMX6 library patches have a filter. The library is version 6 for FMX6
  only (every one of its formant filters has a third formant, and every operator with a formant filter has at least
  64 harmonics, so that the upper formants have harmonics to shape), so it is rebuilt once after upgrading; the FM7
  and Nome6 libraries are kept.

A filter adds 0.05 to the complexity and its envelope another 0.03, so between two patches that sound alike the one
without a filter wins. A patch without filters exports exactly as before and runs on FMX6 builds without operator
filters; a patch with a filter passes `filters:` to `FMX6.ar` and needs the FMX6 plugin with operator filters.

Resonance adds level. A low-pass or high-pass with a high Q passes a harmonic near its cutoff about Q times louder
(20 times at Q 20), and a peak adds its gain (+24 dB is 16 times). On a modulator this deepens the modulation beyond
what the edge amount says, and the limit on radians per destination does not count it; on a carrier the export can
peak well above full scale. Lower `amp` in SuperCollider, or the output level of the operator, if a resonant patch
clips. By default the search loudness-matches every candidate to the target (`TimbreNormalized`), so nothing
steers it away from such patches.

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
| `patch.scd` | the SuperCollider SynthDef at HearSay's level (section 8), with a usage example; needs only SuperCollider and the UGen |
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
`FMX6.ar(ctlMatrix, modMatrix, waves:, shapes:, harmonicLimits:)`, plus `filters:` when an operator has a filter),
never the algorithm helpers. All operator
controls are control-rate envelopes; the server's block size must be 64 (the default) for the export to be
sample-exact with the preview.

The synth frees itself when its longest envelope has finished after `gate` goes to 0 (`Done.freeSelf`); a patch with
one-shot envelopes only frees itself after the longest one, so it can be played without a gate.

The code carries HearSay's level, so that the sound is as loud in SuperCollider as in the Studio: the last lines of the
SynthDef multiply the output by the gain HearSay plays the sound at, the gain that brings its reference note at full
velocity to a peak of 0.5 (a quiet patch, such as a narrow pulse through formants, gets a large one):

```supercollider
	// Level: x 3.2109, the gain HearSay plays this sound at; amp scales on top.
	Out.ar(out, (signal * amp * 3.2109) ! 2);
```

`amp` (default 0.2) scales on top of it; at `amp` 1 a note peaks where it does in the Studio. A result folder's
`patch.scd` has the gain of the searched note (each alternative its own); the Studio's code view, Copy, Save… and a
favourite's `patch.scd` have the gain the Studio plays the shown version at: an edit's is the original's or its matched
one, a favourite's the one it kept, else that of the library's reference note. The code view shows the code without the
level until the gain is known, a moment after a sound is shown. `IPatchExporter.Export` adds it only when
`PatchExportOptions.Gain` is set; `ExportSynthDef` never does. An edit plays, and exports, at its original's gain
until Match loudness is used, so a much louder edit can exceed full scale in SuperCollider at the default `amp`: lower
`amp` or match the loudness. HearSay plays a patch without velocity sensitivity with
the key velocity as volume; the SynthDef does not, so such a patch plays at full level at any `velocity`.

## 9. HearSay Studio

Studio is the library with a window around it. Everything it does is available in the API.

**Header.** MIDI in (see *Playing like a synth* below), Target (FM7, FMX6, Nome6, or All), Budget, Seed, Seeds,
Library, Run, Stop. Stop returns the best patch found so far; the progress bar shows the run within the seeds and the stage within the run. Library (on by default)
adds the nearest patches of the structure library as extra starting points (`StructureLibrarySize`, section 12); the
first search of a target builds its library once, which the status line says. Off, the whole budget goes to the
structures read from the target.

Every target keeps its last result for the current target set: search under FM7, switch the Target selector to
FMX6, search again, and switch back and forth to compare the two by ear on the keyboard, in the matrix and in the
code. "All" runs the three searches one after the other with the full budget and seeds each, keeps every result
and shows the one with the lowest loss; the status line has all losses and similarities, and selecting a single
target shows that one. The result header says which results are kept; adding a target file discards them all. A search
is reproducible, so a target whose kept result was searched with the same inputs (the same target files, unchanged on
disk, with the same pitch and velocity, budget, seed, number of seeds and library setting) is not searched again: after
a Nome6 search, "All" searches only FM7 and FMX6, and the status line says how many results were kept. A search that
was stopped early is always searched again.

**Target notes.** Add one or more WAV files; each is analysed at once and shows its fundamental, length, note-off,
partial count and inharmonicity. Override the pitch (MIDI note or Hz) and the velocity per note when known. The play
button plays the file as it is. Several notes make a target set (section 3).

**Result.** Three tiles for the selected finalist (its audio loss, with the search's initial loss and how much better
or worse it is; its similarity; the search's renders and time with the seed), a similarity bar per note for the
selected finalist, the list of finalists, the keyboard, the modulation matrix and the routing summary.

- **Finalists**: rank, operators in use, origin with seed. Select one to inspect it: the tiles, the per-note bars,
  matrix, summary and code follow the selection, and the keyboard plays it. The search compares only the best patch
  with the targets; another finalist is compared the first time it is selected (one render per target note, a moment
  in which its similarity reads …), and kept. The header of the code says whether it is the best patch or an
  alternative. "Save result folder" always saves the whole result.
- **Keyboard**: MIDI 0..127, scrollable, opens centred on C4 for every new result. Amber dots mark the target notes.
  A key sounds while it is held, as on a synth (see *Playing like a synth* below). Pressing lower on a key plays
  louder (velocity 0.3..1.0). A patch without velocity sensitivity is played at a volume that follows the key velocity
  instead, and the caption says so. One gain per shown patch, taken from the searched note at full velocity, so level
  differences between keys are real.
- **Matrix**: rows are destinations, columns sources, values in radians; the diagonal is self-feedback.
  Off operators leave their cells empty.

**SuperCollider patch.** The code of the shown finalist, at the level HearSay plays it at (section 8). Copy puts it
on the clipboard; Save result folder writes the whole folder of section 7 for the result. **Find in library** opens
the Library tab on the library of the result's profile and places the shown finalist among the library sounds like
it, as an imported sound: a star, selected and played, with its neighbours around it. It joins the imported sounds as
*search best* or *search #2*, so several finalists can be compared there.

**Block diagram.** The second tab of the patch card draws the operators in use (greyed when they cannot be heard; see
*Editing a sound* below): carriers on the bottom row
feeding OUT with their output levels, every modulator above what it modulates, edges with their amounts in radians
(dashed amber when they feed back, a loop for self-feedback, and a small sketch beside the amount when it follows an
envelope; the pointer on an amount shows the edge with its envelope breakpoints), and an operator filter as a strip at
the bottom of its box. Each box gives the frequency and a short waveform name (*tri*, *sqr*, *pls0.3* with its duty,
*S&H*, *fnoise*, *ell q0.26* with the nome), and a sketch of its amplitude envelope, time on a square-root scale so
that a 5 ms attack and a 2 s decay both show, with a flat stretch where the note is held; a filter sweep is a violet
line over it, rising for a sweep up and hanging from the top for a sweep down. Resting
the pointer on a box shows everything about the operator in full: tuning, waveform parameters, filter, shared noise,
the envelope breakpoints, velocity sensitivity, key scaling and start phase. On Nome6, the shared noise source is a box at the top left with its colour
(*white*, or for example *LP 60% · 1.9 kHz*), and a dotted line runs into every operator that plays it (*plays*) or
that it roughens (*φ 0.4*: radians of phase, *m -0.2*: the elliptic parameter). A patch whose pitch moves has a PITCH
box at the top right: the vibrato rate and depth (*vib 5.5 Hz ±14 ct*) and the pitch envelope sketched around a zero
line, up and down in cents. It moves the ratio operators only; fixed-frequency ones keep their pitch. Resting the
pointer on the box shows the breakpoints in cents and the vibrato delay and fade-in.

**Start from scratch.** While a tab shows no sound (the Search tab before a search, or once its result is gone; the
Library tab before a sound is clicked), its empty block diagram has a **Start from scratch** button. It shows a new
sound, *New sound*: OP1 a sine at ratio 1 with a plain envelope (attack 10 ms, decay 0.3 s to sustain 0.7, release
0.3 s), heard at 0.8, the other operators off. It is on the profile of the Target selector in the Search tab and on the
profile of the library shown in the Library tab, and plays at the level of the library's reference note. From there it
is edited like any other sound (*Editing a sound* below): add modulators, filters and the rest, play it on the
keyboard or the MIDI device, Copy or Save its code (the header says *Made from scratch in HearSay Studio.*), keep it
with ☆ (the status line says so). Its favourite says *made from scratch*, edited or not. In the Search tab the next
search replaces it. Its edits are kept like those of any other sound, so starting from scratch again on the same
profile, while the Studio runs, brings back the edits made to it before; the Original side of the switch is the single
sine. The button only shows in an empty tab, though, so an edited new sound that is not kept is asked about before
anything replaces it: *Replace your new sound?* **Replace** (the default) goes on; **Cancel** leaves everything as it
was, and ☆ then keeps it. In the Search tab that is Run search, Add WAV, another target in the Target selector (which
then goes back on Cancel) and Show in Search from the Library tab (which shows the Search tab for the question, and is
not offered while a search runs); in the Library tab a click on a node, a favourite or an import, and Find in library.
A placement that waits for its library asks when the new sound was started meanwhile. An unedited new sound, or one
whose edit is a favourite, is replaced without asking, whichever side of the Original ⇄ Edited switch is shown.

**Editing a sound.** Whatever the patch card shows (a finalist, a library sound, a favourite, a placed import) can be
edited in the block diagram, in both tabs. The original is never changed: editing works on a copy, and the next note
played, on the on-screen keyboard or the MIDI device, plays the edit (notes already held keep their sound). The diagram
follows every edit at once, the code a moment later, with the line *Edited in HearSay Studio.* in its header.

- **Selecting.** Click an operator box, the amount of a connection, the NOISE box or the PITCH box to select it: it is
  outlined, and the inspector beside the diagram shows it. Clicking empty space, or the × of the inspector, closes it;
  the inspector's place then says *Click a part of the diagram to edit it.*, so the diagram keeps its size.
  While a sound is shown, the PITCH box is always there (empty when the pitch does not move), and so is the NOISE box
  on Nome6, so that a pitch envelope, vibrato or noise can be added. An operator that is in use but no longer heard
  (output 0 and modulating nothing that is heard) stays in the diagram, greyed, and can still be selected and edited.
- **The inspector** lists every value of the part. An operator has *Add modulator*, *Modulate…*, *Modulated by…* and
  *Remove*, menus
  for the waveform and the frequency mode (ratio or fixed Hz), its numbers, a filter section (type, *Sweep*) and, on
  Nome6, *Roughness from the shared noise*. A connection has *Disconnect*, its amount and *Amount follows an envelope*;
  NOISE its colour and cutoff; PITCH the toggles *Pitch envelope* and *Vibrato* and the vibrato rate, depth, delay and
  fade-in. *Add modulator* takes the first free operator: a sine at ratio 1 with a short decaying envelope, modulating
  the selected operator by 1 rad and not heard on its own. *Modulate…* lets the selected operator modulate another one
  (or itself: feedback), *Modulated by…* lets another one modulate it; their menus read *OP1 → OP2*, grey the
  connections that exist already and mark *(not heard)* a connection into an operator that is not heard, which changes
  nothing until that operator is heard. A new connection starts at 1 rad and is selected, so that its amount can be
  typed or dragged at once. *Remove* switches an operator off with its own connections;
  the last carrier that is heard cannot be removed.
- **Formant filters.** A formant filter shows F1 (in Hz) and Q1, then F2 and F3 (ratio, level and Q each), and folds
  F4 and F5 under *more formants*, closed at first; it stays open or closed as left while the Studio runs. Ratios go up
  to 32 and every Q up to 50. A formant without a Q of its own shows the Q it plays at, Q1; typing or dragging its Q
  gives it one. Under the filter type, *preset* chooses a set of vowels, *speaker* a voice or group of speakers of
  that set, and a button per vowel, labelled with its word (its IPA symbol in the tooltip), sets all five formants to
  that vowel. A preset is one step to undo and keeps the filter's sweep and the rest of the operator. Each inspector
  applies the set and speaker its own menus show; a newly shown one starts at the set used last and that set's speaker
  used last (at first Sung, soprano). The search never sets F4 and F5 (section 4, *Operator filters*); the presets do.
  Each preset sets F1 in Hz, Q1 = F1 / B1, and for F2..F5 the ratio Fn / F1, a level relative to F1 and Q = Fn / Bn.
  The sets:

  | Set | Speakers | Vowels | Source |
  | --- | --- | --- | --- |
  | Sung | soprano, alto, countertenor, tenor, bass | a e i o u | The formant table of the Csound manual (TABLE III): F1..F5, their levels in dB and their bandwidths, all from the table. |
  | English | men, women, children | heed hid hayed head had hod hawed hoed hood who'd hud heard | Hillenbrand, Getty, Clark & Wheeler (1995), "Acoustic characteristics of American English vowels", JASA 97: the means of F1..F3 of 45 men, 48 women and 46 children (American English). |
  | Dutch | men, women | poet paat poot pat peut piet puut peet put pet pot pit | Pols, Tromp & Plomp (1973), JASA 53 (50 men) and Van Nierop, Pols & Plomp (1973), Acta Psychologica 37 (25 women): the means of F1..F3 and their levels. The speakers are Northern (Netherlands) Standard Dutch. |

  The speech sets measured F1..F3 only (Dutch also their levels); the rest is filled in by rule: bandwidths
  B1..B5 = 80, 100, 150, 250 and 300 Hz; F4 and F5 at 3500 and 4500 Hz for men, 4100 and 5000 Hz for women, 4600 and
  5600 Hz for children, each raised where needed to at least 300 Hz above the formant below; the levels that were not
  measured (English F2..F5, Dutch F4 and F5) from a cascade model of the vocal tract, five two-pole resonances on a
  source falling 6 dB per octave, the Dutch F4 and F5 relative to the measured F3; every level at most F1's. The means
  were computed from the per-speaker data that Praat distributes.

  **A vowel needs a source.** A formant filter only shapes the harmonics it is given: a vowel is heard when the operator
  sends many harmonics of about the same strength, so that every formant finds some to lift. A narrow pulse does that;
  a saw does not (its harmonics fall 6 dB per octave, so F3..F5 have little to work with), and a sine has nothing above
  the fundamental. The number of harmonics matters as much: a waveform built from 8 harmonics sends nothing above
  8 × the note's frequency, so on A3 (220 Hz) nothing above 1760 Hz reaches F2 and higher. *Voice source*, under the
  vowel buttons, makes the operator such a source in one step: a pulse 1 % wide (*pulse width* 0.01) with 64
  harmonics, its filter and everything else left as they are. It is refused on a profile without a pulse waveform.
- **Harmonics.** A triangle, saw, square or pulse operator (FMX6) has a *harmonics* row: the number of harmonics its
  waveform is built from, 1..128 (the FMX6 UGen's range), as in the export. It is a whole number: a typed value is
  rounded, and a drag moves in equal ratios. Sounds from the library may have only 8 or 16: few harmonics make a mellow
  waveform but leave nothing for a filter to shape higher up. Sine, noise and elliptic operators have no such row. It
  is not one of the numbers the search fine-tunes (its structural moves only halve or double it).
- **Numbers.** Type a number and press Enter (or leave the box). Or drag sideways, on the box or its name in the
  inspector, or on the dashed-underlined numbers of the diagram itself (frequency ratio or fixed frequency, connection
  amounts, output levels) while a note plays: 200 pixels cross the whole range, along its scale (a ratio in equal
  musical steps). Hold **Shift** for steps ten times finer, **Ctrl** to snap a ratio to the musical ratios the search
  uses (0.5, 0.75, 1, 1.25, 1.5, 2, 2.5, 3, 3.5, 4 and on up to 16). **Escape** during a drag puts the value back. A
  click on a number of the diagram selects its part; a drag selects it too. While the original is shown, the numbers
  of the diagram are not underlined and do not drag.
- **Envelopes** are small graphs in the inspector, time on the same square-root scale as the diagram, with their
  breakpoints as numbers under them. Drag a point to move it in time and level (the first point only up and down);
  double-click a line to add a point; right-click a point to remove it, hold the note there (*Sustain here*), make the
  envelope run its course (*One-shot*), or choose the curve of the segment that ends there (linear, exponential, step).
  The pitch envelope has no exponential curves: they cannot go below zero, and its cents go either way of the note.
- **Undo and Redo** (the arrows right of the tabs, or above them where the patch card is too narrow; Ctrl+Z, and Ctrl+Y
  or Ctrl+Shift+Z) go back step by step to the sound as it was. One step is one drag from press to release, one typed
  number, one menu choice, one part added or removed, one envelope point dragged, one vowel preset, one *Voice source*.
  While a box holds typed text, Ctrl+Z undoes the typing; during a drag the keys do nothing.
- **Match loudness.** An edit plays at the loudness of the original: its gain is the original's, not normalised again
  after each edit, so that turning an output level down really makes it softer. An edit that changes the sound a lot
  can therefore be much quieter (or louder) than the original: a triangle turned into a 1 % pulse through narrow
  formants can be some 30 dB quieter, and an output level cannot go above 1.5 to make up for it. *Match loudness*,
  beside the arrows (enabled while the edit is shown), computes the gain the edit would get as a new sound (its
  reference note normalised, as every sound of the library) and plays the edit at that gain from then on; the status
  line says *Loudness matched: the edit now plays as loud as a new sound would.* The matched loudness belongs to the
  sound's edit (the same sound in both tabs shares it) and stays while the Studio runs: later edits keep it, it is not
  matched again by itself; click again to match the edit as it then is. Edits made after matching that make the sound
  louder are held back by the player's soft limiter; click *Match loudness* again. *Original* still plays at the
  original's loudness, and undoing back to the original shows it as before (redo comes back to the matched edit); a new
  edit started from the original is another edit and plays at the original's loudness until it is matched. While the
  loudness is being matched the button is disabled, in both tabs. ☆ and *Update* on an edit keep the matched gain in the
  favourite. (The `preview.wav` of *Save edited sound…* is normalised on its own, to a peak of 0.7, which is why it can
  sound louder than an unmatched edit.)
- **Original ⇄ Edited.** Once a sound is edited, a switch beside the arrows shows the original or the edit: the
  diagram, the code and the keys follow, to compare by ear. The original can be inspected but not changed; an edit,
  undo or redo returns to the edit. Copy, Save…, Show in Search, Find in library and ☆ act on the version shown; while
  the edit is shown, *Save result folder…* becomes *Save edited sound…* and saves the edited sound's own folder. In the
  Search tab, the audio loss, the similarity and the per-note similarity shown with an edit are the
  original's, marked *(original)*: they are not computed again; the routing and the matrix follow the edit.
- **Favourites from edits.** ☆ on an edit keeps it as a new favourite whose origin reads *edited from* and the
  original's origin; the favourite of the original stays as it was. In the Library tab the edit then moves to the new
  favourite: it is shown and selected, and the sound it was made from goes back to its original, without the edit's
  history, so that the old favourite reads as before when it is chosen again. To change the favourite itself instead,
  ☆ on an edit of a favourite offers *Update "name"*; after *Overwrite* the favourite keeps its nickname and origin and
  holds the edit (its old version is gone). In the Library tab it is then shown as it now is, with no history; in the
  Search tab the edit stays shown, now as that favourite. The edit plays at the loudness of the original (its
  gain is not normalised again, so a lower output level really is softer) or, after *Match loudness*, at its own, and
  its favourite keeps that loudness: the gain is stored in `favourite.json` as `gain`. Favourites without it are
  normalised as before.
- **Limits of the profile.** An edit always stays playable by the sound's backend. Values are clamped to the profile's
  ranges (those of the search's parameter schema where it has the value). Connection amounts stop at the backend's
  modulation limit: 24 rad into one operator in all, shared by every connection into it (an amount that follows an
  envelope counts at its peak). Menus offer only what the profile has: no waveform menu on FM7 (sine only), filters on
  FMX6 only, roughness and NOISE on Nome6 only, and no new operator beyond six. An edit that cannot be made so is
  refused, and the status line says why.
- **History.** Every sound keeps its edit and its history while the Studio runs, per patch and profile: look at
  another finalist or library sound and come back, and the edit is still there, with its undo steps; the same sound in
  both tabs shares one edit. The edits are kept per sound for as long as the Studio runs, also after a new search:
  coming back to the same sound, as a finalist, from the library or as a favourite, finds its edit and its history
  again. Nothing is kept when the Studio closes (keep an edit with ☆ or Save edited sound…).

**Playing like a synth.** The keyboard of each tab, and a MIDI controller, play the sound of the visible tab: in the
Search tab the shown finalist (or the library sound shown there), in the Library tab the last clicked sound.

- A note sounds for as long as its key is held: an envelope with a sustain point holds there until the key is let go
  and then plays its release; a percussive envelope runs its course whatever the key does. What is heard is what the
  search matched and what the SuperCollider export plays for a note held that long.
- On the on-screen keyboard, mouse button or touch down is note-on and up is note-off. Sliding a held pointer onto
  another key releases the old note and plays the new one. Every finger on a touch screen holds a note of its own;
  with one mouse, one key is held at a time and earlier notes ring on through their release. A finger on the keys
  plays rather than scrolls; the scroll bar under the keys scrolls the keyboard.
- Up to 16 notes sound at once; a 17th takes the oldest, which fades out over a few milliseconds. A note keeps the
  sound it started with: select another finalist or click another node while holding a chord, and the new notes play
  the new sound while the held ones go on.
- Keys light up while they sound, from the mouse or from MIDI, in both tabs.
- **MIDI in** in the header chooses the MIDI input device: *none*, then the devices by name. The list is read again
  whenever it opens. Only note-on, note-off and velocity are used (a note-on with velocity 0 is a note-off); every
  channel plays, and pitch bend, controllers, the sustain pedal, aftertouch and program changes are ignored. The
  choice is remembered by name between sessions in `%LOCALAPPDATA%\HearSay\studio-settings.json`; a remembered device
  that is not connected at start leaves the selector on *none*, without an error. Choosing a device that cannot be
  opened changes nothing: the device before stays open and remembered, and the status line says so. A device
  unplugged while open releases its notes, the selector goes back to *none* and the status line says so; it stays
  remembered for the next session.
- An audio device unplugged while the Studio runs (headphones, a USB interface) is noticed within a second: live notes
  stop, the MIDI device is closed and the selector set to *none* (it stays remembered), the keyboard says it is silent,
  and the status line asks to restart the Studio once an audio device is connected again.
- A sound whose profile has no live voices is not played, and the status line says so (FM7, FMX6 and Nome6 all play).
- Latency from key to sound is about 41 ms at most on the development machine (Windows, WASAPI shared mode: 22 ms of
  it is the device's own buffering). A running search or library build keeps its full speed, so notes may stutter or
  drop out while one runs; on an otherwise idle machine they play cleanly. When the computer cannot keep up, the
  oldest notes are dropped (with a short fade) rather than all of them stuttering.
- The Studio runs on Windows, macOS and Linux; MIDI input works there through OwnAudioSharp.Midi. Android and iOS are
  not supported by the Studio.

Studio plays through OpenAL Soft. Without an audio device the keyboard is silent and a note says so; everything else
works. Target files and live notes can sound at the same time.

The logo in the header and the window icon are one vector drawing (`HearSayLogo`): six operators on a ring, the
chords of a modulation matrix, a wave through them. `HearSay.Studio --logo out.png [size]` writes it as a PNG and
exits, for packaging.

### The Library tab

The Library tab shows the structure library of FM7, FMX6 or Nome6 as sound families: k-means on the spectral
fingerprints splits the 300 000 sounds (600 000 for FMX6 and Nome6, half of them noise-free) into 40 families, each
family into 40 more, down to single sounds. Every node is a real sound, the member closest to its family's centre;
nodes are sized by the number of sounds and coloured from dark (blue) to bright (yellow), and each is joined to its two
nearest neighbours.

- Click a node to hear it at A3 (the note the library was rendered at, held for its 1 s and released) and see its
  code and block diagram; the keyboard and the MIDI device play the last clicked sound at any pitch, held as long as
  the key. Double-click a family, or click the plus, to open it; the
  breadcrumb goes back up. Resting the pointer on a node shows a few words about it: brightness, envelope, the
  waveform of the loudest carrier, *filtered*, *noisy*, and the number of sounds.
- **Copy** copies the SuperCollider code, with the library and seed in its header and the level the sound plays at
  here (section 8). **Save…** writes `patch.json`, `patch.scd`, `preview.wav` and a `search-summary.json` with the
  profile, a folder that can be imported again.
  **Show in Search** makes the sound the patch of the Search tab, until the next search.
- **Import folder…** reads every `patch.json` (and the alternatives) under a folder of exported results. Click one in
  the list to place it: the tab opens the family whose centre is closest at every level and shows the sound there as a
  star among the sounds like it. One level up, the family it fell in has a dashed ring. Placement is by sound, so an
  FM7 export can be placed in the FMX6 library.
- **☆** keeps the shown sound as a favourite under a nickname you type (a library sound, a placed import or finalist
  here; the shown finalist or library sound in the Search tab, which has the same button). A favourite has a filled
  ★; clicking it again renames or removes it. Favourites are kept between sessions as ordinary exported-sound folders
  named by their nicknames under `%LOCALAPPDATA%\HearSay\favourites`, each with a `favourite.json` saying where it came
  from. The **Favourites** side of the list beside the graph, first and shown by default, has them all with their profiles (importing a folder or *Find in library* switches to **Imported**): click one to place it
  among its neighbours in the library of its profile, selected and played, ready to copy or save; **Rename** and
  **Remove** act on the selected one (removing deletes its folder, after asking).
- **Filter ▾** shows only some sounds: by the waveforms and filter types of the operators you can hear, brightness,
  envelope, noise (*noisy* or *noise-free*), the number of audible operators and feedback. Values in a row are
  alternatives, rows must all hold (*saw or square*, and *low-pass*). Each chip shows how many sounds match the rest of
  the filter and have its value. The matching sounds are grouped into families of their own (a few seconds), so the
  screen stays full; **Clear** returns to all sounds. An imported sound is placed among the filtered sounds.

The first time a library is opened its top level is grouped (under a minute) and kept next to the library as
`…-atlas-v1.bin`; a library missing on disk is built first, and the graph shows the share done and the time left
(FMX6: about 20 minutes on 24 threads; when the older FMX6 library is on disk only its noise-free sounds are added,
about 12).

## 10. The command line

```
hearsay match <target.wav>... [--profile p] [--budget n] [--seed n] [--seeds n] [--library n] [--output dir]
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
| `HypothesisCount` [128] | least number of structures screened; the screening share of the budget raises it (all structures from about 8000 evaluations on) | more for small budgets when a prior produces many good structures (watch the screening stage) |
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
| `StructureLibrarySize` [300 000] | random patches whose nearest neighbours by sound become hypotheses, plus as many noise-free ones for FMX6 and Nome6; built once per profile (FM7 under a minute, Nome6 about 11 min, FMX6 about 20 min on 24 threads, less when the older library of the profile is on disk, with the share done and the time left in the progress) and kept in `%LOCALAPPDATA%\HearSay\structure-libraries` | 0 to turn the lookup off, for example in quick tests; larger for closer neighbours |
| `LibraryBasinMode` [Extra], `LibraryBasinCount` [2] | how library neighbours share the basins: Extra adds up to two basins on top of those of the read structures, Capped lets at most two compete, Free lets them compete for all | give about a third more budget with Extra; Free for sounds made like the library's random patches |

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
rate per octave, reference note), for FMX6 `Shape` (pulse duty), `HarmonicLimit` and `Filter` (`OperatorFilter.LowPass`,
`HighPass`, `BandPass`, `Notch`, `PeakAt`, `FormantAt` and `Formants`, up to five formants, build one;
`FormantPresets.Filter(set, speaker, vowelKey, keepSweepFrom)` gives a vowel preset of one of
`FormantPresets.Sets`; null for none), and for Nome6 `Shape`
(elliptic m), `Morph`, `NoisePhase` and `NoiseM`. Per patch: `OutputLevels`, `ModulationMatrix` (edges
`SourceOperator`, `DestinationOperator`, `Enabled`, `Amount` in radians, optional `AmountEnvelope`), `Pitch`
(envelope and vibrato) and, for Nome6, `Noise` (colour and cutoff of the shared noise). A vowel preset needs a source
with many harmonics: the edit `PatchEdits.SetVoiceSource(op)` (applied through a `PatchEditor`, as the Studio does)
makes an operator a 1 % pulse with 64 harmonics.

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

**Notes from the MIDI controller stutter or drop out.** A search or a library build is running (it keeps its full
speed), or the status line warns that the audio device mixes in large blocks: OpenAL Soft did not read the
`alsoft.ini` shipped next to the program.

**The MIDI device is not in the list.** Open the list again (it is read every time it opens); check that no other
program holds the device: on Windows most MIDI drivers let only one program open an input at a time.

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
