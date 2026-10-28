MVP-Workshop 1.0.3  (win-x64, self-contained, no install needed, no .NET required)

WHAT IT DOES
  Asset explorer and editor for two games:
    - MVP 07 NCAA Baseball (PS2)   - extracted game folder (SLUS_215.82) or a PS2 ISO
    - MVP 06 NCAA Baseball (Xbox)  - extracted game folder (default.xbe)
  Browse every archive, preview and edit textures, models, UI movies, fonts, text and team data, then export a mod.

SUPPORTED FILE TYPES
  Both games : BIGF archives (RefPack), EAGL models (.ord/.orl), fonts, LOC strings, text/CSV, audio banks
  PS2        : .ssh textures, .apt/.const UI movies, .fel menus, .mpc movies, ISO images, team/player/uniform/stadium workspaces
  Xbox       : .xsh textures, .xbx images, .xfn fonts, .vp6 movies, .xbe header, Xbox EAGL models

HOW TO OPEN A GAME
  1. Run MVP-Workshop.exe.
  2. Click "Open Game Folder" (Ctrl+O) and pick the folder that contains SLUS_xxx.xx (PS2) or default.xbe (Xbox).
     "Open ISO" opens a PS2 disc image.
  3. From a command line: MVP-Workshop.exe --game "D:\path\to\game"
  The last game you opened is reopened automatically on the next start.

WHERE MOD OUTPUT GOES
  The game folder is never written to. Edits live in memory or in a project file (.mvpwproj; older .mvp07proj files also open).
  "Export Mod" writes only the changed archives to a separate output folder, by default "mod_output" next to the game folder.
  "Export ISO" builds a new ISO (PS2).

MOVIES
  Movie playback and MP4 export need ffmpeg.exe on your PATH (https://ffmpeg.org). It is not bundled.

KNOWN LIMITATIONS (details in CHANGELOG.md)
  - Xbox models: no skin-weight animation (static geometry only).
  - Teams workspace stadium and uniform tables are not yet resolved for Xbox.
  - DXT texture replacement is lossy; some logos show artefacts.
  - Upscaled textures and appended APT strings are not yet tested in-game. Back up your files.

DOCUMENTATION
  CHANGELOG.md and the docs\ folder (FORMATS, XBOX_MVP06, EDITING, TEXTURE_IMPORT, ISO_EDITING, ...).
