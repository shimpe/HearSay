using System.Collections.Concurrent;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.OperatorMatrix.Priors;

/// <summary>
/// Many random patches of a profile, each with the <see cref="SpectralFingerprint"/> of its sound, to look up the patches
/// that sound most like a target. Only the seeds and the fingerprints are kept: a patch is regenerated from its seed,
/// deterministically, when it is needed. The patches are musical ones: a carrier on the note, every heard operator at or
/// above it, no feedback loops through several operators, no fixed operators, moderate modulation, in three densities
/// (two or three, three or four, and four to six operators).
/// </summary>
/// <remarks>
/// <para>
/// A profile with noise (a noise waveform or the shared noise) has as many noise-free sounds again after its regular
/// ones, drawn from seeds of their own: about three quarters of the regular FMX6 and Nome6 sounds are noisy, and a tonal
/// target finds closer neighbours among sounds without noise.
/// </para>
/// <para>
/// Building renders every patch once, minutes for 300 000 patches on 24 threads (FMX6: about 8 for the regular ones and
/// 12 for the noise-free ones, whose band-limited waveforms cost more than noise), so a library is kept on disk and
/// loaded in a second afterwards. Instances are immutable and thread-safe.
/// </para>
/// </remarks>
public sealed class StructureLibrary
{
    private const int FirstSeed = 50_000_000;
    private const int RetryStride = 1_000_003;
    private static readonly ConcurrentDictionary<string, Lazy<StructureLibrary>> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, ProgressRelay> Builds = new(StringComparer.Ordinal);

    /// <summary>First seed of the noise-free sounds; regular sounds have smaller seeds.</summary>
    internal const int NoiseFreeFirstSeed = 1_050_000_000;

    private readonly SynthesisCapabilities _capabilities;
    private readonly int[] _seeds;
    private readonly byte[] _prints;

    private StructureLibrary(SynthesisCapabilities capabilities, int[] seeds, byte[] prints)
    {
        _capabilities = capabilities;
        _seeds = seeds;
        _prints = prints;
    }

    /// <summary>Number of patches.</summary>
    public int Count => _seeds.Length;

    /// <summary>Note at which the library patches are rendered and fingerprinted.</summary>
    public static double ReferenceHz => 220.0;

    /// <summary>The request every library patch is rendered and fingerprinted with: the reference note, held 1 s, released 0.3 s.</summary>
    public static RenderRequest ReferenceRequest => RenderRequest.Create(ReferenceHz, 1.0, 0.3);

    /// <summary>
    /// The library of a profile at a size, from memory, from disk or built and saved; one per profile and size per
    /// process.
    /// </summary>
    /// <param name="profile">Synthesis profile; its patches are generated within its capabilities.</param>
    /// <param name="size">Number of regular patches; see <see cref="CountFor"/>.</param>
    /// <param name="renderer">Renderer of the profile, used when the library has to be built.</param>
    /// <param name="directory">Folder of the library files; the user's local application data when null.</param>
    /// <param name="progress">
    /// Fraction of the patches to render that is rendered, while the library is built; not called when it is loaded. A
    /// caller that waits for a build another caller started gets the progress of that build too.
    /// </param>
    public static StructureLibrary For(SynthesisProfile profile, int size, IOperatorMatrixRenderer renderer, string? directory = null, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        string path = PathOf(profile, size, directory);
        ProgressRelay relay = Builds.GetOrAdd(path, static _ => new ProgressRelay());
        relay.Add(progress);
        try
        {
            return Cache.GetOrAdd(path, _ => new Lazy<StructureLibrary>(() => LoadOrBuild(profile, size, renderer, directory, path, relay))).Value;
        }
        finally
        {
            relay.Remove(progress);
        }
    }

    /// <summary>Number of patches of a library of a size: the regular ones, and as many noise-free ones for a profile with noise.</summary>
    /// <param name="capabilities">Capabilities of the profile.</param>
    /// <param name="size">Number of regular patches.</param>
    public static int CountFor(SynthesisCapabilities capabilities, int size)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return HasNoise(capabilities) ? 2 * size : size;
    }

    /// <summary>
    /// How many patches <see cref="For"/> would render: 0 when the library is in memory or on disk, the noise-free ones
    /// when the file of <see cref="SynthesisProfile.StructureLibraryReusedVersion"/> is on disk, all of them otherwise.
    /// </summary>
    /// <param name="profile">Synthesis profile.</param>
    /// <param name="size">Number of regular patches.</param>
    /// <param name="directory">Folder of the library files; the user's local application data when null.</param>
    public static int SoundsToRender(SynthesisProfile profile, int size, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (IsAvailable(profile, size, directory))
        {
            return 0;
        }

        int count = CountFor(profile.Capabilities, size);
        return count > size && ReusedPathOf(profile, size, directory) is { } reused && File.Exists(reused) ? count - size : count;
    }

    /// <summary>True when the library of a profile and size is in memory or on disk, so that <see cref="For"/> returns at once.</summary>
    /// <param name="profile">Synthesis profile.</param>
    /// <param name="size">Number of patches.</param>
    /// <param name="directory">Folder of the library files; the user's local application data when null.</param>
    public static bool IsAvailable(SynthesisProfile profile, int size, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string path = PathOf(profile, size, directory);
        return (Cache.TryGetValue(path, out Lazy<StructureLibrary>? lazy) && lazy.IsValueCreated) || File.Exists(path);
    }

    /// <summary>The file a library of a profile and size is kept in.</summary>
    /// <param name="profile">Synthesis profile.</param>
    /// <param name="size">Number of patches.</param>
    /// <param name="directory">Folder of the library files; the user's local application data when null.</param>
    public static string FileOf(SynthesisProfile profile, int size, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return PathOf(profile, size, directory);
    }

    private static string PathOf(SynthesisProfile profile, int size, string? directory)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HearSay", "structure-libraries");
        return Path.Combine(directory, $"{profile.Name}-v{profile.StructureLibraryVersion}-{size}.bin");
    }

    private static string? ReusedPathOf(SynthesisProfile profile, int size, string? directory) =>
        profile.StructureLibraryReusedVersion is int version ? PathOf(profile with { StructureLibraryVersion = version }, size, directory) : null;

    private static bool HasNoise(SynthesisCapabilities capabilities) =>
        capabilities.SupportsSharedNoise || capabilities.SupportedWaveforms.Any(static w => w.IsNoise());

    /// <summary>Builds a library in memory without touching the disk.</summary>
    /// <param name="capabilities">Capabilities the patches are generated within.</param>
    /// <param name="size">Number of regular patches; see <see cref="CountFor"/>.</param>
    /// <param name="renderer">Renderer that plays them.</param>
    /// <param name="progress">Fraction of the patches rendered, about a hundred times and 1 at the end.</param>
    public static StructureLibrary Build(SynthesisCapabilities capabilities, int size, IOperatorMatrixRenderer renderer, IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(renderer);
        int[] seeds = [.. Enumerable.Range(FirstSeed, size), .. NoiseFreeSeeds(capabilities, size)];
        return new StructureLibrary(capabilities, seeds, Render(capabilities, seeds, renderer, progress));
    }

    private static IEnumerable<int> NoiseFreeSeeds(SynthesisCapabilities capabilities, int size) =>
        Enumerable.Range(NoiseFreeFirstSeed, CountFor(capabilities, size) - size);

    /// <summary>The library of an older version whose sounds are the regular ones of this version, with the noise-free sounds added.</summary>
    private static StructureLibrary Extend(StructureLibrary regular, IOperatorMatrixRenderer renderer, IProgress<double>? progress)
    {
        int[] added = [.. NoiseFreeSeeds(regular._capabilities, regular.Count)];
        byte[] prints = Render(regular._capabilities, added, renderer, progress);
        return new StructureLibrary(regular._capabilities, [.. regular._seeds, .. added], [.. regular._prints, .. prints]);
    }

    private static byte[] Render(SynthesisCapabilities capabilities, int[] seeds, IOperatorMatrixRenderer renderer, IProgress<double>? progress)
    {
        var prints = new byte[seeds.Length * SpectralFingerprint.Length];
        int done = 0;
        int step = Math.Max(1, seeds.Length / 100);
        Parallel.For(0, seeds.Length, i =>
        {
            OperatorMatrixPatch patch = Generate(capabilities, seeds[i]);
            RenderedAudio audio = renderer.Render(patch, ReferenceRequest);
            SpectralFingerprint.Of(audio.Samples, audio.SampleRate, ReferenceHz).CopyTo(prints, i * SpectralFingerprint.Length);
            int count = Interlocked.Increment(ref done);
            if (count % step == 0 && count < seeds.Length)
            {
                progress?.Report((double)count / seeds.Length);
            }
        });
        progress?.Report(1.0);
        return prints;
    }

    /// <summary>The library patch of a seed.</summary>
    /// <param name="seed">Seed from <see cref="Nearest"/>.</param>
    public OperatorMatrixPatch Patch(int seed) => Generate(_capabilities, seed);

    /// <summary>The fingerprint of the patch at an index.</summary>
    /// <param name="index">Index, 0 to <see cref="Count"/> - 1.</param>
    public ReadOnlySpan<byte> Fingerprint(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        return _prints.AsSpan(index * SpectralFingerprint.Length, SpectralFingerprint.Length);
    }

    /// <summary>The seed of the patch at an index; <see cref="Patch"/> regenerates the patch from it.</summary>
    /// <param name="index">Index, 0 to <see cref="Count"/> - 1.</param>
    public int Seed(int index) => _seeds[index];

    /// <summary>A library from given seeds and fingerprints, for tests.</summary>
    internal static StructureLibrary FromFingerprints(SynthesisCapabilities capabilities, int[] seeds, byte[] prints) => new(capabilities, seeds, prints);

    /// <summary>The seeds of the patches whose fingerprints are closest to a fingerprint, closest first.</summary>
    /// <param name="print">Fingerprint of the target.</param>
    /// <param name="count">Number of neighbours.</param>
    public IReadOnlyList<(int Seed, long Distance)> Nearest(byte[] print, int count)
    {
        ArgumentNullException.ThrowIfNull(print);
        int length = SpectralFingerprint.Length;
        var best = new (long Distance, int Seed)[Math.Max(0, count)];
        Array.Fill(best, (long.MaxValue, 0));
        for (int i = 0; i < _seeds.Length; i++)
        {
            long distance = SpectralFingerprint.Distance(_prints.AsSpan(i * length, length), print);
            if (count > 0 && distance < best[^1].Distance)
            {
                // Insertion into the short sorted list of the best so far.
                int at = best.Length - 1;
                while (at > 0 && best[at - 1].Distance > distance)
                {
                    best[at] = best[at - 1];
                    at--;
                }

                best[at] = (distance, _seeds[i]);
            }
        }

        return [.. best.Where(static b => b.Distance != long.MaxValue).Select(static b => (b.Seed, b.Distance))];
    }

    /// <summary>
    /// The musical random patch of a seed: the seed picks one of three densities, and a patch that breaks the rules is
    /// drawn again with a derived seed. From <see cref="NoiseFreeFirstSeed"/> on, no operator is noise or roughened.
    /// </summary>
    internal static OperatorMatrixPatch Generate(SynthesisCapabilities capabilities, int seed)
    {
        bool noiseFree = seed >= NoiseFreeFirstSeed;
        int kind = ((seed % 3) + 3) % 3;
        var options = new RandomPatchOptions
        {
            MinOperators = kind switch { 0 => 4, 1 => 3, _ => 2 },
            MaxOperators = kind switch { 0 => 6, 1 => 4, _ => 3 },
            EdgeDensity = 0.25,
            AllowCircularFeedback = false,
            FixedFrequencyProbability = 0,
            MaxEdgeAmount = 2.0,
            RoughnessProbability = noiseFree ? 0.0 : 0.2,
            NoiseWaveformProbability = noiseFree ? 0.0 : null,
            FilterProbability = capabilities.SupportsOperatorFilters ? 0.3 : 0.0,
        };
        int heard = options.MinOperators;
        var generator = new RandomPatchGenerator(capabilities, options);
        OperatorMatrixPatch patch = generator.Generate(seed);
        for (int retry = 1; retry < 400 && !IsMusical(patch, heard); retry++)
        {
            patch = generator.Generate(unchecked(seed + (RetryStride * retry)));
        }

        return patch;
    }

    private static bool IsMusical(OperatorMatrixPatch patch, int minimumHeard)
    {
        bool[] audible = patch.FindAudibleOperators();
        int heard = 0;
        bool onTheNote = false;
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            heard++;
            if (patch.Operators[i].Frequency.Ratio < 1.0)
            {
                return false;
            }

            onTheNote |= patch.OutputLevels[i] != 0.0 && patch.Operators[i].Frequency.Ratio == 1.0;
        }

        return heard >= minimumHeard && onTheNote;
    }

    private static StructureLibrary LoadOrBuild(SynthesisProfile profile, int size, IOperatorMatrixRenderer renderer, string? directory, string path, IProgress<double> progress)
    {
        SynthesisCapabilities capabilities = profile.Capabilities;
        int version = profile.StructureLibraryVersion;
        int count = CountFor(capabilities, size);
        if (TryLoad(capabilities, version, count, path) is { } loaded)
        {
            return loaded;
        }

        StructureLibrary built = count > size
            && ReusedPathOf(profile, size, directory) is { } reusedPath
            && TryLoad(capabilities, profile.StructureLibraryReusedVersion!.Value, size, reusedPath) is { } reused
            ? Extend(reused, renderer, progress)
            : Build(capabilities, size, renderer, progress);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                writer.Write(version);
                writer.Write(count);
                writer.Write(SpectralFingerprint.Length);
                foreach (int seed in built._seeds)
                {
                    writer.Write(seed);
                }

                writer.Write(built._prints);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException)
        {
            // A library that cannot be saved still serves this process.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return built;
    }

    private static StructureLibrary? TryLoad(SynthesisCapabilities capabilities, int version, int count, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != version || reader.ReadInt32() != count || reader.ReadInt32() != SpectralFingerprint.Length)
            {
                return null;
            }

            var seeds = new int[count];
            for (int i = 0; i < count; i++)
            {
                seeds[i] = reader.ReadInt32();
            }

            byte[] prints = reader.ReadBytes(count * SpectralFingerprint.Length);
            return prints.Length == count * SpectralFingerprint.Length ? new StructureLibrary(capabilities, seeds, prints) : null;
        }
        catch (IOException)
        {
            // Also a truncated file (EndOfStreamException): it is rebuilt.
            return null;
        }
    }

    /// <summary>Passes the progress of a build to every caller that waits for it.</summary>
    private sealed class ProgressRelay : IProgress<double>
    {
        private readonly List<IProgress<double>> _listeners = [];

        public void Add(IProgress<double>? listener)
        {
            if (listener is not null)
            {
                lock (_listeners)
                {
                    _listeners.Add(listener);
                }
            }
        }

        public void Remove(IProgress<double>? listener)
        {
            if (listener is not null)
            {
                lock (_listeners)
                {
                    _listeners.Remove(listener);
                }
            }
        }

        public void Report(double value)
        {
            IProgress<double>[] listeners;
            lock (_listeners)
            {
                listeners = [.. _listeners];
            }

            foreach (IProgress<double> listener in listeners)
            {
                listener.Report(value);
            }
        }
    }
}
