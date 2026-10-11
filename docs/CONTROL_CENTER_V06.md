# Control Center v0.6.0: Update Review, Recovery and Project Profiles

Updated 2026-10-11. Windows 11 primary. **No sprite or asset generator:** all art creation stays in Aseprite.

## Unified update review

The Control Center checks the latest stable Aseprite source release, Aseprite Manager's official GitHub releases, installed themes, curated Lua tools, managed palettes, Tile Index Overlay, and the Godot Wizard for a selected/saved Godot project. Each check reports its own errors without stopping other checks. None installs or executes updates silently. Aseprite source builds retain their explicit confirmation; scripts and plugins must be reviewed inside the existing resource library. Manager self-update is currently a **release-check and official release link**, NOT an automated self-installer. Signed release distribution and transactional rollback must come first.

## Transactional Aseprite profile recovery

The previous full ZIP profile export remains. The recovery UI previews a user-selected ZIP before allowing restore. It checks file paths, case-insensitive duplicates, file sizes and total decompressed size, symlinks, supported archive paths and recognizable profile contents. Restore requires explicit confirmation and closed Aseprite. It extracts to a sibling staging directory before touching the active profile, renames the previous full profile to a timestamped backup folder, and activates the new one. Activation errors attempt to restore the previous complete folder. The previous profile is never deleted automatically.

Aseprite profile lives under %APPDATA%\Aseprite. Backup/restore includes scripts, themes, brushes, palettes and settings. This does NOT include sprites and project files stored elsewhere. Restoration may reintroduce user-installed Lua or third-party extensions; only use trustworthy archives. Save the ZIP outside the Aseprite profile.

## Portable project profiles

Profiles contain a name, notes, optional Godot project path, and allowlisted resource IDs for installed Manager tools/themes/palettes/extensions and an optional Godot Wizard. Profiles are saved as local JSON under %LOCALAPPDATA%\Aseprite Manager\project-profiles, can be exported/imported, and display missing resources on the current computer. Importing or selecting a profile **never changes Aseprite, Godot, or art files**. It is a portable inventory and setup checklist rather than an unsafe silent profile switch. Computer-specific absolute Godot paths may need re-selection after import.

## UI/UX

Main app gains a prominent Control Center card. The new window contains 3 scrollable pages: Update Center, Recovery, Project Profiles. Main Aseprite updater and separate Creator's Library remain available. Windows layout screenshots at minimum and desktop sizes are added to CI. All destructive operations display explicit confirmations.

## Tests

- Versioning and existing Aseprite installation, script, theme, palette and Godot Wizard smoke tests remain.
- Transactional restore tests validate complete old-profile retention, brush recovery, archive inspection and rejection of traversal paths.
- Project profiles test JSON import/export, duplicate suppression and unknown resource rejection.
- New WPF screenshot tests for all three Control Center pages.
- Actual customized profiles and real-world restore workflows still need device tests before marking the feature production-proven.

## Deferred deliberately

A background scheduler, unattended Update All, self-installer replacement, automatic portable profile activation and full Aseprite-to-Godot art export pipeline require further integration and rollback validation. None should be advertised as complete yet.
