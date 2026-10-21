using System.Globalization;
using HearSay.OperatorMatrix.Priors;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio;

/// <summary>What the user answers when a structure library would be built.</summary>
public enum LibraryBuildAnswer
{
    /// <summary>Build it now (and search, when a search asked).</summary>
    Build,

    /// <summary>Search without the library this time and for the rest of the session.</summary>
    WithoutLibrary,

    /// <summary>Do nothing now.</summary>
    Cancel,
}

/// <summary>A structure library about to be built for the first time, with what it costs.</summary>
/// <param name="Profiles">The synthesis targets whose library would be built.</param>
/// <param name="Sounds">Sounds to render in all.</param>
/// <param name="Minutes">Estimated time on this machine, all cores.</param>
/// <param name="Megabytes">Space the libraries take on disk.</param>
public sealed record LibraryBuildQuestion(IReadOnlyList<SynthesisProfile> Profiles, int Sounds, double Minutes, double Megabytes)
{
    /// <summary>The names of the targets, as the Target selector shows them: "FMX6", "FM7 and FMX6".</summary>
    public string Names
    {
        get
        {
            string[] names = [.. Profiles.Select(static p => ProfileNameConverter.Short(p.Name))];
            return names.Length == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
        }
    }

    /// <summary>The estimated time in words: "less than a minute", "about 20 minutes".</summary>
    public string Duration => Minutes < 1.5 ? "less than a minute" : string.Create(CultureInfo.InvariantCulture, $"about {Math.Round(Minutes):0} minutes");

    /// <summary>What the dialog says.</summary>
    public string Text => string.Create(
        CultureInfo.InvariantCulture,
        $"The {Names} {(Profiles.Count == 1 ? "library is" : "libraries are")} built once, the first time {(Profiles.Count == 1 ? "it is" : "they are")} used: {Sounds:N0} sounds to render, {Duration} on this computer, using every core at full speed (fans may spin up), and {Megabytes:0} MB on disk. The progress bar shows the time left. After that the library is reused. It makes searches better and is what the Library tab shows.");
}

/// <summary>
/// The libraries the user agreed to build while the Studio runs, shared by both tabs, so that neither asks again while
/// one is being built.
/// </summary>
public sealed class LibraryBuildConsent
{
    private readonly HashSet<string> _confirmed = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Notes that the user agreed to build these libraries.</summary>
    /// <param name="profiles">Profiles.</param>
    public void Confirm(IEnumerable<SynthesisProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        lock (_gate)
        {
            foreach (SynthesisProfile profile in profiles)
            {
                _confirmed.Add(profile.Name);
            }
        }
    }

    /// <summary>True when the user agreed to build the library of a profile.</summary>
    /// <param name="profile">Profile.</param>
    public bool IsConfirmed(SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            return _confirmed.Contains(profile.Name);
        }
    }
}

/// <summary>Estimates what building structure libraries costs, for the question the Studio asks first.</summary>
public static class LibraryBuild
{
    /// <summary>
    /// Minutes a whole library of a profile took to build on a 24-thread desktop (Intel Core i7-13700KF), the time
    /// scaling with the sounds to render and inversely with the cores.
    /// </summary>
    private static readonly Dictionary<string, double> MeasuredMinutes = new(StringComparer.Ordinal)
    {
        ["supercollider-fm7"] = 1.0,
        ["supercollider-fmx6"] = 20.0,
        ["supercollider-nome6"] = 11.0,
    };

    private const double MeasuredProcessors = 24.0;

    /// <summary>Bytes a sound takes in a library file: its fingerprint and its seed.</summary>
    private const double BytesPerSound = 292.0;

    /// <summary>
    /// The question for the libraries a set of profiles still needs; null when every library is there (or the profiles
    /// were confirmed already, <paramref name="consent"/>), so that nothing needs asking.
    /// </summary>
    /// <param name="profiles">Profiles about to be used.</param>
    /// <param name="size">Number of regular patches of a library.</param>
    /// <param name="soundsToRender">Sounds a profile's library still needs (<see cref="StructureLibrary.SoundsToRender"/>).</param>
    /// <param name="consent">The libraries the user agreed to build already; null for none.</param>
    /// <param name="processors">Cores of this machine.</param>
    public static LibraryBuildQuestion? Question(IEnumerable<SynthesisProfile> profiles, int size, Func<SynthesisProfile, int> soundsToRender, LibraryBuildConsent? consent = null, int? processors = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(soundsToRender);
        double cores = Math.Max(1, processors ?? Environment.ProcessorCount);
        var needed = new List<SynthesisProfile>();
        int sounds = 0;
        double minutes = 0, megabytes = 0;
        foreach (SynthesisProfile profile in profiles)
        {
            int toRender = soundsToRender(profile);
            if (toRender <= 0 || consent?.IsConfirmed(profile) == true)
            {
                continue;
            }

            int whole = StructureLibrary.CountFor(profile.Capabilities, size);
            needed.Add(profile);
            sounds += toRender;
            minutes += MeasuredMinutes.GetValueOrDefault(profile.Name, 20.0) * toRender / Math.Max(1, whole) * MeasuredProcessors / cores;
            megabytes += whole * BytesPerSound / 1_000_000.0;
        }

        return needed.Count == 0 ? null : new LibraryBuildQuestion(needed, sounds, minutes, megabytes);
    }
}
