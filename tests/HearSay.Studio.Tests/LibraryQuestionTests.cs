using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>
/// Both tabs ask before a structure library is built for the first time: the Search tab before a search that would build
/// one, the Library tab before it shows a library that is not built yet. The view models get a fake of what is on disk
/// (every library missing), a fake question that answers as the test says, and in the Library tab a small library built
/// in memory.
/// </summary>
[Collection("InterfaceThread")]
public sealed class LibraryQuestionTests : IDisposable
{
    private static readonly Lazy<LibraryAtlas> SmallLibrary = new(static () =>
    {
        SynthesisProfile fm7 = SynthesisProfiles.SuperColliderFm7;
        LibraryAtlas atlas = LibraryAtlas.Build(StructureLibrary.Build(fm7.Capabilities, 20, SynthesisBackends.RendererFor(fm7)));
        atlas.Children(atlas.Root);
        return atlas;
    });

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hearsay-studio-libq-" + Guid.NewGuid().ToString("N"));
    private readonly List<LibraryBuildQuestion> _asked = [];
    private LibraryBuildAnswer _answer = LibraryBuildAnswer.Cancel;

    public LibraryQuestionTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private MainViewModel Main()
    {
        var main = new MainViewModel(new FavouriteSounds(Path.Combine(_directory, "favourites")), Path.Combine(_directory, "studio-settings.json"), static s => Task.FromResult(s.FixedGain ?? 0.5f), static _ => SmallLibrary.Value, openAudioDevice: false)
        {
            LibrarySoundsToRender = static _ => 300_000,
            AskToBuildLibrary = Ask,
        };
        main.Library.LibrarySoundsToRender = static _ => 300_000;
        main.Library.AskToBuildLibrary = Ask;
        main.Library.SelectedProfile = SynthesisProfiles.SuperColliderFm7;
        return main;
    }

    private Task<LibraryBuildAnswer> Ask(LibraryBuildQuestion question)
    {
        _asked.Add(question);
        return Task.FromResult(_answer);
    }

    /// <summary>A plucked note at 220 Hz.</summary>
    private string WriteTarget(string name)
    {
        const int rate = 44100;
        var samples = new float[rate];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / rate;
            samples[i] = (float)(0.5 * Math.Exp(-3.0 * t) * Math.Sin(2 * Math.PI * 220 * t));
        }

        string path = Path.Combine(_directory, name);
        HearSay.Audio.WavFile.Write(path, samples, rate);
        return path;
    }

    [Fact]
    public async Task Search_AsksFirst_CancelStartsNothing_WithoutLibrarySearchesAndIsRemembered()
    {
        using MainViewModel main = Main();
        Assert.True(await main.AddAsync(WriteTarget("pluck.wav")));
        main.Budget = 200;

        // Cancel: no search.
        await main.RunAsync();
        LibraryBuildQuestion question = Assert.Single(_asked);
        Assert.Equal(["supercollider-fm7"], question.Profiles.Select(static p => p.Name));
        Assert.False(main.IsRunning);
        Assert.False(main.HasResult);

        // Without the library: the search runs, and the status line says it went without.
        _answer = LibraryBuildAnswer.WithoutLibrary;
        await main.RunAsync();
        Assert.Equal(2, _asked.Count);
        Assert.True(main.HasResult);
        Assert.Contains("without the FM7 library", main.Status, StringComparison.Ordinal);

        // Remembered for the session: another search of the same target does not ask again.
        main.Seed++;
        await main.RunAsync();
        Assert.Equal(2, _asked.Count);
        Assert.True(main.HasResult);

        // With the Library check box off, nothing is asked either.
        main.UseStructureLibrary = false;
        main.Seed++;
        await main.RunAsync();
        Assert.Equal(2, _asked.Count);
    }

    [Fact]
    public async Task LibraryTab_AsksFirst_NotNowLeavesABuildButton_BuildShowsTheLibrary()
    {
        using MainViewModel main = Main();
        LibraryViewModel library = main.Library;

        // Not now: no graph, a message, and the button to build it later.
        await library.EnsureLoadedAsync();
        Assert.Single(_asked);
        Assert.Null(library.Nodes);
        Assert.True(library.CanBuildLibrary);
        Assert.StartsWith("Not built yet.", library.GraphMessage, StringComparison.Ordinal);

        // The button builds it without asking again.
        _answer = LibraryBuildAnswer.Cancel;
        await library.BuildLibraryAsync();
        Assert.Single(_asked);
        Assert.False(library.CanBuildLibrary);
        Assert.NotNull(library.Nodes);

        // And the Search tab does not ask about FM7 any more while the Studio runs: the user agreed.
        Assert.Null(LibraryBuild.Question([SynthesisProfiles.SuperColliderFm7], 300_000, static _ => 300_000, main.LibraryConsent));
    }
}
