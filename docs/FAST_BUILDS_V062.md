# Fast Aseprite Version Switching (v0.6.2)

## Why builds were slow

Previously every upgrade/downgrade allocated a new temporary source directory, recursively cloned the official Aseprite repository, downloaded the Skia binaries through upstream \`build.sh\`, compiled roughly 1,500+ Ninja build steps and **deleted the entire temporary workspace**. Even switching back to a recently built tag repeated this process.

The build helper had already been fixed in v0.6.1 to run through a space-free temporary \`subst\` drive, because older Aseprite release tags use unquoted shell paths.

## Fast path: verified compiled versions

- After a successful source build reports the requested version, Manager stores a **pristine copy** of \`build/bin\` plus its official bundled icon under \`%LOCALAPPDATA%\Aseprite Manager\compiled-versions\`.
- The cache does **not** include sprites, manually copied custom files, user-created palette/theme changes, or other files preserved from the prior installation, because caching happens **before** \`InstallSafety.PreserveUserContent\`.
- Next time the same official release tag is selected, it verifies every cached file against a SHA-256 manifest, copies the package into a fresh staging directory, verifies the staged files again, and runs the executable with \`--version\`. Only after those checks does Manager continue through the normal preservation, full backup, atomic rename and rollback process.
- If verification fails, Manager discards the staged cache copy and falls back to a full official source build, without replacing the current installation.
- The cache is **local to this computer** and is not redistributed as a prebuilt Aseprite binary.
- Capacity is bounded to **five completed versions or 3 GiB**, retaining more recently used packages. Cache size is shown in Build Performance. Users can clean compiled builds without deleting installed Aseprite or previous-install backups.

**First use of an uncached release still requires compilation.** No compile speed is promised for an unbuilt version.

## Shared Skia dependencies

The upstream build helper reads the full revision in \`laf/misc/skia-tag.txt\`, but stores the download under a version family such as \`skia-m124\` inside the temporary source tree. Manager now creates a short-path NTFS junction from the disposable \`source\.deps\skia-mNNN\` folder to a persistent cache directory keyed by a SHA-256 hash of the **full** revision tag. This:

- Reuses the Skia library files and existing ZIP downloaded during previous compatible builds.
- Avoids re-downloading large packages each time.
- Keeps paths that old versions of upstream \`build.sh\` can process even if \`AppData\Aseprite Manager\` contains spaces.
- Removes only the **junction** after the build, not its contents. Failure to establish a junction falls back to upstream's unmodified download behavior.
- Never patches the Aseprite source or touches other users' folders.

Clearing Skia cache is an explicit action in Build Performance.

## Compiler cache and parallelism

- If installed, Mozilla's \`sccache\` can be used as the C and C++ compiler launcher through CMake/Ninja environment variables. Cached objects live under \`%LOCALAPPDATA%\Aseprite Manager\compiler-cache\`, with a 10 GiB size setting and a server I/O failure fallback flag.
- \`sccache\` can be installed explicitly via **winget \`Mozilla.sccache\`** in the Build Performance window. It is **optional**. CMake/MSVC versions and debug flags affect whether individual objects can be cached, and not every release benefits equally.
- Set Ninja's \`CMAKE_BUILD_PARALLEL_LEVEL\` automatically to up to 12 logical cores (minimum 2), or manually choose 2, 4, 6, 8, 12 or 16. Higher parallelism is not always faster on RAM-limited laptops.
- Settings apply to **new source builds**, not an already-running build.

## UI and safety

The main Manager dashboard now includes **Build Performance** with cache sizes and independent buttons to remove (1) compiled binaries, (2) downloaded Skia, or (3) compiler cache. Clearing requires confirmation and locks against another ongoing build. Projects, active Aseprite binaries, themes, configurations and previous-install backups are not in these caches and are not deleted.

If another Manager process is building, the cache lock prevents concurrent deletes and builds. An unexpected cache entry or failed hash results in no cache activation. The verified release goes through the same post-swap \`--version\` check and previous-install backup as before.

## Testing

Automated Windows CI:
- SHA-256 digest and cache-staging tests, including malicious edits to cached files.
- Reusable Skia NTFS junction tested with physical AppData-like directories containing spaces, and confirmed persistent after the source workspace is removed.
- Exclusive cache lock and bounded parallel job settings.
- Existing version comparison, v0.6.1 build-path regression, source prerequisites and full suite of plugin/theme/palette/Godot integration tests.
- New Build Performance WPF layout captures at 650×560 and 810×715.
- Compiled Manager EXE and Inno Setup installer.

**Not claimed:** a complete compiled Aseprite 1.3.x version-switch timing benchmark or a guaranteed sccache MSVC hit rate on a real developer laptop. Those require device-side measurements; the next acceptance test is an uncached downgrade followed by a return to the same cached release.
