<img src="docs/images/hearsay-logo.png" alt="HearSay logo: six operators on a ring, joined by the chords of a modulation matrix, with a wave through them" width="96" align="right" />

# HearSay Studio

**Give it a recording of a note. Get back a synthesizer patch that sounds like it** — one you can play, change and
take to SuperCollider.

HearSay Studio listens to a sound, searches six-operator FM patches until one matches it, and lets you play the result
on the screen keyboard or a MIDI controller right away. Then you edit it in a block diagram, compare it with the
original by ear, or wander through hundreds of thousands of other sounds.

![HearSay Studio after a search: three notes of an electric piano matched by one FM patch, the course of the search, and the patch as a block diagram](docs/images/studio/search-result.png)

## What it does

- **Hears a sound, finds a patch.** Load one or more notes of the same sound (WAV), press *Run search*. HearSay
  searches structures and parameters for the SuperCollider UGens FM7 (six sines), FMX6 (saw, square, pulse, noise and
  operator filters) and Nome6 (elliptic waveforms and shared noise), and draws the search as it goes.
- **Plays like a synth.** Up to 16 voices on the screen keyboard or any MIDI controller, at the moment the search ends.
- **Edits in a block diagram.** Drag numbers, add and duplicate operators, draw envelopes, undo anything. What you
  changed is outlined, and one click switches between your edit and the original.
- **Sings vowels.** Formant filters with sung, English and Dutch vowel presets turn an operator into a voice.
- **Opens a library of sounds.** Every synthesis target comes with a structure library of 300 000 to 600 000 sounds,
  laid out as a map of sound families to click through, filter and keep as favourites.
- **Exports SuperCollider code** that sounds the same, at the same loudness, as what you heard.

<table>
  <tr>
    <td width="33%"><img src="docs/images/studio/edit-marks.png" alt="Editing a patch: changed values outlined in pink" /></td>
    <td width="33%"><img src="docs/images/studio/formants.png" alt="A formant filter set to a vowel" /></td>
    <td width="33%"><img src="docs/images/studio/library.png" alt="The FMX6 structure library as a map of sound families" /></td>
  </tr>
  <tr>
    <td align="center"><sub>Edit, and see what you changed</sub></td>
    <td align="center"><sub>Vowels from formant presets</sub></td>
    <td align="center"><sub>Browse the structure library</sub></td>
  </tr>
</table>

## Get it

Download HearSay Studio for Windows, macOS or Linux from the
**[Releases](https://github.com/shimpe/HearSay/releases)** page. The builds are self-contained: unpack and start,
nothing else to install. The manuals come with them as PDF files.

To play the exported patches in SuperCollider you need the plugin of the synthesis target: sc3-plugins for FM7, the
[FMX6 plugin](https://github.com/shimpe/fmx6) for FMX6, the Nome6 plugin for Nome6.

> [!WARNING]
> **The first use of each synthesis target keeps your CPU busy for a while.** The first search with FM7, FMX6 or Nome6
> (or the first look at its Library tab) builds that target's structure library: it renders 300 000 to 600 000 sounds
> once, on every core at full speed, so fans spin up and the machine gets warm. That is expected, not malware. It takes
> under a minute for FM7, about 11 minutes for Nome6 and 20 for FMX6 on a 24-thread desktop (longer with fewer
> cores), shows its progress and the time left, and stores 86 to 175 MB in the local application data. After that the
> library is reused, and starting the Studio itself never builds anything. **The Studio asks before it starts a build**,
> with the estimate for your computer: build it now, search without it, or not now.

## Manuals

| Manual | For |
| --- | --- |
| [HearSay Studio manual](docs/studio-manual.md) | the desktop app, step by step, with screenshots |
| [Expert manual](docs/expert-manual.md) | getting good patches: preparing targets, choosing FM7, FMX6 or Nome6, budget and seeds, the command line |
| [Patch matching](docs/patch-matching.md) | the design of the search and the evidence behind it |
| [Library reference](docs/library-reference.md) | HearSay as a C# library: comparing two notes, closing the loop with your own synth |
| [Tutorial](docs/tutorial.md) | eleven hands-on steps with the library, from a first comparison to a closed loop |

## Under the hood

HearSay is also a C#/.NET library and a command line. At its core it compares two recordings of the same note and
says *how* they differ ("attack is 22 ms too long", "harmonic 3 is 4 dB too weak"), which makes it the measurement
layer of any closed loop that tunes a synthesizer. The patch search is built on top of it, and runs from the command
line too:

```bash
hearsay-cli match target.wav --profile supercollider-fm7 --output result/
```

From source (.NET 10):

```bash
git clone https://github.com/shimpe/HearSay.git
cd HearSay
dotnet run -c Release --project src/HearSay.Studio
```

## License

GPL-3.0-or-later. See [LICENSE](LICENSE).
