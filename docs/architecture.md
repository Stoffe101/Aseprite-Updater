# Windows-first design and test plan

## Goals

1. A usable, responsive Windows 11 GUI on both computers with and without Aseprite.
2. A fresh source installation from a device without Git, CMake, Ninja, Visual Studio or Skia.
3. Reliable detection of \`x.y.z-dev\` as equivalent to the matching stable release.
4. Offline-safe UI: network calls happen only when Check for updates is clicked.
5. Source builds in isolated work directories; no replacement until executable version verified.
6. Rollback on a failed final directory swap, with retained backups on success.

## Flow

\`\`\`
Launch Manager
  -> scan selected install folder and prerequisites (no network)
  -> Check for updates -> official stable GitHub releases API
  -> Install missing tools (explicit user consent; winget / UAC)
  -> Install or update
      -> clone official release tag, recursive submodules
      -> load MSVC x64 environment
      -> Git Bash build.sh --auto --norun
      -> verify build/bin/aseprite.exe --version
      -> stage files next to target, preserve user extension/theme files
      -> rename old install to a retained backup
      -> rename verified staging folder to install destination
      -> restore backup if second rename fails
\`\`\`

The program never ships upstream compiled Aseprite binaries. Upstream \`build.cmd\` is not used because it ends in \`pause\`.

## Known limitations before public release

- Needs a real Windows 11 x64 machine for full source-build smoke testing.
- Winget may require elevation, installation acceptance, reboot, or a working App Installer.
- Large downloads, Skia, compilers and available disk space can cause environmental build failures.
- Existing installations are adopted only by explicitly choosing their folder.
- Backups currently stay on disk until manually removed.
- Automatic scheduled checks, automatic updater-of-updater, prerelease channels and macOS are future milestones.
- Smoke tests currently cover version comparison only; add integration tests for failed clone/build/verification and swap failures.

## Test matrix

| Scenario | Status |
|---|---|
| Windows CI builds native WPF EXE and Inno installer | CI configured, result pending |
| Stable version equal to source \`-dev\` | Automated unit smoke test |
| Offline check surfaces helpful error | Manual test pending |
| Fresh Windows 11 VM, missing all dependencies | Manual test pending |
| Fresh install through GUI | Manual test pending |
| Upgrade from older Aseprite source build | Manual test pending |
| Failed clone / build leaves original untouched | Manual test pending |
| Interrupted swap restores backup | Manual test pending |
| Custom extensions survive upgrade | Manual test pending |
| Windows source installation launches correctly | Manual test pending |
