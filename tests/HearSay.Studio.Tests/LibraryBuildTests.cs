using HearSay.Synthesis.Profiles;

namespace HearSay.Studio.Tests;

/// <summary>The question the Studio asks before it builds a structure library: what it costs, in words.</summary>
public sealed class LibraryBuildTests
{
    private static readonly SynthesisProfile Fm7 = SynthesisProfiles.SuperColliderFm7;
    private static readonly SynthesisProfile Fmx6 = SynthesisProfiles.SuperColliderFmx6;
    private const int Size = 300_000;

    [Fact]
    public void NothingToBuild_AsksNothing()
    {
        Assert.Null(LibraryBuild.Question([Fm7, Fmx6], Size, static _ => 0, processors: 24));
        Assert.Null(LibraryBuild.Question([], Size, static _ => 1000, processors: 24));

        // A library the user agreed to build already is not asked about again, while it is being built.
        var consent = new LibraryBuildConsent();
        consent.Confirm([Fmx6]);
        Assert.Null(LibraryBuild.Question([Fmx6], Size, static _ => 1000, consent, processors: 24));
        Assert.Equal([Fm7], LibraryBuild.Question([Fm7, Fmx6], Size, static _ => 1000, consent, processors: 24)!.Profiles);
    }

    [Fact]
    public void AFullBuild_TellsSoundsTimeAndSpace_ScaledByTheCores()
    {
        LibraryBuildQuestion question = LibraryBuild.Question([Fmx6], Size, static p => 600_000, processors: 24)!;
        Assert.Equal(["supercollider-fmx6"], question.Profiles.Select(static p => p.Name));
        Assert.Equal(600_000, question.Sounds);
        Assert.InRange(question.Minutes, 19, 21);
        Assert.InRange(question.Megabytes, 170, 180);

        // Twice as long on half the cores.
        Assert.InRange(LibraryBuild.Question([Fmx6], Size, static p => 600_000, processors: 12)!.Minutes, 38, 42);

        string text = question.Text;
        Assert.Contains("FMX6", text, StringComparison.Ordinal);
        Assert.Contains("600,000 sounds", text, StringComparison.Ordinal);
        Assert.Contains("about 20 minutes", text, StringComparison.Ordinal);
        Assert.Contains("175 MB", text, StringComparison.Ordinal);
        Assert.Contains("every core", text, StringComparison.Ordinal);
        Assert.Contains("once", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralTargets_AreOneQuestion_AndOnlyThoseWithoutALibraryAreNamed()
    {
        LibraryBuildQuestion question = LibraryBuild.Question([Fm7, Fmx6], Size, static p => p.Name == "supercollider-fm7" ? 0 : 600_000, processors: 24)!;
        Assert.Equal(["supercollider-fmx6"], question.Profiles.Select(static p => p.Name));

        LibraryBuildQuestion both = LibraryBuild.Question([Fm7, Fmx6], Size, static p => p.Name == "supercollider-fm7" ? 300_000 : 600_000, processors: 24)!;
        Assert.Equal(900_000, both.Sounds);
        Assert.Contains("FM7 and FMX6", both.Text, StringComparison.Ordinal);
        Assert.InRange(both.Minutes, 20, 22.5);
        Assert.Equal("less than a minute", LibraryBuild.Question([Fm7], Size, static _ => 300_000, processors: 24)!.Duration);
    }
}
