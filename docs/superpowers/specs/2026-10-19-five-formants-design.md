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

## Speech vowel presets (added 2026-10-19 at the user's request: "look up and use values for both english and dutch language")

Besides the sung Csound table, the presets offer measured speech vowels, as two more preset sets:

- **English** — Hillenbrand, Getty, Clark & Wheeler (1995), "Acoustic characteristics of American English vowels",
  JASA 97: 45 men, 48 women, 46 children (boys and girls together), 12 vowels in /hVd/ words (heed, hid, hayed, head,
  had, hod, hawed, hoed, hood, who'd, hud, heard); steady-state F1–F3.
- **Dutch** — Pols, Tromp & Plomp (1973), JASA 53, 50 men, and Van Nierop, Pols & Plomp (1973), Acta Psychologica 37,
  25 women: 12 Dutch monophthongs in /hVt/ words (named here by the keywords of the original lists: poet, paat, poot,
  pat, peut, piet, puut, peet, put, pet, pot, pit); F1–F3 and their levels L1–L3 (dB below the overall level).
  These are Northern (Netherlands) Standard Dutch speakers.

The means below were computed from the per-speaker data as Praat distributes them (`dwtools/Table_dataSets.cpp`,
`Table_create_hillenbrandEtAl1995` and `Table_create_polsVanNierop1973`), unmeasured values (0) left out.

What the sources do not give is filled in by rules (my calls):

- **Bandwidths** (both speech sets): B1 80, B2 100, B3 150, B4 250, B5 300 Hz; Q = F / B.
- **F4 and F5**: men 3500 and 4500 Hz, women 4100 and 5000 Hz, children 4600 and 5600 Hz; raised where needed so that
  each formant is at least 300 Hz above the one below.
- **Levels**: Dutch F2 and F3 take the measured levels (level of Fn relative to F1 = (L1 − Ln) dB). The levels that were
  not measured (English F2–F5, Dutch F4–F5) come from a cascade vocal-tract model: with each formant a two-pole
  resonance of unity gain at 0 Hz, `R_k(f) = (F_k² + (B_k/2)²) / sqrt((F_k² + (B_k/2)² − f²)² + (B_k·f)²)`, and a
  −6 dB per octave source-plus-radiation slope `S(f) = 1/f`, the level of Fn is `H(F_n) / H(F_1)` with
  `H(f) = S(f) · Π_k R_k(f)` over all five formants; the Dutch F4/F5 levels are taken relative to the measured F3
  (model F4 level − model F3 level + measured F3 level). Levels are capped at 1 (F1's level).

Means (Hz; Dutch levels in dB below overall):

```
Dutch f a n=25 F=762 1117 2860 L=3.7 8.2 32.0
Dutch f aa n=25 F=986 1443 2778 L=4.2 10.5 27.4
Dutch f e n=25 F=669 1905 2788 L=2.2 18.0 23.4
Dutch f ee n=25 F=472 2352 2896 L=1.9 22.3 24.7
Dutch f eu n=25 F=476 1690 2512 L=1.8 18.7 26.8
Dutch f i n=25 F=465 2262 2840 L=1.8 24.2 26.4
Dutch f ie n=25 F=276 2510 3046 L=-0.2 26.5 29.5
Dutch f o n=25 F=578 934 2852 L=2.8 7.7 34.6
Dutch f oe n=25 F=320 842 2746 L=0.2 15.9 44.3
Dutch f oo n=25 F=506 961 2608 L=2.4 8.9 37.3
Dutch f u n=25 F=490 1688 2568 L=1.6 20.2 28.2
Dutch f uu n=25 F=288 1832 2520 L=-0.2 23.8 29.4
Dutch m a n=50 F=679 1051 2619 L=8.4 12.0 31.2
Dutch m aa n=50 F=795 1301 2565 L=8.2 13.8 28.7
Dutch m e n=50 F=583 1725 2471 L=7.2 18.7 25.4
Dutch m ee n=50 F=407 2017 2554 L=5.0 21.0 23.3
Dutch m eu n=50 F=443 1497 2260 L=5.1 20.7 27.9
Dutch m i n=50 F=388 2003 2571 L=5.0 22.3 24.5
Dutch m ie n=50 F=294 2208 2766 L=5.5 25.0 26.1
Dutch m o n=50 F=523 866 2692 L=6.1 13.9 34.3
Dutch m oe n=50 F=339 810 2323 L=5.2 18.2 41.2
Dutch m oo n=50 F=487 911 2481 L=5.7 13.1 35.6
Dutch m u n=50 F=438 1498 2354 L=4.8 20.5 28.2
Dutch m uu n=50 F=305 1730 2208 L=5.1 23.2 28.0
English children ae n=45 F=720 2499 3292
English children ah n=45 F=1000 1689 2945
English children aw n=46 F=836 1303 2951
English children eh n=46 F=740 2279 3297
English children ei n=46 F=565 2649 3323
English children er n=46 F=586 1721 2154
English children ih n=46 F=513 2556 3409
English children iy n=46 F=453 3076 3702
English children oa n=46 F=599 1140 3006
English children oo n=46 F=571 1506 3076
English children uh n=46 F=738 1538 3126
English children uw n=46 F=493 1404 2992
English men ae n=45 F=591 1930 2595
English men ah n=45 F=756 1309 2535
English men aw n=45 F=656 1023 2521
English men eh n=45 F=588 1803 2604
English men ei n=45 F=476 2090 2692
English men er n=45 F=475 1379 1711
English men ih n=45 F=429 2034 2687
English men iy n=45 F=343 2323 3001
English men oa n=45 F=498 910 2459
English men oo n=45 F=469 1123 2435
English men uh n=45 F=621 1181 2548
English men uw n=45 F=380 992 2355
English women ae n=48 F=676 2335 2973
English women ah n=48 F=921 1526 2832
English women aw n=48 F=804 1188 2824
English women eh n=48 F=727 2063 2952
English women ei n=48 F=535 2526 3050
English women er n=48 F=524 1588 1930
English women ih n=48 F=484 2369 3057
English women iy n=48 F=437 2761 3373
English women oa n=48 F=555 1036 2828
English women oo n=48 F=519 1229 2829
English women uh n=48 F=760 1416 2901
English women uw n=48 F=460 1106 2735
```

Vowel keys — Dutch: oe=poet /u/, aa=paat /a/, oo=poot /o/, a=pat /ɑ/, eu=peut /ø/, ie=piet /i/, uu=puut /y/, ee=peet /e/, u=put /ʏ/, e=pet /ɛ/, o=pot /ɔ/, i=pit /ɪ/. English: iy=heed /i/, ih=hid /ɪ/, ei=hayed /e/, eh=head /ɛ/, ae=had /æ/, ah=hod /ɑ/, aw=hawed /ɔ/, oa=hoed /o/, oo=hood /ʊ/, uw=who'd /u/, uh=hud /ʌ/, er=heard /ɝ/.
