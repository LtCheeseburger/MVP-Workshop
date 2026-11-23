# NASCAR-Workshop release readiness

## v0.1.7 scope

This build improves reliability and packaging. It is not a declaration of complete
format support or in-game validation.

- Visible scene export writes transformed instances, selected parts, material
  definitions and PNG textures. Reflected instances reverse winding. Normals use
  the inverse transpose. Shared bitmap objects export once. The OBJ is committed
  after its resources; a failed export preserves an existing OBJ.
- Texture/model refresh failures remain inside the viewer. Undo can restore the
  preview after an invalid replacement. Texture PNG export reports I/O failures.
- Shared-pixel texture names are disclosed in the texture footer and tooltip.
  Replacing those pixels affects every alias, including alternate palettes.
- Track models have 3D/2D tabs with embedded texture replacement and undo. Scene
  refreshes preserve the camera and selected part alternatives.
- Static geometry/materials are frozen, and shared texture aliases reuse decoded
  bitmaps within each preview.
- Builds publish to a fresh staging folder, run the packaged export self-test,
  verify required files and SHA-256 hashes, and then replace the stable release
  folder. The previous package is retained for rollback. Test scenes and settings
  remain outside the shipping package.

## Repeatable release checks

```powershell
.\scripts\build-nascar.ps1 -Verify -NoRestore -Zip -GameFolder 'E:\Recomp Source\nascar06-ttc\game\DATA'
.\scripts\verify-nascar-package.ps1 -Directory '.\release\NASCAR-Workshop'
```

Omit `-NoRestore` when dependencies have not been restored. `-Verify` runs core
fixtures, vehicle/character texture checks, a rebuilt archive/cache export and
category coverage. Every package runs the WPF scene-export smoke test; with
`-Verify -GameFolder`, it also exports actual car, character and track previews,
injects an invalid texture into project history and checks undo recovery. Tests
write exports into the repository's `.tmp/nascar` directory, never the game data.

The package includes `Verify-Package.ps1` and `package-manifest.json`. Hashes detect
missing or changed package files; they are not a publisher signature. Keep the
OBJ's generated assets folder alongside it when moving an exported scene.

## Remaining release gates

1. Validate supported texture and database edits in the game, including reloads
   from rebuilt QKL caches. Archive verification alone does not prove runtime use.
2. Test installation and launch on a clean Windows machine with the documented
   .NET 9 Desktop Runtime. A self-contained distribution, signing and installer
   have not been implemented.
3. Visually check each track and representative driver/body/LOD combinations.
   The current renderer approximates game materials; multi-layer shaders,
   animation and race-session objects remain incomplete.
4. Expand semantic decoders/editors for physics, AI, cameras, particles, damage
   and frontend resources. Three meshes still fail index bounds checks, and 84
   TMdl containers have no Geom section.
5. Add cancellation/background loading for expensive scene/model operations and
   measure memory and response times against a documented performance budget.

Until these gates are met, describe releases as development builds with tested
workflows, rather than a fully production-ready editor for every game format.
