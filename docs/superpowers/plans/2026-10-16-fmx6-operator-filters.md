# FMX6 Operator Filters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every FMX6 operator gets an optional filter (low-pass, high-pass, band-pass, notch, peak, formant, with an
optional envelope), reproduced sample-exactly by HearSay, exported to SuperCollider, and chosen and tuned by the search.

**Architecture:** The filter is a TPT state-variable filter between an operator's waveform and its amplitude, so the
operator sends the filtered signal both to the output and to the operators it modulates. The C++ UGen (repository
`D:\Projects\fmx6`) and a shared C# kernel (`HearSay.Synthesis/Filters/OperatorFilterKernel.cs`) contain the same
arithmetic, operation for operation. The patch model gains `OperatorPatch.Filter`; the capability
`SupportsOperatorFilters` (FMX6 only) gates the validator, the renderer, the exporter, the parameter schema, the
mutations and two new prior providers.

**Tech Stack:** C# / .NET 10, xUnit; C++ SuperCollider server plugin built with CMake; sclang.

**Spec:** `docs/superpowers/specs/2026-10-16-fmx6-operator-filters-design.md`

## Global Constraints

- Filter type codes, everywhere (UGen, kernel, exporter): 0 off, 1 LP 2-pole, 2 LP 4-pole, 3 HP 2-pole, 4 HP 4-pole, 5 band-pass, 6 notch, 7 peak, 8 formant.
- UGen input layout: 0..72 unchanged; 73..114 = six operators x `[type, freq, octaves, q, formantRatio, formantLevel, gainDb]`; the UGen accepts 72, 73 or 115 inputs.
- Frequency inside the filter: `freq x 2^octaves` in double precision, clamped to 10 Hz .. 0.49 x sample rate.
- `k = 1 / clamp(Q, 0.5, 20)`; 4 poles = a first stage with `k = 1.4142135623730951` (Q = 0.7071), then a stage with `k`.
- Band-pass output `k * v1`; notch `x - k * v1`; peak `x + (g - 1) * (k * v1)`, `g = 10^(dB / 20)`; formant `k * v1(F1) + level * (k * v1(F1 x ratio))`.
- Key-tracked types (LP, HP, BP, notch) use `note x CutoffRatio`, not the pitch envelope, not the vibrato. Peak and formant use `FixedHz`.
- Model ranges: Poles 2 or 4 (4 only for LP/HP); CutoffRatio 0.25..64; FixedHz 10..20000; Q 0.5..20; FormantRatio 1..16; FormantLevel 0..1; PeakGainDb -24..24; EnvelopeOctaves -4..4.
- Search ranges: CutoffRatio 0.25..64 log; FixedHz 80..8000 log; Q 0.5..20 log; FormantRatio 1..8 log; FormantLevel 0..1 linear; PeakGainDb -24..24 linear; EnvelopeOctaves -4..4 linear.
- A patch without filters: same JSON, same hash, same render, same export as before this work.
- HearSay renders and exports FMX6 at oversample 1.
- `supercollider-fmx6` gets `StructureLibraryVersion = 2`; every other profile keeps 1.
- Code style: match the surrounding code (XML doc comments on public members, `FormattableString.Invariant`/`CultureInfo.InvariantCulture`, file-scoped namespaces).
- Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Work on branch `fmx6-operator-filters` (hearsay) and a new branch `operator-filters` (fmx6). On a git "Permission denied" error, wait about 10 s and retry.

## Review Focus

1. A key-tracked cutoff far above Nyquist (CutoffRatio 64 on a 2 kHz note) must render finite audio, clamped at 0.49 x sample rate, not NaN.
2. A high-Q filter (Q 20) on a self-feedback operator must stay bounded: finite output, no blow-up.
3. An envelope pushing the frequency below 10 Hz (EnvelopeOctaves -4 on a low cutoff) must clamp to 10 Hz and stay sample-exact in SuperCollider.
4. A filter on a disabled or inaudible operator must not make an FM7 or Nome6 patch incompatible, and the FMX6 exporter must write that operator's row as off.
5. Patch JSON written before this change (no `filter` key) must load, and a patch with a filter must survive a JSON round trip with the same hash.

Tests for these lines: 1 and 2 in Task 4, 3 in Task 5, 4 in Task 1 and Task 5, 5 in Task 1.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/HearSay.Synthesis/Patches/OperatorFilter.cs` (new) | `OperatorFilterType`, `OperatorFilter` record, factories, validation |
| `src/HearSay.Synthesis/Patches/OperatorPatch.cs` | `Filter` property |
| `src/HearSay.Synthesis/Patches/OperatorMatrixPatch.cs` | filter validation in `Validate()` |
| `src/HearSay.Synthesis/Profiles/SynthesisProfile.cs` | `SupportsOperatorFilters`, `StructureLibraryVersion`, FMX6 profile |
| `src/HearSay.Synthesis/Profiles/PatchCompatibilityValidator.cs` | `OperatorFilter` code and check |
| `src/HearSay.Synthesis/Filters/OperatorFilterKernel.cs` (new) | the filter arithmetic shared by renderers |
| `D:\Projects\fmx6\FMX6.cpp`, `Classes\FMX6.sc`, `HelpSource\Classes\FMX6.schelp`, `README.md` | UGen |
| `src/HearSay.Synthesis/Compilation/CompiledOperatorMatrixPatch.cs` | `CompiledFilters` |
| `src/HearSay.Synthesis.Fmx6Compatible/Rendering/Fmx6CompatibleOperatorMatrixRenderer.cs` | filter in the sample loop |
| `src/HearSay.SuperCollider/SuperColliderOperatorMatrixExporter.cs` | `EnvGen` and `TimeScale` become protected |
| `src/HearSay.SuperCollider/FMX6/SuperColliderFmx6Exporter.cs` | `filters:` argument |
| `src/HearSay.Synthesis/Patches/RandomPatchGenerator.cs` | `FilterProbability` |
| `src/HearSay.OperatorMatrix/Priors/StructureLibrary.cs` | per-profile version, filters in FMX6 library |
| `src/HearSay.OperatorMatrix/Parameters/PatchParameterSchema.cs` | filter parameters |
| `src/HearSay.OperatorMatrix/Canonicalization/OperatorMatrixRegimeKey.cs` | complexity of filters |
| `src/HearSay.OperatorMatrix/Priors/FilterPriors.cs` (new) | spectral analysis for filter starting values |
| `src/HearSay.OperatorMatrix/Priors/FormantPriorProvider.cs` (new) | formant hypotheses |
| `src/HearSay.OperatorMatrix/Priors/SubtractivePriorProvider.cs` (new) | low-pass hypotheses |
| `src/HearSay/OperatorMatrixPatchMatcher.cs` | provider wiring |
| `src/HearSay.OperatorMatrix/Search/TopologyMutator.cs` | filter mutations |
| `src/HearSay/PatchDescription.cs`, `src/HearSay.Studio/FmDiagram.cs` | text and diagram |
| `README.md`, `docs/expert-manual.md` | documentation |

---

### Task 1: Filter model, capability and validation

**Files:**
- Create: `src/HearSay.Synthesis/Patches/OperatorFilter.cs`
- Modify: `src/HearSay.Synthesis/Patches/OperatorPatch.cs` (after the `NoiseM` property)
- Modify: `src/HearSay.Synthesis/Patches/OperatorMatrixPatch.cs` (`Validate()`, operator loop, after the `HarmonicLimit` check)
- Modify: `src/HearSay.Synthesis/Profiles/SynthesisProfile.cs`
- Modify: `src/HearSay.Synthesis/Profiles/PatchCompatibilityValidator.cs`
- Test: `tests/HearSay.Synthesis.Tests/OperatorFilterModelTests.cs` (new)

**Interfaces:**
- Produces: `enum OperatorFilterType { LowPass, HighPass, BandPass, Notch, Peak, Formant }`;
  `record OperatorFilter(OperatorFilterType Type, int Poles = 2, double CutoffRatio = 4.0, double FixedHz = 1000.0, double Q = 0.7071, double FormantRatio = 2.0, double FormantLevel = 0.5, double PeakGainDb = 6.0, double EnvelopeOctaves = 0.0, OperatorEnvelope? Envelope = null)`
  with `bool IsKeyTracked`, `IEnumerable<string> Validate()`, factories `LowPass(double cutoffRatio, double q = 0.7071, int poles = 2)`, `HighPass(double cutoffRatio, double q = 0.7071, int poles = 2)`, `BandPass(double centreRatio, double q)`, `Notch(double centreRatio, double q)`, `PeakAt(double hz, double gainDb, double q)`, `FormantAt(double f1Hz, double f2Ratio, double f2Level, double q)`;
  `OperatorPatch.Filter` (`OperatorFilter?`); `SynthesisCapabilities.SupportsOperatorFilters`;
  `SynthesisProfile.StructureLibraryVersion` (int, default 1); `PatchCompatibilityCodes.OperatorFilter = "operator-filter"`.

- [ ] **Step 1: Write the failing tests**

`tests/HearSay.Synthesis.Tests/OperatorFilterModelTests.cs`:

```csharp
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Synthesis.Tests;

public sealed class OperatorFilterModelTests
{
    private static OperatorMatrixPatch Carrier(OperatorFilter? filter, bool enabled = true) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Enabled = enabled, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Fact]
    public void PatchWithoutFilter_HasNoFilterInItsJson_AndKeepsItsHash()
    {
        OperatorMatrixPatch plain = ReferencePatches.Find("electric-piano")!.Patch;
        string json = plain.ToJson();
        Assert.DoesNotContain("\"filter\"", json, StringComparison.Ordinal);
        Assert.Equal(plain.ComputeHash(), OperatorMatrixPatch.FromJson(json).ComputeHash());
    }

    [Fact]
    public void Filter_RoundTripsThroughJson()
    {
        OperatorFilter filter = OperatorFilter.FormantAt(700, 1.8, 0.6, 8) with
        {
            EnvelopeOctaves = 1.5,
            Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1),
        };
        OperatorMatrixPatch patch = Carrier(filter);
        OperatorMatrixPatch back = OperatorMatrixPatch.FromJson(patch.ToJson());

        Assert.Contains("\"filter\"", patch.ToJson(), StringComparison.Ordinal);
        Assert.Equal(patch.ComputeHash(), back.ComputeHash());
        Assert.Equal(OperatorFilterType.Formant, back.Operators[0].Filter!.Type);
        Assert.Equal(1.8, back.Operators[0].Filter!.FormantRatio);
    }

    public static TheoryData<OperatorFilter> InvalidFilters => new()
    {
        OperatorFilter.LowPass(4) with { Poles = 3 },
        OperatorFilter.BandPass(2, 2) with { Poles = 4 },
        OperatorFilter.LowPass(0.1),
        OperatorFilter.LowPass(80),
        OperatorFilter.LowPass(4, q: 0.2),
        OperatorFilter.LowPass(4, q: 25),
        OperatorFilter.FormantAt(700, 0.8, 0.5, 8),
        OperatorFilter.FormantAt(700, 2, 1.5, 8),
        OperatorFilter.PeakAt(1000, 30, 2),
        OperatorFilter.PeakAt(5, 6, 2),
        OperatorFilter.LowPass(4) with { EnvelopeOctaves = 5 },
        OperatorFilter.LowPass(double.NaN),
    };

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public void OutOfRangeFilter_IsNotWellFormed(OperatorFilter filter) =>
        Assert.Contains(Carrier(filter).Validate(), p => p.StartsWith("OP1 filter", StringComparison.Ordinal));

    [Fact]
    public void ValidFilters_AreWellFormed()
    {
        Assert.Empty(Carrier(OperatorFilter.LowPass(4, 2, 4)).Validate());
        Assert.Empty(Carrier(OperatorFilter.HighPass(0.5)).Validate());
        Assert.Empty(Carrier(OperatorFilter.Notch(3, 4)).Validate());
        Assert.Empty(Carrier(OperatorFilter.PeakAt(1200, -12, 3)).Validate());
        Assert.Empty(Carrier(OperatorFilter.FormantAt(300, 7.5, 0.2, 12) with { EnvelopeOctaves = -4, Envelope = OperatorEnvelope.Adsr(0.01, 0.1, 0.5, 0.1) }).Validate());
    }

    [Fact]
    public void OnlyFmx6_PlaysFilters_ButADisabledOperatorsFilterDoesNotMatter()
    {
        OperatorMatrixPatch filtered = Carrier(OperatorFilter.LowPass(4));
        Assert.True(PatchCompatibilityValidator.Instance.Validate(filtered, SynthesisProfiles.SuperColliderFmx6.Capabilities).IsCompatible);
        foreach (SynthesisProfile profile in new[] { SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderNome6 })
        {
            PatchCompatibilityResult result = PatchCompatibilityValidator.Instance.Validate(filtered, profile.Capabilities);
            Assert.Contains(result.Issues, i => i.Code == PatchCompatibilityCodes.OperatorFilter);

            // Review focus 4: what cannot be heard does not have to be representable.
            OperatorMatrixPatch disabled = Carrier(OperatorFilter.LowPass(4), enabled: false)
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)))
                .WithOutputLevel(1, 1.0);
            Assert.True(PatchCompatibilityValidator.Instance.Validate(disabled, profile.Capabilities).IsCompatible);
        }

        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        Assert.True(fmx6.SupportsOperatorFilters);
        Assert.False(fmx6.IsSubsetOf(fmx6 with { SupportsOperatorFilters = false }));
        Assert.True((fmx6 with { SupportsOperatorFilters = false }).IsSubsetOf(fmx6));
        Assert.Equal(2, SynthesisProfiles.SuperColliderFmx6.StructureLibraryVersion);
        Assert.Equal(1, SynthesisProfiles.SuperColliderFm7.StructureLibraryVersion);
        Assert.Equal(1, SynthesisProfiles.SuperColliderNome6.StructureLibraryVersion);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/HearSay.Synthesis.Tests --filter OperatorFilterModelTests`
Expected: build errors (`OperatorFilter`, `Filter`, `SupportsOperatorFilters`, `StructureLibraryVersion` do not exist).

- [ ] **Step 3: Create the model**

`src/HearSay.Synthesis/Patches/OperatorFilter.cs`:

```csharp
namespace HearSay.Synthesis.Patches;

/// <summary>Response of an operator filter. A structural parameter: the search moves between types by mutation only.</summary>
public enum OperatorFilterType
{
    /// <summary>Low-pass; cutoff follows the note.</summary>
    LowPass,

    /// <summary>High-pass; cutoff follows the note.</summary>
    HighPass,

    /// <summary>Band-pass with unity gain at the centre; centre follows the note.</summary>
    BandPass,

    /// <summary>Band-reject; centre follows the note.</summary>
    Notch,

    /// <summary>Boost or cut around a centre fixed in hertz; the rest passes unchanged.</summary>
    Peak,

    /// <summary>Two band-pass peaks fixed in hertz, F1 and F2 = F1 x ratio, with a shared Q.</summary>
    Formant,
}

/// <summary>
/// Filter between the waveform and the amplitude of an operator: what the operator sends, to the output and to the
/// operators it modulates, is the filtered signal. A TPT state-variable filter, see the FMX6 UGen.
/// </summary>
/// <param name="Type">Response; structural.</param>
/// <param name="Poles">2 or 4; 4 only for low-pass and high-pass. Structural.</param>
/// <param name="CutoffRatio">Low-pass, high-pass, band-pass and notch: frequency as a multiple of the note, 0.25..64.</param>
/// <param name="FixedHz">Peak: centre; formant: F1. In hertz, 10..20000.</param>
/// <param name="Q">Resonance, 0.5..20; shared by both formant peaks.</param>
/// <param name="FormantRatio">Formant: F2 / F1, 1..16.</param>
/// <param name="FormantLevel">Formant: level of F2 relative to F1, 0..1.</param>
/// <param name="PeakGainDb">Peak: gain at the centre, -24..24 dB.</param>
/// <param name="EnvelopeOctaves">How far the envelope moves the frequency, -4..4 octaves at envelope level 1.</param>
/// <param name="Envelope">Levels 0..1; null for a static frequency.</param>
public sealed record OperatorFilter(
    OperatorFilterType Type,
    int Poles = 2,
    double CutoffRatio = 4.0,
    double FixedHz = 1000.0,
    double Q = 0.7071,
    double FormantRatio = 2.0,
    double FormantLevel = 0.5,
    double PeakGainDb = 6.0,
    double EnvelopeOctaves = 0.0,
    OperatorEnvelope? Envelope = null)
{
    /// <summary>Smallest Q.</summary>
    public const double MinQ = 0.5;

    /// <summary>Largest Q, below self-oscillation.</summary>
    public const double MaxQ = 20.0;

    /// <summary>The Q of a Butterworth response.</summary>
    public const double ButterworthQ = 0.7071;

    /// <summary>True for the types whose frequency follows the note; peak and formant are fixed in hertz.</summary>
    public bool IsKeyTracked => Type is OperatorFilterType.LowPass or OperatorFilterType.HighPass or OperatorFilterType.BandPass or OperatorFilterType.Notch;

    /// <summary>A low-pass filter.</summary>
    /// <param name="cutoffRatio">Cutoff as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    /// <param name="poles">2 or 4.</param>
    public static OperatorFilter LowPass(double cutoffRatio, double q = ButterworthQ, int poles = 2) =>
        new(OperatorFilterType.LowPass, poles, CutoffRatio: cutoffRatio, Q: q);

    /// <summary>A high-pass filter.</summary>
    /// <param name="cutoffRatio">Cutoff as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    /// <param name="poles">2 or 4.</param>
    public static OperatorFilter HighPass(double cutoffRatio, double q = ButterworthQ, int poles = 2) =>
        new(OperatorFilterType.HighPass, poles, CutoffRatio: cutoffRatio, Q: q);

    /// <summary>A band-pass filter.</summary>
    /// <param name="centreRatio">Centre as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    public static OperatorFilter BandPass(double centreRatio, double q) => new(OperatorFilterType.BandPass, CutoffRatio: centreRatio, Q: q);

    /// <summary>A notch filter.</summary>
    /// <param name="centreRatio">Centre as a multiple of the note.</param>
    /// <param name="q">Resonance.</param>
    public static OperatorFilter Notch(double centreRatio, double q) => new(OperatorFilterType.Notch, CutoffRatio: centreRatio, Q: q);

    /// <summary>A peak filter.</summary>
    /// <param name="hz">Centre in hertz.</param>
    /// <param name="gainDb">Gain at the centre.</param>
    /// <param name="q">Resonance.</param>
    public static OperatorFilter PeakAt(double hz, double gainDb, double q) => new(OperatorFilterType.Peak, FixedHz: hz, Q: q, PeakGainDb: gainDb);

    /// <summary>A formant filter.</summary>
    /// <param name="f1Hz">First peak in hertz.</param>
    /// <param name="f2Ratio">Second peak as a multiple of the first.</param>
    /// <param name="f2Level">Level of the second peak relative to the first.</param>
    /// <param name="q">Resonance of both peaks.</param>
    public static OperatorFilter FormantAt(double f1Hz, double f2Ratio, double f2Level, double q) =>
        new(OperatorFilterType.Formant, FixedHz: f1Hz, Q: q, FormantRatio: f2Ratio, FormantLevel: f2Level);

    /// <summary>What is wrong with the filter, without operator prefix; empty when it is well formed.</summary>
    public IEnumerable<string> Validate()
    {
        if (Poles is not (2 or 4) || (Poles == 4 && Type is not (OperatorFilterType.LowPass or OperatorFilterType.HighPass)))
        {
            yield return "poles must be 2, or 4 for a low-pass or high-pass.";
        }

        if (!(CutoffRatio is >= 0.25 and <= 64.0))
        {
            yield return "cutoff ratio must be between 0.25 and 64.";
        }

        if (!(FixedHz is >= 10.0 and <= 20000.0))
        {
            yield return "frequency must be between 10 and 20000 Hz.";
        }

        if (!(Q is >= MinQ and <= MaxQ))
        {
            yield return "Q must be between 0.5 and 20.";
        }

        if (!(FormantRatio is >= 1.0 and <= 16.0))
        {
            yield return "formant ratio must be between 1 and 16.";
        }

        if (!(FormantLevel is >= 0.0 and <= 1.0))
        {
            yield return "formant level must be between 0 and 1.";
        }

        if (!(PeakGainDb is >= -24.0 and <= 24.0))
        {
            yield return "peak gain must be between -24 and 24 dB.";
        }

        if (!(EnvelopeOctaves is >= -4.0 and <= 4.0))
        {
            yield return "envelope depth must be between -4 and 4 octaves.";
        }

        if (Envelope is not null)
        {
            foreach (string problem in Envelope.Validate())
            {
                yield return "envelope: " + problem;
            }
        }
    }
}
```

The `!(x is >= a and <= b)` form also rejects NaN.

- [ ] **Step 4: Add the property to `OperatorPatch`**

In `src/HearSay.Synthesis/Patches/OperatorPatch.cs`, add `using System.Text.Json.Serialization;` at the top and, after the `NoiseM` property:

```csharp
    /// <summary>
    /// Filter between the waveform and the amplitude, for backends with operator filters
    /// (<see cref="Profiles.SynthesisCapabilities.SupportsOperatorFilters"/>); null for none. Left out of the JSON when
    /// null, so that patches without filters keep their serialized form and their hash.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperatorFilter? Filter { get; init; }
```

- [ ] **Step 5: Validate filters in `OperatorMatrixPatch.Validate()`**

In the operator loop, after the `HarmonicLimit` check:

```csharp
            if (op.Filter is not null)
            {
                foreach (string problem in op.Filter.Validate())
                {
                    problems.Add($"OP{i + 1} filter {problem}");
                }
            }
```

- [ ] **Step 6: Capability, library version and FMX6 profile**

In `src/HearSay.Synthesis/Profiles/SynthesisProfile.cs`:

1. Add the doc line `/// <param name="SupportsOperatorFilters">Every operator may have a filter (<see cref="OperatorPatch.Filter"/>).</param>` after the `SupportsSharedNoise` param doc, and the parameter `bool SupportsOperatorFilters = false` after `bool SupportsSharedNoise = false`.
2. In `IsSubsetOf`, replace `&& (!SupportsSharedNoise || other.SupportsSharedNoise);` with
   ```csharp
               && (!SupportsSharedNoise || other.SupportsSharedNoise)
               && (!SupportsOperatorFilters || other.SupportsOperatorFilters);
   ```
3. Give `SynthesisProfile` a body:
   ```csharp
   public sealed record SynthesisProfile(
       string Name,
       SynthesisCapabilities Capabilities,
       RenderProfile RenderProfile,
       int ControlBlockSize,
       string Description)
   {
       /// <summary>
       /// Version of the structure library of this profile: part of the cache file name, so raising it rebuilds that
       /// library once. Raise it when the random patches of the profile change; raise every profile when the file
       /// layout or the fingerprint changes.
       /// </summary>
       public int StructureLibraryVersion { get; init; } = 1;
   }
   ```
4. The FMX6 profile: add `SupportsOperatorFilters = true,` inside its `with { ... }` after `SupportedWaveforms = ...`, append ` { StructureLibraryVersion = 2 }` after the closing `)` of `new(...)`, change the description to `"Six operators with sine, triangle, saw, square, pulse, noise and filtered-noise waveforms and a filter per operator, full 6x6 matrix, SuperCollider FMX6 UGen."`, and add to its XML summary: `Needs FMX6 with operator filters.`

- [ ] **Step 7: Compatibility check**

In `src/HearSay.Synthesis/Profiles/PatchCompatibilityValidator.cs`, add to `PatchCompatibilityCodes`:

```csharp
    /// <summary>An operator has a filter the backend cannot play.</summary>
    public const string OperatorFilter = "operator-filter";
```

and in the operator loop of `Validate`, after the shared-noise check:

```csharp
            if (!capabilities.SupportsOperatorFilters && op.Filter is not null)
            {
                issues.Add(new PatchCompatibilityIssue(PatchCompatibilityCodes.OperatorFilter, $"OP{i + 1} has a filter, which the backend does not have.", i));
            }
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test tests/HearSay.Synthesis.Tests`
Expected: all pass, including the six new tests.

- [ ] **Step 9: Commit**

```bash
git add src/HearSay.Synthesis tests/HearSay.Synthesis.Tests/OperatorFilterModelTests.cs
git commit -m "Model: optional filter per operator, capability for backends with operator filters

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Shared filter kernel

**Files:**
- Create: `src/HearSay.Synthesis/Filters/OperatorFilterKernel.cs`
- Test: `tests/HearSay.Synthesis.Tests/OperatorFilterKernelTests.cs` (new)

**Interfaces:**
- Consumes: `OperatorFilter`, `OperatorFilterType` (Task 1).
- Produces: `struct SvfState { double Ic1; double Ic2; }`;
  `static class OperatorFilterKernel` with constants `Off, LowPass2, LowPass4, HighPass2, HighPass4, BandPass, Notch, Peak, Formant` (0..8),
  `static int Code(OperatorFilter? filter)`,
  `static double Process(int type, double x, double baseHz, double octaves, double q, double formantRatio, double formantLevel, double gainDb, double sampleRate, ref SvfState first, ref SvfState second)`.

- [ ] **Step 1: Write the failing tests**

`tests/HearSay.Synthesis.Tests/OperatorFilterKernelTests.cs`:

```csharp
using HearSay.Synthesis.Filters;
using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Tests;

public sealed class OperatorFilterKernelTests
{
    private const double Rate = 48000.0;

    /// <summary>Steady-state gain of a sine through a filter: RMS of the second half over RMS of the input.</summary>
    private static double Gain(int type, double sineHz, double baseHz, double q = 0.7071, double octaves = 0, double ratio = 2, double level = 0.5, double gainDb = 0)
    {
        var a = default(SvfState);
        var b = default(SvfState);
        int n = 48000;
        double sumIn = 0, sumOut = 0;
        for (int i = 0; i < n; i++)
        {
            double x = Math.Sin(2.0 * Math.PI * sineHz * i / Rate);
            double y = OperatorFilterKernel.Process(type, x, baseHz, octaves, q, ratio, level, gainDb, Rate, ref a, ref b);
            if (i >= n / 2)
            {
                sumIn += x * x;
                sumOut += y * y;
            }
        }

        return Math.Sqrt(sumOut / sumIn);
    }

    private static double Db(double gain) => 20.0 * Math.Log10(gain);

    [Fact]
    public void LowPass_FallsTwelveDbPerOctave_AndFourPolesTwiceAsFast()
    {
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass2, 100, 1000)), -0.5, 0.5);
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass2, 1000, 1000)), -3.6, -2.4);
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass2, 4000, 1000)), -26, -22);
        Assert.InRange(Db(Gain(OperatorFilterKernel.LowPass4, 4000, 1000)), -52, -44);
    }

    [Fact]
    public void HighPass_MirrorsTheLowPass()
    {
        Assert.InRange(Db(Gain(OperatorFilterKernel.HighPass2, 10000, 1000)), -0.5, 0.5);
        Assert.InRange(Db(Gain(OperatorFilterKernel.HighPass2, 250, 1000)), -26, -22);
        Assert.InRange(Db(Gain(OperatorFilterKernel.HighPass4, 250, 1000)), -52, -44);
    }

    [Fact]
    public void BandPassNotchAndPeak_ActAtTheirCentre()
    {
        Assert.InRange(Gain(OperatorFilterKernel.BandPass, 1000, 1000, q: 4), 0.98, 1.02);
        Assert.True(Gain(OperatorFilterKernel.BandPass, 4000, 1000, q: 4) < 0.1);
        Assert.True(Gain(OperatorFilterKernel.Notch, 1000, 1000, q: 4) < 0.02);
        Assert.InRange(Gain(OperatorFilterKernel.Notch, 4000, 1000, q: 4), 0.95, 1.01);
        Assert.InRange(Db(Gain(OperatorFilterKernel.Peak, 1000, 1000, q: 2, gainDb: 12)), 11.8, 12.2);
        Assert.InRange(Db(Gain(OperatorFilterKernel.Peak, 1000, 1000, q: 2, gainDb: -12)), -12.2, -11.8);
        Assert.InRange(Gain(OperatorFilterKernel.Peak, 8000, 1000, q: 2, gainDb: 12), 0.98, 1.1);
    }

    [Fact]
    public void Formant_HasPeaksAtF1AndF2_AndTheEnvelopeShiftsBoth()
    {
        // F1 700 Hz, F2 = 1.8 x 700 = 1260 Hz at level 0.5, Q 10.
        double atF1 = Gain(OperatorFilterKernel.Formant, 700, 700, q: 10, ratio: 1.8, level: 0.5);
        double atF2 = Gain(OperatorFilterKernel.Formant, 1260, 700, q: 10, ratio: 1.8, level: 0.5);
        double between = Gain(OperatorFilterKernel.Formant, 940, 700, q: 10, ratio: 1.8, level: 0.5);
        Assert.InRange(atF1, 0.95, 1.1);
        Assert.InRange(atF2, 0.45, 0.6);
        Assert.True(between < 0.3, $"between the peaks {between:F3}");

        // One octave up by the envelope: F1 at 1400 Hz.
        Assert.InRange(Gain(OperatorFilterKernel.Formant, 1400, 700, q: 10, octaves: 1.0, ratio: 1.8, level: 0.5), 0.95, 1.1);
    }

    [Fact]
    public void Frequency_IsClampedAtBothEnds_AndStaysFinite()
    {
        var a = default(SvfState);
        var b = default(SvfState);
        for (int i = 0; i < 4800; i++)
        {
            double x = i % 2 == 0 ? 1.0 : -1.0;
            Assert.True(double.IsFinite(OperatorFilterKernel.Process(OperatorFilterKernel.LowPass4, x, 1e6, 0, 20, 2, 0.5, 0, Rate, ref a, ref b)));
            Assert.True(double.IsFinite(OperatorFilterKernel.Process(OperatorFilterKernel.Formant, x, 20, -4, 20, 16, 1, 0, Rate, ref a, ref b)));
        }
    }

    [Fact]
    public void Code_FollowsTheTypeAndThePoles()
    {
        Assert.Equal(OperatorFilterKernel.Off, OperatorFilterKernel.Code(null));
        Assert.Equal(1, OperatorFilterKernel.Code(OperatorFilter.LowPass(4)));
        Assert.Equal(2, OperatorFilterKernel.Code(OperatorFilter.LowPass(4, poles: 4)));
        Assert.Equal(3, OperatorFilterKernel.Code(OperatorFilter.HighPass(4)));
        Assert.Equal(4, OperatorFilterKernel.Code(OperatorFilter.HighPass(4, poles: 4)));
        Assert.Equal(5, OperatorFilterKernel.Code(OperatorFilter.BandPass(2, 2)));
        Assert.Equal(6, OperatorFilterKernel.Code(OperatorFilter.Notch(2, 2)));
        Assert.Equal(7, OperatorFilterKernel.Code(OperatorFilter.PeakAt(1000, 6, 2)));
        Assert.Equal(8, OperatorFilterKernel.Code(OperatorFilter.FormantAt(700, 2, 0.5, 8)));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/HearSay.Synthesis.Tests --filter OperatorFilterKernelTests`
Expected: build error, `HearSay.Synthesis.Filters` does not exist.

- [ ] **Step 3: Write the kernel**

`src/HearSay.Synthesis/Filters/OperatorFilterKernel.cs`:

```csharp
using HearSay.Synthesis.Patches;

namespace HearSay.Synthesis.Filters;

/// <summary>The two integrator states of one TPT state-variable filter.</summary>
public struct SvfState
{
    /// <summary>First integrator.</summary>
    public double Ic1;

    /// <summary>Second integrator.</summary>
    public double Ic2;
}

/// <summary>
/// The operator filter of the FMX6 UGen, operation for operation, so that a renderer built on it is sample-exact:
/// TPT state-variable filters in double precision with <c>k = 1 / Q</c>, the frequency <c>baseHz x 2^octaves</c>
/// clamped to 10 Hz .. 0.49 x the sample rate. Stateless; the caller keeps two <see cref="SvfState"/> per operator.
/// </summary>
public static class OperatorFilterKernel
{
    /// <summary>No filter.</summary>
    public const int Off = 0;

    /// <summary>Two-pole low-pass.</summary>
    public const int LowPass2 = 1;

    /// <summary>Four-pole low-pass: a Butterworth stage, then a stage with the Q.</summary>
    public const int LowPass4 = 2;

    /// <summary>Two-pole high-pass.</summary>
    public const int HighPass2 = 3;

    /// <summary>Four-pole high-pass: a Butterworth stage, then a stage with the Q.</summary>
    public const int HighPass4 = 4;

    /// <summary>Band-pass, unity gain at the centre.</summary>
    public const int BandPass = 5;

    /// <summary>Notch.</summary>
    public const int Notch = 6;

    /// <summary>Peak with a gain in dB.</summary>
    public const int Peak = 7;

    /// <summary>Two band-pass peaks in parallel.</summary>
    public const int Formant = 8;

    private const double ButterworthK = 1.4142135623730951;

    /// <summary>The type code of the UGen for a filter.</summary>
    /// <param name="filter">Filter, or null for none.</param>
    public static int Code(OperatorFilter? filter) =>
        filter is null
            ? Off
            : filter.Type switch
            {
                OperatorFilterType.LowPass => filter.Poles == 4 ? LowPass4 : LowPass2,
                OperatorFilterType.HighPass => filter.Poles == 4 ? HighPass4 : HighPass2,
                OperatorFilterType.BandPass => BandPass,
                OperatorFilterType.Notch => Notch,
                OperatorFilterType.Peak => Peak,
                _ => Formant,
            };

    /// <summary>Filters one sample.</summary>
    /// <param name="type">Type code, <see cref="Off"/> .. <see cref="Formant"/>.</param>
    /// <param name="x">Input sample.</param>
    /// <param name="baseHz">Cutoff, centre or F1 before the envelope.</param>
    /// <param name="octaves">Envelope offset in octaves.</param>
    /// <param name="q">Resonance; clamped to 0.5..20.</param>
    /// <param name="formantRatio">F2 / F1.</param>
    /// <param name="formantLevel">Level of F2.</param>
    /// <param name="gainDb">Peak gain.</param>
    /// <param name="sampleRate">Sample rate the filter runs at.</param>
    /// <param name="first">State of the first filter.</param>
    /// <param name="second">State of the second filter (four poles, or F2).</param>
    public static double Process(int type, double x, double baseHz, double octaves, double q, double formantRatio, double formantLevel, double gainDb, double sampleRate, ref SvfState first, ref SvfState second)
    {
        double fc = baseHz * Math.Pow(2.0, octaves);
        double k = 1.0 / Math.Max(0.5, Math.Min(q, 20.0));
        switch (type)
        {
            case LowPass2:
            {
                Tick(x, fc, k, sampleRate, ref first, out _, out double lp);
                return lp;
            }

            case LowPass4:
            {
                Tick(x, fc, ButterworthK, sampleRate, ref first, out _, out double lp1);
                Tick(lp1, fc, k, sampleRate, ref second, out _, out double lp2);
                return lp2;
            }

            case HighPass2:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out double v2);
                return x - (k * v1) - v2;
            }

            case HighPass4:
            {
                Tick(x, fc, ButterworthK, sampleRate, ref first, out double v1, out double v2);
                double hp1 = x - (ButterworthK * v1) - v2;
                Tick(hp1, fc, k, sampleRate, ref second, out double w1, out double w2);
                return hp1 - (k * w1) - w2;
            }

            case BandPass:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                return k * v1;
            }

            case Notch:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                return x - (k * v1);
            }

            case Peak:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                double g = Math.Pow(10.0, gainDb / 20.0);
                return x + ((g - 1.0) * (k * v1));
            }

            case Formant:
            {
                Tick(x, fc, k, sampleRate, ref first, out double v1, out _);
                Tick(x, fc * formantRatio, k, sampleRate, ref second, out double w1, out _);
                return (k * v1) + (formantLevel * (k * w1));
            }

            default:
                return x;
        }
    }

    private static void Tick(double x, double fc, double k, double sampleRate, ref SvfState s, out double v1, out double v2)
    {
        double f = Math.Max(10.0, Math.Min(fc, 0.49 * sampleRate));
        double g = Math.Tan(Math.PI * f / sampleRate);
        double a1 = 1.0 / (1.0 + (g * (g + k)));
        double a2 = g * a1;
        double a3 = g * a2;
        double v3 = x - s.Ic2;
        v1 = (a1 * s.Ic1) + (a2 * v3);
        v2 = s.Ic2 + (a2 * s.Ic1) + (a3 * v3);
        s.Ic1 = (2.0 * v1) - s.Ic1;
        s.Ic2 = (2.0 * v2) - s.Ic2;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/HearSay.Synthesis.Tests --filter OperatorFilterKernelTests`
Expected: 6 pass. If a gain bound misses by a little, check the formula against the Global Constraints first; only widen a bound when the formula is right and the bound was too tight (report it).

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.Synthesis/Filters tests/HearSay.Synthesis.Tests/OperatorFilterKernelTests.cs
git commit -m "Synthesis: operator filter kernel, the arithmetic of the FMX6 filters

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: FMX6 UGen with operator filters (repository `D:\Projects\fmx6`)

**Files:**
- Modify: `D:\Projects\fmx6\FMX6.cpp`
- Modify: `D:\Projects\fmx6\Classes\FMX6.sc`
- Modify: `D:\Projects\fmx6\HelpSource\Classes\FMX6.schelp`, `D:\Projects\fmx6\README.md`

**Interfaces:**
- Consumes: the arithmetic of `OperatorFilterKernel` (Task 2); C++ must match it operation for operation.
- Produces: `FMX6.ar(ctlMatrix, modMatrix, waves, shapes, harmonicLimits, oversample, filters)` where `filters` is nil (73 inputs, as today) or six rows `[type, freq, octaves, q, formantRatio, formantLevel, gainDb]` (115 inputs).

- [ ] **Step 1: Branch**

```bash
cd /d/Projects/fmx6 && git status --short && git checkout -b operator-filters
```
Expected: clean tree, new branch.

- [ ] **Step 2: Constants, state and filter functions in `FMX6.cpp`**

After `constexpr int kMaxWave = 7;` add:

```cpp
// Operator filters (optional): six [type, freq, octaves, q, formantRatio, formantLevel, gainDb] rows after the
// oversampling input. type: 0 off, 1 LP 2-pole, 2 LP 4-pole, 3 HP 2-pole, 4 HP 4-pole, 5 band-pass, 6 notch, 7 peak,
// 8 formant. The frequency is freq * 2^octaves, computed here in double precision.
constexpr int kFilterBase = kOversampleIndex + 1;
constexpr int kNumFilterFields = 7;
constexpr int kNumInputsWithFilters = kFilterBase + kNumOps * kNumFilterFields;
constexpr int kMaxFilterType = 8;
constexpr double kButterworthK = 1.4142135623730951;

inline int filterIndex(int op, int field) { return kFilterBase + op * kNumFilterFields + field; }
```

Add before `struct FMX6Op`:

```cpp
struct Svf {
    double ic1;
    double ic2;
};
```

Add to `struct FMX6Op` after `double svfIc2;`:

```cpp
    float filterFreq;
    float filterOct;
    Svf filterA;
    Svf filterB;
```

Add to `struct FMX6` after `int oversample;`:

```cpp
    bool hasFilters;
```

After the `filteredNoise` function add:

```cpp
// One TPT state-variable filter step, the same arithmetic as the filtered-noise waveform with k = 1/Q.
inline void svfTick(double x, double fc, double k, double sampleRate, Svf& s, double& v1, double& v2) {
    const double f = std::max(10.0, std::min(fc, 0.49 * sampleRate));
    const double g = std::tan(kPi * f / sampleRate);
    const double a1 = 1.0 / (1.0 + g * (g + k));
    const double a2 = g * a1;
    const double a3 = g * a2;
    const double v3 = x - s.ic2;
    v1 = a1 * s.ic1 + a2 * v3;
    v2 = s.ic2 + a2 * s.ic1 + a3 * v3;
    s.ic1 = 2.0 * v1 - s.ic1;
    s.ic2 = 2.0 * v2 - s.ic2;
}

// The operator filter. HearSay reproduces this function operation for operation (OperatorFilterKernel).
inline double operatorFilter(int type, double x, double baseHz, double octaves, double q, double formantRatio,
                             double formantLevel, double gainDb, double sampleRate, Svf& a, Svf& b) {
    const double fc = baseHz * std::pow(2.0, octaves);
    const double k = 1.0 / std::max(0.5, std::min(q, 20.0));
    double v1 = 0.0, v2 = 0.0, w1 = 0.0, w2 = 0.0;
    switch (type) {
    case 1:
        svfTick(x, fc, k, sampleRate, a, v1, v2);
        return v2;
    case 2:
        svfTick(x, fc, kButterworthK, sampleRate, a, v1, v2);
        svfTick(v2, fc, k, sampleRate, b, w1, w2);
        return w2;
    case 3:
        svfTick(x, fc, k, sampleRate, a, v1, v2);
        return x - k * v1 - v2;
    case 4: {
        svfTick(x, fc, kButterworthK, sampleRate, a, v1, v2);
        const double hp1 = x - kButterworthK * v1 - v2;
        svfTick(hp1, fc, k, sampleRate, b, w1, w2);
        return hp1 - k * w1 - w2;
    }
    case 5:
        svfTick(x, fc, k, sampleRate, a, v1, v2);
        return k * v1;
    case 6:
        svfTick(x, fc, k, sampleRate, a, v1, v2);
        return x - k * v1;
    case 7: {
        svfTick(x, fc, k, sampleRate, a, v1, v2);
        const double g = std::pow(10.0, gainDb / 20.0);
        return x + (g - 1.0) * (k * v1);
    }
    case 8:
        svfTick(x, fc, k, sampleRate, a, v1, v2);
        svfTick(x, fc * formantRatio, k, sampleRate, b, w1, w2);
        return k * v1 + formantLevel * (k * w1);
    default:
        return x;
    }
}
```

The C# kernel writes `x - (k * v1) - v2` and C++ `x - k * v1 - v2`: the same evaluation order (left to right, multiplication first). Do not reorder any expression.

- [ ] **Step 3: Use the filter in `FMX6_next`**

After `float* outs[kNumOps];` add:

```cpp
    int ftype[kNumOps];
    float ffreq[kNumOps];
    float ffreqInc[kNumOps];
    float foct[kNumOps];
    float foctInc[kNumOps];
    double fq[kNumOps];
    double fratio[kNumOps];
    double flevel[kNumOps];
    double fgain[kNumOps];
```

Inside the first per-operator loop, after `harmonicLimit[i] = ...;`:

```cpp
        if (unit->hasFilters) {
            ftype[i] = std::max(0, std::min(kMaxFilterType, static_cast<int>(std::lround(IN0(filterIndex(i, 0))))));
            ffreq[i] = op.filterFreq;
            ffreqInc[i] = CALCSLOPE(IN0(filterIndex(i, 1)), ffreq[i]) * slopeScale;
            foct[i] = op.filterOct;
            foctInc[i] = CALCSLOPE(IN0(filterIndex(i, 2)), foct[i]) * slopeScale;
            fq[i] = static_cast<double>(IN0(filterIndex(i, 3)));
            fratio[i] = static_cast<double>(IN0(filterIndex(i, 4)));
            flevel[i] = static_cast<double>(IN0(filterIndex(i, 5)));
            fgain[i] = static_cast<double>(IN0(filterIndex(i, 6)));
        } else {
            ftype[i] = 0;
            ffreq[i] = ffreqInc[i] = foct[i] = foctInc[i] = 0.f;
            fq[i] = fratio[i] = flevel[i] = fgain[i] = 0.0;
        }
```

In the sample loop, replace `const float y = amp[i] * osc;` with:

```cpp
            const float shaped = ftype[i] == 0
                ? osc
                : static_cast<float>(operatorFilter(ftype[i], static_cast<double>(osc), static_cast<double>(ffreq[i]),
                                                    static_cast<double>(foct[i]), fq[i], fratio[i], flevel[i], fgain[i],
                                                    static_cast<double>(sampleRate), op.filterA, op.filterB));
            const float y = amp[i] * shaped;
```

In the commit loop, after `amp[i] += ampInc[i];` add:

```cpp
            ffreq[i] += ffreqInc[i];
            foct[i] += foctInc[i];
```

At the end of `FMX6_next`, in the loop that stores `freq`, `phaseOffset`, `amp`, add:

```cpp
        unit->ops[i].filterFreq = ffreq[i];
        unit->ops[i].filterOct = foct[i];
```

- [ ] **Step 4: Constructor**

In `FMX6_Ctor`:

1. After `unit->oversample = 1;` add `unit->hasFilters = false;`.
2. Replace the input-count condition with:
   ```cpp
    if ((unit->mNumInputs != kNumInputs && unit->mNumInputs != kNumInputs + 1 && unit->mNumInputs != kNumInputsWithFilters)
        || unit->mNumOutputs != kNumOps) {
        Print("FMX6: expected %d, %d or %d inputs and %d outputs, got %d inputs and %d outputs.\n",
              kNumInputs, kNumInputs + 1, kNumInputsWithFilters, kNumOps, unit->mNumInputs, unit->mNumOutputs);
   ```
   (keep the rest of that block).
3. After the oversample read add `unit->hasFilters = unit->mNumInputs == kNumInputsWithFilters;`.
4. In the operator initialization loop, after `op.svfIc2 = 0.0;`:
   ```cpp
        op.filterFreq = unit->hasFilters ? IN0(filterIndex(i, 1)) : 0.f;
        op.filterOct = unit->hasFilters ? IN0(filterIndex(i, 2)) : 0.f;
        op.filterA = Svf{0.0, 0.0};
        op.filterB = Svf{0.0, 0.0};
   ```

Update the input layout comment at the top of the file with the line `//   73 .. 114: optional operator filters, six [type, freq, octaves, q, formantRatio, formantLevel, gainDb] rows`.

- [ ] **Step 5: sclang class**

In `Classes/FMX6.sc`:

1. Add `*numFilterFields { ^7 }` after `*numControls { ^3 }`.
2. Change the signature to `|ctlMatrix, modMatrix, waves = 0, shapes = 0.5, harmonicLimits = 32, oversample = 1, filters|` and add `var fs;` to the variable declarations.
3. Replace `inputs = ctls ++ ws ++ sh ++ hs ++ mods ++ [oversample];` with:
   ```supercollider
        inputs = ctls ++ ws ++ sh ++ hs ++ mods ++ [oversample];

        // Without filters the SynthDef is exactly what it was before filters existed.
        if(filters.notNil) {
            fs = filters.flatten(1);
            if(fs.size != (this.numOperators * this.numFilterFields)) {
                Error("FMX6: filters must be 6 rows of [type, freq, octaves, q, formantRatio, formantLevel, gainDb].").throw;
            };
            inputs = inputs ++ fs;
        };
   ```
4. Replace `checkInputs` with:
   ```supercollider
    // 72 inputs: built before oversampling existed; 73: no filters; 115: with filters.
    checkInputs {
        var n = this.class.numRequiredInputs;
        ^if([n - 1, n, n + (this.class.numOperators * this.class.numFilterFields)].includes(inputs.size).not) {
            n.asString ++ " inputs required (" ++ inputs.size ++ ")"
        }
    }
   ```
5. Add class methods after `*filteredNoise { ^7 }`:
   ```supercollider
    *filterOff { ^0 }
    *lowPass2 { ^1 }
    *lowPass4 { ^2 }
    *highPass2 { ^3 }
    *highPass4 { ^4 }
    *bandPass { ^5 }
    *notch { ^6 }
    *peak { ^7 }
    *formant { ^8 }
   ```

- [ ] **Step 6: Help file and README**

In `HelpSource/Classes/FMX6.schelp`, add an `argument:: filters` entry under the `*ar` method:

```
argument:: filters
Optional. Six rows code::[type, freq, octaves, q, formantRatio, formantLevel, gainDb]::, one per operator. The filter
sits between the waveform and the amplitude: what the operator outputs and what it modulates other operators with is
the filtered signal. type: 0 off, 1 low-pass 2-pole, 2 low-pass 4-pole, 3 high-pass 2-pole, 4 high-pass 4-pole,
5 band-pass (unity gain at the centre), 6 notch, 7 peak (gainDb at the centre), 8 formant (band-pass peaks at freq and
freq * formantRatio, the second at formantLevel). The frequency is code::freq * 2 ** octaves::; drive code::octaves::
with an EnvGen for a filter sweep. q from 0.5 to 20. Without this argument the UGen behaves as before.
```

and an example under `examples::`:

```supercollider
(
{
    var ctl = FMX6.controlMatrix([0, 110, 0, 0.2]);
    var sweep = 3 * EnvGen.kr(Env([0, 1, 0], [0.05, 0.6]));
    var filters = [[2, 220, sweep, 2, 1, 0, 0]] ++ ({ [0, 0, 0, 1, 1, 0, 0] } ! 5);
    FMX6.ar(ctl, nil, waves: [2, 0, 0, 0, 0, 0], filters: filters)[0] ! 2
}.play;
)
```

In `README.md`, add a section "Operator filters" with the same text as the `filters` argument entry.

- [ ] **Step 7: Build and install**

```bash
cd /d/Projects/fmx6 && cmake --build build --config Release
```
Expected: `FMX6.scx` rebuilt (find it with `ls build/Release`). Then copy it, the class and the help file into the installed extension:

```bash
cp build/Release/FMX6.scx "$LOCALAPPDATA/SuperCollider/Extensions/FMX6UGens/FMX6.scx"
cp Classes/FMX6.sc "$LOCALAPPDATA/SuperCollider/Extensions/FMX6UGens/Classes/FMX6.sc"
cp HelpSource/Classes/FMX6.schelp "$LOCALAPPDATA/SuperCollider/Extensions/FMX6UGens/HelpSource/Classes/FMX6.schelp"
```

- [ ] **Step 8: Backward compatibility against HearSay**

Run: `cd /d/Projects/hearsay && dotnet test tests/HearSay.SuperCollider.Tests --filter Fmx6Tests`
Expected: all FMX6 tests pass. They export SynthDefs without filters (73 inputs), so they prove the new plugin plays old SynthDefs sample-exactly.

- [ ] **Step 9: Commit (fmx6 repository)**

```bash
cd /d/Projects/fmx6 && git add FMX6.cpp Classes/FMX6.sc HelpSource/Classes/FMX6.schelp README.md
git commit -m "Operator filters: low-pass, high-pass, band-pass, notch, peak and formant per operator

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Compiler and renderer

**Files:**
- Modify: `src/HearSay.Synthesis/Compilation/CompiledOperatorMatrixPatch.cs`
- Modify: `src/HearSay.Synthesis.Fmx6Compatible/Rendering/Fmx6CompatibleOperatorMatrixRenderer.cs`
- Test: `tests/HearSay.SuperCollider.Tests/Fmx6FilterTests.cs` (new; the FMX6 renderer tests live in this project)

**Interfaces:**
- Consumes: `OperatorPatch.Filter` (Task 1), `OperatorFilterKernel`, `SvfState` (Task 2).
- Produces: `sealed record CompiledFilters(int[] Types, float[] BaseHz, float[][] Octaves, float[] Q, float[] FormantRatios, float[] FormantLevels, float[] GainsDb)` in `HearSay.Synthesis.Compilation`; `CompiledOperatorMatrixPatch.Filters` (`CompiledFilters?`, null when no audible operator has a filter). `Octaves[k]` has length 1 (static) or `BlockCount + 1`, like the other control arrays.

- [ ] **Step 1: Write the failing tests**

`tests/HearSay.SuperCollider.Tests/Fmx6FilterTests.cs`:

```csharp
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Rendering;

namespace HearSay.SuperCollider.Tests;

/// <summary>Operator filters in the FMX6-compatible renderer.</summary>
public sealed class Fmx6FilterTests
{
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Renderer = new();
    private const int Settled = 9600;

    internal static OperatorMatrixPatch Saw(OperatorFilter? filter, double ratio = 1) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 64, Filter = filter })
            .WithOutputLevel(0, 1.0);

    private static float[] Render(OperatorMatrixPatch patch, double hz = 100) =>
        Renderer.Render(patch, new RenderRequest(hz, null, 1.0, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.05), 48000)).Samples;

    private static double Centroid(float[] samples, int from, int count)
    {
        var spectrogram = new HearSay.Spectral.StftAnalyzer().Analyze(samples.AsSpan(from, count).ToArray(), 48000, 2048, 512);
        double weighted = 0, total = 0;
        for (int f = 0; f < spectrogram.FrameCount; f++)
        {
            ReadOnlySpan<float> frame = spectrogram.Frame(f);
            for (int k = 1; k < spectrogram.BinCount; k++)
            {
                double power = (double)frame[k] * frame[k];
                weighted += power * k * spectrogram.BinHz;
                total += power;
            }
        }

        return weighted / total;
    }

    [Fact]
    public void NoFilter_RendersExactlyAsBefore()
    {
        OperatorMatrixPatch plain = Saw(null);
        float[] expected = Render(plain);
        Assert.Equal(expected, Render(plain with { }));
        Assert.Null(HearSay.Synthesis.Compilation.PatchCompiler.Compile(plain, RenderRequest.Create(100, 0.4, 0.05)).Filters);
    }

    [Fact]
    public void LowPass_DarkensTheSaw_FourPolesMore()
    {
        double open = Centroid(Render(Saw(null)), Settled, 4800);
        double two = Centroid(Render(Saw(OperatorFilter.LowPass(4))), Settled, 4800);
        double four = Centroid(Render(Saw(OperatorFilter.LowPass(4, poles: 4))), Settled, 4800);
        Assert.True(two < open * 0.7 && four < two, $"open {open:F0}, 2 poles {two:F0}, 4 poles {four:F0}");
    }

    [Fact]
    public void FilteredModulator_ChangesTheModulation()
    {
        OperatorMatrixPatch Fm(OperatorFilter? filter) =>
            OperatorMatrixPatch.Silent(6)
                .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()))
                .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Saw, Filter = filter })
                .WithOutputLevel(0, 1.0)
                .WithEdge(new ModulationEdge(1, 0, true, 2.0));
        double plain = Centroid(Render(Fm(null)), Settled, 4800);
        double soft = Centroid(Render(Fm(OperatorFilter.LowPass(1.5, poles: 4))), Settled, 4800);
        Assert.True(soft < plain * 0.8, $"plain {plain:F0}, low-passed modulator {soft:F0}");
    }

    [Fact]
    public void Sweep_StartsBrightAndCloses()
    {
        var sweep = new OperatorEnvelope([new EnvelopePoint(0, 1), new EnvelopePoint(0.15, 0), new EnvelopePoint(0.05, 0)], SustainPoint: 1, ReleasePoint: 2);
        float[] samples = Render(Saw(OperatorFilter.LowPass(2, poles: 4) with { EnvelopeOctaves = 3, Envelope = sweep }));
        double early = Centroid(samples, 0, 2400);
        double late = Centroid(samples, 12000, 4800);
        Assert.True(early > late * 1.5, $"early {early:F0}, late {late:F0}");
    }

    [Fact]
    public void ExtremeSettings_StayFinite()
    {
        // Review focus 1: a cutoff far above Nyquist on a high note is clamped, not NaN.
        Assert.All(Render(Saw(OperatorFilter.LowPass(64, q: 20, poles: 4)), hz: 2000), static s => Assert.True(float.IsFinite(s)));

        // Review focus 2: a resonant filter inside a self-feedback loop stays bounded.
        OperatorMatrixPatch loop = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Filter = OperatorFilter.BandPass(3, 20) })
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(0, 0, true, 3.0));
        Assert.All(Render(loop), static s => Assert.True(float.IsFinite(s) && Math.Abs(s) < 50f));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/HearSay.SuperCollider.Tests --filter Fmx6FilterTests`
Expected: build error, `Filters` does not exist on `CompiledOperatorMatrixPatch`.

- [ ] **Step 3: `CompiledFilters` and compilation**

In `src/HearSay.Synthesis/Compilation/CompiledOperatorMatrixPatch.cs`, add `using HearSay.Synthesis.Filters;` and, before `public sealed class CompiledOperatorMatrixPatch`:

```csharp
/// <summary>
/// Operator filters of the audible operators, in the order of <see cref="CompiledOperatorMatrixPatch.OperatorIndices"/>,
/// as the FMX6 UGen receives them: single-precision inputs, the envelope offset in octaves per control step.
/// </summary>
/// <param name="Types">Type code per operator, <see cref="OperatorFilterKernel.Off"/> for none.</param>
/// <param name="BaseHz">Cutoff, centre or F1 before the envelope.</param>
/// <param name="Octaves">Envelope offset; one entry when static, otherwise one per control step.</param>
/// <param name="Q">Resonance.</param>
/// <param name="FormantRatios">F2 / F1.</param>
/// <param name="FormantLevels">Level of F2.</param>
/// <param name="GainsDb">Peak gain.</param>
public sealed record CompiledFilters(int[] Types, float[] BaseHz, float[][] Octaves, float[] Q, float[] FormantRatios, float[] FormantLevels, float[] GainsDb);
```

Add a constructor parameter `CompiledFilters? filters` after `float[][] edgeAmounts`, assign `Filters = filters;`, and add the property:

```csharp
    /// <summary>Operator filters; null when no audible operator has one.</summary>
    public CompiledFilters? Filters { get; }
```

In `PatchCompiler.Compile`, before `var sources = new List<int>();` add:

```csharp
        CompiledFilters? filters = null;
        if (indices.Any(i => patch.Operators[i].Filter is not null))
        {
            var types = new int[n];
            var baseHz = new float[n];
            var octaves = new float[n][];
            var q = new float[n];
            var ratios = new float[n];
            var levels = new float[n];
            var gains = new float[n];
            for (int k = 0; k < n; k++)
            {
                OperatorPatch op = patch.Operators[indices[k]];
                OperatorFilter? filter = op.Filter;
                types[k] = OperatorFilterKernel.Code(filter);
                if (filter is null)
                {
                    octaves[k] = [0f];
                    q[k] = 1f;
                    ratios[k] = 1f;
                    continue;
                }

                // Single precision in the order of the exported SynthDef: (freq * ratio), or the fixed frequency.
                baseHz[k] = filter.IsKeyTracked ? (float)request.FrequencyHz * (float)filter.CutoffRatio : (float)filter.FixedHz;
                octaves[k] = filter.Envelope is null || filter.EnvelopeOctaves == 0.0
                    ? [0f]
                    : Run(filter.Envelope, steps, gateOffBlock, controlRate, op.KeyScaling.TimeFactor(midiNote), (float)filter.EnvelopeOctaves);
                q[k] = (float)filter.Q;
                ratios[k] = (float)filter.FormantRatio;
                levels[k] = (float)filter.FormantLevel;
                gains[k] = (float)filter.PeakGainDb;
            }

            filters = new CompiledFilters(types, baseHz, octaves, q, ratios, levels, gains);
        }
```

and pass `filters` as the last argument of `new CompiledOperatorMatrixPatch(...)`. (The rows for operators without a filter mirror what the exporter writes for them: `[0, 0, 0, 1, 1, 0, 0]`.)

- [ ] **Step 4: Renderer**

In `Fmx6CompatibleOperatorMatrixRenderer.Render(CompiledOperatorMatrixPatch compiled)`, add `using HearSay.Synthesis.Filters;` at the top of the file, then after `float[] shape = compiled.Shapes;`:

```csharp
        CompiledFilters? filters = compiled.Filters;
        var filterFreq = new float[n];
        var filterFreqInc = new float[n];
        var filterOct = new float[n];
        var filterOctInc = new float[n];
        var filterA = new SvfState[n];
        var filterB = new SvfState[n];
        if (filters is not null)
        {
            for (int k = 0; k < n; k++)
            {
                filterFreq[k] = filters.BaseHz[k];
                filterOct[k] = filters.Octaves[k][0];
            }
        }
```

In the per-block loop, after the `ampInc[k] = ...` line:

```csharp
                if (filters is not null)
                {
                    float[] o = filters.Octaves[k];
                    filterFreqInc[k] = (filters.BaseHz[k] - filterFreq[k]) * slopeFactor;
                    filterOctInc[k] = ((o.Length == 1 ? o[0] : o[step]) - filterOct[k]) * slopeFactor;
                }
```

In the sample loop, replace `float y = amp[k] * osc;` with:

```csharp
                    float shaped = filters is null || filters.Types[k] == OperatorFilterKernel.Off
                        ? osc
                        : (float)OperatorFilterKernel.Process(
                            filters.Types[k],
                            osc,
                            filterFreq[k],
                            filterOct[k],
                            filters.Q[k],
                            filters.FormantRatios[k],
                            filters.FormantLevels[k],
                            filters.GainsDb[k],
                            sampleRate,
                            ref filterA[k],
                            ref filterB[k]);
                    float y = amp[k] * shaped;
```

and after `amp[k] += ampInc[k];`:

```csharp
                    filterFreq[k] += filterFreqInc[k];
                    filterOct[k] += filterOctInc[k];
```

Add to the class remarks list: `<item>Operator filters: the TPT state-variable filters of <see cref="OperatorFilterKernel"/> between the waveform and the amplitude.</item>`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/HearSay.SuperCollider.Tests --filter "Fmx6FilterTests|Fmx6Tests"` and `dotnet test tests/HearSay.Synthesis.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.Synthesis/Compilation src/HearSay.Synthesis.Fmx6Compatible tests/HearSay.SuperCollider.Tests/Fmx6FilterTests.cs
git commit -m "FMX6 renderer: operator filters between the waveform and the amplitude

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Exporter and sample-exactness in SuperCollider

**Files:**
- Modify: `src/HearSay.SuperCollider/SuperColliderOperatorMatrixExporter.cs`
- Modify: `src/HearSay.SuperCollider/FMX6/SuperColliderFmx6Exporter.cs`
- Modify: `tests/HearSay.SuperCollider.Tests/Fmx6FilterTests.cs`

**Interfaces:**
- Consumes: `OperatorFilter`, `OperatorFilterKernel.Code` (Tasks 1, 2), the UGen of Task 3 (installed), the renderer of Task 4.
- Produces: `protected static string EnvGen(OperatorEnvelope envelope, string? timeScale)` and `protected static string? TimeScale(OperatorPatch op)` on `SuperColliderOperatorMatrixExporter`.

- [ ] **Step 1: Write the failing tests**

Add to `Fmx6FilterTests` (add `using HearSay.SuperCollider.FMX6;`, `using HearSay.SuperCollider.Validation;`, `using HearSay.Synthesis.Profiles;`, `using Xunit.Abstractions;`, and make the class `public sealed class Fmx6FilterTests(ITestOutputHelper output)`):

```csharp
    [Fact]
    public void Exporter_WritesFiltersOnlyWhenThereAreAny()
    {
        var exporter = new SuperColliderFmx6Exporter();
        Assert.DoesNotContain("filters:", exporter.Export(Saw(null)), StringComparison.Ordinal);

        var sweep = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1);
        OperatorMatrixPatch patch = Saw(OperatorFilter.LowPass(4, 2, poles: 4) with { EnvelopeOctaves = 1.5, Envelope = sweep })
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Filter = OperatorFilter.FormantAt(700, 1.8, 0.5, 8) })
            .WithOutputLevel(1, 0.5)
            .WithOperator(2, OperatorPatch.Sine(OperatorFrequency.AtRatio(3)) with { Enabled = false, Filter = OperatorFilter.Notch(2, 2) });
        string scd = exporter.Export(patch);

        Assert.Contains("filters: [", scd, StringComparison.Ordinal);
        Assert.Contains("[2, (freq * 4.0), (1.5 * EnvGen.kr(", scd, StringComparison.Ordinal);
        Assert.Contains("[8, 700.0, 0, 8.0, 1.8, 0.5, 6.0]", scd, StringComparison.Ordinal);

        // Review focus 4: the disabled operator's filter is written as off.
        Assert.Contains("[0, 0, 0, 1, 1, 0, 0]", scd, StringComparison.Ordinal);
    }

    /// <summary>The FMX6 SynthDef of a patch with the UGen at another oversampling factor.</summary>
    private sealed class OversampledExporter(int factor) : ISynthDefExporter
    {
        private readonly SuperColliderFmx6Exporter _inner = new();

        public int ControlBlockSize => _inner.ControlBlockSize;

        public string ExportSynthDef(OperatorMatrixPatch patch, string synthDefName = "HearSayPatch") =>
            _inner.ExportSynthDef(patch, synthDefName).Replace("FMX6.ar(ctlMatrix, modMatrix,", $"FMX6.ar(ctlMatrix, modMatrix, oversample: {factor},", StringComparison.Ordinal);
    }

    [SuperColliderFact]
    public void Filters_AreSampleExactInSuperCollider()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var validator = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new SuperColliderFmx6Exporter(), fmx6));
        var request = new RenderRequest(220, null, 1.0, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.1), 48000);
        var sweep = OperatorEnvelope.Adsr(0.02, 0.2, 0.2, 0.1);
        OperatorMatrixPatch modulated = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()))
            .WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Square, Filter = OperatorFilter.LowPass(3, 4, poles: 4) })
            .WithOutputLevel(0, 1.0)
            .WithEdge(new ModulationEdge(1, 0, true, 1.2));
        (string Name, OperatorMatrixPatch Patch)[] cases =
        [
            ("lp2", Saw(OperatorFilter.LowPass(4, 2))),
            ("lp4 sweep", Saw(OperatorFilter.LowPass(2, 3, poles: 4) with { EnvelopeOctaves = 3, Envelope = sweep })),
            ("hp2", Saw(OperatorFilter.HighPass(3, 1.5))),
            ("hp4", Saw(OperatorFilter.HighPass(3, 1.5, poles: 4))),
            ("bp", Saw(OperatorFilter.BandPass(5, 6))),
            ("notch", Saw(OperatorFilter.Notch(3, 3))),
            ("peak", Saw(OperatorFilter.PeakAt(1500, 12, 4))),
            ("formant", Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 10))),
            ("formant sweep", Saw(OperatorFilter.FormantAt(500, 2.2, 0.4, 8) with { EnvelopeOctaves = -1, Envelope = sweep })),
            ("filtered modulator", modulated),

            // Review focus 3: the envelope pushes the cutoff below 10 Hz, where both sides clamp.
            ("clamped low", Saw(OperatorFilter.LowPass(0.25, 2) with { EnvelopeOctaves = -4, Envelope = sweep })),
        ];

        IReadOnlyList<BackendExportValidation> results = validator.Validate([.. cases.Select(c => (c.Patch, request))]);
        for (int i = 0; i < results.Count; i++)
        {
            BackendExportValidation r = results[i];
            output.WriteLine($"{cases[i].Name,-20} max sample diff {r.MaxSampleDifference:E2} (peak {r.PeakLevel:F2}), overall {r.OverallDistance:F5}");
            Assert.True(r.MaxSampleDifference < 1e-4 * Math.Max(0.05, r.PeakLevel), $"{cases[i].Name}: {r}");
        }

        // At 2x oversampling the UGen runs and stays close to the 1x sound HearSay optimizes.
        var oversampled = new BackendEquivalenceValidator(
            Renderer,
            new SuperColliderFm7Renderer(SuperColliderFactAttribute.Installation.Value!, new OversampledExporter(2), fmx6));
        foreach (BackendExportValidation r in oversampled.Validate([(cases[1].Patch, request), (cases[7].Patch, request)]))
        {
            output.WriteLine($"2x: overall {r.OverallDistance:F5}");
            Assert.True(r.OverallDistance < 0.1, r.ToString());
        }
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/HearSay.SuperCollider.Tests --filter Fmx6FilterTests`
Expected: `Exporter_WritesFiltersOnlyWhenThereAreAny` fails (no `filters:` in the output); the SuperCollider test fails or is skipped without sclang.

- [ ] **Step 3: Share the envelope helpers**

In `SuperColliderOperatorMatrixExporter.cs`:

1. Change `private static string EnvGen(OperatorEnvelope envelope, string? timeScale)` to `protected static string EnvGen(OperatorEnvelope envelope, string? timeScale)` and give it a doc comment: `/// <summary>An <c>EnvGen.kr</c> expression for an envelope, gated, with an optional time scale expression.</summary>`.
2. In `OperatorRow`, replace the `string? timeScale = ...;` statement with `string? timeScale = TimeScale(op);` and add:
   ```csharp
    /// <summary>The key scaling time factor of an operator as a SynthDef expression; null when neutral.</summary>
    /// <param name="op">Operator.</param>
    protected static string? TimeScale(OperatorPatch op) =>
        op.KeyScaling.RatePerOctave != 0.0
            ? $"(2 ** ({Number(-op.KeyScaling.RatePerOctave)} * ((note - {Number(op.KeyScaling.ReferenceMidiNote)}) / 12)))"
            : null;
   ```
   A filter envelope uses its operator's key scaling, which already makes `ExportSynthDef` declare `note`; nothing else changes there.

- [ ] **Step 4: Write the `filters:` argument**

In `SuperColliderFmx6Exporter.cs`, add `using HearSay.Synthesis.Filters;` and replace `UGenCall` with:

```csharp
    /// <inheritdoc />
    protected override string UGenCall(OperatorMatrixPatch patch, bool[] audible)
    {
        string waves = PerOperator(patch, audible, static o => Fmx6CompatibleOperatorMatrixRenderer.WaveIndex(o.Waveform).ToString(CultureInfo.InvariantCulture), "0");
        string shapes = PerOperator(patch, audible, static o => Number(o.Shape), "0.5");
        string harmonicLimits = PerOperator(patch, audible, static o => o.HarmonicLimit.ToString(CultureInfo.InvariantCulture), "32");
        bool filtered = Enumerable.Range(0, patch.OperatorCount).Any(i => audible[i] && patch.Operators[i].Filter is not null);
        string filters = filtered
            ? $",\n\t\tfilters: [{PerOperator(patch, audible, FilterRow, Off)}] // [type, freq, octaves, q, formantRatio, formantLevel, gainDb]"
            : string.Empty;
        return $$"""
            operators = FMX6.ar(ctlMatrix, modMatrix,
            		waves: [{{waves}}], // 0 sine, 1 triangle, 2 saw, 3 square, 4 pulse, 5 sample-and-hold noise, 6 white noise, 7 filtered noise
            		shapes: [{{shapes}}], // pulse duty cycle; filtered noise: 0 low-pass, 0.5 band-pass, 1 high-pass
            		harmonicLimits: [{{harmonicLimits}}]{{filters}});
            """;
    }

    private const string Off = "[0, 0, 0, 1, 1, 0, 0]";

    /// <summary>One operator's filter row; filter types 1 LP2, 2 LP4, 3 HP2, 4 HP4, 5 BP, 6 notch, 7 peak, 8 formant.</summary>
    private static string FilterRow(OperatorPatch op)
    {
        if (op.Filter is not { } filter)
        {
            return Off;
        }

        string baseHz = filter.IsKeyTracked ? $"(freq * {Number(filter.CutoffRatio)})" : Number(filter.FixedHz);
        string octaves = filter.Envelope is null || filter.EnvelopeOctaves == 0.0
            ? "0"
            : $"({Number(filter.EnvelopeOctaves)} * {EnvGen(filter.Envelope, TimeScale(op))})";
        string type = OperatorFilterKernel.Code(filter).ToString(CultureInfo.InvariantCulture);
        return $"[{type}, {baseHz}, {octaves}, {Number(filter.Q)}, {Number(filter.FormantRatio)}, {Number(filter.FormantLevel)}, {Number(filter.PeakGainDb)}]";
    }
```

Note that `PerOperator` writes the `silent` text for inaudible operators, so a disabled operator's filter becomes `Off`. Update the class summary: `..., FMX6.ar(ctlMatrix, modMatrix, waves:, shapes:, harmonicLimits:, filters:)`; filters only when an audible operator has one, so other patches run on FMX6 without filters.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/HearSay.SuperCollider.Tests`
Expected: all pass, including `Filters_AreSampleExactInSuperCollider` (sclang is installed on this machine). If a case is not sample-exact, compare the two sides expression by expression before changing anything: the single-precision base frequency, the envelope multiply, and the order of operations in the kernel.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.SuperCollider tests/HearSay.SuperCollider.Tests/Fmx6FilterTests.cs
git commit -m "FMX6 export: operator filters, sample-exact against the UGen

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Random filtered patches and the per-profile library version

**Files:**
- Modify: `src/HearSay.Synthesis/Patches/RandomPatchGenerator.cs`
- Modify: `src/HearSay.OperatorMatrix/Priors/StructureLibrary.cs`
- Modify: `tests/HearSay.OperatorMatrix.Tests/StructureLibraryTests.cs`
- Test: `tests/HearSay.Synthesis.Tests/OperatorFilterModelTests.cs`

**Interfaces:**
- Consumes: `OperatorFilter` factories (Task 1), `SynthesisProfile.StructureLibraryVersion` (Task 1).
- Produces: `RandomPatchOptions.FilterProbability` (double, default 0). `StructureLibrary.FormatVersion` is removed; the file name is `{profile.Name}-v{profile.StructureLibraryVersion}-{size}.bin`.

- [ ] **Step 1: Write the failing tests**

Add to `OperatorFilterModelTests`:

```csharp
    [Fact]
    public void RandomFilters_LeaveTheRestOfThePatchAsTheSeedGaveIt()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var plain = new RandomPatchGenerator(fmx6, new RandomPatchOptions());
        var filtered = new RandomPatchGenerator(fmx6, new RandomPatchOptions { FilterProbability = 1.0 });
        int withFilter = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            OperatorMatrixPatch a = plain.Generate(seed);
            OperatorMatrixPatch b = filtered.Generate(seed);
            Assert.Empty(b.Validate());
            Assert.True(PatchCompatibilityValidator.Instance.Validate(b, fmx6).IsCompatible);
            withFilter += b.Operators.Count(static o => o.Filter is not null);
            OperatorMatrixPatch stripped = b with { Operators = [.. b.Operators.Select(static o => o with { Filter = null })] };
            Assert.Equal(a.ComputeHash(), stripped.ComputeHash());
        }

        Assert.True(withFilter > 20);

        // Profiles without filters never get one, whatever the option says.
        var fm7 = new RandomPatchGenerator(SynthesisProfiles.SuperColliderFm7.Capabilities, new RandomPatchOptions { FilterProbability = 1.0 });
        Assert.All(Enumerable.Range(0, 10), seed => Assert.All(fm7.Generate(seed).Operators, static o => Assert.Null(o.Filter)));
    }
```

In `tests/HearSay.OperatorMatrix.Tests/StructureLibraryTests.cs`, in `Library_IsSavedAndLoaded`, replace the file-name assertion with:

```csharp
            Assert.Contains($"-v{profile.StructureLibraryVersion}-50.bin", file, StringComparison.Ordinal);
```

and add a new test:

```csharp
    [Fact]
    public void LibraryVersion_IsPerProfile_AndTheFmx6LibraryHasFilters()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-library-" + Guid.NewGuid().ToString("N"));
        try
        {
            SynthesisProfile fm7 = SynthesisProfiles.SuperColliderFm7 with { Name = "test-fm7-" + Guid.NewGuid().ToString("N") };
            SynthesisProfile fmx6 = SynthesisProfiles.SuperColliderFmx6 with { Name = "test-fmx6-" + Guid.NewGuid().ToString("N") };
            StructureLibrary.For(fm7, 30, Renderer, directory);
            StructureLibrary library = StructureLibrary.For(fmx6, 120, new HearSay.Synthesis.Fmx6Compatible.Rendering.Fmx6CompatibleOperatorMatrixRenderer(), directory);

            Assert.Single(Directory.GetFiles(directory, $"{fm7.Name}-v1-30.bin"));
            Assert.Single(Directory.GetFiles(directory, $"{fmx6.Name}-v2-120.bin"));
            int filtered = 0;
            foreach ((int seed, long _) in library.Nearest(new byte[SpectralFingerprint.Length], 120))
            {
                filtered += library.Patch(seed).Operators.Any(static o => o.Filter is not null) ? 1 : 0;
            }

            Assert.InRange(filtered, 30, 110);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/HearSay.Synthesis.Tests --filter RandomFilters` and `dotnet test tests/HearSay.OperatorMatrix.Tests --filter StructureLibraryTests`
Expected: build error (`FilterProbability` does not exist).

- [ ] **Step 3: Random filters**

In `RandomPatchGenerator.cs`, add to `RandomPatchOptions`:

```csharp
    /// <summary>
    /// Chance that an enabled operator gets a filter, for profiles with operator filters. Drawn from a generator of its
    /// own, so the rest of the patch is the one the seed gives without filters.
    /// </summary>
    public double FilterProbability { get; init; }
```

In `Generate`, before the final `return`:

```csharp
        if (_capabilities.SupportsOperatorFilters && _options.FilterProbability > 0)
        {
            var filterRandom = new Random(unchecked((seed * 397) ^ 0x5F17));
            for (int i = 0; i < enabledCount; i++)
            {
                if (filterRandom.NextDouble() < _options.FilterProbability)
                {
                    operators[i] = operators[i] with { Filter = RandomFilter(filterRandom) };
                }
            }
        }
```

and add:

```csharp
    private static OperatorFilter RandomFilter(Random random)
    {
        double q = Round(0.5 * Math.Pow(16, random.NextDouble()));
        int poles = random.Next(2) == 0 ? 2 : 4;
        OperatorFilter filter = random.Next(6) switch
        {
            0 => OperatorFilter.LowPass(Round(1.5 * Math.Pow(16, random.NextDouble())), q, poles),
            1 => OperatorFilter.HighPass(Round(0.5 * Math.Pow(8, random.NextDouble())), q, poles),
            2 => OperatorFilter.BandPass(Round(1 + (random.NextDouble() * 7)), q),
            3 => OperatorFilter.Notch(Round(1 + (random.NextDouble() * 7)), q),
            4 => OperatorFilter.PeakAt(Round(200 * Math.Pow(20, random.NextDouble())), Round((random.NextDouble() * 24) - 12), q),
            _ => OperatorFilter.FormantAt(Round(250 + (random.NextDouble() * 650)), Round(1.5 + (random.NextDouble() * 2.5)), Round(0.3 + (random.NextDouble() * 0.7)), Round(4 + (random.NextDouble() * 8))),
        };
        if (random.NextDouble() >= 0.4)
        {
            return filter;
        }

        // A sweep that starts open and closes to the base frequency.
        double attack = Round(0.005 + (random.NextDouble() * 0.05));
        double decay = Round(0.05 + (random.NextDouble() * 0.5));
        var envelope = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(attack, 1), new EnvelopePoint(decay, 0), new EnvelopePoint(0.1, 0)], SustainPoint: 2, ReleasePoint: 3);
        return filter with { EnvelopeOctaves = Round(0.5 + (random.NextDouble() * 2.5)), Envelope = envelope };
    }
```

- [ ] **Step 4: Per-profile version and filters in the FMX6 library**

In `StructureLibrary.cs`:

1. Remove the `FormatVersion` constant.
2. `PathOf`: `return Path.Combine(directory, $"{profile.Name}-v{profile.StructureLibraryVersion}-{size}.bin");`
3. Pass the version to `LoadOrBuild`: `LoadOrBuild(profile.Capabilities, profile.StructureLibraryVersion, size, renderer, path)`, add an `int version` parameter to `LoadOrBuild` and to the save and load helpers it calls, and replace `writer.Write(FormatVersion)` by `writer.Write(version)` and `reader.ReadInt32() != FormatVersion` by `reader.ReadInt32() != version`.
4. In `Generate(SynthesisCapabilities capabilities, int seed)`, add to the options: `FilterProbability = capabilities.SupportsOperatorFilters ? 0.3 : 0.0,`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/HearSay.Synthesis.Tests` and `dotnet test tests/HearSay.OperatorMatrix.Tests --filter StructureLibraryTests`
Expected: all pass. Also check that the cached FM7 and Nome6 libraries keep their names: `ls "$LOCALAPPDATA/HearSay/structure-libraries"` shows `supercollider-fm7-v1-300000.bin` and `supercollider-nome6-v1-300000.bin`, which the new code still finds.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.Synthesis/Patches/RandomPatchGenerator.cs src/HearSay.OperatorMatrix/Priors/StructureLibrary.cs tests
git commit -m "Structure library: version per profile; FMX6 library patches with filters

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Filter parameters and complexity

**Files:**
- Modify: `src/HearSay.OperatorMatrix/Parameters/PatchParameterSchema.cs`
- Modify: `src/HearSay.OperatorMatrix/Canonicalization/OperatorMatrixRegimeKey.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/ParameterTests.cs`

**Interfaces:**
- Consumes: `OperatorFilter` (Task 1).
- Produces: parameter ids `op{n}.operatorFilter.cutoff`, `.fixedHz`, `.q`, `.formantRatio`, `.formantLevel`, `.peakGain`, `.octaves`, and envelope ids `op{n}.operatorFilter.env.p{k}.time|level`, in group `Patch/Operator {n}/Filter`; `ComplexityOptions.PerFilter` (0.05) and `PerFilterEnvelope` (0.03).

- [ ] **Step 1: Write the failing tests**

Add to `tests/HearSay.OperatorMatrix.Tests/ParameterTests.cs` (add usings for `HearSay.OperatorMatrix.Canonicalization`, `HearSay.Synthesis.Patches`, `HearSay.Synthesis.Profiles` if missing):

```csharp
    private static OperatorMatrixPatch FilteredSaw(OperatorFilter filter) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = OperatorWaveform.Saw, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Fact]
    public void FilterParameters_FollowTheFilterType()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        OperatorMatrixPatch lowPass = FilteredSaw(OperatorFilter.LowPass(4, 2));
        var ids = PatchParameterSchema.Build(lowPass, fmx6).ActiveSmooth(lowPass).Select(static p => p.Id).ToHashSet();
        Assert.Contains("op1.operatorFilter.cutoff", ids);
        Assert.Contains("op1.operatorFilter.q", ids);
        Assert.DoesNotContain("op1.operatorFilter.fixedHz", ids);
        Assert.DoesNotContain("op1.operatorFilter.octaves", ids);

        var sweep = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1);
        OperatorMatrixPatch formant = FilteredSaw(OperatorFilter.FormantAt(700, 1.8, 0.5, 8) with { EnvelopeOctaves = 1, Envelope = sweep });
        PatchParameterSchema schema = PatchParameterSchema.Build(formant, fmx6);
        ids = schema.ActiveSmooth(formant).Select(static p => p.Id).ToHashSet();
        Assert.Contains("op1.operatorFilter.fixedHz", ids);
        Assert.Contains("op1.operatorFilter.formantRatio", ids);
        Assert.Contains("op1.operatorFilter.formantLevel", ids);
        Assert.Contains("op1.operatorFilter.octaves", ids);
        Assert.Contains("op1.operatorFilter.env.p1.time", ids);
        Assert.DoesNotContain("op1.operatorFilter.cutoff", ids);

        // Writing through a parameter changes only the filter and stays in range.
        PatchParameter q = schema.Find("op1.operatorFilter.q")!;
        OperatorMatrixPatch changed = q.Set(formant, 50.0);
        Assert.Equal(20.0, changed.Operators[0].Filter!.Q);
        Assert.Empty(changed.Validate());

        // Profiles without filters have no filter parameters at all.
        Assert.DoesNotContain(PatchParameterSchema.Build(lowPass, SynthesisProfiles.SuperColliderFm7.Capabilities).Parameters, static p => p.Id.Contains("operatorFilter", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_CostsALittleComplexity()
    {
        OperatorMatrixPatch plain = FilteredSaw(OperatorFilter.LowPass(4));
        plain = plain.WithOperator(0, plain.Operators[0] with { Filter = null });
        double none = PatchComplexity.Measure(plain);
        double filter = PatchComplexity.Measure(FilteredSaw(OperatorFilter.LowPass(4)));
        double sweep = PatchComplexity.Measure(FilteredSaw(OperatorFilter.LowPass(4) with { EnvelopeOctaves = 1, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) }));
        Assert.Equal(none + 0.05, filter, 10);
        Assert.Equal(filter + 0.03, sweep, 10);
    }
```

Check the setter name on `PatchParameter` before running (`Set` or `Apply`; use whichever the class defines for writing a native value into a patch).

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FilterParameters|Filter_Costs"`
Expected: both fail (no such parameters; complexity unchanged).

- [ ] **Step 3: Parameters**

In `PatchParameterSchema.Build`, in the operator loop, after the `SupportsSharedNoise` block:

```csharp
            if (capabilities.SupportsOperatorFilters)
            {
                // The filter of an operator, once a mutation or a prior has given it one: only the values its type uses.
                string filterGroup = group + "/Filter";
                string filterId = id + ".operatorFilter";
                OperatorMatrixPatch WithFilter(OperatorMatrixPatch p, Func<OperatorFilter, OperatorFilter> change) =>
                    p.Operators[op].Filter is { } f ? p.WithOperator(op, p.Operators[op] with { Filter = change(f) }) : p;
                bool Has(OperatorMatrixPatch p, Func<OperatorFilter, bool> uses) => Audible(p) && p.Operators[op].Filter is { } f && uses(f);
                double Read(OperatorMatrixPatch p, Func<OperatorFilter, double> value, double fallback) => p.Operators[op].Filter is { } f ? value(f) : fallback;

                list.Add(Smooth(filterId + ".cutoff", filterGroup, 0.25, 64.0, ParameterTransforms.Logarithmic, p => Read(p, static f => f.CutoffRatio, 4.0), (p, v) => WithFilter(p, f => f with { CutoffRatio = Math.Clamp(v, 0.25, 64.0) }), p => Has(p, static f => f.IsKeyTracked)));
                list.Add(Smooth(filterId + ".fixedHz", filterGroup, 80.0, 8000.0, ParameterTransforms.Logarithmic, p => Read(p, static f => f.FixedHz, 1000.0), (p, v) => WithFilter(p, f => f with { FixedHz = Math.Clamp(v, 80.0, 8000.0) }), p => Has(p, static f => !f.IsKeyTracked)));
                list.Add(Smooth(filterId + ".q", filterGroup, OperatorFilter.MinQ, OperatorFilter.MaxQ, ParameterTransforms.Logarithmic, p => Read(p, static f => f.Q, OperatorFilter.ButterworthQ), (p, v) => WithFilter(p, f => f with { Q = Math.Clamp(v, OperatorFilter.MinQ, OperatorFilter.MaxQ) }), p => Has(p, static _ => true)));
                list.Add(Smooth(filterId + ".formantRatio", filterGroup, 1.0, 8.0, ParameterTransforms.Logarithmic, p => Read(p, static f => f.FormantRatio, 2.0), (p, v) => WithFilter(p, f => f with { FormantRatio = Math.Clamp(v, 1.0, 8.0) }), p => Has(p, static f => f.Type == OperatorFilterType.Formant)));
                list.Add(Smooth(filterId + ".formantLevel", filterGroup, 0.0, 1.0, ParameterTransforms.Linear, p => Read(p, static f => f.FormantLevel, 0.5), (p, v) => WithFilter(p, f => f with { FormantLevel = Math.Clamp(v, 0.0, 1.0) }), p => Has(p, static f => f.Type == OperatorFilterType.Formant)));
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
```

- [ ] **Step 4: Complexity**

In `OperatorMatrixRegimeKey.cs`, add to `ComplexityOptions`:

```csharp
    /// <summary>Cost of an operator filter.</summary>
    public double PerFilter { get; init; } = 0.05;

    /// <summary>Extra cost of a filter envelope.</summary>
    public double PerFilterEnvelope { get; init; } = 0.03;
```

and in `PatchComplexity.Measure`, in the audible-operator loop after the `ExtremeDetune` line:

```csharp
            if (op.Filter is { } filter)
            {
                total += options.PerFilter + (filter.Envelope is null ? 0.0 : options.PerFilterEnvelope);
            }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.OperatorMatrix tests/HearSay.OperatorMatrix.Tests/ParameterTests.cs
git commit -m "Search: filter parameters per type, and their cost in the complexity

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Filter priors and the formant and subtractive providers

**Files:**
- Create: `src/HearSay.OperatorMatrix/Priors/FilterPriors.cs`
- Create: `src/HearSay.OperatorMatrix/Priors/FormantPriorProvider.cs`
- Create: `src/HearSay.OperatorMatrix/Priors/SubtractivePriorProvider.cs`
- Modify: `src/HearSay/OperatorMatrixPatchMatcher.cs` (`ReadingProvidersFor`)
- Test: `tests/HearSay.OperatorMatrix.Tests/FilterPriorTests.cs` (new)

**Interfaces:**
- Consumes: `OperatorFilter` (Task 1), the FMX6 renderer (Task 4) in tests, `TargetAnalysis`, `TemporalPriors.OutputEnvelope`.
- Produces:
  - `sealed record SpectralPeak(double Hz, double LevelDb, double Q)`;
  - `sealed record LowPassPrior(double CutoffRatio, int Poles)`;
  - `sealed record SweepPrior(double Octaves, OperatorEnvelope Envelope, bool Rising)`;
  - `static class FilterPriors` with `IReadOnlyList<SpectralPeak> Peaks(TargetAnalysis analysis, int max)`, `OperatorFilter? Formant(TargetAnalysis analysis)`, `LowPassPrior? LowPass(TargetAnalysis analysis)`, `double? HighPassRatio(TargetAnalysis analysis)`, `SweepPrior? Sweep(TargetAnalysis analysis)`;
  - `FormantPriorProvider` (`Name` "formants") and `SubtractivePriorProvider` (`Name` "subtractive"), both `ISynthesisPriorProvider`.

- [ ] **Step 1: Write the failing tests**

`tests/HearSay.OperatorMatrix.Tests/FilterPriorTests.cs`:

```csharp
using HearSay.Analysis;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Tests;

public sealed class FilterPriorTests
{
    private static readonly SynthesisCapabilities Fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
    private static readonly Fmx6CompatibleOperatorMatrixRenderer Renderer = new();

    private static TargetAnalysis Analyze(OperatorMatrixPatch patch, double hz)
    {
        RenderedAudio audio = Renderer.Render(patch, RenderRequest.Create(hz, 1.0, 0.3));
        return new TargetAnalyzer().Analyze(new AudioTarget(audio.Samples, audio.SampleRate, hz));
    }

    private static OperatorMatrixPatch Saw(OperatorFilter? filter, OperatorEnvelope? envelope = null) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), envelope ?? OperatorEnvelope.Adsr(0.01, 0.1, 0.8, 0.2)) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 128, Filter = filter })
            .WithOutputLevel(0, 0.8);

    [Theory]
    [InlineData(110.0, 700.0, 1.7)]
    [InlineData(220.0, 400.0, 5.0)]
    public void Formant_IsRecoveredWithinHalfTheFundamental(double f0, double f1, double ratio)
    {
        TargetAnalysis analysis = Analyze(Saw(OperatorFilter.FormantAt(f1, ratio, 0.6, 8)), f0);
        OperatorFilter? found = FilterPriors.Formant(analysis);
        Assert.NotNull(found);
        Assert.InRange(found!.FixedHz, f1 - (f0 / 2), f1 + (f0 / 2));
        Assert.InRange(found.FixedHz * found.FormantRatio, (f1 * ratio) - (f0 / 2), (f1 * ratio) + (f0 / 2));
    }

    [Fact]
    public void LowPass_FindsTheCutoff_AndTheSlope()
    {
        LowPassPrior? two = FilterPriors.LowPass(Analyze(Saw(OperatorFilter.LowPass(6)), 110));
        LowPassPrior? four = FilterPriors.LowPass(Analyze(Saw(OperatorFilter.LowPass(6, poles: 4)), 110));
        Assert.NotNull(two);
        Assert.InRange(two!.CutoffRatio, 3.0, 12.0);
        Assert.Equal(2, two.Poles);
        Assert.Equal(4, four!.Poles);
        Assert.Null(FilterPriors.LowPass(Analyze(Saw(null), 110)));
    }

    [Fact]
    public void Sweep_IsSeenOnlyWhenTheBrightnessMoves()
    {
        var closing = new OperatorEnvelope([new EnvelopePoint(0, 1), new EnvelopePoint(0.3, 0), new EnvelopePoint(0.1, 0)], SustainPoint: 1, ReleasePoint: 2);
        SweepPrior? sweep = FilterPriors.Sweep(Analyze(Saw(OperatorFilter.LowPass(2, poles: 4) with { EnvelopeOctaves = 3, Envelope = closing }), 110));
        Assert.NotNull(sweep);
        Assert.False(sweep!.Rising);
        Assert.InRange(sweep.Octaves, 1.0, 4.0);
        Assert.Null(FilterPriors.Sweep(Analyze(Saw(OperatorFilter.LowPass(4)), 110)));
    }

    [Fact]
    public void Providers_ProposeValidFilteredCarriers_OnlyForProfilesWithFilters()
    {
        TargetAnalysis vowel = Analyze(Saw(OperatorFilter.FormantAt(700, 1.7, 0.6, 8)), 110);
        IReadOnlyList<PatchHypothesis> formants = new FormantPriorProvider().Generate(vowel, Fmx6);
        Assert.NotEmpty(formants);
        Assert.All(formants, h => Assert.True(PatchCompatibilityValidator.Instance.Validate(h.Patch, Fmx6).IsCompatible));
        Assert.Equal(OperatorFilterType.Formant, formants[0].Patch.Operators[0].Filter!.Type);

        TargetAnalysis dark = Analyze(Saw(OperatorFilter.LowPass(5, poles: 4)), 110);
        IReadOnlyList<PatchHypothesis> subtractive = new SubtractivePriorProvider().Generate(dark, Fmx6);
        Assert.NotEmpty(subtractive);
        Assert.All(subtractive, h => Assert.Equal(OperatorFilterType.LowPass, h.Patch.Operators[0].Filter!.Type));

        Assert.Empty(new FormantPriorProvider().Generate(vowel, SynthesisProfiles.SuperColliderFm7.Capabilities));
        Assert.Empty(new SubtractivePriorProvider().Generate(dark, SynthesisProfiles.SuperColliderNome6.Capabilities));
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter FilterPriorTests`
Expected: build error (types do not exist).

- [ ] **Step 3: `FilterPriors`**

`src/HearSay.OperatorMatrix/Priors/FilterPriors.cs`:

```csharp
using HearSay.Analysis;
using HearSay.Partials;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>A maximum of the spectral envelope of a target, relative to a sawtooth source.</summary>
/// <param name="Hz">Frequency.</param>
/// <param name="LevelDb">Level of the smoothed envelope there.</param>
/// <param name="Q">Centre over the width at 3 dB below the peak, 0.5..20.</param>
public sealed record SpectralPeak(double Hz, double LevelDb, double Q);

/// <summary>A low-pass reading of a target.</summary>
/// <param name="CutoffRatio">Cutoff as a multiple of the fundamental.</param>
/// <param name="Poles">2, or 4 when the spectrum falls 18 dB per octave or more above the cutoff.</param>
public sealed record LowPassPrior(double CutoffRatio, int Poles);

/// <summary>A filter sweep read from the brightness of a target.</summary>
/// <param name="Octaves">Depth.</param>
/// <param name="Envelope">Levels 0..1.</param>
/// <param name="Rising">True when the brightness rises and stays (an opening filter), false when it starts bright and closes.</param>
public sealed record SweepPrior(double Octaves, OperatorEnvelope Envelope, bool Rising);

/// <summary>
/// Starting values for operator filters, read from a target analysis. The harmonic partials are compared with a
/// sawtooth (amplitudes falling as 1/n): what is left is the filter a rich carrier would need. Formants are the
/// maxima of a smooth envelope through the harmonic levels (not LPC, which pulls its poles onto the harmonics when the
/// fundamental is high), so their accuracy is about half the fundamental; the search refines them.
/// </summary>
public static class FilterPriors
{
    private const double SmoothingOctaves = 1.0 / 6.0;
    private const double GridOctaves = 1.0 / 24.0;

    /// <summary>The strongest maxima of the spectral envelope, strongest first.</summary>
    /// <param name="analysis">Target analysis.</param>
    /// <param name="max">Largest number of peaks.</param>
    public static IReadOnlyList<SpectralPeak> Peaks(TargetAnalysis analysis, int max)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<(double Octave, double Db)> residual = Residual(analysis);
        var peaks = new List<SpectralPeak>();
        if (residual.Count < 4)
        {
            return peaks;
        }

        double from = residual[0].Octave, to = residual[^1].Octave;
        int count = (int)((to - from) / GridOctaves) + 1;
        var curve = new double[count];
        for (int g = 0; g < count; g++)
        {
            double x = from + (g * GridOctaves), weighted = 0, total = 0;
            foreach ((double octave, double db) in residual)
            {
                double w = Math.Exp(-0.5 * Math.Pow((x - octave) / SmoothingOctaves, 2));
                weighted += w * db;
                total += w;
            }

            curve[g] = weighted / total;
        }

        for (int g = 1; g < count - 1; g++)
        {
            if (curve[g] < curve[g - 1] || curve[g] < curve[g + 1])
            {
                continue;
            }

            // Prominence: how far the curve falls on both sides before it rises above this peak again.
            int left = g, right = g;
            double leftMin = curve[g], rightMin = curve[g];
            while (left > 0 && curve[left - 1] <= curve[g])
            {
                left--;
                leftMin = Math.Min(leftMin, curve[left]);
            }

            while (right < count - 1 && curve[right + 1] <= curve[g])
            {
                right++;
                rightMin = Math.Min(rightMin, curve[right]);
            }

            if (curve[g] - Math.Max(leftMin, rightMin) < 3.0)
            {
                continue;
            }

            int lo = g, hi = g;
            while (lo > 0 && curve[lo] > curve[g] - 3.0)
            {
                lo--;
            }

            while (hi < count - 1 && curve[hi] > curve[g] - 3.0)
            {
                hi++;
            }

            double hz = Math.Pow(2.0, from + (g * GridOctaves));
            double width = Math.Pow(2.0, from + (hi * GridOctaves)) - Math.Pow(2.0, from + (lo * GridOctaves));
            peaks.Add(new SpectralPeak(hz, curve[g], Math.Clamp(hz / Math.Max(width, 1.0), OperatorFilter.MinQ, OperatorFilter.MaxQ)));
        }

        return [.. peaks.OrderByDescending(static p => p.LevelDb).Take(max)];
    }

    /// <summary>A formant filter from the two strongest peaks, or one peak when there is only one; null without peaks.</summary>
    /// <param name="analysis">Target analysis.</param>
    public static OperatorFilter? Formant(TargetAnalysis analysis)
    {
        IReadOnlyList<SpectralPeak> peaks = Peaks(analysis, 2);
        if (peaks.Count == 0)
        {
            return null;
        }

        if (peaks.Count == 1)
        {
            return OperatorFilter.FormantAt(Math.Clamp(peaks[0].Hz, 80.0, 8000.0), 2.0, 0.0, Math.Clamp(peaks[0].Q, 2.0, OperatorFilter.MaxQ));
        }

        SpectralPeak f1 = peaks[0].Hz < peaks[1].Hz ? peaks[0] : peaks[1];
        SpectralPeak f2 = ReferenceEquals(f1, peaks[0]) ? peaks[1] : peaks[0];
        double level = Math.Clamp(Math.Pow(10.0, (f2.LevelDb - f1.LevelDb) / 20.0), 0.0, 1.0);
        double q = Math.Clamp((f1.Q + f2.Q) / 2.0, 2.0, OperatorFilter.MaxQ);
        return OperatorFilter.FormantAt(Math.Clamp(f1.Hz, 80.0, 8000.0), Math.Clamp(f2.Hz / f1.Hz, 1.05, 8.0), level, q);
    }

    /// <summary>
    /// Where the harmonics fall 6 dB below their level at the bottom of the spectrum, relative to a sawtooth; null when
    /// they do not.
    /// </summary>
    /// <param name="analysis">Target analysis.</param>
    public static LowPassPrior? LowPass(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<(int N, double Db)> harmonics = Harmonics(analysis);
        if (harmonics.Count < 6)
        {
            return null;
        }

        double reference = harmonics.Where(static h => h.N <= 3).Select(static h => h.Db).DefaultIfEmpty(harmonics[0].Db).Average();
        for (int i = 0; i < harmonics.Count - 1; i++)
        {
            if (harmonics[i].N <= 3 || harmonics[i].Db >= reference - 6.0 || harmonics[i + 1].Db >= reference - 6.0)
            {
                continue;
            }

            int cutoff = harmonics[i].N;
            (int N, double Db)? octaveUp = harmonics.Where(h => h.N >= 2 * cutoff).Select(static h => ((int, double)?)h).FirstOrDefault();
            double slope = octaveUp is { } up ? (harmonics[i].Db - up.Db) / Math.Log2((double)up.N / cutoff) : 12.0;
            return new LowPassPrior(Math.Clamp(cutoff, 0.25, 64.0), slope >= 18.0 ? 4 : 2);
        }

        return null;
    }

    /// <summary>A high-pass cutoff ratio when the fundamental is 9 dB or more below the strongest of harmonics 2..4; null otherwise.</summary>
    /// <param name="analysis">Target analysis.</param>
    public static double? HighPassRatio(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<(int N, double Db)> harmonics = Harmonics(analysis);
        (int N, double Db)? first = harmonics.Where(static h => h.N == 1).Select(static h => ((int, double)?)h).FirstOrDefault();
        (int N, double Db)? strongest = harmonics.Where(static h => h.N is >= 2 and <= 4).OrderByDescending(static h => h.Db).Select(static h => ((int, double)?)h).FirstOrDefault();
        if (strongest is not { } s || (first is { } f && f.Db > s.Db - 9.0))
        {
            return null;
        }

        return Math.Clamp(0.7 * s.N, 0.25, 64.0);
    }

    /// <summary>A filter sweep when the spectral centroid moves by half an octave or more during the note; null otherwise.</summary>
    /// <param name="analysis">Target analysis.</param>
    public static SweepPrior? Sweep(TargetAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var trajectories = analysis.Trajectories;
        double frame = trajectories.FrameSeconds;
        int end = Math.Min(trajectories.CentroidHz.Length, (int)(analysis.NoteOffSeconds / frame));
        var frames = Enumerable.Range(0, Math.Max(0, end)).Where(i => trajectories.Active[i] && trajectories.CentroidHz[i] > 0).ToList();
        if (frames.Count < 8)
        {
            return null;
        }

        double Median(IEnumerable<int> indices)
        {
            double[] values = [.. indices.Select(i => (double)trajectories.CentroidHz[i]).Order()];
            return values[values.Length / 2];
        }

        int early = Math.Max(1, frames.Count * 15 / 100);
        double start = Median(frames.Take(early));
        double late = Median(frames.Skip(frames.Count / 2));
        int peakFrame = frames.Take(Math.Max(1, frames.Count * 40 / 100)).MaxBy(i => trajectories.CentroidHz[i]);
        double peak = trajectories.CentroidHz[peakFrame];

        if (peak / late >= Math.Sqrt(2.0))
        {
            // Bright first, then closing to the sustained brightness.
            double octaves = Math.Clamp(Math.Log2(peak / late), 0.25, 4.0);
            double attack = Math.Max(0.002, peakFrame * frame);
            double decay = Math.Max(0.05, ((frames[frames.Count / 2] - peakFrame) * frame) / 2.0);
            return new SweepPrior(octaves, Envelope(attack, decay, 0.0), Rising: false);
        }

        if (late / start >= Math.Sqrt(2.0))
        {
            // Dark first, opening to the sustained brightness.
            double octaves = Math.Clamp(Math.Log2(late / start), 0.25, 4.0);
            int open = frames.First(i => trajectories.CentroidHz[i] >= 0.9 * late);
            return new SweepPrior(octaves, Envelope(Math.Max(0.002, open * frame), 0.01, 1.0), Rising: true);
        }

        return null;
    }

    private static OperatorEnvelope Envelope(double attack, double decay, double sustain) =>
        new([new EnvelopePoint(0, 0), new EnvelopePoint(attack, 1), new EnvelopePoint(decay, sustain), new EnvelopePoint(0.1, sustain)], SustainPoint: 2, ReleasePoint: 3);

    /// <summary>Harmonic number and level in dB relative to a sawtooth, lowest first.</summary>
    private static List<(int N, double Db)> Harmonics(TargetAnalysis analysis)
    {
        var harmonics = new Dictionary<int, double>();
        foreach (SpectralPartial partial in analysis.Partials)
        {
            int n = (int)Math.Round(partial.RelativeFrequency);
            if (n >= 1 && Math.Abs(partial.RelativeFrequency - n) <= 0.06)
            {
                double db = partial.LevelDb + (20.0 * Math.Log10(n));
                harmonics[n] = harmonics.TryGetValue(n, out double old) ? Math.Max(old, db) : db;
            }
        }

        return [.. harmonics.OrderBy(static h => h.Key).Select(static h => (h.Key, h.Value))];
    }

    private static List<(double Octave, double Db)> Residual(TargetAnalysis analysis) =>
        [.. Harmonics(analysis).Select(h => (Math.Log2(h.N * analysis.FundamentalHz), h.Db))];
}
```

- [ ] **Step 4: The providers**

`src/HearSay.OperatorMatrix/Priors/FormantPriorProvider.cs`:

```csharp
using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses for voice-like targets on profiles with operator filters: a rich carrier (saw, or a pulse) through a
/// formant filter whose peaks are read from the spectral envelope (<see cref="FilterPriors"/>), with two peaks, with
/// the strongest alone, and with the first and the third when there are three.
/// </summary>
public sealed class FormantPriorProvider : ISynthesisPriorProvider
{
    /// <inheritdoc />
    public string Name => "formants";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var hypotheses = new List<PatchHypothesis>();
        if (!capabilities.SupportsOperatorFilters || !capabilities.SupportsRatioFrequency || capabilities.OperatorCount < 1)
        {
            return hypotheses;
        }

        IReadOnlyList<SpectralPeak> peaks = FilterPriors.Peaks(analysis, 3);
        if (peaks.Count == 0)
        {
            return hypotheses;
        }

        var filters = new List<(OperatorFilter Filter, string Name)>();
        if (FilterPriors.Formant(analysis) is { } two)
        {
            filters.Add((two, FormattableString.Invariant($"formant {two.FixedHz:0} Hz / {two.FixedHz * two.FormantRatio:0} Hz")));
        }

        SpectralPeak strongest = peaks[0];
        filters.Add((OperatorFilter.FormantAt(Math.Clamp(strongest.Hz, 80.0, 8000.0), 2.0, 0.0, Math.Clamp(strongest.Q, 2.0, OperatorFilter.MaxQ)), FormattableString.Invariant($"one formant {strongest.Hz:0} Hz")));
        SpectralPeak[] byFrequency = [.. peaks.OrderBy(static p => p.Hz)];
        if (byFrequency.Length == 3)
        {
            double level = Math.Clamp(Math.Pow(10.0, (byFrequency[2].LevelDb - byFrequency[0].LevelDb) / 20.0), 0.0, 1.0);
            double q = Math.Clamp((byFrequency[0].Q + byFrequency[2].Q) / 2.0, 2.0, OperatorFilter.MaxQ);
            filters.Add((OperatorFilter.FormantAt(Math.Clamp(byFrequency[0].Hz, 80.0, 8000.0), Math.Clamp(byFrequency[2].Hz / byFrequency[0].Hz, 1.05, 8.0), level, q), "formants F1 + F3"));
        }

        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        int rank = 0;
        foreach ((OperatorFilter filter, string name) in filters)
        {
            foreach ((OperatorWaveform waveform, double shape) in new[] { (OperatorWaveform.Saw, 0.5), (OperatorWaveform.Pulse, 0.2) })
            {
                if (!capabilities.SupportedWaveforms.Contains(waveform))
                {
                    continue;
                }

                OperatorPatch carrier = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output) with { Waveform = waveform, Shape = shape, HarmonicLimit = 64, Filter = filter };
                OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(capabilities.OperatorCount).WithOperator(0, carrier).WithOutputLevel(0, 0.8);
                hypotheses.Add(new PatchHypothesis(patch, 0.55 - (0.01 * rank++), $"{waveform.ToString().ToLowerInvariant()} with {name}"));
            }
        }

        return hypotheses;
    }
}
```

`src/HearSay.OperatorMatrix/Priors/SubtractivePriorProvider.cs`:

```csharp
using HearSay.Analysis;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Hypotheses for subtractive targets on profiles with operator filters: a saw or square carrier through a low-pass
/// whose cutoff and slope are read from the harmonics, with the filter sweep of the brightness when it moves.
/// </summary>
public sealed class SubtractivePriorProvider : ISynthesisPriorProvider
{
    /// <inheritdoc />
    public string Name => "subtractive";

    /// <inheritdoc />
    public IReadOnlyList<PatchHypothesis> Generate(TargetAnalysis analysis, SynthesisCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(capabilities);
        var hypotheses = new List<PatchHypothesis>();
        if (!capabilities.SupportsOperatorFilters || !capabilities.SupportsRatioFrequency || capabilities.OperatorCount < 1 || FilterPriors.LowPass(analysis) is not { } lowPass)
        {
            return hypotheses;
        }

        SweepPrior? sweep = FilterPriors.Sweep(analysis);
        OperatorEnvelope output = TemporalPriors.OutputEnvelope(analysis);
        int rank = 0;
        foreach (OperatorWaveform waveform in new[] { OperatorWaveform.Saw, OperatorWaveform.Square })
        {
            if (!capabilities.SupportedWaveforms.Contains(waveform))
            {
                continue;
            }

            foreach (double q in new[] { OperatorFilter.ButterworthQ, 2.0 })
            {
                // An opening sweep sustains at its top, so its base is the measured cutoff an octave count lower.
                double cutoff = sweep is { Rising: true } ? lowPass.CutoffRatio / Math.Pow(2.0, sweep.Octaves) : lowPass.CutoffRatio;
                OperatorFilter filter = OperatorFilter.LowPass(Math.Clamp(cutoff, 0.25, 64.0), q, lowPass.Poles);
                if (sweep is not null)
                {
                    filter = filter with { EnvelopeOctaves = sweep.Octaves, Envelope = sweep.Envelope };
                }

                OperatorPatch carrier = OperatorPatch.Sine(OperatorFrequency.AtRatio(1), output) with { Waveform = waveform, HarmonicLimit = 64, Filter = filter };
                OperatorMatrixPatch patch = OperatorMatrixPatch.Silent(capabilities.OperatorCount).WithOperator(0, carrier).WithOutputLevel(0, 0.8);
                hypotheses.Add(new PatchHypothesis(patch, 0.5 - (0.01 * rank++), FormattableString.Invariant($"{waveform.ToString().ToLowerInvariant()} through a {lowPass.Poles}-pole low-pass at {filter.CutoffRatio:0.##}x, Q {q:0.##}{(sweep is null ? string.Empty : " with a sweep")}")));
            }
        }

        return hypotheses;
    }
}
```

- [ ] **Step 5: Wire the providers**

In `src/HearSay/OperatorMatrixPatchMatcher.cs`, `ReadingProvidersFor`, replace the `return` statement with:

```csharp
        IReadOnlyList<ISynthesisPriorProvider> reading = profile.Capabilities.SupportedWaveforms.Count > 1
            ? [sine, stacks, tracks, new WaveformVariantPriorProvider(sine, includeInner: false), new HarmonicWaveformPriorProvider(), new ModulatorWaveformPriorProvider(sine), new NoisePriorProvider(sine), new NoisePriorProvider(tracks)]
            : [sine, stacks, tracks];

        // Filtered carriers read from the spectral envelope, each kind in a group of its own.
        return profile.Capabilities.SupportsOperatorFilters
            ? [.. reading, new FormantPriorProvider(), new SubtractivePriorProvider()]
            : reading;
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter FilterPriorTests`
Expected: all pass. If `Formant_IsRecoveredWithinHalfTheFundamental` misses, print `FilterPriors.Peaks(analysis, 3)` and the residual in the test output and tune `SmoothingOctaves` (between 1/8 and 1/4 octave) rather than the tolerance; report the value chosen.

- [ ] **Step 7: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors src/HearSay/OperatorMatrixPatchMatcher.cs tests/HearSay.OperatorMatrix.Tests/FilterPriorTests.cs
git commit -m "Search: formant and subtractive priors read from the spectral envelope

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Filter mutations

**Files:**
- Modify: `src/HearSay.OperatorMatrix/Search/TopologyMutator.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/SearchComponentTests.cs`

**Interfaces:**
- Consumes: `FilterPriors` (Task 8), `OperatorFilter` (Task 1).
- Produces: screened variants whose descriptions contain `" filter "` (for example `OP1 filter low-pass`, `OP2 filter removed`, `OP1 filter sweep`), screened in a turn of their own.

- [ ] **Step 1: Write the failing test**

Add to `SearchComponentTests.cs` (reuse the file's existing helpers for an evaluator; if it builds a `PatchEvaluator` for FM7, build one the same way with the FMX6 renderer and capabilities):

```csharp
    [Fact]
    public void ScreenedMutations_IncludeFilters_ForProfilesWithFilters()
    {
        SynthesisCapabilities fmx6 = SynthesisProfiles.SuperColliderFmx6.Capabilities;
        var renderer = new HearSay.Synthesis.Fmx6Compatible.Rendering.Fmx6CompatibleOperatorMatrixRenderer();
        OperatorMatrixPatch hidden = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.01, 0.1, 0.8, 0.2)) with { Waveform = OperatorWaveform.Saw, HarmonicLimit = 64, Filter = OperatorFilter.LowPass(4, poles: 4) })
            .WithOutputLevel(0, 0.8);
        RenderedAudio audio = renderer.Render(hidden, RenderRequest.Create(110, 1.0, 0.3));
        TargetAnalysis analysis = new TargetAnalyzer().Analyze(new AudioTarget(audio.Samples, audio.SampleRate, 110));
        OperatorMatrixPatch unfiltered = hidden.WithOperator(0, hidden.Operators[0] with { Filter = null });

        PatchEvaluator evaluator = EvaluatorFor(analysis, renderer, fmx6, budget: 2000);
        IReadOnlyList<TopologyMutation> mutations = new TopologyMutator(fmx6).ProposeScreened(unfiltered, analysis, evaluator, new Random(1), 6);
        Assert.Contains(mutations, static m => m.Description.Contains(" filter ", StringComparison.Ordinal));
        Assert.All(mutations, m => Assert.True(PatchCompatibilityValidator.Instance.Validate(m.Patch, fmx6).IsCompatible));

        // The other way round: a filtered patch is offered its removal and a change of slope.
        PatchEvaluator again = EvaluatorFor(analysis, renderer, fmx6, budget: 2000);
        IReadOnlyList<TopologyMutation> back = new TopologyMutator(fmx6).ProposeScreened(hidden, analysis, again, new Random(1), 12, maxScreened: 400);
        Assert.Contains(back, static m => m.Description == "OP1 filter removed" || m.Description == "OP1 filter 2 poles");

        // FM7 is never offered a filter.
        PatchEvaluator fm7 = EvaluatorFor(analysis, new HearSay.Synthesis.Fm7Compatible.Rendering.Fm7CompatibleOperatorMatrixRenderer(), SynthesisProfiles.SuperColliderFm7.Capabilities, budget: 2000);
        OperatorMatrixPatch sine = OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1))).WithOutputLevel(0, 0.8);
        Assert.DoesNotContain(new TopologyMutator(SynthesisProfiles.SuperColliderFm7.Capabilities).ProposeScreened(sine, analysis, fm7, new Random(1), 6), static m => m.Description.Contains(" filter ", StringComparison.Ordinal));
    }
```

If `SearchComponentTests` has no `EvaluatorFor` helper, add one next to the test, built exactly the way the file's other tests construct a `PatchEvaluator` (same objective and options), parameterized by analysis, renderer, capabilities and budget.

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter ScreenedMutations_IncludeFilters`
Expected: FAIL, no mutation mentions a filter.

- [ ] **Step 3: Filter variants**

In `TopologyMutator.ProposeScreened`, right after the noise-wash block (`if (free >= 0) { candidates.AddRange(NoiseWashes(...)); }`):

```csharp
        // Filters, where the profile has them, before the many operator variants so that the cap cannot cut them off.
        if (_capabilities.SupportsOperatorFilters)
        {
            candidates.AddRange(FilterVariants(patch, analysis, used));
        }
```

In the turn assignment, replace the ternary chain with:

```csharp
                (description.StartsWith("add edge", StringComparison.Ordinal) || description.Contains(" fixed at ", StringComparison.Ordinal) ? edgesFirst
                    : description.Contains(" filter ", StringComparison.Ordinal) ? filtersFirst
                    : description.Contains(" noise", StringComparison.Ordinal) ? noiseFirst
                    : description.Contains(" waveform ", StringComparison.Ordinal) ? waveformsFirst
                    : operatorsFirst).Add(index);
```

declare `var filtersFirst = new List<int>();` with the other lists, and change the turns to `new[] { noiseFirst, waveformsFirst, filtersFirst, operatorsFirst }`.

Add the method:

```csharp
    /// <summary>
    /// Filter variants of the heard operators, with starting values from the target: a low-pass at its roll-off, a
    /// high-pass under a weak fundamental, a band-pass and a peak on the strongest maximum of its spectral envelope, a
    /// formant filter on its two strongest; and for a filtered operator its removal, the other slope, a band-pass
    /// instead of a low-pass and back, and the sweep of the target's brightness.
    /// </summary>
    private static List<TopologyMutation> FilterVariants(OperatorMatrixPatch patch, TargetAnalysis analysis, List<int> used)
    {
        var variants = new List<TopologyMutation>();
        LowPassPrior? lowPass = FilterPriors.LowPass(analysis);
        double? highPass = FilterPriors.HighPassRatio(analysis);
        IReadOnlyList<SpectralPeak> peaks = FilterPriors.Peaks(analysis, 1);
        OperatorFilter? formant = FilterPriors.Formant(analysis);
        SweepPrior? sweep = FilterPriors.Sweep(analysis);
        foreach (int op in used)
        {
            OperatorPatch current = patch.Operators[op];
            OperatorMatrixPatch With(OperatorFilter? filter) => patch.WithOperator(op, current with { Filter = filter });
            string name = $"OP{op + 1} filter";
            if (current.Filter is not { } filter)
            {
                variants.Add(new TopologyMutation(With(OperatorFilter.LowPass(lowPass?.CutoffRatio ?? 4.0, OperatorFilter.ButterworthQ, lowPass?.Poles ?? 2)), name + " low-pass"));
                if (highPass is double ratio)
                {
                    variants.Add(new TopologyMutation(With(OperatorFilter.HighPass(ratio)), name + " high-pass"));
                }

                if (peaks.Count > 0)
                {
                    double centre = Math.Clamp(peaks[0].Hz / analysis.FundamentalHz, 0.25, 64.0);
                    variants.Add(new TopologyMutation(With(OperatorFilter.BandPass(centre, peaks[0].Q)), name + " band-pass"));
                    variants.Add(new TopologyMutation(With(OperatorFilter.PeakAt(Math.Clamp(peaks[0].Hz, 80.0, 8000.0), 9.0, peaks[0].Q)), name + " peak"));
                }

                if (formant is not null)
                {
                    variants.Add(new TopologyMutation(With(formant), name + " formant"));
                }

                continue;
            }

            variants.Add(new TopologyMutation(With(null), name + " removed"));
            if (filter.Type is OperatorFilterType.LowPass or OperatorFilterType.HighPass)
            {
                int poles = filter.Poles == 4 ? 2 : 4;
                variants.Add(new TopologyMutation(With(filter with { Poles = poles }), $"{name} {poles} poles"));
            }

            if (filter.Type == OperatorFilterType.LowPass)
            {
                variants.Add(new TopologyMutation(With(filter with { Type = OperatorFilterType.BandPass, Poles = 2 }), name + " band-pass"));
            }
            else if (filter.Type == OperatorFilterType.BandPass)
            {
                variants.Add(new TopologyMutation(With(filter with { Type = OperatorFilterType.LowPass }), name + " low-pass"));
            }

            if (sweep is not null && filter.Envelope is null)
            {
                variants.Add(new TopologyMutation(With(filter with { EnvelopeOctaves = sweep.Octaves, Envelope = sweep.Envelope }), name + " sweep"));
            }
        }

        return variants;
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Search/TopologyMutator.cs tests/HearSay.OperatorMatrix.Tests/SearchComponentTests.cs
git commit -m "Search: screened filter mutations with starting values from the target

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Description, diagram and documentation

**Files:**
- Modify: `src/HearSay/PatchDescription.cs`
- Modify: `src/HearSay.Studio/FmDiagram.cs` (`Describe`)
- Modify: `README.md`, `docs/expert-manual.md`
- Test: the test project that covers `PatchDescription` (find it with `grep -rln "WaveformDetail" tests`; if none, `tests/HearSay.OperatorMatrix.Tests/SearchComponentTests.cs`)

**Interfaces:**
- Consumes: `OperatorFilter` (Task 1).
- Produces: `PatchDescription.FilterDetail(OperatorFilter filter)` (long text) and `PatchDescription.FilterLabel(OperatorFilter filter)` (short label).

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Filters_AreDescribed()
    {
        Assert.Equal("LP4 x4", PatchDescription.FilterLabel(OperatorFilter.LowPass(4, poles: 4)));
        Assert.Equal("formant 700/1260", PatchDescription.FilterLabel(OperatorFilter.FormantAt(700, 1.8, 0.5, 8)));
        Assert.Equal("peak 1500 +12", PatchDescription.FilterLabel(OperatorFilter.PeakAt(1500, 12, 4)));
        Assert.Equal("filter low-pass 4 poles, cutoff 4x, Q 0.71", PatchDescription.FilterDetail(OperatorFilter.LowPass(4, poles: 4)));
        Assert.Equal(
            "filter formant F1 700 Hz, F2 1260 Hz at 0.5, Q 8, sweep 1.5 oct",
            PatchDescription.FilterDetail(OperatorFilter.FormantAt(700, 1.8, 0.5, 8) with { EnvelopeOctaves = 1.5, Envelope = OperatorEnvelope.Adsr(0.01, 0.2, 0.3, 0.1) }));
        OperatorPatch op = OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.BandPass(3, 6) };
        Assert.Contains("filter band-pass, centre 3x, Q 6", PatchDescription.WaveformDetail(op), StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run it to see it fail**

Expected: build error, `FilterLabel` does not exist.

- [ ] **Step 3: Implement**

In `PatchDescription.cs`:

```csharp
    /// <summary>A few characters for an operator filter, as in the block diagram: <c>LP4 x4</c>, <c>formant 700/1260</c>.</summary>
    /// <param name="filter">Filter.</param>
    public static string FilterLabel(OperatorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Type switch
        {
            OperatorFilterType.LowPass => string.Create(CultureInfo.InvariantCulture, $"LP{filter.Poles} x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.HighPass => string.Create(CultureInfo.InvariantCulture, $"HP{filter.Poles} x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.BandPass => string.Create(CultureInfo.InvariantCulture, $"BP x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.Notch => string.Create(CultureInfo.InvariantCulture, $"notch x{filter.CutoffRatio:0.##}"),
            OperatorFilterType.Peak => string.Create(CultureInfo.InvariantCulture, $"peak {filter.FixedHz:0} {(filter.PeakGainDb >= 0 ? "+" : string.Empty)}{filter.PeakGainDb:0.#}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"formant {filter.FixedHz:0}/{filter.FixedHz * filter.FormantRatio:0}"),
        };
    }

    /// <summary>An operator filter in words, with its sweep when it has one.</summary>
    /// <param name="filter">Filter.</param>
    public static string FilterDetail(OperatorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string text = filter.Type switch
        {
            OperatorFilterType.LowPass => string.Create(CultureInfo.InvariantCulture, $"low-pass {filter.Poles} poles, cutoff {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.HighPass => string.Create(CultureInfo.InvariantCulture, $"high-pass {filter.Poles} poles, cutoff {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.BandPass => string.Create(CultureInfo.InvariantCulture, $"band-pass, centre {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.Notch => string.Create(CultureInfo.InvariantCulture, $"notch, centre {filter.CutoffRatio:0.##}x"),
            OperatorFilterType.Peak => string.Create(CultureInfo.InvariantCulture, $"peak {filter.FixedHz:0} Hz {filter.PeakGainDb:+0.#;-0.#;0} dB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"formant F1 {filter.FixedHz:0} Hz, F2 {filter.FixedHz * filter.FormantRatio:0} Hz at {filter.FormantLevel:0.##}"),
        };
        string sweep = filter.Envelope is null || filter.EnvelopeOctaves == 0.0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", sweep {filter.EnvelopeOctaves:0.##} oct");
        return string.Create(CultureInfo.InvariantCulture, $"filter {text}, Q {filter.Q:0.##}{sweep}");
    }
```

and in `WaveformDetail`, add the filter to the joined parts:

```csharp
        string filter = op.Filter is null ? string.Empty : FilterDetail(op.Filter);
        return string.Join("  ", new[] { waveform, phase, m, filter }.Where(static s => s.Length > 0));
```

Update the summary of `WaveformDetail` to mention the operator filter.

In `src/HearSay.Studio/FmDiagram.cs`, `Describe`, before `return rough ? ...`:

```csharp
        if (op.Filter is not null)
        {
            frequency += " " + PatchDescription.FilterLabel(op.Filter);
        }
```

(`PatchDescription` is in namespace `HearSay`; add the using if the file lacks it.)

- [ ] **Step 4: Documentation**

- `README.md`: in the FMX6 target description, add one sentence: "FMX6 operators can have a filter (low-pass, high-pass, band-pass, notch, peak or formant, with an optional sweep); the search proposes and tunes them. Exported patches with filters need the FMX6 plugin with operator filters."
- `docs/expert-manual.md`: a section "Operator filters (FMX6)" listing the types, their parameters with the search ranges from the Global Constraints, the two priors (formant, subtractive), the filter mutations, and that the FMX6 structure library is rebuilt once (version 2).

- [ ] **Step 5: Run the tests and build the Studio**

Run: `dotnet build HearSay.sln -c Release` and `dotnet test tests/HearSay.OperatorMatrix.Tests` (or the project holding the new test)
Expected: build succeeds without new warnings; tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay/PatchDescription.cs src/HearSay.Studio/FmDiagram.cs README.md docs/expert-manual.md tests
git commit -m "Studio and docs: operator filters in the description, the diagram and the manuals

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Evaluation

**Files:**
- Create (scratchpad, not committed): `C:\Scripts\Temp\claude\D--Projects-hearsay\09393e74-00c1-48a1-b400-ea858f745082\scratchpad\filterbench\filterbench.csproj`, `Program.cs`

**Interfaces:**
- Consumes: everything above; `OperatorMatrixPatchMatcher.Match(AudioTarget, PatchMatchOptions)`, `PatchMatchOptions { SynthesisProfile, EvaluationBudget, RandomSeed }`, `SearchSummary.FinalAudioLoss`.

- [ ] **Step 1: The benchmark program**

`filterbench.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="D:\Projects\hearsay\src\HearSay\HearSay.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
// Paired comparison at budget 4000: FMX6 with operator filters against FMX6 without them, on hidden filtered patches
// (should improve) and on unfiltered FMX6 patches (should not get worse).
using System.Globalization;
using HearSay;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Fmx6Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

SynthesisProfile withFilters = SynthesisProfiles.SuperColliderFmx6;
SynthesisProfile withoutFilters = SynthesisProfiles.SuperColliderFmx6 with
{
    Name = "supercollider-fmx6-nofilters",
    Capabilities = SynthesisProfiles.SuperColliderFmx6.Capabilities with { SupportsOperatorFilters = false },
    StructureLibraryVersion = 1,
};
var renderer = new Fmx6CompatibleOperatorMatrixRenderer();
int budget = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 4000;

OperatorMatrixPatch Carrier(OperatorWaveform waveform, OperatorFilter filter) =>
    OperatorMatrixPatch.Silent(6)
        .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Adsr(0.02, 0.2, 0.8, 0.2)) with { Waveform = waveform, HarmonicLimit = 64, Filter = filter })
        .WithOutputLevel(0, 0.8);

var opening = new OperatorEnvelope([new EnvelopePoint(0, 0), new EnvelopePoint(0.12, 1), new EnvelopePoint(0.01, 1), new EnvelopePoint(0.1, 1)], SustainPoint: 2, ReleasePoint: 3);
OperatorMatrixPatch fmBell = ReferencePatches.Find("bell")!.Patch;
var filtered = new List<(string Name, OperatorMatrixPatch Patch, double Hz)>
{
    ("brass", Carrier(OperatorWaveform.Saw, OperatorFilter.LowPass(1.5, 1.2, 4) with { EnvelopeOctaves = 2.5, Envelope = opening }), 146.8),
    ("vowel a", Carrier(OperatorWaveform.Saw, OperatorFilter.FormantAt(730, 1090.0 / 730, 0.5, 8)), 130.8),
    ("vowel i", Carrier(OperatorWaveform.Saw, OperatorFilter.FormantAt(270, 2290.0 / 270, 0.3, 10)), 196.0),
    ("vowel u", Carrier(OperatorWaveform.Saw, OperatorFilter.FormantAt(300, 870.0 / 300, 0.4, 10)), 164.8),
    ("resonant fm", fmBell.WithOperator(0, fmBell.Operators[0] with { Filter = OperatorFilter.PeakAt(1800, 12, 4) }), 220.0),
};

var generator = new RandomPatchGenerator(SynthesisProfiles.SuperColliderFmx6.Capabilities, new RandomPatchOptions { EdgeDensity = 0.3, AllowCircularFeedback = false });
var plain = new List<(string Name, OperatorMatrixPatch Patch, double Hz)>();
for (int seed = 0; seed < 6; seed++)
{
    plain.Add(($"random {seed}", generator.Generate(seed), 110.0 * Math.Pow(2.0, seed / 3.0)));
}

foreach (ReferencePatch reference in ReferencePatches.All.Take(4))
{
    plain.Add((reference.Name, reference.Patch, 220.0));
}

double Loss(SynthesisProfile profile, OperatorMatrixPatch hidden, double hz, int seed)
{
    RenderedAudio audio = renderer.Render(hidden, RenderRequest.Create(hz, 1.0, 0.3));
    var options = new PatchMatchOptions { SynthesisProfile = profile, EvaluationBudget = budget, RandomSeed = seed };
    return new OperatorMatrixPatchMatcher().Match(new AudioTarget(audio.Samples, audio.SampleRate, hz), options).SearchSummary.FinalAudioLoss;
}

void Run(string title, List<(string Name, OperatorMatrixPatch Patch, double Hz)> set)
{
    Console.WriteLine($"== {title} (budget {budget})");
    var differences = new List<double>();
    foreach ((string name, OperatorMatrixPatch patch, double hz) in set)
    {
        for (int seed = 1; seed <= 2; seed++)
        {
            double a = Loss(withFilters, patch, hz, seed);
            double b = Loss(withoutFilters, patch, hz, seed);
            differences.Add(a - b);
            Console.WriteLine(FormattableString.Invariant($"{name,-14} seed {seed}  with {a:F4}  without {b:F4}  diff {a - b:+0.0000;-0.0000}"));
        }
    }

    double mean = differences.Average();
    double se = Math.Sqrt(differences.Sum(d => (d - mean) * (d - mean)) / (differences.Count - 1) / differences.Count);
    Console.WriteLine(FormattableString.Invariant($"mean diff {mean:+0.0000;-0.0000} +/- {se:0.0000} (n = {differences.Count})"));
}

Run("hidden filtered patches", filtered);
Run("unfiltered FMX6 patches", plain);
```

Check the property names (`SearchSummary.FinalAudioLoss`, `ReferencePatch.Name`, `ReferencePatch.Patch`) against the code before running; adjust only names, not the method.

- [ ] **Step 2: Run it detached**

The run takes longer than the 10-minute limit of a background shell. Start it with PowerShell:

```powershell
$dir = "C:\Scripts\Temp\claude\D--Projects-hearsay\09393e74-00c1-48a1-b400-ea858f745082\scratchpad\filterbench"
dotnet build "$dir\filterbench.csproj" -c Release
Start-Process -FilePath dotnet -ArgumentList "run --project `"$dir\filterbench.csproj`" -c Release --no-build" -RedirectStandardOutput "$dir\out.txt" -RedirectStandardError "$dir\out.err" -WindowStyle Hidden
```

Watch `out.txt` with Monitor (re-arm it when it expires after 30 minutes) and also check `out.err` for "Unhandled". The first run builds the FMX6 v2 library and the no-filter v1 library (a few minutes each).

- [ ] **Step 3: Judge and report**

Success: on the hidden filtered patches the mean difference is clearly negative (more than two standard errors below zero); on the unfiltered patches it is within about two standard errors of zero. Report both tables to the user. If the filtered set does not improve, report which targets fail and do not merge; if the unfiltered set gets worse, report it and propose a fix (for example lowering the filter priors' scores or capping filter variants) before merging.

- [ ] **Step 4: Final verification before handing back**

Run: `dotnet test tests/HearSay.Synthesis.Tests`, `dotnet test tests/HearSay.Audio.Tests`, `dotnet test tests/HearSay.Modeling.Tests`, `dotnet test tests/HearSay.OperatorMatrix.Tests`, `dotnet test tests/HearSay.SuperCollider.Tests`
Expected: all pass. Do not merge or push either repository; report the branch names and the evaluation to the user.
