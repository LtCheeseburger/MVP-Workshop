# APT screen preview — fidelity notes

Screens: `docs/apt-fidelity/stages/*.png` (stage only, 854×480, scripts run, bgPS2 backdrop), `docs/apt-fidelity/authored/*.png`
(scripts off), `docs/apt-fidelity/compare-*.png` (before | authored | final), `docs/apt-fidelity/window-gamSetup.png` (full app).
Regenerate: `MVP-Workshop.exe --snapshot win.png --open <vpath>.apt --apt-frame 20 --apt-stage out.png [--apt-noscript] [--apt-nobackdrop] [--apt-smooth] [--apt-bounds] [--apt-pick x,y]`.
Representative screens: gamSetup, slobby, scrLdrBd, nSetup, gamelist, settings, creaGame, trnTree, netcnf, easoMnu1, voiceInv, chalInv.
The disc's APT movies are the EA online/lobby set (OSDKInterface/OSDKTourney); the baseball front end itself is FEL.

## Matches the game data (verified in code/validation)
- **Strip decode fix**: APT batches restart strips in 2-quadword vertex units (not 3); the old decoder bridged two quads of one batch with sliver
  triangles (the diagonal line across gamSetup). `apt-raster-test` pins this on gamSetup geometry 11.
- **Native mapping**: per-draw texture matrix → texel-unit UVs; TAR `PS2CM_CLAMP` vs repeat (1,478 clamped / 453 repeat draws);
  vertex colours (Gouraud gradients) interpolated; line-list draws (3) drawn as 1-px lines.
- **GS-style raster**: pixel-centre sampling, top-left rule, no AA (optional 2× smooth), alpha test, general `(A−B)·C+D` blend.
  `apt-states` shows APT geometry only ever uses source-over (`CS,CD,AS,CD,128`); there is no framebuffer-additive state in the data.
- **Colour transform**: placement multiply (RGBA) + add (RGB offsets, e.g. 0x9D9DA7 in EasoSSFrame), concatenated through nested clips;
  script `Color.setTransform` (the gEaso `ColorStyles.ChangeColor` palette recolour) replaces a clip's RGB multiply.
- **Text**: game TTFs (EASSanCB / 05_Body / 05_Title), kern pairs, alignment/multiline/word-wrap (EditText fields were mis-parsed before:
  +0x14 is alignment, +0x20 read-only), `ltxt()` from `10_easo.loc`. Fonts the game does not ship (Arial, Times, Verdana, EA05_Body is
  shipped but not in fonts.txt) fall back to EASSanCB.ttf, the only entry of `fonts.txt`; shown under Substitutions.
- **Scripts**: params/registers/locals, objects/arrays, prototype chains, `Extends`, `Object.registerClass`, per-clip frame actions with
  `stop()/gotoAndStop`, InitActions, timers, shared `gEaso` module, lazy instantiation of clips a parent script touches.
- Clip-depth masks as per-pixel stencil; imports across movies; optional bgPS2 backdrop (the screen manager layers screens over it).

## Still approximate (also in the Limitations tab)
- Modulate-then-add colour order is inferred from placement data; the VU1 `TextureApt/GouraudApt` microcode is not in the ELF/exe symbols and was not disassembled.
- Bilinear filtering and the 854×480 target (vs 640×448 frame buffer) are assumptions; text uses WPF anti-aliasing, +2 px gutter, no hinting.
- The ncaa palette is seeded by running the `ColorStyles` constructor with COLORPALETTE="ncaa"; how the game selects it is unverified.
- Screens are populated at runtime (screen manager, network, rosters, component `init`); those calls are listed, not executed, so
  player-info bars, spreadsheets, menu titles and the foreground title bar keep authored (often white/placeholder) looks. `lbrow` is a row template.
- Runtime values (`undefined`) concatenate as empty; stage is cleared to black (authored BackgroundColor ignored).

## Validation
`apt-raster-test`, `apt-script-test`, `apt-geometry-test` (873 movies, 1,917 shapes, 0 triangles outside bounds), `apt-native` (0 failures),
`survey --deep` (38,547 files, 0 failures, identical to FORMAT_SURVEY.md), `models` (0 failures). New probes: `apt-shape`, `apt-states`,
`apt-fonts`, `apt-colors`, `apt-grep`, `apt-env`.
