# Library Browser Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Library tab in HearSay Studio that shows the structure library of a profile as a zoomable tree of sound
families drawn as a graph, auditions and exports any sound, and places imported exported sounds among their
neighbours.

**Architecture:** The tree, its layout and the descriptors live in `HearSay.OperatorMatrix/Priors` without UI and are
unit-tested (`LibraryAtlas`, `AtlasLayout`, `SoundDescriptors`). Reading exported folders and saving library sounds
live in `HearSay` (`ExportedSounds`, `LibrarySoundWriter`). The Studio gets a `LibraryViewModel`, a `LibraryView`
user control with a custom-drawn `LibraryGraph`, a reusable `PatchView` and a shared `NotePlayer`.

**Tech Stack:** C# / .NET 10, Avalonia 11.3.22, xUnit 2.9.3. No new packages.

**Spec:** `docs/superpowers/specs/2026-10-16-library-browser-design.md`

## Global Constraints

- Branching: k = 40 children per family; a family of 40 or fewer members has its members as leaves.
- k-means: k-means++ seeded from the family's path, at most 15 iterations, final step assigns every member to its nearest final centroid.
- Children ordered by size, largest first, ties by medoid index.
- Atlas cache file: `<library file name without .bin>-atlas-v1.bin` next to the library file; only the root's children are cached.
- Reference request of the library and of imports: 220 Hz, 1 s note, 0.3 s release (`StructureLibrary.ReferenceRequest`).
- Descriptors joined with ` · `: brightness (*dark* < 2, *mellow* 2–4, *bright* 4–8, *very bright* ≥ 8 times the note), envelope (*percussive* ≤ −18 dB, *decaying* ≤ −6 dB, *sustained* ≤ +6 dB, *swelling* above), waveform of the loudest carrier when not a sine, *filtered*, *noisy*.
- Layout: classical MDS, up to 200 overlap-removal passes, edges to the 2 nearest siblings, unit square, deterministic.
- Copy header: `HearSay library <profile name> v<library version>, seed <seed>.`
- Save writes `patch.json`, `patch.scd`, `preview.wav`, `search-summary.json` = `{"searchSummary":{"profile":"<name>"}}`.
- Show in Search title: `from the library: <FM7|FMX6|Nome6> #<seed>` (imports: their name).
- `TreatWarningsAsErrors` is on and `HearSay.OperatorMatrix` and `HearSay` generate documentation files: every public member needs an XML doc comment.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A library whose fingerprints contain many exact duplicates (silent or near-silent patches): splitting must end and every family must stay openable without an exception (pinned in Task 3, `IdenticalFingerprints_BecomeLeaves`, and Task 5, `IdenticalPrints_AreSpreadWithoutOverlap`).
2. Switching profile while a library is still building or loading: the late result of the old profile must not replace the new profile's view (version counter in Task 9; checked by hand in Task 9 step 6).
3. An imported patch whose `search-summary.json` names a profile that cannot play it, or names no known profile: fall back to the smallest compatible profile (pinned in Task 6, `Scan_FallsBackWhenTheSummaryProfileCannotPlayThePatch`).
4. A family where `Locate` and the k-means partition could disagree (iteration cap reached before convergence): child centroids are the final centroids used for the last assignment, so a member's own fingerprint always descends into its own family (pinned in Task 3, `Locate_OfAMember_FollowsItsFamilies`).
5. Clicking nodes faster than they render: only the latest click is heard (latest-wins version counter in `NotePlayer`, Task 7; checked by hand in Task 9 step 6).

---

### Task 1: StructureLibrary exposes what it holds

**Files:**
- Modify: `src/HearSay.OperatorMatrix/Priors/StructureLibrary.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/StructureLibraryTests.cs`

**Interfaces:**
- Produces:
  - `public static RenderRequest StructureLibrary.ReferenceRequest { get; }` — `RenderRequest.Create(220, 1.0, 0.3)`.
  - `public ReadOnlySpan<byte> Fingerprint(int index)`
  - `public int Seed(int index)`
  - `public static string FileOf(SynthesisProfile profile, int size, string? directory = null)`
  - `public static StructureLibrary For(SynthesisProfile profile, int size, IOperatorMatrixRenderer renderer, string? directory = null, IProgress<double>? progress = null)`
  - `public static StructureLibrary Build(SynthesisCapabilities capabilities, int size, IOperatorMatrixRenderer renderer, IProgress<double>? progress = null)`
  - `internal static StructureLibrary FromFingerprints(SynthesisCapabilities capabilities, int[] seeds, byte[] prints)` (tests only)

- [ ] **Step 1: Write the failing tests**

Add to `StructureLibraryTests` (add `using System.Collections.Concurrent;` at the top):

```csharp
    private sealed class Recorder : IProgress<double>
    {
        public ConcurrentBag<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }

    [Fact]
    public void Library_GivesItsFingerprintsAndSeedsByIndex_AndReportsBuildProgress()
    {
        var recorder = new Recorder();
        StructureLibrary library = StructureLibrary.Build(Fm7, 300, Renderer, recorder);
        foreach (int i in new[] { 0, 7, 299 })
        {
            byte[] expected = Print(library.Patch(library.Seed(i)), StructureLibrary.ReferenceHz);
            Assert.Equal(expected, library.Fingerprint(i).ToArray());
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => library.Fingerprint(300).Length);
        Assert.NotEmpty(recorder.Values);
        Assert.All(recorder.Values, static v => Assert.InRange(v, 0.0, 1.0));
        Assert.Contains(1.0, recorder.Values);
        Assert.Equal(RenderRequest.Create(StructureLibrary.ReferenceHz, 1.0, 0.3), StructureLibrary.ReferenceRequest);
    }
```

In `Library_IsSavedAndLoaded`, after `string file = Assert.Single(Directory.GetFiles(directory));` add:

```csharp
            Assert.Equal(StructureLibrary.FileOf(profile, 50, directory), file);
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~StructureLibraryTests"`
Expected: build FAIL with `CS1061`/`CS0117` for `Seed`, `Fingerprint`, `ReferenceRequest`, `FileOf` and the `Build` overload.

- [ ] **Step 3: Implement**

In `StructureLibrary.cs`:

```csharp
    /// <summary>The request every library patch is rendered and fingerprinted with: the reference note, held 1 s, released 0.3 s.</summary>
    public static RenderRequest ReferenceRequest => RenderRequest.Create(ReferenceHz, 1.0, 0.3);

    /// <summary>The fingerprint of the patch at an index.</summary>
    /// <param name="index">Index, 0 to <see cref="Count"/> - 1.</param>
    public ReadOnlySpan<byte> Fingerprint(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return _prints.AsSpan(index * SpectralFingerprint.Length, SpectralFingerprint.Length);
    }

    /// <summary>The seed of the patch at an index; <see cref="Patch"/> regenerates the patch from it.</summary>
    /// <param name="index">Index, 0 to <see cref="Count"/> - 1.</param>
    public int Seed(int index) => _seeds[index];

    /// <summary>The file a library of a profile and size is kept in.</summary>
    /// <param name="profile">Synthesis profile.</param>
    /// <param name="size">Number of patches.</param>
    /// <param name="directory">Folder of the library files; the user's local application data when null.</param>
    public static string FileOf(SynthesisProfile profile, int size, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return PathOf(profile, size, directory);
    }

    /// <summary>A library from given seeds and fingerprints, for tests.</summary>
    internal static StructureLibrary FromFingerprints(SynthesisCapabilities capabilities, int[] seeds, byte[] prints) => new(capabilities, seeds, prints);
```

Change `For` to take `IProgress<double>? progress = null` as its last parameter (document it: "Fraction of the patches rendered while the library is built; not called when it is loaded.") and pass it on: `LoadOrBuild(profile.Capabilities, profile.StructureLibraryVersion, size, renderer, path, progress)`. Give `LoadOrBuild` the same parameter and call `Build(capabilities, size, renderer, progress)`.

Change `Build`:

```csharp
    /// <summary>Builds a library in memory without touching the disk.</summary>
    /// <param name="capabilities">Capabilities the patches are generated within.</param>
    /// <param name="size">Number of patches.</param>
    /// <param name="renderer">Renderer that plays them.</param>
    /// <param name="progress">Fraction of the patches rendered, about a hundred times and 1 at the end.</param>
    public static StructureLibrary Build(SynthesisCapabilities capabilities, int size, IOperatorMatrixRenderer renderer, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(renderer);
        var seeds = new int[size];
        var prints = new byte[size * SpectralFingerprint.Length];
        int done = 0;
        int step = Math.Max(1, size / 100);
        Parallel.For(0, size, i =>
        {
            int seed = FirstSeed + i;
            OperatorMatrixPatch patch = Generate(capabilities, seed);
            RenderedAudio audio = renderer.Render(patch, ReferenceRequest);
            seeds[i] = seed;
            SpectralFingerprint.Of(audio.Samples, audio.SampleRate, ReferenceHz).CopyTo(prints, i * SpectralFingerprint.Length);
            int count = Interlocked.Increment(ref done);
            if (count % step == 0 && count < size)
            {
                progress?.Report((double)count / size);
            }
        });
        progress?.Report(1.0);
        return new StructureLibrary(capabilities, seeds, prints);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~StructureLibraryTests"`
Expected: PASS, all StructureLibraryTests.

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/StructureLibrary.cs tests/HearSay.OperatorMatrix.Tests/StructureLibraryTests.cs
git commit -m "Library: fingerprints and seeds by index, its file name, its reference request and build progress"
```

---

### Task 2: SoundDescriptors

**Files:**
- Create: `src/HearSay.OperatorMatrix/Priors/SoundDescriptors.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/SoundDescriptorsTests.cs`

**Interfaces:**
- Consumes: `SpectralFingerprint.Bands` (96), `SpectralFingerprint.LowestRatio` (0.25), `SpectralFingerprint.Length` (288).
- Produces:
  - `public static double SoundDescriptors.BrightnessRatio(ReadOnlySpan<byte> print)`
  - `public static double SoundDescriptors.DecayDb(ReadOnlySpan<byte> print)`
  - `public static IReadOnlyList<string> SoundDescriptors.Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)`
  - `public static string SoundDescriptors.Label(ReadOnlySpan<byte> print, OperatorMatrixPatch patch, int size)`

- [ ] **Step 1: Write the failing tests**

```csharp
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Tests;

public sealed class SoundDescriptorsTests
{
    private const byte Floor = 240;

    /// <summary>A fingerprint with one loud band per slice; the attack and decay slices get their own level.</summary>
    private static byte[] Print(int band, byte attack = 0, byte body = 0, byte decay = 0)
    {
        var print = new byte[SpectralFingerprint.Length];
        Array.Fill(print, Floor);
        print[band] = attack;
        print[SpectralFingerprint.Bands + band] = body;
        print[(2 * SpectralFingerprint.Bands) + band] = decay;
        return print;
    }

    private static OperatorMatrixPatch Carrier(OperatorWaveform waveform = OperatorWaveform.Sine, OperatorFilter? filter = null) =>
        OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = waveform, Filter = filter })
            .WithOutputLevel(0, 1.0);

    [Theory]
    [InlineData(30, "dark")]
    [InlineData(42, "mellow")]
    [InlineData(54, "bright")]
    [InlineData(66, "very bright")]
    public void Brightness_FollowsTheBodySlice(int band, string word)
    {
        Assert.Equal(word, SoundDescriptors.Of(Print(band), Carrier())[0]);
        Assert.Equal(0.25 * Math.Pow(2, band / 12.0), SoundDescriptors.BrightnessRatio(Print(band)), 0.02);
    }

    [Theory]
    [InlineData(0, 100, "percussive")]
    [InlineData(0, 40, "decaying")]
    [InlineData(0, 0, "sustained")]
    [InlineData(40, 0, "swelling")]
    public void Envelope_ComparesTheDecayWithTheAttack(byte attack, byte decay, string word) =>
        Assert.Equal(word, SoundDescriptors.Of(Print(30, attack, 0, decay), Carrier())[1]);

    [Fact]
    public void Patch_AddsWaveformFilterAndNoise_OfWhatCanBeHeard()
    {
        byte[] print = Print(30);
        Assert.Equal(new[] { "dark", "sustained" }, SoundDescriptors.Of(print, Carrier()));
        Assert.Equal(new[] { "dark", "sustained", "saw", "filtered" }, SoundDescriptors.Of(print, Carrier(OperatorWaveform.Saw, OperatorFilter.LowPass(4))));
        Assert.Contains("noisy", SoundDescriptors.Of(print, Carrier(OperatorWaveform.Noise)));

        // OP2 cannot be heard: its waveform and filter do not count.
        OperatorMatrixPatch silentSaw = Carrier().WithOperator(1, OperatorPatch.Sine(OperatorFrequency.AtRatio(2)) with { Waveform = OperatorWaveform.Saw, Filter = OperatorFilter.LowPass(4) });
        Assert.Equal(new[] { "dark", "sustained" }, SoundDescriptors.Of(print, silentSaw));
    }

    [Fact]
    public void Label_JoinsTheWordsAndTheSize()
    {
        Assert.Equal("dark · sustained · 1 sound", SoundDescriptors.Label(Print(30), Carrier(), 1));
        Assert.Equal("dark · sustained · 7,412 sounds", SoundDescriptors.Label(Print(30), Carrier(), 7412));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~SoundDescriptorsTests"`
Expected: build FAIL, `SoundDescriptors` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Globalization;
using HearSay.Synthesis.Patches;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// A few words for a sound, from its <see cref="SpectralFingerprint"/> and its patch: brightness, envelope, the waveform
/// of the loudest carrier when it is not a sine, and whether a filter or noise can be heard. For labels in the library
/// browser.
/// </summary>
public static class SoundDescriptors
{
    private const int Bands = SpectralFingerprint.Bands;

    /// <summary>Power-weighted mean frequency of the body slice, as a multiple of the note.</summary>
    /// <param name="print">Fingerprint.</param>
    public static double BrightnessRatio(ReadOnlySpan<byte> print)
    {
        CheckLength(print);
        double sum = 0, weighted = 0;
        for (int b = 0; b < Bands; b++)
        {
            double p = Power(print[Bands + b]);
            sum += p;
            weighted += p * b;
        }

        return SpectralFingerprint.LowestRatio * Math.Pow(2, weighted / sum / 12.0);
    }

    /// <summary>Total power of the decay slice against the attack slice, in decibels; negative when the sound dies away.</summary>
    /// <param name="print">Fingerprint.</param>
    public static double DecayDb(ReadOnlySpan<byte> print)
    {
        CheckLength(print);
        double attack = 0, decay = 0;
        for (int b = 0; b < Bands; b++)
        {
            attack += Power(print[b]);
            decay += Power(print[(2 * Bands) + b]);
        }

        return 10.0 * Math.Log10(decay / attack);
    }

    /// <summary>The words for a sound, in a fixed order.</summary>
    /// <param name="print">Fingerprint of the sound.</param>
    /// <param name="patch">Patch that plays it.</param>
    public static IReadOnlyList<string> Of(ReadOnlySpan<byte> print, OperatorMatrixPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var words = new List<string>(5);
        double ratio = BrightnessRatio(print);
        words.Add(ratio < 2 ? "dark" : ratio < 4 ? "mellow" : ratio < 8 ? "bright" : "very bright");
        double decay = DecayDb(print);
        words.Add(decay <= -18 ? "percussive" : decay <= -6 ? "decaying" : decay <= 6 ? "sustained" : "swelling");

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

        IEnumerable<OperatorPatch> heard = Enumerable.Range(0, patch.OperatorCount).Where(i => audible[i]).Select(i => patch.Operators[i]);
        if (heard.Any(static o => o.Filter is not null))
        {
            words.Add("filtered");
        }

        if (heard.Any(static o => IsNoise(o.Waveform) || o.NoisePhase != 0.0 || o.NoiseM != 0.0))
        {
            words.Add("noisy");
        }

        return words;
    }

    /// <summary>The words joined with a middle dot, and the number of sounds.</summary>
    /// <param name="print">Fingerprint of the sound.</param>
    /// <param name="patch">Patch that plays it.</param>
    /// <param name="size">Number of sounds it stands for.</param>
    public static string Label(ReadOnlySpan<byte> print, OperatorMatrixPatch patch, int size) =>
        string.Join(" · ", Of(print, patch)) + " · " + (size == 1 ? "1 sound" : size.ToString("N0", CultureInfo.InvariantCulture) + " sounds");

    private static double Power(byte value) => Math.Pow(10.0, -value / 40.0);

    private static bool IsNoise(OperatorWaveform waveform) => waveform is OperatorWaveform.Noise or OperatorWaveform.SampleHoldNoise or OperatorWaveform.FilteredNoise;

    private static string WaveformWord(OperatorWaveform waveform) => waveform switch
    {
        OperatorWaveform.SampleHoldNoise => "sample-and-hold noise",
        OperatorWaveform.FilteredNoise => "filtered noise",
        _ => waveform.ToString().ToLowerInvariant(),
    };

    private static void CheckLength(ReadOnlySpan<byte> print)
    {
        if (print.Length != SpectralFingerprint.Length)
        {
            throw new ArgumentException($"A fingerprint has {SpectralFingerprint.Length} values.", nameof(print));
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~SoundDescriptorsTests"`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/SoundDescriptors.cs tests/HearSay.OperatorMatrix.Tests/SoundDescriptorsTests.cs
git commit -m "Descriptors: a few words for a sound from its fingerprint and patch"
```

---

### Task 3: LibraryAtlas — the family tree and Locate

**Files:**
- Create: `src/HearSay.OperatorMatrix/Priors/LibraryAtlas.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/LibraryAtlasTests.cs`

**Interfaces:**
- Consumes: Task 1 `Fingerprint(int)`, `Seed(int)`, `Count`, `FromFingerprints(...)`.
- Produces:
  - `public sealed class LibraryFamily` with `IReadOnlyList<int> Path`, `IReadOnlyList<int> Members` (ascending library indices), `IReadOnlyList<double> Centroid`, `int Medoid`, `int Size`, `bool IsLeaf`.
  - `public sealed class LibraryAtlas` with `const int Branching = 40`, `StructureLibrary Library`, `LibraryFamily Root`, `static LibraryAtlas Build(StructureLibrary library)`, `IReadOnlyList<LibraryFamily> Children(LibraryFamily family)`, `IReadOnlyList<LibraryFamily> Locate(ReadOnlySpan<byte> fingerprint)`.
  - Internal for Task 4: `static LibraryFamily RootOf(StructureLibrary)`, `static IReadOnlyList<LibraryFamily> Split(StructureLibrary, LibraryFamily)`, constructor `LibraryAtlas(StructureLibrary, LibraryFamily)`, `ConcurrentDictionary<LibraryFamily, Lazy<IReadOnlyList<LibraryFamily>>> ChildrenCache`, `LibraryFamily(int[] path, int[] members, double[] centroid, int medoid)`, `int[] MemberArray`, `double[] CentroidArray`.

- [ ] **Step 1: Write the failing tests**

```csharp
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Profiles;

namespace HearSay.OperatorMatrix.Tests;

public sealed class LibraryAtlasTests
{
    private static readonly SynthesisCapabilities Fm7 = SynthesisProfiles.SuperColliderFm7.Capabilities;
    internal static readonly Lazy<StructureLibrary> Library = new(() => StructureLibrary.Build(Fm7, 3000, new Fm7CompatibleOperatorMatrixRenderer()));
    internal static readonly Lazy<LibraryAtlas> Atlas = new(() => LibraryAtlas.Build(Library.Value));

    private static void AssertPartition(LibraryAtlas atlas, LibraryFamily family)
    {
        IReadOnlyList<LibraryFamily> children = atlas.Children(family);
        Assert.InRange(children.Count, 1, LibraryAtlas.Branching);
        Assert.Equal(family.Members, children.SelectMany(static c => c.Members).Order());
        for (int k = 0; k < children.Count; k++)
        {
            LibraryFamily child = children[k];
            Assert.Contains(child.Medoid, child.Members);
            Assert.Equal(SpectralFingerprint.Length, child.Centroid.Count);
            Assert.Equal(family.Path.Append(k), child.Path);
            Assert.Equal(child.Members.Order(), child.Members);
            if (k > 0)
            {
                Assert.True(children[k - 1].Size > child.Size || (children[k - 1].Size == child.Size && children[k - 1].Medoid < child.Medoid));
            }
        }
    }

    [Fact]
    public void Children_PartitionTheirParent()
    {
        LibraryAtlas atlas = Atlas.Value;
        Assert.Equal(Enumerable.Range(0, 3000), atlas.Root.Members);
        AssertPartition(atlas, atlas.Root);
        Assert.Equal(LibraryAtlas.Branching, atlas.Children(atlas.Root).Count);
        LibraryFamily largest = atlas.Children(atlas.Root)[0];
        Assert.True(largest.Size > LibraryAtlas.Branching, "the 3000 sounds should not split into 40 families of 40 or fewer");
        AssertPartition(atlas, largest);
    }

    [Fact]
    public void SmallFamilies_HaveTheirMembersAsLeaves()
    {
        LibraryAtlas atlas = Atlas.Value;
        LibraryFamily small = atlas.Children(atlas.Root).First(static c => c.Size is > 1 and <= LibraryAtlas.Branching);
        IReadOnlyList<LibraryFamily> leaves = atlas.Children(small);
        Assert.All(leaves, static l => Assert.True(l.IsLeaf));
        Assert.Equal(small.Members, leaves.Select(static l => l.Medoid));
        Assert.Empty(atlas.Children(leaves[0]));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        LibraryAtlas a = Atlas.Value, b = LibraryAtlas.Build(Library.Value);
        IReadOnlyList<LibraryFamily> ca = a.Children(a.Root), cb = b.Children(b.Root);
        Assert.Equal(ca.Select(static c => c.Medoid), cb.Select(static c => c.Medoid));
        Assert.Equal(ca.Select(static c => c.Size), cb.Select(static c => c.Size));
        Assert.Equal(a.Children(ca[0]).Select(static c => c.Medoid), b.Children(cb[0]).Select(static c => c.Medoid));
    }

    [Fact]
    public void Locate_OfAMember_FollowsItsFamilies()
    {
        LibraryAtlas atlas = Atlas.Value;
        for (int i = 0; i < 3000; i += 97)
        {
            IReadOnlyList<LibraryFamily> path = atlas.Locate(atlas.Library.Fingerprint(i));
            Assert.Same(atlas.Root, path[0]);
            for (int k = 0; k < path.Count; k++)
            {
                Assert.Contains(i, path[k].Members);
                if (k > 0)
                {
                    Assert.Contains(path[k], atlas.Children(path[k - 1]));
                }
            }

            // The last family is the one whose children the sound is shown among.
            Assert.Contains(atlas.Children(path[^1]), c => (c.IsLeaf && c.Medoid == i) || (!c.IsLeaf && c.Members.Contains(i)));
        }
    }

    [Fact]
    public void IdenticalFingerprints_BecomeLeaves()
    {
        const int n = 100;
        byte[] one = Library.Value.Fingerprint(0).ToArray();
        var prints = new byte[n * SpectralFingerprint.Length];
        for (int i = 0; i < n; i++)
        {
            one.CopyTo(prints, i * SpectralFingerprint.Length);
        }

        StructureLibrary same = StructureLibrary.FromFingerprints(Fm7, [.. Enumerable.Range(1, n)], prints);
        LibraryAtlas atlas = LibraryAtlas.Build(same);
        IReadOnlyList<LibraryFamily> children = atlas.Children(atlas.Root);
        Assert.Equal(LibraryAtlas.Branching, children.Count);
        Assert.All(children, static c => Assert.True(c.IsLeaf));
        Assert.Equal(Enumerable.Range(0, LibraryAtlas.Branching), children.Select(static c => c.Medoid));
        Assert.Equal(new[] { atlas.Root }, atlas.Locate(one));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~LibraryAtlasTests"`
Expected: build FAIL, `LibraryAtlas` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Collections.Concurrent;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// A family of library sounds: a set of library indices, the centroid of their fingerprints, and the medoid, the member
/// nearest that centroid, whose sound stands for the family.
/// </summary>
public sealed class LibraryFamily
{
    internal LibraryFamily(int[] path, int[] members, double[] centroid, int medoid)
    {
        PathArray = path;
        MemberArray = members;
        CentroidArray = centroid;
        Medoid = medoid;
    }

    /// <summary>Positions among the children from the root down; empty for the root.</summary>
    public IReadOnlyList<int> Path => PathArray;

    /// <summary>Library indices of the members, ascending.</summary>
    public IReadOnlyList<int> Members => MemberArray;

    /// <summary>Centroid of the members' fingerprints, in quarter-decibel steps.</summary>
    public IReadOnlyList<double> Centroid => CentroidArray;

    /// <summary>Library index of the member whose sound stands for the family.</summary>
    public int Medoid { get; }

    /// <summary>Number of members.</summary>
    public int Size => MemberArray.Length;

    /// <summary>True for a single sound.</summary>
    public bool IsLeaf => MemberArray.Length == 1;

    internal int[] PathArray { get; }

    internal int[] MemberArray { get; }

    internal double[] CentroidArray { get; }
}

/// <summary>
/// The sounds of a <see cref="StructureLibrary"/> as a tree of families for browsing: the root holds every sound, a
/// family of more than <see cref="Branching"/> members is split by k-means on the fingerprints into at most that many
/// children, a smaller family has its members as leaves. Children are computed when they are first asked for and kept.
/// Deterministic: k-means is seeded from the family's path. Thread-safe.
/// </summary>
public sealed class LibraryAtlas
{
    /// <summary>Most children of a family.</summary>
    public const int Branching = 40;

    private const int Iterations = 15;

    internal LibraryAtlas(StructureLibrary library, LibraryFamily root)
    {
        Library = library;
        Root = root;
    }

    /// <summary>The library the tree is built on.</summary>
    public StructureLibrary Library { get; }

    /// <summary>The family of all sounds.</summary>
    public LibraryFamily Root { get; }

    internal ConcurrentDictionary<LibraryFamily, Lazy<IReadOnlyList<LibraryFamily>>> ChildrenCache { get; } = new();

    /// <summary>The tree of a library, in memory; nothing is split until it is asked for.</summary>
    /// <param name="library">Library.</param>
    public static LibraryAtlas Build(StructureLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return new LibraryAtlas(library, RootOf(library));
    }

    /// <summary>The children of a family, largest first; empty for a leaf.</summary>
    /// <param name="family">Family of this atlas.</param>
    public IReadOnlyList<LibraryFamily> Children(LibraryFamily family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return ChildrenCache.GetOrAdd(family, f => new Lazy<IReadOnlyList<LibraryFamily>>(() => Split(Library, f))).Value;
    }

    /// <summary>
    /// The families a sound falls in, from the root down: at every level the child with the nearest centroid, until the
    /// family whose children are single sounds. The sound belongs among the children of the last family.
    /// </summary>
    /// <param name="fingerprint">Fingerprint of the sound, rendered with <see cref="StructureLibrary.ReferenceRequest"/>.</param>
    public IReadOnlyList<LibraryFamily> Locate(ReadOnlySpan<byte> fingerprint)
    {
        if (fingerprint.Length != SpectralFingerprint.Length)
        {
            throw new ArgumentException($"A fingerprint has {SpectralFingerprint.Length} values.", nameof(fingerprint));
        }

        var path = new List<LibraryFamily> { Root };
        LibraryFamily current = Root;
        while (true)
        {
            IReadOnlyList<LibraryFamily> children = Children(current);
            if (children.All(static c => c.IsLeaf))
            {
                return path;
            }

            int best = 0;
            double bestDistance = double.PositiveInfinity;
            for (int k = 0; k < children.Count; k++)
            {
                double d = Distance(fingerprint, children[k].CentroidArray);
                if (d < bestDistance)
                {
                    (best, bestDistance) = (k, d);
                }
            }

            if (children[best].IsLeaf)
            {
                return path;
            }

            current = children[best];
            path.Add(current);
        }
    }

    internal static LibraryFamily RootOf(StructureLibrary library)
    {
        int[] members = [.. Enumerable.Range(0, library.Count)];
        double[] centroid = Mean(library, members);
        return new LibraryFamily([], members, centroid, MedoidOf(library, members, centroid));
    }

    internal static IReadOnlyList<LibraryFamily> Split(StructureLibrary library, LibraryFamily family)
    {
        int[] members = family.MemberArray;
        if (members.Length <= 1)
        {
            return [];
        }

        if (members.Length <= Branching)
        {
            return Leaves(library, family, members);
        }

        (int[][] clusters, double[][] centroids) = KMeans(library, members, SeedOf(family.PathArray));
        if (clusters.Length <= 1)
        {
            // Nothing to separate (identical fingerprints): the first sounds stand for the rest.
            return Leaves(library, family, members[..Branching]);
        }

        var children = clusters
            .Select((c, j) => (Members: c, Centroid: centroids[j], Medoid: MedoidOf(library, c, centroids[j])))
            .OrderByDescending(static x => x.Members.Length)
            .ThenBy(static x => x.Medoid)
            .ToList();
        return [.. children.Select((x, k) => new LibraryFamily([.. family.PathArray, k], x.Members, x.Centroid, x.Medoid))];
    }

    private static IReadOnlyList<LibraryFamily> Leaves(StructureLibrary library, LibraryFamily parent, int[] members) =>
        [.. members.Select((m, k) => new LibraryFamily([.. parent.PathArray, k], [m], ToDoubles(library.Fingerprint(m)), m))];

    /// <summary>
    /// k-means with k-means++ seeding; the last step assigns every member to its nearest final centroid, so that the
    /// returned centroids and clusters agree. Empty clusters are dropped; members stay in ascending order.
    /// </summary>
    private static (int[][] Clusters, double[][] Centroids) KMeans(StructureLibrary library, int[] members, int seed)
    {
        int n = members.Length;
        int length = SpectralFingerprint.Length;
        var random = new Random(seed);
        var centroids = new List<double[]> { ToDoubles(library.Fingerprint(members[random.Next(n)])) };
        var nearest = new double[n];
        Array.Fill(nearest, double.PositiveInfinity);
        while (centroids.Count < Branching)
        {
            double[] latest = centroids[^1];
            Parallel.For(0, n, i => nearest[i] = Math.Min(nearest[i], Distance(library.Fingerprint(members[i]), latest)));
            double total = 0;
            foreach (double d in nearest)
            {
                total += d;
            }

            if (total <= 0)
            {
                break;
            }

            double r = random.NextDouble() * total;
            int pick = n - 1;
            for (int i = 0; i < n; i++)
            {
                r -= nearest[i];
                if (r < 0)
                {
                    pick = i;
                    break;
                }
            }

            centroids.Add(ToDoubles(library.Fingerprint(members[pick])));
        }

        double[][] c = [.. centroids];
        int k = c.Length;
        var assignment = new int[n];
        for (int iteration = 0; ; iteration++)
        {
            Parallel.For(0, n, i => assignment[i] = NearestIndex(library.Fingerprint(members[i]), c));
            if (iteration == Iterations)
            {
                break;
            }

            var sums = new double[k][];
            var counts = new int[k];
            for (int j = 0; j < k; j++)
            {
                sums[j] = new double[length];
            }

            for (int i = 0; i < n; i++)
            {
                int j = assignment[i];
                counts[j]++;
                ReadOnlySpan<byte> print = library.Fingerprint(members[i]);
                for (int b = 0; b < length; b++)
                {
                    sums[j][b] += print[b];
                }
            }

            bool moved = false;
            for (int j = 0; j < k; j++)
            {
                if (counts[j] == 0)
                {
                    continue;
                }

                for (int b = 0; b < length; b++)
                {
                    double value = sums[j][b] / counts[j];
                    moved |= value != c[j][b];
                    c[j][b] = value;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        var groups = new List<int>[k];
        for (int j = 0; j < k; j++)
        {
            groups[j] = [];
        }

        for (int i = 0; i < n; i++)
        {
            groups[assignment[i]].Add(members[i]);
        }

        int[] used = [.. Enumerable.Range(0, k).Where(j => groups[j].Count > 0)];
        return ([.. used.Select(j => groups[j].ToArray())], [.. used.Select(j => c[j])]);
    }

    private static int NearestIndex(ReadOnlySpan<byte> print, double[][] centroids)
    {
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int j = 0; j < centroids.Length; j++)
        {
            double d = Distance(print, centroids[j]);
            if (d < bestDistance)
            {
                (best, bestDistance) = (j, d);
            }
        }

        return best;
    }

    private static double[] Mean(StructureLibrary library, int[] members)
    {
        var mean = new double[SpectralFingerprint.Length];
        foreach (int m in members)
        {
            ReadOnlySpan<byte> print = library.Fingerprint(m);
            for (int b = 0; b < mean.Length; b++)
            {
                mean[b] += print[b];
            }
        }

        for (int b = 0; b < mean.Length; b++)
        {
            mean[b] /= Math.Max(1, members.Length);
        }

        return mean;
    }

    private static int MedoidOf(StructureLibrary library, int[] members, double[] centroid)
    {
        int best = members[0];
        double bestDistance = double.PositiveInfinity;
        foreach (int m in members)
        {
            double d = Distance(library.Fingerprint(m), centroid);
            if (d < bestDistance)
            {
                (best, bestDistance) = (m, d);
            }
        }

        return best;
    }

    private static double Distance(ReadOnlySpan<byte> print, double[] centroid)
    {
        double sum = 0;
        for (int b = 0; b < print.Length; b++)
        {
            double d = print[b] - centroid[b];
            sum += d * d;
        }

        return sum;
    }

    private static double[] ToDoubles(ReadOnlySpan<byte> print)
    {
        var values = new double[print.Length];
        for (int b = 0; b < print.Length; b++)
        {
            values[b] = print[b];
        }

        return values;
    }

    private static int SeedOf(int[] path)
    {
        int seed = 17;
        foreach (int p in path)
        {
            seed = unchecked((seed * 31) + p + 1);
        }

        return seed;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~LibraryAtlasTests"`
Expected: PASS (5 tests). If `Children_PartitionTheirParent`'s "should not split into 40 families of 40 or fewer" fails, the library of 3000 is too evenly spread for the test's assumption: pick the first child with `Size > Branching` instead of `[0]`, and ledger a ruling.

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/LibraryAtlas.cs tests/HearSay.OperatorMatrix.Tests/LibraryAtlasTests.cs
git commit -m "Atlas: the library as a tree of sound families by k-means, and where a sound falls in it"
```

---

### Task 4: LibraryAtlas — top level kept on disk

**Files:**
- Modify: `src/HearSay.OperatorMatrix/Priors/LibraryAtlas.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/LibraryAtlasTests.cs`

**Interfaces:**
- Consumes: Task 3 internals; Task 1 `StructureLibrary.FileOf`.
- Produces:
  - `public static string LibraryAtlas.CacheFileOf(string libraryFile)`
  - `public static LibraryAtlas LibraryAtlas.Open(StructureLibrary library, string cacheFile)`

- [ ] **Step 1: Write the failing tests**

Add to `LibraryAtlasTests`:

```csharp
    private static void AssertSameTopLevel(LibraryAtlas expected, LibraryAtlas actual)
    {
        IReadOnlyList<LibraryFamily> e = expected.Children(expected.Root), a = actual.Children(actual.Root);
        Assert.Equal(e.Count, a.Count);
        for (int k = 0; k < e.Count; k++)
        {
            Assert.Equal(e[k].Members, a[k].Members);
            Assert.Equal(e[k].Medoid, a[k].Medoid);
            Assert.Equal(e[k].Centroid, a[k].Centroid);
            Assert.Equal(e[k].Path, a[k].Path);
        }
    }

    [Fact]
    public void CacheFile_SitsNextToTheLibrary()
    {
        string library = Path.Combine("x", "supercollider-fmx6-v2-300000.bin");
        Assert.Equal(Path.Combine("x", "supercollider-fmx6-v2-300000-atlas-v1.bin"), LibraryAtlas.CacheFileOf(library));
    }

    [Fact]
    public void Open_KeepsTheTopLevelOnDisk_AndRebuildsABrokenFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hearsay-atlas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = LibraryAtlas.CacheFileOf(Path.Combine(directory, "test-v1-3000.bin"));
            LibraryAtlas first = LibraryAtlas.Open(Library.Value, file);
            Assert.True(File.Exists(file));
            AssertSameTopLevel(Atlas.Value, first);

            // Loaded, not rebuilt: the file is left alone.
            DateTime written = File.GetLastWriteTimeUtc(file);
            AssertSameTopLevel(Atlas.Value, LibraryAtlas.Open(Library.Value, file));
            Assert.Equal(written, File.GetLastWriteTimeUtc(file));

            // Truncated: rebuilt and written again in full.
            long length = new FileInfo(file).Length;
            using (FileStream stream = File.Open(file, FileMode.Open))
            {
                stream.SetLength(length / 2);
            }

            AssertSameTopLevel(Atlas.Value, LibraryAtlas.Open(Library.Value, file));
            Assert.Equal(length, new FileInfo(file).Length);

            // Written for a library of another size: rebuilt.
            StructureLibrary other = StructureLibrary.Build(Fm7, 200, new Fm7CompatibleOperatorMatrixRenderer());
            LibraryAtlas.Open(other, file);
            AssertSameTopLevel(Atlas.Value, LibraryAtlas.Open(Library.Value, file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~LibraryAtlasTests"`
Expected: build FAIL, `CacheFileOf` and `Open` do not exist.

- [ ] **Step 3: Implement**

Add to `LibraryAtlas`:

```csharp
    private const int FileVersion = 1;

    /// <summary>The file the top level of a library's tree is kept in, next to the library file.</summary>
    /// <param name="libraryFile">Path of the library file, see <see cref="StructureLibrary.FileOf"/>.</param>
    public static string CacheFileOf(string libraryFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFile);
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(libraryFile) ?? string.Empty, System.IO.Path.GetFileNameWithoutExtension(libraryFile) + "-atlas-v1.bin");
    }

    /// <summary>
    /// The tree of a library with its top level loaded from a file, or split and saved there; a missing, truncated or
    /// mismatching file is rebuilt, and one that cannot be written still serves this process.
    /// </summary>
    /// <param name="library">Library.</param>
    /// <param name="cacheFile">File for the top level, see <see cref="CacheFileOf"/>.</param>
    public static LibraryAtlas Open(StructureLibrary library, string cacheFile)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheFile);
        LibraryFamily root = RootOf(library);
        var atlas = new LibraryAtlas(library, root);
        IReadOnlyList<LibraryFamily>? children = TryLoad(library, cacheFile);
        if (children is null)
        {
            children = Split(library, root);
            TrySave(library, children, cacheFile);
        }

        atlas.ChildrenCache[root] = new Lazy<IReadOnlyList<LibraryFamily>>(children);
        return atlas;
    }

    private static IReadOnlyList<LibraryFamily>? TryLoad(StructureLibrary library, string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var reader = new BinaryReader(File.OpenRead(file));
            int length = SpectralFingerprint.Length;
            if (reader.ReadInt32() != FileVersion || reader.ReadInt32() != library.Count || reader.ReadInt32() != length)
            {
                return null;
            }

            int count = reader.ReadInt32();
            if (count is < 0 or > Branching)
            {
                return null;
            }

            var children = new List<LibraryFamily>(count);
            int total = 0;
            for (int k = 0; k < count; k++)
            {
                int size = reader.ReadInt32();
                if (size <= 0 || size > library.Count - total)
                {
                    return null;
                }

                var members = new int[size];
                for (int i = 0; i < size; i++)
                {
                    members[i] = reader.ReadInt32();
                    if (members[i] < 0 || members[i] >= library.Count)
                    {
                        return null;
                    }
                }

                var centroid = new double[length];
                for (int b = 0; b < length; b++)
                {
                    centroid[b] = reader.ReadDouble();
                }

                int medoid = reader.ReadInt32();
                children.Add(new LibraryFamily([k], members, centroid, medoid));
                total += size;
            }

            return total == library.Count || count == Branching ? children : null;
        }
        catch (IOException)
        {
            // Also a truncated file (EndOfStreamException): it is rebuilt.
            return null;
        }
    }

    private static void TrySave(StructureLibrary library, IReadOnlyList<LibraryFamily> children, string file)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!);
            string temporary = file + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                writer.Write(FileVersion);
                writer.Write(library.Count);
                writer.Write(SpectralFingerprint.Length);
                writer.Write(children.Count);
                foreach (LibraryFamily child in children)
                {
                    writer.Write(child.Size);
                    foreach (int m in child.MemberArray)
                    {
                        writer.Write(m);
                    }

                    foreach (double value in child.CentroidArray)
                    {
                        writer.Write(value);
                    }

                    writer.Write(child.Medoid);
                }
            }

            File.Move(temporary, file, overwrite: true);
        }
        catch (IOException)
        {
            // An atlas that cannot be saved still serves this process.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
```

Note: `total == library.Count || count == Branching` accepts the identical-fingerprint case where only the first 40 members are leaves; a truncated file still fails with `EndOfStreamException` before that check.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~LibraryAtlasTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/LibraryAtlas.cs tests/HearSay.OperatorMatrix.Tests/LibraryAtlasTests.cs
git commit -m "Atlas: the top level of the tree is kept next to the library file"
```

---

### Task 5: AtlasLayout

**Files:**
- Create: `src/HearSay.OperatorMatrix/Priors/AtlasLayout.cs`
- Test: `tests/HearSay.OperatorMatrix.Tests/AtlasLayoutTests.cs`

**Interfaces:**
- Consumes: `SpectralFingerprint.Distance`; in tests Task 3 `LibraryAtlasTests.Atlas`.
- Produces:
  - `public sealed class AtlasLayout` with `IReadOnlyList<(double X, double Y)> Positions`, `IReadOnlyList<double> Radii`, `IReadOnlyList<(int From, int To)> Edges`.
  - `public static AtlasLayout Compute(IReadOnlyList<byte[]> prints, IReadOnlyList<int> sizes)`
  - `public AtlasLayout CenterOn(int index)`

- [ ] **Step 1: Write the failing tests**

```csharp
using HearSay.OperatorMatrix.Priors;

namespace HearSay.OperatorMatrix.Tests;

public sealed class AtlasLayoutTests
{
    private static void AssertTidy(AtlasLayout layout)
    {
        int n = layout.Positions.Count;
        for (int i = 0; i < n; i++)
        {
            (double x, double y) = layout.Positions[i];
            double r = layout.Radii[i];
            Assert.True(double.IsFinite(x) && double.IsFinite(y) && r > 0);
            Assert.InRange(x, r - 1e-9, 1 - r + 1e-9);
            Assert.InRange(y, r - 1e-9, 1 - r + 1e-9);
            for (int j = i + 1; j < n; j++)
            {
                double d = Math.Sqrt(Math.Pow(x - layout.Positions[j].X, 2) + Math.Pow(y - layout.Positions[j].Y, 2));
                Assert.True(d >= r + layout.Radii[j] - 1e-6, $"nodes {i} and {j} overlap");
            }
        }

        Assert.All(layout.Edges, e => Assert.True(e.From < e.To && e.To < n));
        Assert.Equal(layout.Edges.Count, layout.Edges.Distinct().Count());
        if (n > 1)
        {
            Assert.All(Enumerable.Range(0, n), i => Assert.Contains(layout.Edges, e => e.From == i || e.To == i));
        }
    }

    private static (List<byte[]> Prints, List<int> Sizes) TopLevel()
    {
        LibraryAtlas atlas = LibraryAtlasTests.Atlas.Value;
        IReadOnlyList<LibraryFamily> children = atlas.Children(atlas.Root);
        return ([.. children.Select(c => atlas.Library.Fingerprint(c.Medoid).ToArray())], [.. children.Select(static c => c.Size)]);
    }

    [Fact]
    public void Layout_IsTidyAndDeterministic()
    {
        (List<byte[]> prints, List<int> sizes) = TopLevel();
        AtlasLayout a = AtlasLayout.Compute(prints, sizes), b = AtlasLayout.Compute(prints, sizes);
        AssertTidy(a);
        Assert.Equal(a.Positions, b.Positions);
        Assert.Equal(a.Edges, b.Edges);
        Assert.True(a.Radii[0] > a.Radii[^1], "the largest family should be drawn larger than the smallest");
    }

    [Fact]
    public void SimilarSounds_LieCloser()
    {
        (List<byte[]> prints, _) = TopLevel();
        byte[] near = (byte[])prints[0].Clone();
        near[100] = (byte)Math.Min(240, near[100] + 8);
        AtlasLayout layout = AtlasLayout.Compute([prints[0], near, prints[1], prints[2]], [1, 1, 1, 1]);
        double Gap(int i, int j) => Math.Sqrt(Math.Pow(layout.Positions[i].X - layout.Positions[j].X, 2) + Math.Pow(layout.Positions[i].Y - layout.Positions[j].Y, 2));
        Assert.True(Gap(0, 1) < Gap(0, 2) && Gap(0, 1) < Gap(0, 3));
        Assert.Contains((0, 1), layout.Edges);
    }

    [Fact]
    public void IdenticalPrints_AreSpreadWithoutOverlap()
    {
        (List<byte[]> prints, _) = TopLevel();
        AssertTidy(AtlasLayout.Compute([.. Enumerable.Repeat(prints[0], 41)], [.. Enumerable.Repeat(1, 41)]));
    }

    [Fact]
    public void OneAndNone()
    {
        (List<byte[]> prints, _) = TopLevel();
        Assert.Equal(new[] { (0.5, 0.5) }, AtlasLayout.Compute([prints[0]], [10]).Positions);
        Assert.Empty(AtlasLayout.Compute([], []).Positions);
    }

    [Fact]
    public void CenterOn_PutsTheNodeInTheMiddle_AndKeepsEverythingInside()
    {
        (List<byte[]> prints, List<int> sizes) = TopLevel();
        AtlasLayout layout = AtlasLayout.Compute(prints, sizes);
        AtlasLayout centred = layout.CenterOn(5);
        Assert.Equal(0.5, centred.Positions[5].X, 12);
        Assert.Equal(0.5, centred.Positions[5].Y, 12);
        AssertTidy(centred);
        Assert.Equal(layout.Edges, centred.Edges);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~AtlasLayoutTests"`
Expected: build FAIL, `AtlasLayout` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Where to draw a set of sounds in the unit square: classical multidimensional scaling of their fingerprint
/// distances, nodes sized by how many sounds they stand for and pushed apart until none overlap, each joined to its two
/// nearest neighbours. Deterministic.
/// </summary>
public sealed class AtlasLayout
{
    private const double Gap = 0.006;
    private const int OverlapPasses = 200;

    private AtlasLayout(IReadOnlyList<(double X, double Y)> positions, IReadOnlyList<double> radii, IReadOnlyList<(int From, int To)> edges)
    {
        Positions = positions;
        Radii = radii;
        Edges = edges;
    }

    /// <summary>Centre of every node, in the unit square.</summary>
    public IReadOnlyList<(double X, double Y)> Positions { get; }

    /// <summary>Radius of every node, in the units of <see cref="Positions"/>.</summary>
    public IReadOnlyList<double> Radii { get; }

    /// <summary>Pairs of nodes to join, lower index first, each pair once.</summary>
    public IReadOnlyList<(int From, int To)> Edges { get; }

    /// <summary>The layout of a set of sounds.</summary>
    /// <param name="prints">Fingerprints, one per node.</param>
    /// <param name="sizes">Number of sounds each node stands for, at least 1.</param>
    public static AtlasLayout Compute(IReadOnlyList<byte[]> prints, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(prints);
        ArgumentNullException.ThrowIfNull(sizes);
        if (prints.Count != sizes.Count)
        {
            throw new ArgumentException("One size per fingerprint.", nameof(sizes));
        }

        int n = prints.Count;
        if (n == 0)
        {
            return new AtlasLayout([], [], []);
        }

        var d2 = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                d2[i, j] = d2[j, i] = SpectralFingerprint.Distance(prints[i], prints[j]);
            }
        }

        int largest = Math.Max(1, sizes.Max());
        double[] radii = [.. sizes.Select(s => 0.018 + (0.022 * Math.Log(Math.Max(1, s) + 1) / Math.Log(largest + 1)))];
        (double[] x, double[] y) = Scale(Mds(d2), radii);
        RemoveOverlaps(x, y, radii);
        return new AtlasLayout([.. Enumerable.Range(0, n).Select(i => (x[i], y[i]))], radii, Edges2(d2));
    }

    /// <summary>
    /// The same layout with one node in the middle: positions and radii scaled down together just enough that every
    /// node stays inside, so no overlap comes back.
    /// </summary>
    /// <param name="index">Node to centre.</param>
    public AtlasLayout CenterOn(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Positions.Count);
        (double cx, double cy) = Positions[index];
        double s = 1.0;
        for (int i = 0; i < Positions.Count; i++)
        {
            double reach = Math.Max(Math.Abs(Positions[i].X - cx), Math.Abs(Positions[i].Y - cy)) + Radii[i];
            s = Math.Min(s, 0.5 / reach);
        }

        return new AtlasLayout(
            [.. Positions.Select(p => (0.5 + ((p.X - cx) * s), 0.5 + ((p.Y - cy) * s)))],
            [.. Radii.Select(r => r * s)],
            Edges);
    }

    /// <summary>Two coordinates per point from squared distances: the two largest eigenvectors of the double-centred matrix.</summary>
    private static (double[] X, double[] Y) Mds(double[,] d2)
    {
        int n = d2.GetLength(0);
        var rowMean = new double[n];
        double grand = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                rowMean[i] += d2[i, j] / n;
            }

            grand += rowMean[i] / n;
        }

        var b = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                b[i, j] = -0.5 * (d2[i, j] - rowMean[i] - rowMean[j] + grand);
            }
        }

        (double l1, double[] v1) = PowerIteration(b, i => 1.0 + ((double)i / n));
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                b[i, j] -= l1 * v1[i] * v1[j];
            }
        }

        (double l2, double[] v2) = PowerIteration(b, i => (i % 2 == 0 ? 1.0 : -1.0) + ((double)i / n));
        double s1 = Math.Sqrt(Math.Max(0, l1)), s2 = Math.Sqrt(Math.Max(0, l2));
        return ([.. v1.Select(v => v * s1)], [.. v2.Select(v => v * s2)]);
    }

    private static (double Value, double[] Vector) PowerIteration(double[,] m, Func<int, double> start)
    {
        int n = m.GetLength(0);
        double[] v = [.. Enumerable.Range(0, n).Select(start)];
        double value = 0;
        for (int iteration = 0; iteration < 300; iteration++)
        {
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    w[i] += m[i, j] * v[j];
                }
            }

            double norm = Math.Sqrt(w.Sum(static x => x * x));
            if (norm < 1e-12)
            {
                return (0, new double[n]);
            }

            for (int i = 0; i < n; i++)
            {
                v[i] = w[i] / norm;
            }

            value = norm;
        }

        // The sign of the eigenvalue: Rayleigh quotient.
        double rayleigh = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                rayleigh += v[i] * m[i, j] * v[j];
            }
        }

        return (Math.Sign(rayleigh) * value, v);
    }

    /// <summary>Into the unit square with room for the largest node; points that coincide go on a circle.</summary>
    private static (double[] X, double[] Y) Scale((double[] X, double[] Y) points, double[] radii)
    {
        int n = radii.Length;
        double margin = radii.Max() + Gap;
        double minX = points.X.Min(), maxX = points.X.Max(), minY = points.Y.Min(), maxY = points.Y.Max();
        double span = Math.Max(maxX - minX, maxY - minY);
        var x = new double[n];
        var y = new double[n];
        if (n == 1)
        {
            x[0] = y[0] = 0.5;
            return (x, y);
        }

        if (span < 1e-9)
        {
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * i / n;
                x[i] = 0.5 + (0.3 * Math.Cos(angle));
                y[i] = 0.5 + (0.3 * Math.Sin(angle));
            }

            return (x, y);
        }

        double scale = (1 - (2 * margin)) / span;
        double ox = 0.5 - ((minX + maxX) / 2 * scale), oy = 0.5 - ((minY + maxY) / 2 * scale);
        for (int i = 0; i < n; i++)
        {
            x[i] = ox + (points.X[i] * scale);
            y[i] = oy + (points.Y[i] * scale);
        }

        return (x, y);
    }

    private static void RemoveOverlaps(double[] x, double[] y, double[] radii)
    {
        int n = x.Length;
        for (int pass = 0; pass < OverlapPasses; pass++)
        {
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    double dx = x[j] - x[i], dy = y[j] - y[i];
                    double distance = Math.Sqrt((dx * dx) + (dy * dy));
                    double need = radii[i] + radii[j] + Gap;
                    if (distance >= need)
                    {
                        continue;
                    }

                    any = true;
                    if (distance < 1e-9)
                    {
                        // Coincident: apart along a direction fixed by the pair.
                        double angle = 2.399963 * (i + (n * j));
                        (dx, dy, distance) = (Math.Cos(angle), Math.Sin(angle), 1.0);
                    }

                    double push = (need - distance) / 2;
                    x[i] -= push * dx / distance;
                    y[i] -= push * dy / distance;
                    x[j] += push * dx / distance;
                    y[j] += push * dy / distance;
                }
            }

            for (int i = 0; i < n; i++)
            {
                x[i] = Math.Clamp(x[i], radii[i], 1 - radii[i]);
                y[i] = Math.Clamp(y[i], radii[i], 1 - radii[i]);
            }

            if (!any)
            {
                return;
            }
        }
    }

    private static IReadOnlyList<(int From, int To)> Edges2(double[,] d2)
    {
        int n = d2.GetLength(0);
        var edges = new SortedSet<(int, int)>();
        for (int i = 0; i < n; i++)
        {
            foreach (int j in Enumerable.Range(0, n).Where(j => j != i).OrderBy(j => d2[i, j]).ThenBy(static j => j).Take(2))
            {
                edges.Add((Math.Min(i, j), Math.Max(i, j)));
            }
        }

        return [.. edges];
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HearSay.OperatorMatrix.Tests -c Release --filter "FullyQualifiedName~AtlasLayoutTests"`
Expected: PASS (5 tests). If `IdenticalPrints_AreSpreadWithoutOverlap` or `Layout_IsTidyAndDeterministic` still finds overlaps after 200 passes, the clamp at the border undoes pushes: shrink all radii by a common factor until the final positions are free of overlaps (scale radii by 0.9 and rerun the passes, at most 5 times) and ledger a ruling.

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.OperatorMatrix/Priors/AtlasLayout.cs tests/HearSay.OperatorMatrix.Tests/AtlasLayoutTests.cs
git commit -m "Atlas layout: sounds placed by similarity, sized by family, without overlap"
```

---

### Task 6: ExportedSounds and LibrarySoundWriter

**Files:**
- Create: `src/HearSay/ExportedSounds.cs`
- Create: `src/HearSay/LibrarySoundWriter.cs`
- Test: `tests/HearSay.IntegrationTests/ExportedSoundsTests.cs`

**Interfaces:**
- Consumes: Task 1 `StructureLibrary.ReferenceRequest`, `ReferenceHz`, `Fingerprint(int)`, `Seed(int)`, `Patch(int)`; `SynthesisBackends.RendererFor/ExporterFor`; `PatchCompatibilityValidator.Instance`; `HearSayJson.Serialize`.
- Produces:
  - `public sealed record ExportedSound(string Name, string PatchFile, OperatorMatrixPatch Patch, SynthesisProfile Profile)`
  - `public sealed record ExportedSoundScan(IReadOnlyList<ExportedSound> Sounds, IReadOnlyList<string> Skipped)`
  - `public static partial class ExportedSounds` with `IReadOnlyList<SynthesisProfile> Profiles`, `ExportedSoundScan Scan(string directory)`, `SynthesisProfile? ProfileFor(OperatorMatrixPatch patch, string? summaryProfile)`, `byte[] Fingerprint(ExportedSound sound)`.
  - `public static class LibrarySoundWriter` with `IReadOnlyList<string> Write(OperatorMatrixPatch patch, SynthesisProfile profile, string directory, IReadOnlyList<string> headerComments)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Fm7Compatible.Rendering;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>Reading folders of exported sounds and saving library sounds; fast, no search.</summary>
public sealed class ExportedSoundsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-exported-" + Guid.NewGuid().ToString("N"));

    public ExportedSoundsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static OperatorMatrixPatch Sine => ReferencePatches.Find("electric-piano")!.Patch;

    private static OperatorMatrixPatch With(OperatorWaveform waveform) =>
        OperatorMatrixPatch.Silent(6).WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1)) with { Waveform = waveform }).WithOutputLevel(0, 1.0);

    private string Folder(string relative, OperatorMatrixPatch patch, string? summaryProfile = null)
    {
        string folder = Path.Combine(_directory, relative);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "patch.json"), patch.ToJson());
        if (summaryProfile is not null)
        {
            File.WriteAllText(Path.Combine(folder, "search-summary.json"), $$"""{ "searchSummary": { "profile": "{{summaryProfile}}", "randomSeed": 1 }, "candidates": [] }""");
        }

        return folder;
    }

    [Fact]
    public void Save_ThenScan_GivesTheSamePatchAndProfile()
    {
        OperatorMatrixPatch patch = With(OperatorWaveform.Saw).WithOperator(0, With(OperatorWaveform.Saw).Operators[0] with { Filter = OperatorFilter.LowPass(4) });
        IReadOnlyList<string> files = LibrarySoundWriter.Write(patch, SynthesisProfiles.SuperColliderFmx6, Path.Combine(_directory, "kept"), ["HearSay library supercollider-fmx6 v2, seed 7."]);
        Assert.Contains(files, static f => f.EndsWith("patch.scd", StringComparison.Ordinal));
        Assert.Contains(files, static f => f.EndsWith("preview.wav", StringComparison.Ordinal));
        Assert.Contains("seed 7", File.ReadAllText(Path.Combine(_directory, "kept", "patch.scd")), StringComparison.Ordinal);

        ExportedSound sound = Assert.Single(ExportedSounds.Scan(_directory).Sounds);
        Assert.Equal("kept", sound.Name);
        Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, sound.Profile.Name);
        Assert.Equal(patch.ComputeHash(), sound.Patch.ComputeHash());
    }

    [Fact]
    public void Scan_ReadsAlternatives_AndTheProfileFromTheSummary()
    {
        string folder = Folder("piano-c4", Sine, SynthesisProfiles.SuperColliderFmx6.Name);
        string alternatives = Path.Combine(folder, "alternatives");
        Directory.CreateDirectory(alternatives);
        File.WriteAllText(Path.Combine(alternatives, "01-patch.json"), Sine.ToJson());
        File.WriteAllText(Path.Combine(alternatives, "02-patch.json"), Sine.ToJson());
        File.WriteAllText(Path.Combine(alternatives, "01-patch.scd"), "// not a patch");
        File.WriteAllText(Path.Combine(folder, "analysis.json"), "{}");

        ExportedSoundScan scan = ExportedSounds.Scan(_directory);
        Assert.Empty(scan.Skipped);
        Assert.Equal(new[] { "piano-c4", "piano-c4 alt 01", "piano-c4 alt 02" }, scan.Sounds.Select(static s => s.Name));
        Assert.All(scan.Sounds, static s => Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, s.Profile.Name));
    }

    [Fact]
    public void Scan_FallsBackToTheSmallestProfileThatPlaysThePatch()
    {
        Folder("sine", Sine);
        Folder("saw", With(OperatorWaveform.Saw));
        Folder("elliptic", With(OperatorWaveform.Elliptic));
        Dictionary<string, string> profiles = ExportedSounds.Scan(_directory).Sounds.ToDictionary(static s => s.Name, static s => s.Profile.Name);
        Assert.Equal(SynthesisProfiles.SuperColliderFm7.Name, profiles["sine"]);
        Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, profiles["saw"]);
        Assert.Equal(SynthesisProfiles.SuperColliderNome6.Name, profiles["elliptic"]);
    }

    [Fact]
    public void Scan_FallsBackWhenTheSummaryProfileCannotPlayThePatch()
    {
        Folder("saw-said-fm7", With(OperatorWaveform.Saw), SynthesisProfiles.SuperColliderFm7.Name);
        Folder("unknown-profile", Sine, "some-synth");
        Dictionary<string, string> profiles = ExportedSounds.Scan(_directory).Sounds.ToDictionary(static s => s.Name, static s => s.Profile.Name);
        Assert.Equal(SynthesisProfiles.SuperColliderFmx6.Name, profiles["saw-said-fm7"]);
        Assert.Equal(SynthesisProfiles.SuperColliderFm7.Name, profiles["unknown-profile"]);
    }

    [Fact]
    public void Scan_SkipsWhatItCannotUse_WithTheReason()
    {
        Folder("good", Sine);
        Directory.CreateDirectory(Path.Combine(_directory, "broken"));
        File.WriteAllText(Path.Combine(_directory, "broken", "patch.json"), "{ not json");
        Folder("invalid", Sine.WithOperator(0, Sine.Operators[0] with { HarmonicLimit = 0 }));

        ExportedSoundScan scan = ExportedSounds.Scan(_directory);
        Assert.Equal("good", Assert.Single(scan.Sounds).Name);
        Assert.Contains(scan.Skipped, static s => s.StartsWith("broken: unreadable", StringComparison.Ordinal));
        Assert.Contains(scan.Skipped, static s => s.StartsWith("invalid: invalid patch", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_OfAFolderWithoutPatches_FindsNothing()
    {
        File.WriteAllText(Path.Combine(_directory, "notes.txt"), "nothing here");
        ExportedSoundScan scan = ExportedSounds.Scan(_directory);
        Assert.Empty(scan.Sounds);
        Assert.Empty(scan.Skipped);
    }

    [Fact]
    public void Fingerprint_IsTheOneTheLibraryWouldGive()
    {
        StructureLibrary library = StructureLibrary.Build(SynthesisProfiles.SuperColliderFm7.Capabilities, 20, new Fm7CompatibleOperatorMatrixRenderer());
        var sound = new ExportedSound("member", "patch.json", library.Patch(library.Seed(3)), SynthesisProfiles.SuperColliderFm7);
        Assert.Equal(library.Fingerprint(3).ToArray(), ExportedSounds.Fingerprint(sound));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test tests/HearSay.IntegrationTests -c Release --filter "FullyQualifiedName~ExportedSoundsTests"`
Expected: build FAIL, `ExportedSounds`, `ExportedSound`, `LibrarySoundWriter` do not exist.

- [ ] **Step 3: Implement `ExportedSounds`**

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay;

/// <summary>A sound exported earlier: a <c>patch.json</c> and the profile it plays on.</summary>
/// <param name="Name">The folder name, with <c>alt NN</c> for an alternative.</param>
/// <param name="PatchFile">Path of the <c>patch.json</c>.</param>
/// <param name="Patch">The patch.</param>
/// <param name="Profile">The profile it was made for, or the smallest one that plays it.</param>
public sealed record ExportedSound(string Name, string PatchFile, OperatorMatrixPatch Patch, SynthesisProfile Profile);

/// <summary>What a folder of exported sounds held.</summary>
/// <param name="Sounds">Usable sounds, in path order.</param>
/// <param name="Skipped">One line per file that could not be used: <c>name: reason</c>.</param>
public sealed record ExportedSoundScan(IReadOnlyList<ExportedSound> Sounds, IReadOnlyList<string> Skipped);

/// <summary>
/// Finds the sounds in result folders written by Studio, the command line or the library browser: every
/// <c>patch.json</c> and every <c>alternatives/NN-patch.json</c>, with the profile from <c>search-summary.json</c>.
/// </summary>
public static partial class ExportedSounds
{
    /// <summary>Profiles a sound can be played on, smallest first.</summary>
    public static IReadOnlyList<SynthesisProfile> Profiles { get; } = [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6];

    /// <summary>The sounds under a folder, recursively.</summary>
    /// <param name="directory">Folder to search.</param>
    public static ExportedSoundScan Scan(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var sounds = new List<ExportedSound>();
        var skipped = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*patch.json", SearchOption.AllDirectories).Where(IsExportedPatch).Order(StringComparer.Ordinal))
        {
            string name = NameOf(file);
            OperatorMatrixPatch patch;
            try
            {
                patch = OperatorMatrixPatch.FromJson(File.ReadAllText(file));
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException or ArgumentException or InvalidOperationException or NullReferenceException)
            {
                skipped.Add($"{name}: unreadable ({e.Message})");
                continue;
            }

            IReadOnlyList<string> problems = patch.Validate();
            if (problems.Count > 0)
            {
                skipped.Add($"{name}: invalid patch ({problems[0]})");
                continue;
            }

            if (ProfileFor(patch, SummaryProfileOf(file)) is not { } profile)
            {
                skipped.Add($"{name}: no profile can play it");
                continue;
            }

            sounds.Add(new ExportedSound(name, file, patch, profile));
        }

        return new ExportedSoundScan(sounds, skipped);
    }

    /// <summary>The profile named in the summary when it plays the patch, otherwise the smallest one that does; null for none.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="summaryProfile">Profile name from <c>search-summary.json</c>, if any.</param>
    public static SynthesisProfile? ProfileFor(OperatorMatrixPatch patch, string? summaryProfile)
    {
        ArgumentNullException.ThrowIfNull(patch);
        bool Plays(SynthesisProfile p) => PatchCompatibilityValidator.Instance.Validate(patch, p.Capabilities).IsCompatible;
        SynthesisProfile? named = Profiles.FirstOrDefault(p => p.Name == summaryProfile);
        return named is not null && Plays(named) ? named : Profiles.FirstOrDefault(Plays);
    }

    /// <summary>The fingerprint of a sound as the library would take it: rendered with <see cref="StructureLibrary.ReferenceRequest"/>.</summary>
    /// <param name="sound">Sound.</param>
    public static byte[] Fingerprint(ExportedSound sound)
    {
        ArgumentNullException.ThrowIfNull(sound);
        RenderedAudio audio = SynthesisBackends.RendererFor(sound.Profile).Render(sound.Patch, StructureLibrary.ReferenceRequest);
        return SpectralFingerprint.Of(audio.Samples, audio.SampleRate, StructureLibrary.ReferenceHz);
    }

    [GeneratedRegex(@"^\d+-patch\.json$")]
    private static partial Regex AlternativeFile();

    private static bool IsAlternative(string file) => Path.GetFileName(file) != "patch.json";

    private static bool IsExportedPatch(string file) =>
        Path.GetFileName(file) == "patch.json"
        || (Path.GetFileName(Path.GetDirectoryName(file)) == "alternatives" && AlternativeFile().IsMatch(Path.GetFileName(file)));

    private static string ResultFolderOf(string file)
    {
        string folder = Path.GetDirectoryName(file)!;
        return IsAlternative(file) ? Path.GetDirectoryName(folder)! : folder;
    }

    private static string NameOf(string file)
    {
        string name = Path.GetFileName(ResultFolderOf(file));
        return IsAlternative(file) ? $"{name} alt {Path.GetFileName(file).Split('-')[0]}" : name;
    }

    private static string? SummaryProfileOf(string file)
    {
        string summary = Path.Combine(ResultFolderOf(file), "search-summary.json");
        if (!File.Exists(summary))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(summary));
            return Property(Property(document.RootElement, "searchSummary"), "profile") is { ValueKind: JsonValueKind.String } profile ? profile.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private static JsonElement? Property(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Implement `LibrarySoundWriter`**

```csharp
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay;

/// <summary>
/// Writes a single sound, from the library browser, as a folder that <see cref="ExportedSounds"/> reads again:
/// <c>patch.json</c>, <c>preview.wav</c> of the reference note, the backend export and a <c>search-summary.json</c>
/// holding only the profile.
/// </summary>
public static class LibrarySoundWriter
{
    /// <summary>Writes the folder.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile it plays on.</param>
    /// <param name="directory">Output folder; created when missing.</param>
    /// <param name="headerComments">Comment lines at the top of the export.</param>
    /// <returns>Paths of all files written.</returns>
    public static IReadOnlyList<string> Write(OperatorMatrixPatch patch, SynthesisProfile profile, string directory, IReadOnlyList<string> headerComments)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(headerComments);
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        void WriteText(string path, string text)
        {
            File.WriteAllText(path, text);
            written.Add(path);
        }

        WriteText(Path.Combine(directory, "patch.json"), patch.ToJson());
        string preview = Path.Combine(directory, "preview.wav");
        SynthesisBackends.RendererFor(profile).Render(patch, StructureLibrary.ReferenceRequest).WriteWav(preview, normalizePeakTo: 0.7f);
        written.Add(preview);
        if (SynthesisBackends.ExporterFor(profile) is { } exporter)
        {
            WriteText(Path.Combine(directory, "patch" + exporter.FileExtension), exporter.Export(patch, new PatchExportOptions { HeaderComments = headerComments }));
        }

        WriteText(Path.Combine(directory, "search-summary.json"), HearSayJson.Serialize(new { SearchSummary = new { Profile = profile.Name } }));
        return written;
    }
}
```

If `HearSayJson` is not visible from `HearSay` (it is public in `HearSay.Audio`), add `using HearSay.Audio;` only if its namespace differs — check `src/HearSay.Audio/HearSayJson.cs` for the namespace line.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/HearSay.IntegrationTests -c Release --filter "FullyQualifiedName~ExportedSoundsTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/HearSay/ExportedSounds.cs src/HearSay/LibrarySoundWriter.cs tests/HearSay.IntegrationTests/ExportedSoundsTests.cs
git commit -m "Exported sounds: read result folders with their profile, and save a single sound so that it reads again"
```

---

### Task 7: Studio — PatchView and NotePlayer

**Files:**
- Create: `src/HearSay.Studio/PatchView.axaml`, `src/HearSay.Studio/PatchView.axaml.cs`
- Create: `src/HearSay.Studio/NotePlayer.cs`
- Modify: `src/HearSay.Studio/MainWindow.axaml` (right column), `src/HearSay.Studio/MainWindow.axaml.cs` (`OnNotePressed`), `src/HearSay.Studio/MainViewModel.cs` (`PlayNote`, `_resultGain`, `Scale`, `PlayTarget`, `StopPlayback`, `CanPlay`)

**Interfaces:**
- Produces:
  - `PatchView` styled properties `string Code`, `string? Watermark`, `OperatorMatrixPatch? Patch`, `string CodeHeader` (default `"SuperCollider · patch.scd"`).
  - `public sealed class NotePlayer(AudioPlayer player)` with `bool CanPlay`, `Task<bool> PlayAsync(OperatorMatrixPatch patch, SynthesisProfile profile, RenderRequest gainReference, RenderRequest note)`, `void Play(RenderedAudio audio)`, `void Stop()`, `static bool RespondsToVelocity(OperatorMatrixPatch patch)`.
  - `MainViewModel.Notes` (`NotePlayer`, internal use by Task 9), `public Task PlayNoteAsync(int midiNote, double velocity)` replacing `PlayNote`.

There is no Studio test project; this task is a refactor verified by build and by hand.

- [ ] **Step 1: Create `NotePlayer.cs`**

```csharp
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>
/// Plays notes of patches for both tabs: renders off the UI thread, keeps one gain per patch from its reference note so
/// that the differences between keys and velocities survive, and plays only the note asked for last.
/// </summary>
public sealed class NotePlayer(AudioPlayer player)
{
    private object? _gainPatch;
    private float _gain;
    private int _version;

    /// <summary>True when an audio device could be opened.</summary>
    public bool CanPlay => player.IsAvailable;

    /// <summary>True when the patch reacts to key velocity; otherwise the keyboard applies it as volume.</summary>
    /// <param name="patch">Patch.</param>
    public static bool RespondsToVelocity(OperatorMatrixPatch patch) => patch.Operators.Any(static o => o.Enabled && o.VelocitySensitivity > 0);

    /// <summary>Renders a note and plays it, unless another note was asked for meanwhile.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile whose renderer plays it.</param>
    /// <param name="gainReference">Request whose loudest sample, at full velocity, sets the gain of the patch.</param>
    /// <param name="note">The note to play.</param>
    /// <returns>True when the note was played.</returns>
    public async Task<bool> PlayAsync(OperatorMatrixPatch patch, SynthesisProfile profile, RenderRequest gainReference, RenderRequest note)
    {
        int version = Interlocked.Increment(ref _version);
        bool known = ReferenceEquals(_gainPatch, patch);
        float knownGain = _gain;
        (RenderedAudio audio, float gain) = await Task.Run(() =>
        {
            IOperatorMatrixRenderer renderer = SynthesisBackends.RendererFor(profile);
            float g = knownGain;
            if (!known)
            {
                RenderedAudio full = renderer.Render(patch, gainReference with { Velocity = 1.0 });
                g = full.Peak > 0 ? 0.5f / full.Peak : 1f;
            }

            return (renderer.Render(patch, note), g);
        });
        if (version != Volatile.Read(ref _version))
        {
            return false;
        }

        (_gainPatch, _gain) = (patch, gain);
        float level = gain * (RespondsToVelocity(patch) ? 1f : (float)note.Velocity);
        player.Play(Scale(audio, Math.Min(level, 0.98f / Math.Max(1e-6f, audio.Peak))));
        return true;
    }

    /// <summary>Plays audio as it is.</summary>
    /// <param name="audio">Audio.</param>
    public void Play(RenderedAudio audio)
    {
        Interlocked.Increment(ref _version);
        player.Play(audio);
    }

    /// <summary>Stops playback.</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _version);
        player.Stop();
    }

    private static RenderedAudio Scale(RenderedAudio audio, float gain)
    {
        var samples = new float[audio.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i] * gain;
        }

        return new RenderedAudio(samples, audio.SampleRate);
    }
}
```

- [ ] **Step 2: Route `MainViewModel` through it**

In `MainViewModel`: keep `private readonly AudioPlayer _player = new();`, add `internal NotePlayer Notes { get; }` initialised in the constructor as `Notes = new NotePlayer(_player);`. Remove `_resultGain` and its reset in `ShowCandidate`, and the private `Scale` method. `CanPlay` becomes `public bool CanPlay => Notes.CanPlay;`. `StopPlayback` becomes `public void StopPlayback() => Notes.Stop();`. In `PlayTarget` replace `_player.Play(...)` with `Notes.Play(...)`. Replace `PlayNote` with:

```csharp
    /// <summary>Renders the shown patch at a note and plays it. Nothing happens without a patch.</summary>
    /// <param name="midiNote">MIDI note.</param>
    /// <param name="velocity">Key velocity 0..1.</param>
    public async Task PlayNoteAsync(int midiNote, double velocity)
    {
        if (_result is null || _shownPatch is null)
        {
            return;
        }

        PatchMatchResult result = _result;
        OperatorMatrixPatch patch = _shownPatch;
        double hz = 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
        double held = Math.Clamp(result.Request.NoteDuration.TotalSeconds, 0.3, 2.0);
        double tail = Math.Clamp(Math.Max(0.3, result.Request.ReleaseDuration.TotalSeconds), 0.3, 2.0);
        var note = new RenderRequest(hz, midiNote, velocity, TimeSpan.FromSeconds(held), TimeSpan.FromSeconds(tail), result.Request.SampleRate);
        if (await Notes.PlayAsync(patch, result.Profile, result.Request, note))
        {
            PlayingText = FormattableString.Invariant($"{PianoKeyboard.NoteName(midiNote)} ({hz:F1} Hz), velocity {velocity:F2}") + (NotePlayer.RespondsToVelocity(patch) ? string.Empty : " (as volume: this patch has no velocity sensitivity)");
        }
    }
```

In `MainWindow.axaml.cs`: `private async void OnNotePressed(object? sender, KeyPress e) => await ViewModel.PlayNoteAsync(e.MidiNote, e.Velocity);`

- [ ] **Step 3: Create `PatchView`**

`PatchView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="using:HearSay.Studio"
             x:Class="HearSay.Studio.PatchView"
             x:Name="Root">
  <TabControl Padding="0,6,0,0">
    <TabItem Header="{Binding CodeHeader, ElementName=Root}" FontSize="13">
      <TextBox Classes="mono" Text="{Binding Code, ElementName=Root}" Watermark="{Binding Watermark, ElementName=Root}" />
    </TabItem>
    <TabItem Header="Block diagram" FontSize="13">
      <Border Background="{StaticResource EditorBackgroundBrush}" BorderBrush="{StaticResource CardBorderBrush}" BorderThickness="1" CornerRadius="6" ClipToBounds="True">
        <local:FmDiagram Patch="{Binding Patch, ElementName=Root}" Margin="8" />
      </Border>
    </TabItem>
  </TabControl>
</UserControl>
```

`PatchView.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using HearSay.Synthesis.Patches;

namespace HearSay.Studio;

/// <summary>A patch as SuperCollider code and as a block diagram, in two tabs; used by the Search and Library tabs.</summary>
public sealed partial class PatchView : UserControl
{
    /// <summary>The generated code.</summary>
    public static readonly StyledProperty<string> CodeProperty = AvaloniaProperty.Register<PatchView, string>(nameof(Code), string.Empty);

    /// <summary>Shown while there is no code.</summary>
    public static readonly StyledProperty<string?> WatermarkProperty = AvaloniaProperty.Register<PatchView, string?>(nameof(Watermark));

    /// <summary>The patch drawn as a block diagram.</summary>
    public static readonly StyledProperty<OperatorMatrixPatch?> PatchProperty = AvaloniaProperty.Register<PatchView, OperatorMatrixPatch?>(nameof(Patch));

    /// <summary>Header of the code tab.</summary>
    public static readonly StyledProperty<string> CodeHeaderProperty = AvaloniaProperty.Register<PatchView, string>(nameof(CodeHeader), "SuperCollider · patch.scd");

    public PatchView()
    {
        InitializeComponent();
    }

    /// <inheritdoc cref="CodeProperty"/>
    public string Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <inheritdoc cref="WatermarkProperty"/>
    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <inheritdoc cref="PatchProperty"/>
    public OperatorMatrixPatch? Patch
    {
        get => GetValue(PatchProperty);
        set => SetValue(PatchProperty, value);
    }

    /// <inheritdoc cref="CodeHeaderProperty"/>
    public string CodeHeader
    {
        get => GetValue(CodeHeaderProperty);
        set => SetValue(CodeHeaderProperty, value);
    }
}
```

In `MainWindow.axaml`, replace the right column's `<TabControl Padding="0,6,0,0"> … </TabControl>` (the two TabItems "SuperCollider · patch.scd" and "Block diagram") with:

```xml
          <local:PatchView Code="{Binding Scd}" Watermark="{Binding ScdWatermark}" Patch="{Binding ShownPatch}" />
```

- [ ] **Step 4: Build and check by hand**

Run: `dotnet build src/HearSay.Studio -c Debug -nologo -v q`
Expected: `0 Error(s)`, no new warnings.
Then run `dotnet run --project src/HearSay.Studio -c Debug`, add a WAV, run a short search (budget 2000), and check: the code tab and the block diagram show the result; the keyboard plays notes at different pitches with level differences; clicking a target's play button plays it.

- [ ] **Step 5: Commit**

```bash
git add src/HearSay.Studio/NotePlayer.cs src/HearSay.Studio/PatchView.axaml src/HearSay.Studio/PatchView.axaml.cs src/HearSay.Studio/MainViewModel.cs src/HearSay.Studio/MainWindow.axaml src/HearSay.Studio/MainWindow.axaml.cs
git commit -m "Studio: the patch panel and note playback become shareable"
```

---

### Task 8: Studio — LibraryGraph control

**Files:**
- Create: `src/HearSay.Studio/LibraryGraph.cs`

**Interfaces:**
- Produces:
  - `public sealed record GraphNode(double X, double Y, double Radius, double Brightness, string Caption, string Tip, bool IsImported, bool CanOpen, bool IsMarked)` — X, Y, Radius in the unit square; Brightness 0..1.
  - `public sealed record GraphEdge(int From, int To)`
  - `LibraryGraph` styled properties `IReadOnlyList<GraphNode>? Nodes`, `IReadOnlyList<GraphEdge>? Edges`, `int SelectedIndex` (default −1), `string? Message`; events `EventHandler<int>? NodeClicked`, `EventHandler<int>? NodeOpened`.

- [ ] **Step 1: Implement**

```csharp
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HearSay.Studio;

/// <summary>A node of the library graph, in the unit square.</summary>
/// <param name="X">Centre, 0..1 from the left.</param>
/// <param name="Y">Centre, 0..1 from the top.</param>
/// <param name="Radius">Radius in the same units.</param>
/// <param name="Brightness">0 dark to 1 very bright; sets the colour.</param>
/// <param name="Caption">Short text under the node.</param>
/// <param name="Tip">Text shown when the pointer rests on the node.</param>
/// <param name="IsImported">An imported sound: drawn as a star.</param>
/// <param name="CanOpen">A family that can be opened.</param>
/// <param name="IsMarked">On the way to a placed imported sound.</param>
public sealed record GraphNode(double X, double Y, double Radius, double Brightness, string Caption, string Tip, bool IsImported, bool CanOpen, bool IsMarked);

/// <summary>A line between two nodes.</summary>
/// <param name="From">Index of one node.</param>
/// <param name="To">Index of the other.</param>
public sealed record GraphEdge(int From, int To);

/// <summary>
/// Sound families drawn as a graph: nodes coloured by brightness and sized by family, lines to the nearest neighbours,
/// a ring around the selected node with a plus to open it, a star for an imported sound. A click raises
/// <see cref="NodeClicked"/>, a double click or a click on the plus raises <see cref="NodeOpened"/>.
/// </summary>
public sealed class LibraryGraph : Control
{
    /// <summary>Nodes to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<GraphNode>?> NodesProperty = AvaloniaProperty.Register<LibraryGraph, IReadOnlyList<GraphNode>?>(nameof(Nodes));

    /// <summary>Lines to draw.</summary>
    public static readonly StyledProperty<IReadOnlyList<GraphEdge>?> EdgesProperty = AvaloniaProperty.Register<LibraryGraph, IReadOnlyList<GraphEdge>?>(nameof(Edges));

    /// <summary>Index of the selected node, −1 for none.</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty = AvaloniaProperty.Register<LibraryGraph, int>(nameof(SelectedIndex), -1);

    /// <summary>Text shown instead of the graph, for example while the library is built.</summary>
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<LibraryGraph, string?>(nameof(Message));

    private const double Pad = 24;
    private static readonly Color Dark = Color.Parse("#1E3A8A");
    private static readonly Color Bright = Color.Parse("#FDE68A");
    private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#0B1220"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#94A3B8"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F59E0B"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#22D3EE"));
    private static readonly Pen EdgePen = new(new SolidColorBrush(Color.Parse("#334155")), 1.2);
    private static readonly Pen NodePen = new(new SolidColorBrush(Color.Parse("#0F172A")), 1);
    private static readonly Pen SelectedPen = new(Accent, 2.5);
    private static readonly Pen MarkedPen = new(Amber, 1.8, new DashStyle([3, 2], 0));

    private int _hover = -1;

    static LibraryGraph()
    {
        AffectsRender<LibraryGraph>(NodesProperty, EdgesProperty, SelectedIndexProperty, MessageProperty);
    }

    /// <summary>A node was clicked.</summary>
    public event EventHandler<int>? NodeClicked;

    /// <summary>A node was double-clicked, or its plus was clicked.</summary>
    public event EventHandler<int>? NodeOpened;

    /// <inheritdoc cref="NodesProperty"/>
    public IReadOnlyList<GraphNode>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    /// <inheritdoc cref="EdgesProperty"/>
    public IReadOnlyList<GraphEdge>? Edges
    {
        get => GetValue(EdgesProperty);
        set => SetValue(EdgesProperty, value);
    }

    /// <inheritdoc cref="SelectedIndexProperty"/>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <inheritdoc cref="MessageProperty"/>
    public string? Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        IReadOnlyList<GraphNode>? nodes = Nodes;
        if (!string.IsNullOrEmpty(Message) || nodes is null || nodes.Count == 0)
        {
            DrawText(context, Message ?? string.Empty, new Point(14, 14), 12, Muted);
            return;
        }

        foreach (GraphEdge edge in Edges ?? [])
        {
            if (edge.From < nodes.Count && edge.To < nodes.Count)
            {
                context.DrawLine(EdgePen, Centre(nodes[edge.From]), Centre(nodes[edge.To]));
            }
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            GraphNode node = nodes[i];
            Point c = Centre(node);
            double r = Radius(node);
            var fill = new SolidColorBrush(Lerp(Dark, Bright, node.Brightness));
            if (node.IsImported)
            {
                context.DrawGeometry(Amber, NodePen, Star(c, r + 3));
            }
            else
            {
                context.DrawEllipse(fill, NodePen, c, r, r);
            }

            if (node.IsMarked)
            {
                context.DrawEllipse(null, MarkedPen, c, r + 4, r + 4);
            }

            if (i == SelectedIndex)
            {
                context.DrawEllipse(null, SelectedPen, c, r + 5, r + 5);
                if (node.CanOpen)
                {
                    Point badge = Badge(node);
                    context.DrawEllipse(Accent, null, badge, 7, 7);
                    DrawText(context, "+", new Point(badge.X - 4, badge.Y - 9), 13, Background, FontWeight.Bold);
                }
            }

            if (node.Caption.Length > 0)
            {
                var text = new FormattedText(node.Caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Muted);
                context.DrawText(text, new Point(c.X - (text.Width / 2), c.Y + r + 3));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        IReadOnlyList<GraphNode>? nodes = Nodes;
        if (nodes is null || !string.IsNullOrEmpty(Message))
        {
            return;
        }

        Point p = e.GetPosition(this);
        if (SelectedIndex >= 0 && SelectedIndex < nodes.Count && nodes[SelectedIndex].CanOpen && Distance(p, Badge(nodes[SelectedIndex])) <= 8)
        {
            NodeOpened?.Invoke(this, SelectedIndex);
            return;
        }

        int hit = HitTest(p);
        if (hit < 0)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            NodeOpened?.Invoke(this, hit);
        }
        else
        {
            NodeClicked?.Invoke(this, hit);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int hit = HitTest(e.GetPosition(this));
        if (hit == _hover)
        {
            return;
        }

        _hover = hit;
        ToolTip.SetIsOpen(this, false);
        if (hit >= 0 && Nodes is { } nodes)
        {
            ToolTip.SetTip(this, nodes[hit].Tip);
            ToolTip.SetIsOpen(this, true);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        ToolTip.SetIsOpen(this, false);
    }

    private double Side => Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) - (2 * Pad));

    private Point Centre(GraphNode node) => new(
        ((Bounds.Width - Side) / 2) + (node.X * Side),
        ((Bounds.Height - Side) / 2) + (node.Y * Side));

    private double Radius(GraphNode node) => Math.Max(4, node.Radius * Side);

    private Point Badge(GraphNode node)
    {
        Point c = Centre(node);
        double r = Radius(node) + 5;
        return new Point(c.X + (r * 0.75), c.Y - (r * 0.75));
    }

    private int HitTest(Point p)
    {
        IReadOnlyList<GraphNode>? nodes = Nodes;
        if (nodes is null)
        {
            return -1;
        }

        int best = -1;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < nodes.Count; i++)
        {
            double d = Distance(p, Centre(nodes[i]));
            if (d <= Math.Max(Radius(nodes[i]) + 3, 8) && d < bestDistance)
            {
                (best, bestDistance) = (i, d);
            }
        }

        return best;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte Mix(byte x, byte y) => (byte)Math.Round(x + ((y - x) * t));
        return Color.FromRgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    private static StreamGeometry Star(Point c, double r)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        for (int k = 0; k < 10; k++)
        {
            double angle = (-Math.PI / 2) + (k * Math.PI / 5);
            double reach = k % 2 == 0 ? r : r * 0.45;
            var p = new Point(c.X + (reach * Math.Cos(angle)), c.Y + (reach * Math.Sin(angle)));
            if (k == 0)
            {
                g.BeginFigure(p, true);
            }
            else
            {
                g.LineTo(p);
            }
        }

        g.EndFigure(true);
        return geometry;
    }

    private static void DrawText(DrawingContext context, string text, Point at, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, weight), size, brush);
        context.DrawText(formatted, at);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build src/HearSay.Studio -c Debug -nologo -v q`
Expected: `0 Error(s)`.

- [ ] **Step 3: Check the drawing headless**

Write a scratch console project outside the repository (scratchpad) that references `src/HearSay.Studio/HearSay.Studio.csproj`, `Avalonia.Headless` and `Avalonia.Skia` 11.3.22, sets up `AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()`, shows a 700×520 `Window` whose content is a `LibraryGraph` with 40 nodes from `AtlasLayout.Compute` on the top level of a 3000-patch FM7 `StructureLibrary`, `SelectedIndex = 3`, node 3 `CanOpen`, one `IsImported` node and one `IsMarked` node, and saves `window.CaptureRenderedFrame()` as PNG. Look at the PNG: nodes coloured from blue to yellow, lines between neighbours, a cyan ring and plus on node 3, an amber star, a dashed amber ring, captions under the nodes, nothing clipped.

- [ ] **Step 4: Commit**

```bash
git add src/HearSay.Studio/LibraryGraph.cs
git commit -m "Studio: a graph control for sound families"
```

---

### Task 9: Studio — the Library tab: browsing, audition, keyboard, Copy and Save

**Files:**
- Create: `src/HearSay.Studio/LibraryViewModel.cs`
- Create: `src/HearSay.Studio/LibraryView.axaml`, `src/HearSay.Studio/LibraryView.axaml.cs`
- Modify: `src/HearSay.Studio/MainViewModel.cs` (`Library`, `SelectedTab`), `src/HearSay.Studio/MainWindow.axaml` (tab control around the body)

**Interfaces:**
- Consumes: Task 1 `StructureLibrary.For(…, progress)`, `FileOf`, `IsAvailable`, `ReferenceRequest`, `Seed`, `Patch`, `Fingerprint`; Task 2 `SoundDescriptors.Of/Label/BrightnessRatio`; Tasks 3–4 `LibraryAtlas.Open/CacheFileOf/Children/Root`; Task 5 `AtlasLayout.Compute`; Task 6 `LibrarySoundWriter.Write`; Task 7 `NotePlayer`, `PatchView`; Task 8 `LibraryGraph`, `GraphNode`, `GraphEdge`.
- Produces:
  - `public sealed record Crumb(string Label, LibraryFamily Family)`
  - `public sealed record LibraryPatch(string Title, OperatorMatrixPatch Patch, SynthesisProfile Profile, string Scd, IReadOnlyList<string> HeaderComments)`
  - `public sealed class LibraryViewModel : INotifyPropertyChanged` with constructor `(NotePlayer notes, Action<string> status, Action<double> progress, Action<LibraryPatch> showInSearch)`, `Profiles`, `SelectedProfile`, `Crumbs`, `Nodes`, `Edges`, `SelectedIndex`, `GraphMessage`, `ShownPatch`, `Scd`, `SelectionText`, `HasSelection`, `PlayingText`, `CanPlay`, `Task EnsureLoadedAsync()`, `void Select(int index)`, `Task OpenAsync(int index)`, `Task OpenCrumbAsync(Crumb crumb)`, `Task PlayNoteAsync(int midi, double velocity)`, `Task SaveAsync(string directory)`, `void ShowInSearch()`.
  - Private members used by Task 10: `LibraryAtlas? _atlas`, `LibraryFamily? _family`, `IReadOnlyList<LibraryFamily> _children`, `Task ShowFamilyAsync(LibraryFamily family)`, `record Selection`, `void SetSelection(Selection)`.
  - `MainViewModel.Library` (`LibraryViewModel`), `MainViewModel.SelectedTab` (`int`, 0 Search, 1 Library), and `void ShowLibraryPatch(LibraryPatch patch)` as a stub that only sets `SelectedTab = 0` (Task 11 fills it in).

- [ ] **Step 1: Create `LibraryViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>One step of the breadcrumb: a family that was opened.</summary>
/// <param name="Label">Text of the crumb.</param>
/// <param name="Family">The family it opens.</param>
public sealed record Crumb(string Label, LibraryFamily Family);

/// <summary>A sound from the library, as it is handed to the Search tab.</summary>
/// <param name="Title">For example <c>FMX6 #50123456</c>, or an import's name.</param>
/// <param name="Patch">Patch.</param>
/// <param name="Profile">Profile it plays on.</param>
/// <param name="Scd">Its SuperCollider code.</param>
/// <param name="HeaderComments">Header lines of the code, for Save.</param>
public sealed record LibraryPatch(string Title, OperatorMatrixPatch Patch, SynthesisProfile Profile, string Scd, IReadOnlyList<string> HeaderComments);

/// <summary>
/// State of the Library tab: the profile whose library is shown, the family opened, its children as graph nodes, the
/// selected sound with its code, and what the keyboard plays.
/// </summary>
public sealed class LibraryViewModel : INotifyPropertyChanged
{
    private readonly NotePlayer _notes;
    private readonly Action<string> _status;
    private readonly Action<double> _progress;
    private readonly Action<LibraryPatch> _showInSearch;
    private readonly int _size = new PatchMatchOptions().StructureLibrarySize;
    private SynthesisProfile _profile = SynthesisProfiles.SuperColliderFmx6;
    private LibraryAtlas? _atlas;
    private LibraryFamily? _family;
    private IReadOnlyList<LibraryFamily> _children = [];
    private int _loadVersion;
    private bool _loaded;
    private IReadOnlyList<GraphNode>? _nodes;
    private IReadOnlyList<GraphEdge>? _edges;
    private int _selectedIndex = -1;
    private string? _graphMessage = "Open this tab to load the library.";
    private Selection? _selection;
    private string _playingText = string.Empty;

    /// <summary>Creates the view model.</summary>
    /// <param name="notes">Shared note player.</param>
    /// <param name="status">Sets the status line of the window.</param>
    /// <param name="progress">Sets the progress bar of the window, 0..1.</param>
    /// <param name="showInSearch">Shows a sound in the Search tab.</param>
    public LibraryViewModel(NotePlayer notes, Action<string> status, Action<double> progress, Action<LibraryPatch> showInSearch)
    {
        _notes = notes;
        _status = status;
        _progress = progress;
        _showInSearch = showInSearch;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Profiles with a library.</summary>
    public IReadOnlyList<SynthesisProfile> Profiles { get; } = [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6];

    /// <summary>Profile whose library is shown; changing it loads that library.</summary>
    public SynthesisProfile SelectedProfile
    {
        get => _profile;
        set
        {
            if (Set(ref _profile, value) && _loaded)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>The families opened, from all sounds down.</summary>
    public ObservableCollection<Crumb> Crumbs { get; } = [];

    /// <summary>Children of the opened family as graph nodes.</summary>
    public IReadOnlyList<GraphNode>? Nodes
    {
        get => _nodes;
        private set => Set(ref _nodes, value);
    }

    /// <summary>Lines between the nodes.</summary>
    public IReadOnlyList<GraphEdge>? Edges
    {
        get => _edges;
        private set => Set(ref _edges, value);
    }

    /// <summary>Index of the selected node, −1 for none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set => Set(ref _selectedIndex, value);
    }

    /// <summary>Shown instead of the graph while there is none.</summary>
    public string? GraphMessage
    {
        get => _graphMessage;
        private set => Set(ref _graphMessage, value);
    }

    /// <summary>The last clicked sound's patch.</summary>
    public OperatorMatrixPatch? ShownPatch => _selection?.Patch;

    /// <summary>Its SuperCollider code.</summary>
    public string Scd => _selection?.Scd ?? string.Empty;

    /// <summary>One line about it.</summary>
    public string SelectionText => _selection?.Description ?? "Click a node to hear it; double-click a family to open it.";

    /// <summary>True once a sound was clicked.</summary>
    public bool HasSelection => _selection is not null;

    /// <summary>What the keyboard last played.</summary>
    public string PlayingText
    {
        get => _playingText;
        private set => Set(ref _playingText, value);
    }

    /// <summary>True when an audio device could be opened.</summary>
    public bool CanPlay => _notes.CanPlay;

    /// <summary>Loads the library the first time the tab is shown.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return Task.CompletedTask;
        }

        _loaded = true;
        return LoadAsync();
    }

    /// <summary>Selects a node, shows its code and plays it at the reference note.</summary>
    /// <param name="index">Node index.</param>
    public void Select(int index)
    {
        if (_atlas is not { } atlas || index < 0 || index >= _children.Count)
        {
            return;
        }

        LibraryFamily child = _children[index];
        int seed = atlas.Library.Seed(child.Medoid);
        OperatorMatrixPatch patch = atlas.Library.Patch(seed);
        string label = SoundDescriptors.Label(atlas.Library.Fingerprint(child.Medoid), patch, child.Size);
        string[] header =
        [
            string.Create(CultureInfo.InvariantCulture, $"HearSay library {_profile.Name} v{_profile.StructureLibraryVersion}, seed {seed}."),
            label,
        ];
        SelectedIndex = index;
        SetSelection(new Selection($"{ProfileNameConverter.Short(_profile.Name)} #{seed}", patch, _profile, $"#{index + 1} · {label}", header));
    }

    /// <summary>Opens a family node: the graph shows its children.</summary>
    /// <param name="index">Node index.</param>
    public async Task OpenAsync(int index)
    {
        if (index < 0 || index >= _children.Count || _children[index].IsLeaf || _atlas is not { } atlas)
        {
            return;
        }

        LibraryFamily child = _children[index];
        Crumbs.Add(new Crumb(CrumbLabel(atlas, child, index), child));
        await ShowFamilyAsync(child);
    }

    /// <summary>Goes back to a family of the breadcrumb.</summary>
    /// <param name="crumb">Crumb.</param>
    public async Task OpenCrumbAsync(Crumb crumb)
    {
        int at = Crumbs.IndexOf(crumb);
        if (at < 0)
        {
            return;
        }

        while (Crumbs.Count > at + 1)
        {
            Crumbs.RemoveAt(Crumbs.Count - 1);
        }

        await ShowFamilyAsync(crumb.Family);
    }

    /// <summary>Plays the last clicked sound at a note.</summary>
    /// <param name="midiNote">MIDI note.</param>
    /// <param name="velocity">Key velocity 0..1.</param>
    public async Task PlayNoteAsync(int midiNote, double velocity)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        RenderRequest reference = StructureLibrary.ReferenceRequest;
        double hz = 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
        var note = new RenderRequest(hz, midiNote, velocity, reference.NoteDuration, reference.ReleaseDuration, reference.SampleRate);
        if (await _notes.PlayAsync(selection.Patch, selection.Profile, reference, note))
        {
            PlayingText = FormattableString.Invariant($"{PianoKeyboard.NoteName(midiNote)} ({hz:F1} Hz), velocity {velocity:F2}") + (NotePlayer.RespondsToVelocity(selection.Patch) ? string.Empty : " (as volume: this patch has no velocity sensitivity)");
        }
    }

    /// <summary>Writes the selected sound as a folder that imports again.</summary>
    /// <param name="directory">Destination folder.</param>
    public async Task SaveAsync(string directory)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        IReadOnlyList<string> files = await Task.Run(() => LibrarySoundWriter.Write(selection.Patch, selection.Profile, directory, selection.HeaderComments));
        _status($"{files.Count} files written to {directory}.");
    }

    /// <summary>Shows the selected sound in the Search tab.</summary>
    public void ShowInSearch()
    {
        if (_selection is { } selection)
        {
            _showInSearch(new LibraryPatch(selection.Title, selection.Patch, selection.Profile, selection.Scd, selection.HeaderComments));
        }
    }

    private async Task LoadAsync()
    {
        int version = ++_loadVersion;
        SynthesisProfile profile = _profile;
        string name = ProfileNameConverter.Short(profile.Name);
        _atlas = null;
        _family = null;
        _children = [];
        Crumbs.Clear();
        Nodes = null;
        Edges = null;
        SelectedIndex = -1;
        bool onDisk = StructureLibrary.IsAvailable(profile, _size);
        GraphMessage = onDisk
            ? $"Loading the {name} library…"
            : string.Create(CultureInfo.InvariantCulture, $"Building the {name} library ({_size:N0} sounds, once; FMX6 takes about 17 minutes)…");
        var progress = new Progress<double>(f =>
        {
            if (version == _loadVersion)
            {
                _progress(f);
            }
        });
        var stage = new Progress<string>(s =>
        {
            if (version == _loadVersion)
            {
                GraphMessage = s;
            }
        });
        try
        {
            LibraryAtlas atlas = await Task.Run(() =>
            {
                StructureLibrary library = StructureLibrary.For(profile, _size, SynthesisBackends.RendererFor(profile), progress: progress);
                string cache = LibraryAtlas.CacheFileOf(StructureLibrary.FileOf(profile, _size));
                if (!File.Exists(cache))
                {
                    ((IProgress<string>)stage).Report($"Grouping the {name} sounds into families (once, under a minute)…");
                }

                return LibraryAtlas.Open(library, cache);
            });
            if (version != _loadVersion)
            {
                return;
            }

            _atlas = atlas;
            Crumbs.Add(new Crumb("all", atlas.Root));
            await ShowFamilyAsync(atlas.Root);
            _status(string.Create(CultureInfo.InvariantCulture, $"{name} library: {atlas.Library.Count:N0} sounds in {_children.Count} families. Click a node to hear it, double-click a family to open it."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (version == _loadVersion)
            {
                GraphMessage = "The library could not be loaded: " + e.Message;
            }
        }
    }

    private async Task ShowFamilyAsync(LibraryFamily family)
    {
        if (_atlas is not { } atlas)
        {
            return;
        }

        int version = _loadVersion;
        (IReadOnlyList<LibraryFamily> children, List<byte[]> prints, List<OperatorMatrixPatch> patches) = await Task.Run(() =>
        {
            IReadOnlyList<LibraryFamily> c = atlas.Children(family);
            return (c, c.Select(f => atlas.Library.Fingerprint(f.Medoid).ToArray()).ToList(), c.Select(f => atlas.Library.Patch(atlas.Library.Seed(f.Medoid))).ToList());
        });
        if (version != _loadVersion)
        {
            return;
        }

        _family = family;
        _children = children;
        AtlasLayout layout = AtlasLayout.Compute(prints, [.. children.Select(static c => c.Size)]);
        Nodes = [.. children.Select((c, k) => new GraphNode(
            layout.Positions[k].X,
            layout.Positions[k].Y,
            layout.Radii[k],
            Brightness(prints[k]),
            c.IsLeaf ? string.Empty : c.Size.ToString("N0", CultureInfo.InvariantCulture),
            $"#{k + 1} · {SoundDescriptors.Label(prints[k], patches[k], c.Size)}",
            IsImported: false,
            CanOpen: !c.IsLeaf,
            IsMarked: false))];
        Edges = [.. layout.Edges.Select(static e => new GraphEdge(e.From, e.To))];
        SelectedIndex = -1;
        GraphMessage = null;
    }

    private static double Brightness(byte[] print) => Math.Clamp(Math.Log2(SoundDescriptors.BrightnessRatio(print)) / 4.0, 0.0, 1.0);

    private static string CrumbLabel(LibraryAtlas atlas, LibraryFamily family, int index)
    {
        IReadOnlyList<string> words = SoundDescriptors.Of(atlas.Library.Fingerprint(family.Medoid), atlas.Library.Patch(atlas.Library.Seed(family.Medoid)));
        return $"#{index + 1} {string.Join(" · ", words.Take(2))}";
    }

    private void SetSelection(Selection selection)
    {
        _selection = selection;
        OnPropertyChanged(nameof(ShownPatch));
        OnPropertyChanged(nameof(Scd));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        RenderRequest reference = StructureLibrary.ReferenceRequest;
        _ = PlayReferenceAsync(selection, reference);
    }

    private async Task PlayReferenceAsync(Selection selection, RenderRequest reference)
    {
        if (await _notes.PlayAsync(selection.Patch, selection.Profile, reference, reference))
        {
            PlayingText = "A3 (220.0 Hz), the library's reference note";
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed record Selection(string Title, OperatorMatrixPatch Patch, SynthesisProfile Profile, string Description, IReadOnlyList<string> HeaderComments)
    {
        public string Scd { get; } = SynthesisBackends.ExporterFor(Profile)?.Export(Patch, new PatchExportOptions { HeaderComments = HeaderComments }) ?? "This profile has no export backend.";
    }
}
```

- [ ] **Step 2: Create `LibraryView`**

`LibraryView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="using:HearSay.Studio"
             xmlns:profiles="using:HearSay.Synthesis.Profiles"
             x:Class="HearSay.Studio.LibraryView"
             x:DataType="local:LibraryViewModel">
  <Grid RowDefinitions="*,Auto" ColumnDefinitions="3*,2*" RowSpacing="12" ColumnSpacing="12">

    <!-- Graph: profile, breadcrumb, families -->
    <Border Grid.Row="0" Grid.Column="0" Classes="card">
      <Grid RowDefinitions="Auto,Auto,*" RowSpacing="8">
        <Grid Grid.Row="0" ColumnDefinitions="Auto,Auto,*" ColumnSpacing="10">
          <TextBlock Grid.Column="0" Classes="title" Text="LIBRARY" Margin="0" VerticalAlignment="Center" />
          <ComboBox Grid.Column="1" Width="110" ItemsSource="{Binding Profiles}" SelectedItem="{Binding SelectedProfile}">
            <ComboBox.ItemTemplate>
              <DataTemplate x:DataType="profiles:SynthesisProfile">
                <TextBlock Text="{Binding Name, Converter={x:Static local:ProfileNameConverter.Instance}}" />
              </DataTemplate>
            </ComboBox.ItemTemplate>
          </ComboBox>
        </Grid>
        <ItemsControl Grid.Row="1" ItemsSource="{Binding Crumbs}">
          <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate><WrapPanel Orientation="Horizontal" /></ItemsPanelTemplate>
          </ItemsControl.ItemsPanel>
          <ItemsControl.ItemTemplate>
            <DataTemplate x:DataType="local:Crumb">
              <StackPanel Orientation="Horizontal" Spacing="2">
                <Button Content="{Binding Label}" Click="OnCrumb" Padding="6,2" FontSize="11" Background="Transparent" Foreground="{StaticResource AccentBrush}" />
                <TextBlock Text="›" Classes="muted" VerticalAlignment="Center" />
              </StackPanel>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
        <Grid Grid.Row="2" ColumnDefinitions="*">
          <Border Grid.Column="0" CornerRadius="6" ClipToBounds="True">
            <local:LibraryGraph x:Name="Graph" Nodes="{Binding Nodes}" Edges="{Binding Edges}" SelectedIndex="{Binding SelectedIndex}" Message="{Binding GraphMessage}" NodeClicked="OnNodeClicked" NodeOpened="OnNodeOpened" />
          </Border>
        </Grid>
      </Grid>
    </Border>

    <!-- The selected sound -->
    <Border Grid.Row="0" Grid.Column="1" Classes="card">
      <DockPanel>
        <Grid DockPanel.Dock="Top" ColumnDefinitions="*,Auto,Auto,Auto" ColumnSpacing="8" Margin="0,0,0,8">
          <TextBlock Grid.Column="0" Classes="title" Text="PATCH" Margin="0" VerticalAlignment="Center" />
          <Button Grid.Column="1" Content="Copy" Click="OnCopy" IsEnabled="{Binding HasSelection}" ToolTip.Tip="Copy the SuperCollider code" />
          <Button Grid.Column="2" Content="Save…" Click="OnSave" IsEnabled="{Binding HasSelection}" ToolTip.Tip="Write patch.json, patch.scd and preview.wav to a folder; it can be imported again" />
          <Button Grid.Column="3" Content="Show in Search" Click="OnShowInSearch" IsEnabled="{Binding HasSelection}" ToolTip.Tip="Show this sound as the patch of the Search tab" />
        </Grid>
        <TextBlock DockPanel.Dock="Bottom" Text="{Binding SelectionText}" Classes="muted" FontSize="11" TextWrapping="Wrap" Margin="0,8,0,0" />
        <local:PatchView Code="{Binding Scd}" Patch="{Binding ShownPatch}" Watermark="Click a node to see its code." />
      </DockPanel>
    </Border>

    <!-- Keyboard -->
    <Border Grid.Row="1" Grid.Column="0" Grid.ColumnSpan="2" Classes="card" Padding="14,10">
      <StackPanel Spacing="4">
        <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8">
          <TextBlock Grid.Column="0" Text="play the last clicked sound" Classes="muted" FontSize="10" VerticalAlignment="Center" />
          <TextBlock Grid.Column="1" Text="{Binding PlayingText}" FontSize="10" Foreground="{StaticResource AccentBrush}" VerticalAlignment="Center" />
          <TextBlock Grid.Column="2" Text="lower on a key = louder" Classes="muted" FontSize="9" VerticalAlignment="Center" />
        </Grid>
        <ScrollViewer x:Name="KeyboardScroll" HorizontalScrollBarVisibility="Visible" VerticalScrollBarVisibility="Disabled" AllowAutoHide="False" LayoutUpdated="OnKeyboardLayoutUpdated">
          <local:PianoKeyboard x:Name="Keyboard" Height="80" Margin="0,0,0,18" LowestNote="0" HighestNote="127" WhiteKeyWidth="26" NotePressed="OnNotePressed" />
        </ScrollViewer>
        <TextBlock Text="No audio device could be opened; the keyboard is silent." Classes="muted" FontSize="10" Foreground="{StaticResource AmberBrush}" IsVisible="{Binding !CanPlay}" />
      </StackPanel>
    </Border>
  </Grid>
</UserControl>
```

If `ProfileNameConverter` does not convert a profile name string (check `Converters.cs`: it takes the name), use `{Binding Name}` plus the converter as written; if it expects the profile, bind `{Binding}` instead.

`LibraryView.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace HearSay.Studio;

/// <summary>The Library tab. Pickers and the clipboard live here; everything else is in <see cref="LibraryViewModel"/>.</summary>
public sealed partial class LibraryView : UserControl
{
    private bool _keyboardCentred;

    public LibraryView()
    {
        InitializeComponent();
    }

    private LibraryViewModel ViewModel => (LibraryViewModel)DataContext!;

    private void OnNodeClicked(object? sender, int index) => ViewModel.Select(index);

    private async void OnNodeOpened(object? sender, int index) => await ViewModel.OpenAsync(index);

    private async void OnCrumb(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Crumb crumb })
        {
            await ViewModel.OpenCrumbAsync(crumb);
        }
    }

    private async void OnNotePressed(object? sender, KeyPress e) => await ViewModel.PlayNoteAsync(e.MidiNote, e.Velocity);

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && ViewModel.Scd.Length > 0)
        {
            await clipboard.SetTextAsync(ViewModel.Scd);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder for patch.json, patch.scd and preview.wav",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is string directory)
        {
            await ViewModel.SaveAsync(directory);
        }
    }

    private void OnShowInSearch(object? sender, RoutedEventArgs e) => ViewModel.ShowInSearch();

    /// <summary>Puts A3, the library's reference note, in the middle of the keyboard once it has a size.</summary>
    private void OnKeyboardLayoutUpdated(object? sender, EventArgs e)
    {
        double viewport = KeyboardScroll.Viewport.Width;
        if (_keyboardCentred || viewport <= 0 || KeyboardScroll.Extent.Width <= viewport)
        {
            return;
        }

        KeyboardScroll.Offset = new Vector(Math.Max(0, Keyboard.CentreOf(57) - (viewport / 2)), 0);
        _keyboardCentred = true;
    }
}
```

- [ ] **Step 3: Add the tab to the main window**

In `MainViewModel`: add

```csharp
    private int _selectedTab;

    /// <summary>The Library tab.</summary>
    public LibraryViewModel Library { get; }

    /// <summary>Selected tab: 0 Search, 1 Library. Showing the Library tab loads its library the first time.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value) && value == 1)
            {
                _ = Library.EnsureLoadedAsync();
            }
        }
    }

    /// <summary>Shows a library sound in the Search tab.</summary>
    /// <param name="patch">The sound.</param>
    public void ShowLibraryPatch(LibraryPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        SelectedTab = 0;
    }
```

and in the constructor, after `Notes` is created: `Library = new LibraryViewModel(Notes, s => Status = s, p => Progress = p, ShowLibraryPatch);`

In `MainWindow.axaml`, wrap the body grid (the element with comment `<!-- Body -->`, `Grid.Row="1" ColumnDefinitions="2*,3*"`) in a tab control: move `Grid.Row="1"` from that grid to the tab control, and add the Library tab:

```xml
    <!-- Body: the search and the library browser -->
    <TabControl Grid.Row="1" SelectedIndex="{Binding SelectedTab}" Padding="0,8,0,0">
      <TabItem Header="Search" FontSize="15">
        <Grid ColumnDefinitions="2*,3*" ColumnSpacing="12">
          … the existing left and right columns, unchanged …
        </Grid>
      </TabItem>
      <TabItem Header="Library" FontSize="15">
        <local:LibraryView DataContext="{Binding Library}" />
      </TabItem>
    </TabControl>
```

- [ ] **Step 4: Build**

Run: `dotnet build src/HearSay.Studio -c Debug -nologo -v q`
Expected: `0 Error(s)`.

- [ ] **Step 5: Check the tab headless**

Extend the Task 8 scratch project: create `MainViewModel`, set `SelectedTab = 1`, await until `Library.Nodes` is not null (poll with `Dispatcher.UIThread.RunJobs()` and a 2-minute timeout; the FMX6 library and its atlas are on disk after the first Studio run, otherwise use FM7 by setting `Library.SelectedProfile` first), render a 1220×860 `MainWindow` with that DataContext to PNG. Look at it: the Library tab with the graph of about 40 families, the breadcrumb `all ›`, the empty patch panel with its watermark, the keyboard below. Then call `Library.Select(0)` and `await Library.OpenAsync(0)` and render again: code and diagram on the right, breadcrumb `all › #1 …`, the children of family 1.

- [ ] **Step 6: Click through by hand**

Run `dotnet run --project src/HearSay.Studio -c Debug`. Open the Library tab: the FMX6 library loads (the first time the family tree is built: under a minute, message in the graph). Click nodes: each plays; the code and diagram follow. Click several nodes fast: only the last is heard. Double-click a family and use the plus: its children appear; the breadcrumb goes back. Open down to single sounds. Play the keyboard: the last clicked sound at other pitches. Copy: code on the clipboard with the seed in the header. Save: a folder with patch.json, patch.scd, preview.wav, search-summary.json. Switch to FM7 while FMX6 still loads (delete `%LOCALAPPDATA%\HearSay\structure-libraries\supercollider-fmx6-v2-300000-atlas-v1.bin` first to make the load slow): the FM7 tree appears and stays, the FMX6 result does not replace it.

- [ ] **Step 7: Commit**

```bash
git add src/HearSay.Studio/LibraryViewModel.cs src/HearSay.Studio/LibraryView.axaml src/HearSay.Studio/LibraryView.axaml.cs src/HearSay.Studio/MainViewModel.cs src/HearSay.Studio/MainWindow.axaml
git commit -m "Studio: a Library tab to browse the structure library by sound family, hear and copy any sound"
```

---

### Task 10: Studio — importing exported sounds

**Files:**
- Modify: `src/HearSay.Studio/LibraryViewModel.cs`, `src/HearSay.Studio/LibraryView.axaml`, `src/HearSay.Studio/LibraryView.axaml.cs`

**Interfaces:**
- Consumes: Task 6 `ExportedSounds.Scan/Fingerprint`, `ExportedSound`; Task 3 `LibraryAtlas.Locate`; Task 5 `AtlasLayout.CenterOn`; Task 9 private members.
- Produces:
  - `public sealed record ImportedItem(ExportedSound Sound, byte[] Print)` with `string Name`, `string ProfileName`.
  - `LibraryViewModel.Imports` (`ObservableCollection<ImportedItem>`), `Task ImportAsync(string directory)`, `Task PlaceAsync(ImportedItem item)`.

- [ ] **Step 1: Add the import model and commands**

Add to `LibraryViewModel.cs` (top level, next to `Crumb`):

```csharp
/// <summary>An imported sound with its fingerprint.</summary>
/// <param name="Sound">The sound.</param>
/// <param name="Print">Its fingerprint, rendered as a library sound is.</param>
public sealed record ImportedItem(ExportedSound Sound, byte[] Print)
{
    /// <summary>Name in the list.</summary>
    public string Name => Sound.Name;

    /// <summary>Profile it plays on, short.</summary>
    public string ProfileName => ProfileNameConverter.Short(Sound.Profile.Name);
}
```

Add to `LibraryViewModel`:

```csharp
    private (ImportedItem Item, IReadOnlyList<LibraryFamily> Path)? _placed;

    /// <summary>Sounds imported this session.</summary>
    public ObservableCollection<ImportedItem> Imports { get; } = [];

    /// <summary>Reads the exported sounds under a folder and adds them to the list.</summary>
    /// <param name="directory">Folder.</param>
    public async Task ImportAsync(string directory)
    {
        _status("Reading " + directory + "…");
        (ExportedSoundScan scan, List<ImportedItem> items) = await Task.Run(() =>
        {
            ExportedSoundScan s = ExportedSounds.Scan(directory);
            return (s, s.Sounds.AsParallel().AsOrdered().Select(static sound => new ImportedItem(sound, ExportedSounds.Fingerprint(sound))).ToList());
        });
        int added = 0;
        foreach (ImportedItem item in items.Where(i => Imports.All(known => known.Sound.PatchFile != i.Sound.PatchFile)))
        {
            Imports.Add(item);
            added++;
        }

        if (scan.Sounds.Count == 0 && scan.Skipped.Count == 0)
        {
            _status($"No exported sounds (patch.json) under {directory}.");
            return;
        }

        string skipped = scan.Skipped.Count == 0
            ? string.Empty
            : $", {scan.Skipped.Count} skipped: {string.Join("; ", scan.Skipped.Take(3))}{(scan.Skipped.Count > 3 ? "; …" : string.Empty)}";
        _status($"{added} sound(s) imported{skipped}. Click one in the list to place it in the library.");
    }

    /// <summary>Opens the family an imported sound falls in and shows it there as a star, selected and centred.</summary>
    /// <param name="item">Imported sound.</param>
    public async Task PlaceAsync(ImportedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_atlas is not { } atlas)
        {
            _status("The library is not ready yet; place the sound when it is.");
            return;
        }

        IReadOnlyList<LibraryFamily> path = await Task.Run(() => atlas.Locate(item.Print));
        Crumbs.Clear();
        Crumbs.Add(new Crumb("all", atlas.Root));
        for (int k = 1; k < path.Count; k++)
        {
            Crumbs.Add(new Crumb(CrumbLabel(atlas, path[k], path[k].Path[^1]), path[k]));
        }

        _placed = (item, path);
        await ShowFamilyAsync(path[^1]);
        Select(_children.Count);
        _status($"{item.Name} placed among {_children.Count} {(path[^1].Size <= LibraryAtlas.Branching ? "sounds" : "families")} like it; one level up shows the neighbouring families.");
    }
```

- [ ] **Step 2: Show the star, the marked path, and select imports**

In `ShowFamilyAsync`, after computing `prints`, `patches` and before building `Nodes`, replace the layout and node building with:

```csharp
        _family = family;
        _children = children;
        ImportedItem? star = _placed is { } placed && ReferenceEquals(placed.Path[^1], family) ? placed.Item : null;
        int marked = -1;
        if (_placed is { } p)
        {
            int at = p.Path.ToList().FindIndex(f => ReferenceEquals(f, family));
            if (at >= 0 && at + 1 < p.Path.Count)
            {
                marked = p.Path[at + 1].Path[^1];
            }
        }

        List<byte[]> layoutPrints = star is null ? prints : [.. prints, star.Print];
        List<int> sizes = [.. children.Select(static c => c.Size)];
        if (star is not null)
        {
            sizes.Add(1);
        }

        AtlasLayout layout = AtlasLayout.Compute(layoutPrints, sizes);
        if (star is not null)
        {
            layout = layout.CenterOn(children.Count);
        }

        var nodes = children.Select((c, k) => new GraphNode(
            layout.Positions[k].X,
            layout.Positions[k].Y,
            layout.Radii[k],
            Brightness(prints[k]),
            c.IsLeaf ? string.Empty : c.Size.ToString("N0", CultureInfo.InvariantCulture),
            $"#{k + 1} · {SoundDescriptors.Label(prints[k], patches[k], c.Size)}",
            IsImported: false,
            CanOpen: !c.IsLeaf,
            IsMarked: k == marked)).ToList();
        if (star is not null)
        {
            int s = children.Count;
            nodes.Add(new GraphNode(
                layout.Positions[s].X,
                layout.Positions[s].Y,
                layout.Radii[s],
                Brightness(star.Print),
                star.Name,
                $"{star.Name} · {string.Join(" · ", SoundDescriptors.Of(star.Print, star.Sound.Patch))} · imported, {star.ProfileName}",
                IsImported: true,
                CanOpen: false,
                IsMarked: false));
        }

        Nodes = nodes;
        Edges = [.. layout.Edges.Select(static e => new GraphEdge(e.From, e.To))];
        SelectedIndex = -1;
        GraphMessage = null;
```

In `Select`, before the library branch, handle the star:

```csharp
        if (index == _children.Count && _placed is { } placed && ReferenceEquals(placed.Path[^1], _family))
        {
            ImportedItem item = placed.Item;
            SelectedIndex = index;
            SetSelection(new Selection(
                item.Name,
                item.Sound.Patch,
                item.Sound.Profile,
                $"{item.Name} · imported from {item.Sound.PatchFile}",
                [$"Imported from {item.Sound.PatchFile}."]));
            return;
        }
```

(The existing guard `index >= _children.Count` then stays for library nodes, after this branch.) In `LoadAsync`, clear `_placed = null;` together with the other fields.

- [ ] **Step 3: Add the list and the button to the view**

In `LibraryView.axaml`, add to the first row grid of the graph card a fourth column and the button:

```xml
        <Grid Grid.Row="0" ColumnDefinitions="Auto,Auto,*,Auto" ColumnSpacing="10">
          … LIBRARY title and profile ComboBox as before …
          <Button Grid.Column="3" Content="Import folder…" Click="OnImport" ToolTip.Tip="Read the patch.json files of exported results under a folder; click one in the list to place it among the library sounds like it" />
        </Grid>
```

and replace the `Grid Grid.Row="2" ColumnDefinitions="*"` with a list column:

```xml
        <Grid Grid.Row="2" ColumnDefinitions="Auto,*" ColumnSpacing="8">
          <ListBox Grid.Column="0" Width="170" ItemsSource="{Binding Imports}" Background="{StaticResource EditorBackgroundBrush}" CornerRadius="6" IsVisible="{Binding Imports.Count}" Tapped="OnImportTapped">
            <ListBox.ItemTemplate>
              <DataTemplate x:DataType="local:ImportedItem">
                <StackPanel>
                  <TextBlock Text="{Binding Name}" FontSize="11" TextTrimming="CharacterEllipsis" />
                  <TextBlock Text="{Binding ProfileName}" Classes="muted" FontSize="9" />
                </StackPanel>
              </DataTemplate>
            </ListBox.ItemTemplate>
          </ListBox>
          <Border Grid.Column="1" CornerRadius="6" ClipToBounds="True">
            <local:LibraryGraph x:Name="Graph" Nodes="{Binding Nodes}" Edges="{Binding Edges}" SelectedIndex="{Binding SelectedIndex}" Message="{Binding GraphMessage}" NodeClicked="OnNodeClicked" NodeOpened="OnNodeOpened" />
          </Border>
        </Grid>
```

`IsVisible="{Binding Imports.Count}"` relies on Avalonia converting a non-zero int to true; if the build rejects it under compiled bindings, add `public bool HasImports => Imports.Count > 0;` raised from `ImportAsync` and bind to that.

In `LibraryView.axaml.cs`:

```csharp
    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder of exported results (patch.json)",
            AllowMultiple = false,
        });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is string directory)
        {
            await ViewModel.ImportAsync(directory);
        }
    }

    private async void OnImportTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (e.Source is Control { DataContext: ImportedItem item })
        {
            await ViewModel.PlaceAsync(item);
        }
    }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/HearSay.Studio -c Debug -nologo -v q`
Expected: `0 Error(s)`.

- [ ] **Step 5: Check by hand**

Run the Studio. In the Library tab press Import folder… and pick a folder holding a few result folders (save two library sounds with Save… first, and use `demo-results` if it holds `patch.json` files). The list fills; the status line counts imported and skipped. Click an entry: the breadcrumb jumps to where it falls, the star sits in the middle among its neighbours, selected, and plays; its code shows with the import header. Click its neighbours to compare. Click the crumb one level up: the family holding it has a dashed amber ring. A library sound saved with Save… and imported again lands next to (on top of) its own node.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.Studio/LibraryViewModel.cs src/HearSay.Studio/LibraryView.axaml src/HearSay.Studio/LibraryView.axaml.cs
git commit -m "Studio: import exported sounds and place them among the library sounds like them"
```

---

### Task 11: Show in Search, and documentation

**Files:**
- Modify: `src/HearSay.Studio/MainViewModel.cs`, `src/HearSay.Studio/MainWindow.axaml`, `src/HearSay.Studio/MainWindow.axaml.cs`
- Modify: `docs/expert-manual.md` (section 9), `README.md` (architecture line of `HearSay.Studio`)

**Interfaces:**
- Consumes: Task 9 `LibraryPatch`, `ShowLibraryPatch` stub; Task 7 `Notes.PlayAsync`; Task 6 `LibrarySoundWriter.Write`.
- Produces: `MainViewModel.HasPatch` (`bool`); library-patch mode.

- [ ] **Step 1: Library-patch mode in `MainViewModel`**

Add the field `private LibraryPatch? _libraryPatch;` and:

```csharp
    /// <summary>True when a patch is shown: a search result or a sound from the library.</summary>
    public bool HasPatch => _result is not null || _libraryPatch is not null;
```

Replace the stub `ShowLibraryPatch` with:

```csharp
    /// <summary>Shows a library sound as the patch of the Search tab: code, diagram and keyboard; the next search replaces it.</summary>
    /// <param name="patch">The sound.</param>
    public void ShowLibraryPatch(LibraryPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        SetResult(null);
        _libraryPatch = patch;
        _shownPatch = patch.Patch;
        OnPropertyChanged(nameof(ShownPatch));
        Scd = patch.Scd;
        string routing = PatchDescription.Routing(patch.Patch);
        int matrixAt = routing.IndexOf("modulation matrix", StringComparison.Ordinal);
        ResultSummary = $"from the library: {patch.Title}\n\n{(matrixAt > 0 ? routing[..matrixAt].TrimEnd() : routing)}";
        OnPropertyChanged(nameof(HasPatch));
        SelectedTab = 0;
        Status = $"Showing {patch.Title} from the library. The next search replaces it.";
    }
```

In `SetResult`, set `_libraryPatch = null;` as its first statement and add `OnPropertyChanged(nameof(HasPatch));` next to `OnPropertyChanged(nameof(HasResult));`.

In `PlayNoteAsync`, before the result branch:

```csharp
        if (_libraryPatch is { } library)
        {
            RenderRequest reference = HearSay.OperatorMatrix.Priors.StructureLibrary.ReferenceRequest;
            double libraryHz = 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
            var libraryNote = new RenderRequest(libraryHz, midiNote, velocity, reference.NoteDuration, reference.ReleaseDuration, reference.SampleRate);
            if (await Notes.PlayAsync(library.Patch, library.Profile, reference, libraryNote))
            {
                PlayingText = FormattableString.Invariant($"{PianoKeyboard.NoteName(midiNote)} ({libraryHz:F1} Hz), velocity {velocity:F2}");
            }

            return;
        }
```

In `SaveAsync`, before the result branch:

```csharp
        if (_libraryPatch is { } library)
        {
            IReadOnlyList<string> written = await Task.Run(() => LibrarySoundWriter.Write(library.Patch, library.Profile, directory, library.HeaderComments));
            Status = $"{written.Count} files written to {directory}.";
            return;
        }
```

- [ ] **Step 2: Bindings**

In `MainWindow.axaml` (Search tab):
- the Copy and "Save result folder…" buttons: `IsEnabled="{Binding HasPatch}"`;
- the keyboard `StackPanel` (`Grid.Row="3"`): `IsVisible="{Binding HasPatch}"`;
- the matrix-and-summary grid (`Grid.Row="4"`): `IsVisible="{Binding HasPatch}"`, and its inner matrix `StackPanel Grid.Column="0"`: `IsVisible="{Binding HasResult}"`;
- the hint `Run a search to see the result here.`: `IsVisible="{Binding !HasPatch}"`.

In `MainWindow.axaml.cs` `OnDataContextChanged`, re-centre the keyboard on `nameof(MainViewModel.HasPatch)` instead of `HasResult`.

- [ ] **Step 3: Build and check by hand**

Run: `dotnet build src/HearSay.Studio -c Debug -nologo -v q`
Expected: `0 Error(s)`.
Run the Studio: in the Library tab click a sound and press Show in Search. The Search tab opens with the code and the diagram of that sound, the summary starts `from the library: FMX6 #…`, there are no stat tiles, the keyboard plays it, Copy copies it, Save writes the four files. Run a search: the result replaces it. Show a library sound again, then switch the Target selector to a target with a kept result: the kept result replaces it.

- [ ] **Step 4: Documentation**

In `docs/expert-manual.md`, at the end of section 9 (before section 10), add:

```markdown
### The Library tab

The Library tab shows the structure library of FM7, FMX6 or Nome6 as sound families: k-means on the spectral
fingerprints splits the 300 000 sounds into 40 families, each family into 40 more, down to single sounds. Every node is
a real sound, the member closest to its family's centre; nodes are sized by the number of sounds and coloured from
dark (blue) to bright (yellow), and each is joined to its two nearest neighbours.

- Click a node to hear it at A3 (the note the library was rendered at) and see its code and block diagram; the
  keyboard plays the last clicked sound at any pitch. Double-click a family, or click the plus, to open it; the
  breadcrumb goes back up. Resting the pointer on a node shows a few words about it: brightness, envelope, the
  waveform of the loudest carrier, *filtered*, *noisy*, and the number of sounds.
- **Copy** copies the SuperCollider code, with the library and seed in its header. **Save…** writes `patch.json`,
  `patch.scd`, `preview.wav` and a `search-summary.json` with the profile, a folder that can be imported again.
  **Show in Search** makes the sound the patch of the Search tab, until the next search.
- **Import folder…** reads every `patch.json` (and the alternatives) under a folder of exported results. Click one in
  the list to place it: the tab opens the family whose centre is closest at every level and shows the sound there as a
  star among the sounds like it. One level up, the family it fell in has a dashed ring. Placement is by sound, so an
  FM7 export can be placed in the FMX6 library.

The first time a library is opened its top level is grouped (under a minute) and kept next to the library as
`…-atlas-v1.bin`; a library missing on disk is built first (FMX6: about 17 minutes).
```

In `README.md`, change the architecture line of `HearSay.Studio` to:

```
│   └── HearSay.Studio                    Avalonia desktop app: load WAVs, run the search, play the result on a keyboard, show patch.scd, browse the structure library by sound family
```

(keep the column alignment of the surrounding lines).

- [ ] **Step 5: Run all fast test suites**

Run: `for p in Synthesis Audio Modeling OperatorMatrix SuperCollider; do dotnet test tests/HearSay.$p.Tests -c Release -nologo -v q; done` and `dotnet test tests/HearSay.IntegrationTests -c Release --filter "FullyQualifiedName~ExportedSoundsTests|FullyQualifiedName~PatchDescriptionTests"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/HearSay.Studio/MainViewModel.cs src/HearSay.Studio/MainWindow.axaml src/HearSay.Studio/MainWindow.axaml.cs docs/expert-manual.md README.md
git commit -m "Studio: show a library sound in the Search tab; document the Library tab"
```
