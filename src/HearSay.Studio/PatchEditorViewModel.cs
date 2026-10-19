using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using HearSay.Editing;
using HearSay.Playing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;
using HearSay.Synthesis.Rendering;

namespace HearSay.Studio;

/// <summary>
/// The editing of the sound a tab shows: which version the diagram, the code view and the keyboard get (the original or
/// the edit), undo and redo, the part selected for the inspector, and the gain the edit plays at: the original's, until
/// Match loudness gives the edit a gain of its own. Each tab has one; the tabs share the edits of a sound through one
/// <see cref="EditSessions"/>, so that the same patch shown in both has one edited copy, one history and one matched
/// gain.
/// </summary>
/// <remarks>
/// Used on the interface thread only. The original's gain is fetched when a sound is shown; a fetch that completes after
/// another sound was shown is dropped.
/// </remarks>
public sealed class PatchEditorViewModel : INotifyPropertyChanged
{
    /// <summary>The line added to the header comments of the code of an edited version.</summary>
    public const string EditedHeaderLine = "Edited in HearSay Studio.";

    /// <summary>The status line once Match loudness is done.</summary>
    public const string MatchedNotice = "Loudness matched: the edit now plays as loud as a new sound would.";

    /// <summary>
    /// The header comments of an edited version: the original's with <see cref="EditedHeaderLine"/> added, unless they
    /// already have it (an edit of an edit).
    /// </summary>
    /// <param name="header">The header comments of the original.</param>
    public static IReadOnlyList<string> WithEditedLine(IReadOnlyList<string> header)
    {
        ArgumentNullException.ThrowIfNull(header);
        return header.Contains(EditedHeaderLine, StringComparer.Ordinal) ? header : [.. header, EditedHeaderLine];
    }

    private readonly EditSessions _sessions;
    private readonly Func<Sound, Task<float>> _gainOf;

    /// <summary>The gains of the originals shown before, so that coming back to an edit plays it at once.</summary>
    private readonly Dictionary<Sound, float> _gains = [];

    private PatchEditor? _editor;
    private Sound? _original;
    private string _origin = string.Empty;
    private int _shown;
    private bool _gainKnown;
    private float? _gain;
    private Task<float?> _gainTask = Task.FromResult<float?>(null);
    private bool _isEdited;
    private PatchPart? _selected;
    private IReadOnlyList<EditableValue> _selectedValues = [];
    private string? _lastRefusal;
    private string? _notice;
    private bool _matching;

    /// <summary>Creates the editing of a tab, with nothing shown.</summary>
    /// <param name="sessions">The edits of every sound, shared by the tabs.</param>
    /// <param name="gainOf">The gain the live player plays a sound at; called on the interface thread.</param>
    public PatchEditorViewModel(EditSessions sessions, Func<Sound, Task<float>> gainOf)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(gainOf);
        _sessions = sessions;
        _gainOf = gainOf;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The edit model of the shown sound; null when nothing is shown.</summary>
    public PatchEditor? Editor => _editor;

    /// <summary>True when a sound is shown, so that it can be edited.</summary>
    public bool HasSound => _editor is not null;

    /// <summary>The version the diagram and the code show: the original or the edit (<see cref="PatchEditor.Displayed"/>).</summary>
    public OperatorMatrixPatch? DisplayedPatch => _editor?.Displayed;

    /// <summary>True to show the original instead of the edit; switching during a gesture ends the gesture first.</summary>
    public bool ShowingOriginal
    {
        get => _editor?.ShowingOriginal ?? false;
        set
        {
            if (_editor is { } editor && editor.ShowingOriginal != value)
            {
                // The editor commits a gesture in progress, and says when the displayed version changed.
                editor.ShowingOriginal = value;
            }
        }
    }

    /// <summary>The opposite of <see cref="ShowingOriginal"/>, for the Edited half of the switch.</summary>
    public bool ShowingEdited
    {
        get => !ShowingOriginal;
        set => ShowingOriginal = !value;
    }

    /// <summary>True when the edited patch differs from the original.</summary>
    public bool IsEdited => _isEdited;

    /// <summary>True when the edit is what is displayed: edited, and not switched to the original.</summary>
    public bool ShowsEdit => _isEdited && !ShowingOriginal;

    /// <summary>True when there is a step to undo.</summary>
    public bool CanUndo => _editor?.CanUndo ?? false;

    /// <summary>True when there is an undone step to redo.</summary>
    public bool CanRedo => _editor?.CanRedo ?? false;

    /// <summary>
    /// True while a gesture, such as a drag, is under way: from <see cref="BeginGesture"/> until it ends, or until an
    /// undo, a redo or the Original ⇄ Edited switch ends it underneath the control that began it.
    /// </summary>
    public bool InGesture => _editor?.InGesture ?? false;

    /// <summary>The part of the patch selected for the inspector; null for none.</summary>
    public PatchPart? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                UpdateSelectedValues(force: true);
            }
        }
    }

    /// <summary>The values of the selected part, on the displayed version; empty without a selection.</summary>
    public IReadOnlyList<EditableValue> SelectedValues => _selectedValues;

    /// <summary>
    /// The sound a new note plays: the original as the tab plays it, or, while the edit is displayed, the edit at its
    /// matched gain (<see cref="MatchLoudnessAsync"/>) or else at the original's. Null when nothing is shown, and while
    /// the edit is displayed without a matched gain but the original's gain is not known yet. When the original's gain
    /// could not be computed, the edit is normalised on its own.
    /// </summary>
    public Sound? DisplayedSound
    {
        get
        {
            if (_editor is not { } editor || _original is not { } original)
            {
                return null;
            }

            if (!ShowsEdit)
            {
                return original;
            }

            if (editor.MatchedGain is float matched)
            {
                return original with { Patch = editor.Current, FixedGain = matched };
            }

            return _gainKnown ? original with { Patch = editor.Current, FixedGain = _gain } : null;
        }
    }

    /// <summary>True when Match loudness can be clicked: the edit is displayed and its gain is not being computed.</summary>
    public bool CanMatchLoudness => ShowsEdit && !_matching;

    /// <summary>The gain the edit plays at since its loudness was matched; null when it plays at the original's.</summary>
    public float? MatchedGain => _editor?.MatchedGain;

    /// <summary>A line for the status line about an editing action that was done, such as Match loudness; null for none.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            // Said again when the same action is done again.
            _notice = value;
            OnPropertyChanged(nameof(Notice));
        }
    }

    /// <summary>The gain of the original, once known; null before, or when it could not be computed.</summary>
    public float? OriginalGain => _gainKnown ? _gain : null;

    /// <summary>Where the displayed version came from, for a favourite: <c>edited from …</c> while the edit is displayed.</summary>
    public string FavouriteOrigin => ShowsEdit ? "edited from " + _origin : _origin;

    /// <summary>Why the last edit was refused, for the status line; null when it was done.</summary>
    public string? LastRefusal
    {
        get => _lastRefusal;
        private set => Set(ref _lastRefusal, value);
    }

    /// <summary>
    /// Shows a sound: its edits, if it was edited before, and its history come back. Call it whenever the tab's shown sound
    /// changes. A gesture under way on the sound shown before ends as one step. The original's gain is fetched; a fetch
    /// for a sound shown earlier never overwrites it.
    /// </summary>
    /// <param name="original">The patch as it was; null when nothing is shown.</param>
    /// <param name="profile">Its profile; null when nothing is shown.</param>
    /// <param name="gainReference">The note whose loudness sets the original's gain, as the tab plays it.</param>
    /// <param name="origin">Where the sound came from, as a favourite of it would say.</param>
    /// <param name="fixedGain">The gain the tab plays the original at when it is already known (a favourite that kept one); null to compute it.</param>
    public void Show(OperatorMatrixPatch? original, SynthesisProfile? profile, RenderRequest? gainReference, string origin, float? fixedGain = null)
    {
        PatchEditor? editor = original is not null && profile is not null && gainReference is not null ? _sessions.For(original, profile) : null;
        if (!ReferenceEquals(editor, _editor))
        {
            if (_editor is not null)
            {
                // A drag on the sound that goes is one step of its history; the control that began it sees that its
                // gesture ended and stops.
                _editor.EndGesture();
                _editor.Changed -= OnEditorChanged;
            }

            if (editor is not null)
            {
                editor.Changed += OnEditorChanged;
            }

            _editor = editor;
            _selected = null;
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(Editor));
            OnPropertyChanged(nameof(HasSound));
        }

        _original = editor is null ? null : new Sound(original!, profile!, gainReference!) { FixedGain = fixedGain };
        _origin = origin ?? string.Empty;
        LastRefusal = null;
        FetchGain(_original);
        Refresh();
    }

    /// <summary>Makes an edit as one step; editing while the original is shown shows the edit.</summary>
    /// <param name="e">The change.</param>
    /// <returns>The outcome; a refusal is also in <see cref="LastRefusal"/>.</returns>
    /// <exception cref="InvalidOperationException">No sound is shown.</exception>
    public EditOutcome Apply(PatchEdit e)
    {
        PatchEditor editor = EditorOrThrow();
        editor.ShowingOriginal = false;
        EditOutcome outcome = editor.Apply(e);
        LastRefusal = outcome.Refusal;
        return outcome;
    }

    /// <summary>Starts a gesture, such as a drag, whose previews become one step; it shows the edit.</summary>
    /// <exception cref="InvalidOperationException">No sound is shown.</exception>
    public void BeginGesture()
    {
        PatchEditor editor = EditorOrThrow();
        editor.ShowingOriginal = false;
        editor.BeginGesture();
        Refresh();
    }

    /// <summary>
    /// Shows an edit of the patch as it was when the gesture began. Without a gesture under way (it was never begun, or
    /// an undo, a redo or the switch ended it) nothing changes and the outcome is a refusal, not shown in the status line.
    /// </summary>
    /// <param name="e">The change.</param>
    /// <returns>The outcome; a refusal of the edit is also in <see cref="LastRefusal"/>.</returns>
    /// <exception cref="InvalidOperationException">No sound is shown.</exception>
    public EditOutcome Preview(PatchEdit e)
    {
        PatchEditor editor = EditorOrThrow();
        if (!editor.InGesture)
        {
            return EditOutcome.Refused(editor.Current, "No gesture is under way.");
        }

        EditOutcome outcome = editor.Preview(e);
        LastRefusal = outcome.Refusal;
        return outcome;
    }

    /// <summary>Ends the gesture: one step when the patch changed.</summary>
    public void EndGesture()
    {
        _editor?.EndGesture();
        Refresh();
    }

    /// <summary>Ends the gesture and goes back to how the patch was when it began.</summary>
    public void CancelGesture()
    {
        _editor?.CancelGesture();
        Refresh();
    }

    /// <summary>Goes back one step, and shows the edit.</summary>
    public void Undo()
    {
        if (_editor is { } editor)
        {
            editor.ShowingOriginal = false;
            editor.Undo();
        }
    }

    /// <summary>Goes forward one undone step, and shows the edit.</summary>
    public void Redo()
    {
        if (_editor is { } editor)
        {
            editor.ShowingOriginal = false;
            editor.Redo();
        }
    }

    /// <summary>
    /// Plays the edit from now on as loud as a new sound of it would be: its own gain, computed as for a fresh sound (the
    /// original with the edited patch and no fixed gain) off the interface thread, becomes the edit session's
    /// <see cref="PatchEditor.MatchedGain"/>, which the same sound in the other tab shares and later edits keep. Clicking
    /// again computes it again for the edit as it then is. Nothing while the edit is not displayed.
    /// </summary>
    public async Task MatchLoudnessAsync()
    {
        if (!CanMatchLoudness || _editor is not { } editor || _original is not { } original)
        {
            return;
        }

        Sound fresh = original with { Patch = editor.Current, FixedGain = null };
        _matching = true;
        OnPropertyChanged(nameof(CanMatchLoudness));
        try
        {
            float gain = await _gainOf(fresh);
            editor.MatchedGain = gain;
            Notice = MatchedNotice;
        }
        catch (Exception e)
        {
            // The reference note of the edit could not be rendered (as for the original's gain, whatever the reason):
            // the edit keeps the gain it had.
            LastRefusal = "The loudness of the edit could not be computed: " + e.Message;
        }
        finally
        {
            _matching = false;
            Refresh();
        }
    }

    /// <summary>
    /// The gain a favourite of the displayed version keeps: for an edit its matched gain, else the original's, waited for
    /// when still being computed.
    /// </summary>
    /// <returns>The gain; null to normalise the favourite as usual.</returns>
    public async Task<float?> FavouriteGainAsync()
    {
        if (!ShowsEdit)
        {
            return _original?.FixedGain;
        }

        return _editor?.MatchedGain is float matched ? matched : await _gainTask;
    }

    /// <summary>
    /// Waits until the original's gain is known, or could not be computed; once it completes, on the interface thread,
    /// <see cref="DisplayedSound"/> has the edit.
    /// </summary>
    public Task WhenGainKnownAsync() => _gainTask;

    private PatchEditor EditorOrThrow() => _editor ?? throw new InvalidOperationException("No sound is shown to edit.");

    private void FetchGain(Sound? sound)
    {
        int shown = ++_shown;
        if (sound is null)
        {
            SetGain(known: false, null);
            _gainTask = Task.FromResult<float?>(null);
            return;
        }

        if (_gains.TryGetValue(sound, out float kept))
        {
            SetGain(known: true, kept);
            _gainTask = Task.FromResult<float?>(kept);
            return;
        }

        Task<float> fetch;
        try
        {
            fetch = _gainOf(sound);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            fetch = Task.FromException<float>(e);
        }

        if (fetch.IsCompleted)
        {
            _gainTask = Task.FromResult(Arrived(sound, fetch));
            return;
        }

        // Completed on the interface thread once the gain is in place, so that whoever waits for it finds it there.
        var known = new TaskCompletionSource<float?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gainTask = known.Task;
        SetGain(known: false, null);
        _ = fetch.ContinueWith(
            t => Dispatcher.UIThread.Post(() =>
            {
                // Another sound was shown meanwhile: its own fetch decides what is played.
                if (shown == _shown)
                {
                    known.TrySetResult(Arrived(sound, t));
                    Refresh();
                }
                else
                {
                    _ = t.Exception;
                    known.TrySetResult(t.IsCompletedSuccessfully ? t.Result : null);
                }
            }),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>Keeps the original's gain once it is computed; null, with the edit normalised on its own, when it could not be.</summary>
    private float? Arrived(Sound sound, Task<float> fetch)
    {
        if (fetch.IsCompletedSuccessfully)
        {
            _gains[sound] = fetch.Result;
            SetGain(known: true, fetch.Result);
            return fetch.Result;
        }

        // The original cannot be played live.
        _ = fetch.Exception;
        SetGain(known: true, null);
        return null;
    }

    private void SetGain(bool known, float? gain)
    {
        _gainKnown = known;
        _gain = gain;
    }

    private void OnEditorChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Says what follows the edit: the displayed version, the switch, undo and redo, the sound and the inspector's values.</summary>
    private void Refresh()
    {
        _isEdited = _editor?.IsEdited ?? false;
        OnPropertyChanged(nameof(DisplayedPatch));
        OnPropertyChanged(nameof(IsEdited));
        OnPropertyChanged(nameof(ShowingOriginal));
        OnPropertyChanged(nameof(ShowingEdited));
        OnPropertyChanged(nameof(ShowsEdit));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(OriginalGain));
        OnPropertyChanged(nameof(CanMatchLoudness));
        OnPropertyChanged(nameof(MatchedGain));
        OnPropertyChanged(nameof(DisplayedSound));
        OnPropertyChanged(nameof(FavouriteOrigin));
        UpdateSelectedValues(force: false);
    }

    /// <summary>
    /// Lists the values of the selected part again. During a gesture the list is kept while the part has the same values,
    /// so that a field being dragged is not replaced under the pointer.
    /// </summary>
    private void UpdateSelectedValues(bool force)
    {
        IReadOnlyList<EditableValue> values = _editor is { } editor && _selected is { } part
            ? EditableValues.For(editor.Displayed, editor.Profile.Capabilities, part)
            : [];
        if (!force && _editor is { InGesture: true } && values.Select(static v => v.Id).SequenceEqual(_selectedValues.Select(static v => v.Id)))
        {
            return;
        }

        _selectedValues = values;
        OnPropertyChanged(nameof(SelectedValues));
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
