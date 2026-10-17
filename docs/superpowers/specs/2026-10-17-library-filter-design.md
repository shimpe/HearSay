# Library filter: design

Date: 2026-10-17. Status: approved in conversation, awaiting review of this document.

Builds on [the library browser](2026-10-16-library-browser-design.md).

## Goal

In the Library tab, show only the sounds that match a filter: by waveform, filter type, the sound words of the labels,
and structure. The matching sounds are grouped again into families of their own, so that every screen still holds
about forty nodes and every node matches.

Success criteria:

1. Choosing a filter shows the top level of a tree built over exactly the matching sounds, within a few seconds for
   300 000 sounds; clearing it returns to the unfiltered tree at once.
2. Every chip shows how many sounds the filter would hold with that chip added, so that empty results are visible
   before they are chosen.
3. The same filter on the same library always gives the same tree.
4. A "percussive" chip and a "percussive" label agree: both come from the same thresholds.

Out of scope: saving filters between sessions, named filter presets, filtering in the Search tab, filtering by
parameter ranges (ratios, levels, envelope times), keeping the position in the tree when the filter changes.

## The filter panel

- A **Filter ▾** button next to **Import folder…** opens a flyout with four groups of toggle chips:
  - **Waveform**: sine, triangle, saw, square, pulse, noise, sample-and-hold noise, filtered noise, elliptic,
    additive, wavetable;
  - **Filter**: none, low-pass, high-pass, band-pass, notch, peak, formant;
  - **Sound**: dark, mellow, bright, very bright; percussive, decaying, sustained, swelling; noisy;
  - **Structure**: 2, 3, 4, 5, 6 audible operators; feedback, no feedback.
- A chip is shown only when at least one sound of the unfiltered library has its value; a group with fewer than two
  shown chips is hidden. So FM7 offers no waveform and no filter group, Nome6 offers sine, elliptic and noise.
- Each chip shows the number of sounds the filter would hold with that chip's value added to its group, for a chosen
  chip the number it holds now, for example `saw 12,408`. Chips at 0 are dimmed, and can still be chosen.
- Below the breadcrumb, while a filter is set: `filter: saw or square · low-pass · percussive — 4,213 sounds` and a
  **Clear** link. Without a filter the line is hidden.

### Matching

- Within a group the chosen values are alternatives (OR); groups are combined (AND). A group without a chosen value
  does not filter.
- Waveform: a sound matches when one of its audible operators (`OperatorMatrixPatch.FindAudibleOperators`) has the
  waveform. Filter: when one of its audible operators has a filter of the type; *none* when no audible operator has a
  filter. Feedback: when an audible operator has a self-feedback edge with a non-zero amount; *no feedback* otherwise.
- Sound words: the brightness and envelope words of `SoundDescriptors`, and *noisy* by its rule (an audible noise
  waveform or shared noise roughening).
- Audible operators: the number of audible operators.

### Applying

- A change of the chips applies after 0.4 s without a further change; the latest filter wins, older builds are dropped.
- While the tree is built the graph says `grouping 4,213 sounds…`; then it shows the top level of the new tree and the
  breadcrumb restarts at `all`.
- Clearing the filter shows the unfiltered tree, which is cached, at once.
- No matching sound: the graph says `No sounds match the filter`.
- Switching profile clears the filter.
- Imported sounds are placed in the current tree, by sound as before. An import that does not match the filter itself
  is still placed, and the status line says `placed among the filtered sounds; it does not match the filter itself`.
  With no matching sound, placement says `No sounds match the filter` and does nothing else.

## Computation

### `SoundTraits`

New in `HearSay.OperatorMatrix/Priors`. A small value per library sound:

- `Waveforms`: bit mask over `OperatorWaveform` of the audible operators;
- `Filters`: bit mask over `OperatorFilterType` of the audible operators' filters, plus a bit for *none*;
- `Brightness` (`Dark`, `Mellow`, `Bright`, `VeryBright`) and `Envelope` (`Percussive`, `Decaying`, `Sustained`,
  `Swelling`);
- `Noisy`, `Feedback` (booleans), `AudibleOperators` (count).

`SoundTraits.Of(print, patch)` computes one; `SoundTraits.Of(StructureLibrary)` computes all of a library in parallel
(regenerating every patch from its seed: about a second for 300 000). The Studio computes them once per loaded
library, after loading and before the first graph (`reading sounds…`), and keeps them in memory.

`SoundDescriptors` is refactored to take its brightness word, envelope word and noisy flag from the same functions
(`Brightness`/`Envelope` classification), so labels and chips cannot drift apart. Its existing tests stay unchanged
and green.

### `SoundFilter`

New, immutable record in `HearSay.OperatorMatrix/Priors`: per group the set of chosen values (`SoundFilter.None`
chooses nothing).

- `Matches(SoundTraits)`;
- `Members(IReadOnlyList<SoundTraits>)`: indices of the matching sounds, ascending;
- `With(value)`, `Without(value)`, `IsEmpty`;
- `Count(IReadOnlyList<SoundTraits>, value)`: the number of sounds that match the filter with `value` added to its
  group (for a chosen value: the filter as it is). One pass per change computes all chips: a sound is counted for a
  value when it matches every other group and has the value.
- `Describe()`: the summary text without the count, groups in panel order, values joined with ` or `, groups with
  ` · `.

A value is one of the chip values of the four groups (a small `TraitValue` type: group plus value).

### Atlas over a subset

`LibraryAtlas.Build(StructureLibrary library, IReadOnlyList<int> members)`: an atlas whose root holds the given
members (ascending, distinct) instead of the whole library. Split, k-means, `Children` and `Locate` are unchanged, so
the same filter gives the same tree. An empty set gives a root without children. Filtered atlases are not written
to disk; the Studio keeps only the current one, besides the unfiltered atlas of `Open`.

Measured on the FMX6 library of 300 000: matching sounds with a saw 109 086, top-level split 1.5 s; with a formant
filter 47 974, 0.5 s; all sounds, 3.1 s.

## Studio

- `LibraryViewModel`: the traits of the loaded library, the filter, the chip view models (group, label, count, chosen)
  and the summary; the debounced filtered build, under the existing `_loadVersion` / `_navVersion` latest-wins rules and
  the atlas identity check; clearing on profile switch; the placement messages above.
- `LibraryView.axaml`: the Filter button with its flyout (four labelled `WrapPanel`s of toggle buttons) and the summary
  line with Clear.

## Testing

`HearSay.OperatorMatrix.Tests`:

- traits on synthetic patches: the waveform and filter of an inaudible operator do not count, those of an audible
  modulator do; *none*; noisy; feedback; number of audible operators; brightness and envelope agree with
  `SoundDescriptors`;
- filter on synthetic traits: OR within a group, AND across groups, an empty filter matches all; every chip count
  equals the number of members of the filter with that value added;
- subset atlas on the 3 000-patch FM7 library: children partition the given members; building twice gives the same
  tree; 40 or fewer members give leaves; an empty set gives a root without children; `Locate` ends in families of the
  subset.

UI: headless screenshot of the flyout and summary; a click-through by hand.
