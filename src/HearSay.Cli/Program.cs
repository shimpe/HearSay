using System.Globalization;
using HearSay;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

return Cli.Run(args);

/// <summary>Command line of HearSay.</summary>
internal static class Cli
{
    private const string Usage = """
        hearsay - audio-to-operator-matrix solver

        Usage:
          hearsay match <target.wav>... [options]  find a patch that approximates one note, or one patch for several notes
                                                   of the same sound: file.wav@60 (MIDI note), file.wav@261.6 (Hz), file.wav@60:v0.5 (velocity)
          hearsay analyze <target.wav> [options]   print the target analysis as JSON
          hearsay profiles                         list synthesis profiles
          hearsay reference [name] [options]       render a built-in reference patch to WAV files, as test targets
                                                   (no name: list the patches). --notes 48,60,72 --velocities 1,0.5
                                                   --seconds 1.0 --release 0.3 --output dir

        Options for match:
          --profile <name>     synthesis profile: supercollider-fm7 (default), supercollider-fmx6 or supercollider-nome6
          --budget <n>         number of candidate evaluations; one evaluation renders every target note (default 20000)
          --seed <n>           random seed (default 1234)
          --seeds <n>          run n consecutive seeds with the full budget each; the best wins, the finalists of all seeds become alternatives (default 1)
          --output <dir>       result folder (default result)
          --note <midi>        MIDI note of targets without a pitch of their own, when known
          --f0 <hz>            fundamental of targets without a pitch of their own, when known
          --sample-rate <hz>   analysis and render rate; use the rate the patch will run at (default 48000)
          --max-seconds <s>    longest stretch of the target that is matched (default 4)
          --parallel <n>       simultaneous evaluations (default: all processors)
          --quiet              no progress output
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0] switch
            {
                "match" => Match(args),
                "analyze" => Analyze(args),
                "profiles" => Profiles(),
                "reference" => Reference(args),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or NotSupportedException or FormatException)
        {
            return Fail(e.Message);
        }
    }

    private static int Profiles()
    {
        foreach (SynthesisProfile profile in SynthesisProfiles.All)
        {
            string status;
            try
            {
                SynthesisBackends.RendererFor(profile);
                status = SynthesisBackends.ExporterFor(profile) is null ? "searchable, no export backend" : "searchable, exportable";
            }
            catch (NotSupportedException)
            {
                status = "reserved, no renderer yet";
            }

            Console.WriteLine($"{profile.Name,-28} {status,-32} {profile.Description}");
        }

        return 0;
    }

    private static int Reference(string[] args)
    {
        (IReadOnlyList<string> names, Dictionary<string, string> options) = ParseAll(args);
        if (names.Count == 0)
        {
            foreach (ReferencePatch p in ReferencePatches.All)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{p.Name,-22} {p.FrequencyHz,7:F1} Hz  {p.NoteSeconds:F1} s + {p.ReleaseSeconds:F1} s"));
            }

            return 0;
        }

        ReferencePatch? reference = ReferencePatches.Find(names[0]);
        if (reference is null)
        {
            return Fail($"Unknown reference patch '{names[0]}'. Run 'hearsay reference' for the list.");
        }

        int[] notes = options.TryGetValue("notes", out string? noteList)
            ? [.. noteList.Split(',').Select(static n => int.Parse(n, CultureInfo.InvariantCulture))]
            : [(int)Math.Round(69 + (12 * Math.Log2(reference.FrequencyHz / 440.0)))];
        double[] velocities = options.TryGetValue("velocities", out string? velocityList)
            ? [.. velocityList.Split(',').Select(static v => double.Parse(v, CultureInfo.InvariantCulture))]
            : [1.0];
        double seconds = Number(options, "seconds") ?? reference.NoteSeconds;
        double release = Number(options, "release") ?? reference.ReleaseSeconds;
        string output = options.GetValueOrDefault("output", reference.Name);
        Directory.CreateDirectory(output);
        var renderer = new HearSay.Synthesis.Fm7Compatible.Rendering.Fm7CompatibleOperatorMatrixRenderer();
        File.WriteAllText(Path.Combine(output, "hidden-patch.json"), reference.Patch.ToJson());
        foreach (int midi in notes)
        {
            foreach (double velocity in velocities)
            {
                double hz = 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
                var request = new HearSay.Synthesis.Rendering.RenderRequest(hz, midi, velocity, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(release), 48000);
                string suffix = velocities.Length > 1 ? string.Create(CultureInfo.InvariantCulture, $"-v{velocity:0.00}") : string.Empty;
                string path = Path.Combine(output, string.Create(CultureInfo.InvariantCulture, $"{reference.Name}-{midi}{suffix}.wav"));
                renderer.Render(reference.Patch, request).WriteWav(path, normalizePeakTo: 0.7f);
                string hint = velocities.Length > 1 ? FormattableString.Invariant($":v{velocity:0.##}") : string.Empty;
                Console.WriteLine(FormattableString.Invariant($"{path}  ({hz:F1} Hz, velocity {velocity:F2}) -> match with {Path.GetFileName(path)}@{midi}{hint}"));
            }
        }

        return 0;
    }

    private static int Analyze(string[] args)
    {
        (string? path, Dictionary<string, string> options) = Parse(args);
        if (path is null)
        {
            return Fail("analyze needs a WAV file.");
        }

        AudioTarget target = AudioTarget.FromWaveFile(path, Number(options, "f0"), (int?)Number(options, "note"));
        TargetAnalysis analysis = new TargetAnalyzer().Analyze(target, new TargetAnalysisOptions { SampleRate = (int)(Number(options, "sample-rate") ?? 48000) });
        Console.WriteLine(HearSayJson.Serialize(TargetAnalysisReport.From(analysis)));
        return analysis.IsUsable ? 0 : 2;
    }

    private static int Match(string[] args)
    {
        (IReadOnlyList<string> paths, Dictionary<string, string> options) = ParseAll(args);
        if (paths.Count == 0)
        {
            return Fail("match needs at least one WAV file.");
        }

        string profileName = options.GetValueOrDefault("profile", SynthesisProfiles.SuperColliderFm7.Name);
        SynthesisProfile? profile = SynthesisProfiles.Find(profileName);
        if (profile is null)
        {
            return Fail($"Unknown profile '{profileName}'. Run 'hearsay profiles'.");
        }

        var matchOptions = new PatchMatchOptions
        {
            SynthesisProfile = profile,
            EvaluationBudget = (int)(Number(options, "budget") ?? 20000),
            RandomSeed = (int)(Number(options, "seed") ?? 1234),
            SampleRate = (int)(Number(options, "sample-rate") ?? 48000),
            MaxSeconds = Number(options, "max-seconds") ?? 4.0,
            MaxParallelEvaluations = (int)(Number(options, "parallel") ?? Environment.ProcessorCount),
        };
        string output = options.GetValueOrDefault("output", "result");
        bool quiet = options.ContainsKey("quiet");

        List<AudioTarget> targets = [.. paths.Select(p => ReadTarget(p, options))];
        string lastStage = string.Empty;
        var progress = new SynchronousProgress(p =>
        {
            string key = p.Run + ":" + p.Stage;
            if (!quiet && key != lastStage)
            {
                lastStage = key;
                string run = p.RunCount > 1 ? FormattableString.Invariant($"seed {p.Run}/{p.RunCount} ") : string.Empty;
                Console.Error.WriteLine(FormattableString.Invariant($"{run}[{p.EvaluationsUsed,6}/{p.EvaluationBudget}] {p.Stage,-32} best loss {p.BestLoss:F4}"));
            }
        });

        int seeds = (int)(Number(options, "seeds") ?? 1);
        PatchMatchResult result = new OperatorMatrixPatchMatcher().MatchBestOf(targets, seeds, matchOptions, progress);
        IReadOnlyList<string> files = PatchMatchResultWriter.Write(result, output);

        SearchSummary summary = result.SearchSummary;
        foreach (NoteMatch note in result.Notes)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"target      f0 {note.Analysis.FundamentalHz:F2} Hz, velocity {note.Request.Velocity:F2}, {note.Analysis.DurationSeconds:F2} s, {note.Analysis.Partials.Count} partials, inharmonicity {note.Analysis.Inharmonicity:F2}, similarity {note.Comparison.Scores.Similarity:F3}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"search      {summary.EvaluationsUsed} evaluations, {summary.RendersUsed} renders in {summary.Elapsed.TotalSeconds:F1} s, profile {summary.Profile}, seed {summary.RandomSeed}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"audio loss  {summary.InitialAudioLoss:F4} (best untouched hypothesis) -> {summary.FinalAudioLoss:F4}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"similarity  {result.Comparison.Scores.Similarity:F3} (HearSay overall distance {result.Comparison.Scores.Overall:F4})"));
        Console.WriteLine($"structure   {result.AlternativePatches[0].Regime}");
        Console.WriteLine($"origin      {result.AlternativePatches[0].Origin}");
        Console.WriteLine();
        Console.WriteLine(PatchDescription.Routing(result.BestPatch));
        Console.WriteLine($"{files.Count} files written to {Path.GetFullPath(output)}");
        return 0;
    }

    private static (string? Path, Dictionary<string, string> Options) Parse(string[] args)
    {
        (IReadOnlyList<string> paths, Dictionary<string, string> options) = ParseAll(args);
        return (paths.Count > 0 ? paths[0] : null, options);
    }

    private static (IReadOnlyList<string> Paths, Dictionary<string, string> Options) ParseAll(string[] args)
    {
        var paths = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                string name = args[i][2..];
                bool hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                options[name] = hasValue ? args[++i] : "true";
            }
            else
            {
                paths.Add(args[i]);
            }
        }

        return (paths, options);
    }

    /// <summary>
    /// Reads a target argument: <c>file.wav</c>, <c>file.wav@60</c> (MIDI note), <c>file.wav@261.6</c> (hertz, any
    /// value above 127) and an optional <c>:v0.8</c> velocity suffix. Global --f0 and --note apply to files without a pitch.
    /// </summary>
    private static AudioTarget ReadTarget(string argument, Dictionary<string, string> options)
    {
        string spec = argument;
        double? velocity = null;
        int colon = spec.LastIndexOf(":v", StringComparison.OrdinalIgnoreCase);
        if (colon > 0 && double.TryParse(spec[(colon + 2)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            velocity = Math.Clamp(v, 0.0, 1.0);
            spec = spec[..colon];
        }

        double? f0 = Number(options, "f0");
        int? midi = (int?)Number(options, "note");
        int at = spec.LastIndexOf('@');
        if (at > 0 && double.TryParse(spec[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double pitch))
        {
            spec = spec[..at];
            if (pitch > 127)
            {
                f0 = pitch;
                midi = null;
            }
            else
            {
                midi = (int)Math.Round(pitch);
                f0 = null;
            }
        }

        AudioTarget target = AudioTarget.FromWaveFile(spec, f0, midi);
        return velocity is null ? target : target with { Velocity = velocity };
    }

    private static double? Number(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out string? text) ? double.Parse(text, CultureInfo.InvariantCulture) : null;

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 1;
    }

    /// <summary>Progress that reports on the calling thread, so that lines appear in order.</summary>
    private sealed class SynchronousProgress(Action<SearchProgress> handler) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => handler(value);
    }
}
