# Playing sounds like a synth: MIDI input and held, polyphonic notes

Date: 2026-10-18. Status: written from the conversation and a builder's check, awaiting review.

## Intent

The Studio auditions a sound by playing one fixed-length note per click on its on-screen keyboard, one note at a time.
The user wants to play sounds the way a synth is played: from a MIDI controller and from the on-screen keyboard, each
note sounding for as long as its key is held and released when the key is let go, several notes at once. This is for
judging sounds (search finalists, library sounds, favourites) by playing them, not for recording or sequencing.

## Goals

- Choose a MIDI input device in the Studio and play the sound the on-screen keyboard of the visible tab plays.
- Note-on, note-off and velocity only. Other MIDI messages are ignored.
- A note sounds while its key is held: an envelope with a sustain point holds at its sustain level until note-off,
  then plays its release. A percussive envelope (no sustain point) runs its course whatever the key does.
- Polyphony: several notes sound together.
- The on-screen keyboard behaves the same way: mouse button (or touch) down is note-on, up is note-off.

## Non-goals

- Pitch bend, modulation wheel, sustain pedal, aftertouch, program change, MIDI channels (all channels are played),
  MIDI output, MIDI files, recording.
- A computer-keyboard (QWERTY) piano.
- Running the Studio on Android or iOS.

## Anti-goals

- A held note that sounds different from the offline render of the same note: what is played must be what the search
  matched and what the SuperCollider export plays (see *Sound*).
- Clicks, stuck notes or audio drop-outs while playing; a note that never stops is worse than a missing one.
- Breaking the existing one-shot playback (target files, previews) or the search.

## Constraints and decisions

### MIDI library (decided by the user, checked)

`OwnAudioSharp.Midi` (NuGet, MIT, 4.0.9 of October 2026, no other dependencies): input port enumeration
(`MidiPortFactory.GetInputPortNames()`), `MessageReceived` with `IsNoteOn` / `IsNoteOff` (a note-on with velocity 0 is a
note-off) and the velocity in `Data2`, and `PortsChanged` for devices added or removed. Live input works on Windows
(WinMM), macOS (CoreMIDI) and Linux (ALSA); iOS is implemented but not validated on devices; Android has no live
input (opening a port throws). The Studio runs on Windows, macOS and Linux, so that is covered. Windows hot-plug
detection is not reliable in this library, so the device list is also refreshed whenever it is opened.

### Audio output

Keep the Studio's OpenAL Soft output (`Silk.NET.OpenAL`). A new streaming source plays the mixed voices through a
queue of small buffers; the existing one-shot source stays for target files and previews. The two may sound at the
same time. Latency from key to sound: aim for about 20 ms (a few buffers of 256 samples at 48 kHz, and OpenAL Soft's
own period set small through its context attributes); a goal, not a hard requirement. The real figure on Windows is
measured and reported; OpenAL Soft stays unless it is far off. *(Builder's-check answer; my call.)*

### Sound

- Which sound: the one the on-screen keyboard of the visible tab plays (the shown finalist or library sound in the
  Search tab, the last clicked sound in the Library tab). A note keeps the sound it started with until it ends; a new
  note uses the sound shown at that moment.
- Pitch: the MIDI note number, as the on-screen keyboard does now (A4 = 440 Hz). Velocity: `velocity / 127` as the
  request velocity; a patch without velocity sensitivity gets it as volume, as now.
- Level: one gain per patch from its reference note at full velocity, as now (`NotePlayer`), so that differences
  between keys and velocities survive. It is computed when a sound becomes the one played, off the interface thread;
  a note that arrives before it is ready may wait for it but must not be dropped.
- Equivalence: a voice held for D seconds and then released must produce the same samples as the existing offline
  renderer for that patch with a note of duration D (gate closing in the same control block), up to the end of its
  release, for every profile (FM7, FMX6, Nome6, and the extended renderer if it plays in the Studio). Tests must pin
  this. Nome6's shared noise is random; it must match in level and colour, not sample for sample.
- How the renderers become incremental (control signals evaluated block by block as the voice runs, a gate that closes
  when note-off arrives) is left to the builder, provided the offline renderers and every existing test, including
  the SuperCollider equivalence tests, keep passing.

### Voices

- Up to 16 voices. A 17th note takes the oldest voice, which fades out over a few milliseconds instead of stopping
  dead.
- A voice ends when its sound has: silent (under -90 dBFS for 50 ms) after note-off or after a percussive envelope has
  run, or at the latest when the SuperCollider export would free its synth (`SuperColliderOperatorMatrixExporter`
  lifetime: `max(release, one-shot) × stretch + 0.05 s` after the gate closes for a sustaining patch,
  `one-shot × stretch + 0.05 s` after the start for a one-shot one). There it fades out over 5 ms instead of the
  export's cut, so that an envelope that stops at a non-zero level (the search may find those) neither hangs nor clicks.
  *(Builder's-check answer; my call.)*
- The same note played again while still held: a new voice; the old one is released.
- A note-off with no note held for it is ignored. When the device is closed or changed, or the window loses the
  device, every voice from it is released.
- Mixing: voices are summed; a soft limiter or a fixed headroom keeps sixteen loud voices from clipping. Builder's
  choice which, as long as one note sounds as loud as it does now.

### Load

- A running search or library build keeps its full speed (the user's choice): while one runs, notes may stutter or
  drop out; on an otherwise idle machine they play cleanly.
- Voices are rendered on several cores, not one. When a profile cannot keep up with the voices sounding (Nome6 is the
  heaviest: it renders at twice the sample rate), the oldest voices are dropped, with the short fade, rather than
  every voice stuttering. *(My call.)*

### Choosing the device

- A **MIDI in** selector in the window header (both tabs share it): *none* and the input devices by name.
- The choice is remembered between sessions by device name (a small settings file in the Studio's application data
  folder, or whatever the Studio already uses for settings, if anything). At start, a remembered device that is
  connected is opened; one that is not leaves the selector on *none* without an error.
- A device that disappears while open: the selector shows *none* and the status line says so; held notes are released.

### On-screen keyboard

- Mouse button or touch down on a key: note-on, at the velocity the press height gives now (lower = louder). Up:
  note-off. Moving to another key while held: note-off for the old key, note-on for the new one.
- Several touches hold several keys. With one mouse, only one key is held at a time; earlier notes keep sounding
  through their release.
- Keys sound by MIDI or by mouse light up while they sound (until note-off), in both tabs.
- Clicking a node in the Library tab keeps playing the sound once, as now: a note held for the reference duration and
  then released, through the same voice engine.

## Left to the builder

- The incremental renderer design, the voice and mixer classes, and the threading between the MIDI callback, the
  interface and the audio buffer feeding.
- Buffer size and count within the latency target, and what happens when rendering falls behind (it must not crash
  or stick; a short gap is acceptable on an overloaded machine).
- How the gain of a newly selected sound is computed ahead of the first note.
- Exact placement and look of the selector and of the lit keys.

## Testing

- Equivalence of held-then-released voices with offline renders, per profile, for sustaining and percussive envelopes,
  with edge-amount envelopes, filter sweeps, pitch envelopes and vibrato.
- Voice allocation: polyphony, the 16-voice limit with stealing of the oldest, retriggering a held note, stray
  note-offs, release of all voices when the device closes.
- MIDI message handling without hardware (velocity 0 note-on as note-off, channels ignored, other messages ignored),
  behind an interface that the tests can drive.
- By hand: a real controller on Windows; latency and stuck notes by ear.
