using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HearSay.Analysis;
using HearSay.Editing;
using HearSay.OperatorMatrix.Search;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>One loaded target note.</summary>
public sealed class TargetItem : INotifyPropertyChanged
{
    private string _pitch = string.Empty;
    private string _velocity = string.Empty;
    private string _summary = string.Empty;

    /// <summary>Creates an item for a file.</summary>
    /// <param name="path">Path of the WAV file.</param>
    /// <param name="analysis">First analysis, with the pitch estimated.</param>
    public TargetItem(string path, TargetAnalysis analysis)
    {
        Path = path;
        Update(analysis);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Path of the file.</summary>
    public string Path { get; }

    /// <summary>File name.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Pitch typed by the user: a MIDI note (up to 127) or a frequency in hertz. Empty means estimate it.</summary>
    public string Pitch
    {
        get => _pitch;
        set
        {
            _pitch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pitch)));
        }
    }

    /// <summary>Velocity 0..1 typed by the user; empty means the default.</summary>
    public string Velocity
    {
        get => _velocity;
        set
        {
            _velocity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Velocity)));
        }
    }

    /// <summary>What the analysis found.</summary>
    public string Summary
    {
        get => _summary;
        private set
        {
            _summary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    /// <summary>The target as the matcher takes it, with the typed pitch and velocity applied.</summary>
    public AudioTarget ToTarget()
    {
        double? f0 = null;
        int? midi = null;
        if (double.TryParse(Pitch, NumberStyles.Float, CultureInfo.InvariantCulture, out double pitch) && pitch > 0)
        {
            if (pitch > 127)
            {
                f0 = pitch;
            }
            else
            {
                midi = (int)Math.Round(pitch);
            }
        }

        double? velocity = double.TryParse(Velocity, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? Math.Clamp(v, 0.0, 1.0) : null;
        return AudioTarget.FromWaveFile(Path, f0, midi) with { Velocity = velocity };
    }

    /// <summary>Refreshes the summary from a new analysis.</summary>
    /// <param name="a">Analysis of this file.</param>
    public void Update(TargetAnalysis a)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"f0 {a.FundamentalHz:F1} Hz, {a.DurationSeconds:F2} s, note-off {a.NoteOffSeconds:F2} s, {a.Partials.Count} partials, inharmonicity {a.Inharmonicity:F2}");
        if (a.Beating.IsPresent)
        {
            text += string.Create(CultureInfo.InvariantCulture, $", beating {a.Beating.RateHz:F1} Hz");
        }

        if (a.HasPitchMovement)
        {
            text += ", pitch moves";
        }

        if (a.Issues.Count > 0)
        {
            text += "; " + string.Join(", ", a.Issues);
        }

        Summary = text;
    }
}

/// <summary>
/// State of the main window: the loaded target notes, the search options, the running search and its result, and the
/// live notes of both tabs. File and clipboard access stay in the view; the view model only exposes what to do with a
/// path once it is known.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly OperatorMatrixPatchMatcher _matcher = new();
    private CancellationTokenSource? _cancellation;
    private TargetItem? _selectedTarget;
    private int _budget = 20000;
    private int _seed = 1234;
    private int _seeds = 1;
    private bool _useStructureLibrary = true;
    private SynthesisTargetChoice? _selectedSynthesisTarget;
    private bool _isRunning;
    private double _progress;
    private string _status = "Add one or more WAV files with a single dry note each. Several notes of the same sound at different pitches make key scaling and fixed-frequency operators searchable.";
    private string _resultSummary = string.Empty;
    private string _scd = string.Empty;
    private PatchMatchResult? _result;
    private LibraryPatch? _libraryPatch;
    private readonly object _keyboard = new();
    private string _playingText = string.Empty;
    private AlternativeItem? _selectedAlternative;
    private PatchCandidateResult? _candidate;
    private bool _statsShowEdit;
    private PatchCandidateResult? _statsCandidate;
    // Each finalist's diagnosis against the target notes, by finalist: the search gives the best one's, the others are
    // compared when first shown.
    private readonly Dictionary<PatchCandidateResult, IReadOnlyList<NoteMatch>> _noteMatches = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PatchCandidateResult> _comparing = new(ReferenceEqualityComparer.Instance);
    private bool _detailsPending;
    private string _favouriteNickname = string.Empty;

    /// <summary>The last result of every synthesis target, by profile name, so that the targets can be compared by ear.</summary>
    private readonly Dictionary<string, PatchMatchResult> _results = new(StringComparer.Ordinal);

    /// <summary>What each kept result was searched with (<see cref="SearchInputs"/>); a search with the same inputs reuses it.</summary>
    private readonly Dictionary<string, string> _resultInputs = new(StringComparer.Ordinal);

    /// <param name="favourites">Where the favourites are kept; the user's local application data when null.</param>
    /// <param name="studioSettingsPath">Where the Studio settings (the MIDI device) are kept; the user's local application data when null.</param>
    /// <param name="gainOf">The gain a sound is played at, for the editing of both tabs; the live mixer's when null.</param>
    /// <param name="openLibrary">
    /// Opens the library of a profile for the Library tab, off the interface thread; the one on disk (built the first
    /// time) when null.
    /// </param>
    public MainViewModel(FavouriteSounds? favourites = null, string? studioSettingsPath = null, Func<Sound, Task<float>>? gainOf = null, Func<SynthesisProfile, HearSay.OperatorMatrix.Priors.LibraryAtlas>? openLibrary = null)
    {
        Live = new LivePlayer(studioSettingsPath) { CurrentSound = VisibleSound };

        // Both tabs share the edits of a sound: the same patch shown in both has one edited copy and one history.
        var sessions = new EditSessions();
        gainOf ??= Live.Mixer.GainAsync;
        Edits = new PatchEditorViewModel(sessions, gainOf);
        Edits.PropertyChanged += OnEditsChanged;
        Live.NotePlayed += OnNotePlayed;
        Live.StatusChanged += (_, line) => Status = line;
        Live.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(LivePlayer.SoundingNotes))
            {
                OnPropertyChanged(nameof(SoundingNotes));
            }
            else if (args.PropertyName == nameof(LivePlayer.CanPlay))
            {
                OnPropertyChanged(nameof(CanPlay));
            }
        };
        Notes = new NotePlayer(Live.Device);
        Library = new LibraryViewModel(Live, s => Status = s, p => Progress = p, ShowInSearchAsync, favourites, sessions, gainOf, openLibrary);
        Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(LibraryViewModel.ShownPatch) or nameof(LibraryViewModel.CurrentSound))
            {
                // Its gain is computed while it is shown, also behind the Search tab, so that its first note need not wait.
                Live.Prepare(Library.CurrentSound);
                Live.SoundChanged();
            }
        };
        if (Live.NewWarning() is { } warning)
        {
            _status = warning;
        }

        Library.Favourites.CollectionChanged += (_, _) => OnFavouriteChanged();
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => Targets.Count > 0 && !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        RemoveCommand = new RelayCommand(RemoveSelected, () => SelectedTarget is not null && !IsRunning);
        ClearCommand = new RelayCommand(() => { Targets.Clear(); RaiseCanExecute(); }, () => Targets.Count > 0 && !IsRunning);
        StartFromScratchCommand = new RelayCommand(StartFromScratch, () => CanStartFromScratch);
        Targets.CollectionChanged += (_, _) => RaiseCanExecute();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Plays target files as they are.</summary>
    internal NotePlayer Notes { get; }

    /// <summary>Plays the live notes of both tabs, from the keyboards and the MIDI device, and holds the MIDI in selector's state.</summary>
    public LivePlayer Live { get; }

    /// <summary>The notes that sound now, lit on the keyboard.</summary>
    public IReadOnlyCollection<int> SoundingNotes => Live.SoundingNotes;

    private int _selectedTab;

    /// <summary>The Library tab.</summary>
    public LibraryViewModel Library { get; }

    /// <summary>The editing of the patch the Search tab shows: what its diagram, code and keyboard get, undo and redo.</summary>
    public PatchEditorViewModel Edits { get; }

    /// <summary>The editing of the visible tab, which Undo and Redo on the keyboard act on.</summary>
    public PatchEditorViewModel VisibleEdits => SelectedTab == 1 ? Library.Edits : Edits;

    /// <summary>Selected tab: 0 Search, 1 Library. Showing the Library tab loads its library the first time.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (Set(ref _selectedTab, value) && value == 1)
            {
                _ = Library.EnsureLoadedAsync();
            }
        }
    }

    /// <summary>Shows a library sound as the patch of the Search tab: code, diagram and keyboard; the next search replaces it.</summary>
    /// <param name="patch">The sound.</param>
    public void ShowLibraryPatch(LibraryPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        SetResult(null);
        _libraryPatch = patch;
        Edits.Show(patch.Patch, patch.Profile, HearSay.OperatorMatrix.Priors.StructureLibrary.ReferenceRequest, patch.Origin ?? string.Join(" ", patch.HeaderComments), patch.Gain);
        OnPropertyChanged(nameof(ShownPatch));
        OnPropertyChanged(nameof(ShownPatchSharesNoise));
        ShowDetails();
        OnPropertyChanged(nameof(HasPatch));
        SelectedTab = 0;
        Status = IsFromScratch(patch)
            ? "New sound made from scratch; edit it in the block diagram, and ☆ to keep it. The next search replaces it."
            : $"Showing {patch.Title} from the library. The next search replaces it.";
    }

    /// <summary>
    /// Shows a sound of the Library tab here (its Show in Search), once <see cref="ConfirmReplaceAsync"/> lets it replace
    /// the sound shown. When that asks, the Search tab is shown first, so that the question is about the sound in view and
    /// its ☆ is at hand after Cancel.
    /// </summary>
    /// <param name="patch">The sound.</param>
    public async Task ShowInSearchAsync(LibraryPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (ShowsUnsavedScratchEdit)
        {
            SelectedTab = 0;
        }

        if (await ConfirmReplaceAsync())
        {
            ShowLibraryPatch(patch);
        }
    }

    /// <summary>
    /// True while the sound shown here was made from scratch, has an edit, and the version shown is not a favourite: the
    /// new sound would be lost if another one replaced it, since the Start from scratch button only shows in an empty tab.
    /// </summary>
    public bool ShowsUnsavedScratchEdit => _libraryPatch is { } patch && IsFromScratch(patch) && Edits.IsEdited && !IsShownFavourite;

    /// <summary>
    /// Asks the user whether the sound shown may be replaced; set by the window, which shows the question. Its task gives
    /// true for Replace and false for Cancel. Null replaces without asking.
    /// </summary>
    public Func<Task<bool>>? AskToReplace { get; set; }

    /// <summary>
    /// Whether an action may replace the sound shown here: true at once when nothing needs asking, else the answer of
    /// <see cref="AskToReplace"/> while <see cref="ShowsUnsavedScratchEdit"/>. Every action that replaces the shown sound
    /// asks this before it changes anything.
    /// </summary>
    public async Task<bool> ConfirmReplaceAsync() => !ShowsUnsavedScratchEdit || AskToReplace is not { } ask || await ask();

    /// <summary>
    /// True while the Search tab shows nothing (no result, no library sound) and no search runs: the empty block diagram
    /// then offers to start a new sound from scratch.
    /// </summary>
    public bool CanStartFromScratch => ShownPatch is null && !IsRunning;

    /// <summary>Starts a new sound from scratch; enabled while <see cref="CanStartFromScratch"/>.</summary>
    public RelayCommand StartFromScratchCommand { get; }

    /// <summary>
    /// Shows a new sound, <see cref="ScratchSound"/>, on the profile of the Target selector, as a library sound is shown
    /// here: edited like any other, with the edits made to it before, and replaced by the next search. Nothing happens
    /// while a sound is shown.
    /// </summary>
    public void StartFromScratch()
    {
        if (CanStartFromScratch)
        {
            ShowLibraryPatch(new LibraryPatch(ScratchSound.Title, ScratchSound.Patch, SelectedProfile, ScratchSound.HeaderComments) { Origin = ScratchSound.Origin });
        }
    }

    /// <summary>True for a sound made from scratch, also when it comes back from the Library tab, edited or not.</summary>
    private static bool IsFromScratch(LibraryPatch patch) => patch.Origin == ScratchSound.Origin;

    /// <summary>
    /// Shows the Library tab with the selected finalist placed among its neighbours, as an imported sound: the version the
    /// Original ⇄ Edited switch shows, an edit at the gain it plays at (the original's, or the matched one).
    /// </summary>
    public async Task FindInLibraryAsync()
    {
        if (_result is not { } result || SelectedAlternative is not { } finalist || Edits.DisplayedPatch is not { } patch)
        {
            return;
        }

        bool edited = Edits.ShowsEdit;
        float? gain = edited ? await Edits.FavouriteGainAsync() : null;
        SelectedTab = 1;
        string name = (finalist.Rank == 1 ? "search best" : $"search {finalist.Label}") + (edited ? " edited" : string.Empty);
        string origin = edited ? "edited from " + finalist.Candidate.Origin : finalist.Candidate.Origin;
        await Library.FindAsync(name, origin, patch, result.Profile, gain);
    }

    /// <summary>The nickname typed in the favourite box.</summary>
    public string FavouriteNickname
    {
        get => _favouriteNickname;
        set => Set(ref _favouriteNickname, value ?? string.Empty);
    }

    /// <summary>True when the shown patch is a favourite.</summary>
    public bool IsShownFavourite => ShownSound() is { } shown && Library.FindFavourite(shown.Patch, shown.Profile) is not null;

    /// <summary>The star of the favourite button: filled for a favourite.</summary>
    public string FavouriteStar => IsShownFavourite ? "★" : "☆";

    /// <summary>Fills the favourite box: the nickname of the shown patch when it is a favourite, else a suggestion.</summary>
    public void PrepareFavouriteName()
    {
        if (ShownSound() is { } shown)
        {
            FavouriteNickname = Library.FindFavourite(shown.Patch, shown.Profile)?.Name ?? shown.Suggestion;
        }
    }

    /// <summary>
    /// Keeps the shown version as a favourite with the typed nickname, or renames it when it is one already. An edit that
    /// is not a favourite yet becomes a new one, <c>edited from …</c>, at the gain it plays at; the original's own
    /// favourite is left as it is.
    /// </summary>
    public async Task SaveFavouriteAsync()
    {
        if (ShownSound() is { } shown)
        {
            float? gain = await Edits.FavouriteGainAsync();
            await Library.SaveFavouriteAsync(FavouriteNickname, shown.Patch, shown.Profile, shown.Origin, gain);
        }
    }

    /// <summary>The favourite the edit shown here was made from, which it can update; null when no edit of a favourite is shown.</summary>
    public FavouriteItem? EditedFavourite() => Library.EditedFavourite(Edits, ShownSound()?.Profile);

    /// <summary>
    /// Puts the edit shown here in the place of the favourite it was made from; the edit stays shown, and now is that
    /// favourite.
    /// </summary>
    public async Task UpdateEditedFavouriteAsync()
    {
        if (ShownSound() is { } shown)
        {
            await Library.UpdateEditedFavouriteAsync(Edits, shown.Profile, showUpdated: false);
        }
    }

    /// <summary>Removes the shown patch from the favourites.</summary>
    public void RemoveShownFavourite()
    {
        if (ShownSound() is { } shown && Library.FindFavourite(shown.Patch, shown.Profile) is { } item)
        {
            Library.RemoveFavourite(item);
        }
    }

    /// <summary>
    /// The version shown in the Search tab (the original or the edit), with a suggested nickname and where it came from:
    /// the selected finalist (named after the first target, for example <c>piano-c4 best</c>), or a library sound shown
    /// here; an edit's origin reads <c>edited from …</c>.
    /// </summary>
    private (OperatorMatrixPatch Patch, SynthesisProfile Profile, string Suggestion, string Origin)? ShownSound()
    {
        if (Edits.DisplayedPatch is not { } patch)
        {
            return null;
        }

        string edited = Edits.ShowsEdit ? " edited" : string.Empty;
        if (_libraryPatch is { } library)
        {
            return (patch, library.Profile, library.Title + edited, Edits.FavouriteOrigin);
        }

        if (_result is { } result && SelectedAlternative is { } finalist)
        {
            string target = Targets.Count > 0 ? System.IO.Path.GetFileNameWithoutExtension(Targets[0].Path) : "search";
            string suggestion = $"{target} {(finalist.Rank == 1 ? "best" : finalist.Label)}{edited}";
            return (patch, result.Profile, suggestion, Edits.FavouriteOrigin);
        }

        return null;
    }

    /// <summary>Where a finalist came from, as its favourite says: the targets, the seed and the finalist's own origin.</summary>
    private string FinalistOrigin(PatchMatchResult result, PatchCandidateResult candidate)
    {
        string targets = string.Join(", ", Targets.Select(static t => t.Name));
        return string.Create(CultureInfo.InvariantCulture, $"From the search for {targets}, seed {result.SearchSummary.RandomSeed}: {candidate.Origin}");
    }

    private void OnFavouriteChanged()
    {
        OnPropertyChanged(nameof(IsShownFavourite));
        OnPropertyChanged(nameof(FavouriteStar));
    }

    /// <summary>Loaded target notes.</summary>
    public ObservableCollection<TargetItem> Targets { get; } = [];

    /// <summary>Starts the search; enabled once a target is loaded.</summary>
    public RelayCommand RunCommand { get; }

    /// <summary>Stops the running search and keeps the best patch found so far.</summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>Removes the selected target.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Removes all targets.</summary>
    public RelayCommand ClearCommand { get; }

    /// <summary>Target selected in the list.</summary>
    public TargetItem? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (Set(ref _selectedTarget, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Candidate evaluations per search; one evaluation renders every target note.</summary>
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

    /// <summary>
    /// Whether the structure library adds its nearest neighbours as hypotheses. Off, a search spends its whole budget
    /// on the structures read from the target, as before the library existed.
    /// </summary>
    public bool UseStructureLibrary
    {
        get => _useStructureLibrary;
        set => Set(ref _useStructureLibrary, value);
    }

    /// <summary>Synthesis targets that can be searched and exported: the SuperCollider FM7, FMX6 and Nome6 UGens.</summary>
    public IReadOnlyList<SynthesisProfile> Profiles { get; } = [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6];

    /// <summary>Choices of the Target selector: each profile alone, and all of them one after the other.</summary>
    public IReadOnlyList<SynthesisTargetChoice> SynthesisTargets { get; } =
    [
        new("FM7", [SynthesisProfiles.SuperColliderFm7]),
        new("FMX6", [SynthesisProfiles.SuperColliderFmx6]),
        new("Nome6", [SynthesisProfiles.SuperColliderNome6]),
        new("All", [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6]),
    ];

    /// <summary>
    /// Target of the next search: FM7 (sine operators only), FMX6 (band-limited waveforms and noise as well), Nome6
    /// (Jacobi elliptic operators), or all three, searched one after the other with the full budget each. Selecting a
    /// target shows its kept result. A change that would replace an unsaved scratch edit is asked about first
    /// (<see cref="ConfirmReplaceAsync"/>); on Cancel the selector goes back to the target it had.
    /// </summary>
    public SynthesisTargetChoice SelectedSynthesisTarget
    {
        get => _selectedSynthesisTarget ?? SynthesisTargets[0];
        set
        {
            SynthesisTargetChoice choice = value ?? SynthesisTargets[0];
            if (!ReferenceEquals(choice, SelectedSynthesisTarget) && !IsRunning && ShowsUnsavedScratchEdit)
            {
                _ = ChangeTargetAsync(choice);
                return;
            }

            ApplyTarget(choice);
        }
    }

    private void ApplyTarget(SynthesisTargetChoice choice)
    {
        // Setting the target it has already (before any choice, the first one) changes nothing.
        if (ReferenceEquals(choice, SelectedSynthesisTarget))
        {
            _selectedSynthesisTarget = choice;
            return;
        }

        if (Set(ref _selectedSynthesisTarget, choice, nameof(SelectedSynthesisTarget)))
        {
            OnPropertyChanged(nameof(ScdWatermark));
            if (!IsRunning)
            {
                ShowKeptResult();
            }
        }
    }

    /// <summary>Changes the target once the user lets it replace the sound shown; else puts the selector back.</summary>
    private async Task ChangeTargetAsync(SynthesisTargetChoice choice)
    {
        if (await ConfirmReplaceAsync())
        {
            ApplyTarget(choice);
        }
        else
        {
            // The selector still shows the choice it made: it is told that the target is the one it was.
            OnPropertyChanged(nameof(SelectedSynthesisTarget));
        }
    }

    /// <summary>Profile of the next single search: the first of the selected target.</summary>
    public SynthesisProfile SelectedProfile => SelectedSynthesisTarget.Profiles[0];

    /// <summary>Which targets have a result to switch to, for the result header; empty with none.</summary>
    public string ResultsKept => _results.Count == 0 ? string.Empty : "results kept: " + string.Join(", ", Profiles.Where(p => _results.ContainsKey(p.Name)).Select(p => ProfileNameConverter.Short(p.Name)));

    /// <summary>What the code box says before there is code: which plugin the export of the selected target needs.</summary>
    public string ScdWatermark =>
        "The generated SynthDef appears here. It needs SuperCollider with "
        + string.Join(" or ", SelectedSynthesisTarget.Profiles.Select(static p => p.Name switch
        {
            "supercollider-fmx6" => "the FMX6 plugin (github.com/shimpe/fmx6)",
            "supercollider-nome6" => "the Nome6 plugin",
            _ => "sc3-plugins (FM7)",
        }))
        + " and nothing of HearSay.";

    /// <summary>Shows the kept result of the selected target: with all targets selected, the best of them.</summary>
    private void ShowKeptResult()
    {
        var kept = SelectedSynthesisTarget.Profiles
            .Select(p => _results.GetValueOrDefault(p.Name))
            .Where(static r => r is not null)
            .OrderBy(static r => r!.AlternativePatches[0].Loss)
            .ToList();
        SetResult(kept.Count > 0 ? kept[0] : null);
        if (kept.Count > 0)
        {
            Status = "Showing " + string.Join("; ", kept.Select(static r => FormattableString.Invariant($"{ProfileNameConverter.Short(r!.Profile.Name)}: audio loss {r.AlternativePatches[0].AudioLoss:F4}, similarity {r.Comparison.Scores.Similarity:F3}")))
                + (kept.Count > 1 ? $". The {ProfileNameConverter.Short(kept[0]!.Profile.Name)} result is shown; select one target to see another." : ".");
        }
        else if (_results.Count > 0)
        {
            Status = $"No {SelectedSynthesisTarget.Label} result yet; run a search to compare it with {string.Join(" and ", _results.Keys.Select(ProfileNameConverter.Short))}.";
        }
    }

    /// <summary>True while a search runs.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                RaiseCanExecute();
                OnPropertyChanged(nameof(ShowsProgress));
            }
        }
    }

    /// <summary>Share of the budget spent, or of a library built, 0..1.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                OnPropertyChanged(nameof(ShowsProgress));
            }
        }
    }

    /// <summary>The progress bar is shown while a search runs or a library is built, and hidden when it is done.</summary>
    public bool ShowsProgress => IsRunning || (Progress > 0 && Progress < 1);

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

    /// <summary>
    /// The patch that is shown, drawn as a block diagram: the selected finalist or the library sound shown here, as the
    /// Original ⇄ Edited switch has it.
    /// </summary>
    public OperatorMatrixPatch? ShownPatch => Edits.DisplayedPatch;

    /// <summary>Whether the profile of the shown patch has a shared noise source, which the block diagram then draws.</summary>
    public bool ShownPatchSharesNoise => (_libraryPatch?.Profile ?? _result?.Profile)?.Capabilities.SupportsSharedNoise == true;

    /// <summary>
    /// The generated SuperCollider code of the version shown, with the gain it plays at once that is known: until then
    /// without it.
    /// </summary>
    public string Scd
    {
        get => _scd;
        private set => Set(ref _scd, value);
    }

    /// <summary>What Copy puts on the clipboard: <see cref="Scd"/> once the gain of the version shown is known.</summary>
    public async Task<string> CodeToCopyAsync()
    {
        // The gain of the version shown when it arrives: a sound shown meanwhile is the one copied, with its gain.
        await Edits.DisplayedGainAsync();
        ShowDetails();
        return Scd;
    }

    /// <summary>Headline numbers of the result, empty without one.</summary>
    public ObservableCollection<StatTile> Stats { get; } = [];

    /// <summary>Similarity per target note.</summary>
    public ObservableCollection<NoteRow> NoteRows { get; } = [];

    /// <summary>The heading of <see cref="NoteRows"/>; while an edit is shown it says they are the original's.</summary>
    public string NoteRowsHeading => _statsShowEdit ? "per-note similarity (original)" : "per-note similarity";

    /// <summary>The label of the save button: the result folder, or while an edit is shown, the edited sound alone.</summary>
    public string SaveLabel => Edits.ShowsEdit ? "Save edited sound…" : "Save result folder…";

    /// <summary>The 6x6 modulation matrix of the best patch, row-major, destination rows and source columns.</summary>
    public ObservableCollection<MatrixCell> MatrixCells { get; } = [];

    /// <summary>Column headers of the matrix.</summary>
    public ObservableCollection<string> OperatorNames { get; } = [];

    /// <summary>The finalists of the search, best first; with several seeds, the finalists of every seed.</summary>
    public ObservableCollection<AlternativeItem> Alternatives { get; } = [];

    /// <summary>The finalist shown in the matrix, the summary, the code and on the keyboard.</summary>
    public AlternativeItem? SelectedAlternative
    {
        get => _selectedAlternative;
        set
        {
            if (Set(ref _selectedAlternative, value) && value is not null && _result is not null)
            {
                ShowCandidate(_result, value.Candidate);
            }
        }
    }

    /// <summary>True once a result exists.</summary>
    public bool HasResult => _result is not null;

    /// <summary>True when a patch is shown: a search result or a sound from the library.</summary>
    public bool HasPatch => _result is not null || _libraryPatch is not null;

    /// <summary>True when the search produced more than one finalist.</summary>
    public bool HasAlternatives => Alternatives.Count > 1;

    /// <summary>
    /// Loads and analyses a WAV file and adds it to the targets. Another target set empties the tab, so an unsaved scratch
    /// edit is asked about first (<see cref="ConfirmReplaceAsync"/>).
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <returns>False when the user chose to keep the sound shown, and nothing was added.</returns>
    public async Task<bool> AddAsync(string path)
    {
        if (!await ConfirmReplaceAsync())
        {
            return false;
        }

        Status = "Analysing " + Path.GetFileName(path) + "…";
        try
        {
            TargetAnalysis analysis = await Task.Run(() => new TargetAnalyzer().Analyze(AudioTarget.FromWaveFile(path)));
            var item = new TargetItem(path, analysis);
            Targets.Add(item);
            SelectedTarget = item;
            Status = analysis.IsUsable ? $"{Targets.Count} target(s). Press Run." : "This file cannot be matched: " + string.Join(", ", analysis.Issues);
            // Another target set makes every kept result stale.
            _results.Clear();
            _resultInputs.Clear();
            OnPropertyChanged(nameof(ResultsKept));
            SetResult(null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            Status = "Could not load the file: " + e.Message;
        }

        return true;
    }

    /// <summary>Re-analyses a target after its pitch was typed, so that the summary shows what the search will use.</summary>
    /// <param name="item">Target to refresh.</param>
    public async Task RefreshAsync(TargetItem item)
    {
        try
        {
            AudioTarget target = item.ToTarget();
            TargetAnalysis analysis = await Task.Run(() => new TargetAnalyzer().Analyze(target));
            item.Update(analysis);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            Status = "Could not analyse the file: " + e.Message;
        }
    }

    /// <summary>
    /// Everything a search result depends on besides the profile, as one string: every target file (path, size and
    /// modification time, so that a file changed on disk counts) with its typed pitch and velocity, the budget, the
    /// seed, the number of seeds and whether the structure library is used.
    /// </summary>
    private string SearchInputs()
    {
        var text = new System.Text.StringBuilder();
        foreach (TargetItem t in Targets)
        {
            var file = new FileInfo(t.Path);
            text.Append(CultureInfo.InvariantCulture, $"{t.Path}|{(file.Exists ? file.Length : -1)}|{(file.Exists ? file.LastWriteTimeUtc.Ticks : 0)}|{t.Pitch}|{t.Velocity};");
        }

        text.Append(CultureInfo.InvariantCulture, $"budget {Budget}, seed {Seed}, seeds {Seeds}, library {UseStructureLibrary}");
        return text.ToString();
    }

    /// <summary>
    /// Runs the search with the current options. Its result replaces the sound shown, so an unsaved scratch edit is asked
    /// about first (<see cref="ConfirmReplaceAsync"/>); on Cancel the search does not start.
    /// </summary>
    public async Task RunAsync()
    {
        if (Targets.Count == 0 || IsRunning)
        {
            return;
        }

        if (!await ConfirmReplaceAsync() || Targets.Count == 0 || IsRunning)
        {
            return;
        }

        IsRunning = true;
        Progress = 0;
        SetResult(null);
        _cancellation = new CancellationTokenSource();
        IReadOnlyList<SynthesisProfile> profiles = SelectedSynthesisTarget.Profiles;
        try
        {
            List<AudioTarget> targets = [.. Targets.Select(static t => t.ToTarget())];
            int seeds = Seeds;
            string inputs = SearchInputs();
            CancellationToken token = _cancellation.Token;
            var done = new List<PatchMatchResult>();
            int searched = 0;
            for (int i = 0; i < profiles.Count && !token.IsCancellationRequested; i++)
            {
                SynthesisProfile profile = profiles[i];
                int index = i;
                string prefix = profiles.Count > 1 ? ProfileNameConverter.Short(profile.Name) + " · " : string.Empty;

                // The search is reproducible: the same inputs would give the same result again, so the kept one is shown.
                if (_results.TryGetValue(profile.Name, out PatchMatchResult? kept) && _resultInputs.GetValueOrDefault(profile.Name) == inputs)
                {
                    done.Add(kept);
                    SetResult(kept);
                    Progress = (index + 1.0) / profiles.Count;
                    Status = prefix + "kept from the earlier search with the same targets, budget, seed and library";
                    continue;
                }

                var options = new PatchMatchOptions { EvaluationBudget = Budget, RandomSeed = Seed, SynthesisProfile = profile, StructureLibrarySize = UseStructureLibrary ? new PatchMatchOptions().StructureLibrarySize : 0 };
                var progress = new Progress<SearchProgress>(p =>
                {
                    // While a structure library is built, the bar shows the build, then the search.
                    Progress = (index + (p.Preparation ?? p.Fraction)) / profiles.Count;
                    string run = p.RunCount > 1 ? FormattableString.Invariant($"seed {p.Run} of {p.RunCount} (seed {Seed + p.Run - 1}) · ") : string.Empty;
                    string previous = double.IsNaN(p.BestLossOfPreviousRuns) ? string.Empty : FormattableString.Invariant($" · best of earlier seeds {p.BestLossOfPreviousRuns:F4}");
                    // Before the search proper (building a structure library) there is no loss yet.
                    Status = double.IsNaN(p.BestLoss)
                        ? prefix + run + p.Stage
                        : FormattableString.Invariant($"{prefix}{run}{p.Stage}: {p.EvaluationsUsed} of {p.EvaluationBudget} evaluations, best loss {p.BestLoss:F4}{previous}");
                });
                PatchMatchResult result = await Task.Run(() => _matcher.MatchBestOf(targets, seeds, options, progress, token), CancellationToken.None);
                searched++;
                _results[result.Profile.Name] = result;
                if (token.IsCancellationRequested)
                {
                    // Stopped early: kept for listening, but not as the result of these inputs.
                    _resultInputs.Remove(result.Profile.Name);
                }
                else
                {
                    _resultInputs[result.Profile.Name] = inputs;
                }

                done.Add(result);
                OnPropertyChanged(nameof(ResultsKept));
                SetResult(result);
            }

            Progress = 1;
            if (done.Count > 1)
            {
                // All targets: the best one is shown, the status line has every one.
                ShowKeptResult();
                string kept = searched < done.Count ? $" ({done.Count - searched} kept from the earlier search)" : string.Empty;
                Status = (token.IsCancellationRequested ? "Stopped" : "Done") + kept + ". " + Status;
            }
            else
            {
                Status = token.IsCancellationRequested ? "Stopped; showing the best patch found so far." : searched == 0 ? "Nothing changed since the last search of this target; its result is shown." : "Done.";
            }
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

    /// <summary>MIDI notes of the target notes, marked on the keyboard.</summary>
    public ObservableCollection<int> TargetNotes { get; } = [];

    /// <summary>What the keyboard last played.</summary>
    public string PlayingText
    {
        get => _playingText;
        private set => Set(ref _playingText, value);
    }

    /// <summary>True when the live notes can be heard: an audio device could be opened.</summary>
    public bool CanPlay => Live.CanPlay;

    /// <summary>Starts a note of the shown patch, held until <see cref="NoteOff"/>. Nothing happens without a patch.</summary>
    /// <param name="midiNote">MIDI note.</param>
    /// <param name="velocity">Key velocity 0..1.</param>
    public void NoteOn(int midiNote, double velocity) => Live.NoteOn(midiNote, velocity, _keyboard);

    /// <summary>Releases a note started by <see cref="NoteOn"/>.</summary>
    /// <param name="midiNote">MIDI note.</param>
    public void NoteOff(int midiNote) => Live.NoteOff(midiNote, _keyboard);

    /// <summary>Stops the live notes and closes the MIDI and audio devices.</summary>
    public void Dispose() => Live.Dispose();

    /// <summary>
    /// The sound the Search tab plays: the shown finalist, at the gain of the searched note, or a library sound shown
    /// here, at the gain of the library's reference note; an edit of either at the original's gain or its matched gain;
    /// null when nothing is shown.
    /// </summary>
    private Sound? SearchSound() => Edits.DisplayedSound;

    /// <summary>The sound of the visible tab, which a new note plays.</summary>
    private Sound? VisibleSound() => SelectedTab == 1 ? Library.CurrentSound : SearchSound();

    /// <summary>A note-on from a keyboard or the MIDI device: the caption of the visible tab says what it played.</summary>
    private void OnNotePlayed(object? sender, PlayedNote played)
    {
        if (played.Sound is not { } sound)
        {
            Status = SelectedTab == 1
                ? "No sound to play yet: click a node of the library, and the keys play it."
                : "No sound to play yet: run a search, or show a library sound here, and the keys play it.";
            return;
        }

        if (!played.Played)
        {
            Status = LivePlayer.CannotPlay(sound);
            return;
        }

        double hz = 440.0 * Math.Pow(2.0, (played.Note - 69) / 12.0);
        string text = FormattableString.Invariant($"{PianoKeyboard.NoteName(played.Note)} ({hz:F1} Hz), velocity {played.Velocity:F2}")
            + (sound.RespondsToVelocity ? string.Empty : " (as volume: this patch has no velocity sensitivity)");
        if (SelectedTab == 1)
        {
            Library.ShowPlaying(text);
        }
        else
        {
            PlayingText = text;
        }

        if (Live.NewWarning() is { } warning)
        {
            Status = warning;
        }
    }

    /// <summary>Plays a target file as it is.</summary>
    /// <param name="item">Target to play.</param>
    public void PlayTarget(TargetItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            HearSay.Audio.AudioNote note = HearSay.Audio.WavFile.Read(item.Path);
            Notes.Play(Level(new RenderedAudio(note.Samples.ToArray(), note.SampleRate)));
            PlayingText = "target " + item.Name;
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            Status = "Could not play the file: " + e.Message;
        }
    }

    /// <summary>Stops playback.</summary>
    public void StopPlayback() => Notes.Stop();

    private static RenderedAudio Level(RenderedAudio audio)
    {
        float peak = audio.Peak;
        if (peak <= 0)
        {
            return audio;
        }

        float gain = PlaybackLevel.GainFor(peak);
        var samples = new float[audio.Samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = audio.Samples[i] * gain;
        }

        return new RenderedAudio(samples, audio.SampleRate);
    }

    /// <summary>Stops the running search.</summary>
    public void Cancel()
    {
        _cancellation?.Cancel();
        Status = "Stopping after the current step…";
    }

    /// <summary>
    /// Writes the result folder; while an edit is shown, the edited sound alone, as a folder that imports again, with
    /// <see cref="PatchEditorViewModel.EditedHeaderLine"/> in its code.
    /// </summary>
    /// <param name="directory">Destination folder.</param>
    public async Task SaveAsync(string directory)
    {
        // The gain the shown version plays at, which its code carries, waited for while it is being computed. Everything
        // else is read after it: a sound shown meanwhile is the one saved, with its own gain.
        float? gain = await Edits.DisplayedGainAsync();
        if (_libraryPatch is { } library)
        {
            OperatorMatrixPatch patch = Edits.DisplayedPatch ?? library.Patch;
            IReadOnlyList<string> header = Edits.ShowsEdit ? PatchEditorViewModel.WithEditedLine(library.HeaderComments) : library.HeaderComments;
            IReadOnlyList<string> written = await Task.Run(() => LibrarySoundWriter.Write(patch, library.Profile, directory, header, gain));
            Status = $"{written.Count} files written to {directory}.";
            return;
        }

        if (_result is null)
        {
            return;
        }

        if (Edits.ShowsEdit && Edits.DisplayedPatch is { } edited && _candidate is { } candidate)
        {
            PatchMatchResult shown = _result;
            IReadOnlyList<string> header = PatchEditorViewModel.WithEditedLine(FinalistHeader(shown, candidate));
            IReadOnlyList<string> written = await Task.Run(() => LibrarySoundWriter.Write(edited, shown.Profile, directory, header, gain));
            Status = $"{written.Count} files of the edited sound written to {directory}.";
            return;
        }

        PatchMatchResult result = _result;
        IReadOnlyList<string> files = await Task.Run(() => PatchMatchResultWriter.Write(result, directory));
        Status = $"{files.Count} files written to {directory}.";
    }

    private void RemoveSelected()
    {
        if (SelectedTarget is { } item)
        {
            Targets.Remove(item);
            SelectedTarget = Targets.Count > 0 ? Targets[^1] : null;
        }
    }

    private void RaiseCanExecute()
    {
        RunCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        ClearCommand.RaiseCanExecuteChanged();
    }

    private void SetResult(PatchMatchResult? result)
    {
        _libraryPatch = null;
        _result = result;
        _selectedAlternative = null;
        _candidate = null;
        Edits.Show(null, null, null, string.Empty);
        OnPropertyChanged(nameof(ShownPatch));
        OnPropertyChanged(nameof(ShownPatchSharesNoise));
        OnFavouriteChanged();
        Alternatives.Clear();
        if (result is null)
        {
            Stats.Clear();
            NoteRows.Clear();
            MatrixCells.Clear();
            OperatorNames.Clear();
            ResultSummary = string.Empty;
            Scd = string.Empty;
        }
        else
        {
            // The numbers follow the selected finalist (below); the search diagnosed the best one already.
            _noteMatches.Clear();
            _noteMatches[result.AlternativePatches[0]] = result.Notes;
            _statsCandidate = null;

            for (int i = 0; i < result.AlternativePatches.Count; i++)
            {
                Alternatives.Add(new AlternativeItem(i + 1, result.AlternativePatches[i]));
            }

            // Through the property, so that the list selects it and the patch is shown.
            SelectedAlternative = Alternatives[0];
        }

        OnPropertyChanged(nameof(HasAlternatives));
        TargetNotes.Clear();
        if (result is not null)
        {
            foreach (NoteMatch n in result.Notes)
            {
                TargetNotes.Add(n.Request.MidiNote ?? (int)Math.Round(69 + (12 * Math.Log2(n.Analysis.FundamentalHz / 440.0))));
            }
        }

        PlayingText = string.Empty;
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasPatch));
    }

    /// <summary>Shows one finalist (its edit, when it has one): its matrix, routing, code, and what the keyboard plays.</summary>
    private void ShowCandidate(PatchMatchResult result, PatchCandidateResult candidate)
    {
        _candidate = candidate;
        Edits.Show(candidate.Patch, result.Profile, result.Request, FinalistOrigin(result, candidate));
        OnPropertyChanged(nameof(ShownPatch));
        OnPropertyChanged(nameof(ShownPatchSharesNoise));
        ShowDetails();
        PlayingText = string.Empty;
    }

    /// <summary>
    /// The headline numbers of the selected finalist: its audio loss (against the search's initial loss), its similarity
    /// and per-note similarities, and the search's renders. A finalist other than the best is compared with the targets
    /// the first time it is shown, off the interface thread. While an edit is shown they say they are the original's.
    /// </summary>
    private void ShowStats(PatchMatchResult result, PatchCandidateResult candidate, bool edited)
    {
        _statsShowEdit = edited;
        _statsCandidate = candidate;
        OnPropertyChanged(nameof(NoteRowsHeading));
        string of = edited ? " (original)" : string.Empty;
        SearchSummary s = result.SearchSummary;
        double gain = s.InitialAudioLoss / Math.Max(1e-9, candidate.AudioLoss);
        string change = gain >= 1.0 ? FormattableString.Invariant($"{gain:F1}× better") : FormattableString.Invariant($"{1.0 / gain:F1}× worse");
        Stats.Clear();
        NoteRows.Clear();
        Stats.Add(new StatTile("audio loss" + of, FormattableString.Invariant($"{candidate.AudioLoss:F3}"), FormattableString.Invariant($"from {s.InitialAudioLoss:F3}, {change}")));
        if (!_noteMatches.TryGetValue(candidate, out IReadOnlyList<NoteMatch>? notes))
        {
            Stats.Add(new StatTile("similarity" + of, "…", "comparing with the targets"));
            _ = CompareAsync(result, candidate);
        }
        else if (notes.Count == 0)
        {
            Stats.Add(new StatTile("similarity" + of, "–", "could not be rendered"));
        }
        else
        {
            NoteMatch main = notes.FirstOrDefault(n => n.Request == result.Request) ?? notes[0];
            Stats.Add(new StatTile("similarity" + of, FormattableString.Invariant($"{main.Comparison.Scores.Similarity:F2}"), "1 = identical"));
            foreach (NoteMatch n in notes)
            {
                NoteRows.Add(new NoteRow(FormattableString.Invariant($"{n.Analysis.FundamentalHz:F1} Hz · v{n.Request.Velocity:F2}"), n.Comparison.Scores.Similarity));
            }
        }

        Stats.Add(new StatTile("renders", s.RendersUsed.ToString("N0", CultureInfo.InvariantCulture), FormattableString.Invariant($"{s.Elapsed.TotalSeconds:F0} s, seed {s.RandomSeed}")));
    }

    /// <summary>Compares a finalist with the target notes, once, and shows the numbers if it is still the one shown.</summary>
    private async Task CompareAsync(PatchMatchResult result, PatchCandidateResult candidate)
    {
        if (!_comparing.Add(candidate))
        {
            return;
        }

        IReadOnlyList<NoteMatch> notes;
        try
        {
            notes = await Task.Run(() => result.CompareNotes(candidate.Patch));
        }
        catch (Exception e) when (e is NotSupportedException or PatchNotSupportedException)
        {
            notes = [];
        }
        finally
        {
            _comparing.Remove(candidate);
        }

        if (!ReferenceEquals(_result, result))
        {
            return;
        }

        _noteMatches[candidate] = notes;
        if (ReferenceEquals(_candidate, candidate))
        {
            ShowStats(result, candidate, _statsShowEdit);
        }
    }

    /// <summary>
    /// What follows the patch shown, edited or not: the diagram at once; the code, the routing, the matrix, the labels of
    /// the numbers and the star a moment later, once per burst of changes, so that a drag stays smooth.
    /// </summary>
    private void OnEditsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PatchEditorViewModel.DisplayedPatch):
                OnPropertyChanged(nameof(ShownPatch));
                ScheduleDetails();
                break;
            case nameof(PatchEditorViewModel.DisplayedSound):
                Live.SoundChanged();
                break;
            case nameof(PatchEditorViewModel.DisplayedGain):
                // The code carries the gain the shown version plays at, once it is known or matched.
                ScheduleDetails();
                break;
            case nameof(PatchEditorViewModel.ShowsEdit):
                OnPropertyChanged(nameof(SaveLabel));
                break;
            case nameof(PatchEditorViewModel.LastRefusal) when Edits.LastRefusal is { } refusal:
                Status = refusal;
                break;
            case nameof(PatchEditorViewModel.Notice) when Edits.Notice is { } notice:
                Status = notice;
                break;
        }
    }

    private void ScheduleDetails()
    {
        if (_detailsPending)
        {
            return;
        }

        _detailsPending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                _detailsPending = false;
                ShowDetails();
            },
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Shows the code, the routing and the matrix of the version shown, and the result's numbers labelled as the
    /// original's while it is an edit: they are not computed again.
    /// </summary>
    private void ShowDetails()
    {
        if (Edits.DisplayedPatch is not { } patch)
        {
            return;
        }

        bool edited = Edits.ShowsEdit;
        if (_libraryPatch is { } library)
        {
            string from = IsFromScratch(library) ? ScratchSound.Origin : "from the library";
            ResultSummary = $"{from}: {library.Title}{(edited ? ", edited" : string.Empty)}\n\n{Routing(patch)}";
            Scd = Edits.DisplayedCode(library.HeaderComments);
        }
        else if (_result is { } result && _candidate is { } candidate)
        {
            ShowMatrix(patch);
            string of = edited ? " (original)" : string.Empty;
            ResultSummary = FormattableString.Invariant(
                $"""
                audio loss  {candidate.AudioLoss:F4}{of}
                origin      {candidate.Origin}{(edited ? ", edited" : string.Empty)}
                structure   {candidate.Regime.Value}

                {Routing(patch)}
                """);
            Scd = Edits.DisplayedCode(FinalistHeader(result, candidate));
            if (_statsShowEdit != edited || !ReferenceEquals(_statsCandidate, candidate))
            {
                ShowStats(result, candidate, edited);
            }
        }

        OnFavouriteChanged();
    }

    /// <summary>The header of a finalist's code: what it was matched to, and where it came from.</summary>
    private IReadOnlyList<string> FinalistHeader(PatchMatchResult result, PatchCandidateResult candidate)
    {
        bool isBest = ReferenceEquals(candidate, result.AlternativePatches[0]);
        string comparison = isBest ? FormattableString.Invariant($", similarity {result.Comparison.Scores.Similarity:F3}") : string.Empty;
        return
        [
            string.Create(CultureInfo.InvariantCulture, $"Matched to {result.Notes.Count} note(s) of {string.Join(", ", Targets.Select(static t => t.Name))}; audio loss {candidate.AudioLoss:F4}{comparison} at {result.Analysis.FundamentalHz:F2} Hz."),
            string.Create(CultureInfo.InvariantCulture, $"{(isBest ? "Best patch" : "Alternative")}: {candidate.Origin}"),
        ];
    }

    /// <summary>The routing of a patch, without the matrix the Search tab draws.</summary>
    private static string Routing(OperatorMatrixPatch patch)
    {
        string routing = PatchDescription.Routing(patch);
        int matrixAt = routing.IndexOf("modulation matrix", StringComparison.Ordinal);
        return matrixAt > 0 ? routing[..matrixAt].TrimEnd() : routing;
    }

    /// <summary>The modulation matrix of the version shown.</summary>
    private void ShowMatrix(OperatorMatrixPatch patch)
    {
        bool[] audible = patch.FindAudibleOperators();
        OperatorNames.Clear();
        MatrixCells.Clear();
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            OperatorNames.Add("OP" + (i + 1));
        }

        for (int d = 0; d < patch.OperatorCount; d++)
        {
            for (int src = 0; src < patch.OperatorCount; src++)
            {
                double amount = audible[d] && audible[src] ? patch.ModulationMatrix[d, src] : 0.0;
                MatrixCells.Add(new MatrixCell(amount, src == d));
            }
        }
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

    private void OnPropertyChanged(string? name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(ShownPatch) or nameof(IsRunning))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartFromScratch)));
            StartFromScratchCommand?.RaiseCanExecuteChanged();
        }

        if (name is nameof(ShownPatch) or nameof(SelectedTab))
        {
            // Its gain is computed while it is shown, also behind the Library tab, so that its first note need not wait.
            Live.Prepare(SearchSound());
            Live.SoundChanged();
        }
    }
}

/// <summary>Command backed by delegates.</summary>
public sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute();

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>One headline number of a result.</summary>
/// <param name="Label">What the number is.</param>
/// <param name="Value">The number as text.</param>
/// <param name="Detail">A short qualification.</param>
public sealed record StatTile(string Label, string Value, string Detail);

/// <summary>One entry of the Target selector: a label and the profiles searched, one after the other.</summary>
/// <param name="Label">Text in the selector.</param>
/// <param name="Profiles">Profiles searched, in order.</param>
public sealed class SynthesisTargetChoice(string label, IReadOnlyList<SynthesisProfile> profiles)
{
    /// <inheritdoc cref="SynthesisTargetChoice"/>
    public string Label { get; } = label;

    /// <inheritdoc cref="SynthesisTargetChoice"/>
    public IReadOnlyList<SynthesisProfile> Profiles { get; } = profiles;

    public override string ToString() => Label;
}

/// <summary>One finalist of the search, for the alternatives list.</summary>
/// <param name="Rank">1 for the best patch.</param>
/// <param name="Candidate">The finalist.</param>
public sealed record AlternativeItem(int Rank, PatchCandidateResult Candidate)
{
    /// <summary>"#1", "#2", …</summary>
    public string Label => "#" + Rank.ToString(CultureInfo.InvariantCulture);

    /// <summary>Operators in use, for example "OP1 Saw r1 → out · OP2 Sine r3".</summary>
    public string Structure
    {
        get
        {
            OperatorMatrixPatch patch = Candidate.Patch;
            bool[] audible = patch.FindAudibleOperators();
            var parts = new List<string>();
            for (int i = 0; i < patch.OperatorCount; i++)
            {
                if (audible[i])
                {
                    OperatorPatch op = patch.Operators[i];
                    string frequency = op.Frequency.Mode == OperatorFrequencyMode.Ratio
                        ? "r" + op.Frequency.Ratio.ToString("0.###", CultureInfo.InvariantCulture)
                        : op.Frequency.FixedFrequencyHz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";
                    parts.Add($"OP{i + 1} {op.Waveform} {frequency}{(patch.OutputLevels[i] != 0.0 ? " → out" : string.Empty)}");
                }
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Hypothesis and mutations, with the seed when several were run.</summary>
    public string Origin => Candidate.Origin;
}

/// <summary>Similarity of one target note, for a bar.</summary>
/// <param name="Label">Pitch and velocity.</param>
/// <param name="Similarity">0..1.</param>
public sealed record NoteRow(string Label, double Similarity)
{
    /// <summary>Similarity as text.</summary>
    public string Text => Similarity.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>Similarity in percent, for the bar.</summary>
    public double Percent => 100.0 * Similarity;
}

/// <summary>One cell of the modulation matrix.</summary>
/// <param name="Amount">Modulation amount in radians; zero when the edge is absent.</param>
/// <param name="IsDiagonal">True for self-feedback cells.</param>
public sealed record MatrixCell(double Amount, bool IsDiagonal)
{
    /// <summary>Amount as text; empty for absent edges.</summary>
    public string Text => Amount == 0.0 ? string.Empty : Amount.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Colour intensity 0..1, saturating at 6 radians.</summary>
    public double Intensity => Math.Clamp(Math.Abs(Amount) / 6.0, 0.0, 1.0);
}
