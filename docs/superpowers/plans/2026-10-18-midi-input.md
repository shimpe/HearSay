# MIDI Input and Held, Polyphonic Notes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Play the shown sound from a MIDI controller and from the on-screen keyboard like a synth: each note held while its key is down, released on key up, up to 16 at once.

**Architecture:** The patch compiler already evaluates every control signal block by block (`ControlRateEnvelope.Next(gateOpen)`); a new `ControlStream` exposes that one block at a time with the gate decided live, and `PatchCompiler.Compile` is rebuilt on it. Each backend renderer's per-sample kernel moves into a voice class driven by a control source, so the offline render and a live voice run the same code and cannot drift apart. A `VoiceMixer` in the core library allocates, releases, ends and mixes voices; the Studio streams it through OpenAL and feeds it from the on-screen keyboard and from OwnAudioSharp.Midi.

**Tech Stack:** .NET 10, C#, xUnit, Avalonia 11.3.22, Silk.NET.OpenAL 2.23.0 (OpenAL Soft), OwnAudioSharp.Midi 4.0.9.

**Spec:** `docs/superpowers/specs/2026-10-18-midi-input-design.md`

## Global Constraints

- MIDI library: `OwnAudioSharp.Midi` 4.0.9 (namespace `OwnAudio.Midi.IO`: `MidiPortFactory.GetInputPortNames()`, `MidiPortFactory.OpenInput(name)` → `IMidiInputPort` with `MessageReceived(MidiMessage)`, `Start()`, `Stop()`, `Dispose()`; `MidiMessage.Status/Data1/Data2/Channel/IsNoteOn/IsNoteOff`; `MidiPortFactory.PortsChanged`, `StartMonitoring()`, `StopMonitoring()`). Referenced by the Studio only.
- Audio output stays OpenAL Soft through Silk.NET.OpenAL; the one-shot source for target files and previews stays as it is.
- Note-on, note-off (a note-on with velocity 0 is a note-off) and velocity only; every channel is played; all other messages are ignored.
- Velocity: `velocity / 127`. Pitch: `440 × 2^((note − 69) / 12)` Hz, the MIDI note passed as the request's MIDI note. Live sample rate: 48 000 Hz.
- At most 16 voices; a 17th note takes the oldest voice. Stealing, dropping and the end of a voice fade over 5 ms.
- A voice ends when silent (every sample under −90 dBFS, i.e. |x| < 3.1623e-5, for 50 ms) after note-off or after a one-shot envelope has run, or at the latest at the SuperCollider export's lifetime.
- A note keeps the sound it started with; a new note uses the sound shown at that moment.
- A running search or library build keeps its full speed; notes may stutter then. Voices render on several cores; a profile that cannot keep up drops its oldest voices.
- Latency from key to sound: aim for about 20 ms; measure and report the real figure; not a hard requirement.
- Settings file: `%LOCALAPPDATA%\HearSay\studio-settings.json`; the MIDI device is remembered by name.
- The extended (experimental) renderer is not played in the Studio and gets no voice.
- Every existing test keeps passing, in particular `tests/HearSay.SuperCollider.Tests` (backend equivalence) and the renderer tests.

## Review Focus

1. A tap shorter than one control block (note-off before the first block is rendered) must still sound: attack, then release — never a stuck or silent note. (Task 2: `Voice_ReleasedBeforeTheFirstBlock_IsTheOfflineNoteOfZeroLength`; Task 5: `NoteOff_BeforeTheFirstRender_StillPlaysAndEnds`.)
2. A note-off for a note whose sound's gain is still being computed (first note of a newly shown sound) must release it, not leave it hanging. (Task 5: `NoteOff_WhileTheSoundIsNotReady_EndsTheNote`.)
3. The MIDI device disappearing, or another being chosen, while keys are held must release every note from it. (Task 5: `ReleaseAll_ReleasesEveryHeldNote`; Task 8 wires `MidiInput.DeviceLost` and a device change to it, checked by hand.)
4. Changing the shown sound while keys are held: held notes keep their sound; the next note uses the new one. (Task 5: `HeldNotes_KeepTheirSound_WhenTheSoundChanges`.)
5. Rendering slower than real time (a search running, many Nome6 voices) must not crash or hang the audio thread: oldest voices are dropped. (Task 5: `Shed_DropsTheOldestVoicesWithAFade`; Task 7 verifies the output calls it on underrun.)

---

## File Structure

| File | Responsibility |
| --- | --- |
| `src/HearSay.Synthesis/Rendering/VoiceRequest.cs` (new) | `VoiceRequest`: a note without a length. |
| `src/HearSay.Synthesis/Compilation/ControlStream.cs` (new) | The compiled patch's static parts and its control values one block at a time, gate decided by the caller. |
| `src/HearSay.Synthesis/Compilation/CompiledOperatorMatrixPatch.cs` | `PatchCompiler.Compile` rebuilt on `ControlStream`; an `IControlSource` over the compiled arrays. |
| `src/HearSay.Synthesis/Rendering/IOperatorMatrixVoice.cs` (new) | `IOperatorMatrixVoice`, `IVoiceRenderer`, `IControlSource`. |
| `src/HearSay.Synthesis.Fm7Compatible/Rendering/Fm7Voice.cs` (new) + renderer | FM7 kernel as a voice; offline render through it. |
| `src/HearSay.Synthesis.Fmx6Compatible/Rendering/Fmx6Voice.cs` (new) + renderer | Same for FMX6. |
| `src/HearSay.Synthesis.Nome6Compatible/Rendering/Nome6Voice.cs` (new) + renderer | Same for Nome6 (oversampling, decimators, shared noise). |
| `src/HearSay.Synthesis/Rendering/PatchLifetime.cs` (new) | The export's synth lifetime, shared by the SuperCollider exporter and the mixer. |
| `src/HearSay/Playing/VoiceMixer.cs`, `Sound.cs` (new) | Voices: allocation, release, ending, stealing, shedding, mixing. |
| `src/HearSay/Playing/MidiNoteDecoder.cs`, `StudioSettings.cs` (new) | Raw MIDI bytes to note events; the Studio's settings file. |
| `src/HearSay.Studio/LiveAudioOutput.cs` (new) | OpenAL streaming source fed from the mixer on its own thread. |
| `src/HearSay.Studio/MidiInput.cs` (new) | OwnAudioSharp.Midi adapter: devices, open, hot-plug, notes. |
| `src/HearSay.Studio/LivePlayer.cs` (new) | Owns mixer, output and MIDI input; routes notes to the shown sound; replaces `NotePlayer` for keyboard notes. |
| `src/HearSay.Studio/PianoKeyboard.cs`, `MainWindow.axaml(.cs)`, `LibraryView.axaml(.cs)`, view models | Hold/slide/multi-touch, lit keys, MIDI selector, routing. |
| `tests/HearSay.Synthesis.Tests/ControlStreamTests.cs` (new) | Stream equals compile. |
| `tests/HearSay.IntegrationTests/VoiceEquivalenceTests.cs`, `VoiceMixerTests.cs`, `MidiNoteDecoderTests.cs`, `StudioSettingsTests.cs` (new) | Voice equivalence per profile; mixer; decoder; settings. |

---

### Task 1: ControlStream, and the compiler on top of it

**Files:**
- Create: `src/HearSay.Synthesis/Rendering/VoiceRequest.cs`, `src/HearSay.Synthesis/Compilation/ControlStream.cs`
- Modify: `src/HearSay.Synthesis/Compilation/CompiledOperatorMatrixPatch.cs` (`PatchCompiler.Compile`, `Run`, `PitchRatio`)
- Test: `tests/HearSay.Synthesis.Tests/ControlStreamTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record VoiceRequest(double FrequencyHz, int? MidiNote, double Velocity, int SampleRate = 48000)` with `double EffectiveMidiNote` (same formula as `RenderRequest`), and `RenderRequest.ToVoiceRequest()`.
  - `public sealed class ControlStream` — `ControlStream(OperatorMatrixPatch patch, VoiceRequest request, int controlBlockSize = 64)`; static parts with the same names and types as `CompiledOperatorMatrixPatch` (`SampleRate`, `BlockSize`, `OperatorIndices`, `Waveforms`, `Phases`, `Shapes`, `Morphs`, `NoisePhases`, `NoiseMs`, `NoiseColor`, `NoiseCutoffHz`, `HarmonicLimits`, `OutputGains`, `EdgeSources`, `EdgeDestinations`, and filter statics `FilterTypes`, `FilterBaseHz`, `FilterQ`, `FormantRatios`, `FormantLevels`, `FilterGainsDb`, `HasFilters`); current values `float[] Frequencies`, `float[] Amplitudes`, `float[] EdgeAmounts`, `float[] FilterOctaves` (one entry per operator or edge); `void Advance(bool gateOpen)` computes the values of the next block; before the first `Advance` the current values are the construction values (entry 0 of the compiled arrays).
  - `PatchCompiler.Compile` unchanged in signature and output.

- [ ] **Step 1: Write the failing test** `ControlStream_StepByStep_EqualsTheCompiledArrays` (theory over patches: a sine stack; an ADSR with sustain; a two-stage decay; a fixed-frequency operator; an edge with an amount envelope; an FMX6 operator with a filter sweep; a pitch envelope; vibrato with delay and fade-in; key scaling at MIDI note 40; gate-off blocks 0, 1, 37). For each: compile with `RenderRequest(hz, midi, 0.7, noteSeconds = gateOffBlock × 64 / 48000, release 0.5 s)`, build `new ControlStream(patch, request.ToVoiceRequest())`, assert the construction values equal entry 0 of every array, then for `b` in `0..BlockCount−1` call `Advance(b < gateOffBlock)` and assert every current value equals entry `b + 1` (arrays of length 1 compare to their single value), bit for bit (`Assert.Equal(expected, actual)` on floats).
- [ ] **Step 2: Run it**: `dotnet test tests/HearSay.Synthesis.Tests --filter ControlStream` — Expected: compile error, `ControlStream` missing.
- [ ] **Step 3: Implement** `VoiceRequest`, `ToVoiceRequest` and `ControlStream` by moving the per-step evaluation out of `PatchCompiler` (`ControlRateEnvelope` runners per amplitude, edge amount, filter sweep and pitch envelope; the vibrato's depth runner always gate-open and its phase counted in blocks from 0; construction value = the first block's value, as `Run` does). Keep `ControlRateEnvelope` as it is.
- [ ] **Step 4: Run it** — Expected: PASS.
- [ ] **Step 5: Rebuild** `PatchCompiler.Compile` to fill its arrays by running a `ControlStream` with `gateOpen = b < gateOffBlock`; constant signals stay length-1 arrays as today.
- [ ] **Step 6: Run** `dotnet test tests/HearSay.Synthesis.Tests` and `dotnet test tests/HearSay.SuperCollider.Tests` — Expected: all pass (ControlStream tests still pass against the rebuilt compiler; renderer and backend-equivalence tests unchanged).
- [ ] **Step 7: Commit** "Synthesis: control signals one block at a time (ControlStream); the compiler runs on it".

### Task 2: The voice API and the FM7 voice

**Files:**
- Create: `src/HearSay.Synthesis/Rendering/IOperatorMatrixVoice.cs`, `src/HearSay.Synthesis.Fm7Compatible/Rendering/Fm7Voice.cs`
- Modify: `ControlStream.cs` (implements `IControlSource`), `Fm7CompatibleOperatorMatrixRenderer.cs` (static `Render(CompiledOperatorMatrixPatch)` and instance `Render` go through `Fm7Voice`); `CompiledOperatorMatrixPatch.cs` (an `IControlSource` over its arrays)
- Test: `tests/HearSay.IntegrationTests/VoiceEquivalenceTests.cs`

**Interfaces:**
- Consumes: `ControlStream`, `VoiceRequest` (Task 1).
- Produces:
  - `public interface IControlSource` — the static parts and current values of `ControlStream` (it implements it), plus `void Advance(bool gateOpen)`; `CompiledOperatorMatrixPatch.Controls()` returns one over the compiled arrays (their gate is already in them, so it ignores `gateOpen`).
  - `public interface IOperatorMatrixVoice` — `int SampleRate { get; }`; `bool IsReleased { get; }`; `void Release()` (the gate closes from the next block not yet started); `void Render(Span<float> output)` (any length; keeps a partly used block for the next call); `long SamplesRendered { get; }`.
  - `public interface IVoiceRenderer` — `IOperatorMatrixVoice StartVoice(OperatorMatrixPatch patch, VoiceRequest request)` (validates compatibility like `Render`, throws `PatchNotSupportedException`). Implemented by the FM7, FMX6 and Nome6 renderers.
  - Test helper in the test file: `static void AssertSameAsOffline(IOperatorMatrixRenderer renderer, OperatorMatrixPatch patch, int heldBlocks, double velocity = 0.8, int midi = 57)`: offline render with `RenderRequest(hz, midi, velocity, heldBlocks × 64 / 48000 s, 1.5 s)`; a voice rendered `heldBlocks × 64` samples, `Release()`, then the rest in chunks of 37 samples (an odd size, to cross block boundaries); assert sample-for-sample equality over the offline length.

- [ ] **Step 1: Write the failing tests** `Fm7Voice_HeldThenReleased_IsTheOfflineNote` (theory: reference patches `electric-piano`, `bell`, `brass-like`, and `bell` with a pitch envelope of −80 → +15 → 0 cents plus vibrato 5.5 Hz ±14 ct after 0.3 s fading in over 0.4 s; held blocks 1, 20, 300) and `Voice_ReleasedBeforeTheFirstBlock_IsTheOfflineNoteOfZeroLength` (FM7 `bell`, `heldBlocks = 0`, `Release()` before any `Render`).
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter VoiceEquivalence` — Expected: compile error.
- [ ] **Step 3: Implement** the interfaces and `Fm7Voice` by moving the FM7 kernel's state (phases, frequency/amplitude/modulation ramps, `mem`, the construction sample) into the voice; one block per `Advance`. The static `Render(CompiledOperatorMatrixPatch)` becomes a voice over `compiled.Controls()` rendered to `SampleCount`.
- [ ] **Step 4: Run** the equivalence tests and `dotnet test tests/HearSay.Synthesis.Tests`, `dotnet test tests/HearSay.SuperCollider.Tests` — Expected: all pass.
- [ ] **Step 5: Commit** "Synthesis: a live voice for FM7, the same kernel as the offline render".

### Task 3: The FMX6 voice

**Files:** Create `src/HearSay.Synthesis.Fmx6Compatible/Rendering/Fmx6Voice.cs`; modify `Fmx6CompatibleOperatorMatrixRenderer.cs`; test in `VoiceEquivalenceTests.cs`.

**Interfaces:** Consumes Task 2's `IControlSource`, `IOperatorMatrixVoice`, `IVoiceRenderer`, `AssertSameAsOffline`.

- [ ] **Step 1: Write the failing test** `Fmx6Voice_HeldThenReleased_IsTheOfflineNote` (theory: an FMX6 patch per waveform family — saw with a low-pass filter sweep up, pulse with an edge amount envelope, filtered noise, sample-and-hold noise; and the same filter sweep with `EnvelopeOctaves` negative; held blocks 0, 20, 300).
- [ ] **Step 2: Run** — Expected: FAIL (`StartVoice` missing on the FMX6 renderer).
- [ ] **Step 3: Implement** `Fmx6Voice` from the FMX6 kernel (phase cycles, sample-and-hold, filter states and filter frequency ramps, noise generator state); the offline render goes through it.
- [ ] **Step 4: Run** equivalence, Synthesis and SuperCollider tests — Expected: all pass.
- [ ] **Step 5: Commit** "Synthesis: a live voice for FMX6".

### Task 4: The Nome6 voice

**Files:** Create `src/HearSay.Synthesis.Nome6Compatible/Rendering/Nome6Voice.cs`; modify `Nome6CompatibleOperatorMatrixRenderer.cs`; test in `VoiceEquivalenceTests.cs`.

**Interfaces:** As Task 3.

- [ ] **Step 1: Write the failing test** `Nome6Voice_HeldThenReleased_IsTheOfflineNote` (theory: an elliptic stack, an elliptic operator roughened by the shared noise in phase and in m with a coloured noise, a noise operator; held blocks 0, 20, 300). Each voice starts its shared noise from the same seed as the offline render, so equality is exact.
- [ ] **Step 2: Run** — Expected: FAIL.
- [ ] **Step 3: Implement** `Nome6Voice` (2× oversampling, `HalfbandDecimator` per operator, `SharedNoiseSource`, prepared elliptic coefficients); the offline render goes through it.
- [ ] **Step 4: Run** equivalence, Synthesis and SuperCollider tests — Expected: all pass. Also time one second of a 6-operator Nome6 voice in the test output (`ITestOutputHelper`) for the record.
- [ ] **Step 5: Commit** "Synthesis: a live voice for Nome6".

### Task 5: The voice mixer

**Files:**
- Create: `src/HearSay.Synthesis/Rendering/PatchLifetime.cs`, `src/HearSay/Playing/Sound.cs`, `src/HearSay/Playing/VoiceMixer.cs`
- Modify: `src/HearSay.SuperCollider/SuperColliderOperatorMatrixExporter.cs` (`Lifetime` uses `PatchLifetime`)
- Test: `tests/HearSay.IntegrationTests/VoiceMixerTests.cs`

**Interfaces:**
- Consumes: `IVoiceRenderer`, `IOperatorMatrixVoice`, `VoiceRequest`; `SynthesisBackends.RendererFor(profile)` (cast to `IVoiceRenderer`).
- Produces:
  - `public static class PatchLifetime` — `double? SecondsAfterGateOff(OperatorMatrixPatch patch)` (sustaining patch: `max(release, oneShot) + 0.05`, null when it has no sustaining carrier) and `double SecondsAfterStart(OperatorMatrixPatch patch)` (one-shot patch: `oneShot + 0.05`); the same audible-carrier loop and stretch `max(1, 2^(|RatePerOctave| × 4))` the exporter uses today. The exporter's SynthDef text stays byte-identical.
  - `public sealed record Sound(OperatorMatrixPatch Patch, SynthesisProfile Profile, RenderRequest GainReference)` — the gain is computed as `NotePlayer` does today: `0.5 / peak` of the full-velocity reference render.
  - `public sealed class VoiceMixer` — `VoiceMixer(int sampleRate = 48000, int maxVoices = 16)`; `Task PrepareAsync(Sound sound)` (computes and caches the gain, off the calling thread); `void NoteOn(int note, double velocity, Sound sound, object source)` (`source` tells MIDI from mouse, for `ReleaseAll(source)`); `void NoteOff(int note, object source)`; `void ReleaseAll(object source)`; `void Render(Span<float> output)` (mixes; thread-safe against the note methods); `void Shed(int voices)` (drops the oldest with the 5 ms fade); `int ActiveVoices { get; }`; `IReadOnlySet<int> SoundingNotes { get; }`; `event Action? SoundingNotesChanged`.
  - Rules (spec): velocity scaling and the no-velocity-sensitivity volume rule as in `NotePlayer.PlayAsync`; 16 voices, oldest stolen with a 5 ms fade; retriggering a held note releases the old voice; stray note-offs ignored; a note-on whose sound's gain is not ready yet starts as soon as it is, and a note-off before then cancels it after its attack has started (the note sounds and is released, never dropped silently); a voice ends on silence (|x| < 3.1623e-5 for 50 ms) after release or after `PatchLifetime.SecondsAfterStart` for a one-shot patch, or at the lifetime cap with the 5 ms fade; voices render in parallel (`Parallel.For` over voices per call) into per-voice buffers that are summed; the sum passes `tanh` soft limiting above 0.9 so one voice is unchanged at today's levels (peak 0.5).

- [ ] **Step 1: Write the failing tests:**
  - `PatchLifetime_MatchesTheExporter` (for 30 random FMX6 library patches, the exporter's SynthDef contains `Number(SecondsAfterGateOff)` or `Number(SecondsAfterStart)` as before).
  - `TwoNotes_SoundTogether` (two note-ons, render 4800 samples: the mix equals the sum of two separately rendered voices at their gains, within 1e-6 when under the limiter threshold).
  - `SeventeenthNote_TakesTheOldestVoice` (17 note-ons: `ActiveVoices` 17 during the 5 ms fade, then 16; the first note is not in `SoundingNotes` after it).
  - `Retrigger_ReleasesTheHeldVoice`, `StrayNoteOff_IsIgnored`.
  - `NoteOff_BeforeTheFirstRender_StillPlaysAndEnds` (note-on, note-off, render: non-silent output, then `ActiveVoices` 0 within the lifetime).
  - `NoteOff_WhileTheSoundIsNotReady_EndsTheNote` (note-on without `PrepareAsync`, note-off, await the gain, render: it sounds and ends).
  - `HeldNotes_KeepTheirSound_WhenTheSoundChanges` (note 60 on sound A, note 64 on sound B: note 60's output equals A's voice alone).
  - `ReleaseAll_ReleasesEveryHeldNote` (only notes of that source).
  - `OneShot_EndsAfterItsEnvelope`, `Sustaining_HoldsUntilNoteOff` (a sustaining patch still sounds after 3 s held), `NonZeroRelease_EndsAtTheLifetimeWithAFade` (a patch whose release ends at level 0.3: the voice ends at `SecondsAfterGateOff` and the last 240 samples fall to zero).
  - `Shed_DropsTheOldestVoicesWithAFade`.
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter "VoiceMixer|PatchLifetime"` — Expected: compile errors.
- [ ] **Step 3: Implement** `PatchLifetime` (exporter switched to it), `Sound`, `VoiceMixer`.
- [ ] **Step 4: Run** the new tests and `dotnet test tests/HearSay.SuperCollider.Tests` — Expected: all pass.
- [ ] **Step 5: Commit** "Playing: a voice mixer for held, polyphonic notes".

### Task 6: MIDI notes and settings

**Files:**
- Create: `src/HearSay/Playing/MidiNoteDecoder.cs`, `src/HearSay/Playing/StudioSettings.cs`, `src/HearSay.Studio/MidiInput.cs`
- Modify: `src/HearSay.Studio/HearSay.Studio.csproj` (`<PackageReference Include="OwnAudioSharp.Midi" Version="4.0.9" />`)
- Test: `tests/HearSay.IntegrationTests/MidiNoteDecoderTests.cs`, `StudioSettingsTests.cs`

**Interfaces:**
- Produces:
  - `public readonly record struct NoteEvent(bool IsOn, int Note, double Velocity)`; `public static class MidiNoteDecoder` — `NoteEvent? Decode(byte status, byte data1, byte data2)`: `0x9n` with data2 > 0 → on, velocity `data2 / 127.0`; `0x8n`, or `0x9n` with data2 = 0 → off; anything else → null; any channel.
  - `public sealed record StudioSettings(string? MidiInput)` with `static StudioSettings Load(string? path = null)` (missing or unreadable file → defaults) and `void Save(string? path = null)`; default path `%LOCALAPPDATA%\HearSay\studio-settings.json`.
  - Studio `MidiInput : IDisposable` — `static IReadOnlyList<string> DeviceNames()`; `bool Open(string name)` (false and no exception when it cannot be opened, e.g. unplugged or unsupported); `void Close()`; `string? OpenDevice { get; }`; `event Action<NoteEvent>? Note` (raised on the library's background thread); `event Action? DevicesChanged` (from `PortsChanged`); `event Action? DeviceLost` (the open device is no longer in `DeviceNames()` after a change).

- [ ] **Step 1: Write the failing tests:** `Decode_NoteOn_NoteOff_AndZeroVelocity` (`0x90 60 100` → on 60 at 100/127; `0x80 60 64` → off; `0x93 60 0` → off; channel 14 `0x9E` → on), `Decode_IgnoresEverythingElse` (`0xB0` CC, `0xE0` pitch bend, `0xC0`, `0xF8` clock → null); `Settings_SaveAndLoad`, `Settings_MissingOrBrokenFile_GivesDefaults`.
- [ ] **Step 2: Run** `dotnet test tests/HearSay.IntegrationTests --filter "MidiNoteDecoder|StudioSettings"` — Expected: compile errors.
- [ ] **Step 3: Implement** decoder, settings and the `MidiInput` adapter (decodes `MidiMessage.Status/Data1/Data2` with `MidiNoteDecoder`).
- [ ] **Step 4: Run** — Expected: PASS; `dotnet build src/HearSay.Studio` with 0 warnings.
- [ ] **Step 5: Commit** "MIDI input: note events from OwnAudioSharp.Midi, and Studio settings".

### Task 7: Streaming audio output

**Files:** Create `src/HearSay.Studio/LiveAudioOutput.cs`; modify `src/HearSay.Studio/AudioPlayer.cs` only to share its OpenAL device and context (one device, two sources).

**Interfaces:**
- Consumes: `VoiceMixer.Render`, `VoiceMixer.Shed`, `VoiceMixer.ActiveVoices`.
- Produces: `LiveAudioOutput(AudioPlayer device, VoiceMixer mixer)` : `IDisposable` — a streaming OpenAL source with 4 queued buffers of 256 mono 16-bit samples at 48 kHz, refilled on a dedicated above-normal-priority thread; the OpenAL context created with a small refresh period (`ALC_REFRESH` 48000 / 256); while no voice sounds it idles (no CPU spent rendering silence beyond one buffer); when a buffer is not ready in time (the source stopped or fewer than 2 buffers queued) it calls `mixer.Shed(1)` and restarts the source; `double MeasuredLatencyMilliseconds { get; }` from the queued buffers plus OpenAL's own reported latency (`AL_SOFT_source_latency` when available).

- [ ] **Step 1: Write a smoke check** (headless console in a temporary folder outside the repo, not committed): start `LiveAudioOutput`, `NoteOn` a FMX6 library sound, hold 1 s, `NoteOff`, wait for the voice to end; print `MeasuredLatencyMilliseconds`, underruns and CPU use; repeat with 16 Nome6 notes.
- [ ] **Step 2: Run it** — Expected before implementation: does not compile.
- [ ] **Step 3: Implement** `LiveAudioOutput`.
- [ ] **Step 4: Run it** — Expected: the note plays and ends; latency printed (goal ≈ 20 ms); 16 Nome6 notes either play or shed voices without an exception. Record the figures in the commit message.
- [ ] **Step 5: Commit** "Studio: streaming audio output for live notes (latency N ms on this machine)".

### Task 8: The Studio plays like a synth

**Files:**
- Create: `src/HearSay.Studio/LivePlayer.cs`
- Modify: `PianoKeyboard.cs` (pointer press/hold/release per pointer id; sliding; `SoundingNotes` drawn lit), `MainViewModel.cs`, `LibraryViewModel.cs`, `MainWindow.axaml(.cs)`, `LibraryView.axaml(.cs)`, `docs/expert-manual.md`, `README.md` (dependency list)

**Interfaces:**
- Consumes: everything above.
- Produces: `LivePlayer` (one per window): `VoiceMixer`, `LiveAudioOutput`, `MidiInput`; `Func<Sound?> CurrentSound` set by `MainViewModel` to the visible tab's sound; `NoteOn(int note, double velocity, object source)`, `NoteOff(int note, object source)`; `IReadOnlyList<string> MidiDevices`, `string? SelectedMidiDevice` (bound to the header selector; *none* first; refreshed when the list opens and on `DevicesChanged`); persisted through `StudioSettings`; `DeviceLost` → `ReleaseAll(midi)`, selector to *none*, status line "The MIDI device … was disconnected." `PianoKeyboard`: `event EventHandler<KeyPress> NotePressed` stays, `NoteReleased` becomes `EventHandler<int>` (the note), a second pointer holds a second note, moving a held pointer to another key releases the old note and presses the new one; `SoundingNotes` (`IReadOnlyCollection<int>?`) lights keys.

- [ ] **Step 1: Write the failing headless check** (temporary harness outside the repo, see `[[studio-headless-checks]]` memory: Avalonia.Headless, Debug build): load the FMX6 library, click a node; press a key with `window.MouseDown`, hold 500 ms, release; assert the mixer had one voice that is released after the mouse up and that the key was lit while held; press, slide to the next key, release: two notes, the first released at the slide; select a MIDI device by name through the view model when one exists (skip when `DeviceNames()` is empty) and check the settings file has it.
- [ ] **Step 2: Run** — Expected: FAIL (no `LivePlayer`).
- [ ] **Step 3: Implement** `LivePlayer`, the keyboard changes, the header selector ("MIDI in", with *none*), the routing (`CurrentSound`: Search tab → the shown finalist with `result.Request` as gain reference, or the shown library sound; Library tab → the selected sound with `StructureLibrary.ReferenceRequest`), `PrepareAsync` whenever the shown sound changes, and the Library node click as a note held for `StructureLibrary.ReferenceRequest.NoteDuration` then released. `NotePlayer` keeps only target-file and preview playback.
- [ ] **Step 4: Run** the harness, `dotnet build HearSay.sln` (0 warnings) and the fast suites (`Synthesis`, `Audio`, `Modeling`, `OperatorMatrix`, `SuperCollider`, and IntegrationTests filtered to the new and description tests) — Expected: all pass.
- [ ] **Step 5: Document** in `docs/expert-manual.md` (keyboard: hold to sustain, slide, several touches, lit keys; "MIDI in" selector, remembered device, note-on/off/velocity only, all channels, Android/iOS not supported by the Studio; a search slows notes down) and add OwnAudioSharp.Midi to the README's dependency list.
- [ ] **Step 6: Commit** "Studio: play sounds like a synth from a MIDI controller or the on-screen keyboard".
- [ ] **Step 7: By hand (user):** a real controller on Windows: chords held and released, fast repeated notes, no stuck notes, latency by ear.
