using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>One step of the breadcrumb: a family that was opened.</summary>
/// <param name="Label">Text of the crumb.</param>
/// <param name="Family">The family it opens.</param>
public sealed record Crumb(string Label, LibraryFamily Family);

/// <summary>A sound from the library, as it is handed to the Search tab.</summary>
/// <param name="Title">For example <c>FMX6 #50123456</c>, or an import's name.</param>
/// <param name="Patch">Patch.</param>
/// <param name="Profile">Profile it plays on.</param>
/// <param name="Scd">Its SuperCollider code.</param>
/// <param name="HeaderComments">Header lines of the code, for Save.</param>
public sealed record LibraryPatch(string Title, OperatorMatrixPatch Patch, SynthesisProfile Profile, string Scd, IReadOnlyList<string> HeaderComments);

/// <summary>
/// State of the Library tab: the profile whose library is shown, the family opened, its children as graph nodes, the
/// selected sound with its code, and what the keyboard plays.
/// </summary>
public sealed class LibraryViewModel : INotifyPropertyChanged
{
    private readonly NotePlayer _notes;
    private readonly Action<string> _status;
    private readonly Action<double> _progress;
    private readonly Action<LibraryPatch> _showInSearch;
    private readonly int _size = new PatchMatchOptions().StructureLibrarySize;
    private SynthesisProfile _profile = SynthesisProfiles.SuperColliderFmx6;
    private LibraryAtlas? _atlas;
    private LibraryFamily? _family;
    private IReadOnlyList<LibraryFamily> _children = [];
    private int _loadVersion;
    private bool _loaded;
    private IReadOnlyList<GraphNode>? _nodes;
    private IReadOnlyList<GraphEdge>? _edges;
    private int _selectedIndex = -1;
    private string? _graphMessage = "Open this tab to load the library.";
    private Selection? _selection;
    private string _playingText = string.Empty;

    /// <summary>Creates the view model.</summary>
    /// <param name="notes">Shared note player.</param>
    /// <param name="status">Sets the status line of the window.</param>
    /// <param name="progress">Sets the progress bar of the window, 0..1.</param>
    /// <param name="showInSearch">Shows a sound in the Search tab.</param>
    public LibraryViewModel(NotePlayer notes, Action<string> status, Action<double> progress, Action<LibraryPatch> showInSearch)
    {
        _notes = notes;
        _status = status;
        _progress = progress;
        _showInSearch = showInSearch;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Profiles with a library.</summary>
    public IReadOnlyList<SynthesisProfile> Profiles { get; } = [SynthesisProfiles.SuperColliderFm7, SynthesisProfiles.SuperColliderFmx6, SynthesisProfiles.SuperColliderNome6];

    /// <summary>Profile whose library is shown; changing it loads that library.</summary>
    public SynthesisProfile SelectedProfile
    {
        get => _profile;
        set
        {
            if (Set(ref _profile, value) && _loaded)
            {
                _ = LoadAsync();
            }
        }
    }

    /// <summary>The families opened, from all sounds down.</summary>
    public ObservableCollection<Crumb> Crumbs { get; } = [];

    /// <summary>Children of the opened family as graph nodes.</summary>
    public IReadOnlyList<GraphNode>? Nodes
    {
        get => _nodes;
        private set => Set(ref _nodes, value);
    }

    /// <summary>Lines between the nodes.</summary>
    public IReadOnlyList<GraphEdge>? Edges
    {
        get => _edges;
        private set => Set(ref _edges, value);
    }

    /// <summary>Index of the selected node, −1 for none.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        private set => Set(ref _selectedIndex, value);
    }

    /// <summary>Shown instead of the graph while there is none.</summary>
    public string? GraphMessage
    {
        get => _graphMessage;
        private set => Set(ref _graphMessage, value);
    }

    /// <summary>The last clicked sound's patch.</summary>
    public OperatorMatrixPatch? ShownPatch => _selection?.Patch;

    /// <summary>Its SuperCollider code.</summary>
    public string Scd => _selection?.Scd ?? string.Empty;

    /// <summary>One line about it.</summary>
    public string SelectionText => _selection?.Description ?? "Click a node to hear it; double-click a family to open it.";

    /// <summary>True once a sound was clicked.</summary>
    public bool HasSelection => _selection is not null;

    /// <summary>What the keyboard last played.</summary>
    public string PlayingText
    {
        get => _playingText;
        private set => Set(ref _playingText, value);
    }

    /// <summary>True when an audio device could be opened.</summary>
    public bool CanPlay => _notes.CanPlay;

    /// <summary>Loads the library the first time the tab is shown.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return Task.CompletedTask;
        }

        _loaded = true;
        return LoadAsync();
    }

    /// <summary>Selects a node, shows its code and plays it at the reference note.</summary>
    /// <param name="index">Node index.</param>
    public void Select(int index)
    {
        if (_atlas is not { } atlas || index < 0 || index >= _children.Count)
        {
            return;
        }

        LibraryFamily child = _children[index];
        int seed = atlas.Library.Seed(child.Medoid);
        OperatorMatrixPatch patch = atlas.Library.Patch(seed);
        string label = SoundDescriptors.Label(atlas.Library.Fingerprint(child.Medoid), patch, child.Size);
        string[] header =
        [
            string.Create(CultureInfo.InvariantCulture, $"HearSay library {_profile.Name} v{_profile.StructureLibraryVersion}, seed {seed}."),
            label,
        ];
        SelectedIndex = index;
        SetSelection(new Selection($"{ProfileNameConverter.Short(_profile.Name)} #{seed}", patch, _profile, $"#{index + 1} · {label}", header));
    }

    /// <summary>Opens a family node: the graph shows its children.</summary>
    /// <param name="index">Node index.</param>
    public async Task OpenAsync(int index)
    {
        if (index < 0 || index >= _children.Count || _children[index].IsLeaf || _atlas is not { } atlas)
        {
            return;
        }

        LibraryFamily child = _children[index];
        Crumbs.Add(new Crumb(CrumbLabel(atlas, child, index), child));
        await ShowFamilyAsync(child);
    }

    /// <summary>Goes back to a family of the breadcrumb.</summary>
    /// <param name="crumb">Crumb.</param>
    public async Task OpenCrumbAsync(Crumb crumb)
    {
        int at = Crumbs.IndexOf(crumb);
        if (at < 0)
        {
            return;
        }

        while (Crumbs.Count > at + 1)
        {
            Crumbs.RemoveAt(Crumbs.Count - 1);
        }

        await ShowFamilyAsync(crumb.Family);
    }

    /// <summary>Plays the last clicked sound at a note.</summary>
    /// <param name="midiNote">MIDI note.</param>
    /// <param name="velocity">Key velocity 0..1.</param>
    public async Task PlayNoteAsync(int midiNote, double velocity)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        RenderRequest reference = StructureLibrary.ReferenceRequest;
        double hz = 440.0 * Math.Pow(2.0, (midiNote - 69) / 12.0);
        var note = new RenderRequest(hz, midiNote, velocity, reference.NoteDuration, reference.ReleaseDuration, reference.SampleRate);
        if (await _notes.PlayAsync(selection.Patch, selection.Profile, reference, note))
        {
            PlayingText = FormattableString.Invariant($"{PianoKeyboard.NoteName(midiNote)} ({hz:F1} Hz), velocity {velocity:F2}") + (NotePlayer.RespondsToVelocity(selection.Patch) ? string.Empty : " (as volume: this patch has no velocity sensitivity)");
        }
    }

    /// <summary>Writes the selected sound as a folder that imports again.</summary>
    /// <param name="directory">Destination folder.</param>
    public async Task SaveAsync(string directory)
    {
        if (_selection is not { } selection)
        {
            return;
        }

        IReadOnlyList<string> files = await Task.Run(() => LibrarySoundWriter.Write(selection.Patch, selection.Profile, directory, selection.HeaderComments));
        _status($"{files.Count} files written to {directory}.");
    }

    /// <summary>Shows the selected sound in the Search tab.</summary>
    public void ShowInSearch()
    {
        if (_selection is { } selection)
        {
            _showInSearch(new LibraryPatch(selection.Title, selection.Patch, selection.Profile, selection.Scd, selection.HeaderComments));
        }
    }

    private async Task LoadAsync()
    {
        int version = ++_loadVersion;
        SynthesisProfile profile = _profile;
        string name = ProfileNameConverter.Short(profile.Name);
        _atlas = null;
        _family = null;
        _children = [];
        Crumbs.Clear();
        Nodes = null;
        Edges = null;
        SelectedIndex = -1;
        bool onDisk = StructureLibrary.IsAvailable(profile, _size);
        GraphMessage = onDisk
            ? $"Loading the {name} library…"
            : string.Create(CultureInfo.InvariantCulture, $"Building the {name} library ({_size:N0} sounds, once; FMX6 takes about 17 minutes)…");
        var progress = new Progress<double>(f =>
        {
            if (version == _loadVersion)
            {
                _progress(f);
            }
        });
        var stage = new Progress<string>(s =>
        {
            if (version == _loadVersion)
            {
                GraphMessage = s;
            }
        });
        try
        {
            LibraryAtlas atlas = await Task.Run(() =>
            {
                StructureLibrary library = StructureLibrary.For(profile, _size, SynthesisBackends.RendererFor(profile), progress: progress);
                string cache = LibraryAtlas.CacheFileOf(StructureLibrary.FileOf(profile, _size));
                if (!File.Exists(cache))
                {
                    ((IProgress<string>)stage).Report($"Grouping the {name} sounds into families (once, under a minute)…");
                }

                return LibraryAtlas.Open(library, cache);
            });
            if (version != _loadVersion)
            {
                return;
            }

            _atlas = atlas;
            Crumbs.Add(new Crumb("all", atlas.Root));
            await ShowFamilyAsync(atlas.Root);
            _status(string.Create(CultureInfo.InvariantCulture, $"{name} library: {atlas.Library.Count:N0} sounds in {_children.Count} families. Click a node to hear it, double-click a family to open it."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (version == _loadVersion)
            {
                GraphMessage = "The library could not be loaded: " + e.Message;
            }
        }
    }

    private async Task ShowFamilyAsync(LibraryFamily family)
    {
        if (_atlas is not { } atlas)
        {
            return;
        }

        int version = _loadVersion;
        (IReadOnlyList<LibraryFamily> children, List<byte[]> prints, List<OperatorMatrixPatch> patches) = await Task.Run(() =>
        {
            IReadOnlyList<LibraryFamily> c = atlas.Children(family);
            return (c, c.Select(f => atlas.Library.Fingerprint(f.Medoid).ToArray()).ToList(), c.Select(f => atlas.Library.Patch(atlas.Library.Seed(f.Medoid))).ToList());
        });
        if (version != _loadVersion)
        {
            return;
        }

        _family = family;
        _children = children;
        AtlasLayout layout = AtlasLayout.Compute(prints, [.. children.Select(static c => c.Size)]);
        Nodes = [.. children.Select((c, k) => new GraphNode(
            layout.Positions[k].X,
            layout.Positions[k].Y,
            layout.Radii[k],
            Brightness(prints[k]),
            c.IsLeaf ? string.Empty : c.Size.ToString("N0", CultureInfo.InvariantCulture),
            $"#{k + 1} · {SoundDescriptors.Label(prints[k], patches[k], c.Size)}",
            IsImported: false,
            CanOpen: !c.IsLeaf,
            IsMarked: false))];
        Edges = [.. layout.Edges.Select(static e => new GraphEdge(e.From, e.To))];
        SelectedIndex = -1;
        GraphMessage = null;
    }

    private static double Brightness(byte[] print) => Math.Clamp(Math.Log2(SoundDescriptors.BrightnessRatio(print)) / 4.0, 0.0, 1.0);

    private static string CrumbLabel(LibraryAtlas atlas, LibraryFamily family, int index)
    {
        IReadOnlyList<string> words = SoundDescriptors.Of(atlas.Library.Fingerprint(family.Medoid), atlas.Library.Patch(atlas.Library.Seed(family.Medoid)));
        return $"#{index + 1} {string.Join(" · ", words.Take(2))}";
    }

    private void SetSelection(Selection selection)
    {
        _selection = selection;
        OnPropertyChanged(nameof(ShownPatch));
        OnPropertyChanged(nameof(Scd));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        RenderRequest reference = StructureLibrary.ReferenceRequest;
        _ = PlayReferenceAsync(selection, reference);
    }

    private async Task PlayReferenceAsync(Selection selection, RenderRequest reference)
    {
        if (await _notes.PlayAsync(selection.Patch, selection.Profile, reference, reference))
        {
            PlayingText = "A3 (220.0 Hz), the library's reference note";
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

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed record Selection(string Title, OperatorMatrixPatch Patch, SynthesisProfile Profile, string Description, IReadOnlyList<string> HeaderComments)
    {
        public string Scd { get; } = SynthesisBackends.ExporterFor(Profile)?.Export(Patch, new PatchExportOptions { HeaderComments = HeaderComments }) ?? "This profile has no export backend.";
    }
}
