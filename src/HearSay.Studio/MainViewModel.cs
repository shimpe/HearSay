using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HearSay.Analysis;
using HearSay.OperatorMatrix.Search;
using HearSay.SuperCollider.FM7;
using HearSay.Synthesis.Export;

namespace HearSay.Studio;

/// <summary>
/// State of the main window: the loaded target, the search options, the running search and its result. File and
/// clipboard access stay in the view; the view model only exposes what to do with a path once it is known.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OperatorMatrixPatchMatcher _matcher = new();
    private CancellationTokenSource? _cancellation;
    private string? _targetPath;
    private string _targetSummary = "No target loaded.";
    private string _fundamental = string.Empty;
    private int _budget = 20000;
    private int _seed = 1234;
    private int _seeds = 1;
    private bool _isRunning;
    private double _progress;
    private string _status = "Load a WAV file with a single dry note to begin.";
    private string _resultSummary = string.Empty;
    private string _scd = string.Empty;
    private PatchMatchResult? _result;

    public MainViewModel()
    {
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => TargetPath is not null && !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Starts the search; enabled once a target is loaded.</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>Stops the running search and keeps the best patch found so far.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Path of the loaded target, or null.</summary>
    public string? TargetPath
    {
        get => _targetPath;
        private set => Set(ref _targetPath, value);
    }

    /// <summary>What the analysis found in the target.</summary>
    public string TargetSummary
    {
        get => _targetSummary;
        private set => Set(ref _targetSummary, value);
    }

    /// <summary>Fundamental in hertz typed by the user; empty means estimate it.</summary>
    public string Fundamental
    {
        get => _fundamental;
        set => Set(ref _fundamental, value);
    }

    /// <summary>Renders per search.</summary>
    public int Budget
    {
        get => _budget;
        set => Set(ref _budget, Math.Clamp(value, 200, 1_000_000));
    }

    /// <summary>Random seed of the first run.</summary>
    public int Seed
    {
        get => _seed;
        set => Set(ref _seed, value);
    }

    /// <summary>Number of consecutive seeds; the best result is kept.</summary>
    public int Seeds
    {
        get => _seeds;
        set => Set(ref _seeds, Math.Clamp(value, 1, 16));
    }

    /// <summary>True while a search runs.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                RunCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Share of the budget spent, 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>One line about what is happening.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Losses, similarity, structure and routing of the best patch.</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        private set => Set(ref _resultSummary, value);
    }

    /// <summary>The generated SuperCollider code.</summary>
    public string Scd
    {
        get => _scd;
        private set => Set(ref _scd, value);
    }

    /// <summary>True once a result exists.</summary>
    public bool HasResult => _result is not null;

    /// <summary>Loads and analyses a WAV file.</summary>
    /// <param name="path">Path of the file.</param>
    public async Task LoadAsync(string path)
    {
        Status = "Analysing " + Path.GetFileName(path) + "…";
        try
        {
            double? f0 = ParseFundamental();
            TargetAnalysis analysis = await Task.Run(() => new TargetAnalyzer().Analyze(AudioTarget.FromWaveFile(path, f0)));
            TargetPath = path;
            TargetSummary = Describe(analysis);
            Status = analysis.IsUsable ? "Ready. Press Run." : "This file cannot be matched: " + string.Join(", ", analysis.Issues);
            SetResult(null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            Status = "Could not load the file: " + e.Message;
        }

        RunCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Runs the search with the current options.</summary>
    public async Task RunAsync()
    {
        if (TargetPath is null || IsRunning)
        {
            return;
        }

        IsRunning = true;
        Progress = 0;
        SetResult(null);
        _cancellation = new CancellationTokenSource();
        var options = new PatchMatchOptions { EvaluationBudget = Budget, RandomSeed = Seed };
        var progress = new Progress<SearchProgress>(p =>
        {
            Progress = (double)p.EvaluationsUsed / p.EvaluationBudget;
            Status = FormattableString.Invariant($"{p.Stage}: {p.EvaluationsUsed} of {p.EvaluationBudget} renders, best loss {p.BestLoss:F4}");
        });

        try
        {
            string path = TargetPath;
            int seeds = Seeds;
            double? f0 = ParseFundamental();
            CancellationToken token = _cancellation.Token;
            PatchMatchResult result = await Task.Run(() => _matcher.MatchBestOf(AudioTarget.FromWaveFile(path, f0), seeds, options, progress, token), CancellationToken.None);
            SetResult(result);
            Progress = 1;
            Status = token.IsCancellationRequested ? "Stopped; showing the best patch found so far." : "Done.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            Status = "The search failed: " + e.Message;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>Stops the running search.</summary>
    public void Cancel()
    {
        _cancellation?.Cancel();
        Status = "Stopping after the current step…";
    }

    /// <summary>Writes the result folder.</summary>
    /// <param name="directory">Destination folder.</param>
    public async Task SaveAsync(string directory)
    {
        if (_result is null)
        {
            return;
        }

        PatchMatchResult result = _result;
        IReadOnlyList<string> files = await Task.Run(() => PatchMatchResultWriter.Write(result, directory));
        Status = $"{files.Count} files written to {directory}.";
    }

    private void SetResult(PatchMatchResult? result)
    {
        _result = result;
        if (result is null)
        {
            ResultSummary = string.Empty;
            Scd = string.Empty;
        }
        else
        {
            SearchSummary s = result.SearchSummary;
            PatchCandidateResult best = result.AlternativePatches[0];
            string routing = PatchDescription.Routing(result.BestPatch);
            ResultSummary = FormattableString.Invariant(
                $"""
                audio loss  {s.InitialAudioLoss:F4} (best untouched hypothesis) -> {s.FinalAudioLoss:F4}
                similarity  {result.Comparison.Scores.Similarity:F3}
                search      {s.EvaluationsUsed} renders in {s.Elapsed.TotalSeconds:F1} s, seed {s.RandomSeed}
                origin      {best.Origin}

                {routing}
                """);
            Scd = SynthesisBackends.ExporterFor(result.Profile)?.Export(result.BestPatch, new PatchExportOptions
            {
                HeaderComments =
                [
                    string.Create(CultureInfo.InvariantCulture, $"Matched to {Path.GetFileName(TargetPath)} at {result.Analysis.FundamentalHz:F2} Hz; audio loss {best.AudioLoss:F4}, similarity {result.Comparison.Scores.Similarity:F3}."),
                ],
            }) ?? "This profile has no export backend.";
        }

        OnPropertyChanged(nameof(HasResult));
    }

    private double? ParseFundamental() =>
        double.TryParse(Fundamental, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz) && hz > 0 ? hz : null;

    private static string Describe(TargetAnalysis a)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"fundamental   {a.FundamentalHz:F2} Hz (confidence {a.FundamentalConfidence:F2})"),
            string.Create(CultureInfo.InvariantCulture, $"length        {a.DurationSeconds:F2} s, note-off at {a.NoteOffSeconds:F2} s"),
            string.Create(CultureInfo.InvariantCulture, $"partials      {a.Partials.Count}, inharmonicity {a.Inharmonicity:F2}"),
            FormattableString.Invariant($"decay         {a.AmplitudeDecay.FastRateDbPerSecond:F1} dB/s") + (a.AmplitudeDecay.IsTwoStage ? FormattableString.Invariant($" then {a.AmplitudeDecay.SlowRateDbPerSecond:F1} dB/s") : string.Empty),
        };
        if (a.HasPitchMovement)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"pitch         offset {a.Pitch.AttackOffsetCents:F0} ct, vibrato {a.Pitch.VibratoDepthCents:F0} ct at {a.Pitch.VibratoRateHz:F1} Hz"));
        }

        if (a.Beating.IsPresent)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"beating       {a.Beating.RateHz:F1} Hz, {a.Beating.DepthDb:F1} dB"));
        }

        if (a.Issues.Count > 0)
        {
            lines.Add("issues        " + string.Join(", ", a.Issues));
        }

        return string.Join('\n', lines);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Command backed by delegates.</summary>
public sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
