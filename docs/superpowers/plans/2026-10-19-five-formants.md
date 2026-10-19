# Five Formants Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The FMX6 formant filter holds five formants with their own Q; the editor sets them from vowel presets; the search uses F1–F3; a benchmark shows whether F3 in the search pays off.

**Architecture:** The fmx6 UGen (C++, `D:\Projects\fmx6`) gets optional extra formant inputs after its filter rows; HearSay's C# filter kernel mirrors it operation for operation; the patch model gains scalar fields for F2's own Q and F3–F5 that are omitted from the JSON when unused, so existing patches stay byte-identical; the search schema, priors, mutation and random generator learn F3; the editor gets rows and presets.

**Tech Stack:** C++ (SuperCollider server plugin, CMake, Visual Studio 17 2022), SuperCollider 3.14.1 sclang, .NET 10, C#, xUnit, Avalonia 11.3.22.

**Spec:** `docs/superpowers/specs/2026-10-19-five-formants-design.md` (holds the preset table).

## Global Constraints

- Every existing patch, library sound and favourite renders, plays live and exports exactly as before (bit-identical audio, identical `.scd` text, identical patch JSON and `ComputeHash`).
- New model fields, all on `OperatorFilter`, all meaningful only for `OperatorFilterType.Formant`: `FormantQ2` (0 = same as `Q`), and for n = 3, 4, 5: `Formant{n}Ratio` (default 1), `Formant{n}Level` (default 0 = off), `Formant{n}Q` (0 = same as `Q`). Serialised only when not default.
- Ranges: formant ratios (F2..F5) 1..32; formant levels 0..1; formant Qs (`Q` of a formant filter, `FormantQ2`, `Formant{n}Q`) 0.5..50 or 0 for "same as Q" where allowed; other filter types keep Q 0.5..20.
- The formant response is the sum of unity-gain band-passes: `k1·bp(F1) + L2·k2·bp(F2) + L3·k3·bp(F3) + L4·k4·bp(F4) + L5·k5·bp(F5)`, with `k = 1 / clamp(q, 0.5, 50)`, Fn = F1 × ratio_n; a peak whose level is 0 (F3–F5) is not computed and its state stays untouched. For Q ≤ 20 and F3–F5 off this is exactly today's formula.
- UGen inputs: the existing 115-input layouts stay valid; a 175-input layout appends six rows of `[q2, ratio3, level3, q3, ratio4, level4, q4, ratio5, level5, q5]` (q2/q3/q4/q5 ≤ 0 = same as q). `FMX6.ar(filters:)` accepts rows of 7 fields (old) or 17 fields `[type, freq, octaves, q, ratio2, level2, gainDb, q2, ratio3, level3, q3, ratio4, level4, q4, ratio5, level5, q5]`.
- The exporter writes 17-field rows only when an audible operator's formant filter uses `FormantQ2` or F3–F5; otherwise exactly today's text.
- The search sets F1, Q, F2 (ratio, level, own Q) and F3 (ratio, level, Q); never F4/F5.
- Presets: 5 voices × 5 vowels from the spec table. F1 Hz = F1; `Q` = F1/bw1; for n = 2..5: ratio = Fn/F1, level = 10^(dBn/20), Q = Fn/bwn, each clamped to the ranges. A preset keeps the filter's sweep (`EnvelopeOctaves`, `Envelope`) and is one undo step; on a non-formant or absent filter it makes the filter a formant filter.
- fmx6 commits go on a branch `five-formants` in `D:\Projects\fmx6`; HearSay commits on branch `formants`. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A patch saved before this change (favourite, library sound, exported `.scd`) must be unchanged in sound, JSON hash and exported text. (Task 2: `OldFormantPatch_IsByteIdentical` tests; Task 1: old 7-field SynthDef renders identically on the new plugin.)
2. A preset with Q above 20 (bass "o" F1: 400/40 = 10, tenor "i" F3: 28) must not be clamped to 20 anywhere in the chain (model validation, kernel, UGen, exporter). (Task 2 parity test uses Q 41.)
3. Toggling F3 level to 0 and back must not leave stale filter state that differs between C# and the UGen. (Task 2 parity test with an F3 level envelope is not possible; use two renders: level 0 vs a tiny level, both matching the UGen.)
4. The search with F3 must not refuse or break on profiles without filters (FM7, Nome6). (Task 3: schema has no F3 ids there.)
5. Library rebuild must not silently reuse the v3 file for FMX6. (Task 3: `StructureLibraryVersion` bumped, reuse cleared, test.)

---

### Task 1: The FMX6 UGen: five formants with own Q (fmx6 repo)

**Files:** `D:\Projects\fmx6\FMX6.cpp`, `Classes/FMX6.sc`, `HelpSource/Classes/FMX6.schelp`, `README.md`. Build `cmake --build build --config Release` (configured for Visual Studio 17 2022 with `SC_PATH` set); install by copying `build/Release/FMX6.scx` (and the class/help files) into `%LOCALAPPDATA%\SuperCollider\Extensions\FMX6UGens`, replacing the old files (no backup: old builds can be recreated from git).

**Interfaces — Produces:** the 175-input layout and 17-field `filters:` rows per Global Constraints; `operatorFilter(...)` in C++ taking per-peak q, ratio and level for F2..F5 (HearSay mirrors it in Task 2 — keep the order of floating-point operations simple and documented in a comment so it can be mirrored: per peak `svfTick(x, fc * ratio, k_n, ...)`, then the sum in the order F1, F2, F3, F4, F5).

- [ ] **Step 1:** Before changing anything, render a reference with the installed plugin via sclang NRT (`Score.recordNRT`): a 7-field formant SynthDef (F1 700 Hz, ratio 1.8, level 0.5, q 8) on a saw, 1 s; keep the WAV outside the repo (`D:\Projects\hearsay-e2e\fmx6-ref\`).
- [ ] **Step 2:** Implement the C++ change, the `.sc` class (split 17-field rows into the base rows and the extra block; 7-field rows unchanged; error message lists both forms), help and README.
- [ ] **Step 3:** Build (0 errors), back up and install.
- [ ] **Step 4:** Verify with sclang NRT: the Step 1 SynthDef renders sample-identical to the reference; a 17-field SynthDef with five formants (soprano "a" values from the spec) renders, is not silent, and its spectrum has peaks near 800, 1150 and 2900 Hz; a formant with q 41 differs from the same with q 20.
- [ ] **Step 5:** Commit on branch `five-formants` in fmx6: "Formant filter: five formants with their own Q".

### Task 2: HearSay synthesis: model, kernel, live voice, exporter, parity

**Files:** `src/HearSay.Synthesis/Patches/OperatorFilter.cs`, `src/HearSay.Synthesis/Filters/OperatorFilterKernel.cs`, `src/HearSay.Synthesis/Compilation/CompiledOperatorMatrixPatch.cs`, `ControlStream.cs`, `src/HearSay.Synthesis.Fmx6Compatible/Rendering/Fmx6Voice.cs` (and the offline FMX6 renderer path if separate), `src/HearSay.SuperCollider/FMX6/SuperColliderFmx6Exporter.cs`, `src/HearSay/PatchDescription.cs`; tests in `tests/HearSay.Synthesis.Tests`, `tests/HearSay.SuperCollider.Tests/Fmx6FilterTests.cs`, `tests/HearSay.IntegrationTests` (description, voice equivalence).

**Interfaces — Consumes:** Task 1's UGen. **Produces:** the `OperatorFilter` fields of Global Constraints with validation; `OperatorFilter.FormantAt(f1Hz, f2Ratio, f2Level, q)` unchanged; new `OperatorFilter.Formants(double f1Hz, double q, params FormantPeak[] higher)` where `public readonly record struct FormantPeak(double Ratio, double Level, double Q)` describes F2..F5 in order (Q 0 = same as q); `OperatorFilter.FormantPeaks` (`IReadOnlyList<FormantPeak>` of F2..F5 as stored, for UI and description); `OperatorFilter.MaxFormantQ = 50`, `MaxFormantRatio = 32`.

- [ ] **Step 1: Failing tests** — `OldFormantPatch_IsByteIdentical` (a patch with today's formant filter: JSON text and `ComputeHash` equal to a value captured before the change — capture it first from the unchanged code and paste the literal into the test), `Formants_ValidatesRangesAndOwnQ`, `KernelFormant_WithF3Off_IsTodaysFormula` (sample-identical to today's two-peak result for Q ≤ 20), `LiveVoice_EqualsOfflineRender_WithFiveFormants` (the existing voice-equivalence pattern), exporter tests (`Export_WithoutExtras_IsUnchanged`, `Export_WithFiveFormants_Writes17FieldRows`), `Description_ListsTheFormants`, and in `Fmx6FilterTests` an NRT parity case with five formants, own Qs and one Q of 41 (C# vs the installed UGen, at the same tolerance as the existing filter cases).
- [ ] **Step 2: Run** — fail.
- [ ] **Step 3: Implement**; JSON omits default new fields (check how `OperatorFilter` is serialised and how `ComputeHash` is computed, and keep both unchanged for old patches).
- [ ] **Step 4: Run** — pass; fast suites; `dotnet build HearSay.sln` 0 warnings.
- [ ] **Step 5: Commit** "Synthesis: five formants with their own Q, mirrored from FMX6".

### Task 3: The search learns F3

**Files:** `src/HearSay.OperatorMatrix/Parameters/PatchParameterSchema.cs`, `Priors/FilterPriors.cs`, `Priors/FormantPriorProvider.cs`, `Search/TopologyMutator.cs`, `src/HearSay.Synthesis/Patches/RandomPatchGenerator.cs`, `src/HearSay.Synthesis/Profiles/SynthesisProfile.cs` (FMX6 `StructureLibraryVersion` bumped, `StructureLibraryReusedVersion` cleared or set so that no file with old formant sounds is reused); tests in `tests/HearSay.OperatorMatrix.Tests`, `tests/HearSay.Synthesis.Tests`.

**Interfaces — Consumes:** Task 2's fields. **Produces:** schema ids `op{n}.operatorFilter.formantQ2`, `.formant3Ratio`, `.formant3Level`, `.formant3Q` (present only for formant filters; ranges per Global Constraints, log scale for ratio and Q, linear for level); `FilterPriors.Formant(analysis)` returns up to three peaks (F1 from the lowest of the strongest three, ratios and levels relative to it, own Qs from the peak widths, clamped); the generator's formant filters get F3 with level > 0 in a stated share of cases (document the share), F4/F5 never.

- [ ] **Step 1: Failing tests** — `Schema_FormantFilter_HasF3AndOwnQ2`, `Schema_Fm7AndNome6_HaveNoFormantIds`, `FormantPrior_ThreePeaks_GiveF3`, `FormantPrior_TwoPeaks_LeaveF3Off`, `Generator_FormantFilters_SometimesHaveF3_NeverF4F5`, `Fmx6LibraryVersion_IsBumped_AndDoesNotReuseOldFormantSounds`.
- [ ] **Step 2: Run** — fail. **Step 3: Implement.** **Step 4: Run** — pass; fast suites (`HearSay.OperatorMatrix.Tests` takes ~2 min).
- [ ] **Step 5: Commit** "Search: a third formant with its own Q".

### Task 3b: Fine-tuning moves filter values (added at the user's request)

**Files:** `src/HearSay.OperatorMatrix/Basins/SearchBasin.cs` (`IsSpectral` and the groups built from it), `src/HearSay.OperatorMatrix/Search/LocalRefiner.cs` (whatever selects its parameters); tests in `tests/HearSay.OperatorMatrix.Tests`.

**Why:** the optimizer never moves operator-filter values (no `.operatorFilter.*` id is spectral or an envelope shape), so cutoffs, Q, formant frequencies, levels and Qs, peak gain and sweep depth come only from priors, mutations and the library. The user: "fine tuning should probably also adjust filter values".

**Interfaces — Produces:** the operator-filter values the schema offers (`op{n}.operatorFilter.cutoffRatio`, `.fixedHz`, `.q`, `.formantRatio`, `.formantLevel`, `.formantQ2`, `.formant3Ratio`, `.formant3Level`, `.formant3Q`, `.peakGainDb`, `.envelopeOctaves` — use the schema's real ids) are tuned with the spectral group in SearchBasin and LocalRefiner; the filter's sweep envelope points follow the envelope-shape rule like other envelopes, if the schema exposes them. Profiles without filters are unaffected (no such ids).

- [ ] **Step 1: Failing tests** — `Basin_TunesOperatorFilterValues` (the active spectral set of a basin on an FMX6 patch with a low-pass and a formant filter contains their value ids), `Refiner_TunesOperatorFilterValues` (same for LocalRefiner), `Refinement_MovesACutoffTowardsTheTarget` (target rendered from a patch with a low-pass at cutoff ratio 3; start from the same patch at cutoff 6; a short refinement lowers the audio loss and moves the cutoff below 6), `Fm7Search_IsUnchanged` (an FM7 search with a fixed seed and small budget gives the same result as before — capture the hash first).
- [ ] **Step 2: Run** — fail. **Step 3: Implement.** **Step 4: Run** — pass; fast suites.
- [ ] **Step 5: Commit** "Search: fine-tuning moves operator-filter values".

The benchmark (Task 5) then compares three arms: `main`, F3 without filter tuning (commit 202bcef), and the branch head with filter tuning.

### Task 4: Editor rows and vowel presets

**Files:** create `src/HearSay/Editing/FormantPresets.cs`; modify `src/HearSay/Editing/EditableValues.cs`, `PatchEdits.cs`, `src/HearSay.Studio/PatchInspector.axaml.cs`, `docs/expert-manual.md`; tests `tests/HearSay.IntegrationTests/FormantPresetsTests.cs`, `EditableValuesTests.cs`.

**Interfaces — Consumes:** Task 2's model. **Produces:** `public enum FormantVoice { Soprano, Alto, Countertenor, Tenor, Bass }`, `public enum FormantVowel { A, E, I, O, U }`; `FormantPresets.Filter(FormantVoice, FormantVowel, OperatorFilter? keepSweepFrom) : OperatorFilter` (values per Global Constraints, the table transcribed from the spec); `PatchEdits.SetFormantPreset(int op, FormantVoice voice, FormantVowel vowel) : PatchEdit` (refused without filters on the profile); `EditableValues` rows for a formant filter: F1 Hz, Q (labelled "Q1"), F2 ratio, F2 level, F2 Q, F3 ratio, F3 level, F3 Q, F4…, F5… with ids `op{n}.operatorFilter.fixedHz`, `.q`, `.formantRatio`, `.formantLevel`, `.formantQ2`, `.formant3Ratio` … `.formant5Q` (an own-Q row shows the effective Q when it is "same as Q", and setting it stores it).
- Inspector: for a formant filter, under the filter type, a "Preset" row with a voice menu (default the last used, else soprano) and five vowel buttons a e i o u; F4 and F5 rows folded under an expander "more formants" (collapsed by default, remembered while the Studio runs).

- [ ] **Step 1: Failing tests** — `EveryPreset_IsValidAndCompatible_OnFmx6` (all 25), `Preset_FollowsTheTable` (soprano "a": F1 800, Q 10, F2 ratio 1.4375, level 10^(-6/20), Q 1150/90, …), `Preset_KeepsTheSweep`, `Preset_IsOneUndoStep`, `Preset_OnFm7_IsRefused`, `For_FormantFilter_ListsFiveFormants`.
- [ ] **Step 2: Run** — fail. **Step 3: Implement** (plus a throwaway headless harness outside the repo, `D:\Projects\hearsay-e2e\formants`: select an FMX6 formant operator, click voice "tenor" and vowel "o", check the patch and one undo step, expand "more formants", screenshot `D:\Projects\hearsay-e2e\formant-presets.png`; delete the harness after). Manual: the formant filter paragraph (five formants, own Q, presets, search uses F1–F3).
- [ ] **Step 4: Run** — pass; Studio Debug build 0 warnings.
- [ ] **Step 5: Commit** "Editor: five formant rows and vowel presets".

### Task 4b: English and Dutch speech presets (added at the user's request)

**Files:** `src/HearSay/Editing/FormantPresets.cs` (generalised), `PatchEdits.cs`, `src/HearSay.Studio/PatchInspector.axaml.cs`, `docs/expert-manual.md`; tests `tests/HearSay.IntegrationTests/FormantPresetsTests.cs`.

**Why:** the user: "in the formant presets look up and use values for both english and dutch language". Data, sources and the fill-in rules (bandwidths, F4/F5, levels from a cascade model) are in the spec's section "Speech vowel presets".

**Interfaces — Produces:** preset sets replace Task 4's single table: `public sealed record FormantPresetSet(string Name, string Source, IReadOnlyList<string> Speakers, IReadOnlyList<FormantPresetVowel> Vowels)` with `public sealed record FormantPresetVowel(string Key, string Word, string Ipa)`; `FormantPresets.Sets` = [Sung (the Csound table: soprano … bass × a e i o u), English (men, women, children × heed … heard), Dutch (men, women × poet … pit)]; `FormantPresets.Filter(FormantPresetSet set, string speaker, string vowelKey, OperatorFilter? keepSweepFrom)`; `PatchEdits.SetFormantPreset(int op, FormantPresetSet set, string speaker, string vowelKey)`. Task 4's enums and calls are migrated (keep the sung results identical; Task 4's tests keep passing, adapted to the new API).
- Inspector: the preset row gets a set menu (Sung · English · Dutch), a speaker menu for that set, and the set's vowel buttons labelled with their word (a e i o u; heed hid …; poet paat …), IPA in the tooltip, wrapping; the last set and speaker are remembered while the Studio runs.

- [ ] **Step 1: Failing tests** — `EveryPreset_OfEverySet_IsValidAndCompatible_OnFmx6` (3 + 36 + 24 presets), `SungPresets_AreUnchanged` (Task 4 values), `DutchPreset_FollowsTheMeasuredData` (men "piet": F1 294, F2/F1 = 2208/294, F3/F1 = 2766/294, F2 level 10^((5.5−25.0)/20), F3 level 10^((5.5−26.1)/20), Q1 = 294/80), `EnglishPreset_FollowsTheData` (men "heed": F1 343, F2 2323, F3 3001), `F4F5_FollowTheRules` (men F4 3500, F5 4500; a case where F3 + 300 > the default raises F4), `CascadeLevels_FallWithFrequency_AndAreCapped` (levels ≤ 1, and for a front vowel F2 level below F1).
- [ ] **Step 2: Run** — fail. **Step 3: Implement** (transcribe the means from the spec exactly; a throwaway headless harness: choose Dutch, women, "pit"; check the patch, one undo step; screenshot `D:\Projects\hearsay-e2e\formant-presets-speech.png`). Manual: the preset sets with their sources.
- [ ] **Step 4: Run** — pass; Studio Debug 0 warnings.
- [ ] **Step 5: Commit** "Editor: English and Dutch speech vowel presets".

### Task 5: Benchmark: does F3 in the search pay off?

**Files:** a throwaway harness outside the repo (`D:\Projects\hearsay-e2e\formant-bench`); results written to `D:\Projects\hearsay-e2e\formant-bench-results.md`; no repo change unless the ruling below says so.

- [ ] **Step 1:** Targets: (a) vowel-like targets made without HearSay's filter — a band-limited pulse train (or a sum of harmonics with a 1/n slope) at 130, 220 and 330 Hz, each harmonic weighted by five resonance curves from the spec table (amplitude dB and bandwidth), for soprano a, e, i, o and bass a, o, u (pick so pitches suit the voice), 1 s with a short attack and release; (b) the `target.wav` files under `demo-results/*/`.
- [ ] **Step 2:** Run each target with FMX6, budget 4000, seeds 1 and 2, the structure library on, once with `main` (a git worktree of `main` built in Debug, outside the repo folder) and once with branch `formants` (this builds the new FMX6 library the first time, ~12 min; run in the background).
- [ ] **Step 3:** Report per target: final audio loss and similarity before and after, means per group (vowel, demo), and how often a finalist uses F3.
- [ ] **Step 4: Ruling:** if the vowel group's mean audio loss is not lower after, or the demo group's is more than 2 % higher, take F3 out of the search again (schema, priors, mutation, generator back to two peaks; library version stays bumped) as a fix commit and say so; otherwise keep it.

### Task 6: Hand test (user)

- [ ] Build and run the Studio; on an FMX6 sound with a formant filter, try the presets while playing, open "more formants", undo/redo; run a search on a sung or spoken note.
