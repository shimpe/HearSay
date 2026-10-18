# Patch Editor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Edit the shown sound in the Studio's block diagram — every value and the structure, through an inspector panel and by dragging numbers on the diagram — hear it on the next note, compare with the original, keep it as a favourite, and undo every edit.

**Architecture:** A UI-free edit model in the core library (`src/HearSay/Editing`): pure edit operations on the immutable `OperatorMatrixPatch` that always yield a patch the profile can play, value descriptors with ranges taken from the search's `PatchParameterSchema`, envelope operations, and a `PatchEditor` holding the original, the current patch and snapshot undo/redo, kept per original sound in `EditSessions`. The Studio wraps it in one `PatchEditorViewModel` per tab, which decides what the diagram, the code view and the live player show (Original ⇄ Edited); an inspector panel and an editing mode of `FmDiagram` drive it.

**Tech Stack:** .NET 10, C#, xUnit, Avalonia 11.3.22.

**Spec:** `docs/superpowers/specs/2026-10-18-patch-editor-design.md`

## Global Constraints

- Every patch an edit produces is compatible with the sound's profile (`PatchCompatibilityValidator.Instance.Validate(patch, profile.Capabilities).IsCompatible`) and valid (`patch.Validate()` empty); an edit that cannot be made so is refused with a message, never applied.
- The original patch is never changed; editing works on a copy.
- One completed change is one undo step: one drag (pointer down to up), one typed value (on commit), one menu choice, one added or removed part, one envelope point drag. Undo/Redo go back to the start of editing that sound; history lives while the Studio runs, per original patch and profile.
- The edited sound plays with the original's gain (not normalised again); a favourite made from an edit stores that gain (`favourite.json` optional field `gain`) and plays with it; favourites without it are normalised as today.
- Ratio is continuous (typed or dragged on a logarithmic scale); a modifier while dragging snaps to `PatchParameterSchema.DefaultRatios`.
- Value ranges and scales come from `PatchParameterSchema` (by parameter id) where it has the value; others from the model's own validation.
- New modulator: first disabled operator slot; sine (or, on a profile without sine, its first waveform); ratio 1; amplitude `OperatorEnvelope.Adsr(0.005, 0.3, 0.2, 0.2)`; connected to the target with amount 1 rad; output level 0; refused when no slot is free.
- Removing an operator disables it and removes its own connections (no cascade); removing the last heard carrier is refused; a silent patch is otherwise allowed.
- Adding parts uses these defaults: filter `OperatorFilter.LowPass(4)` (type changes keep the frequency where the types share it; Peak `PeakAt(1000, 6, 2)`, Formant `FormantAt(700, 1.8, 0.5, 8)`); sweep 2 octaves with `Adsr(0.005, 0.3, 0.0, 0.2)`; connection envelope `Adsr(0.005, 0.3, 0.5, 0.2)`; pitch envelope two points 0 → 0 cents over 0.1 s, one-shot; vibrato `LfoSettings(5, 10)`; roughness `NoisePhase = 0.3` (elliptic operators: `NoiseM = 0.1` as well).
- Search tab: audio loss and similarity are not recomputed; while the edited version is shown they are labelled as the original's; the routing summary and the modulation matrix follow the edit.
- Show in Search, Save…, Find in library, placing among neighbours and ☆ act on the version the Original ⇄ Edited switch shows; a favourite from an edit has origin `edited from <original origin>` and is always a new favourite.
- No change to renderers, exporters, the search or the library for unedited patches.

## Review Focus

1. Dragging a connection amount past the backend's modulation limit must stop at the limit, never refuse mid-drag nor produce an unplayable patch. (Task 3: `EdgeAmount_StopsAtTheModulationLimit`.)
2. Removing the envelope point that is the sustain or release point must leave a valid envelope with sensible indexes. (Task 3: `RemovePoint_AtTheSustainPoint_KeepsTheEnvelopeValid`.)
3. Garbage or out-of-range typed values (letters, NaN, ∞, negative times) must be clamped or ignored, never thrown. (Task 3: `SetClamped_RejectsNonNumbersAndClampsTheRest`; Task 6 harness types "abc".)
4. Looking at another finalist and coming back must find the edits and history intact; Original ⇄ Edited during a drag must not lose the drag. (Task 1: `Sessions_KeepTheEditsOfEachOriginal`, `Switch_DuringAGesture_CommitsItFirst`.)
5. Starring an edited favourite must write a new favourite and leave the original's folder untouched. (Task 4: `EditedFavourite_IsANewFavourite`.)

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/HearSay/Editing/PatchEditor.cs` (new) | `EditOutcome`, `PatchPart`, `PatchEditor` (current patch, undo/redo, gestures), `EditSessions`. |
| `src/HearSay/Editing/PatchEdits.cs` (new) | Structural edits that keep the patch playable. |
| `src/HearSay/Editing/EditableValues.cs` (new) | `EditableValue` descriptors of a part, with ranges from the schema, clamping, nudging. |
| `src/HearSay/Editing/EnvelopeEdits.cs` (new) | `EnvelopeRef` and point/sustain/curve edits. |
| `src/HearSay/Playing/Sound.cs`, `VoiceMixer.cs`, `src/HearSay/FavouriteSounds.cs` | Fixed gain; `GainAsync`; favourite gain. |
| `src/HearSay.Studio/PatchEditorViewModel.cs` (new) | Per-tab editing state: displayed patch and sound, switch, undo/redo, selection, refusals. |
| `src/HearSay.Studio/PatchInspector.axaml(.cs)`, `EnvelopeEditor.cs` (new) | Inspector panel and envelope graph. |
| `src/HearSay.Studio/FmDiagram.cs`, `DiagramEditing.cs` (new) | Editing mode: selection, greyed operators, empty PITCH/NOISE boxes, drag-to-nudge. |
| `src/HearSay.Studio/PatchView.axaml(.cs)`, `MainViewModel.cs`, `LibraryViewModel.cs`, `MainWindow.axaml`, `LibraryView.axaml` | Toolbar, wiring, labels. |
| `tests/HearSay.IntegrationTests/PatchEditorTests.cs`, `PatchEditsTests.cs`, `EditableValuesTests.cs`, `EnvelopeEditsTests.cs` (new); `VoiceMixerTests.cs`, `FavouriteSoundsTests.cs` | Tests. |

---

### Task 1: The editor: undo, redo, gestures, sessions

**Files:** Create `src/HearSay/Editing/PatchEditor.cs`; test `tests/HearSay.IntegrationTests/PatchEditorTests.cs`.

**Interfaces — Produces:**
- `public readonly record struct EditOutcome(OperatorMatrixPatch Patch, string? Refusal)` with `bool IsRefused`, `static EditOutcome Refused(OperatorMatrixPatch unchanged, string why)`, `static EditOutcome Done(OperatorMatrixPatch patch)`.
- `public abstract record PatchPart` with nested records `OperatorPart(int Index)`, `ConnectionPart(int Source, int Destination)`, `NoisePart`, `PitchPart`.
- `public delegate EditOutcome PatchEdit(OperatorMatrixPatch patch, SynthesisCapabilities capabilities);`
- `public sealed class PatchEditor` — `PatchEditor(OperatorMatrixPatch original, SynthesisProfile profile)`; `Original`, `Profile`, `Current`; `bool IsEdited` (Current's hash differs from Original's); `CanUndo`, `CanRedo`; `EditOutcome Apply(PatchEdit edit)` (one step; validates the result with `patch.Validate()` and the compatibility validator, refusing with the validator's first message); `void BeginGesture()`, `EditOutcome Preview(PatchEdit edit)` (applied to the patch as it was at `BeginGesture`, replaces `Current` without a step), `void EndGesture()` (one step if `Current` changed), `void CancelGesture()` (back to the gesture start), `bool InGesture`; `Undo()`, `Redo()` (a new step clears redo); `bool ShowingOriginal` (setting it ends an active gesture first, committing it as one step) and `OperatorMatrixPatch Displayed` (Original while `ShowingOriginal`, else Current); `event EventHandler? Changed`.
- `public sealed class EditSessions` — `PatchEditor For(OperatorMatrixPatch original, SynthesisProfile profile)` (same instance for the same profile name and `original.ComputeHash()`), thread-safe.

- [ ] **Step 1: Write the failing tests** — `Apply_ThenUndo_RestoresTheExactPatch` (hash equal before/after; `CanRedo` true; `Redo` gives the edited hash), `NewStep_ClearsRedo`, `Gesture_IsOneStep` (BeginGesture, 10 Previews, EndGesture → one Undo returns to the start), `CancelGesture_RestoresTheStart`, `UnchangedGesture_AddsNoStep`, `RefusedEdit_ChangesNothing` (an edit returning an incompatible patch, e.g. a Saw waveform on FM7: `IsRefused`, message non-empty, `Current` unchanged, no step), `Original_IsNeverChanged`, `Changed_IsRaisedOncePerApplyUndoRedo`, `Sessions_KeepTheEditsOfEachOriginal` (two originals, edit both, `For` again returns the same editors with their `Current`), `Switch_DuringAGesture_CommitsItFirst` (BeginGesture, Preview, ShowingOriginal = true → `InGesture` false, one undo step exists, `Displayed` is the original; ShowingOriginal = false → `Displayed` is the edited patch).
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter PatchEditorTests` — Expected: compile error.
- [ ] **Step 3: Implement** in `PatchEditor.cs` (snapshot stacks of patches).
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** "Editing: a patch editor with undo, redo and one step per gesture".

### Task 2: Structural edits

**Files:** Create `src/HearSay/Editing/PatchEdits.cs`; test `tests/HearSay.IntegrationTests/PatchEditsTests.cs`.

**Interfaces — Consumes:** `EditOutcome`, `PatchEdit` (Task 1). **Produces** `public static class PatchEdits`, each returning a `PatchEdit`:
`AddModulator(int target)`, `RemoveOperator(int op)`, `Connect(int source, int destination, double amount = 1.0)`, `Disconnect(int source, int destination)`, `SetWaveform(int op, OperatorWaveform waveform)`, `SetFrequencyMode(int op, OperatorFrequencyMode mode)` (ratio ↔ fixed keeping the pitch at A4 = 440 Hz: fixed Hz = 440 × ratio, ratio = Hz / 440), `SetFilter(int op, OperatorFilterType? type)` (null removes), `SetSweep(int op, bool on)`, `SetConnectionEnvelope(int source, int destination, bool on)`, `SetPitchEnvelope(bool on)`, `SetVibrato(bool on)`, `SetRoughness(int op, bool on)`; plus `static IReadOnlyList<OperatorWaveform> WaveformsFor(SynthesisCapabilities c)`, `static bool HasFilters(SynthesisCapabilities c)`, `static bool HasSharedNoise(SynthesisCapabilities c)` for the UI. Defaults and rules: Global Constraints. An edit the profile cannot make (a filter on FM7, a waveform it lacks, roughness without shared noise, a connection over the modulation limit) is refused with a message.

- [ ] **Step 1: Write the failing tests** — one per edit on a small FMX6 two-operator patch (`AddModulator_TakesTheFirstFreeSlot_WithTheDefaults`, `AddModulator_WithNoFreeSlot_IsRefused`, `RemoveOperator_RemovesItsConnectionsOnly`, `RemoveOperator_LastHeardCarrier_IsRefused`, `Connect_SelfFeedback`, `Connect_OverTheModulationLimit_IsRefused`, `SetWaveform_OnFm7_IsRefused`, `SetFrequencyMode_KeepsThePitchAtA4`, `SetFilter_AddChangeRemove`, `SetFilter_OnFm7_IsRefused`, `SetSweep_OnAndOff`, `SetConnectionEnvelope_OnAndOff`, `SetPitchEnvelope_And_SetVibrato_OnAndOff`, `SetRoughness_OnNome6_And_RefusedOnFmx6`), and `RandomEdits_AlwaysLeaveAPlayablePatch` (property test: for seeds 0 … 199 of `new RandomPatchGenerator(profile.Capabilities).Generate(seed)` for each of FM7, FMX6, Nome6, with a fixed `Random(seed)` choosing the edits, apply 20 random structural edits through a `PatchEditor`; after every step `Current` is valid and compatible, and a refusal leaves it unchanged).
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter PatchEditsTests` — Expected: compile error.
- [ ] **Step 3: Implement** `PatchEdits`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** "Editing: structural edits that keep the patch playable".

### Task 3: Values and envelopes

**Files:** Create `src/HearSay/Editing/EditableValues.cs`, `src/HearSay/Editing/EnvelopeEdits.cs`; tests `tests/HearSay.IntegrationTests/EditableValuesTests.cs`, `EnvelopeEditsTests.cs`.

**Interfaces — Consumes:** Task 1 types; `PatchParameterSchema.Build(patch, capabilities)`, `PatchParameter.Minimum/Maximum/Transform/Find(id)`, `PatchParameterSchema.DefaultRatios`. **Produces:**
- `public sealed record EditableValue(string Id, string Label, string Unit, double Minimum, double Maximum, bool Logarithmic, IReadOnlyList<double>? SnapPoints)` with `double Get(OperatorMatrixPatch p)`, `PatchEdit Set(double value)` (non-finite → refused; else clamped to [Minimum, Maximum] and, for connection amounts, to what the modulation limit leaves), `double Nudge(double from, double pixels, bool fine, bool snap)` (logarithmic or linear step of 1/200 of the range per pixel, 1/2000 when `fine`; `snap` moves to the nearest snap point).
- `public static class EditableValues` — `IReadOnlyList<EditableValue> For(OperatorMatrixPatch patch, SynthesisCapabilities c, PatchPart part)`: the values of that part that exist on it (ids follow the schema's: `op1.frequency.ratio`, `op1.frequency.fixedHz`, `op1.fineTune`, `op1.detune`, `op1.phase`, `op1.shape` / `.filter` / `.elliptic` / `.morph`, `op1.output.level`, `op1.velocity`, `op1.keyScaling.level/rate`, `op1.operatorFilter.*`, `op1.noisePhase`, `op1.noiseM`, `edge.<s>><d>.amount`, `noise.color`, `noise.cutoff`, `pitch.vibrato.*`); ranges from the schema when it has the id; ratio is continuous, logarithmic, 1/16 … 32, snap points `DefaultRatios`.
- `public enum EnvelopeKind { Amplitude, Sweep, Amount, Pitch }`; `public sealed record EnvelopeRef(PatchPart Part, EnvelopeKind Kind)`; `public static class EnvelopeEdits` — `OperatorEnvelope? Get(OperatorMatrixPatch p, EnvelopeRef r)`, `(double Min, double Max) LevelRange(EnvelopeKind k)` (Amplitude, Sweep, Amount 0 … 1; Pitch −1200 … 1200 cents), and `PatchEdit` factories `MovePoint(EnvelopeRef r, int index, double timeSeconds, double level)` (time ≥ 0, first point's time stays 0, level clamped), `AddPoint(EnvelopeRef r, int afterIndex)` (splits the segment at its middle), `RemovePoint(EnvelopeRef r, int index)` (not the first, at least 2 points remain, sustain and release indexes shifted or cleared so the envelope stays valid), `SetSustain(EnvelopeRef r, int? index)` (null = one-shot), `SetCurve(EnvelopeRef r, int index, EnvelopeCurve curve)`. Every result passes `OperatorEnvelope.Validate` or the edit is refused.

- [ ] **Step 1: Write the failing tests** — `For_Operator_ListsItsValuesWithSchemaRanges`, `For_Connection_ListsTheAmount`, `Ratio_IsContinuous_AndSnapsWhenAsked`, `SetClamped_RejectsNonNumbersAndClampsTheRest` (NaN, ∞ refused; below/above range clamped), `EdgeAmount_StopsAtTheModulationLimit` (two connections into one operator at 20 rad each on a 24 rad limit: setting one to 20 gives 4), `Nudge_FineIsTenTimesSmaller`, and in `EnvelopeEditsTests`: `MovePoint_ClampsTimeAndLevel`, `AddPoint_SplitsTheSegment`, `RemovePoint_AtTheSustainPoint_KeepsTheEnvelopeValid`, `RemovePoint_TheFirstOrDownToOne_IsRefused`, `SetSustain_NullMakesItOneShot`, `SetCurve_ChangesOneSegment`, `PitchEnvelope_LevelsAreCents`.
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter "EditableValuesTests|EnvelopeEditsTests"` — Expected: compile error.
- [ ] **Step 3: Implement** both files.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** "Editing: values with the search's ranges, and envelope edits".

### Task 4: The original's gain, and favourites that keep it

**Files:** Modify `src/HearSay/Playing/Sound.cs`, `src/HearSay/Playing/VoiceMixer.cs`, `src/HearSay/FavouriteSounds.cs`, `src/HearSay.Studio/LibraryViewModel.cs` (favourites and placed imports carry the gain into the played sound); tests in `VoiceMixerTests.cs`, `FavouriteSoundsTests.cs`.

**Interfaces — Produces:** `Sound` gains `public float? FixedGain { get; init; }` (when set, `ComputeGain()` returns it without rendering); `VoiceMixer.GainAsync(Sound sound) : Task<float>` (the cached gain, computed as `PrepareAsync` does); `Favourite` gains `float? Gain`; `FavouriteSounds.Add(string nickname, OperatorMatrixPatch patch, SynthesisProfile profile, string origin, float? gain = null)` writes `gain` into `favourite.json` when set and `Load` reads it back (missing → null); the Library plays a favourite with `FixedGain = Gain`.

- [ ] **Step 1: Write the failing tests** — `FixedGain_IsUsedWithoutRendering` (a Sound with FixedGain 0.25: the mix equals the voice × 0.25 × velocity rule), `GainAsync_IsTheGainPrepareUses`, `Favourite_KeepsItsGain` (Add with 0.3 → Load gives 0.3; Add without → null; an old favourite.json without the field → null), `EditedFavourite_IsANewFavourite` (Add the edited patch with origin `edited from <origin>` next to an existing favourite of the original: two folders, the original's files byte-identical).
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter "VoiceMixerTests|FavouriteSoundsTests"` — Expected: compile errors.
- [ ] **Step 3: Implement**; the Library's favourite path passes the gain to its Sound.
- [ ] **Step 4: Run** — Expected: PASS; `dotnet build src/HearSay.Studio -c Debug` 0 warnings.
- [ ] **Step 5: Commit** "Playing: a fixed gain for a sound, and favourites that keep it".

### Task 5: Editing in the Studio: view model, toolbar, wiring

**Files:** Create `src/HearSay.Studio/PatchEditorViewModel.cs`; modify `PatchView.axaml(.cs)`, `MainViewModel.cs`, `LibraryViewModel.cs`, `MainWindow.axaml(.cs)`, `LibraryView.axaml(.cs)`.

**Interfaces — Consumes:** Tasks 1–4. **Produces:** `PatchEditorViewModel : INotifyPropertyChanged` — ctor `(EditSessions sessions, Func<Sound, Task<float>> gainOf)`; `void Show(OperatorMatrixPatch? original, SynthesisProfile? profile, RenderRequest? gainReference, string origin)` (called whenever a tab's shown sound changes; fetches the original's gain); `OperatorMatrixPatch? DisplayedPatch` (the editor's `Displayed`); `bool ShowingOriginal` (the editor's); `bool IsEdited`; `bool CanUndo/CanRedo`; `void Undo()`, `void Redo()`; `PatchPart? Selected`; `IReadOnlyList<EditableValue> SelectedValues`; `EditOutcome Apply(PatchEdit e)`, `void BeginGesture()`, `EditOutcome Preview(PatchEdit e)`, `void EndGesture()`; `Sound? DisplayedSound` (edited: `FixedGain` = the original's gain); `string FavouriteOrigin` (`edited from <origin>` when edited); `string? LastRefusal` (for the status line); `PatchEditor? Editor`. Switching `ShowingOriginal` during a gesture ends the gesture first.
Wiring: each tab owns one, both share one `EditSessions` (created by `MainViewModel`); `ShownPatch`, the code view (`Scd`, header line `Edited in HearSay Studio.` added when edited), the live player's sound, ☆, Save…, Show in Search, Find in library and placing among neighbours use the displayed version; the Search tab's loss/similarity tiles say `(original)` while the edited version is shown, the routing summary and matrix follow the displayed patch. Toolbar in the patch panel: ↶ Undo, ↷ Redo, Original | Edited (shown only when edited), next to ☆ and Copy; Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z.

- [ ] **Step 1: Write a throwaway headless harness** (outside the repo; the Studio has no test project — the logic it relies on is tested in Tasks 1–4) that: shows a finalist, applies a ratio edit through the VM, checks the code view changed and contains the header line, plays a note through `LivePlayer` and checks the sound used is the edited patch with the original's gain, switches to Original and checks the code and diagram patch, undoes and redoes, looks at another finalist and back (edits kept), stars the edited version (new favourite with `edited from …`).
- [ ] **Step 2: Run** them — Expected: fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: PASS; Studio Debug build 0 warnings; fast suites pass.
- [ ] **Step 5: Commit** "Studio: edit the shown sound with undo, redo and Original ⇄ Edited".

### Task 6: The inspector panel and the envelope editor

**Files:** Create `src/HearSay.Studio/PatchInspector.axaml(.cs)`, `src/HearSay.Studio/EnvelopeEditor.cs`; modify `PatchView.axaml(.cs)` (the inspector sits beside the diagram in the Block diagram tab while something is selected).

**Interfaces — Consumes:** `PatchEditorViewModel` (Task 5), `EditableValues`, `PatchEdits`, `EnvelopeEdits`. **Produces:** `PatchInspector` (DataContext: the tab's `PatchEditorViewModel`): a title for the selected part; one row per `EditableValue` (label, a number box committed on Enter or focus loss with the text parsed invariantly — unparseable text restores the value — and draggable sideways with `Nudge` as one gesture, Shift fine, Ctrl snap); menus for waveform (`WaveformsFor`), frequency mode, filter type (none + the types, only when `HasFilters`); toggles for sweep, connection envelope, roughness, pitch envelope, vibrato (each shown only where it applies); buttons *Add modulator*, *Remove*, *Connect to…* (a menu of operators), *Disconnect*; an `EnvelopeEditor` per envelope of the part. `EnvelopeEditor : Control` — styled property `Envelope` (`OperatorEnvelope?`) and `Kind`; draws points and segments (time on the same square-root scale as the diagram's sketch); drag a point (one gesture: `MovePoint` previews), double-click a segment to add a point, right-click a point for Remove / Sustain here / One-shot / Curve ▸ Linear, Exponential, Step…; the breakpoint values are also listed as number rows.

- [ ] **Step 1: Write a throwaway headless harness** (outside the repo): select OP1 of an FMX6 finalist (via the VM), type `2.5` into its ratio box and press Enter → patch ratio 2.5 and one undo step; type `abc` → value unchanged, no step; drag the ratio box sideways 50 px → one step; choose waveform Square → patch changed; Add modulator → a new operator connected to OP1; drag an envelope point → one step; right-click → Sustain here; screenshot the panel.
- [ ] **Step 2: Run** — Expected: fail (no inspector).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — Expected: all checks pass; the screenshot shows the panel next to a readable diagram at 1280×900.
- [ ] **Step 5: Commit** "Studio: an inspector panel and an envelope editor for the selected part".

### Task 7: Editing on the diagram, and the manual

**Files:** Create `src/HearSay.Studio/DiagramEditing.cs` (hit-testing of parts and draggable numbers, drag state); modify `src/HearSay.Studio/FmDiagram.cs` (editing mode), `PatchView.axaml(.cs)`, `docs/expert-manual.md`.

**Interfaces — Consumes:** `PatchEditorViewModel`, `EditableValues`. **Produces:** `FmDiagram` styled properties `IsEditing` (bool), `SelectedPart` (`PatchPart?`, two-way), event `PartClicked(PatchPart?)`, and drag events carrying the `EditableValue` id: `NudgeStarted(string id)`, `Nudged(string id, double pixels, bool fine, bool snap)`, `NudgeEnded(string id)` — the view wires them to the VM's gesture (`BeginGesture` / `Preview(value.Set(value.Nudge(start, pixels, fine, snap)))` / `EndGesture`). In editing mode: clicking an operator box, a connection's amount label, the NOISE box or the PITCH box selects it (outlined); clicking empty space clears the selection; operators that are enabled but not heard are drawn greyed and selectable; the NOISE box (profiles with shared noise) and the PITCH box are always drawn, empty when unused; the numbers drawn for ratio or fixed frequency, connection amounts and output levels show a dashed underline and can be dragged sideways (Shift fine, Ctrl snap to ratios); the tooltip stays as today when not dragging. Outside editing mode nothing changes (the diagram of a search result looks as before until the user selects something — editing mode is on whenever a sound is shown).

- [ ] **Step 1: Write a throwaway headless harness**: click OP2's box → `SelectedPart` OP2 and the inspector shows OP2; drag OP1's ratio number 40 px right → ratio increased, one undo step, code view updated; Shift-drag → ten times smaller change; drag a connection amount to the right far past the limit → stops at the limit (Review Focus 1); a greyed operator appears after setting a carrier's output to 0 and can be selected; the PITCH box is present on a static patch and selecting it shows *Pitch envelope* and *Vibrato* toggles; screenshots.
- [ ] **Step 2: Run** — Expected: fail.
- [ ] **Step 3: Implement**; document editing in `docs/expert-manual.md` (Block diagram paragraph: selecting, the inspector, dragging numbers with Shift/Ctrl, envelopes, Undo/Redo and shortcuts, Original ⇄ Edited, favourites from edits keep their loudness, limits of the profile, history while the Studio runs).
- [ ] **Step 4: Run** the harness, `dotnet build HearSay.sln` (0 warnings) and the fast suites (Synthesis, Audio, Modeling, OperatorMatrix, SuperCollider, IntegrationTests filtered to the new and the playing/favourite/description tests) — Expected: all pass.
- [ ] **Step 5: Commit** "Studio: select and nudge values on the block diagram; manual".
- [ ] **Step 6: By hand (user):** explore a finalist with a controller: nudge ratios and amounts while playing, edit envelopes, add a modulator, undo/redo, compare, star.
