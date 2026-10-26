# Changelog

## 1.0.0 — 2026-10-25

First release under the **MVP-Workshop** name (formerly "MVP07 Asset Tool", last version 0.9.0-beta.1).

### Added
- **MVP 06 NCAA Baseball (Xbox) support** alongside MVP 07 NCAA Baseball (PS2). Open an extracted Xbox game folder (`default.xbe`).
  - `.xsh` SHPX texture packs: DXT1/3/5, PAL8, BGRA8888 and 16-bit formats, Xbox Morton (un-)swizzling.
  - **Texture replacement** on Xbox textures with automatic **mip-map regeneration** and DXT re-encode (PAL8 and BGRA8888 are written without DXT loss).
  - `.xbx` XPR0 title/save images, `.xfn` FntX bitmap fonts, `.vp6` MVhd movies (playback through ffmpeg), `.xbe` header/section/library inspector.
  - Xbox EAGL models (`.ord` objects with `__VB` vertex buffers, `__RM` render methods and 16-bit triangle-strip index buffers), with textures and skeletons.
- Version number shown in the title bar, start page and startup log line.
- Project extension `.mvpwproj`; old `.mvp07proj` projects still open (same format).
- Settings migrate once from `%APPDATA%\MVP07AssetTool` to `%APPDATA%\MVP-Workshop`.

### Changed
- Rebranded to MVP-Workshop: executable `MVP-Workshop.exe`, window/title bar, start page, message-box captions, default export names (`MVP-Workshop-modded.iso`), survey titles, docs.
- Projects and namespaces renamed `AssetTool.*` → `MVPWorkshop.*`.
- Temp folder is now `%TEMP%\MVP-Workshop`.
- PS2 (MVP 07) behaviour and file formats are unchanged.

### Known limitations
- Xbox models: **no skin-weight animation/posing** (skin weights are decoded, but the Xbox layout is shown as static geometry).
- Teams workspace: **stadium and uniform tables are not yet resolved for Xbox**.
- **Lossy DXT**: replacing DXT textures re-encodes with a block fit; some logos with fine detail or hard edges show visible artefacts. Texture dimensions must be multiples of 4.
- Movies (MPC and VP6) need `ffmpeg.exe` on `PATH` (not bundled).
- Upscaled textures and APT appended strings are not yet tested in-game (carried over from 0.9.0-beta.1). Back up your files.
