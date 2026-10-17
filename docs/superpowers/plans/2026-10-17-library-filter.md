# Library Filter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the Library tab, show only the sounds that match a filter (waveform, filter type, brightness, envelope,
noise, audible operators, feedback), re-clustered into families of their own.

**Architecture:** Per library sound a small `SoundTraits` value is computed once (patch regenerated from its seed,
words from its fingerprint). A `SoundFilter` (chosen `TraitValue`s, OR within a facet, AND across facets) picks the
matching indices and counts per chip. `LibraryAtlas.Build(library, members)` builds the familiar tree over just those
indices. The Studio's `LibraryViewModel` debounces chip changes, builds the filtered atlas off the UI thread and shows
its root, under the existing latest-wins version counters.

**Tech Stack:** C# / .NET 10, xUnit, Avalonia 11.3 (compiled bindings).

**Spec:** `docs/superpowers/specs/2026-10-17-library-filter-design.md`

## Global Constraints

- Within a row (facet) the chosen values are alternatives (OR); rows are combined (AND). A row without a chosen value does not filter.
- Waveform, filter and feedback count any audible operator (`OperatorMatrixPatch.FindAudibleOperators`).
- A chip is shown only when it splits the unfiltered library: some sounds have its value and some do not. A row without chips is hidden.
- Each chip shows the number of sounds that match the other rows and have its value. Chips at 0 are dimmed, and can still be chosen.
- A change of the chips applies after 0.4 s without a further change; the latest filter wins, older builds are dropped.
- Graph texts: `Grouping 4,213 sounds…`, `No sounds match the filter.`; summary `filter: saw or square · low-pass · percussive — 4,213 sounds`.
- Import that does not match the filter: status adds `Placed among the filtered sounds; it does not match the filter itself.`
- Switching profile clears the filter. Filtered atlases are never written to disk.
- Labels and chips share thresholds: brightness and envelope words come from the same functions in `SoundDescriptors`.
- Code style: file-scoped namespaces, XML doc comments on public members, `static` lambdas where possible, invariant culture for numbers in text; 0 warnings.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. Filter changed while the previous filtered build is still running, or profile switched during it: the newest wins, no stale graph or breadcrumb (version + atlas identity checks in `ApplyFilterAsync`).
2. A navigation (open family, crumb) in flight when the filtered atlas arrives: must not overwrite the new root (`_navVersion++` when grouping starts; atlas identity in `ShowFamilyAsync`).
3. Filter matching exactly one sound: the root must show it as one leaf, not an empty graph (covered by `Subset_SingleMember_GivesOneLeaf`).
4. Filter matching nothing, then importing and placing a sound: status message, no crash in `Locate`/`AtlasLayout` (covered by `Subset_Empty_HasNoChildren` + the empty branch in `PlaceAsync`).
5. Flyout bindings: chips inside an `Avalonia.Controls.Flyout` must see the view model (DataContext inheritance) — check in the headless harness or by hand.

---

### Task 1: Sound traits and trait values

**Files:**
- Create: `src/HearSay.OperatorMatrix/Priors/SoundTraits.cs`
- Modify: `src/HearSay.OperatorMatrix/Priors/SoundDescriptors.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/SoundTraitsTests.cs`

**Interfaces:**
- Consumes: `StructureLibrary.Count`, `Fingerprint(int)`, `Seed(int)`, `Patch(int seed)`; `OperatorWaveformExtensions.IsNoise`.
- Produces:
  - `enum SoundBrightness { Dark, Mellow, Bright, VeryBright }`, `enum SoundEnvelope { Percussive, Decaying, Sustained, Swelling }`
  - `enum TraitFacet { Waveform, Filter, Brightness, Envelope, Noise, Operators, Feedback }`
  - `readonly record struct TraitValue(TraitFacet Facet, int Value)` with `const int NoFilter = -1`, factories `Waveform(OperatorWaveform)`, `Filter(OperatorFilterType?)`, `Of(SoundBrightness)`, `Of(SoundEnvelope)`, `Noisy`, `Operators(int)`, `Feedback(bool)`, `string Label`, `static IReadOnlyList<TraitValue> All` (ordered by facet, then value), `static string TitleOf(TraitFacet)`
  - `readonly record struct SoundTraits(int Waveforms, int Filters, SoundBrightness Brightness, SoundEnvelope Envelope, bool Noisy, bool Feedback, int AudibleOperators)` with `const int NoFilterBit = 1 << 15`, `bool Has(TraitValue)`, `static SoundTraits Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)`, `static SoundTraits[] Of(StructureLibrary library)`
  - `SoundDescriptors.BrightnessOf(print)`, `EnvelopeOf(print)`, `Word(SoundBrightness)`, `Word(SoundEnvelope)`, `internal IsNoisy(patch, audible)`

- [ ] **Step 1: Write the failing tests**

`tests/HearSay.OperatorMatrix.Tests/SoundTraitsTests.cs`:

```csharp
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SoundTraitsTests
{
    private const byte Floor = 240;

    private static byte[] Print(int band, byte attack = 0, byte decay = 0)
    {
        var print = new byte[SpectralFingerprint.Length];
        Array.Fill(print, Floor);
        print[band] = attack;
        print[SpectralFingerprint.Bands + band] = 0;
        print[(2 * SpectralFingerprint.Bands) + band] = decay;
        return print;
    }

    private static OperatorPatch Op(double ratio, OperatorWaveform waveform = OperatorWaveform.Sine, OperatorFilter? filter = null) =>
        OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio)) with { Waveform = waveform, Filter = filter };

    private static OperatorMatrixPatch Carrier(OperatorWaveform waveform = OperatorWaveform.Sine, OperatorFilter? filter = null) =>
        OperatorMatrixPatch.Silent(6).WithOperator(0, Op(1, waveform, filter)).WithOutputLevel(0, 1.0);

    [Fact]
    public void AudibleOperators_GiveTheirWaveformsAndFilters()
    {
        // OP2 modulates OP1 and so is heard; OP3 is connected to nothing.
        OperatorMatrixPatch patch = Carrier(OperatorWaveform.Saw, OperatorFilter.LowPass(4))
            .WithOperator(1, Op(2, OperatorWaveform.Square, OperatorFilter.Notch(2, 2)))
            .WithEdge(new ModulationEdge(1, 0, true, 1.0))
            .WithOperator(2, Op(3, OperatorWaveform.Pulse, OperatorFilter.FormantAt(500, 2, 0.5, 5)));
        SoundTraits traits = SoundTraits.Of(Print(30), patch);

        Assert.True(traits.Has(TraitValue.Waveform(OperatorWaveform.Saw)));
        Assert.True(traits.Has(TraitValue.Waveform(OperatorWaveform.Square)));
        Assert.False(traits.Has(TraitValue.Waveform(OperatorWaveform.Pulse)));
        Assert.False(traits.Has(TraitValue.Waveform(OperatorWaveform.Sine)));
        Assert.True(traits.Has(TraitValue.Filter(OperatorFilterType.LowPass)));
        Assert.True(traits.Has(TraitValue.Filter(OperatorFilterType.Notch)));
        Assert.False(traits.Has(TraitValue.Filter(OperatorFilterType.Formant)));
        Assert.False(traits.Has(TraitValue.Filter(null)));
        Assert.Equal(2, traits.AudibleOperators);
        Assert.True(traits.Has(TraitValue.Operators(2)));
        Assert.False(traits.Has(TraitValue.Operators(3)));
    }

    [Fact]
    public void NoFilter_IsAValueOfItsOwn()
    {
        SoundTraits traits = SoundTraits.Of(Print(30), Carrier());
        Assert.True(traits.Has(TraitValue.Filter(null)));
        Assert.False(traits.Has(TraitValue.Filter(OperatorFilterType.LowPass)));
        Assert.Equal(SoundTraits.NoFilterBit, traits.Filters);
    }

    [Fact]
    public void Noise_FollowsTheDescriptorRule()
    {
        Assert.True(SoundTraits.Of(Print(30), Carrier(OperatorWaveform.Noise)).Has(TraitValue.Noisy));
        Assert.False(SoundTraits.Of(Print(30), Carrier()).Has(TraitValue.Noisy));
    }

    [Fact]
    public void Feedback_CountsOnlyOnAudibleOperators()
    {
        Assert.True(SoundTraits.Of(Print(30), Carrier().WithEdge(new ModulationEdge(0, 0, true, 0.5))).Has(TraitValue.Feedback(true)));
        Assert.True(SoundTraits.Of(Print(30), Carrier()).Has(TraitValue.Feedback(false)));

        OperatorMatrixPatch silent = Carrier().WithOperator(1, Op(2)).WithEdge(new ModulationEdge(1, 1, true, 0.5));
        Assert.True(SoundTraits.Of(Print(30), silent).Has(TraitValue.Feedback(false)));
    }

    [Theory]
    [InlineData(30, 0, 0)]
    [InlineData(42, 0, 100)]
    [InlineData(54, 0, 40)]
    [InlineData(66, 40, 0)]
    public void Words_AgreeWithTheLabels(int band, byte attack, byte decay)
    {
        byte[] print = Print(band, attack, decay);
        SoundTraits traits = SoundTraits.Of(print, Carrier());
        IReadOnlyList<string> words = SoundDescriptors.Of(print, Carrier());
        Assert.Equal(words[0], SoundDescriptors.Word(traits.Brightness));
        Assert.Equal(words[1], SoundDescriptors.Word(traits.Envelope));
        Assert.True(traits.Has(TraitValue.Of(traits.Brightness)));
        Assert.True(traits.Has(TraitValue.Of(traits.Envelope)));
    }

    [Fact]
    public void Library_GivesTheTraitsOfEverySound()
    {
        StructureLibrary library = LibraryAtlasTests.Library.Value;
        SoundTraits[] traits = SoundTraits.Of(library);
        Assert.Equal(library.Count, traits.Length);
        foreach (int i in new[] { 0, 1234, library.Count - 1 })
        {
            Assert.Equal(SoundTraits.Of(library.Fingerprint(i), library.Patch(library.Seed(i))), traits[i]);
        }
    }

    [Fact]
    public void Values_HaveLabelsAndAFixedOrder()
    {
        Assert.Equal("S&H noise", TraitValue.Waveform(OperatorWaveform.SampleHoldNoise).Label);
        Assert.Equal("saw", TraitValue.Waveform(OperatorWaveform.Saw).Label);
        Assert.Equal("none", TraitValue.Filter(null).Label);
        Assert.Equal("low-pass", TraitValue.Filter(OperatorFilterType.LowPass).Label);
        Assert.Equal("very bright", TraitValue.Of(SoundBrightness.VeryBright).Label);
        Assert.Equal("percussive", TraitValue.Of(SoundEnvelope.Percussive).Label);
        Assert.Equal("noisy", TraitValue.Noisy.Label);
        Assert.Equal("3 ops", TraitValue.Operators(3).Label);
        Assert.Equal("no feedback", TraitValue.Feedback(false).Label);
        Assert.Equal("Audible operators", TraitValue.TitleOf(TraitFacet.Operators));

        IReadOnlyList<TraitValue> all = TraitValue.All;
        Assert.Equal(11 + 7 + 4 + 4 + 1 + 6 + 2, all.Count);
        Assert.Equal(all.OrderBy(static v => v.Facet).ThenBy(static v => v.Value), all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FullyQualifiedName~SoundTraitsTests"`
Expected: build FAIL, `SoundTraits` / `TraitValue` / `SoundDescriptors.Word` not found.

- [ ] **Step 3: Refactor `SoundDescriptors` to expose the classifications**

In `src/HearSay.OperatorMatrix/Priors/SoundDescriptors.cs`, replace the body of `Of` from `var words = new List<string>(5);` through the noisy check, and add the new members, so that the file reads (unchanged members elided only where marked):

```csharp
    /// <summary>Brightness class of a sound: the power-weighted mean of the body slice, as a multiple of the note.</summary>
    /// <param name="print">Fingerprint.</param>
    public static SoundBrightness BrightnessOf(ReadOnlySpan<byte> print)
    {
        double ratio = BrightnessRatio(print);
        return ratio < 2 ? SoundBrightness.Dark : ratio < 4 ? SoundBrightness.Mellow : ratio < 8 ? SoundBrightness.Bright : SoundBrightness.VeryBright;
    }

    /// <summary>Envelope class of a sound: the decay slice against the attack slice.</summary>
    /// <param name="print">Fingerprint.</param>
    public static SoundEnvelope EnvelopeOf(ReadOnlySpan<byte> print)
    {
        double decay = DecayDb(print);
        return decay <= -18 ? SoundEnvelope.Percussive : decay <= -6 ? SoundEnvelope.Decaying : decay <= 6 ? SoundEnvelope.Sustained : SoundEnvelope.Swelling;
    }

    /// <summary>The word for a brightness class.</summary>
    /// <param name="brightness">Class.</param>
    public static string Word(SoundBrightness brightness) => brightness switch
    {
        SoundBrightness.Dark => "dark",
        SoundBrightness.Mellow => "mellow",
        SoundBrightness.Bright => "bright",
        _ => "very bright",
    };

    /// <summary>The word for an envelope class.</summary>
    /// <param name="envelope">Class.</param>
    public static string Word(SoundEnvelope envelope) => envelope switch
    {
        SoundEnvelope.Percussive => "percussive",
        SoundEnvelope.Decaying => "decaying",
        SoundEnvelope.Sustained => "sustained",
        _ => "swelling",
    };

    /// <summary>True when an audible operator is a noise waveform or roughened by the shared noise.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="audible">Its audible operators, see <see cref="OperatorMatrixPatch.FindAudibleOperators"/>.</param>
    internal static bool IsNoisy(OperatorMatrixPatch patch, bool[] audible)
    {
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            OperatorPatch o = patch.Operators[i];
            if (audible[i] && (o.Waveform.IsNoise() || o.NoisePhase != 0.0 || o.NoiseM != 0.0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The words for a sound, in a fixed order.</summary>
    /// <param name="print">Fingerprint of the sound.</param>
    /// <param name="patch">Patch that plays it.</param>
    public static IReadOnlyList<string> Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var words = new List<string>(5) { Word(BrightnessOf(print)), Word(EnvelopeOf(print)) };

        bool[] audible = patch.FindAudibleOperators();
        int loudest = -1;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (audible[i] && patch.OutputLevels[i] != 0.0 && (loudest < 0 || Math.Abs(patch.OutputLevels[i]) > Math.Abs(patch.OutputLevels[loudest])))
            {
                loudest = i;
            }
        }

        if (loudest >= 0 && patch.Operators[loudest].Waveform != OperatorWaveform.Sine)
        {
            words.Add(WaveformWord(patch.Operators[loudest].Waveform));
        }

        if (Enumerable.Range(0, patch.OperatorCount).Any(i => audible[i] && patch.Operators[i].Filter is not null))
        {
            words.Add("filtered");
        }

        if (IsNoisy(patch, audible))
        {
            words.Add("noisy");
        }

        return words;
    }
```

Delete the private `IsNoise(OperatorWaveform)` helper (replaced by `OperatorWaveformExtensions.IsNoise`).

- [ ] **Step 4: Create `SoundTraits.cs`**

`src/HearSay.OperatorMatrix/Priors/SoundTraits.cs`:

```csharp
using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>Brightness class of a sound, see <see cref="SoundDescriptors.BrightnessOf"/>.</summary>
public enum SoundBrightness
{
    /// <summary>Below twice the note.</summary>
    Dark,

    /// <summary>Two to four times the note.</summary>
    Mellow,

    /// <summary>Four to eight times the note.</summary>
    Bright,

    /// <summary>From eight times the note.</summary>
    VeryBright,
}

/// <summary>Envelope class of a sound, see <see cref="SoundDescriptors.EnvelopeOf"/>.</summary>
public enum SoundEnvelope
{
    /// <summary>The decay at least 18 dB below the attack.</summary>
    Percussive,

    /// <summary>6 to 18 dB below.</summary>
    Decaying,

    /// <summary>Within 6 dB.</summary>
    Sustained,

    /// <summary>More than 6 dB above.</summary>
    Swelling,
}

/// <summary>A row of the library filter.</summary>
public enum TraitFacet
{
    /// <summary>Waveform of an audible operator.</summary>
    Waveform,

    /// <summary>Filter type of an audible operator, or none.</summary>
    Filter,

    /// <summary>Brightness class.</summary>
    Brightness,

    /// <summary>Envelope class.</summary>
    Envelope,

    /// <summary>Noisy.</summary>
    Noise,

    /// <summary>Number of audible operators.</summary>
    Operators,

    /// <summary>Self-feedback on an audible operator, or none.</summary>
    Feedback,
}

/// <summary>One chip of the library filter: a facet and a value of it.</summary>
/// <param name="Facet">Row.</param>
/// <param name="Value">
/// The waveform or filter type as an integer (<see cref="NoFilter"/> for none), the brightness or envelope class, 1 for
/// noisy, the number of operators, or 1 / 0 for feedback / no feedback.
/// </param>
public readonly record struct TraitValue(TraitFacet Facet, int Value)
{
    /// <summary>The filter value for "no audible operator has a filter".</summary>
    public const int NoFilter = -1;

    /// <summary>Every value, ordered by facet, then value.</summary>
    public static IReadOnlyList<TraitValue> All { get; } =
    [
        .. Enum.GetValues<OperatorWaveform>().Select(Waveform),
        Filter(null),
        .. Enum.GetValues<OperatorFilterType>().Select(static t => Filter(t)),
        .. Enum.GetValues<SoundBrightness>().Select(Of),
        .. Enum.GetValues<SoundEnvelope>().Select(Of),
        Noisy,
        .. Enumerable.Range(1, 6).Select(Operators),
        Feedback(false),
        Feedback(true),
    ];

    /// <summary>Noisy.</summary>
    public static TraitValue Noisy => new(TraitFacet.Noise, 1);

    /// <summary>An audible operator has this waveform.</summary>
    /// <param name="waveform">Waveform.</param>
    public static TraitValue Waveform(OperatorWaveform waveform) => new(TraitFacet.Waveform, (int)waveform);

    /// <summary>An audible operator has a filter of this type; for null, none has a filter.</summary>
    /// <param name="type">Filter type, or null for none.</param>
    public static TraitValue Filter(OperatorFilterType? type) => new(TraitFacet.Filter, type is { } t ? (int)t : NoFilter);

    /// <summary>This brightness class.</summary>
    /// <param name="brightness">Class.</param>
    public static TraitValue Of(SoundBrightness brightness) => new(TraitFacet.Brightness, (int)brightness);

    /// <summary>This envelope class.</summary>
    /// <param name="envelope">Class.</param>
    public static TraitValue Of(SoundEnvelope envelope) => new(TraitFacet.Envelope, (int)envelope);

    /// <summary>This many audible operators.</summary>
    /// <param name="count">Number of operators.</param>
    public static TraitValue Operators(int count) => new(TraitFacet.Operators, count);

    /// <summary>Self-feedback on an audible operator, or none.</summary>
    /// <param name="present">True for feedback.</param>
    public static TraitValue Feedback(bool present) => new(TraitFacet.Feedback, present ? 1 : 0);

    /// <summary>Title of a row.</summary>
    /// <param name="facet">Facet.</param>
    public static string TitleOf(TraitFacet facet) => facet switch
    {
        TraitFacet.Waveform => "Waveform",
        TraitFacet.Filter => "Filter",
        TraitFacet.Brightness => "Brightness",
        TraitFacet.Envelope => "Envelope",
        TraitFacet.Noise => "Noise",
        TraitFacet.Operators => "Audible operators",
        _ => "Feedback",
    };

    /// <summary>Text of the chip.</summary>
    public string Label => Facet switch
    {
        TraitFacet.Waveform => (OperatorWaveform)Value switch
        {
            OperatorWaveform.SampleHoldNoise => "S&H noise",
            OperatorWaveform.FilteredNoise => "filtered noise",
            var w => w.ToString().ToLowerInvariant(),
        },
        TraitFacet.Filter => Value == NoFilter ? "none" : (OperatorFilterType)Value switch
        {
            OperatorFilterType.LowPass => "low-pass",
            OperatorFilterType.HighPass => "high-pass",
            OperatorFilterType.BandPass => "band-pass",
            var t => t.ToString().ToLowerInvariant(),
        },
        TraitFacet.Brightness => SoundDescriptors.Word((SoundBrightness)Value),
        TraitFacet.Envelope => SoundDescriptors.Word((SoundEnvelope)Value),
        TraitFacet.Noise => "noisy",
        TraitFacet.Operators => Value.ToString(CultureInfo.InvariantCulture) + " ops",
        _ => Value == 1 ? "feedback" : "no feedback",
    };
}

/// <summary>What the library filter knows about a sound.</summary>
/// <param name="Waveforms">Bit mask over <see cref="OperatorWaveform"/> of the audible operators.</param>
/// <param name="Filters">Bit mask over <see cref="OperatorFilterType"/> of their filters; <see cref="NoFilterBit"/> when none has one.</param>
/// <param name="Brightness">Brightness class.</param>
/// <param name="Envelope">Envelope class.</param>
/// <param name="Noisy">An audible operator is noise or roughened by the shared noise.</param>
/// <param name="Feedback">An audible operator has self-feedback.</param>
/// <param name="AudibleOperators">Number of audible operators.</param>
public readonly record struct SoundTraits(int Waveforms, int Filters, SoundBrightness Brightness, SoundEnvelope Envelope, bool Noisy, bool Feedback, int AudibleOperators)
{
    /// <summary>The bit of <see cref="Filters"/> for "no filter".</summary>
    public const int NoFilterBit = 1 << 15;

    /// <summary>The traits of a sound.</summary>
    /// <param name="print">Its fingerprint.</param>
    /// <param name="patch">Its patch.</param>
    public static SoundTraits Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool[] audible = patch.FindAudibleOperators();
        int waveforms = 0, filters = 0, heard = 0;
        bool feedback = false;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            heard++;
            OperatorPatch op = patch.Operators[i];
            waveforms |= 1 << (int)op.Waveform;
            if (op.Filter is { } filter)
            {
                filters |= 1 << (int)filter.Type;
            }

            feedback |= patch.ModulationMatrix[i, i] != 0.0;
        }

        return new SoundTraits(
            waveforms,
            filters == 0 ? NoFilterBit : filters,
            SoundDescriptors.BrightnessOf(print),
            SoundDescriptors.EnvelopeOf(print),
            SoundDescriptors.IsNoisy(patch, audible),
            feedback,
            heard);
    }

    /// <summary>The traits of every sound of a library, by index; every patch is generated again from its seed.</summary>
    /// <param name="library">Library.</param>
    public static SoundTraits[] Of(StructureLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var traits = new SoundTraits[library.Count];
        Parallel.For(0, library.Count, i => traits[i] = Of(library.Fingerprint(i), library.Patch(library.Seed(i))));
        return traits;
    }

    /// <summary>True when the sound has the value.</summary>
    /// <param name="value">Chip value.</param>
    public bool Has(TraitValue value) => value.Facet switch
    {
        TraitFacet.Waveform => (Waveforms & (1 << value.Value)) != 0,
        TraitFacet.Filter => (Filters & (value.Value == TraitValue.NoFilter ? NoFilterBit : 1 << value.Value)) != 0,
        TraitFacet.Brightness => (int)Brightness == value.Value,
        TraitFacet.Envelope => (int)Envelope == value.Value,
        TraitFacet.Noise => Noisy == (value.Value == 1),
        TraitFacet.Operators => AudibleOperators == value.Value,
        _ => Feedback == (value.Value == 1),
    };
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FullyQualifiedName~SoundTraitsTests|FullyQualifiedName~SoundDescriptorsTests"`
Expected: PASS, all SoundTraitsTests and the 10 existing SoundDescriptorsTests.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/SoundTraits.cs src/HearSay.OperatorMatrix/Priors/SoundDescriptors.cs tests/HearSay.OperatorMatrix.Tests/SoundTraitsTests.cs
git commit -m "Library: the traits of a sound (waveforms, filters, words, operators, feedback) for filtering

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Sound filter

**Files:**
- Create: `src/HearSay.OperatorMatrix/Priors/SoundFilter.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/SoundFilterTests.cs`

**Interfaces:**
- Consumes: `SoundTraits`, `SoundTraits.Has`, `SoundTraits.NoFilterBit`, `TraitValue` (Task 1).
- Produces: `sealed class SoundFilter` with `static SoundFilter None`, `IReadOnlyCollection<TraitValue> Chosen`, `bool IsEmpty`, `bool Contains(TraitValue)`, `SoundFilter With(TraitValue)`, `SoundFilter Without(TraitValue)`, `bool Matches(SoundTraits)`, `int[] Members(IReadOnlyList<SoundTraits>)`, `int[] Counts(IReadOnlyList<SoundTraits>, IReadOnlyList<TraitValue>)`, `string Describe()`.

- [ ] **Step 1: Write the failing tests**

`tests/HearSay.OperatorMatrix.Tests/SoundFilterTests.cs`:

```csharp
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SoundFilterTests
{
    private static SoundTraits T(OperatorWaveform waveform, OperatorFilterType? filter, SoundBrightness brightness, bool noisy = false) =>
        new(1 << (int)waveform, filter is { } f ? 1 << (int)f : SoundTraits.NoFilterBit, brightness, SoundEnvelope.Sustained, noisy, false, 2);

    private static readonly SoundTraits[] Sounds =
    [
        T(OperatorWaveform.Saw, OperatorFilterType.LowPass, SoundBrightness.Dark),
        T(OperatorWaveform.Square, OperatorFilterType.LowPass, SoundBrightness.Bright),
        T(OperatorWaveform.Saw, null, SoundBrightness.Bright, noisy: true),
        T(OperatorWaveform.Sine, OperatorFilterType.HighPass, SoundBrightness.Dark),
        T(OperatorWaveform.Square, null, SoundBrightness.Dark),
    ];

    private static readonly TraitValue Saw = TraitValue.Waveform(OperatorWaveform.Saw);
    private static readonly TraitValue Square = TraitValue.Waveform(OperatorWaveform.Square);
    private static readonly TraitValue LowPass = TraitValue.Filter(OperatorFilterType.LowPass);

    [Fact]
    public void Empty_MatchesEverySound()
    {
        Assert.True(SoundFilter.None.IsEmpty);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, SoundFilter.None.Members(Sounds));
    }

    [Fact]
    public void ValuesOfOneFacet_AreAlternatives()
    {
        SoundFilter filter = SoundFilter.None.With(Saw).With(Square);
        Assert.False(filter.IsEmpty);
        Assert.True(filter.Contains(Saw));
        Assert.Equal(new[] { 0, 1, 2, 4 }, filter.Members(Sounds));
    }

    [Fact]
    public void Facets_AreCombined()
    {
        SoundFilter filter = SoundFilter.None.With(Saw).With(Square).With(LowPass);
        Assert.Equal(new[] { 0, 1 }, filter.Members(Sounds));
        Assert.Equal(new[] { 1 }, filter.With(TraitValue.Of(SoundBrightness.Bright)).Members(Sounds));
        Assert.Equal(new[] { 2 }, SoundFilter.None.With(TraitValue.Noisy).Members(Sounds));
        Assert.Equal(new[] { 0, 2 }, filter.Without(Square).Without(LowPass).Members(Sounds));
    }

    [Fact]
    public void Counts_AreTheSoundsThatMatchTheOtherFacetsAndHaveTheValue()
    {
        SoundFilter filter = SoundFilter.None.With(Saw).With(Square).With(LowPass);
        IReadOnlyList<TraitValue> values = TraitValue.All;
        int[] counts = filter.Counts(Sounds, values);
        Assert.Equal(values.Count, counts.Length);
        for (int k = 0; k < values.Count; k++)
        {
            TraitValue v = values[k];
            SoundFilter others = filter.Chosen.Where(c => c.Facet != v.Facet).Aggregate(SoundFilter.None, static (f, c) => f.With(c));
            Assert.Equal(Sounds.Count(t => t.Has(v) && others.Matches(t)), counts[k]);
        }

        Assert.Equal(1, counts[IndexOf(values, Saw)]);
        Assert.Equal(0, counts[IndexOf(values, TraitValue.Waveform(OperatorWaveform.Sine))]);
        Assert.Equal(2, counts[IndexOf(values, TraitValue.Filter(null))]);
        Assert.Equal(2, counts[IndexOf(values, LowPass)]);
    }

    [Fact]
    public void Describe_ListsFacetsInPanelOrder()
    {
        SoundFilter filter = SoundFilter.None.With(TraitValue.Of(SoundEnvelope.Percussive)).With(LowPass).With(Square).With(Saw);
        Assert.Equal("saw or square · low-pass · percussive", filter.Describe());
        Assert.Equal(string.Empty, SoundFilter.None.Describe());
    }

    private static int IndexOf(IReadOnlyList<TraitValue> values, TraitValue value) => values.ToList().IndexOf(value);
}
```


- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FullyQualifiedName~SoundFilterTests"`
Expected: build FAIL, `SoundFilter` not found.

- [ ] **Step 3: Create `SoundFilter.cs`**

`src/HearSay.OperatorMatrix/Priors/SoundFilter.cs`:

```csharp
using System.Collections.Immutable;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// The chips chosen in the library filter. Values of one facet are alternatives, facets are combined; a facet without
/// a chosen value does not filter. Immutable.
/// </summary>
public sealed class SoundFilter
{
    private static readonly int FacetCount = Enum.GetValues<TraitFacet>().Length;
    private readonly ImmutableHashSet<TraitValue> _chosen;
    private readonly TraitValue[][] _facets;

    private SoundFilter(ImmutableHashSet<TraitValue> chosen)
    {
        _chosen = chosen;
        _facets = [.. chosen.GroupBy(static v => v.Facet).OrderBy(static g => g.Key).Select(static g => g.OrderBy(static v => v.Value).ToArray())];
    }

    /// <summary>Nothing chosen: every sound matches.</summary>
    public static SoundFilter None { get; } = new(ImmutableHashSet<TraitValue>.Empty);

    /// <summary>The chosen values.</summary>
    public IReadOnlyCollection<TraitValue> Chosen => _chosen;

    /// <summary>True when nothing is chosen.</summary>
    public bool IsEmpty => _chosen.IsEmpty;

    /// <summary>True when the value is chosen.</summary>
    /// <param name="value">Value.</param>
    public bool Contains(TraitValue value) => _chosen.Contains(value);

    /// <summary>A filter with the value chosen as well.</summary>
    /// <param name="value">Value.</param>
    public SoundFilter With(TraitValue value) => new(_chosen.Add(value));

    /// <summary>A filter without the value.</summary>
    /// <param name="value">Value.</param>
    public SoundFilter Without(TraitValue value) => new(_chosen.Remove(value));

    /// <summary>True when the sound has a chosen value of every facet with one.</summary>
    /// <param name="traits">The sound's traits.</param>
    public bool Matches(SoundTraits traits)
    {
        foreach (TraitValue[] facet in _facets)
        {
            if (!HasAny(traits, facet))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Indices of the matching sounds, ascending.</summary>
    /// <param name="traits">Traits of all sounds, by index.</param>
    public int[] Members(IReadOnlyList<SoundTraits> traits)
    {
        ArgumentNullException.ThrowIfNull(traits);
        var members = new List<int>();
        for (int i = 0; i < traits.Count; i++)
        {
            if (Matches(traits[i]))
            {
                members.Add(i);
            }
        }

        return [.. members];
    }

    /// <summary>Per value, the number of sounds that match every other facet and have the value; one pass.</summary>
    /// <param name="traits">Traits of all sounds.</param>
    /// <param name="values">The chip values.</param>
    public int[] Counts(IReadOnlyList<SoundTraits> traits, IReadOnlyList<TraitValue> values)
    {
        ArgumentNullException.ThrowIfNull(traits);
        ArgumentNullException.ThrowIfNull(values);
        var counts = new int[values.Count];
        var missed = new bool[FacetCount];
        for (int i = 0; i < traits.Count; i++)
        {
            SoundTraits t = traits[i];
            Array.Clear(missed);
            int misses = 0;
            foreach (TraitValue[] facet in _facets)
            {
                if (!HasAny(t, facet))
                {
                    missed[(int)facet[0].Facet] = true;
                    misses++;
                }
            }

            if (misses > 1)
            {
                continue;
            }

            for (int k = 0; k < values.Count; k++)
            {
                TraitValue v = values[k];
                if ((misses == 0 || missed[(int)v.Facet]) && t.Has(v))
                {
                    counts[k]++;
                }
            }
        }

        return counts;
    }

    /// <summary>The chosen values for the summary line: facets in panel order, alternatives joined with "or".</summary>
    public string Describe() => string.Join(" · ", _facets.Select(static f => string.Join(" or ", f.Select(static v => v.Label))));

    private static bool HasAny(SoundTraits traits, TraitValue[] facet)
    {
        foreach (TraitValue v in facet)
        {
            if (traits.Has(v))
            {
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FullyQualifiedName~SoundFilterTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/SoundFilter.cs tests/HearSay.OperatorMatrix.Tests/SoundFilterTests.cs
git commit -m "Library: a sound filter, alternatives within a facet and facets combined, with counts per chip

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Atlas over a subset

**Files:**
- Modify: `src/HearSay.OperatorMatrix/Priors/LibraryAtlas.cs` (`Build`, `RootOf`, `Split`)
- Test: `tests/HearSay.OperatorMatrix.Tests/LibraryAtlasTests.cs`

**Interfaces:**
- Consumes: existing `LibraryAtlas` internals (`RootOf`, `Split`, `Mean`, `MedoidOf`, `Leaves`).
- Produces: `public static LibraryAtlas Build(StructureLibrary library, IReadOnlyList<int> members)`; an empty set gives `Root.Size == 0`, `Root.Medoid == -1`, `Children(Root)` empty, `Locate(...) == [Root]`; a root of one member has that member as its one leaf.

- [ ] **Step 1: Write the failing tests**

Append to `LibraryAtlasTests` (before `AssertSameTopLevel`):

```csharp
    private static readonly int[] EveryThird = [.. Enumerable.Range(0, 3000).Where(static i => i % 3 == 0)];

    [Fact]
    public void Subset_ChildrenPartitionTheGivenMembers()
    {
        LibraryAtlas atlas = LibraryAtlas.Build(Library.Value, EveryThird);
        Assert.Equal(EveryThird, atlas.Root.Members);
        AssertPartition(atlas, atlas.Root);
        LibraryFamily largest = atlas.Children(atlas.Root)[0];
        Assert.True(largest.Size > LibraryAtlas.Branching);
        AssertPartition(atlas, largest);
    }

    [Fact]
    public void Subset_IsDeterministic()
    {
        LibraryAtlas a = LibraryAtlas.Build(Library.Value, EveryThird), b = LibraryAtlas.Build(Library.Value, EveryThird);
        Assert.Equal(a.Children(a.Root).Select(static c => c.Medoid), b.Children(b.Root).Select(static c => c.Medoid));
    }

    [Fact]
    public void Subset_LocateStaysInside()
    {
        LibraryAtlas atlas = LibraryAtlas.Build(Library.Value, EveryThird);
        var inside = EveryThird.ToHashSet();
        for (int i = 0; i < 3000; i += 101)
        {
            IReadOnlyList<LibraryFamily> path = atlas.Locate(atlas.Library.Fingerprint(i));
            Assert.Same(atlas.Root, path[0]);
            Assert.All(path, f => Assert.True(f.Members.All(inside.Contains)));
        }
    }

    [Fact]
    public void Subset_OfFewSounds_GivesLeaves()
    {
        LibraryAtlas atlas = LibraryAtlas.Build(Library.Value, [5, 17, 2999]);
        IReadOnlyList<LibraryFamily> leaves = atlas.Children(atlas.Root);
        Assert.Equal(new[] { 5, 17, 2999 }, leaves.Select(static l => l.Medoid));
        Assert.All(leaves, static l => Assert.True(l.IsLeaf));
    }

    [Fact]
    public void Subset_SingleMember_GivesOneLeaf()
    {
        LibraryAtlas atlas = LibraryAtlas.Build(Library.Value, [42]);
        LibraryFamily leaf = Assert.Single(atlas.Children(atlas.Root));
        Assert.Equal(42, leaf.Medoid);
        Assert.Empty(atlas.Children(leaf));
        Assert.Equal(new[] { atlas.Root }, atlas.Locate(atlas.Library.Fingerprint(42)));
    }

    [Fact]
    public void Subset_Empty_HasNoChildren()
    {
        LibraryAtlas atlas = LibraryAtlas.Build(Library.Value, []);
        Assert.Equal(0, atlas.Root.Size);
        Assert.Equal(-1, atlas.Root.Medoid);
        Assert.Empty(atlas.Children(atlas.Root));
        Assert.Equal(new[] { atlas.Root }, atlas.Locate(atlas.Library.Fingerprint(0)));
    }

    [Theory]
    [InlineData(new[] { 3, 1 })]
    [InlineData(new[] { 1, 1 })]
    [InlineData(new[] { 3000 })]
    [InlineData(new[] { -1 })]
    public void Subset_RejectsIndicesThatAreNotAscendingDistinctMembers(int[] members) =>
        Assert.Throws<ArgumentException>(() => LibraryAtlas.Build(Library.Value, members));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FullyQualifiedName~LibraryAtlasTests"`
Expected: build FAIL, no overload `Build(StructureLibrary, int[])`.

- [ ] **Step 3: Implement**

In `LibraryAtlas.cs`, after the existing `Build(StructureLibrary library)`:

```csharp
    /// <summary>
    /// The tree of some sounds of a library, in memory: its root holds the given members instead of every sound. The
    /// same members always give the same tree. An empty set gives a root without children.
    /// </summary>
    /// <param name="library">Library.</param>
    /// <param name="members">Library indices, ascending and distinct.</param>
    /// <exception cref="ArgumentException">An index is out of range, or the indices are not ascending and distinct.</exception>
    public static LibraryAtlas Build(StructureLibrary library, IReadOnlyList<int> members)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(members);
        int[] sorted = [.. members];
        for (int i = 0; i < sorted.Length; i++)
        {
            if ((uint)sorted[i] >= (uint)library.Count || (i > 0 && sorted[i] <= sorted[i - 1]))
            {
                throw new ArgumentException("Members are ascending, distinct indices of the library.", nameof(members));
            }
        }

        return new LibraryAtlas(library, RootOf(library, sorted));
    }
```

Replace `RootOf`:

```csharp
    internal static LibraryFamily RootOf(StructureLibrary library) => RootOf(library, [.. Enumerable.Range(0, library.Count)]);

    private static LibraryFamily RootOf(StructureLibrary library, int[] members)
    {
        if (members.Length == 0)
        {
            return new LibraryFamily([], [], new double[SpectralFingerprint.Length], -1);
        }

        double[] centroid = Mean(library, members);
        return new LibraryFamily([], members, centroid, MedoidOf(library, members, centroid));
    }
```

In `Split`, replace

```csharp
        if (members.Length <= 1)
        {
            return [];
        }
```

with

```csharp
        // A single sound below the root is a leaf; a root of one sound shows it as its one leaf.
        if (members.Length == 0 || (members.Length == 1 && family.PathArray.Length > 0))
        {
            return [];
        }
```

Update the `LibraryFamily.Medoid` doc comment to add: `−1 for the root of an empty subset.`

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests --filter "FullyQualifiedName~LibraryAtlasTests"`
Expected: PASS, all old and new atlas tests.

- [ ] **Step 5: Run the whole project and commit**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests`
Expected: PASS, 0 failed.

```bash
git add src/HearSay.OperatorMatrix/Priors/LibraryAtlas.cs tests/HearSay.OperatorMatrix.Tests/LibraryAtlasTests.cs
git commit -m "Library atlas: build the tree over some of the sounds

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Studio filter panel and filtered browsing

**Files:**
- Create: `src/HearSay.Studio/FilterChips.cs`
- Modify: `src/HearSay.Studio/LibraryViewModel.cs`, `src/HearSay.Studio/LibraryView.axaml`, `src/HearSay.Studio/LibraryView.axaml.cs`
- Modify: `docs/expert-manual.md` (§9 "The Library tab")

**Interfaces:**
- Consumes: `SoundTraits.Of(StructureLibrary)`, `SoundTraits.Of(print, patch)`, `TraitValue.All`, `TraitValue.TitleOf`, `SoundFilter` (Tasks 1–2), `LibraryAtlas.Build(library, members)` (Task 3).
- Produces (Studio only): `TraitChip`, `TraitRow`; `LibraryViewModel.FilterRows`, `FilterSummary`, `HasFilter`, `ClearFilter()`.

No Studio test project exists (spec: UI checked by headless screenshot and by hand). The TDD gate for this task is the
compile with 0 warnings, the full fast suites, and the harness/hand check of Step 6.

- [ ] **Step 1: Chip view models**

`src/HearSay.Studio/FilterChips.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using HearSay.OperatorMatrix.Priors;

namespace HearSay.Studio;

/// <summary>One chip of the library filter: a value, how many sounds it would show, and whether it is chosen.</summary>
public sealed class TraitChip : INotifyPropertyChanged
{
    private readonly Action<TraitChip> _toggled;
    private bool _isChosen;
    private int _count;

    /// <summary>Creates a chip.</summary>
    /// <param name="value">Its value.</param>
    /// <param name="count">Sounds that match the rest of the filter and have the value.</param>
    /// <param name="toggled">Called when the chip is chosen or released by the user.</param>
    public TraitChip(TraitValue value, int count, Action<TraitChip> toggled)
    {
        Value = value;
        _count = count;
        _toggled = toggled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The value.</summary>
    public TraitValue Value { get; }

    /// <summary>Chosen; set by the toggle button.</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (_isChosen == value)
            {
                return;
            }

            _isChosen = value;
            Changed(nameof(IsChosen));
            _toggled(this);
        }
    }

    /// <summary>Sounds that match the rest of the filter and have the value.</summary>
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            Changed(nameof(Count));
            Changed(nameof(Text));
            Changed(nameof(IsEmpty));
        }
    }

    /// <summary>Label and count, for example <c>saw 12,408</c>.</summary>
    public string Text => Value.Label + " " + Count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>True when no sound would be shown.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Releases the chip without telling the owner.</summary>
    internal void Release()
    {
        if (_isChosen)
        {
            _isChosen = false;
            Changed(nameof(IsChosen));
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A row of the filter panel: one facet and its chips.</summary>
/// <param name="Title">Row title.</param>
/// <param name="Chips">Chips, in panel order.</param>
public sealed record TraitRow(string Title, IReadOnlyList<TraitChip> Chips);
```

- [ ] **Step 2: View model state and properties**

In `LibraryViewModel.cs`, add fields after `private string _playingText = string.Empty;`:

```csharp
    private LibraryAtlas? _fullAtlas;
    private SoundTraits[]? _traits;
    private SoundFilter _filter = SoundFilter.None;
    private int _filterVersion;
    private string? _filterSummary;
```

Add properties after `CanPlay`:

```csharp
    /// <summary>Rows of the filter panel; empty until the library is read, and for a library nothing can filter.</summary>
    public ObservableCollection<TraitRow> FilterRows { get; } = [];

    /// <summary>The summary line under the breadcrumb while a filter is set.</summary>
    public string? FilterSummary
    {
        get => _filterSummary;
        private set => Set(ref _filterSummary, value);
    }

    /// <summary>True while a filter is set.</summary>
    public bool HasFilter => !_filter.IsEmpty;

    /// <summary>Releases every chip and shows all sounds again.</summary>
    public void ClearFilter()
    {
        foreach (TraitChip chip in FilterRows.SelectMany(static r => r.Chips))
        {
            chip.Release();
        }

        SetFilter(SoundFilter.None);
    }
```

- [ ] **Step 3: Loading reads the traits and resets the filter**

In `LoadAsync`, after `SelectedIndex = -1;` (the first block of resets) add:

```csharp
        _fullAtlas = null;
        _traits = null;
        _filter = SoundFilter.None;
        _filterVersion++;
        FilterRows.Clear();
        FilterSummary = null;
        OnPropertyChanged(nameof(HasFilter));
```

Replace the `Task.Run` that returns the atlas and the lines up to `_atlas = atlas;` with:

```csharp
            (LibraryAtlas atlas, SoundTraits[] traits, int[] counts) = await Task.Run(() =>
            {
                StructureLibrary library = StructureLibrary.For(profile, _size, SynthesisBackends.RendererFor(profile), progress: progress);
                string cache = LibraryAtlas.CacheFileOf(StructureLibrary.FileOf(profile, _size));
                if (!File.Exists(cache))
                {
                    ((IProgress<string>)stage).Report($"Grouping the {name} sounds into families (once, under a minute)…");
                }

                LibraryAtlas opened = LibraryAtlas.Open(library, cache);
                ((IProgress<string>)stage).Report($"Reading the {name} sounds…");
                SoundTraits[] t = SoundTraits.Of(library);
                return (opened, t, SoundFilter.None.Counts(t, TraitValue.All));
            });
            if (version != _loadVersion)
            {
                return;
            }

            _fullAtlas = atlas;
            _traits = traits;
            BuildFilterRows(counts, traits.Length);
            _atlas = atlas;
```

Add the helper next to `CrumbLabel`:

```csharp
    /// <summary>The chips that split the library: some sounds have the value and some do not.</summary>
    private void BuildFilterRows(int[] counts, int size)
    {
        FilterRows.Clear();
        IEnumerable<IGrouping<TraitFacet, (TraitValue Value, int Count)>> facets = TraitValue.All
            .Select((v, k) => (Value: v, Count: counts[k]))
            .Where(x => x.Count > 0 && x.Count < size)
            .GroupBy(static x => x.Value.Facet);
        foreach (IGrouping<TraitFacet, (TraitValue Value, int Count)> facet in facets)
        {
            FilterRows.Add(new TraitRow(TraitValue.TitleOf(facet.Key), [.. facet.Select(x => new TraitChip(x.Value, x.Count, OnChipToggled))]));
        }
    }
```

- [ ] **Step 4: Applying the filter**

Add these methods after `ClearFilter`:

```csharp
    private void OnChipToggled(TraitChip chip) => SetFilter(chip.IsChosen ? _filter.With(chip.Value) : _filter.Without(chip.Value));

    private void SetFilter(SoundFilter filter)
    {
        _filter = filter;
        OnPropertyChanged(nameof(HasFilter));
        FilterSummary = filter.IsEmpty ? null : $"filter: {filter.Describe()} — counting…";
        _ = ApplyFilterAsync(++_filterVersion);
    }

    /// <summary>
    /// After 0.4 s without a further change: counts the chips, builds the tree of the matching sounds and shows its top
    /// level. The latest filter wins; a library opened meanwhile drops the build.
    /// </summary>
    private async Task ApplyFilterAsync(int version)
    {
        await Task.Delay(400);
        if (version != _filterVersion || _fullAtlas is not { } full || _traits is not { } traits)
        {
            return;
        }

        int load = _loadVersion;
        bool Stale() => version != _filterVersion || load != _loadVersion || !ReferenceEquals(full, _fullAtlas);
        SoundFilter filter = _filter;
        TraitChip[] chips = [.. FilterRows.SelectMany(static r => r.Chips)];
        TraitValue[] values = [.. chips.Select(static c => c.Value)];
        (int[] counts, int[] members) = await Task.Run(() => (filter.Counts(traits, values), filter.IsEmpty ? [] : filter.Members(traits)));
        if (Stale())
        {
            return;
        }

        for (int k = 0; k < chips.Length; k++)
        {
            chips[k].Count = counts[k];
        }

        if (filter.IsEmpty)
        {
            FilterSummary = null;
            await ShowAtlasAsync(full);
            return;
        }

        FilterSummary = string.Create(CultureInfo.InvariantCulture, $"filter: {filter.Describe()} — {members.Length:N0} sounds");
        _navVersion++;
        _placed = null;
        Nodes = null;
        Edges = null;
        SelectedIndex = -1;
        if (members.Length == 0)
        {
            _atlas = LibraryAtlas.Build(full.Library, []);
            _family = _atlas.Root;
            _children = [];
            Crumbs.Clear();
            GraphMessage = "No sounds match the filter.";
            return;
        }

        GraphMessage = string.Create(CultureInfo.InvariantCulture, $"Grouping {members.Length:N0} sounds…");
        LibraryAtlas atlas = await Task.Run(() =>
        {
            LibraryAtlas built = LibraryAtlas.Build(full.Library, members);
            built.Children(built.Root);
            return built;
        });
        if (Stale())
        {
            return;
        }

        await ShowAtlasAsync(atlas);
    }

    private async Task ShowAtlasAsync(LibraryAtlas atlas)
    {
        _atlas = atlas;
        _placed = null;
        await ShowFamilyAsync(atlas.Root, [new Crumb("all", atlas.Root)]);
    }
```

- [ ] **Step 5: Placement with a filter**

In `PlaceAsync`, after the `if (_atlas is not { } atlas) { … }` block add:

```csharp
        if (atlas.Root.Size == 0)
        {
            _status("No sounds match the filter.");
            return;
        }
```

and replace its final `_status(...)` line with:

```csharp
        string outside = !_filter.IsEmpty && !_filter.Matches(SoundTraits.Of(item.Print, item.Sound.Patch))
            ? " Placed among the filtered sounds; it does not match the filter itself."
            : string.Empty;
        _status($"{item.Name} placed among {_children.Count} {(path[^1].Size <= LibraryAtlas.Branching ? "sounds" : "families")} like it; one level up shows the neighbouring families.{outside}");
```

- [ ] **Step 6: View**

In `LibraryView.axaml`:

1. Add styles as the first child of the `UserControl`:

```xml
  <UserControl.Styles>
    <Style Selector="ToggleButton.empty">
      <Setter Property="Opacity" Value="0.45" />
    </Style>
  </UserControl.Styles>
```

2. Graph card grid: `RowDefinitions="Auto,Auto,*"` becomes `RowDefinitions="Auto,Auto,Auto,*"`; the header grid's `ColumnDefinitions="Auto,Auto,*,Auto"` becomes `ColumnDefinitions="Auto,Auto,*,Auto,Auto"`; the Import button moves to `Grid.Column="4"`; the graph `Grid` (imports list + graph) moves to `Grid.Row="3"`.

3. Insert before the Import button:

```xml
          <Button Grid.Column="3" Content="Filter ▾" IsEnabled="{Binding FilterRows.Count}" ToolTip.Tip="Show only some sounds: values in a row are alternatives, rows must all hold">
            <Button.Flyout>
              <Flyout Placement="BottomEdgeAlignedRight">
                <ItemsControl ItemsSource="{Binding FilterRows}" MaxWidth="480">
                  <ItemsControl.ItemTemplate>
                    <DataTemplate x:DataType="local:TraitRow">
                      <StackPanel Spacing="3" Margin="0,0,0,8">
                        <TextBlock Text="{Binding Title}" Classes="muted" FontSize="10" />
                        <ItemsControl ItemsSource="{Binding Chips}">
                          <ItemsControl.ItemsPanel>
                            <ItemsPanelTemplate><WrapPanel Orientation="Horizontal" /></ItemsPanelTemplate>
                          </ItemsControl.ItemsPanel>
                          <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="local:TraitChip">
                              <ToggleButton Content="{Binding Text}" IsChecked="{Binding IsChosen, Mode=TwoWay}" Classes.empty="{Binding IsEmpty}" Padding="8,2" FontSize="11" Margin="0,0,4,4" />
                            </DataTemplate>
                          </ItemsControl.ItemTemplate>
                        </ItemsControl>
                      </StackPanel>
                    </DataTemplate>
                  </ItemsControl.ItemTemplate>
                </ItemsControl>
              </Flyout>
            </Button.Flyout>
          </Button>
```

4. Insert after the breadcrumb `ItemsControl`:

```xml
        <StackPanel Grid.Row="2" Orientation="Horizontal" Spacing="6" IsVisible="{Binding HasFilter}">
          <TextBlock Text="{Binding FilterSummary}" FontSize="11" Foreground="{StaticResource AccentBrush}" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
          <Button Content="Clear" Click="OnClearFilter" Padding="6,2" FontSize="11" Background="Transparent" Foreground="{StaticResource AccentBrush}" />
        </StackPanel>
```

In `LibraryView.axaml.cs`, after `OnShowInSearch`:

```csharp
    private void OnClearFilter(object? sender, RoutedEventArgs e) => ViewModel.ClearFilter();
```

- [ ] **Step 7: Build and check**

Run: `dotnet build HearSay.sln -c Debug 2>&1 | tail -3`
Expected: `0 Warning(s)`, `0 Error(s)`.

Check by hand (or with the scratch headless harness, `library` mode): open the Library tab on FMX6; Filter ▾ shows rows
Waveform, Filter, Brightness, Envelope, Noise, Audible operators, Feedback with counts; choose saw + low-pass: after
about half a second the summary reads `filter: saw · low-pass — N sounds`, the graph says `Grouping N sounds…`, then
shows about 40 nodes whose tooltips mention saw where it is the loudest carrier; Clear returns to the full tree at once;
FM7 shows no Waveform and Filter rows.

- [ ] **Step 8: Manual**

In `docs/expert-manual.md`, in "### The Library tab", add after the **Import folder…** bullet:

```markdown
- **Filter ▾** shows only some sounds: by the waveforms and filter types of the operators you can hear, brightness,
  envelope, noise, the number of audible operators and feedback. Values in a row are alternatives, rows must all hold
  (*saw or square*, and *low-pass*). Each chip shows how many sounds match the rest of the filter and have its value.
  The matching sounds are grouped into families of their own (a few seconds), so the screen stays full; **Clear**
  returns to all sounds. An imported sound is placed among the filtered sounds.
```

- [ ] **Step 9: Run the fast suites and commit**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests && dotnet test tests/HearSay.Synthesis.Tests`
Expected: PASS, 0 failed.

```bash
git add src/HearSay.Studio/FilterChips.cs src/HearSay.Studio/LibraryViewModel.cs src/HearSay.Studio/LibraryView.axaml src/HearSay.Studio/LibraryView.axaml.cs docs/expert-manual.md
git commit -m "Studio: filter the Library tab by waveform, filter type, sound words and structure

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
