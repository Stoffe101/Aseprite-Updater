# Aseprite Manager

Windows-first **source build installer and updater** for [Aseprite](https://github.com/aseprite/aseprite).

> Early development: CI compilation is not the same as a successful Windows source-build/install test. Do not remove a working Aseprite installation until a full end-to-end installation has been verified on Windows 11.

## What this project does

- Browse all **published Aseprite GitHub releases** in a dropdown. The latest stable release is labeled **LATEST**; previews are labeled separately.
- Detect Git, CMake, Ninja and Visual Studio 2022 C++ build tools.
- Offer to install missing build prerequisites through Windows Package Manager, with explicit approval and UAC where required.
- Clone the exact official release tag with recursive submodules and use Aseprite's upstream build helper with Git Bash and the Visual Studio x64 environment.
- Install to a per-user folder by default, or switch an existing explicitly-selected source-built installation to an older/newer published version.
- Verify the new build before and after swapping; retain the **complete** previous install as a full backup, preserve local extensions/themes/palettes and art stored in the app folder. Projects saved in Documents/Pictures/other folders are not touched.
- Keep the GUI responsive, with a log and explicit **Check for updates** and **Install / Update** actions.

## Get a Windows build

See the [Actions tab](../../actions/workflows/windows.yml). Download the artifact from a successful Windows build, extract the zip, and run the EXE. The CI workflow also creates an Inno Setup installer.

**No Windows release should be described as fully tested until a fresh dependency installation, source build, update, downgrade and rollback are verified on real Windows 11 machines.**

Older published tags may use obsolete upstream build scripts, Skia versions or toolchains. The selection list is not a guarantee that every historical tag will compile with this manager. On a build failure, the currently installed copy is left untouched. Always keep independent backups of your work before downgrading because old Aseprite versions may not understand newly saved sprite formats.


## Companion update center (v0.3.0)

- **Check for updates:** fetch the official Aseprite release list and highlight a newer stable version if one exists.
- **Update Aseprite to latest:** a dedicated button selects the latest stable release and starts the existing, independently verified compile-and-backup pipeline. Nothing is updated without confirmation.
- **Curated theme gallery:** Catppuccin Mocha/Macchiato/Frappé/Latte, Dracula and Monaki. Download from theme maintainers' official GitHub sources, verify package metadata, and extract a restricted list of theme-related files into the *user* Aseprite extensions directory.
- **Install recommended themes:** one click for Catppuccin Mocha, Dracula and Monaki (individual installs remain optional).
- **Theme update checks:** compare installed manager-tracked revisions with upstream GitHub. Update managed themes separately from Aseprite. Local file changes block replacement, and original theme directories are retained as backups.
- **Aseprite logo/icon repair:** use the official icon bundled with the upstream source-built Aseprite. Recreate the Windows Start Menu shortcut with the bundled icon.

Theme settings and extension files are stored under %APPDATA%\Aseprite\extensions. Project/sprite files are not touched by the theme installer. Themes from third parties are not redistributed in our installer. Theme authors retain their copyrights and licenses. The app does not bundle the Aseprite logo, using the local source-installed icon instead.

*This is an early-access implementation.* Theme API integration, custom edits, and Aseprite's version-specific theme compatibility still need end-to-end Windows testing. Version checking requires internet; source builds still require Git, CMake, Ninja, Visual Studio C++ tools, and a Windows SDK.

## PixelTown Creator's Library (v0.4.0 preview)

The Creator's Library is a separate window launched from the Manager dashboard. It contains a small curated set of **official MIT-licensed Aseprite Lua examples** that help with tiles, animation cels and selection outlines. Each script can be installed, checked for updates and uninstalled. Sources are downloaded directly from pinned upstream GitHub commit URLs, not bundled or executed by Manager.

- **Tiles to Layers**, **Pack Similar Tiles**, **Move Cels**, **Stroke Outside Selection**.
- **Extract PixelTown prototype palette** from a *local copy* of PixelTown: select a Godot project directory with project.godot and art/world_canvas.gd; unique source hex colors become a draft Aseprite GPL palette extension without editing or uploading the game project.
- **Import local GPL** palette for any other artwork. Palettes are installed as individual Aseprite extension packages in your profile; manually edited resources are protected.
- Installations live under \`%APPDATA%\Aseprite\`, independent of source-build updates. Existing managed files are SHA-256 checked and prior copies are retained in \`%LOCALAPPDATA%\Aseprite Manager\library-backups\`.

Scripts can execute code *when you run them inside Aseprite*. Only explicitly reviewed, MIT-licensed examples are automatically offered; work on a copy of valuable sprites until you trust a tool. Godot importer and brush installation are future phases, not disguised as completed functions.

See the full [PixelTown audit and library research](docs/PIXELTOWN_LIBRARY_AUDIT.md) for art scales, source licensing, scope boundaries and Windows QA gates. PixelTown's public source/art remains All Rights Reserved: this app does not copy those assets or automatically modify the Godot project.

## Expanded Creator's Library (v0.5.0 preview)

The Windows Manager now provides a larger Aseprite resource hub:

- **Seven curated Lua tools** from the official MIT-licensed Aseprite examples, including animation-tag sprite sheet exports, sprite stacking and layer merging. The four foundational tools remain in the PixelTown starter kit.
- **13 color palettes** with live swatch previews from the upstream Aseprite palette collection. Many are already included in Aseprite, and authors keep their individual licensing terms. Optional managed copies can be installed without changing any original artwork.
- **Tile Index Overlay** from Pixeltica (MIT), with opt-in installation, update and removal. This Lua extension adds a command and may register keyboard shortcuts.
- **Godot 4 Aseprite Wizard**, installed per selected Godot 4 project using the exact upstream commit. Revisions are tracked, unknown/modified plugins are protected, and previous copies are backed up before upgrades. **Manager does not enable the plugin or modify project.godot.** Godot 4.7.2 compatibility still requires real editor-side testing.
- **PNG brush-source vault**: organizes user-supplied brush stamps safely. Open the PNG in Aseprite and press Ctrl+B to make a custom brush. Does **not** silently rewrite Aseprite's internal brush settings.

Each executable third-party component requires user confirmation and carries a direct source link. The goal is a secure, predictable resource hub rather than an unreviewed automatic code installer. Brush-pack registration and a general extension marketplace are not yet complete.

**Detailed provenance and the Windows test plan:** [docs/RESOURCE_HUB_V05.md](docs/RESOURCE_HUB_V05.md).

## Control Center (v0.6.0)

- **Unified update review:** checks Aseprite, Manager releases, managed themes, scripts, palettes, Tile Index and Godot Wizard. Updates still require individual confirmation in their dedicated workflows.
- **Profile recovery:** previews and validates a full configuration ZIP, then restores in stages while retaining the complete previous Aseprite profile for rollback.
- **Project profiles:** save portable JSON manifests for a project's preferred installed tools, palettes and themes; compare missing resources on another computer.
- **Polished navigation:** Update Center, Recovery and Project Profiles have dedicated pages with multi-size WPF screenshot tests.

No sprite or asset generator is included. Auto-updating the Manager executable and unattended Update All are not claimed; both require verified release distribution and rollback checks. Read [Control Center v0.6 documentation](docs/CONTROL_CENTER_V06.md) for complete safety notes and outstanding testing.

## Fast Version Switching (v0.6.2)

- **Previously compiled versions:** keep a local SHA-256 verified copy, restore to staging and verify the executable before swapping. Up to five versions or 3 GiB cached, with LRU cleanup. No binaries are redistributed.
- **Persistent Skia downloads:** NTFS junction into a cache keyed to the full upstream Skia tag means repeated source builds can reuse compatible dependencies, despite the temporary source tree being removed.
- **Optional Mozilla sccache:** install through winget by choice; enables C/C++ compiler object caching via CMake and Ninja when supported by the local MSVC toolchain. No guaranteed hit rate across all tags.
- **Parallelism:** automatic Ninja job setting (2–12) or configurable 2–16 jobs.
- **Build Performance window:** cache sizes, clear only caches (not artwork, installed versions, settings or backups), settings and optional compiler cache installation.

Source-building a version for the very first time still takes time; repeated switches to locally cached versions should avoid recompiling. See [docs/FAST_BUILDS_V062.md](docs/FAST_BUILDS_V062.md) for cache safety, tests and limitations.

## Development

1. Install the .NET 8 SDK on a Windows 11 development machine.
2. Run `dotnet run --project src/AsepriteManager/AsepriteManager.csproj`.
3. Run `dotnet publish src/AsepriteManager/AsepriteManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`.

The manager may offer to install *Aseprite's* build dependencies, including several GB of Visual Studio C++ tools. No installs begin automatically at startup.

## Scope

- Windows 11 x64: active priority.
- macOS: planned after Windows install/update/rollback tests pass.
- Linux: out of scope.

## Legal

This is an **unofficial** independent manager; it is not affiliated with Aseprite. We do **not** redistribute compiled Aseprite binaries, Skia assets, or Aseprite source. The manager downloads upstream sources to the user's machine. Users remain responsible for complying with [Aseprite's license](https://github.com/aseprite/aseprite/blob/main/EULA.txt). Manager code is MIT licensed.
