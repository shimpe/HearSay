# Library browser: design

Date: 2026-10-16. Status: approved in conversation, awaiting review of this document.

## Goal

A **Library** tab in HearSay Studio for sound design and inspiration: wander through the structure library of a
profile by ear, find a sound you like, and take it along as SuperCollider code or as the patch shown in the Search
tab. Sounds exported earlier can be imported and placed in the library, so that their neighbourhood can be browsed.

Success criteria:

1. Every one of the 300 000 sounds of a library is reachable by zooming into families; the screen never shows more
   than about forty nodes.
2. Clicking a node plays its sound and shows its code and block diagram; the keyboard plays the last clicked sound at
   any pitch.
3. An imported sound lands among the library sounds closest to it, selected and centred.
4. The same library always gives the same tree and the same layout.

Out of scope: favourites, using a library sound as a starting point of a search, naming families by hand, a single
continuous map of all sounds (UMAP, t-SNE), persisting the import list between sessions, importing `.scd` files.

## The tab

The middle area of the main window (search column and patch panel) becomes a `TabControl` with two tabs: **Search**,
the current layout unchanged, and **Library**. Header and footer (status line, progress bar) are shared.

```
+------------------------------ graph (about 60%) ---------+---- patch (about 40%) ------+
| Library [FMX6 v]  [Import folder...]                     | PATCH [Copy] [Save...]      |
| all > #12 bright . decaying > #7                         |       [Show in Search]      |
| +----------------+                                       | +- SuperCollider -+ Block -+ |
| | imported       |     o---o        o  family (medoid)   | | code / diagram           | |
| | piano-c4       |    / O   \       size ~ sounds        | |                          | |
| | piano-c4 alt 03|   o   \   o--o   colour ~ brightness  | +--------------------------+ |
| +----------------+        o         * imported sound     | #7 . saw . LP4 . 412 sounds |
+----------------------------------------------------------+-----------------------------+
| piano keyboard: plays the last clicked sound                                           |
+----------------------------------------------------------------------------------------+
```

- **Profile selector**: FM7, FMX6, Nome6. A library missing on disk is built when it is selected, with progress in
  the footer (FMX6: about 17 minutes once); the search uses the same file. While it builds the graph says so, and
  importing still works (placement waits for the library).
- **Single click** on a node selects it (ring), plays its sound and shows its code and diagram on the right.
- **Double click**, or the expand mark on the selected node, opens the node: the graph shows its children. The
  breadcrumb leads back up; each crumb is clickable.
- **Hover** shows the label: descriptors and number of sounds.
- **Keyboard**: the `PianoKeyboard` of the Search tab, playing the last clicked sound (library or imported).

### Imported sounds

- **Import folder...** reads every `patch.json` and every `alternatives/NN-patch.json` under the chosen folder,
  recursively. Name: the folder name, with ` alt NN` for an alternative.
- Profile: `SearchSummary.Profile` from `search-summary.json` next to the patch (in the parent folder for an
  alternative) when present and known; otherwise the smallest profile the patch is compatible with, in the order FM7,
  FMX6, Nome6.
- Skipped, and counted in the status line with the reason: unreadable json, an invalid patch, a patch no profile can
  play. A folder without any `patch.json` gives a status message.
- Each imported sound is rendered once with its profile's renderer as a library sound is (220 Hz, 1 s note, 0.3 s
  release) and fingerprinted.
- The list of imported sounds sits left of the graph. Clicking an entry places the sound: the graph opens the deepest
  family on the path of `Locate`, shows the imported sound among that family's children as a star, laid out with
  them, selected and centred, and plays it. One level up shows the neighbouring families.
- Placement is by sound only, so a sound exported for FM7 can be placed in the FMX6 library.
- The list lives for the session. Imported sounds are not added to the library.

## Data and computation: `LibraryAtlas`

New class in `HearSay.OperatorMatrix/Priors`, without UI, on top of a `StructureLibrary`.

`StructureLibrary` gets read-only access to what it holds: `ReadOnlySpan<byte> Fingerprint(int index)` and
`int Seed(int index)` (indices `0..Count-1`), the file name of a library (`FileOf`), the request its patches are
rendered with (`ReferenceRequest`), and an optional progress report while it is built.

### Families

- A family is a set of library indices, with a centroid (the final k-means centroid, as doubles: the mean of its
  members when k-means converged), a medoid (the member nearest
  the centroid; its sound is the family's sound), a size and descriptors.
- The root holds the whole library. A family of more than 40 members is split by k-means with k = 40 into its
  children; a family of 40 or fewer has its members as children, as leaves of one sound each.
- k-means: k-means++ initialisation with a random generator seeded from the family's path, at most 15 iterations, the
  squared distance of `SpectralFingerprint.Distance` (on centroids: the same sum of squares in doubles). The final step
  assigns every member to its nearest final centroid, so that `Locate` and the partition agree. Empty clusters are
  dropped; a family whose members all have the same fingerprint becomes leaves directly, in index order, limited to
  the first 40 (the rest are duplicates of the same sound). The same holds when a split leaves a single non-empty
  cluster, so that the tree always ends.
- Children are ordered by size, largest first, ties by medoid index.
- The root's children are computed once and saved next to the library file as
  `<library file name without .bin>-atlas-v1.bin` (format version, library size, fingerprint length, then per family
  its member indices, centroid and medoid). A missing, truncated or mismatching file is rebuilt. Deeper families are
  computed on demand and kept in memory (for 300 000 sounds a family below the root holds about 7 500 members:
  well under a second).

### Layout

- Per opened family: the children's medoids (and a placed imported sound) are laid out in two dimensions by classical
  MDS on their pairwise distances (square roots of the squared fingerprint distances), the two largest eigenvectors of
  the double-centred matrix by power iteration with a fixed start vector.
- Then up to 200 overlap-removal steps push apart nodes closer than the sum of their radii (radius from the size, on a
  logarithmic scale). The result is scaled to the unit square.
- Edges: each node is joined to its two nearest siblings by distance.
- The layout is deterministic: no randomness besides the seeded k-means.
- Centring on a placed imported sound moves it to the middle and scales positions and radii down together just
  enough that every node stays inside, so no overlap comes back.

### Descriptors

From the medoid's fingerprint and patch, in this order, joined with ` · `:

- brightness: the power-weighted mean band of the body slice, as a multiple of the note (band b is 0.25 x 2^(b/12)):
  *dark* below 2, *mellow* 2 to 4, *bright* 4 to 8, *very bright* from 8;
- envelope: total power of the attack slice against the decay slice, as *percussive* (decay at least 18 dB
  below the attack), *decaying* (6 to 18 dB below), *sustained* (within 6 dB), *swelling* (decay more than 6 dB above);
- the waveform of the loudest carrier when it is not a sine;
- *filtered* when an audible operator has a filter;
- *noisy* when an audible operator is a noise waveform or roughened by the shared noise.

The fingerprint stores dB under the loudest band of the whole note, so slices are comparable.

### `Locate`

`Locate(byte[] fingerprint)` returns the path of families from the root, at each level the child with the nearest
centroid, down to the family whose children are leaves. Within that family the imported sound is shown among the
children.

## Audition

- Library sounds are rendered with the renderer of the library's profile, imported sounds with their own profile's.
  Rendering runs off the UI thread; the latest click wins, older renders are dropped.
- A click plays the request the library was fingerprinted with: 220 Hz (A3, MIDI 57), velocity 1, 1 s note, 0.3 s
  release.
- The keyboard plays the last clicked sound at the key's pitch and velocity, with the same request lengths. A patch
  without velocity sensitivity gets the velocity as volume. One gain per sound, from its reference note, so that
  differences between keys survive.
- This logic, now in `MainViewModel.PlayNote`, moves to a shared `NotePlayer` (render, gain per patch, velocity rule)
  used by both tabs, with one shared `AudioPlayer`.

## Actions

- **Copy**: the SuperCollider code of the selected sound from the profile's exporter, with a header comment naming the
  library and seed (`HearSay library supercollider-fmx6 v2, seed 50123456`), or the import's name and folder.
- **Save...**: a folder with `patch.json`, `patch.scd` (the exporter's extension), `preview.wav` (the reference note)
  and a `search-summary.json` holding only `{"SearchSummary": {"Profile": "<name>"}}`, so that the folder imports
  again. Written by a new `LibrarySoundWriter` in `HearSay`; `PatchMatchResultWriter` is unchanged.
- **Show in Search**: switches to the Search tab, whose patch panel enters a library-patch mode: code, diagram and
  keyboard work, Copy and Save work (Save as above); stats, matrix and note rows are empty and the summary shows the
  patch description, titled `from the library: FMX6 #<seed>` (or the import's name). The next search, or selecting a
  kept result, leaves the mode.

## Code structure

- `HearSay.OperatorMatrix/Priors/LibraryAtlas.cs`: tree, k-means, cache, `Locate`.
- `HearSay.OperatorMatrix/Priors/AtlasLayout.cs`: MDS, overlap removal, edges.
- `HearSay.OperatorMatrix/Priors/SoundDescriptors.cs`: descriptors.
- `HearSay/ExportedSounds.cs`: scanning a folder, choosing the profile, rendering and fingerprinting.
- `HearSay/LibrarySoundWriter.cs`: Save.
- `HearSay.Studio/PatchView.axaml`: the code and diagram tabs, used by both tabs.
- `HearSay.Studio/NotePlayer.cs`: shared audition.
- `HearSay.Studio/LibraryViewModel.cs`: profile, tree navigation, selection, imports, actions.
- `HearSay.Studio/LibraryGraph.cs`: the graph control, custom drawn like `FmDiagram`; raises node clicked, node
  opened.
- `MainWindow.axaml`, `MainViewModel.cs`: the tab control, library-patch mode, `PlayNote` through `NotePlayer`.

## Errors

- Library being built: the graph shows a message, progress in the footer; imports are listed and placed once the
  library is ready.
- No audio device: the keyboard is disabled (as in Search) and clicks only select.
- Import problems: skipped with a count and reason in the status line.
- Atlas file problems: rebuilt; a file that cannot be written still serves the session (as the library does).

## Testing

- `HearSay.OperatorMatrix.Tests`, on a 3 000-patch FM7 library built in memory (about a second):
  - children partition their parent exactly; at most 40 children; the medoid is a member; families of 40 or fewer
    give leaves;
  - building twice gives the same tree; the layout is the same twice, finite, and without overlaps;
  - `Locate` of a member's own fingerprint ends in the family holding it, at every level;
  - the atlas file round-trips; a truncated file and a file of another size are rebuilt;
  - a family of identical fingerprints becomes leaves;
  - descriptors on synthetic fingerprints and patches give the expected words.
- `HearSay.IntegrationTests` (fast, run by filter): scanning a folder with alternatives; the profile from the summary
  and the smallest-profile fallback; a broken json skipped with its reason; save then import gives the same patch and
  profile.
- UI: no Studio test project. Headless screenshots of `LibraryGraph` and the tab, and a click-through by hand.
