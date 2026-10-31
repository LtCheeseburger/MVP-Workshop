# Roster CSV

Open a school workspace and choose **Export roster CSV** or **Import roster CSV**.
The Tools menu has equivalent all-school commands. Apply or reset player drafts
first. Export writes a UTF-8 CSV with a BOM for spreadsheet applications, outside
the game installation.

Each row identifies a school roster slot and one of its source DAT tables. A player
with both attribute and pitcher records therefore has multiple rows. The first five
columns—School index, School, Roster slot, Player key and Source—are read-only.
They keep the import scoped to existing players and files. Player keys use a `0x`
prefix so spreadsheet applications keep them as text. Players with no resolved DAT
source appear as `(unresolved)` rows; their fields cannot be edited.

Editable columns begin with a readable name and end with the original field index
and source label, for example `Jersey number [2:playerattrib_jerseynum]`. Keep column
headers intact. Blank cells belonging to a different source table are not editable.
You may omit player rows or editable columns to import a subset; omitted data stays
unchanged. Repeated rows for a shared player must agree on the values they target.

Import opens a review of changed fields with school, player, source, before/after
values and validation problems. Apply stays disabled until the CSV has no problems.
Values go through the same span editor as the roster UI: names must be nonempty
Latin-1 text without DAT delimiters/control characters; numeric fields retain their
source numeric shape, including integer bounds. Jersey numbers must be integers
from 0 to 99. Other enum/rating ranges remain unresolved and are displayed as raw
numbers; the tool does not invent bounds for them.

All source files are validated before any commit. If a source DAT or roster changes
after review, import stops and asks for a fresh review. Unchanged CSVs add no history.
Changed files become ordinary project overrides, with one undo step per source DAT.
Unknown payloads, whitespace, line endings, other players and binary roster data are
retained. Save the project or export the mod as usual; source game files are never
written.
