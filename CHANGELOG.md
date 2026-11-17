# Changelog

All notable changes to Amberkeep. Versions follow [Semantic Versioning](https://semver.org/).

## [1.1.0] – 2026-11-17

First public release.

### Added

- **Show in folder** from the list of changes: right-click a row and choose *Show in source folder* or *Show in target folder* to open the file manager with the item selected. The option is off on the side where the item does not exist; an item gone since the analysis opens its nearest folder. Files are never opened or started from the list.
- **Continue in background** on the backup page goes back to the overview while the backup keeps running. The overview shows its progress, then its result, with a button to return to it.

### Changed

- Deleting a backup keeps its past runs in History, marked *Deleted*; they go with the history limit like any other run. *Also delete its runs from History* in the delete dialog removes them right away.
- Speed and time estimate on the backup page no longer jump when the speed changes.

### Fixed

- The scrollbar no longer covers the last column (*New*, *Removed*, sizes) in History and in the list of changes.
- The result heading in History is aligned with the buttons next to it.

### Security

Fixes from a security review of the whole code.

- **Import of backups:** before importing, a summary shows what each backup in the file would set up (source, targets, automatic runs, whether it asks before every run, a switched-off removal guard) and marks paths on another computer. Nothing from the file is checked on disk before you confirm; the backups are then imported with the settings shown.
- **Archive and restore** never work through links: the archive browser refuses a target whose `.amberkeep` or `archive` folder is a link, and a restore only writes plain names under the chosen folder, never through a link. The disk marker is not written through a linked folder either.
- Names that Windows changes (ending with a dot or a space, names the running Windows treats as a device, like `NUL`) on a disk written by another system are skipped instead of hitting a different file.
- A write the safety guard refuses now fails only its item; the run goes on and its history is saved.
- Exclusion patterns are matched in linear time, so no pattern can stall an analysis.
- Damaged history records and invalid characters in paths give a clear result instead of an error.
- SPEC 4.5 now states what the safety guards protect against, and what is up to securing the computer.

## [1.0.2] – 2026-11-14

Fixes from a second review of the whole code.

### Fixed

- A folder renamed only in letter case now converges again for case-sensitive backups; the "back in the source" check follows the backup's own name rules.
- A folder shared from this computer (`\\PC\share` or a mapped drive) is recognized as its local folder, so a target cannot overlap the source through a share. If that cannot be checked, the run is blocked with a hint to use the local path.
- Folders above a target item are checked for links before writing.
- Imported backups and a target found at a new place are checked against other backups before use.
- Schedule values are corrected on load; switching a schedule on does not start a run at once.
- *Clean up everything* never locks the program; *Quit* always exits; the window no longer freezes on an unreachable network path; the backup screen no longer gets stuck.

## [1.0.1] – 2026-11-14

Adjustments after the first test, and fixes from a review of the whole code.

### Changed

- Window: default size 1120 × 720, minimum 900 × 560; it shrinks to fit smaller screens.
- Windows-style auto-scroll with the middle mouse button.
- The mass-removal guard asks when **either** limit is reached (more than 50 % of the target's files, or at least 1,000 files); each limit can be switched off.
- Schedules: weekly on chosen days, monthly on a day 1–31 (31 = last day of every month).
- Clearer labels and hints in the backup editor and the one-time run.

### Fixed

- Source and target are compared where they really are on disk (junctions, symbolic links, `subst` drives, 8.3 names, shares of the local disk), so an aliased target can never delete source files.
- Nothing is written through a linked `.amberkeep` folder; folder trees are never deleted through a link.
- *Clean up everything* deletes only files Amberkeep wrote.
- Names that differ only in Unicode form (e.g. from a Mac) are renamed instead of duplicated.
- Windows sign-out and shutdown are no longer blocked; *Quit* stops a running backup cleanly.
- Unexpected errors are written to `amberkeep-error.log` and shown instead of crashing silently.

## [1.0.0] – 2026-11-14

First version, for Windows (portable zip, win-x64).

- One-way mirror of folders to any number of target disks, recognized by a marker instead of the drive letter.
- List of changes with checkboxes before a run; approval can be switched off per backup.
- Safety guards: missing or empty source, overlapping folders, mass removal, free space; atomic copies.
- Optional archive of replaced and removed files, with a browser to restore them.
- Background mode in the tray: runs when a disk is connected or on a schedule, with notifications.
- One-time run, history with HTML/text reports, export and import of backups, *Clean up everything*.

[1.1.0]: ../../compare/v1.0.2...v1.1.0
[1.0.2]: ../../compare/v1.0.1...v1.0.2
[1.0.1]: ../../compare/v1.0.0...v1.0.1
[1.0.0]: ../../releases/tag/v1.0.0
