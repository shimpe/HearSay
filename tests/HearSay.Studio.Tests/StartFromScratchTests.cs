using System.ComponentModel;
using HearSay.Editing;
using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>
/// Start from scratch in the empty block diagram of either tab: a new sound, one sine, on the tab's profile, edited like
/// any other. The view models get a real live player on a settings file and a favourites folder of the test's own, and a
/// fake gain, completed at once, instead of the mixer's.
/// </summary>
public sealed class StartFromScratchTests : IDisposable
{
    private const float Gain = 0.5f;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-studio-scratch-" + Guid.NewGuid().ToString("N"));

    public StartFromScratchTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Task<float> FakeGain(Sound sound) => Task.FromResult(sound.FixedGain ?? Gain);

    private MainViewModel Main() =>
        new(new FavouriteSounds(Path.Combine(_directory, "favourites")), Path.Combine(_directory, "studio-settings.json"), FakeGain);

    private static string Hash(OperatorMatrixPatch? patch) => Assert.IsType<OperatorMatrixPatch>(patch).ComputeHash();

    [Fact]
    public async Task StartFromScratch_Search_ShowsANewSoundOnTheSelectedProfile()
    {
        using MainViewModel main = Main();
        main.SelectedSynthesisTarget = main.SynthesisTargets.Single(static t => t.Label == "Nome6");

        main.StartFromScratchCommand.Execute(null);

        Assert.Equal(Hash(ScratchSound.Patch), Hash(main.ShownPatch));
        Assert.Same(SynthesisProfiles.SuperColliderNome6, main.Edits.Editor!.Profile);
        Assert.Equal("New sound made from scratch; edit it in the block diagram. The next search replaces it.", main.Status);
        Assert.Equal(0, main.SelectedTab);
        Assert.True(main.HasPatch);
        Assert.False(main.HasResult);
        Assert.True(main.ShownPatchSharesNoise);

        // Levelled on the library's reference note, as library sounds shown here are.
        Sound sound = Assert.IsType<Sound>(main.Edits.DisplayedSound);
        Assert.Equal(StructureLibrary.ReferenceRequest, sound.GainReference);
        Assert.Equal(Gain, main.Edits.DisplayedGain);
        Assert.Contains("// Made from scratch in HearSay Studio.", main.Scd, StringComparison.Ordinal);
        Assert.Equal("made from scratch", main.Edits.FavouriteOrigin);

        // Edited like any other sound; its favourite still says it was made from scratch.
        Assert.False(main.Edits.Apply(PatchEdits.AddModulator(0)).IsRefused);
        Assert.True(main.Edits.ShowsEdit);
        Assert.Equal("made from scratch", main.Edits.FavouriteOrigin);
        main.PrepareFavouriteName();
        Assert.Equal("New sound edited", main.FavouriteNickname);
        await main.SaveFavouriteAsync();
        Favourite favourite = Assert.Single(main.Library.Favourites).Favourite;
        Assert.Equal("made from scratch", favourite.Origin);
        Assert.Equal(Hash(main.Edits.Editor.Current), favourite.Sound.Patch.ComputeHash());
    }

    [Fact]
    public void StartFromScratch_Library_SelectsANewSound()
    {
        using MainViewModel main = Main();
        LibraryViewModel library = main.Library;
        library.SelectedProfile = SynthesisProfiles.SuperColliderFm7;

        library.StartFromScratchCommand.Execute(null);

        Assert.True(library.HasSelection);
        Assert.Equal(-1, library.SelectedIndex);
        Assert.Equal(Hash(ScratchSound.Patch), Hash(library.ShownPatch));
        Assert.Same(SynthesisProfiles.SuperColliderFm7, library.Edits.Editor!.Profile);
        Assert.Equal("made from scratch", library.SelectionText);
        Assert.Equal("New sound made from scratch; edit it in the block diagram.", main.Status);
        Assert.Contains("// Made from scratch in HearSay Studio.", library.Scd, StringComparison.Ordinal);
        Assert.Equal(StructureLibrary.ReferenceRequest, library.CurrentSound!.GainReference);
        Assert.Equal("made from scratch", library.Edits.FavouriteOrigin);
        library.PrepareFavouriteName();
        Assert.Equal("New sound", library.FavouriteNickname);

        // An edit: the selection line and the favourite's origin say so, cleanly.
        Assert.False(library.Edits.Apply(PatchEdits.AddModulator(0)).IsRefused);
        Assert.Equal("made from scratch", library.Edits.FavouriteOrigin);

        // The same sound on the same profile in the Search tab has the same edits.
        main.SelectedSynthesisTarget = main.SynthesisTargets.Single(static t => t.Label == "FM7");
        main.StartFromScratch();
        Assert.Same(library.Edits.Editor, main.Edits.Editor);
        Assert.True(main.Edits.ShowsEdit);
    }

    [Fact]
    public void StartFromScratch_IsOfferedOnlyWhenNothingIsShown()
    {
        using MainViewModel main = Main();
        LibraryViewModel library = main.Library;
        var raised = new List<string>();
        main.PropertyChanged += (_, e) => Record(raised, "search", e);
        library.PropertyChanged += (_, e) => Record(raised, "library", e);
        int searchCommand = 0;
        int libraryCommand = 0;
        main.StartFromScratchCommand.CanExecuteChanged += (_, _) => searchCommand++;
        library.StartFromScratchCommand.CanExecuteChanged += (_, _) => libraryCommand++;
        Assert.True(main.CanStartFromScratch);
        Assert.True(main.StartFromScratchCommand.CanExecute(null));
        Assert.True(library.CanStartFromScratch);
        Assert.True(library.StartFromScratchCommand.CanExecute(null));

        // Shown in the Search tab: no longer offered there, still in the empty Library tab.
        main.StartFromScratch();
        Assert.False(main.CanStartFromScratch);
        Assert.False(main.StartFromScratchCommand.CanExecute(null));
        Assert.Contains("search", raised);
        Assert.True(searchCommand > 0);
        Assert.True(library.CanStartFromScratch);

        // Edited, then another sound is shown: starting from scratch now changes nothing.
        Assert.False(main.Edits.Apply(PatchEdits.AddModulator(0)).IsRefused);
        OperatorMatrixPatch bell = ReferencePatches.Find("bell")!.Patch;
        main.ShowLibraryPatch(new LibraryPatch("bell", bell, SynthesisProfiles.SuperColliderFm7, ["A bell."]));
        main.StartFromScratch();
        Assert.Equal(Hash(bell), Hash(main.ShownPatch));

        // Another target with no result of its own empties the tab: offered again, and the edits made before come back.
        main.SelectedSynthesisTarget = main.SynthesisTargets.Single(static t => t.Label == "FMX6");
        main.SelectedSynthesisTarget = main.SynthesisTargets.Single(static t => t.Label == "FM7");
        Assert.Null(main.ShownPatch);
        Assert.True(main.CanStartFromScratch);
        main.StartFromScratch();
        Assert.True(main.Edits.ShowsEdit);

        // Selected in the Library tab: no longer offered there.
        library.StartFromScratch();
        Assert.False(library.CanStartFromScratch);
        Assert.False(library.StartFromScratchCommand.CanExecute(null));
        Assert.Contains("library", raised);
        Assert.True(libraryCommand > 0);
    }

    private static void Record(List<string> raised, string tab, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "CanStartFromScratch")
        {
            raised.Add(tab);
        }
    }
}
