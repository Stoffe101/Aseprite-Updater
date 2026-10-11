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
