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
          hearsay match <target.wav> [options]     find a patch that approximates a monophonic note
          hearsay analyze <target.wav> [options]   print the target analysis as JSON
          hearsay profiles                         list synthesis profiles

        Options for match:
          --profile <name>     synthesis profile (default supercollider-fm7)
          --budget <n>         number of candidate renders (default 20000)
          --seed <n>           random seed (default 1234)
          --output <dir>       result folder (default result)
          --note <midi>        MIDI note of the target, when known
          --f0 <hz>            fundamental of the target, when known
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
        (string? path, Dictionary<string, string> options) = Parse(args);
        if (path is null)
        {
            return Fail("match needs a WAV file.");
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

        AudioTarget target = AudioTarget.FromWaveFile(path, Number(options, "f0"), (int?)Number(options, "note"));
        string lastStage = string.Empty;
        var progress = new SynchronousProgress(p =>
        {
            if (!quiet && p.Stage != lastStage)
            {
                lastStage = p.Stage;
                Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{p.EvaluationsUsed,6}/{p.EvaluationBudget}] {p.Stage,-32} best loss {p.BestLoss:F4}"));
            }
        });

        PatchMatchResult result = new OperatorMatrixPatchMatcher().Match(target, matchOptions, progress);
        IReadOnlyList<string> files = PatchMatchResultWriter.Write(result, output);

        SearchSummary summary = result.SearchSummary;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"target      {Path.GetFileName(path)}: f0 {result.Analysis.FundamentalHz:F2} Hz, {result.Analysis.DurationSeconds:F2} s, {result.Analysis.Partials.Count} partials, inharmonicity {result.Analysis.Inharmonicity:F2}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"search      {summary.EvaluationsUsed} renders in {summary.Elapsed.TotalSeconds:F1} s, profile {summary.Profile}, seed {summary.RandomSeed}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"audio loss  {summary.InitialAudioLoss:F4} (best untouched hypothesis) -> {summary.FinalAudioLoss:F4}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"similarity  {result.Comparison.Scores.Similarity:F3} (HearSay overall distance {result.Comparison.Scores.Overall:F4})"));
        Console.WriteLine($"structure   {result.AlternativePatches[0].Regime}");
        Console.WriteLine($"origin      {result.AlternativePatches[0].Origin}");
        Console.WriteLine();
        Console.WriteLine(DescribeRouting(result.BestPatch));
        Console.WriteLine($"{files.Count} files written to {Path.GetFullPath(output)}");
        return 0;
    }

    /// <summary>Operators and modulation matrix as text.</summary>
    internal static string DescribeRouting(OperatorMatrixPatch patch)
    {
        bool[] audible = patch.FindAudibleOperators();
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!audible[i])
            {
                continue;
            }

            OperatorPatch op = patch.Operators[i];
            string frequency = op.Frequency.Mode == OperatorFrequencyMode.Ratio
                ? string.Create(CultureInfo.InvariantCulture, $"ratio {op.Frequency.Ratio:0.###}")
                : string.Create(CultureInfo.InvariantCulture, $"fixed {op.Frequency.FixedFrequencyHz:0.#} Hz");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"OP{i + 1}  {op.Waveform,-8} {frequency,-16} fine {op.FineTuneCents,6:F1} ct  detune {op.DetuneHz,5:F2} Hz  output {patch.OutputLevels[i]:F3}"));
        }

        text.AppendLine();
        text.AppendLine("modulation matrix (rows: destination, columns: source, radians)");
        text.Append("      ");
        for (int s = 0; s < patch.OperatorCount; s++)
        {
            text.Append(CultureInfo.InvariantCulture, $"   OP{s + 1} ");
        }

        text.AppendLine();
        for (int d = 0; d < patch.OperatorCount; d++)
        {
            text.Append(CultureInfo.InvariantCulture, $"  OP{d + 1} ");
            for (int s = 0; s < patch.OperatorCount; s++)
            {
                double amount = audible[d] && audible[s] ? patch.ModulationMatrix[d, s] : 0.0;
                text.Append(amount == 0.0 ? "     . " : string.Create(CultureInfo.InvariantCulture, $" {amount,6:F2}"));
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static (string? Path, Dictionary<string, string> Options) Parse(string[] args)
    {
        string? path = null;
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
                path ??= args[i];
            }
        }

        return (path, options);
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
