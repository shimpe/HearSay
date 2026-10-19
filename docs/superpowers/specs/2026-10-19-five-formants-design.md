# Five formants, presets, and a third formant in the search

Date: 2026-10-19. Status: agreed in conversation (the "middle way").

## Intent

The FMX6 formant filter has two peaks (F1 and F2 = F1 × ratio) that share one Q. Real vowels need at least three
formants, each with its own bandwidth. The user wants the formant filter to hold five formants, wants to set them from
vowel presets while editing a sound, and wants the search to use the third formant too.

## Decisions (the user's)

- **Five formant peaks** in the filter, each with its own frequency, level and Q.
- **Own Q per formant** (not one shared Q), so that presets match their table.
- **The search uses F1–F3**; F4 and F5 are for the editor and the presets only (the search leaves them off).
- **Presets in the editor**: while a formant filter is edited, buttons set all five formants from the Csound formant
  table (below): a voice (soprano, alto, countertenor, tenor, bass) and a vowel (a, e, i, o, u).
- **A before/after benchmark** shows whether F3 in the search pays off; if it does not, the search goes back to two
  peaks and everything else stays.

## Decisions (my calls)

- **Representation.** F1 stays in hertz (`FixedHz`) with its Q (`Q`). F2 keeps `FormantRatio`/`FormantLevel` and gains
  its own Q; F3, F4 and F5 are new peaks with a ratio to F1, a level relative to F1 and a Q. Ratios keep the formants
  together when F1 moves (a vowel shifted as a whole), as the search already does for F2. An absent own Q of F2 means
  "same as Q", and an absent or zero-level F3–F5 is off, so every existing patch, library sound and favourite sounds,
  renders and exports exactly as before.
- **Ranges.** Formant ratios 1..32 (the table needs up to 18.3); formant peak Q 0.5..50 (the table needs up to 41;
  other filter types keep 0.5..20); levels 0..1 (the table's dB relative to F1, as 10^(dB/20)).
- **The FMX6 UGen** gets the extra values as optional inputs after the existing filter rows, so SynthDefs exported
  before keep working with the new plugin, and the exporter writes them only when a formant uses them, so patches
  without F3–F5 or an own F2 Q export exactly as before. HearSay's C# filter kernel mirrors the UGen operation for
  operation, as now; the plugin is rebuilt and installed in the user's SuperCollider extensions, replacing the old
  files (no backup: the user's call, old builds can be recreated from git).
- **Editor.** The inspector shows F1–F3 (Hz or ratio, level, Q) and folds F4–F5 under "more formants". The presets
  are a voice menu and five vowel buttons (a e i o u); a preset is one undo step and keeps F1's sweep and the
  operator's other settings. Each preset sets F1 Hz, Q1 = F1/bw1, and for F2..F5 ratio = Fn/F1, level = 10^(dBn/20),
  Qn = Fn/bwn (clamped to the ranges).
- **Search.** The parameter schema adds F2's own Q and F3 (ratio, level, Q); the formant prior and the formant mutation
  read three peaks from the target where there are three; the random patch generator gives formant filters a third
  peak, so the FMX6 structure library is rebuilt (a new library version). F4/F5 are never set by the search.

## Non-goals

- Formants on FM7 or Nome6 (no operator filters there).
- More than five formants, formant glides between presets, presets for other filter types.

## Csound formant table (TABLE III of the Csound manual)

Columns: F1..F5 frequency (Hz), F1..F5 amplitude (dB), F1..F5 bandwidth (Hz).

```
soprano a       800 1150 2900 3900 4950   0  -6 -32 -20 -50   80  90 120 130 140
soprano e       350 2000 2800 3600 4950   0 -20 -15 -40 -56   60 100 120 150 200
soprano i       270 2140 2950 3900 4950   0 -12 -26 -26 -44   60  90 100 120 120
soprano o       450  800 2830 3800 4950   0 -11 -22 -22 -50   70  80 100 130 135
soprano u       325  700 2700 3800 4950   0 -16 -35 -40 -60   50  60 170 180 200
alto a          800 1150 2800 3500 4950   0  -4 -20 -36 -60   80  90 120 130 140
alto e          400 1600 2700 3300 4950   0 -24 -30 -35 -60   60  80 120 150 200
alto i          350 1700 2700 3700 4950   0 -20 -30 -36 -60   50 100 120 150 200
alto o          450  800 2830 3500 4950   0  -9 -16 -28 -55   70  80 100 130 135
alto u          325  700 2530 3500 4950   0 -12 -30 -40 -64   50  60 170 180 200
countertenor a  660 1120 2750 3000 3350   0  -6 -23 -24 -38   80  90 120 130 140
countertenor e  440 1800 2700 3000 3300   0 -14 -18 -20 -20   70  80 100 120 120
countertenor i  270 1850 2900 3350 3590   0 -24 -24 -36 -36   40  90 100 120 120
countertenor o  430  820 2700 3000 3300   0 -10 -26 -22 -34   40  80 100 120 120
countertenor u  370  630 2750 3000 3400   0 -20 -23 -30 -34   40  60 100 120 120
tenor a         650 1080 2650 2900 3250   0  -6  -7  -8 -22   80  90 120 130 140
tenor e         400 1700 2600 3200 3580   0 -14 -12 -14 -20   70  80 100 120 120
tenor i         290 1870 2800 3250 3540   0 -15 -18 -20 -30   40  90 100 120 120
tenor o         400  800 2600 2800 3000   0 -10 -12 -12 -26   40  80 100 120 120
tenor u         350  600 2700 2900 3300   0 -20 -17 -14 -26   40  60 100 120 120
bass a          600 1040 2250 2450 2750   0  -7  -9  -9 -20   60  70 110 120 130
bass e          400 1620 2400 2800 3100   0 -12  -9 -12 -18   40  80 100 120 120
bass i          250 1750 2600 3050 3340   0 -30 -16 -22 -28   60  90 100 120 120
bass o          400  750 2400 2600 2900   0 -11 -21 -20 -40   40  80 100 120 120
bass u          350  600 2400 2675 2950   0 -20 -32 -28 -36   40  80 100 120 120
```

## Testing

- Bit-exactness: the C# kernel against the rebuilt UGen (the existing NRT filter tests, extended to five formants with
  own Qs); every existing filter and formant test unchanged; an unedited patch exports the same text as before.
- Old SynthDefs (filter rows without the new inputs) still run on the new plugin.
- Presets: each of the 25 produces a valid, compatible patch whose values follow the table.
- The benchmark: FMX6, same seeds and budget, before (main) and after, on vowel-like targets made outside HearSay's
  filter (a pulse train through five resonators from the table) and on the existing demo targets.
