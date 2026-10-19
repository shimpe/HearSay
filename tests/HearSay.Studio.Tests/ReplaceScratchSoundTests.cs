using HearSay.Audio;
using HearSay.Editing;
using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>
/// A sound made from scratch, edited and not kept, is asked about before an action replaces it in its tab: Replace goes
/// on, Cancel leaves everything as it was. The view models get a real live player on a settings file and a favourites
/// folder of the test's own, a fake gain, a fake question that counts how often it is asked and answers as the test says,
/// and in the Library tab a small library of twenty FM7 sounds built in memory.
/// </summary>
[Collection("InterfaceThread")]
public sealed class ReplaceScratchSoundTests : IDisposable
{
    private static readonly Lazy<LibraryAtlas> SmallLibrary = new(static () =>
    {
        SynthesisProfile fm7 = SynthesisProfiles.SuperColliderFm7;
        LibraryAtlas atlas = LibraryAtlas.Build(StructureLibrary.Build(fm7.Capabilities, 20, SynthesisBackends.RendererFor(fm7)));
        atlas.Children(atlas.Root);
        return atlas;
    });

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-studio-replace-" + Guid.NewGuid().ToString("N"));
    private int _asked;
    private bool _answer;

    public ReplaceScratchSoundTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Task<float> FakeGain(Sound sound) => Task.FromResult(sound.FixedGain ?? 0.5f);

    private FavouriteSounds Favourites(string folder) => new(Path.Combine(_directory, folder));

    private MainViewModel Main(string favourites = "favourites")
    {
        var main = new MainViewModel(Favourites(favourites), Path.Combine(_directory, "studio-settings.json"), FakeGain, static _ => SmallLibrary.Value);
        main.AskToReplace = Ask;
        main.Library.AskToReplace = Ask;
        main.Library.SelectedProfile = SynthesisProfiles.SuperColliderFm7;
        return main;
    }

    private Task<bool> Ask()
    {
        _asked++;
        return Task.FromResult(_answer);
    }

    private static string Hash(OperatorMatrixPatch? patch) => Assert.IsType<OperatorMatrixPatch>(patch).ComputeHash();

    private static SynthesisTargetChoice Target(MainViewModel main, string label) => main.SynthesisTargets.Single(t => t.Label == label);

    private static void Edit(PatchEditorViewModel edits) => Assert.False(edits.Apply(PatchEdits.AddModulator(0)).IsRefused);

    /// <summary>A plucked note at 220 Hz, a target the search can take.</summary>
    private string WriteTarget(string name)
    {
        const int rate = 44100;
        var samples = new float[rate * 3 / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / rate;
            samples[i] = (float)(0.5 * Math.Exp(-3.0 * t) * (Math.Sin(2 * Math.PI * 220 * t) + (0.4 * Math.Sin(2 * Math.PI * 440 * t)) + (0.2 * Math.Sin(2 * Math.PI * 660 * t))));
        }

        string path = Path.Combine(_directory, name);
        WavFile.Write(path, samples, rate);
        return path;
    }

    [Fact]
    public async Task ReplacingAnUnsavedScratchEdit_AsksFirst()
    {
        Favourite pad = Favourites("favourites").Add("pad", ReferencePatches.Find("electric-piano")!.Patch, SynthesisProfiles.SuperColliderFm7, "search best");
        using MainViewModel main = Main();
        LibraryViewModel library = main.Library;
        Assert.True(await main.AddAsync(WriteTarget("pluck.wav")));
        main.UseStructureLibrary = false;
        main.Budget = 200;
        await library.EnsureLoadedAsync();
        Assert.Equal(0, _asked);

        // Search tab: a new sound, edited, not kept.
        main.StartFromScratch();
        Edit(main.Edits);
        string edit = Hash(main.ShownPatch);
        Assert.True(main.ShowsUnsavedScratchEdit);

        // Cancel: the search does not start, the Target selector goes back, no target is added; the sound stays.
        _answer = false;
        await main.RunAsync();
        Assert.Equal(1, _asked);
        Assert.False(main.IsRunning);
        Assert.Equal(edit, Hash(main.ShownPatch));

        var raised = new List<string?>();
        main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        main.SelectedSynthesisTarget = Target(main, "FMX6");
        Assert.Equal(2, _asked);
        Assert.Same(Target(main, "FM7"), main.SelectedSynthesisTarget);
        Assert.Contains(nameof(MainViewModel.SelectedSynthesisTarget), raised);
        Assert.Equal(edit, Hash(main.ShownPatch));

        Assert.False(await main.AddAsync(WriteTarget("second.wav")));
        Assert.Equal(3, _asked);
        Assert.Single(main.Targets);
        Assert.Equal(edit, Hash(main.ShownPatch));

        // Library tab: the same new sound on the same profile, with the same edit.
        library.StartFromScratch();
        Assert.Equal(edit, Hash(library.ShownPatch));
        Assert.True(library.ShowsUnsavedScratchEdit);

        // "Show in Search" from the Library tab: the Search tab is shown for the question, and its sound stays.
        main.SelectedTab = 1;
        await library.ShowInSearchAsync();
        Assert.Equal(4, _asked);
        Assert.Equal(0, main.SelectedTab);
        Assert.Equal(edit, Hash(main.ShownPatch));
        Assert.True(main.Edits.ShowsEdit);

        // Cancel in the Library tab: a node click, a favourite placed and a sound found from the Search tab.
        await library.SelectAsync(0);
        Assert.Equal(5, _asked);
        Assert.Equal(-1, library.SelectedIndex);
        Assert.Equal(edit, Hash(library.ShownPatch));

        await library.PlaceFavouriteAsync(library.Favourites.Single(f => f.Favourite.Directory == pad.Directory));
        Assert.Equal(6, _asked);
        Assert.Equal(edit, Hash(library.ShownPatch));

        await library.FindAsync("search best", "a search", ReferencePatches.Find("bell")!.Patch, SynthesisProfiles.SuperColliderFm7);
        Assert.Equal(7, _asked);
        Assert.Empty(library.Imports);
        Assert.Equal(edit, Hash(library.ShownPatch));
        Assert.Equal("made from scratch · edited", library.SelectionText);

        // Replace: the node is selected.
        _answer = true;
        await library.SelectAsync(0);
        Assert.Equal(8, _asked);
        Assert.Equal(0, library.SelectedIndex);
        Assert.NotEqual(edit, Hash(library.ShownPatch));

        // Replace: the target changes and its missing result empties the tab; a new start brings the edit back.
        main.SelectedSynthesisTarget = Target(main, "FMX6");
        Assert.Equal(9, _asked);
        Assert.Same(Target(main, "FMX6"), main.SelectedSynthesisTarget);
        Assert.Null(main.ShownPatch);
        main.SelectedSynthesisTarget = Target(main, "FM7");
        Assert.Equal(9, _asked);
        main.StartFromScratch();
        Assert.Equal(edit, Hash(main.ShownPatch));

        // Replace: the search starts.
        Task run = main.RunAsync();
        Assert.Equal(10, _asked);
        Assert.True(main.IsRunning);
        Assert.Null(main.ShownPatch);
        main.Cancel();
        await run;
        Assert.False(main.IsRunning);
    }

    [Fact]
    public async Task ReplacingAnUneditedOrKeptScratchSound_DoesNotAsk()
    {
        using (MainViewModel main = Main())
        {
            LibraryViewModel library = main.Library;
            await library.EnsureLoadedAsync();

            // Unedited: nothing is lost.
            main.StartFromScratch();
            Assert.False(main.ShowsUnsavedScratchEdit);
            main.SelectedSynthesisTarget = Target(main, "FMX6");
            Assert.Null(main.ShownPatch);
            library.StartFromScratch();
            Assert.False(library.ShowsUnsavedScratchEdit);
            await library.SelectAsync(0);
            Assert.Equal(0, library.SelectedIndex);

            // Edited and kept with ☆: the favourite has it.
            main.StartFromScratch();
            Edit(main.Edits);
            Assert.True(main.ShowsUnsavedScratchEdit);
            main.PrepareFavouriteName();
            await main.SaveFavouriteAsync();
            Assert.True(main.IsShownFavourite);
            Assert.False(main.ShowsUnsavedScratchEdit);
            main.SelectedSynthesisTarget = Target(main, "Nome6");
            Assert.Null(main.ShownPatch);
        }

        // The same in the Library tab.
        using (MainViewModel main = Main("favourites-2"))
        {
            LibraryViewModel library = main.Library;
            await library.EnsureLoadedAsync();
            library.StartFromScratch();
            Edit(library.Edits);
            Assert.True(library.ShowsUnsavedScratchEdit);
            Assert.NotNull(await library.SaveFavouriteAsync("kept", library.ShownPatch!, SynthesisProfiles.SuperColliderFm7, ScratchSound.Origin));
            Assert.True(library.IsShownFavourite);
            Assert.False(library.ShowsUnsavedScratchEdit);
            await library.SelectAsync(1);
            Assert.Equal(1, library.SelectedIndex);
        }

        Assert.Equal(0, _asked);
    }

    [Fact]
    public async Task OtherSounds_AreReplacedWithoutAsking()
    {
        using MainViewModel main = Main();
        LibraryViewModel library = main.Library;
        await library.EnsureLoadedAsync();

        // An edited library sound shown in the Search tab.
        OperatorMatrixPatch bell = ReferencePatches.Find("bell")!.Patch;
        main.ShowLibraryPatch(new LibraryPatch("bell", bell, SynthesisProfiles.SuperColliderFm7, ["A bell."]));
        Edit(main.Edits);
        Assert.False(main.ShowsUnsavedScratchEdit);
        main.SelectedSynthesisTarget = Target(main, "FMX6");
        Assert.Null(main.ShownPatch);

        // An edited library node, replaced by another node, and one shown in Search over another edited sound.
        await library.SelectAsync(0);
        Edit(library.Edits);
        Assert.False(library.ShowsUnsavedScratchEdit);
        await library.SelectAsync(1);
        Assert.Equal(1, library.SelectedIndex);
        main.ShowLibraryPatch(new LibraryPatch("bell", bell, SynthesisProfiles.SuperColliderFm7, ["A bell."]));
        Edit(main.Edits);
        await library.ShowInSearchAsync();
        Assert.Equal(Hash(library.ShownPatch), Hash(main.ShownPatch));

        Assert.Equal(0, _asked);
    }
}
