using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using HearSay.OperatorMatrix.Priors;
using HearSay.OperatorMatrix.Search;
using HearSay.Playing;
using HearSay.Synthesis.Export;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>One step of the breadcrumb: a family that was opened.</summary>
/// <param name="Label">Text of the crumb.</param>
/// <param name="Family">The family it opens.</param>
public sealed record Crumb(string Label, LibraryFamily Family);

/// <summary>An imported sound with its fingerprint.</summary>
/// <param name="Sound">The sound.</param>
/// <param name="Print">Its fingerprint, rendered as a library sound is.</param>
public sealed record ImportedItem(ExportedSound Sound, byte[] Print)
{
    /// <summary>Name in the list.</summary>
    public string Name => Sound.Name;

    /// <summary>Profile it plays on, short.</summary>
    public string ProfileName => ProfileNameConverter.Short(Sound.Profile.Name);

    /// <summary>For a finalist of the Search tab, where the search found it; null for a sound read from a folder.</summary>
    public string? SearchOrigin { get; init; }

    /// <summary>Where it came from, said differently from the search or import wording; null to use those.</summary>
    public string? SourceText { get; init; }

    /// <summary>Where it came from, for the selection line and the header of its code.</summary>
    public string Source => SourceText ?? (SearchOrigin is { } origin ? "from the search: " + origin : "imported from " + Sound.PatchFile);

    /// <summary>The same key is the same sound: its file, or for a finalist its profile and patch.</summary>
    public string Key => SearchOrigin is null ? Sound.PatchFile : $"search {Sound.Profile.Name} {Sound.Patch.ComputeHash()}";
}

/// <summary>A favourite sound in the side list of the Library tab.</summary>
/// <param name="Favourite">The favourite as it is kept on disk.</param>
public sealed record FavouriteItem(Favourite Favourite)
{
    /// <summary>Its nickname.</summary>
    public string Name => Favourite.Nickname;

    /// <summary>Profile it plays on, short.</summary>
    public string ProfileName => ProfileNameConverter.Short(Favourite.Sound.Profile.Name);
}

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
    private readonly LivePlayer _live;
    private readonly object _keyboard = new();
    private readonly Action<string> _status;
    private readonly Action<double> _progress;
    private readonly Action<LibraryPatch> _showInSearch;
    private readonly int _size = new PatchMatchOptions().StructureLibrarySize;
    private SynthesisProfile _profile = SynthesisProfiles.SuperColliderFmx6;
    private LibraryAtlas? _atlas;
    private LibraryFamily? _family;
    private IReadOnlyList<LibraryFamily> _children = [];
    private int _loadVersion;
    private int _navVersion;
    private bool _loaded;
    private Task _loading = Task.CompletedTask;
    private IReadOnlyList<GraphNode>? _nodes;
    private IReadOnlyList<GraphEdge>? _edges;
    private int _selectedIndex = -1;
    private string? _graphMessage = "Open this tab to load the library.";
    private Selection? _selection;
    private string _playingText = string.Empty;
    private LibraryAtlas? _fullAtlas;
    private SoundTraits[]? _traits;
    private SoundFilter _filter = SoundFilter.None;
    private int _filterVersion;
    private SoundFilter _shownFilter = SoundFilter.None;
    private string? _filterSummary;
    private readonly FavouriteSounds _favouriteStore;
    private bool _showFavourites = true;
    private FavouriteItem? _selectedFavourite;
    private string _favouriteNickname = string.Empty;

    /// <summary>Creates the view model.</summary>
    /// <param name="live">The live notes, shared with the Search tab.</param>
    /// <param name="status">Sets the status line of the window.</param>
    /// <param name="progress">Sets the progress bar of the window, 0..1.</param>
    /// <param name="showInSearch">Shows a sound in the Search tab.</param>
    /// <param name="favourites">Where the favourites are kept; the user's local application data when null.</param>
    public LibraryViewModel(LivePlayer live, Action<string> status, Action<double> progress, Action<LibraryPatch> showInSearch, FavouriteSounds? favourites = null)
    {
        _favouriteStore = favourites ?? new FavouriteSounds();
        Imports.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSideList));
        Favourites.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSideList));
            OnPropertyChanged(nameof(IsShownFavourite));
            OnPropertyChanged(nameof(FavouriteStar));
        };
        ReloadFavourites();
        _live = live;
        _live.PropertyChanged += (_, args) =>
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
                _loading = LoadAsync();
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

    /// <summary>Whether its profile has a shared noise source, which the block diagram then draws.</summary>
    public bool ShownPatchSharesNoise => _selection?.Profile.Capabilities.SupportsSharedNoise == true;

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
    public bool CanPlay => _live.CanPlay;

    /// <summary>Rows of the filter panel; empty until the library is read, and for a library nothing can filter.</summary>
    public ObservableCollection<TraitRow> FilterRows { get; } = [];

    /// <summary>The summary line under the breadcrumb while a filter is set.</summary>
    public string? FilterSummary
    {
        get => _filterSummary;
        private set => Set(ref _filterSummary, value);
    }

    /// <summary>True while a filter is set.</summary>
    public bool HasFilter => !_filter.IsEmpty;

    /// <summary>Releases every chip and shows all sounds again.</summary>
    public void ClearFilter()
    {
        foreach (TraitChip chip in FilterRows.SelectMany(static r => r.Chips))
        {
            chip.Release();
        }

        SetFilter(SoundFilter.None);
    }

    private void OnChipToggled(TraitChip chip) => SetFilter(chip.IsChosen ? _filter.With(chip.Value) : _filter.Without(chip.Value));

    private void SetFilter(SoundFilter filter)
    {
        _filter = filter;
        OnPropertyChanged(nameof(HasFilter));
        FilterSummary = filter.IsEmpty ? null : $"filter: {filter.Describe()} — counting…";
        int version = ++_filterVersion;
        if (filter.IsEmpty && !_shownFilter.IsEmpty && _fullAtlas is { } full)
        {
            // The unfiltered tree is at hand: show it at once; only the chip counts wait.
            _ = ShowAtlasAsync(full, SoundFilter.None);
        }

        _ = ApplyFilterAsync(version);
    }

    /// <summary>
    /// After 0.4 s without a further change: counts the chips, builds the tree of the matching sounds and shows its top
    /// level. The latest filter wins; a library opened meanwhile drops the build.
    /// </summary>
    private async Task ApplyFilterAsync(int version)
    {
        await Task.Delay(400);
        if (version != _filterVersion || _fullAtlas is not { } full || _traits is not { } traits)
        {
            return;
        }

        int load = _loadVersion;
        bool Stale() => version != _filterVersion || load != _loadVersion || !ReferenceEquals(full, _fullAtlas);
        SoundFilter filter = _filter;
        TraitChip[] chips = [.. FilterRows.SelectMany(static r => r.Chips)];
        TraitValue[] values = [.. chips.Select(static c => c.Value)];
        (int[] counts, int[] members) = await Task.Run(() => (filter.Counts(traits, values), filter.IsEmpty ? [] : filter.Members(traits)));
        if (Stale())
        {
            return;
        }

        for (int k = 0; k < chips.Length; k++)
        {
            chips[k].Count = counts[k];
        }

        FilterSummary = filter.IsEmpty ? null : string.Create(CultureInfo.InvariantCulture, $"filter: {filter.Describe()} — {members.Length:N0} sounds");
        if (SameChoice(filter, _shownFilter))
        {
            // The tree shown is already this filter's (a chip chosen and released again): keep the place in it.
            return;
        }

        if (filter.IsEmpty)
        {
            await ShowAtlasAsync(full, filter);
            return;
        }

        // The outgoing tree can no longer be browsed or placed into: its breadcrumb goes with it.
        _navVersion++;
        _placed = null;
        _atlas = null;
        _family = null;
        _children = [];
        Crumbs.Clear();
        Nodes = null;
        Edges = null;
        SelectedIndex = -1;
        if (members.Length == 0)
        {
            _atlas = LibraryAtlas.Build(full.Library, []);
            _family = _atlas.Root;
            _shownFilter = filter;
            GraphMessage = "No sounds match the filter.";
            return;
        }

        GraphMessage = string.Create(CultureInfo.InvariantCulture, $"Grouping {members.Length:N0} sounds…");
        LibraryAtlas atlas = await Task.Run(() =>
        {
            LibraryAtlas built = LibraryAtlas.Build(full.Library, members);
            built.Children(built.Root);
            return built;
        });
        if (Stale())
        {
            return;
        }

        await ShowAtlasAsync(atlas, filter);
    }

    private async Task ShowAtlasAsync(LibraryAtlas atlas, SoundFilter filter)
    {
        _atlas = atlas;
        _shownFilter = filter;
        _placed = null;
        await ShowFamilyAsync(atlas.Root, [new Crumb("all", atlas.Root)]);
    }

    private static bool SameChoice(SoundFilter a, SoundFilter b) => a.Chosen.Count == b.Chosen.Count && a.Chosen.All(b.Contains);

    private (ImportedItem Item, IReadOnlyList<LibraryFamily> Path)? _placed;

    /// <summary>Sounds imported this session.</summary>
    public ObservableCollection<ImportedItem> Imports { get; } = [];

    /// <summary>Favourite sounds, kept between sessions, by nickname.</summary>
    public ObservableCollection<FavouriteItem> Favourites { get; } = [];

    /// <summary>The side list is shown when there is something in it.</summary>
    public bool HasSideList => Imports.Count > 0 || Favourites.Count > 0;

    /// <summary>The side list shows the favourites (the default) rather than the imported sounds.</summary>
    public bool ShowFavourites
    {
        get => _showFavourites;
        set
        {
            if (Set(ref _showFavourites, value))
            {
                OnPropertyChanged(nameof(ShowImports));
            }
        }
    }

    /// <summary>The side list shows the imported sounds.</summary>
    public bool ShowImports
    {
        get => !_showFavourites;
        set => ShowFavourites = !value;
    }

    /// <summary>The favourite selected in the side list, for Rename and Remove.</summary>
    public FavouriteItem? SelectedFavourite
    {
        get => _selectedFavourite;
        set
        {
            if (Set(ref _selectedFavourite, value))
            {
                OnPropertyChanged(nameof(HasSelectedFavourite));
            }
        }
    }

    /// <summary>True when a favourite is selected in the side list.</summary>
    public bool HasSelectedFavourite => _selectedFavourite is not null;

    /// <summary>The nickname typed in the favourite box.</summary>
    public string FavouriteNickname
    {
        get => _favouriteNickname;
        set => Set(ref _favouriteNickname, value ?? string.Empty);
    }

    /// <summary>True when the shown sound is a favourite.</summary>
    public bool IsShownFavourite => _selection is { } s && FindFavourite(s.Patch, s.Profile) is not null;

    /// <summary>The star of the favourite button: filled for a favourite.</summary>
    public string FavouriteStar => IsShownFavourite ? "★" : "☆";

    /// <summary>The favourite of a patch on a profile, if there is one.</summary>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile.</param>
    public FavouriteItem? FindFavourite(OperatorMatrixPatch patch, SynthesisProfile profile) =>
        FavouriteSounds.Find(Favourites.Select(static f => f.Favourite), patch, profile) is { } found
            ? Favourites.First(f => ReferenceEquals(f.Favourite, found))
            : null;

    /// <summary>Fills the favourite box: the nickname of the shown sound when it is a favourite, else a suggestion.</summary>
    public void PrepareFavouriteName()
    {
        if (_selection is { } s)
        {
            FavouriteNickname = FindFavourite(s.Patch, s.Profile)?.Name ?? s.Title;
        }
    }

    /// <summary>Keeps the shown sound as a favourite with the typed nickname, or renames it when it is one already.</summary>
    public async Task SaveShownFavouriteAsync()
    {
        if (_selection is { } s)
        {
            await SaveFavouriteAsync(FavouriteNickname, s.Patch, s.Profile, string.Join(" ", s.HeaderComments));
        }
    }

    /// <summary>Removes the shown sound from the favourites.</summary>
    public void RemoveShownFavourite()
    {
        if (_selection is { } s && FindFavourite(s.Patch, s.Profile) is { } item)
        {
            RemoveFavourite(item);
        }
    }

    /// <summary>
    /// Keeps a sound as a favourite under a nickname, or gives the favourite it already is that nickname; writing a
    /// favourite renders its preview, so this runs off the interface thread.
    /// </summary>
    /// <param name="nickname">Nickname.</param>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile it plays on.</param>
    /// <param name="origin">Where it came from.</param>
    public async Task SaveFavouriteAsync(string nickname, OperatorMatrixPatch patch, SynthesisProfile profile, string origin)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            if (FindFavourite(patch, profile) is { } existing)
            {
                RenameFavourite(existing, nickname);
                return;
            }

            Favourite added = await Task.Run(() => _favouriteStore.Add(nickname, patch, profile, origin));
            ReloadFavourites();
            _status($"\u2605 {added.Nickname} is a favourite; the Favourites list of the Library tab has it.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status("The favourite could not be saved: " + e.Message);
        }
    }

    /// <summary>Gives a favourite another nickname.</summary>
    /// <param name="item">Favourite.</param>
    /// <param name="nickname">New nickname.</param>
    public void RenameFavourite(FavouriteItem item, string nickname)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            Favourite renamed = _favouriteStore.Rename(item.Favourite, nickname);
            ReloadFavourites();
            SelectedFavourite = Favourites.FirstOrDefault(f => f.Favourite.Directory == renamed.Directory);
            _status($"Renamed to {renamed.Nickname}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status("The favourite could not be renamed: " + e.Message);
        }
    }

    /// <summary>Removes a favourite and its folder.</summary>
    /// <param name="item">Favourite.</param>
    public void RemoveFavourite(FavouriteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            _favouriteStore.Remove(item.Favourite);
            ReloadFavourites();
            _status($"{item.Name} is no longer a favourite.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status("The favourite could not be removed: " + e.Message);
        }
    }

    /// <summary>Places a favourite among its neighbours in the library of its profile, selected and played.</summary>
    /// <param name="item">Favourite.</param>
    public async Task PlaceFavouriteAsync(FavouriteItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ExportedSound sound = item.Favourite.Sound;
        _status($"Rendering {item.Name} to find its neighbours…");
        byte[] print = await Task.Run(() => ExportedSounds.Fingerprint(sound));
        string source = item.Favourite.Origin.Length > 0 ? "favourite: " + item.Favourite.Origin : "favourite";
        await ShowAmongNeighboursAsync(new ImportedItem(sound with { Name = item.Name }, print) { SourceText = source }, sound.Profile);
    }

    private void ReloadFavourites()
    {
        string? selected = _selectedFavourite?.Favourite.Directory;
        Favourites.Clear();
        foreach (Favourite favourite in _favouriteStore.Load())
        {
            Favourites.Add(new FavouriteItem(favourite));
        }

        SelectedFavourite = Favourites.FirstOrDefault(f => f.Favourite.Directory == selected);
    }

    /// <summary>Reads the exported sounds under a folder and adds them to the list.</summary>
    /// <param name="directory">Folder.</param>
    public async Task ImportAsync(string directory)
    {
        _status("Reading " + directory + "…");
        ExportedSoundScan scan;
        List<ImportedItem> items;
        List<string> unplayable;
        try
        {
            (scan, items, unplayable) = await Task.Run(() =>
            {
                ExportedSoundScan s = ExportedSounds.Scan(directory);
                var rendered = s.Sounds.AsParallel().AsOrdered().Select(static sound =>
                {
                    try
                    {
                        return (Item: new ImportedItem(sound, ExportedSounds.Fingerprint(sound)), Problem: (string?)null);
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
                    {
                        return (Item: (ImportedItem?)null, Problem: $"{sound.Name}: cannot be rendered ({e.Message})");
                    }
                }).ToList();
                return (s, rendered.Where(static r => r.Item is not null).Select(static r => r.Item!).ToList(), rendered.Where(static r => r.Problem is not null).Select(static r => r.Problem!).ToList());
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            _status("The folder could not be read: " + e.Message);
            return;
        }

        scan = scan with { Skipped = [.. scan.Skipped, .. unplayable] };
        int added = 0;
        foreach (ImportedItem item in items.Where(i => Imports.All(known => known.Key != i.Key)))
        {
            Imports.Add(item);
            added++;
            ShowImports = true;
        }

        if (scan.Sounds.Count == 0 && scan.Skipped.Count == 0)
        {
            _status($"No exported sounds (patch.json) under {directory}.");
            return;
        }

        string skipped = scan.Skipped.Count == 0
            ? string.Empty
            : $", {scan.Skipped.Count} skipped: {string.Join("; ", scan.Skipped.Take(3))}{(scan.Skipped.Count > 3 ? "; …" : string.Empty)}";
        _status($"{added} sound(s) imported{skipped}. Click one in the list to place it in the library.");
    }

    /// <summary>Opens the family an imported sound falls in and shows it there as a star, selected and centred.</summary>
    /// <param name="item">Imported sound.</param>
    public async Task PlaceAsync(ImportedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_atlas is not { } atlas)
        {
            _status(_fullAtlas is null ? "The library is not ready yet; place the sound when it is." : "The filtered sounds are being grouped; place the sound when they are shown.");
            return;
        }

        if (atlas.Root.Size == 0)
        {
            _status("No sounds match the filter.");
            return;
        }

        int version = _loadVersion;
        IReadOnlyList<LibraryFamily> path = await Task.Run(() => atlas.Locate(item.Print));
        if (version != _loadVersion || !ReferenceEquals(atlas, _atlas))
        {
            // Another library was opened meanwhile; this placement belongs to the old one.
            return;
        }

        List<Crumb> crumbs = [new Crumb("all", atlas.Root), .. path.Skip(1).Select(f => new Crumb(CrumbLabel(atlas, f, f.Path[^1]), f))];
        _placed = (item, path);
        if (!await ShowFamilyAsync(path[^1], crumbs))
        {
            return;
        }

        Select(_children.Count);
        string outside = !_filter.IsEmpty && !_filter.Matches(SoundTraits.Of(item.Print, item.Sound.Patch))
            ? " Placed among the filtered sounds; it does not match the filter itself."
            : string.Empty;
        _status($"{item.Name} placed among {_children.Count} {(path[^1].Size <= LibraryAtlas.Branching ? "sounds" : "families")} like it; one level up shows the neighbouring families.{outside}");
    }

    /// <summary>Loads the library the first time the tab is shown.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return _loading;
        }

        _loaded = true;
        _loading = LoadAsync();
        return _loading;
    }

    /// <summary>
    /// Places a finalist of the Search tab among its neighbours, as an imported sound: the library of its profile is
    /// shown (loaded or built first when needed), the finalist joins the imported sounds and is placed, selected and
    /// played.
    /// </summary>
    /// <param name="name">Name in the list, for example <c>search #2</c>.</param>
    /// <param name="origin">Where the search found it.</param>
    /// <param name="patch">Patch.</param>
    /// <param name="profile">Profile it was found for.</param>
    public async Task FindAsync(string name, string origin, OperatorMatrixPatch patch, SynthesisProfile profile)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(profile);
        var sound = new ExportedSound(name, string.Empty, patch, profile);
        var found = new ImportedItem(sound, []) { SearchOrigin = origin };
        ImportedItem? item = Imports.FirstOrDefault(k => k.Key == found.Key);
        if (item is null)
        {
            _status($"Rendering {name} to find its neighbours…");
            byte[] print = await Task.Run(() => ExportedSounds.Fingerprint(sound));
            item = found with { Print = print };
            Imports.Add(item);
            ShowImports = true;
        }

        await ShowAmongNeighboursAsync(item, profile);
    }

    /// <summary>Shows the library of a profile and places a sound in it once it is shown.</summary>
    private async Task ShowAmongNeighboursAsync(ImportedItem item, SynthesisProfile profile)
    {
        // Its own profile's library holds the neighbours; a profile without one keeps the library shown.
        if (Profiles.FirstOrDefault(p => p.Name == profile.Name) is { } own)
        {
            SelectedProfile = own;
        }

        // Placed in the library that is shown once loading settles, also when another profile was chosen meanwhile.
        await EnsureLoadedAsync();
        while (!_loading.IsCompleted)
        {
            await _loading;
        }

        await PlaceAsync(item);
    }

    /// <summary>Selects a node, shows its code and plays it at the reference note.</summary>
    /// <param name="index">Node index.</param>
    public void Select(int index)
    {
        if (index == _children.Count && _placed is { } placed && ReferenceEquals(placed.Path[^1], _family))
        {
            ImportedItem item = placed.Item;
            SelectedIndex = index;
            SetSelection(new Selection(
                item.Name,
                item.Sound.Patch,
                item.Sound.Profile,
                $"{item.Name} · {item.Source}",
                [$"{char.ToUpperInvariant(item.Source[0])}{item.Source[1..]}."]));
            return;
        }

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
        await ShowFamilyAsync(child, [.. Crumbs, new Crumb(CrumbLabel(atlas, child, index), child)]);
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

        await ShowFamilyAsync(crumb.Family, [.. Crumbs.Take(at + 1)]);
    }

    /// <summary>The sound the keyboard plays: the last clicked one, at the gain of the library's reference note; null before a click.</summary>
    public Sound? CurrentSound => _selection is { } s ? new Sound(s.Patch, s.Profile, StructureLibrary.ReferenceRequest) : null;

    /// <summary>The notes that sound now, lit on the keyboard.</summary>
    public IReadOnlyCollection<int> SoundingNotes => _live.SoundingNotes;

    /// <summary>Starts a note of the last clicked sound, held until <see cref="NoteOff"/>.</summary>
    /// <param name="midiNote">MIDI note.</param>
    /// <param name="velocity">Key velocity 0..1.</param>
    public void NoteOn(int midiNote, double velocity) => _live.NoteOn(midiNote, velocity, _keyboard);

    /// <summary>Releases a note started by <see cref="NoteOn"/>.</summary>
    /// <param name="midiNote">MIDI note.</param>
    public void NoteOff(int midiNote) => _live.NoteOff(midiNote, _keyboard);

    /// <summary>Shows what the keyboard or the MIDI device last played.</summary>
    /// <param name="text">For example <c>C4 (261.6 Hz), velocity 0.80</c>.</param>
    internal void ShowPlaying(string text) => PlayingText = text;

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
        _navVersion++;
        SynthesisProfile profile = _profile;
        string name = ProfileNameConverter.Short(profile.Name);
        _atlas = null;
        _family = null;
        _placed = null;
        _children = [];
        Crumbs.Clear();
        Nodes = null;
        Edges = null;
        SelectedIndex = -1;
        _fullAtlas = null;
        _traits = null;
        _filter = SoundFilter.None;
        _filterVersion++;
        _shownFilter = SoundFilter.None;
        FilterRows.Clear();
        FilterSummary = null;
        OnPropertyChanged(nameof(HasFilter));

        // Building takes minutes: the graph says what is rendered, the share done and the time left.
        int toRender = StructureLibrary.SoundsToRender(profile, _size);
        string building = toRender < StructureLibrary.CountFor(profile.Capabilities, _size)
            ? string.Create(CultureInfo.InvariantCulture, $"Adding {toRender:N0} noise-free sounds to the {name} library, once")
            : string.Create(CultureInfo.InvariantCulture, $"Building the {name} library: {toRender:N0} sounds to render, once");
        var clock = new RemainingTime();
        _progress(0.0);
        GraphMessage = toRender == 0 ? $"Loading the {name} library…" : $"{building}; {clock.Describe(0.0)}…";
        var progress = new Progress<double>(f =>
        {
            if (version == _loadVersion)
            {
                _progress(f);
                GraphMessage = $"{building}; {clock.Describe(f)}…";
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
            (LibraryAtlas atlas, SoundTraits[] traits, int[] counts) = await Task.Run(() =>
            {
                StructureLibrary library = StructureLibrary.For(profile, _size, SynthesisBackends.RendererFor(profile), progress: progress);
                string cache = LibraryAtlas.CacheFileOf(StructureLibrary.FileOf(profile, _size));
                if (!File.Exists(cache))
                {
                    ((IProgress<string>)stage).Report($"Grouping the {name} sounds into families (once, under a minute)…");
                }

                LibraryAtlas opened = LibraryAtlas.Open(library, cache);
                ((IProgress<string>)stage).Report($"Reading the {name} sounds…");
                SoundTraits[] t = SoundTraits.Of(library);
                return (opened, t, SoundFilter.None.Counts(t, TraitValue.All));
            });
            if (version != _loadVersion)
            {
                return;
            }

            _fullAtlas = atlas;
            _traits = traits;
            BuildFilterRows(counts, traits.Length);
            _atlas = atlas;
            if (!await ShowFamilyAsync(atlas.Root, [new Crumb("all", atlas.Root)]) || version != _loadVersion)
            {
                return;
            }

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

    /// <summary>
    /// Shows the children of a family with the breadcrumb that leads to it, both at once and only if nothing else was
    /// opened meanwhile: the last family asked for wins, so the breadcrumb always tells where the graph is.
    /// </summary>
    /// <returns>True when the family is shown.</returns>
    private async Task<bool> ShowFamilyAsync(LibraryFamily family, IReadOnlyList<Crumb> crumbs)
    {
        if (_atlas is not { } atlas)
        {
            return false;
        }

        int version = _loadVersion;
        int navigation = ++_navVersion;
        (IReadOnlyList<LibraryFamily> children, List<byte[]> prints, List<OperatorMatrixPatch> patches) = await Task.Run(() =>
        {
            IReadOnlyList<LibraryFamily> c = atlas.Children(family);
            return (c, c.Select(f => atlas.Library.Fingerprint(f.Medoid).ToArray()).ToList(), c.Select(f => atlas.Library.Patch(atlas.Library.Seed(f.Medoid))).ToList());
        });
        if (version != _loadVersion || navigation != _navVersion || !ReferenceEquals(atlas, _atlas))
        {
            return false;
        }

        if (!Crumbs.SequenceEqual(crumbs))
        {
            Crumbs.Clear();
            foreach (Crumb crumb in crumbs)
            {
                Crumbs.Add(crumb);
            }
        }

        _family = family;
        _children = children;
        ImportedItem? star = _placed is { } placed && ReferenceEquals(placed.Path[^1], family) ? placed.Item : null;
        int marked = -1;
        if (_placed is { } p)
        {
            int at = p.Path.ToList().FindIndex(f => ReferenceEquals(f, family));
            if (at >= 0 && at + 1 < p.Path.Count)
            {
                marked = p.Path[at + 1].Path[^1];
            }
        }

        List<byte[]> layoutPrints = star is null ? prints : [.. prints, star.Print];
        List<int> sizes = [.. children.Select(static c => c.Size)];
        if (star is not null)
        {
            sizes.Add(1);
        }

        AtlasLayout layout = AtlasLayout.Compute(layoutPrints, sizes);
        if (star is not null)
        {
            layout = layout.CenterOn(children.Count);
        }

        var nodes = children.Select((c, k) => new GraphNode(
            layout.Positions[k].X,
            layout.Positions[k].Y,
            layout.Radii[k],
            Brightness(prints[k]),
            c.IsLeaf ? string.Empty : c.Size.ToString("N0", CultureInfo.InvariantCulture),
            $"#{k + 1} · {SoundDescriptors.Label(prints[k], patches[k], c.Size)}",
            IsImported: false,
            CanOpen: !c.IsLeaf,
            IsMarked: k == marked)).ToList();
        if (star is not null)
        {
            int s = children.Count;
            nodes.Add(new GraphNode(
                layout.Positions[s].X,
                layout.Positions[s].Y,
                layout.Radii[s],
                Brightness(star.Print),
                star.Name,
                $"{star.Name} · {string.Join(" · ", SoundDescriptors.Of(star.Print, star.Sound.Patch))} · imported, {star.ProfileName}",
                IsImported: true,
                CanOpen: false,
                IsMarked: false));
        }

        Nodes = nodes;
        Edges = [.. layout.Edges.Select(static e => new GraphEdge(e.From, e.To))];
        SelectedIndex = -1;
        GraphMessage = null;
        return true;
    }

    private static double Brightness(byte[] print) => Math.Clamp(Math.Log2(SoundDescriptors.BrightnessRatio(print)) / 4.0, 0.0, 1.0);

    /// <summary>The chips that split the library: some sounds have the value and some do not.</summary>
    private void BuildFilterRows(int[] counts, int size)
    {
        FilterRows.Clear();
        IEnumerable<IGrouping<TraitFacet, (TraitValue Value, int Count)>> facets = TraitValue.All
            .Select((v, k) => (Value: v, Count: counts[k]))
            .Where(x => x.Count > 0 && x.Count < size)
            .GroupBy(static x => x.Value.Facet);
        foreach (IGrouping<TraitFacet, (TraitValue Value, int Count)> facet in facets)
        {
            FilterRows.Add(new TraitRow(TraitValue.TitleOf(facet.Key), [.. facet.Select(x => new TraitChip(x.Value, x.Count, OnChipToggled))]));
        }
    }

    private static string CrumbLabel(LibraryAtlas atlas, LibraryFamily family, int index)
    {
        IReadOnlyList<string> words = SoundDescriptors.Of(atlas.Library.Fingerprint(family.Medoid), atlas.Library.Patch(atlas.Library.Seed(family.Medoid)));
        return $"#{index + 1} {string.Join(" · ", words.Take(2))}";
    }

    private void SetSelection(Selection selection)
    {
        _selection = selection;
        OnPropertyChanged(nameof(ShownPatch));
        OnPropertyChanged(nameof(ShownPatchSharesNoise));
        OnPropertyChanged(nameof(Scd));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsShownFavourite));
        OnPropertyChanged(nameof(FavouriteStar));
        _ = PlayReferenceAsync(selection);
    }

    /// <summary>
    /// Plays the library's reference note once, as the library rendered it: held for its note duration from when the
    /// sound's gain is known, then released, through the live voices. Every click is a note of its own.
    /// </summary>
    private async Task PlayReferenceAsync(Selection selection)
    {
        if (!_live.CanPlay)
        {
            return;
        }

        RenderRequest reference = StructureLibrary.ReferenceRequest;
        var sound = new Sound(selection.Patch, selection.Profile, reference);
        int note = reference.MidiNote ?? (int)Math.Round(reference.EffectiveMidiNote);
        var click = new object();
        if (!_live.Play(sound, note, reference.Velocity, click))
        {
            _status(LivePlayer.CannotPlay(sound));
            return;
        }

        PlayingText = "A3 (220.0 Hz), the library's reference note";

        // A note waits for its gain before it sounds: the hold starts then. A failed gain ends the note unheard.
        await Task.WhenAny(_live.Mixer.PrepareAsync(sound));
        await Task.Delay(reference.NoteDuration);
        _live.NoteOff(note, click);
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
