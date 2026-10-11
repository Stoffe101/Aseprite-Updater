# Aseprite Manager v0.5.0 Resource Hub

**Status:** Windows feature branch, pending end-to-end user validation. Last reviewed 2026-10-11.

## Outcome and design contract

Aseprite Manager is intended to be a one-stop **personal resource manager** for Aseprite and associated 2D game tooling, with **Windows-first support**. It preserves source-built Aseprite installs, custom themes, user-created sprites, source material, and unrelated projects. The resource hub is curated rather than a free-for-all script marketplace.

PixelTown is proprietary and still at its small Prototype 00 art-validation stage. Its 16x16 terrain and 16x24/20x30 character sizes are **provisional**. The recommended tools target grid editing, equipment overlays and tagged animations without fixing those sizes or importing third-party art into PixelTown. The Godot project remains separate; the Wizard is a developer-tool addon, not finished game content.

## What v0.5.0 adds

| Resource | Scope | Direct install? | Update/remove | Original source/license |
| --- | --- | --- | --- | --- |
| 7 Aseprite Lua tools | Grid split, tile packing, cel movement, outlining, tagged export, merge helper, sprite comparison | Yes, opt-in | Tracked SHA, protected files, retained backups | [Aseprite Script Examples](https://github.com/aseprite/Aseprite-Script-Examples), Igara Studio, MIT |
| Tile Index Overlay | Labels every tile on a new layer; optional key mapping | Yes, opt-in | Manager-tracked checksums and backups | [Pixeltica](https://github.com/Pixeltica/AsepriteExtensions/tree/main/TileIndexOverlay), MIT |
| 13 curated color palettes | DawnBringer, ENDESGA, AAP, Zughy, Rosy/Bubblegum, Arne, PICO-8 | Optional individual managed copy | Checksummed content; old files kept in backup | [Aseprite upstream palette collections](https://github.com/aseprite/aseprite/tree/main/data/extensions), original palette creators credited in UI |
| PixelTown prototype colors | 12 named colors (or full 206-color inventory); NOT art approval | Yes, from user-chosen local Godot project | Does not edit Godot project | Original PixelTown proprietary local source |
| Custom PNG brush sources | Safe vault for user-supplied stamp images | Imports PNG into dedicated vault, **not** directly into Aseprite's saved brushes | Existing files never overwritten | User-provided source (user responsible for rights) |
| Godot Aseprite Wizard | SpriteFrames, tags, layer-based animations, tilemap textures | Yes, **per project** | Compare GitHub revisions, backup previous plugin as ZIP, refuse modified installs, safe uninstall | [Vinicius Gerevini / godot_4 branch](https://github.com/viniciusgerevini/godot-aseprite-wizard/tree/godot_4), MIT |

### Aseprite Wizard integration

- Check selected folder contains **Godot 4** \`project.godot\`, and refuse invalid/unsupported projects.
- Read the official **Godot 4** branch's latest **commit SHA** and download that precise source snapshot from GitHub.
- Extract **only** \`addons/AsepriteWizard\` from the archive. Reject traversal, symlinks, unexpected file types, excess archive content, and missing \`plugin.cfg\`, \`plugin.gd\` or \`LICENSE\`.
- Never overwrite an unknown plugin or a managed plugin changed after install. Track SHA-256 per installed file.
- On update, create a ZIP backup outside PixelTown first, stage within its \`addons/\` directory, and swap with rollback if the replacement fails. Uninstall keeps a recovery ZIP.
- **Do not change \`project.godot\`, \`.godot\`, user scenes, sprites, import settings or other assets.** The addon is disabled until the user explicitly enables it in **Godot → Project → Project Settings → Plugins**. User then configures Aseprite's executable path in Wizard.
- CI now tests that the addon loads inside a real pinned Godot 4.7.2 headless editor on a disposable project. It still needs testing with actual layered .aseprite sprite imports, tags and tilemaps before production approval.
- Before uninstalling, **disable the editor plugin in Godot**, close Godot, then remove through Manager. Imported Godot resources and the project's settings remain in place.

### Palettes and licenses

Palette source files are downloaded individually from Aseprite's upstream repository when the user requests a preview/install. Many palettes are *already included* with Aseprite, so the gallery helps visual discovery and favorites rather than pretending all copies are new. **The original palette creators retain copyright/terms**; inclusion in the Aseprite source tree does not mean every palette can be redistributed or shipped inside a commercial game's assets. The Manager itself does not ship third-party palettes. Only use a chosen palette for PixelTown or commercially distributed assets after its underlying creator's license is confirmed.

### Brushes

Aseprite documents \`user.aseprite-brushes\` as its internal saved-custom-brushes store. Directly merging that XML would risk corrupting or replacing someone's brushes. v0.5.0 therefore provides a PNG source vault with signature/size validation and collision-resistant filenames. Open a stamp PNG in Aseprite, select its pixels, and use **Ctrl+B** (Edit → New Brush) to create a custom pattern brush. Direct in-app brush registration is still **not implemented** and should never be presented as complete.

### Scripts and risk

Lua scripts and Godot editor plugins run code, unlike passive palettes. The Manager installs only explicit allowlisted upstream GitHub sources and does not execute the scripts on install. Installation requires a confirmation; user should inspect unfamiliar code and run modifying tools on sprite copies. SHA-pinned source URLs establish the fetched revision, **not** a security audit of every upstream future code change. A repository update is an opportunity to review code again.

## Testing matrix

The Windows GitHub Actions pipeline:
- C# smoke tests and production Windows WPF build.
- Existing Aseprite theme archive integration tests.
- Direct download/install/uninstall of the official Lua scripts.
- Direct download, extract/validate, install, uninstall of real Godot Aseprite Wizard into a **temporary fixture project**, asserting that \`project.godot\` is unchanged.
- All 13 real upstream palette files parse into RGB swatches.
- Real Tile Index Overlay install/remove.
- Archive path traversal fixtures and custom-file overwrite refusal tests.
- WPF layout screenshots at minimum and desktop window sizes.
- Self-contained executable and Inno Setup installer packaging.

CI passing confirms real Godot 4.7.2 editor loading, but does **not** prove importing actual .aseprite sprites, in-editor Lua script quality, or full PixelTown production suitability. Those require hands-on PixelTown testing on real sprite masters.

## Remaining improvements, by value

1. **PixelTown Aseprite master templates** once art scale and palette are approved (layer naming for equipment, directional animation tags, 16x16 tile grid experiments).
2. **Godot 4.7.2 live validation** using the editor to enable Wizard in a disposable project, import simple layered .aseprite animation, verify tileset + SpriteFrames and disable/uninstall it.
3. **Real thumbnails and screenshots** from permitted creators for tool/brush listings; license review before hosting screenshots.
4. **Library-managed local favorites and installed-resource dashboard** across types. Add grouped filters, resource refresh diagnostics, backup restore UI.
5. **Safer custom brush import** after implementing/exporting Aseprite's internal format with tests; do not edit that file until proven.
6. **Automatic optional checks** with download-on-confirm and no forced silent code upgrades.
7. **macOS** after the Windows core is field-tested.

Related audit: [PIXELTOWN_LIBRARY_AUDIT.md](PIXELTOWN_LIBRARY_AUDIT.md). The art direction and repository rights come from PixelTown docs/00–08, particularly 03_ART_DIRECTION and 05_DECISIONS_AND_OPEN_QUESTIONS.


## User-profile backups (v0.5.0)

The Creator's Library now has navigation for **Tools, Palettes, Extensions, Godot, Brushes and Backups**. Managed palette copies can be updated in bulk or safely uninstalled, retaining the previous files.

The user can also create a **read-only ZIP backup** of the complete \`%APPDATA%\Aseprite\` configuration folder, covering themes, palettes, scripts, user settings and the \`user.aseprite-brushes\` file. It validates the ZIP, rejects symlink/reparse-point traversals and disallows writing the backup inside the profile being archived. Users choose the destination and should keep it private because it can contain personal sessions or settings.

There is intentionally **no automatic restore** until an equally reliable transactional restore and recovery workflow has passed testing. Profile backups never edit the original Aseprite files, and Aseprite should be closed to avoid inconsistent snapshots.

CI now tests actual Godot 4.7.2 addon loading, checks archives and paths, and verifies saved brushes/settings appear in test profile ZIPs without changing the source.
