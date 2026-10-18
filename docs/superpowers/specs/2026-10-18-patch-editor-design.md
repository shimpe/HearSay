# Editing a sound in the block diagram

Date: 2026-10-18. Status: written from the conversation and a builder's check, awaiting review.

## Intent

The user finds sounds by searching and by browsing the library, and plays them on a MIDI controller or the on-screen
keyboard. Now they want to explore the sound space around a sound they found: change a frequency ratio or an envelope
time, add a modulator to an operator, change an operator's waveform, and hear the result at once on the next note.
What they like they keep as a favourite, and they compare it with where they started. Every edit can be undone.

## Goals

- Every patch value the block diagram and its tooltips show is editable, in both tabs, on whatever the patch panel
  shows (a search finalist, a library sound, a favourite, an imported sound placed in the library):
  - per operator: frequency (ratio or fixed Hz, and switching between them), fine tune, detune, start phase, waveform
    and its own settings (pulse duty, filtered-noise response, elliptic m and morph), amplitude envelope, output level
    (0 makes it a modulator only), velocity sensitivity, key scaling (level and rate), filter (type, cutoff or fixed
    frequency, Q, formant ratio and level, peak gain) and its sweep (depth in octaves and envelope), shared-noise
    roughness (phase, elliptic m);
  - per connection: amount and its envelope; adding and removing connections, including self-feedback;
  - per patch: shared noise colour and cutoff, pitch envelope, vibrato (rate, depth, delay, fade-in);
  - structure: adding a modulator to an operator (a new operator connected to it), removing an operator.
- **Adding what a sound does not have yet, and removing it again** (the user's answer: full editing): a filter (on
  profiles with filters) and its sweep, an envelope on a connection, a pitch envelope, vibrato, shared-noise roughness
  on an operator (on profiles with shared noise). The PITCH and NOISE boxes are always shown while editing (empty when
  the patch has none) so that they can be selected.
- **Full envelope editing** (the user's answer), for every envelope (amplitude, connection amount, filter sweep,
  pitch): move points (time and level), add and remove points, move the sustain point (or make the envelope one-shot),
  and change a segment's curve (the curves the model defines). Edits that would make an envelope invalid
  (`OperatorEnvelope.Validate`) are refused or clamped.
- Two ways to edit (the user's choice, option C of the mockups):
  - an **inspector panel**: click an operator, a connection, the NOISE box or the PITCH box in the diagram, and a panel
    beside it shows all of its values as fields (numbers to type or drag, menus for waveform and filter type, envelopes
    as a small graph with draggable points and the breakpoint values as numbers), with *Add modulator* and *Remove*;
  - **drag to nudge on the diagram**: the numbers drawn on the diagram (ratio or fixed frequency, connection amounts,
    output levels) can be dragged sideways to change them while playing.
- The SuperCollider code view and the block diagram follow every edit.
- The next note played (MIDI or on-screen keyboard) uses the edited sound. Notes already held keep the sound they
  started with, as they do today.
- **Undo / Redo** of every edit, back to the start of editing that sound.
- **Original ⇄ Edited** switch: what the keyboard plays and what the diagram and code show, to compare by ear.
- **☆** keeps the edited sound as a favourite (the existing favourites), its origin reading "edited from …" followed by
  the original's origin.

## Non-goals

- Editing the SuperCollider code as text and parsing it back.
- Changing the profile of a sound (an FM7 sound stays FM7; it can be saved and imported elsewhere as today).
- Undo history kept across Studio restarts.
- Changing notes that are already sounding (no live parameter changes inside a held note).
- Editing several sounds at once, presets, randomise or morph between sounds.

## Anti-goals

- An edit that produces a patch the sound's backend cannot play, or one whose SuperCollider code differs from what the
  Studio plays. Edits stay within the profile: values outside its limits are clamped, choices it does not have are not
  offered (for example no waveform menu beyond sine for FM7; no filter for FM7 and Nome6; elliptic and noise for
  Nome6), and limits that span several values (most modulation into one operator, the profile's operator count) are
  enforced so that the result is always compatible (`PatchCompatibilityValidator`).
- Losing the original: editing works on a copy; the search result, library sound or favourite it came from is never
  changed by an edit.
- A sluggish diagram: dragging a value must feel immediate (the diagram and the inspector follow the pointer; the code
  view may update a moment later).

## Decisions

- **Loudness (my call, agreed).** The edited sound keeps the gain of the original (the gain the live player computed
  from the original's reference note), so that changing an output level or a modulation amount sounds as loud or soft
  as it really is. The gain is not normalised again after each edit.
- **Undo steps (my call, agreed).** One completed change is one step: one drag (from pointer down to up), one typed
  value (on commit), one menu choice, one added or removed operator or connection, one envelope point dragged.
- **History per sound (my call, agreed).** Each sound keeps its edited copy and its history while the Studio runs, so
  that looking at another finalist and coming back keeps the edits. A new search replaces the finalists, and with them
  their edits.
- **Profile limits (my call, agreed).** Ranges, scales (linear or logarithmic) and validity of the values follow the
  search's own parameter schema (`PatchParameterSchema`) where it covers them, so that editing and searching agree on
  what a sensible value is; values the schema does not cover get ranges from the patch model's own validation.
- **Search-tab numbers after an edit (the user's answer).** Audio loss and similarity are not recomputed against the
  target: editing does not need a target (it can start from a library sound). While the edited version is shown they
  are labelled as the original's; the routing summary and the modulation matrix, which describe the patch itself,
  follow the edit.
- **Ratio (my call).** Continuous: any ratio can be typed or dragged (logarithmic drag), not only the search's list of
  ratios; holding a modifier while dragging snaps to the musical ratios that list holds.
- **Operators no longer heard (my call).** An enabled operator that an edit leaves unheard (output 0 and no path to the
  output) stays in the editing diagram, greyed, and can be selected and edited; removing an operator removes its own
  connections only (no cascade); *Add modulator* takes the first disabled slot; a silent patch is allowed (undo brings
  the sound back), except that removing the last heard carrier is refused.
- **Favourites from edits (my call).** Every press of ☆ on an edited version that is not a favourite yet adds a new
  favourite; an edited favourite always becomes a new favourite. A favourite made from an edit keeps the gain the user
  heard (an optional field in `favourite.json`, read back when present; favourites without it are normalised as today).
- **Which sound the edits belong to (my call).** Edits are kept per original patch and profile: the same patch shown as
  a finalist, a library sound or a favourite shares its edited copy and history. Show in Search, Save…, Find in library
  and placing among neighbours act on the version the Original ⇄ Edited switch shows.
- **New modulator (my call).** *Add modulator* on an operator enables the first unused operator slot as a sine (or the
  profile's first waveform) at ratio 1, a short decaying envelope and a moderate amount (1 rad), connected to that
  operator and not to the output; refused with a message when all slots are in use.
- **Removing an operator (my call).** Disables it and removes its connections; removing the last carrier is refused.

## Constraints

- Desktop Studio only (Avalonia 11.3.22), Windows, macOS and Linux; the existing patch model (`OperatorMatrixPatch`,
  immutable records), renderers, live player (`LivePlayer`, `VoiceMixer`) and favourites are reused, not replaced.
- The edited patch must render through the existing renderers and export through the existing SuperCollider exporters
  with no change to either for unedited patches.
- The search, the library and the offline renders are unaffected.

## Left to the builder

- Where the inspector sits (beside the diagram inside the patch panel's Block diagram tab, or a panel that slides in),
  as long as the diagram stays readable at the Studio's usual window size.
- The look of the toolbar (Undo, Redo, Original ⇄ Edited, ☆) and the keyboard shortcuts (Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z
  at least).
- Drag sensitivity and fine-adjust modifiers (for example Shift for finer steps).
- How an edited sound is marked in the Search tab's finalist list and the Library's selection line.
- Whether edits show in the code view on every drag step or when the drag ends.

## Testing

- An edit model with undo/redo, tested without the UI: every kind of edit, one step each (including adding and removing
  filters, sweeps, connection envelopes, pitch envelope, vibrato, roughness, and envelope points, sustain point and
  curves); undo and redo restore the
  exact patch; a drag is one step; the original is never changed; every edited patch is compatible with its profile
  (property test over random edits on library patches of FM7, FMX6 and Nome6).
- Clamping and refusals at the profile limits (modulation into one operator, operator count, choices the profile lacks).
- The edited sound plays with the original's gain; Original ⇄ Edited switches what the live player plays.
- Saving an edited sound as a favourite writes the edited patch and the "edited from …" origin.
- UI by a headless harness and by hand: selecting operators and connections, drag-to-nudge, envelope point drags,
  undo/redo, the switch, the star.
