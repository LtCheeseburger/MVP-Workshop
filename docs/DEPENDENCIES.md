# Asset dependencies

Select an asset and choose **Dependencies** in the main toolbar. The subject picker also contains decoded team and player records. It supports typing a subject name. **Depends on** follows outgoing edges; **Used by** performs reverse lookup. Double-click or **Follow relationship** moves to the other subject. **Open source** and **Open target** open the underlying asset viewer. Team/player subjects open their roster source; their record offsets are retained in the evidence.

Filter evidence into three independent classes:

* **Explicit references:** decoded APT import movie fields and FEL SF includes, LS texture-pack declarations and TB font fields. Local references resolve only when exactly one entry matches in the source directory. Duplicate BIG entries retain their directory index identities. Global basename matches remain candidates. External runtime search rules are not assumed.
* **Decoded assignments:** roster uniform/logo families, stadium executable mappings, roster-to-player hexadecimal DAT joins, split ELF link partners, speech HDR/DAT records and OFF stream indexes. Assignment evidence retains offsets, DAT lines, filename rules and registry details from the existing decoders. Authentic stadium registry links remain distinguishable from assigned stadium links in their evidence.
* **Heuristic candidates:** embedded filename strings, APT resource/font conventions, localization ID matches with unknown language/table selection, team lookup text matches and archive-local model relocation/SSH tag matches. Identical filenames or texture tags alone never establish a confirmed dependency. Shared portrait/equipment/audio libraries are not asserted as player/team dependencies while selection remains undecoded.

Select a row to inspect its full evidence and location. **Missing references** lists decoded references with no candidate target, and player keys without matching DAT rows. References with ambiguous local targets or unknown global search scope remain unresolved without being falsely marked missing. Parser/load diagnostics appear in the status tooltip; an incomplete decode is not evidence that an asset is unused.

**Impact preview** walks reverse explicit/decoded edges transitively, terminates on cycles and excludes candidates. Texture **Replace image** builds/opens the explorer and shows affected subjects plus the candidate count before applying the replacement. Cancel the build using **Cancel** or cancel the final replacement dialog. Runtime relationships that have not been decoded can still exist.

The index builds on a worker task. Cancellation discards the incomplete snapshot. Edits, undo/redo and session changes cancel in-flight work and invalidate the entire snapshot, including reverse edges and semantic joins; use **Build / refresh** to rebuild from the current overlay bytes. This deliberately favors correctness over incremental performance. No stale snapshot is used for replacement approval. Project save notifications also invalidate the snapshot conservatively.

## Validation

```
dotnet run --project tests/Dependencies -- ../../game
dotnet build src/MVPWorkshop.App/MVPWorkshop.App.csproj --no-restore
```

The dependency test executable always runs synthetic checks for scoped FEL includes, missing references, duplicate basenames and duplicate BIG entries, reverse lookup, evidence locations, cycle-safe impact, cancellation and removal/restoration of edges after edits/undo. With a game folder it also verifies LSU uniform family 032, the Alex Box stadium mapping, player entities, actual APT imports and model relocation/tag candidates. Eight undecodable model objects on the bundled disc are reported as diagnostics rather than silently treated as dependency-free.
