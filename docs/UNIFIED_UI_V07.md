# Unified UI/UX Shell (v0.7.0)

Windows-first. The Manager remains a configuration and installation companion, not a sprite editor.

## Design decisions

The old application had four WPF windows, each containing a long scrolling page. Users had to dismiss modal windows to return to Aseprite version management. v0.7 replaces this with **one persistent application window**:

- **Sidebar:** Overview, Aseprite, Themes, Creator's Library, Update Center, Project Profiles, Recovery, Build Performance and Activity Log.
- **Overview:** concise installation snapshots and direct actions instead of three full-height navigation cards.
- **Aseprite:** official release picker, installation destination, prerequisites, build progress, install/launch actions.
- **Themes:** curated theme picker, install/update/remove and shortcut/icon repair.
- **Creator's Library:** internal tabs for Tools, Palettes, Import, Extensions, Godot, Brushes and Backups, with only the chosen category visible.
- **Update Center / Profiles / Recovery:** existing controls presented as embedded pages in the main shell, including active internal tabs.
- **Build Performance:** cache statistics, sccache and CPU settings as an embedded page.
- **Activity:** dedicated readable log with a persistent sidebar progress summary.

Dialogs for selecting files, choosing project folders, and confirming writes remain normal operating system dialogs. The application no longer opens its three main feature areas in separate windows.

**Behavioral safety:** install/update/restore logic is reused, with its existing version verification, cache integrity, backups, user-content protection, and confirmation prompts. The root window refuses to close while a resource operation is busy.

## Layout rules

- Keep one task group visible instead of relying on stacked card sections and scrolling past unrelated features.
- Sidebar stays visible while a page has its own scroll position.
- Task-specific actions belong to the relevant page; Library status and script management remain on Tools.
- Use a normal light-dark readable text hierarchy, a single accent for the active route and subdued inactive controls.
- Allow narrower windows to scroll horizontally never: wrap descriptive text, preserve minimum interactive control widths and use a vertical scrollbar.
- Keep version-build progress accessible from the persistent sidebar, including when viewing Activity.
- Avoid duplicating artwork editors or adding automated sprite creation.

## Visual QA pipeline

`src/AsepriteManager/LayoutSmoke.cs` renders the **entire unified MainWindow**, not individual feature panels, at three tested window sizes:

- 1024×720 compact laptop
- 1366×900 standard window
- 1920×1080 desktop

The capture runs for all 15 routes: overview, aseprite, themes, tools, palettes, palette-import, extensions, godot, brushes, backup, updates, recovery, profiles, performance and activity. For every page that can scroll, it also captures the middle and bottom. The test checks the shell/sidebar dimensions and key critical controls.

`tools/generate_ui_review.py` assembles these into paged contact sheets and a machine-readable `review-index.json`. CI attaches the original full-resolution screenshots and contact sheets to each Windows run. Actual layout review means inspecting these images for clipping, unintended overlapping text, empty/awkward panels, visually confusing controls and excessive scroll.

This suite cannot automatically prove usability or accessibility; real user validation remains necessary. Future passes should add keyboard focus and tab-order tests, Windows high-DPI captures, screenshots of running/stale/error states, and baseline image-diff checks after intentional design signoff.

## Functional acceptance checklist

1. Start the app and switch across all sidebar pages without any new WPF window opening.
2. Choose Resource Library and switch through each internal tab; verify content changes and scroll resets.
3. Open Update Center, Recovery, Profiles, Build Performance and Activity without losing context.
4. Select an Aseprite release, perform a cached switch and confirm the install log and sidebar progress remain available.
5. Open file/folder/confirmation dialogs from embedded controls: they remain owned by the main window and don't appear behind it.
6. Verify an ongoing installation or resource update cannot be lost by closing the main application.
7. Inspect all CI screenshot sheets, including compact views and scrolled pages, before merging.

## Remaining beyond the pass

Optional improvements after the unified layout: system DPI scaling at 125–200%, keyboard-only navigation, semantic screen-reader labels, live screenshot tests for dialogs and application errors, and richer resource cards/preview thumbnails. These have not been claimed as complete.
