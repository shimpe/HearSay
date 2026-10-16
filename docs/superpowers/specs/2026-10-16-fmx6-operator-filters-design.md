# FMX6 operator filters: design

Date: 2026-10-16. Status: approved in conversation, awaiting review of this document.

## Goal

Give every FMX6 operator an optional filter, and let the search choose and tune it, so that HearSay can match sounds
that phase modulation alone reaches poorly:

- subtractive sounds: brass with an opening filter, plucks that darken, resonant analogue synth sounds;
- voice-like sounds: vowels and other formant-shaped spectra;
- instrument body resonances.

Success criteria:

1. The extended UGen is backward compatible: SynthDefs built for the current FMX6 (72 or 73 inputs) sound bit-identical.
2. The HearSay renderer reproduces the extended UGen sample-exactly, as it does for FMX6 and Nome6 today.
3. On hidden patches with filters (see Evaluation) the search finds clearly lower losses than a search without
   filters, and the existing FMX6 benchmarks do not get worse beyond the noise of the benchmark.

Out of scope: comb filters (a separate physical-modelling project), shelving and all-pass filters, filters for the FM7
and Nome6 backends, a filter on the mixed output.

## Signal flow

The filter sits inside the operator:

```
osc -> [filter] -> x amp --+--> operator output
                           |
                           +--> modulation of other operators (mem)
```

What the operator sends is the filtered signal, both to the output mix and to the operators it modulates. A low-pass
on a sawtooth modulator softens the modulation; a filter on a carrier shapes the heard sound. The filter runs at the
oversampled rate, inside the one-sample feedback loop, before the decimator.

## Filter types

| Type | Frequency | Parameters | Response |
|---|---|---|---|
| Low-pass | key-tracked (`CutoffRatio` x note) | Q, 2 or 4 poles | SVF low-pass output; 4 poles = two SVFs in series |
| High-pass | key-tracked | Q, 2 or 4 poles | SVF high-pass output; 4 poles = two SVFs in series |
| Band-pass | key-tracked | Q | SVF band-pass, unity gain at the centre (`k * v1`) |
| Notch | key-tracked | Q | `x - k * v1` |
| Peak | fixed Hz (`FixedHz`) | Q, gain dB | `x + (g - 1) * k * v1`, `g = 10^(dB / 20)` |
| Formant | fixed Hz (`FixedHz` = F1) | Q (shared), F2/F1, F2 level | `bp(F1) + level * bp(F2)`, two band-pass SVFs in parallel |

All filters are the TPT state-variable filter already used by the filtered-noise waveform, in double precision, with
`k = 1 / Q` instead of the fixed `sqrt(2)`. Q ranges from 0.5 to 20, below self-oscillation. Frequencies are clamped to
10 Hz .. 0.49 x the internal (oversampled) sample rate.

Key-tracked frequencies follow the played note (note x ratio) but not the pitch envelope and not the vibrato. Peak and
formant frequencies are fixed in hertz: formants and body resonances do not move with the pitch.

### Filter envelope

An optional envelope moves the frequency: `frequency = base x 2^(EnvelopeOctaves x env)`, with `env` an
`OperatorEnvelope` with levels 0..1. For the formant filter both peaks move together. The search gives it the simple
attack, decay, sustain, release form (four points), as it does for the pitch envelope. The envelope uses the same key
scaling time factor as the operator's amplitude envelope.

## Patch model

`OperatorPatch` gets one optional property; `null` means no filter, so existing patches, their hashes and their
serialized form are unchanged.

```csharp
public enum OperatorFilterType { LowPass, HighPass, BandPass, Notch, Peak, Formant }

public sealed record OperatorFilter(
    OperatorFilterType Type,
    int Poles,               // 2 or 4; low-pass and high-pass only, 2 elsewhere
    double CutoffRatio,      // low-pass, high-pass, band-pass, notch: frequency as a multiple of the note
    double FixedHz,          // peak: centre; formant: F1
    double Q,                // 0.5..20
    double FormantRatio,     // formant: F2 / F1, >= 1
    double FormantLevel,     // formant: level of F2 relative to F1, 0..1
    double PeakGainDb,       // peak: -24..24
    double EnvelopeOctaves,  // -4..4
    OperatorEnvelope? Envelope);  // levels 0..1; null = static frequency
```

`OperatorPatch.Filter` is `OperatorFilter?`. The type and the number of poles are structural; the other fields are
smooth parameters. Parameterizing the formant as F1 and the ratio F2/F1 >= 1 removes the symmetry of two free peaks
(F1 = 500, F2 = 1500 is the same filter as F1 = 1500, F2 = 500).

`SynthesisCapabilities` gets `SupportsOperatorFilters` (default false), true for `supercollider-fmx6` only.
`PatchCompatibilityValidator` rejects a filter on a profile without it, and out-of-range fields (Q, poles, ratios,
levels, gain) with messages like the existing ones.

## UGen (repository `fmx6`)

Input layout, appended after the existing optional oversampling input (index 72):

| Indices | Content |
|---|---|
| 0 .. 72 | unchanged |
| 73 .. 108 | six operators x `[type, freq, q, formantRatio, formantLevel, gainDb]` |

- `type`: 0 off, 1 LP 2-pole, 2 LP 4-pole, 3 HP 2-pole, 4 HP 4-pole, 5 band-pass, 6 notch, 7 peak, 8 formant;
  read every block.
- `freq`: frequency in Hz (centre, cutoff or F1); interpolated per sample like `amp` (`CALCSLOPE`), since the
  exporter drives it with `EnvGen.kr`.
- `q`, `formantRatio`, `formantLevel`, `gainDb`: read every block.

The constructor accepts 72, 73 or 109 inputs. With 72 or 73 no filter runs and the output is bit-identical to the
current plugin. Each operator keeps two SVF states (two variables each), reset to zero at construction.

`FMX6.sc` gets a `filters` argument to `FMX6.ar` (an array of six `[type, freq, q, formantRatio, formantLevel,
gainDb]` rows, defaulting to all off) and accepts the three input counts in `checkInputs`. The help file and the README
document the filter types and the new argument. The filtered-noise waveform (7) stays as it is.

## Renderer, compiler, exporter

**`CompiledOperatorMatrixPatch`**: per audible operator the filter type code, Q, formant ratio, formant level and
gain, and `FilterFrequencies[op][block]`: base (`note x CutoffRatio` or `FixedHz`) x `2^(EnvelopeOctaves x env)`, with
the envelope run by the existing `ControlRateEnvelope` and the operator's key scaling time factor.

**`Fmx6CompatibleOperatorMatrixRenderer`**: the filter between the waveform and the amplitude, with formulas line for
line equal to the C++ code, at the oversampled rate. Operators without a filter run exactly the current code, so
existing renders stay bit-identical.

**`SuperColliderFmx6Exporter` / `SuperColliderOperatorMatrixExporter`**: a patch with at least one filter passes
`filters:` to `FMX6.ar`. A static frequency is a number expression (`freq * ratio` or the fixed Hz); with an envelope it
is `(base * (2 ** (depth * EnvGen.kr(Env(...), gate, timeScale: ...))))`. A patch without filters exports exactly as
today and runs on the current plugin.

## Search

### Parameters

`PatchParameterSchema` adds a "Filter" group per filtered operator, with only the parameters its type uses:

| Parameter | Range | Axis | Types |
|---|---|---|---|
| `CutoffRatio` | 0.25 .. 64 | logarithmic | LP, HP, BP, notch |
| `FixedHz` | 80 .. 8000 Hz | logarithmic | peak, formant |
| `Q` | 0.5 .. 20 | logarithmic | all |
| `FormantRatio` | 1 .. 8 | logarithmic | formant |
| `FormantLevel` | 0 .. 1 | linear | formant |
| `PeakGainDb` | -24 .. 24 | linear | peak |
| `EnvelopeOctaves` | -4 .. 4 | linear | all, with an envelope |
| filter envelope | via `AddEnvelope` | | all, with an envelope |

### Mutations

`TopologyMutator` proposes, and screens like its other variants, with starting values from the analysis:

- add a filter: low-pass with the cutoff at the target's roll-off; high-pass below the weakest low partials; band-pass
  or peak on the strongest bump of the spectral envelope; formant from the formant prior;
- remove a filter;
- change the type (for example low-pass to band-pass) or 2 to 4 poles;
- add a filter sweep, only when the target's spectral centroid moves clearly over time; depth and decay from the
  centroid trajectory.

### Priors

Both new providers only run for profiles with `SupportsOperatorFilters` and get their own group in the survivor
allocation.

**`FormantPriorProvider`**

1. From the tracked harmonic amplitudes (as `PartialTrackPriorProvider` uses them) fit a smooth spectral envelope
   through the harmonic peaks (cepstral smoothing or a true-envelope fit), not plain LPC, which pulls its poles to the
   harmonics when f0 is high.
2. Divide out the slope of the source the hypothesis will use (a sawtooth falls as 1/n).
3. The two strongest maxima give F1 and F2/F1, their widths a starting Q, their height difference the F2 level.
4. Hypotheses: a rich carrier (saw or pulse) with the formant filter, plus variants with one peak and with F1 + F3.

The estimate is limited to about half of f0; the search refines it.

**`SubtractivePriorProvider`**: a rich carrier with a low-pass; cutoff from the roll-off, envelope from the centroid
trajectory, 2 or 4 poles from the slope above the cutoff.

### Structure library and random patches

`RandomPatchGenerator` gives about 30 % of FMX6 patches a filter, with parameters in musical ranges.
The library version becomes a property of the profile instead of the global `StructureLibrary.FormatVersion`:
`SynthesisProfile.StructureLibraryVersion` (default 1), used in the cache file name
(`{profile}-v{version}-{size}.bin`) and checked in the file header. `supercollider-fmx6` gets version 2, so only the
FMX6 library is rebuilt once; FM7 and Nome6 keep version 1, the same file names, and their cached libraries. A later
change of the file layout raises the version of every profile.

### Complexity

A filter adds to the complexity term, an envelope a little more, so that at equal sound the patch without a filter
wins.

## Studio and descriptions

The block diagram draws a filter block between an operator's oscillator and its output, labelled with the type and its
main value (cutoff ratio or Hz). `PatchDescription` describes the filter. No new controls: filters are always
available for FMX6.

## Testing

Fast tests (synthesis and operator matrix projects):

- response per type on a sine sweep: low-pass attenuates above the cutoff, 4 poles about twice the dB of 2 poles; peak
  gives +G dB at the centre; notch a dip; formant peaks at F1 and F2;
- a patch without a filter keeps its hash; the validator rejects filters on FM7 and Nome6 and out-of-range fields;
- the schema exposes only the parameters of the filter type; mutations produce valid patches;
- the formant prior recovers F1 and F2 within half of f0 from a rendered saw + formant patch.

SuperCollider tests (skipped without sclang): per filter type an exported patch rendered in NRT equals the renderer
sample-exactly, including a filtered modulator, a filter envelope, and oversampling 1 and 2. A SynthDef with 72 or 73
inputs renders bit-identically on the new plugin.

## Evaluation

Before merging, at budget 4000, paired:

1. hidden filtered patches: an opening brass sound (saw + 4-pole low-pass + sweep), the vowels a, i and u (saw +
   formant), a resonance peak on an FM sound; the search must find clearly lower losses than without filters;
2. no regression on the FMX6 reference benchmark and the dense and sparse FMX6 sets beyond the noise of the benchmark;
3. real voice recordings, measured separately, when available.

## Delivery

- Repository `fmx6`: own branch and commit; built with CMake and installed in
  `%LOCALAPPDATA%\SuperCollider\Extensions\FMX6UGens`.
- Repository `hearsay`: branch `fmx6-operator-filters`; the README and the expert manual mention the filters and the
  plugin version they need.
