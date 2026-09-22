# Sample targets

Synthetic target notes rendered from the built-in reference patches, for trying the matcher and the desktop app.
Every folder has the hidden patch next to the WAV files, so a search result can be compared with what made the sound.

| Folder | Notes | What it tests |
| --- | --- | --- |
| `electric-piano-keyed/` | MIDI 48, 60, 72 | one patch for several octaves: the tine has key scaling (-6 dB and half the envelope time per octave) and a fixed 60 Hz thump does not transpose |

Match the set:

```bash
dotnet run -c Release --project src/HearSay.Cli -- match \
    samples/targets/electric-piano-keyed/electric-piano-keyed-48.wav@48 \
    samples/targets/electric-piano-keyed/electric-piano-keyed-60.wav@60 \
    samples/targets/electric-piano-keyed/electric-piano-keyed-72.wav@72 --output result/
```

or open the three files in HearSay Studio. Regenerate, or render any other reference patch at any notes and velocities:

```bash
dotnet run -c Release --project src/HearSay.Cli -- reference                      # list the patches
dotnet run -c Release --project src/HearSay.Cli -- reference bell --notes 55,67,79 --velocities 1,0.5 --output targets/bell
```
