using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>
/// Renaming favourites from the Library tab: a favourite that no longer renders, and a selection made while the rename
/// runs. The view model gets a real live player, on a settings file and a favourites folder of the test's own.
/// </summary>
public sealed class LibraryViewModelFavouritesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-studio-favourites-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _statuses = [];
    private readonly LivePlayer _live;

    public LibraryViewModelFavouritesTests()
    {
        Directory.CreateDirectory(_directory);
        _live = new LivePlayer(Path.Combine(_directory, "studio-settings.json"));
    }

    public void Dispose()
    {
        _live.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string FavouritesFolder => Path.Combine(_directory, "favourites");

    private LibraryViewModel Library() => new(_live, _statuses.Add, static _ => { }, static _ => Task.CompletedTask, new FavouriteSounds(FavouritesFolder));

    private static OperatorMatrixPatch Piano => ReferencePatches.Find("electric-piano")!.Patch;

    [Fact]
    public async Task Rename_OfAFavouriteThatNoLongerRenders_IsReported_AndLeavesItAsItWas()
    {
        Favourite added = new FavouriteSounds(FavouritesFolder).Add("pad", Piano, SynthesisProfiles.SuperColliderFm7, "search best");
        string code = File.ReadAllText(Path.Combine(added.Directory, "patch.scd"));
        LibraryViewModel library = Library();

        // Its sound is now a sawtooth on FM7, which cannot play it.
        OperatorMatrixPatch saw = OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(1), OperatorEnvelope.Constant()) with { Waveform = OperatorWaveform.Saw })
            .WithOutputLevel(0, 1.0);
        Favourite loaded = Assert.Single(library.Favourites).Favourite;
        var item = new FavouriteItem(loaded with { Sound = loaded.Sound with { Patch = saw } });

        await library.RenameFavouriteAsync(item, "warm pad");

        Assert.Contains(_statuses, static s => s.StartsWith("The favourite could not be renamed: ", StringComparison.Ordinal));
        Assert.Equal([added.Directory], Directory.GetDirectories(FavouritesFolder));
        Assert.Equal(code, File.ReadAllText(Path.Combine(added.Directory, "patch.scd")));
        Assert.Equal("pad", Assert.Single(library.Favourites).Name);
    }

    [Fact]
    public async Task Rename_KeepsAFavouriteSelectedMeanwhile_ElseSelectsTheRenamedOne()
    {
        var store = new FavouriteSounds(FavouritesFolder);
        store.Add("a", Piano, SynthesisProfiles.SuperColliderFm7, "one");
        store.Add("b", ReferencePatches.Find("bell")!.Patch, SynthesisProfiles.SuperColliderFm7, "two");
        LibraryViewModel library = Library();
        FavouriteItem a = library.Favourites.Single(static f => f.Name == "a");
        library.SelectedFavourite = a;

        // Renamed alone: the renamed favourite stays selected.
        await library.RenameFavouriteAsync(a, "a2");
        Assert.Equal("a2", library.SelectedFavourite?.Name);

        // Another favourite selected while the rename runs (it renders the reference note): that one stays selected. The
        // rename's continuation is queued until the selection has changed, as on the interface thread.
        FavouriteItem renaming = library.SelectedFavourite!;
        QueueingSynchronizationContext.Run(
            () => library.RenameFavouriteAsync(renaming, "a3"),
            () => library.SelectedFavourite = library.Favourites.Single(static f => f.Name == "b"));
        Assert.Equal("b", library.SelectedFavourite?.Name);
        Assert.Contains(library.Favourites, static f => f.Name == "a3");
    }
}
