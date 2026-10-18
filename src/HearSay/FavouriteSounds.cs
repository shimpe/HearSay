using System.Text.Json.Serialization;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay;

/// <summary>A sound kept as a favourite.</summary>
/// <param name="Nickname">Name given to it, which is also the name of its folder.</param>
/// <param name="Directory">Its folder.</param>
/// <param name="Sound">The patch and its profile, as <see cref="ExportedSounds"/> reads them.</param>
/// <param name="Origin">Where it came from: the library seed, the search, or the imported file.</param>
/// <param name="Gain">
/// The gain it plays at when it was made from an edit and keeps the gain of the original; null to normalise it as usual.
/// </param>
public sealed record Favourite(string Nickname, string Directory, ExportedSound Sound, string Origin, float? Gain = null);

/// <summary>
/// Favourite sounds, kept between sessions as folders under one folder, one per favourite and named by its nickname:
/// <c>patch.json</c>, <c>preview.wav</c>, the backend code and <c>search-summary.json</c> as
/// <see cref="LibrarySoundWriter"/> writes them, so that each is also an ordinary exported sound, and
/// <c>favourite.json</c> with its origin.
/// </summary>
public sealed class FavouriteSounds
{
    private const string OriginFile = "favourite.json";

    /// <summary>The highest gain a favourite is played at from its <c>favourite.json</c>.</summary>
    private const float MaxGain = 100f;

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(static i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Uses a folder of favourites; it is created when the first favourite is added.</summary>
    /// <param name="directory">The folder; the user's local application data when null.</param>
    public FavouriteSounds(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearSay", "favourites");
    }

    /// <summary>The folder that holds the favourites.</summary>
    public string DirectoryPath { get; }

    /// <summary>All favourites, by nickname; folders that cannot be read are passed over.</summary>
    public IReadOnlyList<Favourite> Load()
    {
        if (!System.IO.Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var favourites = new List<Favourite>();
        foreach (string folder in System.IO.Directory.EnumerateDirectories(DirectoryPath))
        {
            if (!File.Exists(Path.Combine(folder, "patch.json")))
            {
                continue;
            }

            ExportedSound? sound = ExportedSounds.Scan(folder).Sounds.FirstOrDefault(s => Path.GetDirectoryName(s.PatchFile) == folder);
            if (sound is not null)
            {
                FavouriteFile file = ReadFile(folder);
                favourites.Add(new Favourite(Path.GetFileName(folder), folder, sound, file.Origin ?? string.Empty, SaneGain(file.Gain)));
            }
        }

        return [.. favourites.OrderBy(static f => f.Nickname, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Keeps a sound as a favourite; a nickname that is taken gets a number.</summary>
    /// <param name="nickname">Name to give it.</param>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile it plays on.</param>
    /// <param name="origin">Where it came from, for the header of its code.</param>
    /// <param name="gain">Gain to play it at, kept in <c>favourite.json</c>; null to normalise it as usual.</param>
    public Favourite Add(string nickname, OperatorMatrixPatch patch, SynthesisProfile profile, string origin, float? gain = null)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(origin);
        string name = FreeName(SafeName(nickname), except: null);
        string folder = Path.Combine(DirectoryPath, name);
        LibrarySoundWriter.Write(patch, profile, folder, Header(name, origin));
        File.WriteAllText(Path.Combine(folder, OriginFile), HearSayJson.Serialize(new FavouriteFile(origin, gain)));
        return new Favourite(name, folder, new ExportedSound(name, Path.Combine(folder, "patch.json"), patch, profile), origin, gain);
    }

    /// <summary>
    /// Replaces the sound of a favourite with another patch, such as an edit of it: same nickname, folder and origin, new
    /// patch, code and preview, and the given gain. The new files are written aside first (in <c>.staging</c>, which is no
    /// favourite), so that a failure leaves the favourite as it was.
    /// </summary>
    /// <param name="favourite">Favourite.</param>
    /// <param name="patch">Its new patch.</param>
    /// <param name="profile">Profile it plays on.</param>
    /// <param name="gain">Gain to play it at, kept in <c>favourite.json</c>; null to normalise it as usual.</param>
    public Favourite Update(Favourite favourite, OperatorMatrixPatch patch, SynthesisProfile profile, float? gain = null)
    {
        ArgumentNullException.ThrowIfNull(favourite);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        string folder = favourite.Directory;
        string staging = Path.Combine(DirectoryPath, ".staging");
        string suffix = Guid.NewGuid().ToString("N");
        string written = Path.Combine(staging, "new-" + suffix), replaced = Path.Combine(staging, "old-" + suffix);
        try
        {
            LibrarySoundWriter.Write(patch, profile, written, Header(favourite.Nickname, favourite.Origin));
            File.WriteAllText(Path.Combine(written, OriginFile), HearSayJson.Serialize(new FavouriteFile(favourite.Origin, gain)));
        }
        catch
        {
            if (System.IO.Directory.Exists(written))
            {
                System.IO.Directory.Delete(written, recursive: true);
            }

            throw;
        }

        System.IO.Directory.Move(folder, replaced);
        System.IO.Directory.Move(written, folder);
        System.IO.Directory.Delete(replaced, recursive: true);
        if (!System.IO.Directory.EnumerateFileSystemEntries(staging).Any())
        {
            System.IO.Directory.Delete(staging);
        }

        return favourite with { Sound = new ExportedSound(favourite.Nickname, Path.Combine(folder, "patch.json"), patch, profile), Gain = gain };
    }

    /// <summary>Gives a favourite another nickname: its folder is renamed and its code says the new name.</summary>
    /// <param name="favourite">Favourite.</param>
    /// <param name="nickname">New name; a name that is taken gets a number.</param>
    public Favourite Rename(Favourite favourite, string nickname)
    {
        ArgumentNullException.ThrowIfNull(favourite);
        string name = FreeName(SafeName(nickname), except: favourite.Directory);
        string folder = Path.Combine(DirectoryPath, name);
        if (!string.Equals(folder, favourite.Directory, StringComparison.Ordinal))
        {
            // Through a temporary name, so that a change of case alone also renames the folder on Windows.
            string temporary = folder + ".renaming-" + Guid.NewGuid().ToString("N");
            System.IO.Directory.Move(favourite.Directory, temporary);
            System.IO.Directory.Move(temporary, folder);
        }

        ExportedSound sound = favourite.Sound with { Name = name, PatchFile = Path.Combine(folder, "patch.json") };
        if (SynthesisBackends.ExporterFor(sound.Profile) is { } exporter)
        {
            File.WriteAllText(Path.Combine(folder, "patch" + exporter.FileExtension), exporter.Export(sound.Patch, new PatchExportOptions { HeaderComments = Header(name, favourite.Origin) }));
        }

        return favourite with { Nickname = name, Directory = folder, Sound = sound };
    }

    /// <summary>Deletes a favourite and its folder.</summary>
    /// <param name="favourite">Favourite.</param>
    public void Remove(Favourite favourite)
    {
        ArgumentNullException.ThrowIfNull(favourite);
        if (System.IO.Directory.Exists(favourite.Directory))
        {
            System.IO.Directory.Delete(favourite.Directory, recursive: true);
        }
    }

    /// <summary>The favourite of a patch on a profile, if there is one.</summary>
    /// <param name="favourites">Favourites to look in.</param>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile.</param>
    public static Favourite? Find(IEnumerable<Favourite> favourites, OperatorMatrixPatch patch, SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(favourites);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        string hash = patch.ComputeHash();
        return favourites.FirstOrDefault(f => f.Sound.Profile.Name == profile.Name && f.Sound.Patch.ComputeHash() == hash);
    }

    /// <summary>
    /// A nickname as a folder name: characters a file name cannot hold become <c>_</c>, spaces and dots at the ends go,
    /// a device name such as <c>CON</c> gets a <c>_</c>, and nothing left is <c>favourite</c>.
    /// </summary>
    /// <param name="nickname">Nickname.</param>
    public static string SafeName(string nickname)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = new string([.. (nickname ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            return "favourite";
        }

        return ReservedNames.Contains(name) ? name + "_" : name;
    }

    private static string[] Header(string nickname, string origin) => [$"HearSay favourite: {nickname}.", origin];

    private string FreeName(string name, string? except)
    {
        string candidate = name;
        for (int n = 2; Taken(candidate); n++)
        {
            candidate = $"{name} {n}";
        }

        return candidate;

        bool Taken(string folderName)
        {
            string folder = Path.Combine(DirectoryPath, folderName);
            return System.IO.Directory.Exists(folder) && !string.Equals(folder, except, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A gain read from <c>favourite.json</c>, or null (normalise as usual) for one that is no number, not above 0 or
    /// above <see cref="MaxGain"/>, which no edit of a sound needs and which an edited file could otherwise make deafening.
    /// </summary>
    private static float? SaneGain(float? gain) => gain is float g && float.IsFinite(g) && g > 0f && g <= MaxGain ? g : null;

    private static FavouriteFile ReadFile(string folder)
    {
        try
        {
            string file = Path.Combine(folder, OriginFile);
            return File.Exists(file) ? HearSayJson.Deserialize<FavouriteFile>(File.ReadAllText(file)) : new FavouriteFile(string.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return new FavouriteFile(string.Empty);
        }
    }

    private sealed record FavouriteFile(string Origin, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Gain = null);
}
