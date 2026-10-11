# Aseprite Manager roadmap

## Shipped in Windows v0.3.0 preview
- Curated Aseprite theme gallery with separate install / update buttons.
- Aseprite source-release version selector and explicit Update Aseprite to latest action.
- Official icon repair for Windows Start Menu shortcuts.
- Safety checks: theme archives must contain manifest and theme.xml, reject ZIP traversal and symlinks, skip scripts/binaries, keep full backups, and refuse overwrites if managed theme files changed.

## Priority 1: make core maintenance dependable
1. **Real-world integration tests** on Windows 11: fresh app install, Aseprite compile, theme install, upgrades, downgrades, auto rollback and existing extensions. CI currently checks source compilation and theme ZIP processing but not all Windows end-to-end behavior.
2. **Update notifications**: optional daily background check without forcing a build or deleting projects; user controls frequency and metered-network behavior.
3. **Restore previous version** UI: list full retained install backups, verify the rollback target, switch back atomically, and always ask for confirmation.
4. **Update Aseprite Manager** itself through signed, verifiable GitHub releases, with rollback.
5. **Disk storage manager**: show size of compiled source caches and retained backups; never delete projects or local theme edits without explicit confirmation.

## Priority 2: customization center
6. Theme cards with authors, screenshots, contrast info and direct repository/source links. Test at 100%/125%/150%/200% Windows scaling.
7. Safe **Export/Import customization profile**: copy themes, extensions, palettes, brushes, keyboard shortcuts and settings into a user-selected backup archive.
8. Theme enable/disable and uninstall with restoration options; never overwrite Aseprite's own default theme.
9. Curated and permission-labeled scripts/palettes/brushes. Scripts can execute code and require an explicit trust model, unlike theme-only archives.
10. Save theme preferences per Aseprite version, particularly to avoid applying outdated theme assets to a new major release.

## Priority 3: platforms / experience
11. Native macOS app after the Windows pipeline is field-tested.
12. Smarter build cache or incremental build capability, including Skia compatibility detection.
13. Preview changes and user-facing release notes before upgrading, with a warning for project formats unsupported by older versions.

## Non-goals and safety rules
- Never bundle compiled Aseprite binaries in Manager releases.
- Never silently update executable code or modify source projects.
- Never overwrite managed theme files edited after installation.
- Never promise all historical Aseprite tags will compile with current MSVC/Skia.
