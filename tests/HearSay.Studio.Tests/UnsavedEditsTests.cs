using HearSay.Editing;
using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>
/// The edits the Studio would lose when it closes, which it asks about first: edited sounds in either tab whose edit is
/// neither a favourite nor written by Save…. The view model gets a favourites folder and a settings file of the test's
/// own, a fake gain and no audio device.
/// </summary>
[Collection("InterfaceThread")]
public sealed class UnsavedEditsTests : IDisposable
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-studio-unsaved-" + Guid.NewGuid().ToString("N"));

    public UnsavedEditsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private MainViewModel Main() =>
        new(new FavouriteSounds(Path.Combine(_directory, "favourites")), Path.Combine(_directory, "studio-settings.json"), static s => Task.FromResult(s.FixedGain ?? 0.5f), static _ => throw new InvalidOperationException("no library in this test"), openAudioDevice: false);

    private static OperatorMatrixPatch Sound(double ratio)
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOutputLevel(0, 0.5);

    private static void Edit(PatchEditorViewModel edits) => Assert.False(edits.Apply(PatchEdits.AddModulator(0)).IsRefused);

    [Fact]
    public async Task AnEdit_IsUnsaved_UntilSavedToAFolder_OrUndone()
    {
        using MainViewModel main = Main();
        Assert.Equal(0, main.UnsavedEditCount);
        main.ShowLibraryPatch(new LibraryPatch("one", Sound(1), Fm7, []));
        Assert.Equal(0, main.UnsavedEditCount);

        Edit(main.Edits);
        Assert.Equal(1, main.UnsavedEditCount);
        main.Edits.Undo();
        Assert.Equal(0, main.UnsavedEditCount);
        main.Edits.Redo();
        Assert.Equal(1, main.UnsavedEditCount);

        // Save… writes the edit: kept. A further edit is not.
        await main.SaveAsync(Path.Combine(_directory, "saved"));
        Assert.Equal(0, main.UnsavedEditCount);
        Edit(main.Edits);
        Assert.Equal(1, main.UnsavedEditCount);

        // While the original is shown, Save… writes the original: the edit is still unsaved.
        main.Edits.ShowingOriginal = true;
        await main.SaveAsync(Path.Combine(_directory, "original"));
        Assert.Equal(1, main.UnsavedEditCount);
    }

    [Fact]
    public async Task AFavourite_KeepsTheEdit()
    {
        using MainViewModel main = Main();
        main.ShowLibraryPatch(new LibraryPatch("one", Sound(1), Fm7, []));
        Edit(main.Edits);
        Assert.Equal(1, main.UnsavedEditCount);

        await main.SaveFavouriteAsync();
        Assert.Equal(0, main.UnsavedEditCount);
    }

    [Fact]
    public void EditsOfEverySound_InBothTabs_Count_EachSoundOnce()
    {
        using MainViewModel main = Main();

        // A sound edited, then another shown: the first edit is kept for the session, and still unsaved.
        main.ShowLibraryPatch(new LibraryPatch("one", Sound(1), Fm7, []));
        Edit(main.Edits);
        main.ShowLibraryPatch(new LibraryPatch("two", Sound(2), Fm7, []));
        Assert.Equal(1, main.UnsavedEditCount);
        Edit(main.Edits);
        Assert.Equal(2, main.UnsavedEditCount);

        // The Library tab on the second sound shares its edit; on a third sound it adds one.
        main.Library.Edits.Show(Sound(2), Fm7, StructureLibrary.ReferenceRequest, "two", 0.5f);
        Assert.Equal(2, main.UnsavedEditCount);
        main.Library.Edits.Show(Sound(3), Fm7, StructureLibrary.ReferenceRequest, "three", 0.5f);
        Edit(main.Library.Edits);
        Assert.Equal(3, main.UnsavedEditCount);
    }
}
