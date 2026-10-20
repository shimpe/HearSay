using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using HearSay.Editing;
using HearSay.Synthesis.Patches;
using HearSay.Synthesis.Profiles;

namespace HearSay.Studio;

/// <summary>
/// The values of the part of the sound selected in the block diagram, to edit: an operator, a connection, the shared
/// noise or the pitch. Numbers are typed (committed on Enter or when the box is left) or dragged sideways on their name
/// or box (Shift finer, Ctrl to the musical ratios); waveform, frequency mode and filter type are menus, and a formant
/// filter has vowel presets (set and speaker menus and a button per vowel) and folds F4 and F5 away; what a part may
/// have or not (a sweep, a connection envelope, roughness, a pitch envelope, vibrato) is a toggle; envelopes are graphs
/// with their breakpoints as numbers. Its data context is the tab's <see cref="PatchEditorViewModel"/>. While the
/// original is shown it shows the original's values and cannot be changed. Refusals go to the status line. While the
/// edit is shown, every control whose value differs from the original's has an outline in the edited colour, and its
/// tooltip tells the original value; in a part the edit added (a new operator or connection) all of them have.
/// </summary>
public sealed partial class PatchInspector : UserControl
{
    /// <summary>
    /// The speakers chosen last per preset set while the Studio runs: the ones a newly built speaker menu starts at.
    /// </summary>
    private static readonly Dictionary<string, string> _lastSpeakers = [];

    /// <summary>
    /// The preset set chosen last while the Studio runs: the one a newly built set menu starts at. The vowel buttons
    /// apply the set and speaker their own menus show.
    /// </summary>
    private static string _lastSet = "Sung";

    /// <summary>True when "more formants" (F4 and F5) was left open, while the Studio runs.</summary>
    private static bool _moreFormantsExpanded;

    private readonly List<NumberBox> _boxes = [];
    private readonly List<EnvelopeEditor> _envelopes = [];
    private readonly List<Mark> _marks = [];
    private readonly Dictionary<string, EditableValue> _values = [];
    private PatchEditorViewModel? _editor;

    /// <summary>What the controls were built for; when it changes they are built again.</summary>
    private string? _shape;
    private bool _rebuildPending;
    private bool _building;

    public PatchInspector()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorChanged;
        }

        _editor = DataContext as PatchEditorViewModel;
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorChanged;
        }

        Update();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PatchEditorViewModel.DisplayedPatch) or nameof(PatchEditorViewModel.SelectedValues) or nameof(PatchEditorViewModel.Selected)
            or nameof(PatchEditorViewModel.ShowingOriginal) or nameof(PatchEditorViewModel.Editor))
        {
            Update();
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_editor is not null)
        {
            _editor.Selected = null;
        }
    }

    /// <summary>
    /// Follows the editing: the values in place at once, so that a drag stays smooth; the controls built again a moment
    /// later when the part has other values, menus or envelopes, not inside the handler of a control that goes away.
    /// </summary>
    private void Update()
    {
        _values.Clear();
        foreach (EditableValue value in _editor?.SelectedValues ?? [])
        {
            _values[value.Id] = value;
        }

        if (Shape() != _shape && !_rebuildPending)
        {
            _rebuildPending = true;
            Dispatcher.UIThread.Post(Rebuild);
        }

        Refresh();
    }

    private void Refresh()
    {
        if (_editor?.DisplayedPatch is not { } patch)
        {
            return;
        }

        foreach (NumberBox box in _boxes)
        {
            box.Show(patch);
        }

        foreach (EnvelopeEditor envelope in _envelopes)
        {
            envelope.Envelope = EnvelopeEdits.Get(patch, new EnvelopeRef(envelope.Part!, envelope.Kind));
        }

        ShowMarks(patch);

        // The original cannot be changed; "more formants" still opens and closes, its rows read-only.
        bool editable = !_editor.ShowingOriginal;
        foreach (Control child in Body.Children)
        {
            if (child is Expander { Content: Control rows })
            {
                child.IsEnabled = true;
                rows.IsEnabled = editable;
            }
            else
            {
                child.IsEnabled = editable;
            }
        }

        string about = _editor.Selected is { } part ? About(patch, part) : string.Empty;
        Subtitle.Text = _editor.ShowingOriginal ? "The original, as it was; switch to Edited to change it." : about;
    }

    /// <summary>The selection, and what of it decides which controls there are.</summary>
    private string Shape()
    {
        if (_editor is not { Editor: { } editor, DisplayedPatch: { } patch, Selected: { } part })
        {
            return string.Empty;
        }

        var shape = new List<string> { part.ToString(), editor.Profile.Name, _editor.ShowingOriginal.ToString(), string.Join(",", _editor.SelectedValues.Select(static v => v.Id)) };
        switch (part)
        {
            case PatchPart.OperatorPart { Index: var op } when (uint)op < (uint)patch.OperatorCount:
                OperatorPatch o = patch.Operators[op];
                shape.Add(FormattableString.Invariant($"{o.Enabled} {o.Waveform} {o.Frequency.Mode} {o.Filter?.Type} {o.Filter?.Envelope is not null} {o.NoisePhase != 0 || o.NoiseM != 0}"));
                break;
            case PatchPart.ConnectionPart { Source: var s, Destination: var d } when (uint)s < (uint)patch.OperatorCount && (uint)d < (uint)patch.OperatorCount:
                shape.Add(FormattableString.Invariant($"{patch.ModulationMatrix.Find(s, d) is not null}"));
                break;
            case PatchPart.PitchPart:
                shape.Add(FormattableString.Invariant($"{patch.Pitch.Vibrato is not null}"));
                break;
        }

        shape.AddRange(Envelopes(patch, part).Select(r => FormattableString.Invariant($"{r.Kind}:{EnvelopeEdits.Get(patch, r)!.Points.Count}")));
        return string.Join("|", shape);
    }

    /// <summary>The envelopes a part has.</summary>
    private static IEnumerable<EnvelopeRef> Envelopes(OperatorMatrixPatch patch, PatchPart part)
    {
        EnvelopeKind[] kinds = part switch
        {
            PatchPart.OperatorPart => [EnvelopeKind.Amplitude, EnvelopeKind.Sweep],
            PatchPart.ConnectionPart => [EnvelopeKind.Amount],
            PatchPart.PitchPart => [EnvelopeKind.Pitch],
            _ => [],
        };
        return kinds.Select(k => new EnvelopeRef(part, k)).Where(r => EnvelopeEdits.Get(patch, r) is not null);
    }

    private void Rebuild()
    {
        // Typed text first becomes its step, on the value it was typed for, before its box goes.
        foreach (NumberBox box in _boxes.ToList())
        {
            box.Commit();
        }

        _rebuildPending = false;
        _shape = Shape();
        _building = true;
        try
        {
            Body.Children.Clear();
            _boxes.Clear();
            _envelopes.Clear();
            _marks.Clear();
            Title.Text = string.Empty;
            if (_editor is { Editor: { } editor, DisplayedPatch: { } patch, Selected: { } part })
            {
                Build(patch, editor.Profile.Capabilities, part);
            }
        }
        finally
        {
            _building = false;
        }

        Refresh();
    }

    private void Build(OperatorMatrixPatch patch, SynthesisCapabilities caps, PatchPart part)
    {
        switch (part)
        {
            case PatchPart.OperatorPart { Index: var op } when (uint)op < (uint)patch.OperatorCount:
                Title.Text = "OP" + (op + 1).ToString(CultureInfo.InvariantCulture);
                BuildOperator(patch, caps, part, op);
                break;
            case PatchPart.ConnectionPart { Source: var s, Destination: var d } when (uint)s < (uint)patch.OperatorCount && (uint)d < (uint)patch.OperatorCount:
                Title.Text = s == d ? $"OP{s + 1} → OP{d + 1} (feedback)" : $"OP{s + 1} → OP{d + 1}";
                BuildConnection(patch, caps, part, s, d);
                break;
            case PatchPart.NoisePart:
                Title.Text = "NOISE";
                if (!PatchEdits.HasSharedNoise(caps))
                {
                    Note("This profile has no shared noise.");
                    return;
                }

                AddRows(_editor!.SelectedValues);
                Note("Roughen an operator (its roughness toggle) or give it a noise waveform to hear the shared noise.");
                break;
            case PatchPart.PitchPart:
                Title.Text = "PITCH";
                Toggle("PitchEnvelope", "Pitch envelope", static p => p.Pitch.PitchEnvelope is not null, on => PatchEdits.SetPitchEnvelope(on));
                Toggle("Vibrato", "Vibrato", static p => p.Pitch.Vibrato is not null, on => PatchEdits.SetVibrato(on));
                AddRows(_editor!.SelectedValues);
                AddEnvelopes(patch, part, "Pitch envelope (cents)");
                break;
            default:
                Title.Text = "Not in the patch";
                Note("This part is not in the patch (any more).");
                break;
        }
    }

    private void BuildOperator(OperatorMatrixPatch patch, SynthesisCapabilities caps, PatchPart part, int op)
    {
        OperatorPatch o = patch.Operators[op];
        if (!o.Enabled)
        {
            Note($"OP{op + 1} is not in use.");
            return;
        }

        Buttons(
            Action("AddModulator", "Add modulator", $"A new operator that modulates OP{op + 1}", () => _editor!.Apply(PatchEdits.AddModulator(op))),
            ConnectButton("ModulateTo", "Modulate…", $"Let OP{op + 1} modulate another operator, or itself", op, outgoing: true),
            ConnectButton("ModulatedBy", "Modulated by…", $"Let another operator, or OP{op + 1} itself, modulate OP{op + 1}", op, outgoing: false),
            Action("Duplicate", "Duplicate", $"A copy of OP{op + 1} in the first free operator, with all its connections", () => ApplyAndSelectAdded(PatchEdits.DuplicateOperator(op))),
            Action("Remove", "Remove", $"Remove OP{op + 1} and its connections", () => Deselect(_editor!.Apply(PatchEdits.RemoveOperator(op)))));

        IReadOnlyList<OperatorWaveform> waveforms = PatchEdits.WaveformsFor(caps);
        if (waveforms.Count > 1)
        {
            Menu("Waveform", "waveform", [.. waveforms.Select(w => (PatchDescription.WaveformName(w), (object)w))], p => p.Operators[op].Waveform, w => PatchEdits.SetWaveform(op, (OperatorWaveform)w));
        }

        if (caps.SupportsRatioFrequency && caps.SupportsFixedFrequency)
        {
            Menu("FrequencyMode", "frequency", [("ratio", OperatorFrequencyMode.Ratio), ("fixed Hz", OperatorFrequencyMode.Fixed)], p => p.Operators[op].Frequency.Mode, m => PatchEdits.SetFrequencyMode(op, (OperatorFrequencyMode)m));
        }

        static bool IsFilter(EditableValue v) => v.Id.Contains(".operatorFilter.", StringComparison.Ordinal);
        static bool IsRoughness(EditableValue v) => v.Id.EndsWith(".noisePhase", StringComparison.Ordinal) || v.Id.EndsWith(".noiseM", StringComparison.Ordinal);
        IReadOnlyList<EditableValue> values = _editor!.SelectedValues;
        AddRows(values.Where(v => !IsFilter(v) && !IsRoughness(v)));

        if (PatchEdits.HasFilters(caps))
        {
            Heading("Filter");
            (string, object)[] types = [("none", "none"), .. Enum.GetValues<OperatorFilterType>().Select(t => (FilterName(t), (object)t))];
            Menu("FilterType", "type", types, p => p.Operators[op].Filter?.Type is { } type ? type : "none", t => PatchEdits.SetFilter(op, t as OperatorFilterType?));
            bool formant = o.Filter?.Type == OperatorFilterType.Formant;
            if (formant)
            {
                FormantPresetRows(op);
            }

            if (o.Filter is not null)
            {
                Toggle("Sweep", "Sweep (an envelope moves the frequency)", p => p.Operators[op].Filter?.Envelope is not null, on => PatchEdits.SetSweep(op, on));
            }

            // F4 and F5 fold away, in their place after F3: the search never sets them, and they matter mostly for the
            // vowel presets.
            static bool IsHigherFormant(EditableValue v) => v.Id.Contains(".operatorFilter.formant4", StringComparison.Ordinal) || v.Id.Contains(".operatorFilter.formant5", StringComparison.Ordinal);
            List<EditableValue> filterValues = [.. values.Where(IsFilter)];
            int higherAt = filterValues.FindIndex(IsHigherFormant);
            AddRows(higherAt < 0 ? filterValues : filterValues.Take(higherAt));
            if (formant)
            {
                var more = new StackPanel { Spacing = 4 };
                AddRows(filterValues.Where(IsHigherFormant), more);
                var expander = new Expander
                {
                    Name = "MoreFormants",
                    Header = "more formants",
                    IsExpanded = _moreFormantsExpanded,
                    Content = more,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(6, 4),
                    FontSize = 12,
                };
                ToolTip.SetTip(expander, "F4 and F5: the vowel presets set them; the search leaves them off");
                expander.PropertyChanged += (_, e) =>
                {
                    if (e.Property == Expander.IsExpandedProperty)
                    {
                        _moreFormantsExpanded = expander.IsExpanded;
                    }
                };
                Body.Children.Add(expander);
            }

            if (higherAt >= 0)
            {
                AddRows(filterValues.Skip(higherAt).Where(v => !IsHigherFormant(v)));
            }
        }

        if (PatchEdits.HasSharedNoise(caps) && !o.Waveform.IsNoise())
        {
            Toggle("Roughness", "Roughness from the shared noise", p => p.Operators[op].NoisePhase != 0 || p.Operators[op].NoiseM != 0, on => PatchEdits.SetRoughness(op, on));
            AddRows(values.Where(IsRoughness));
        }

        AddEnvelopes(patch, part, "Amplitude envelope", "Filter sweep envelope");
    }

    private void BuildConnection(OperatorMatrixPatch patch, SynthesisCapabilities caps, PatchPart part, int s, int d)
    {
        if (patch.ModulationMatrix.Find(s, d) is not { } edge)
        {
            Note("These operators are not connected (any more).");
            return;
        }

        Buttons(Action("Disconnect", "Disconnect", "Remove this connection", () => Deselect(_editor!.Apply(PatchEdits.Disconnect(s, d)))));
        AddRows(_editor!.SelectedValues);
        if (caps.SupportsDynamicModulationMatrix || edge.AmountEnvelope is not null)
        {
            Toggle("ConnectionEnvelope", "Amount follows an envelope", p => p.ModulationMatrix.Find(s, d)?.AmountEnvelope is not null, on => PatchEdits.SetConnectionEnvelope(s, d, on));
        }

        AddEnvelopes(patch, part, "Amount envelope (× the amount)");
    }

    /// <summary>
    /// The vowel presets of a formant filter: a menu of preset sets (sung, English, Dutch; the one used last, at first
    /// sung), a menu of the set's speakers (the one used last for that set, at first its first) and a button per vowel of
    /// the set, labelled with its word, which sets all five formants of that speaker and vowel as one step and keeps the
    /// sweep. A button applies what this inspector's own menus show: another inspector (the other tab) may have chosen
    /// another set or speaker since. Under them, Voice source makes the operator a narrow pulse with many harmonics.
    /// </summary>
    private void FormantPresetRows(int op)
    {
        var sets = new ComboBox { Name = "FormantSet" };
        ToolTip.SetTip(sets, "The vowel data: sung vowels (Csound), American English (Hillenbrand et al. 1995) or Dutch (Pols et al. 1973)");
        foreach (FormantPresetSet set in FormantPresets.Sets)
        {
            var item = new ComboBoxItem { Content = set.Name, Tag = set };
            ToolTip.SetTip(item, set.Source);
            sets.Items.Add(item);
        }

        var speakers = new ComboBox { Name = "FormantSpeaker" };
        ToolTip.SetTip(speakers, "The voice or group of speakers of the vowel buttons");
        var vowels = new WrapPanel { Name = "FormantVowels", Orientation = Orientation.Horizontal, ItemSpacing = 3, LineSpacing = 3, Margin = new Thickness(0, 0, 0, 2) };

        FormantPresetSet? Shown() => sets.SelectedItem is ComboBoxItem { Tag: FormantPresetSet set } ? set : null;

        // The speakers and vowels of the set the menu shows.
        void Fill(FormantPresetSet set)
        {
            speakers.Items.Clear();
            string speaker = _lastSpeakers.GetValueOrDefault(set.Name) is { } last && set.Speakers.Contains(last) ? last : set.Speakers[0];
            foreach (string name in set.Speakers)
            {
                var item = new ComboBoxItem { Content = name, Tag = name };
                speakers.Items.Add(item);
                if (name == speaker)
                {
                    speakers.SelectedItem = item;
                }
            }

            vowels.Children.Clear();
            foreach (FormantPresetVowel vowel in set.Vowels)
            {
                var button = new Button { Name = "Vowel:" + vowel.Word, Content = vowel.Word, MinWidth = 30, Padding = new Thickness(6, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
                ToolTip.SetTip(button, set.Name == "Sung" ? $"{vowel.Ipa}: set the five formants to this sung vowel" : $"{vowel.Ipa} as in \"{vowel.Word}\": set the five formants to this vowel");
                button.Click += (_, _) =>
                {
                    if (Shown() is { } shown && speakers.SelectedItem is ComboBoxItem { Tag: string who })
                    {
                        _editor!.Apply(PatchEdits.SetFormantPreset(op, shown, who, vowel.Key));
                    }
                };
                vowels.Children.Add(button);
            }
        }

        sets.SelectedItem = sets.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is FormantPresetSet { Name: var name } && name == _lastSet) ?? sets.Items[0];
        Fill(Shown()!);
        sets.SelectionChanged += (_, _) =>
        {
            if (Shown() is { } set)
            {
                _lastSet = set.Name;
                Fill(set);
            }
        };
        speakers.SelectionChanged += (_, _) =>
        {
            if (Shown() is { } set && speakers.SelectedItem is ComboBoxItem { Tag: string speaker })
            {
                _lastSpeakers[set.Name] = speaker;
            }
        };

        AddRow(new TextBlock { Text = "preset", Classes = { "row" } }, sets, string.Empty);
        AddRow(new TextBlock { Text = "speaker", Classes = { "row" } }, speakers, string.Empty);
        Body.Children.Add(vowels);

        // A vowel needs a source with many equally strong harmonics for its formants to shape.
        Buttons(Action("VoiceSource", "Voice source", "Make this operator a vowel source: a narrow pulse (1 %) with 64 harmonics", () => _editor!.Apply(PatchEdits.SetVoiceSource(op))));
    }

    /// <summary>
    /// A button whose menu connects the selected operator to another one (or itself): as the modulator when
    /// <paramref name="outgoing"/>, else as the operator modulated. The menu is built on each click, from the patch as it
    /// is then (a menu filled while it opens shows empty).
    /// </summary>
    private Button ConnectButton(string name, string text, string tip, int op, bool outgoing)
    {
        var button = new Button { Name = name, Content = text };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) =>
        {
            if (ConnectMenu(op, outgoing) is { } menu)
            {
                menu.ShowAt(button);
            }
        };
        return button;
    }

    /// <summary>
    /// One item per operator in use, as <c>OPs → OPd</c>: those already connected are greyed, and those whose connection
    /// would not be heard (it ends in an operator that is not heard) say so. Choosing one connects at 1 rad and selects the
    /// new connection, so that its amount is at hand.
    /// </summary>
    private MenuFlyout? ConnectMenu(int op, bool outgoing)
    {
        if (_editor?.DisplayedPatch is not { } patch)
        {
            return null;
        }

        bool[] audible = patch.FindAudibleOperators();
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!patch.Operators[i].Enabled)
            {
                continue;
            }

            (int source, int destination) = outgoing ? (op, i) : (i, op);
            bool connected = patch.ModulationMatrix.Find(source, destination) is { Enabled: true };
            string note = connected ? " (connected)" : source == destination ? " (feedback)" : !audible[destination] ? " (not heard)" : string.Empty;
            var item = new MenuItem { Header = $"OP{source + 1} → OP{destination + 1}{note}", IsEnabled = !connected };
            item.Click += (_, _) =>
            {
                if (!_editor.Apply(PatchEdits.Connect(source, destination)).IsRefused)
                {
                    _editor.Selected = new PatchPart.ConnectionPart(source, destination);
                }
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Closes the inspector once the part it shows was removed.</summary>
    private void Deselect(EditOutcome outcome)
    {
        if (!outcome.IsRefused && _editor is not null)
        {
            _editor.Selected = null;
        }
    }

    /// <summary>Makes an edit and selects the operator it put in a free slot, such as a copy, so that it can be changed at once.</summary>
    /// <param name="edit">The edit.</param>
    private void ApplyAndSelectAdded(PatchEdit edit)
    {
        if (_editor is null)
        {
            return;
        }

        // The patch the edit is made on, which is the edited version even while the original is shown.
        OperatorMatrixPatch? before = null;
        EditOutcome outcome = _editor.Apply((p, c) =>
        {
            before = p;
            return edit(p, c);
        });
        if (outcome.IsRefused || before is null)
        {
            return;
        }

        for (int i = 0; i < before.OperatorCount; i++)
        {
            if (!before.Operators[i].Enabled && outcome.Patch.Operators[i].Enabled)
            {
                _editor.Selected = new PatchPart.OperatorPart(i);
                return;
            }
        }
    }

    /// <summary>One row per value: its name and box, both draggable sideways, and its unit.</summary>
    /// <param name="values">The values.</param>
    /// <param name="into">The panel the rows go into; null for the body.</param>
    private void AddRows(IEnumerable<EditableValue> values, Panel? into = null)
    {
        foreach (EditableValue value in values)
        {
            // The latest instance of the value, or, once another part is selected, the one the row was made for: typed
            // text committed then still goes to the value it was typed for.
            string id = value.Id;
            EditableValue Current() => _values.GetValueOrDefault(id) ?? value;
            var box = new NumberBox(
                _editor!,
                p => Current().Get(p),
                x => Current().Set(x),
                (from, pixels, fine, snap) => Current().Nudge(from, pixels, fine, snap))
            {
                Name = "value:" + id,
            };
            var label = new TextBlock { Name = "label:" + id, Text = value.Label, Classes = { "row", "drag" } };
            ToolTip.SetTip(label, "Drag sideways to change; Shift for finer steps" + (value.SnapPoints is null ? "." : ", Ctrl for the musical ratios."));
            box.DragWith(label);
            AddMark(box, p => Current().Get(p), static v => NumberBox.Format((double)v!));
            AddRow(label, box, value.Unit, into);
        }
    }

    private void AddRow(Control label, Control box, string unit, Panel? into = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,44"), ColumnSpacing = 4 };
        Grid.SetColumn(box, 1);
        var units = new TextBlock { Text = unit, Classes = { "row" }, FontSize = 11 };
        Grid.SetColumn(units, 2);
        row.Children.Add(label);
        row.Children.Add(box);
        row.Children.Add(units);
        (into ?? Body).Children.Add(row);
        if (box is NumberBox number)
        {
            _boxes.Add(number);
        }
    }

    /// <summary>A graph per envelope of the part, with its breakpoints as numbers under it.</summary>
    private void AddEnvelopes(OperatorMatrixPatch patch, PatchPart part, params string[] headings)
    {
        int k = 0;
        foreach (EnvelopeRef reference in Envelopes(patch, part))
        {
            OperatorEnvelope envelope = EnvelopeEdits.Get(patch, reference)!;
            Heading(reference.Kind == EnvelopeKind.Sweep && headings.Length > 1 ? headings[1] : headings[Math.Min(k, headings.Length - 1)]);
            k++;
            var editor = new EnvelopeEditor { Kind = reference.Kind, Part = part, Editor = _editor, Height = 96 };
            ToolTip.SetTip(editor, "Drag a point; double-click a line to add one; right-click a point for more");
            var frame = new Border { Child = editor, Classes = { "mark" } };
            AddMark(frame, p => EnvelopeEdits.Get(p, reference) is { } e ? HearSayJson.Serialize(e, indented: false) : null, null);
            Body.Children.Add(frame);
            _envelopes.Add(editor);
            (double low, double high) = EnvelopeEdits.LevelRange(reference.Kind);
            string unit = reference.Kind == EnvelopeKind.Pitch ? "ct" : string.Empty;
            for (int i = 0; i < envelope.Points.Count; i++)
            {
                int index = i;
                Control time = index == 0
                    ? new TextBlock { Text = "start", Classes = { "row" }, FontSize = 11 }
                    : Breakpoint(reference, index, "time", static p => p.TimeSeconds, (r, j, x, p) => EnvelopeEdits.MovePoint(r, j, x, p.Level), static (from, pixels, fine) => SquareRootNudge(from, pixels, fine));
                NumberBox level = Breakpoint(reference, index, "level", static p => p.Level, (r, j, x, p) => EnvelopeEdits.MovePoint(r, j, p.TimeSeconds, x), (from, pixels, fine) => Math.Clamp(from + (pixels / (fine ? 2000.0 : 200.0) * (high - low)), Math.Min(low, from), Math.Max(high, from)));
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,14,*,18"), ColumnSpacing = 4 };
                row.Children.Add(new TextBlock { Text = (index + 1).ToString(CultureInfo.InvariantCulture), Classes = { "row" }, FontSize = 11 });
                Grid.SetColumn(time, 1);
                row.Children.Add(time);
                var seconds = new TextBlock { Text = index == 0 ? string.Empty : "s", Classes = { "row" }, FontSize = 11 };
                Grid.SetColumn(seconds, 2);
                row.Children.Add(seconds);
                Grid.SetColumn(level, 3);
                row.Children.Add(level);
                var cents = new TextBlock { Text = unit, Classes = { "row" }, FontSize = 11 };
                Grid.SetColumn(cents, 4);
                row.Children.Add(cents);
                Body.Children.Add(row);
            }
        }
    }

    /// <summary>A box for the time or level of an envelope point.</summary>
    private NumberBox Breakpoint(EnvelopeRef reference, int index, string what, Func<EnvelopePoint, double> read, Func<EnvelopeRef, int, double, EnvelopePoint, PatchEdit> move, Func<double, double, bool, double> nudge)
    {
        EnvelopePoint? PointOf(OperatorMatrixPatch p) => EnvelopeEdits.Get(p, reference) is { } e && index < e.Points.Count ? e.Points[index] : null;
        var box = new NumberBox(
            _editor!,
            p => PointOf(p) is { } point ? read(point) : double.NaN,
            x => (p, c) => PointOf(p) is { } point ? move(reference, index, x, point)(p, c) : EditOutcome.Refused(p, "The envelope has no such point (any more)."),
            (from, pixels, fine, _) => nudge(from, pixels, fine))
        {
            Name = FormattableString.Invariant($"envelope:{reference.Kind}:{index}:{what}"),
        };
        _boxes.Add(box);
        AddMark(box, p => PointOf(p) is { } point ? read(point) : double.NaN, static v => NumberBox.Format((double)v!));
        return box;
    }

    /// <summary>A drag of a time: along its square root, as the graph shows it, up to 20 s.</summary>
    private static double SquareRootNudge(double from, double pixels, bool fine)
    {
        double root = Math.Sqrt(Math.Max(0, from)) + (pixels * (fine ? 0.0015 : 0.015));
        return Math.Min(20.0, root <= 0 ? 0 : root * root);
    }

    private void Menu(string name, string label, (string Text, object Tag)[] choices, Func<OperatorMatrixPatch, object> read, Func<object, PatchEdit> edit)
    {
        object current = read(_editor!.DisplayedPatch!);
        var menu = new ComboBox { Name = name };
        AddMark(menu, read, v => choices.FirstOrDefault(c => Equals(c.Tag, v)).Text ?? string.Empty);
        foreach ((string text, object tag) in choices)
        {
            var item = new ComboBoxItem { Content = text, Tag = tag };
            menu.Items.Add(item);
            if (Equals(tag, current))
            {
                menu.SelectedItem = item;
            }
        }

        menu.SelectionChanged += (_, _) =>
        {
            if (!_building && menu.SelectedItem is ComboBoxItem { Tag: { } tag })
            {
                Restore(_editor!.Apply(edit(tag)));
            }
        };
        AddRow(new TextBlock { Text = label, Classes = { "row" } }, menu, string.Empty);
    }

    private void Toggle(string name, string text, Func<OperatorMatrixPatch, bool> read, Func<bool, PatchEdit> edit)
    {
        var toggle = new CheckBox { Name = name, Content = text, IsChecked = read(_editor!.DisplayedPatch!) };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!_building)
            {
                Restore(_editor!.Apply(edit(toggle.IsChecked == true)));
            }
        };
        var frame = new Border { Child = toggle, Classes = { "mark" } };
        AddMark(frame, p => read(p), static v => (bool)v! ? "on" : "off");
        Body.Children.Add(frame);
    }

    /// <summary>Marks a control while the edit is shown when the value it shows differs from the original's.</summary>
    /// <param name="target">The control that gets the outline: the box or menu itself, or a frame around a toggle or graph.</param>
    /// <param name="read">Reads the value from a patch.</param>
    /// <param name="describe">The value in words for the tooltip; null for no tooltip.</param>
    private void AddMark(Control target, Func<OperatorMatrixPatch, object?> read, Func<object?, string>? describe)
        => _marks.Add(new Mark(target, read, describe));

    /// <summary>
    /// Gives the controls whose value the edit changed the "edited" class and a tooltip with the original value; none
    /// while the original is shown or nothing is edited.
    /// </summary>
    private void ShowMarks(OperatorMatrixPatch patch)
    {
        OperatorMatrixPatch? original = _editor?.EditedFrom;
        bool isNew = original is not null && _editor?.Selected is { } part && EditMarks.IsNew(original, patch, part);
        foreach (Mark mark in _marks)
        {
            object? was = original is null ? null : mark.Read(original);
            bool edited = original is not null && (isNew || !Equals(mark.Read(patch), was));
            mark.Target.Classes.Set("edited", edited);
            if (mark.Describe is { } describe)
            {
                string? tip = !edited ? null : isNew ? "New: not in the original" : $"Edited; the original is {Described(describe(was))}";
                ToolTip.SetTip(mark.Target, tip);
            }
        }

        static string Described(string text) => text.Length == 0 ? "not set" : text;
    }

    /// <summary>Puts a menu or toggle back when its edit was refused: the controls are built again from the patch.</summary>
    private void Restore(EditOutcome outcome)
    {
        if (outcome.IsRefused && !_rebuildPending)
        {
            _rebuildPending = true;
            Dispatcher.UIThread.Post(Rebuild);
        }
    }

    private static Button Action(string name, string text, string tip, Action click)
    {
        var button = new Button { Name = name, Content = text };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void Buttons(params Control[] buttons)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4, Margin = new Thickness(0, 0, 0, 4) };
        foreach (Control button in buttons)
        {
            panel.Children.Add(button);
        }

        Body.Children.Add(panel);
    }

    private void Heading(string text) => Body.Children.Add(new TextBlock { Text = text, Classes = { "heading" } });

    private void Note(string text) => Body.Children.Add(new TextBlock { Text = text, FontSize = 11, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "row" } });

    /// <summary>
    /// A control that shows a value of the patch, marked when the edit changed that value: <see cref="Read"/> takes the
    /// value from a patch, to compare the edit with the original.
    /// </summary>
    private sealed record Mark(Control Target, Func<OperatorMatrixPatch, object?> Read, Func<object?, string>? Describe);

    private static string FilterName(OperatorFilterType type) => type switch
    {
        OperatorFilterType.LowPass => "low-pass",
        OperatorFilterType.HighPass => "high-pass",
        OperatorFilterType.BandPass => "band-pass",
        OperatorFilterType.Notch => "notch",
        OperatorFilterType.Peak => "peak",
        _ => "formant",
    };

    /// <summary>A line about the part: what an operator does in the sound, what a connection is.</summary>
    private static string About(OperatorMatrixPatch patch, PatchPart part)
    {
        switch (part)
        {
            case PatchPart.OperatorPart { Index: var op } when (uint)op < (uint)patch.OperatorCount && patch.Operators[op].Enabled:
                string[] modulates = [.. patch.ModulationMatrix.Edges.Where(e => e.SourceOperator == op && e.DestinationOperator != op && e.Enabled).Select(e => $"OP{e.DestinationOperator + 1}")];
                var roles = new List<string>();
                if (patch.OutputLevels[op] > 0)
                {
                    roles.Add(string.Create(CultureInfo.InvariantCulture, $"carrier at {patch.OutputLevels[op]:0.##}"));
                }

                if (modulates.Length > 0)
                {
                    roles.Add("modulates " + string.Join(", ", modulates));
                }

                return roles.Count == 0 ? "Not heard: no output and modulates nothing." : char.ToUpperInvariant(roles[0][0]) + string.Join("; ", roles)[1..] + ".";
            case PatchPart.ConnectionPart { Source: var s, Destination: var d }:
                return s == d ? "The operator modulates its own phase, in radians." : $"OP{s + 1} modulates the phase of OP{d + 1}, in radians.";
            case PatchPart.NoisePart:
                return "The noise source the operators share.";
            case PatchPart.PitchPart:
                return "The pitch of the whole sound: an envelope and vibrato.";
            default:
                return string.Empty;
        }
    }
}

/// <summary>
/// A number of the patch in a box: typed and committed on Enter or when the box is left (text that is no number puts
/// the value back), or dragged sideways on the box or its name as one gesture, Shift finer and Ctrl snapping, Escape
/// cancelling. A click without a drag puts the box in typing mode.
/// </summary>
public sealed class NumberBox : TextBox
{
    /// <summary>Pixels the pointer moves before a press becomes a drag.</summary>
    private const double DragThreshold = 3;

    private readonly PatchEditorViewModel _editor;

    /// <summary>The edit model of the sound the box was made for.</summary>
    private readonly PatchEditor? _owner;
    private readonly Func<OperatorMatrixPatch, double> _read;
    private readonly Func<double, PatchEdit> _write;
    private readonly Func<double, double, bool, bool, double> _nudge;
    private string _shown = string.Empty;
    private Control? _pressedOn;
    private double _pressX;
    private double _from;
    private bool _dragging;
    private TopLevel? _escapeSource;
    private IPointer? _pointer;

    /// <summary>A box for one number.</summary>
    /// <param name="editor">The editing the changes go to.</param>
    /// <param name="read">Reads the number from a patch; NaN when it has none.</param>
    /// <param name="write">The edit that sets it.</param>
    /// <param name="nudge">The value a drag reaches: from the value at its start, by pixels, fine, snapping.</param>
    public NumberBox(PatchEditorViewModel editor, Func<OperatorMatrixPatch, double> read, Func<double, PatchEdit> write, Func<double, double, bool, bool, double> nudge)
    {
        _editor = editor;
        _owner = editor.Editor;
        _read = read;
        _write = write;
        _nudge = nudge;
        AddHandler(PointerPressedEvent, (_, e) => Press(this, e), RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, e) => Move(e), RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, e) => Release(e), RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => EndDrag(cancel: false);
    }

    /// <summary>True while the box holds typed text that is not committed yet; Ctrl+Z is then the box's own.</summary>
    public bool IsEditing => (Text ?? string.Empty) != _shown;

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>Lets a drag on another control, the row's name, change the number too.</summary>
    /// <param name="handle">The control.</param>
    public void DragWith(Control handle)
    {
        handle.PointerPressed += (_, e) => Press(handle, e);
        handle.PointerMoved += (_, e) => Move(e);
        handle.PointerReleased += (_, e) => Release(e);
        handle.PointerCaptureLost += (_, _) => EndDrag(cancel: false);
    }

    /// <summary>Shows the number a patch has, unless typed text is waiting to be committed.</summary>
    /// <param name="patch">The patch shown.</param>
    public void Show(OperatorMatrixPatch patch)
    {
        bool editing = IsEditing;
        _shown = Format(_read(patch));
        if (!editing)
        {
            Text = _shown;
        }
    }

    /// <summary>A number as the boxes show it: invariant, with four decimals at most, fewer for large numbers.</summary>
    /// <param name="value">The number; NaN shows as empty.</param>
    public static string Format(double value)
    {
        if (!double.IsFinite(value))
        {
            return string.Empty;
        }

        double size = Math.Abs(value);
        return value.ToString(size >= 1000 ? "0.#" : size >= 100 ? "0.##" : "0.####", CultureInfo.InvariantCulture);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Commit();
            SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && IsEditing)
        {
            Text = _shown;
            SelectAll();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        Commit();
        base.OnLostFocus(e);
    }

    /// <summary>
    /// Makes the typed number one step; text that is no number puts the shown value back. Typed text for a sound that is
    /// no longer shown is dropped: the edit would go to the sound shown now.
    /// </summary>
    public void Commit()
    {
        if (!IsEditing)
        {
            return;
        }

        string typed = Text ?? string.Empty;
        Text = _shown;
        if (double.TryParse(typed.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            && _editor.Editor is { } shown && ReferenceEquals(shown, _owner))
        {
            _editor.Apply(_write(value));
        }
    }

    private void Press(Control on, PointerPressedEventArgs e)
    {
        // In typing mode the box places the caret; its name still drags.
        if ((on == this && IsFocused) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !_editor.HasSound || !IsEffectivelyEnabled)
        {
            return;
        }

        _pressedOn = on;
        _pressX = e.GetPosition(null).X;
        _dragging = false;
        _pointer = e.Pointer;
        e.Pointer.Capture(on);
        e.Handled = true;
    }

    private void Move(PointerEventArgs e)
    {
        if (_pressedOn is null)
        {
            return;
        }

        double pixels = e.GetPosition(null).X - _pressX;
        if (!_dragging)
        {
            if (Math.Abs(pixels) < DragThreshold || _editor.DisplayedPatch is not { } patch)
            {
                return;
            }

            _from = _read(patch);
            _dragging = true;
            _editor.BeginGesture();
            _escapeSource = TopLevel.GetTopLevel(this);
            _escapeSource?.AddHandler(KeyDownEvent, OnEscape, RoutingStrategies.Tunnel);
        }
        else if (!_editor.InGesture)
        {
            // An undo, a redo or the switch ended the gesture underneath: the drag ends with it.
            EndDrag(cancel: false, gestureEnded: true);
            return;
        }

        _editor.Preview(_write(_nudge(_from, pixels, e.KeyModifiers.HasFlag(KeyModifiers.Shift), e.KeyModifiers.HasFlag(KeyModifiers.Control))));
        e.Handled = true;
    }

    private void Release(PointerReleasedEventArgs e)
    {
        if (_pressedOn is not { } on)
        {
            return;
        }

        bool clicked = !_dragging && on == this;
        EndDrag(cancel: false);
        e.Pointer.Capture(null);
        e.Handled = true;
        if (clicked)
        {
            Focus(NavigationMethod.Pointer);
            SelectAll();
        }
    }

    private void OnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragging)
        {
            EndDrag(cancel: true);
            e.Handled = true;
        }
    }

    /// <summary>Stops a press or drag: the drag's gesture becomes one step, or is cancelled, unless it already ended underneath.</summary>
    private void EndDrag(bool cancel, bool gestureEnded = false)
    {
        bool dragging = _dragging;
        _pressedOn = null;
        _dragging = false;
        _escapeSource?.RemoveHandler(KeyDownEvent, OnEscape);
        _escapeSource = null;
        IPointer? pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        if (!dragging || gestureEnded)
        {
            return;
        }

        if (cancel)
        {
            _editor.CancelGesture();
        }
        else
        {
            _editor.EndGesture();
        }
    }
}
