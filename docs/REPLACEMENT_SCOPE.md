# Replacement scope

Image replacement, PNG-folder import, uniform workspace replacement and raw texture,
audio or model import open an impact review before applying bytes. The review uses
DependencyIndex's confirmed reverse relationships to list schools; heuristic filename
matches never count as ownership. Unknown runtime consumers remain explicitly unknown.
Inspect Dependencies closes the review so the evidence can be explored before retrying.

Choose a school and **Replace for school only**, or **Replace for all N schools**.
With no confirmed consumers the shared action says school usage is unresolved.
Cancelling applies nothing. Preview-only formats cannot create saved assignments.

School-only isolation supports the decoded uniform and logo family assignments.
Uniform forks copy every native u/f variant, paired font and matching menu artwork,
using the current project/base bytes. The allocator reserves all existing files and
assigned 9-bit family IDs. The selected replacement changes one copied file; other
family members retain their bytes. TeamAssignmentEditor repoints only that school's
uniform or logo field, preserving other assignment bits and roster bytes.

The complete copy and roster update publish as one saved history entry. One undo
removes all copied files and restores the assignment; redo restores both. The files
are project Added entries, visible in school resolution, saved projects and `.mvpmod`
exports. Source files are never written. Stale roster/member bytes or destination
collisions refuse publication before any edit is applied.

Shared player geometry, audio event/bank selection and stadium definitions do not
yet have writable per-school bindings in the decoded formats. Their school-only
button is disabled with that reason; copying a file without a valid runtime reference
would not isolate it. Shared replacement and its impact preview remain available.

Verification: `tests/SchoolFork` covers ownership, allocation, family completeness,
assignment preservation, VFS resolution, undo/redo and stale-review rejection.
The `--replacement-selftest` WPF check operates the real dialog, applies both scopes,
reloads a project and verifies a `.mvpmod` export using synthetic textures and schools.
