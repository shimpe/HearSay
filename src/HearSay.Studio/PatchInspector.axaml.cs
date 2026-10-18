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
/// or box (Shift finer, Ctrl to the musical ratios); waveform, frequency mode and filter type are menus; what a part may
/// have or not (a sweep, a connection envelope, roughness, a pitch envelope, vibrato) is a toggle; envelopes are graphs
/// with their breakpoints as numbers. Its data context is the tab's <see cref="PatchEditorViewModel"/>. While the
/// original is shown it shows the original's values and cannot be changed. Refusals go to the status line.
/// </summary>
public sealed partial class PatchInspector : UserControl
{
    private readonly List<NumberBox> _boxes = [];
    private readonly List<EnvelopeEditor> _envelopes = [];
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

        Body.IsEnabled = !_editor.ShowingOriginal;
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
                Toggle("PitchEnvelope", "Pitch envelope", patch.Pitch.PitchEnvelope is not null, on => PatchEdits.SetPitchEnvelope(on));
                Toggle("Vibrato", "Vibrato", patch.Pitch.Vibrato is not null, on => PatchEdits.SetVibrato(on));
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

        var connect = new Button { Name = "ConnectTo", Content = "Connect to…", Flyout = new MenuFlyout() };
        ToolTip.SetTip(connect, $"Let OP{op + 1} modulate another operator, or itself");
        ((MenuFlyout)connect.Flyout).Opening += (_, _) => FillConnectMenu((MenuFlyout)connect.Flyout, op);
        Buttons(
            Action("AddModulator", "Add modulator", $"A new operator that modulates OP{op + 1}", () => _editor!.Apply(PatchEdits.AddModulator(op))),
            connect,
            Action("Remove", "Remove", $"Remove OP{op + 1} and its connections", () => Deselect(_editor!.Apply(PatchEdits.RemoveOperator(op)))));

        IReadOnlyList<OperatorWaveform> waveforms = PatchEdits.WaveformsFor(caps);
        if (waveforms.Count > 1)
        {
            Menu("Waveform", "waveform", [.. waveforms.Select(w => (PatchDescription.WaveformName(w), (object)w))], o.Waveform, w => PatchEdits.SetWaveform(op, (OperatorWaveform)w));
        }

        if (caps.SupportsRatioFrequency && caps.SupportsFixedFrequency)
        {
            Menu("FrequencyMode", "frequency", [("ratio", OperatorFrequencyMode.Ratio), ("fixed Hz", OperatorFrequencyMode.Fixed)], o.Frequency.Mode, m => PatchEdits.SetFrequencyMode(op, (OperatorFrequencyMode)m));
        }

        static bool IsFilter(EditableValue v) => v.Id.Contains(".operatorFilter.", StringComparison.Ordinal);
        static bool IsRoughness(EditableValue v) => v.Id.EndsWith(".noisePhase", StringComparison.Ordinal) || v.Id.EndsWith(".noiseM", StringComparison.Ordinal);
        IReadOnlyList<EditableValue> values = _editor!.SelectedValues;
        AddRows(values.Where(v => !IsFilter(v) && !IsRoughness(v)));

        if (PatchEdits.HasFilters(caps))
        {
            Heading("Filter");
            (string, object)[] types = [("none", "none"), .. Enum.GetValues<OperatorFilterType>().Select(t => (FilterName(t), (object)t))];
            Menu("FilterType", "type", types, o.Filter?.Type is { } type ? type : "none", t => PatchEdits.SetFilter(op, t as OperatorFilterType?));
            if (o.Filter is not null)
            {
                Toggle("Sweep", "Sweep (an envelope moves the frequency)", o.Filter.Envelope is not null, on => PatchEdits.SetSweep(op, on));
            }

            AddRows(values.Where(IsFilter));
        }

        if (PatchEdits.HasSharedNoise(caps) && !o.Waveform.IsNoise())
        {
            Toggle("Roughness", "Roughness from the shared noise", o.NoisePhase != 0 || o.NoiseM != 0, on => PatchEdits.SetRoughness(op, on));
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
            Toggle("ConnectionEnvelope", "Amount follows an envelope", edge.AmountEnvelope is not null, on => PatchEdits.SetConnectionEnvelope(s, d, on));
        }

        AddEnvelopes(patch, part, "Amount envelope (× the amount)");
    }

    /// <summary>The operators the selected one can modulate; those it already modulates are greyed.</summary>
    private void FillConnectMenu(MenuFlyout flyout, int op)
    {
        flyout.Items.Clear();
        if (_editor?.DisplayedPatch is not { } patch)
        {
            return;
        }

        for (int i = 0; i < patch.OperatorCount; i++)
        {
            if (!patch.Operators[i].Enabled)
            {
                continue;
            }

            int destination = i;
            var item = new MenuItem
            {
                Header = i == op ? $"OP{i + 1} (itself: feedback)" : $"OP{i + 1}",
                IsEnabled = patch.ModulationMatrix.Find(op, i) is not { Enabled: true },
            };
            item.Click += (_, _) => _editor.Apply(PatchEdits.Connect(op, destination));
            flyout.Items.Add(item);
        }
    }

    /// <summary>Closes the inspector once the part it shows was removed.</summary>
    private void Deselect(EditOutcome outcome)
    {
        if (!outcome.IsRefused && _editor is not null)
        {
            _editor.Selected = null;
        }
    }

    /// <summary>One row per value: its name and box, both draggable sideways, and its unit.</summary>
    private void AddRows(IEnumerable<EditableValue> values)
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
            AddRow(label, box, value.Unit);
        }
    }

    private void AddRow(Control label, Control box, string unit)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,44"), ColumnSpacing = 4 };
        Grid.SetColumn(box, 1);
        var units = new TextBlock { Text = unit, Classes = { "row" }, FontSize = 11 };
        Grid.SetColumn(units, 2);
        row.Children.Add(label);
        row.Children.Add(box);
        row.Children.Add(units);
        Body.Children.Add(row);
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
            Body.Children.Add(editor);
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
        return box;
    }

    /// <summary>A drag of a time: along its square root, as the graph shows it, up to 20 s.</summary>
    private static double SquareRootNudge(double from, double pixels, bool fine)
    {
        double root = Math.Sqrt(Math.Max(0, from)) + (pixels * (fine ? 0.0015 : 0.015));
        return Math.Min(20.0, root <= 0 ? 0 : root * root);
    }

    private void Menu(string name, string label, (string Text, object Tag)[] choices, object current, Func<object, PatchEdit> edit)
    {
        var menu = new ComboBox { Name = name };
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

    private void Toggle(string name, string text, bool on, Func<bool, PatchEdit> edit)
    {
        var toggle = new CheckBox { Name = name, Content = text, IsChecked = on };
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (!_building)
            {
                Restore(_editor!.Apply(edit(toggle.IsChecked == true)));
            }
        };
        Body.Children.Add(toggle);
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
