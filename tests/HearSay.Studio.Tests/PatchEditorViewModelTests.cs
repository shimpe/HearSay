using HearSay.Editing;
using HearSay.OperatorMatrix.Priors;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>
/// The editing state of a Studio tab and the gain its sound plays at: Match loudness, the Original side, favourites,
/// undo and redo, two tabs on one sound. The gains come from a fake instead of the mixer, completed at once or when the
/// test says, so that no dispatcher and no rendering are needed.
/// </summary>
public sealed class PatchEditorViewModelTests
{
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;

    /// <summary>The gain the original plays at, kept as a favourite keeps it.</summary>
    private const float OriginalGain = 0.5f;

    /// <summary>The gain the fake gives a sound without a fixed gain: a new sound of the edit.</summary>
    private const float FreshGain = 8f;

    private static OperatorMatrixPatch Original(double ratio = 1)
        => OperatorMatrixPatch.Silent(6)
            .WithOperator(0, OperatorPatch.Sine(OperatorFrequency.AtRatio(ratio), OperatorEnvelope.Adsr(0.01, 0.2, 0.5, 0.1)))
            .WithOutputLevel(0, 0.5);

    private static PatchEdit SetRatio(double ratio)
        => (patch, _) => EditOutcome.Done(patch.WithOperator(0, patch.Operators[0] with { Frequency = OperatorFrequency.AtRatio(ratio) }));

    /// <summary>The mixer's gains: a fixed gain as it is, any other sound <see cref="FreshGain"/>, or what <see cref="Next"/> gives.</summary>
    private sealed class FakeGains
    {
        public List<Sound> Asked { get; } = [];

        public Func<Sound, Task<float>>? Next { get; set; }

        public Task<float> Of(Sound sound)
        {
            Asked.Add(sound);
            return sound.FixedGain is float fixedGain ? Task.FromResult(fixedGain) : Next?.Invoke(sound) ?? Task.FromResult(FreshGain);
        }
    }

    private static PatchEditorViewModel Shown(EditSessions sessions, FakeGains gains, OperatorMatrixPatch? original = null)
    {
        var vm = new PatchEditorViewModel(sessions, gains.Of);
        vm.Show(original ?? Original(), Fmx6, StructureLibrary.ReferenceRequest, "test", OriginalGain);
        return vm;
    }

    [Fact]
    public async Task MatchLoudness_PlaysTheEditAtItsOwnGain_TheOriginalAtItsGain()
    {
        var gains = new FakeGains();
        PatchEditorViewModel vm = Shown(new EditSessions(), gains);
        Assert.False(vm.CanMatchLoudness);
        vm.Apply(SetRatio(2));
        OperatorMatrixPatch edit = vm.Editor!.Current;
        Assert.Equal(OriginalGain, vm.DisplayedSound!.FixedGain);
        Assert.Equal(OriginalGain, await vm.FavouriteGainAsync());
        Assert.True(vm.CanMatchLoudness);

        var notices = new List<string?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PatchEditorViewModel.Notice))
            {
                notices.Add(vm.Notice);
            }
        };
        await vm.MatchLoudnessAsync();

        // The gain of a new sound of the edit: the original with the edited patch and no fixed gain.
        Sound asked = gains.Asked[^1];
        Assert.Null(asked.FixedGain);
        Assert.Same(edit, asked.Patch);
        Assert.Equal(FreshGain, vm.MatchedGain);
        Assert.Equal(FreshGain, vm.DisplayedSound!.FixedGain);
        Assert.Same(edit, vm.DisplayedSound.Patch);
        Assert.Equal(FreshGain, await vm.FavouriteGainAsync());
        Assert.Equal([PatchEditorViewModel.MatchedNotice], notices);

        // The Original side plays at the original's gain, and a favourite of it keeps that.
        vm.ShowingOriginal = true;
        Assert.Equal(OriginalGain, vm.DisplayedSound!.FixedGain);
        Assert.Equal(Original().ComputeHash(), vm.DisplayedSound.Patch.ComputeHash());
        Assert.Equal(OriginalGain, await vm.FavouriteGainAsync());
        Assert.False(vm.CanMatchLoudness);
        vm.ShowingOriginal = false;

        // A later edit keeps the matched gain; undoing to the original shows it as before; redo comes back to the match.
        vm.Apply(SetRatio(3));
        Assert.Equal(FreshGain, vm.DisplayedSound!.FixedGain);
        vm.Undo();
        vm.Undo();
        Assert.False(vm.IsEdited);
        Assert.Equal(OriginalGain, vm.DisplayedSound!.FixedGain);
        Assert.False(vm.CanMatchLoudness);
        vm.Redo();
        Assert.Equal(FreshGain, vm.DisplayedSound!.FixedGain);
    }

    [Fact]
    public async Task MatchLoudness_ANewEditFromTheOriginal_PlaysAtTheOriginalsGain()
    {
        PatchEditorViewModel vm = Shown(new EditSessions(), new FakeGains());
        vm.Apply(SetRatio(2));
        await vm.MatchLoudnessAsync();
        Assert.Equal(FreshGain, vm.DisplayedSound!.FixedGain);

        vm.Undo();
        vm.Apply(SetRatio(4));
        Assert.Null(vm.MatchedGain);
        Assert.Equal(OriginalGain, vm.DisplayedSound!.FixedGain);
        Assert.Equal(OriginalGain, await vm.FavouriteGainAsync());
    }

    [Fact]
    public async Task MatchLoudness_RunsOncePerSound_InBothTabs()
    {
        var sessions = new EditSessions();
        var gains = new FakeGains();
        PatchEditorViewModel search = Shown(sessions, gains);
        PatchEditorViewModel library = Shown(sessions, gains);
        search.Apply(SetRatio(2));
        Assert.True(library.ShowsEdit);
        Assert.True(library.CanMatchLoudness);

        var pending = new TaskCompletionSource<float>();
        gains.Next = _ => pending.Task;
        Task match = search.MatchLoudnessAsync();
        Assert.False(search.CanMatchLoudness);
        Assert.False(library.CanMatchLoudness);
        int asked = gains.Asked.Count;
        await library.MatchLoudnessAsync();
        Assert.Equal(asked, gains.Asked.Count);

        pending.SetResult(FreshGain);
        await match;
        Assert.True(search.CanMatchLoudness);
        Assert.True(library.CanMatchLoudness);
        Assert.Equal(FreshGain, library.MatchedGain);
        Assert.Equal(FreshGain, library.DisplayedSound!.FixedGain);
    }

    [Fact]
    public async Task MatchLoudness_ForAnEditThatChangedMeanwhile_IsNotApplied()
    {
        var gains = new FakeGains();
        PatchEditorViewModel vm = Shown(new EditSessions(), gains);
        vm.Apply(SetRatio(2));

        // Edited again while the gain is computed: the gain is not the edit's any more.
        var pending = new TaskCompletionSource<float>();
        gains.Next = _ => pending.Task;
        Task match = vm.MatchLoudnessAsync();
        vm.Apply(SetRatio(3));
        pending.SetResult(FreshGain);
        await match;
        Assert.Null(vm.MatchedGain);
        Assert.Equal(OriginalGain, vm.DisplayedSound!.FixedGain);
        Assert.NotNull(vm.LastRefusal);
        Assert.True(vm.CanMatchLoudness);

        // Reverted while the gain is computed: nothing is applied.
        var again = new TaskCompletionSource<float>();
        gains.Next = _ => again.Task;
        match = vm.MatchLoudnessAsync();
        vm.Editor!.Revert();
        again.SetResult(FreshGain);
        await match;
        Assert.Null(vm.MatchedGain);
        Assert.Null(vm.Editor.MatchedGain);
    }

    [Fact]
    public async Task MatchLoudness_AfterAnotherSoundIsShown_StaysWithItsSound_WithoutANotice()
    {
        var sessions = new EditSessions();
        var gains = new FakeGains();
        PatchEditorViewModel vm = Shown(sessions, gains);
        vm.Apply(SetRatio(2));
        PatchEditor first = vm.Editor!;

        var pending = new TaskCompletionSource<float>();
        gains.Next = _ => pending.Task;
        Task match = vm.MatchLoudnessAsync();
        vm.Show(Original(1.5), Fmx6, StructureLibrary.ReferenceRequest, "other", OriginalGain);
        int notices = 0;
        vm.PropertyChanged += (_, e) => notices += e.PropertyName == nameof(PatchEditorViewModel.Notice) ? 1 : 0;
        pending.SetResult(FreshGain);
        await match;

        Assert.Equal(0, notices);
        Assert.Equal(FreshGain, first.MatchedGain);
        Assert.Null(vm.MatchedGain);
        vm.Show(Original(), Fmx6, StructureLibrary.ReferenceRequest, "test", OriginalGain);
        Assert.Equal(FreshGain, vm.DisplayedSound!.FixedGain);
    }

    [Fact]
    public async Task CodeView_CarriesTheDisplayedSoundsGain()
    {
        static string Output(string code) => code.Split('\n').Single(static l => l.Contains("Out.ar(", StringComparison.Ordinal)).Trim();
        string[] header = ["HearSay library test, seed 1."];
        var gains = new FakeGains();
        PatchEditorViewModel vm = Shown(new EditSessions(), gains);

        // The original at the gain it plays at: here the one a favourite kept.
        string code = vm.DisplayedCode(header);
        Assert.Equal(OriginalGain, vm.DisplayedGain);
        Assert.Equal("Out.ar(out, (signal * amp * 0.5) ! 2);", Output(code));
        Assert.Contains("// Level: x 0.5, the gain HearSay plays this sound at", code, StringComparison.Ordinal);
        Assert.Contains("// HearSay library test, seed 1.", code, StringComparison.Ordinal);
        Assert.DoesNotContain(PatchEditorViewModel.EditedHeaderLine, code, StringComparison.Ordinal);

        // An edit plays at the original's gain, and says it is an edit.
        vm.Apply(SetRatio(2));
        code = vm.DisplayedCode(header);
        Assert.Equal("Out.ar(out, (signal * amp * 0.5) ! 2);", Output(code));
        Assert.Contains("(freq * 2.0)", code, StringComparison.Ordinal);
        Assert.Contains(PatchEditorViewModel.EditedHeaderLine, code, StringComparison.Ordinal);

        // Matched: the edit's own gain; the Original side keeps the original's.
        await vm.MatchLoudnessAsync();
        Assert.Equal(FreshGain, vm.DisplayedGain);
        Assert.Equal("Out.ar(out, (signal * amp * 8.0) ! 2);", Output(vm.DisplayedCode(header)));
        vm.ShowingOriginal = true;
        Assert.Equal("Out.ar(out, (signal * amp * 0.5) ! 2);", Output(vm.DisplayedCode(header)));

        // Without a kept gain: the gain the mixer computes for it, once known; before, the code has no level.
        PatchEditorViewModel fresh = new(new EditSessions(), gains.Of);
        fresh.Show(Original(3), Fmx6, StructureLibrary.ReferenceRequest, "test");
        Assert.Equal("Out.ar(out, (signal * amp * 8.0) ! 2);", Output(fresh.DisplayedCode(header)));
        gains.Next = _ => new TaskCompletionSource<float>().Task;
        fresh.Show(Original(4), Fmx6, StructureLibrary.ReferenceRequest, "test");
        Assert.Null(fresh.DisplayedGain);
        Assert.Equal("Out.ar(out, (signal * amp) ! 2);", Output(fresh.DisplayedCode(header)));
        Assert.DoesNotContain("Level:", fresh.DisplayedCode(header), StringComparison.Ordinal);

        fresh.Show(null, null, null, string.Empty);
        Assert.Equal(string.Empty, fresh.DisplayedCode(header));
    }

    [Fact]
    public async Task Copy_WhenAnotherSoundIsShownMeanwhile_CarriesTheGainOfTheSoundShown()
    {
        // Copy and Save wait for the gain; a sound shown meanwhile is the one they then carry, with its own gain.
        var gains = new FakeGains { Next = _ => new TaskCompletionSource<float>().Task };
        var vm = new PatchEditorViewModel(new EditSessions(), gains.Of);
        vm.Show(Original(3), Fmx6, StructureLibrary.ReferenceRequest, "first");
        Task<float?> copy = vm.DisplayedGainAsync();
        Assert.False(copy.IsCompleted);

        vm.Show(Original(), Fmx6, StructureLibrary.ReferenceRequest, "second", OriginalGain);
        Assert.Equal(OriginalGain, await copy.WaitAsync(TimeSpan.FromSeconds(10)));
        string code = vm.DisplayedCode(["second"]);
        Assert.Contains("Out.ar(out, (signal * amp * 0.5) ! 2);", code, StringComparison.Ordinal);
        Assert.Contains("(freq * 1.0)", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MatchLoudness_ReportsOnlyAFailedComputation()
    {
        // The gain cannot be computed: the status line says so, and the edit keeps its gain.
        var gains = new FakeGains { Next = _ => Task.FromException<float>(new NotSupportedException("no renderer")) };
        PatchEditorViewModel vm = Shown(new EditSessions(), gains);
        vm.Apply(SetRatio(2));
        await vm.MatchLoudnessAsync();
        Assert.Contains("could not be computed", vm.LastRefusal, StringComparison.Ordinal);
        Assert.Null(vm.MatchedGain);
        Assert.True(vm.CanMatchLoudness);

        // A failure after the gain is known (here of whoever follows the sound) is not reported as one of the computation.
        gains.Next = null;
        vm.Apply(SetRatio(3));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PatchEditorViewModel.DisplayedSound) && vm.MatchedGain is not null)
            {
                throw new InvalidOperationException("follower failed");
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(vm.MatchLoudnessAsync);
        Assert.DoesNotContain("could not be computed", vm.LastRefusal ?? string.Empty, StringComparison.Ordinal);
    }
}
