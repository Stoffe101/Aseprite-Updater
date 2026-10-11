# PixelTown art-pipeline audit and Aseprite resource-library plan

Updated: 2026-10-11. **Source-of-truth references:** [Stoffe101/PixelTown](https://github.com/Stoffe101/PixelTown) README, docs/00–08, START_HERE_NEXT_CHAT, CONTRIBUTING, project.godot, scenes/main.gd, scripts/demo_simulation.gd, art/world_canvas.gd, and CI. This is a study of the **existing game**, not permission to change its accepted design.

## What PixelTown actually is

An original proprietary (All Rights Reserved) 2D fantasy **living village plus autonomous adventurer expedition** simulation, with both halves equally important. It is **not** NODE//ZERO or an action RPG. Warm, cozy village life is contrasted with opt-in atmospheric cave risk. Mouse-first, player-placed construction blueprints and visible hauling. A small four-inhabitant / two-adventurer **First Descent** is a future playable target, not shipped. Core decisions are kept in PixelTown docs/05; no new gameplay decisions are made here.

The chosen art direction is slightly angled-looking top-down **on a square tile grid**, NOT true isometric. Small whimsical storybook figures, consistent silhouettes, modular buildings, coherent village/cave palettes, equipment overlays and honest meaningful animation. Current *provisional* tile size is 16×16; character sizes 16×24 and 20×30 are under comparison in the engine. Visual QA at 1920×1080, 2560×1440, **3440×1440**; actual Godot camera and asset alignment matter.

PixelTown's current art in \`art/world_canvas.gd\` is **hand-written procedural drawing code**, not layered Aseprite files. The current worker carries three wood units and completes a demonstration hut; a guard/cave encounter is diagnostic. Real animations, crafting, combat, complete dungeon progression and reusable sprite masters are **not built**. Art feasibility and user sign-off (Art Gate A) are still pending. Do not design a huge asset content pipeline based on unapproved proportions or look.

## Consequences for the resource manager

| Priority | Production task | Recommended workflow |
| --- | --- | --- |
| P0 | Extract consistent starting colors | Read locally from PixelTown world_canvas.gd to generate a draft .gpl preset. User can choose/curate later. Never bundle ARR art in Manager. |
| P0 | 16×16 terrain and house tiles | Grid tile splitting / deduplication. Warn that 16×16 is a test, not a locked size. |
| P0 | Reusable villagers and guard/equipment | Move cels, layer organization, silhouette/outline tools. Preserve Aseprite editable masters and overlays. |
| P1 | Four-direction walk / work / combat | Tag-aware exports, animation guides and systematic overlay QA after sprite proportions are approved. |
| P1 | Cave/overworld mood | Contrast ramps and palettes, plus readable effects, not indiscriminate procedural detail. |
| P1 | Aseprite ↔ Godot import | Validate Aseprite tags/exports and Godot importer. Do **not** overwrite the PixelTown Godot project automatically. |
| P2 | Brushes/texture tools | Pattern and vegetation/stone brushes only after master grid and palette work; Aseprite user.aseprite-brushes is custom XML and must never be patched blindly. |

## Curated sources researched

### Direct installation enabled in v0.4.0

All first-phase executable scripts come from the official **[Aseprite Script Examples](https://github.com/aseprite/Aseprite-Script-Examples)**, authored by Igara Studio, license **MIT**, [license source](https://github.com/aseprite/Aseprite-Script-Examples/blob/main/LICENSE.txt). Versions are fetched by a GitHub commit SHA and the license copied with each installed script:

- **Tiles to Layers.lua**: break spritesheets into layer-per-tile, with user-selected width/height. Helps exploratory 16×16 environmental work.
- **Pack Similar Tiles.lua**: deduplicate repeated tiles and create a tilemap and tileset. Can create many output assets; operate on a duplicate/copy.
- **Move Cels.lua**: move selected animation cels, useful for equipment alignment and animation organization.
- **Stroke Outside Selection.lua**: creates a border on selected areas, useful for tiny readable silhouettes.

These scripts are examples, not an Aseprite-endorsed professional production toolkit. Actual utility depends on testing with working .aseprite assets. They can modify currently open documents *when the user invokes them*. The Manager installs code, but **does not execute it**.

### Palettes shipped in v0.4.0

- **PixelTown prototype color extractor:** reads user-selected local \`project.godot\` and \`art/world_canvas.gd\`, extracts the 12 named core constants by default, with an optional full 206-color diagnostic inventory, from explicit six-/eight-character \`Color("#...")\` literals, produces GIMP Palette .gpl in an Aseprite **palette extension** under \`%APPDATA%\Aseprite\extensions\`. It does NOT copy PixelTown colors into this public GitHub repository, send data online or modify the game project.
- **Import local GPL palette:** user provides a file already obtained with permission. Validate basic GIMP Palette format and RGB bounds, then create an Aseprite \`contributes.palettes\` extension. Do not scrape or redistribute community palette art.

Aseprite already bundles several palette families, including AAP and Arne; **don't claim that repackaging default palettes adds value**. For browsing more colors, link to [Lospec Palette List](https://lospec.com/palette-list/) and respect creators' individual licensing/terms rather than bulk fetching arbitrary palettes.

### Further relevant research, not auto-installed yet

- **[Pixeltica AsepriteExtensions](https://github.com/Pixeltica/AsepriteExtensions)** (MIT): Tile Index Overlay, useful for debugging grid layouts; requires reviewing Lua/plugin behavior and verifying current Aseprite API.
- **[Godot Aseprite Wizard](https://github.com/viniciusgerevini/godot-aseprite-wizard)** (MIT, Godot 4 branch): potentially the most important future Godot-side workflow extension for sprite frames/tilemap import. It is a **Godot editor plugin**, not an Aseprite script. Must test against PixelTown's pinned Godot 4.7.2 and project's ARR licensing before any opt-in project mutation.
- **[JRiggles Lospec Palette Importer](https://github.com/JRiggles/Lospec-Palette-Importer)** (MIT): imports palettes inside Aseprite and requests full Lua permission. Good candidate only after independent security/compatibility assessment.
- **[Aseprite official customization docs](https://www.aseprite.org/docs/customization/)**: script path is from File > Scripts > Open Scripts Folder, extensions through Preferences; Windows settings under \`%APPDATA%\Aseprite\`.
- **[Aseprite docs: brush storage](https://aseprite.com/docs/files/)**: user brushes stored in \`user.aseprite-brushes\`, rather than interchangeable individual file bundles. Imported brush packs need a defined format plus backup/restore, not generic zip extraction.

## Design contract

Aseprite Manager is a separate MIT-licensed **companion installer**, PixelTown is proprietary ARR; never mix licenses or copy original PixelTown art into Manager's public repository. Source code and themes from third parties keep their original licenses; record author, exact upstream, updates, and package attribution. Only audited sources may have direct-install buttons for executable Lua. Display source/review warnings. Never execute Lua during download/install.

Managed script directory: \`%APPDATA%\Aseprite\scripts\AsepriteManager\managed-<tool-id>\`.
Managed palette directory: \`%APPDATA%\Aseprite\extensions\managed-<palette-id>\`.
Manifests/checksums/backups: \`%LOCALAPPDATA%\Aseprite Manager\library-*\`.
Before overwrite or removal: require current files to match tracked SHA-256; reject unknown folders. Keep previous complete directory as backup. Continue protecting \`.aseprite\` files and all paths outside the scope. Installed Aseprite versions and theme resources are independent.

No implicit user-project changes, no direct Godot write yet. Tool/extension update checking requires internet and installs always require user confirmation. An updater comparing upstream repository HEAD detects *source changes*, not verified script compatibility; consider file-specific commit tracking in a future release.

## Scope and limitations of v0.4.0

- First installable version includes curated official Lua scripts and local GPL palettes. **Not** a large generic browser of hundreds of community packages.
- Script installable resources require that Aseprite be closed. Aseprite Lua execution is privileged and could affect open sprites; open/edit a copy if testing experimental tools.
- The PixelTown color set is a generated draft. The procedural art source is not a final palette and sprite proportions are **unapproved**.
- Aseprite custom brushes and Godot import tooling remain research-stage. No one-click brush editing or automatic project plugin integration is claimed.
- Manager's automated Windows CI validates package downloads, files, and simulated protection; human checks in real Aseprite and PixelTown scenes are required before claiming game-production approval.

## Windows validation and future gates

1. Verify native WPF UI at 850×660, 1040×875 and 1440×950; check 125% / 150% DPI and no clipped panels.
2. Download a real MIT script to the user scripts folder; confirm it appears under File > Scripts after restarting.
3. Update a script, alter a tracked script manually, and confirm Manager blocks replacing edited code.
4. Import a 2-color GPL and ensure it appears in the Aseprite palettes chooser without touching open sprites.
5. Select PixelTown root and extract local draft colors; ensure no modification to \`world_canvas.gd\`.
6. Create a few 16×16 tiles and test Tiles to Layers on a **copy** at both candidate resolutions.
7. Only after approved sprite scale: produce reusable Aseprite layered templates, 4-direction animations, Godot tags and editable source masters.
8. Only then validate Godot plugin import on a throwaway/test copy of PixelTown; ensure nearest-neighbor, tileset grid, animation tags and equipment overlays.
