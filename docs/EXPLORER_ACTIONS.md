# Explorer asset actions

Tree and search-result menus act on the clicked asset, independently of the active document.
**Open original / sources (read-only)** opens a separate inspection tab with frozen byte snapshots:

- Vanilla / game source, when that path exists in the source installation.
- Selected base mods, when the selected base layer supplies bytes, labelled with its origin.
- Current project / preview, including preview-only changes.

Project Added assets and base-only assets have no vanilla tab. Source lookup bypasses both
base-layer and project-added children. Hex, Strings and Structure inspect these snapshots;
no editable asset viewer is instantiated, and the document has no active asset binding.
Reopen sources to capture newer edits. Inspection never switches or detaches the active overlay.

**Format import / export** uses the existing viewer and its selected image/sound/model variant:

| Asset | Commands |
| --- | --- |
| Texture pack / loose DDS image | Import image or folder; export selected PNG or pack PNGs |
| Native audio stream / bank / Sony pair | Import audio or folder; export selected WAV or all sounds |
| Model ELF / split object | Import constrained glTF positions; export glTF or OBJ |
| Text / CSV | Import or export the text file |

Opening a format command selects the clicked asset's existing document, preserving its current
viewer selection and drafts. Import uses the existing scope/impact review and undo logic.
Unsupported interchange has an explicit disabled explanation; parser failures or missing model/audio
partners show an unavailable message and the existing Preview explanation. Codec and storage limits
remain those documented in AUDIO_REPLACEMENT.md and MODEL_INTERCHANGE.md. Raw import/export stay
separate. Export commands retain source-folder protection. Bookmark remains in the normal menu.

The explorer desktop regression checks cover tree/search targeting, source separation, read-only
inspection, Added assets, existing edit preservation and unchanged installation files. The base-mod
desktop check verifies distinct vanilla/base/project snapshots.
