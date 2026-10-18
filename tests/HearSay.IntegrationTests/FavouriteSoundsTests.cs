using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.IntegrationTests;

/// <summary>Favourite sounds kept as folders between sessions; fast, no search.</summary>
public sealed class FavouriteSoundsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-favourites-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static OperatorMatrixPatch Piano => ReferencePatches.Find("electric-piano")!.Patch;

    private static OperatorMatrixPatch Bell => ReferencePatches.Find("bell")!.Patch;

    [Fact]
    public void Add_IsLoadedAgain_WithItsNicknameProfileOriginAndPatch()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite added = favourites.Add("glassy bell", Bell, SynthesisProfiles.SuperColliderFm7, "HearSay library supercollider-fm7 v1, seed 50000007.");

        // Another session: a new instance on the same folder.
        Favourite loaded = Assert.Single(new FavouriteSounds(_directory).Load());
        Assert.Equal("glassy bell", loaded.Nickname);
        Assert.Equal(added.Directory, loaded.Directory);
        Assert.Equal(SynthesisProfiles.SuperColliderFm7.Name, loaded.Sound.Profile.Name);
        Assert.Equal(Bell.ComputeHash(), loaded.Sound.Patch.ComputeHash());
        Assert.Equal("HearSay library supercollider-fm7 v1, seed 50000007.", loaded.Origin);
        string code = File.ReadAllText(Path.Combine(loaded.Directory, "patch.scd"));
        Assert.Contains("HearSay favourite: glassy bell", code, StringComparison.Ordinal);
        Assert.Contains("seed 50000007", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Favourite_KeepsItsGain()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite withGain = favourites.Add("with", Bell, SynthesisProfiles.SuperColliderFm7, "edited", gain: 0.3f);
        favourites.Add("without", Piano, SynthesisProfiles.SuperColliderFm7, "search best");
        Assert.Equal(0.3f, withGain.Gain);

        IReadOnlyList<Favourite> loaded = new FavouriteSounds(_directory).Load();
        Assert.Equal(0.3f, loaded.Single(static f => f.Nickname == "with").Gain);
        Favourite plain = loaded.Single(static f => f.Nickname == "without");
        Assert.Null(plain.Gain);
        Assert.DoesNotContain("gain", File.ReadAllText(Path.Combine(plain.Directory, "favourite.json")), StringComparison.OrdinalIgnoreCase);

        // A favourite.json from before the field existed.
        File.WriteAllText(Path.Combine(plain.Directory, "favourite.json"), "{\"origin\":\"old\"}");
        Favourite old = new FavouriteSounds(_directory).Load().Single(static f => f.Nickname == "without");
        Assert.Null(old.Gain);
        Assert.Equal("old", old.Origin);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1e9")]
    [InlineData("100.5")]
    public void Favourite_WithAGainOutOfReason_IsNormalised(string gain)
    {
        Favourite added = new FavouriteSounds(_directory).Add("odd", Bell, SynthesisProfiles.SuperColliderFm7, "edited", gain: 0.3f);
        File.WriteAllText(Path.Combine(added.Directory, "favourite.json"), "{\"origin\":\"edited\",\"gain\":" + gain + "}");

        Favourite loaded = Assert.Single(new FavouriteSounds(_directory).Load());
        Assert.Null(loaded.Gain);
        Assert.Equal("edited", loaded.Origin);
    }

    [Fact]
    public void Favourite_WithAGainOfUpTo100_KeepsIt()
    {
        Favourite added = new FavouriteSounds(_directory).Add("loud", Bell, SynthesisProfiles.SuperColliderFm7, "edited", gain: 0.3f);
        File.WriteAllText(Path.Combine(added.Directory, "favourite.json"), "{\"origin\":\"edited\",\"gain\":100}");
        Assert.Equal(100f, Assert.Single(new FavouriteSounds(_directory).Load()).Gain);
    }

    [Fact]
    public void EditedFavourite_IsANewFavourite()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite original = favourites.Add("bell", Bell, SynthesisProfiles.SuperColliderFm7, "search best");
        var before = Directory.GetFiles(original.Directory).ToDictionary(static f => Path.GetFileName(f), static f => File.ReadAllBytes(f));

        Favourite edited = favourites.Add("bell", Piano, SynthesisProfiles.SuperColliderFm7, "edited from search best", gain: 0.4f);

        Assert.Equal(2, favourites.Load().Count);
        Assert.NotEqual(original.Directory, edited.Directory);
        Assert.Equal("edited from search best", edited.Origin);
        var after = Directory.GetFiles(original.Directory).ToDictionary(static f => Path.GetFileName(f), static f => File.ReadAllBytes(f));
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string name, byte[] bytes) in before)
        {
            Assert.Equal(bytes, after[name]);
        }
    }

    [Fact]
    public void Update_ReplacesThePatch_KeepsTheNameAndOrigin_AndStoresTheGain()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite original = favourites.Add("bell", Bell, SynthesisProfiles.SuperColliderFm7, "search best");
        byte[] oldPreview = File.ReadAllBytes(Path.Combine(original.Directory, "preview.wav"));

        Favourite updated = favourites.Update(original, Piano, SynthesisProfiles.SuperColliderFm7, gain: 0.4f);

        Favourite loaded = Assert.Single(new FavouriteSounds(_directory).Load());
        Assert.Equal("bell", loaded.Nickname);
        Assert.Equal(original.Directory, loaded.Directory);
        Assert.Equal(updated.Directory, loaded.Directory);
        Assert.Equal("search best", loaded.Origin);
        Assert.Equal(0.4f, loaded.Gain);
        Assert.Equal(Piano.ComputeHash(), loaded.Sound.Patch.ComputeHash());
        Assert.Equal(Piano.ComputeHash(), updated.Sound.Patch.ComputeHash());
        Assert.NotEqual(oldPreview, File.ReadAllBytes(Path.Combine(loaded.Directory, "preview.wav")));
        Assert.Contains("HearSay favourite: bell.", File.ReadAllText(Path.Combine(loaded.Directory, "patch.scd")));
        Assert.Equal([loaded.Directory], Directory.GetDirectories(_directory));
    }

    [Fact]
    public void Update_WithoutAGain_ForgetsTheOldOne()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite original = favourites.Add("bell", Bell, SynthesisProfiles.SuperColliderFm7, "edited from search best", gain: 0.3f);

        favourites.Update(original, Piano, SynthesisProfiles.SuperColliderFm7, gain: null);

        Assert.Null(Assert.Single(new FavouriteSounds(_directory).Load()).Gain);
    }

    [Fact]
    public void Load_IsInNicknameOrder_AndAnEmptyOrMissingFolderHasNone()
    {
        var favourites = new FavouriteSounds(_directory);
        Assert.Empty(favourites.Load());
        favourites.Add("warm pad", Piano, SynthesisProfiles.SuperColliderFm7, "search best");
        favourites.Add("Bright bell", Bell, SynthesisProfiles.SuperColliderFm7, "search #2");
        Assert.Equal(new[] { "Bright bell", "warm pad" }, favourites.Load().Select(static f => f.Nickname));
    }

    [Fact]
    public void SameNickname_GetsANumber()
    {
        var favourites = new FavouriteSounds(_directory);
        favourites.Add("pad", Piano, SynthesisProfiles.SuperColliderFm7, "a");
        Favourite second = favourites.Add("pad", Bell, SynthesisProfiles.SuperColliderFm7, "b");
        Assert.Equal("pad 2", second.Nickname);
        Assert.Equal(2, favourites.Load().Count);
    }

    [Theory]
    [InlineData("glassy bell", "glassy bell")]
    [InlineData("a/b:c?", "a_b_c_")]
    [InlineData("  trailing dots...  ", "trailing dots")]
    [InlineData("   ", "favourite")]
    [InlineData("CON", "CON_")]
    [InlineData("com1", "com1_")]
    public void SafeName_IsAUsableFolderName(string nickname, string expected) =>
        Assert.Equal(expected, FavouriteSounds.SafeName(nickname));

    [Fact]
    public void Rename_MovesTheFolder_AndTheCodeSaysTheNewName()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite added = favourites.Add("pad", Piano, SynthesisProfiles.SuperColliderFm7, "search best");

        Favourite renamed = favourites.Rename(added, "warm pad");

        Assert.Equal("warm pad", renamed.Nickname);
        Assert.False(Directory.Exists(added.Directory));
        Assert.Equal("warm pad", Assert.Single(favourites.Load()).Nickname);
        string code = File.ReadAllText(Path.Combine(renamed.Directory, "patch.scd"));
        Assert.Contains("HearSay favourite: warm pad", code, StringComparison.Ordinal);
        Assert.Contains("search best", code, StringComparison.Ordinal);

        // Only the case changes: still renamed.
        Assert.Equal("Warm Pad", favourites.Rename(renamed, "Warm Pad").Nickname);
        Assert.Equal("Warm Pad", Assert.Single(favourites.Load()).Nickname);
    }

    [Fact]
    public void Remove_DeletesTheFolder()
    {
        var favourites = new FavouriteSounds(_directory);
        Favourite added = favourites.Add("pad", Piano, SynthesisProfiles.SuperColliderFm7, "search best");
        favourites.Remove(added);
        Assert.False(Directory.Exists(added.Directory));
        Assert.Empty(favourites.Load());
    }

    [Fact]
    public void Find_MatchesTheProfileAndThePatch()
    {
        var favourites = new FavouriteSounds(_directory);
        favourites.Add("pad", Piano, SynthesisProfiles.SuperColliderFm7, "search best");
        IReadOnlyList<Favourite> all = favourites.Load();
        Assert.Equal("pad", FavouriteSounds.Find(all, Piano, SynthesisProfiles.SuperColliderFm7)?.Nickname);
        Assert.Null(FavouriteSounds.Find(all, Piano, SynthesisProfiles.SuperColliderFmx6));
        Assert.Null(FavouriteSounds.Find(all, Bell, SynthesisProfiles.SuperColliderFm7));
    }
}
