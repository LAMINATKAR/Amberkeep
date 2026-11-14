# Amberkeep

**Folder backups you can see before they happen.**

Amberkeep keeps exact copies (mirrors) of the folders you choose on one or more backup disks. Before anything is changed, it shows you every file that would be copied, replaced or removed, and lets you untick what you don't want. Files on the backup disk that are not part of the backup are never touched.

It is a single portable program for Windows: no installer, no database, no background services. Everything it stores lives next to the executable.

## Features

- **One-way mirror** of selected folders to any number of targets (external disks, USB sticks, other internal disks).
- **Preview and approval**: a list of changes with checkboxes – new, modified, newer on the target, removed, renamed by case only. Approval before every run can be switched off per backup.
- **Disks recognized by a marker**, not by drive letter. A disk that comes back as `F:` instead of `E:` is still found; a different disk at the same letter is never written to.
- **Safety guards**:
  - stops when the source is missing, empty or unreadable, or when source and target overlap,
  - asks before removing many files (more than 50 % or at least 1,000 files, adjustable),
  - checks free space before starting,
  - copies to a temporary name and renames only when complete, so an interrupted run never leaves half-written files.
- **Optional archive**: replaced and removed files are kept on the target for a number of days, with a browser to restore them.
- **Comparison** by size and modification time (±2 s, with a FAT daylight-saving fix), or byte by byte.
- **Exclusions** with simple gitignore-style patterns (`node_modules/`, `*.tmp`, `/build/`).
- **Runs in the background** from the system tray:
  - automatic runs when a target disk is connected, or on a schedule (daily, chosen weekdays, monthly, every N hours),
  - progress ring in the tray icon and small notifications,
  - optional start with Windows.
- **One-time run**: mirror any folder once without saving anything.
- **History** of every run with export to HTML or text, **export and import** of backup settings to another computer.
- **Clean up everything**: removes the settings, history, autostart entry and (optionally) the markers on your disks. Your backed-up files are never deleted.

## Download and start

1. Download `Amberkeep-<version>-win-x64.zip` from the releases.
2. Unpack it into a folder you can write to, for example `D:\Tools\Amberkeep` (not `Program Files`).
3. Start `Amberkeep.exe`. No .NET or other runtime needs to be installed.

Windows may warn about an unknown publisher (SmartScreen), because the program is not code-signed. Choose **More info → Run anyway**.

Requirements: Windows 10 or 11, 64-bit.

### Where Amberkeep keeps its data

```
Amberkeep.exe, *.dll, LICENSE …   the program
amberkeep.json                    your backups and settings (plain JSON, can be copied)
history/                          one record per run
```

On each target disk Amberkeep writes only into a `.amberkeep/` folder (the marker, temporary files during a run and the optional archive). The source folders are only ever read. Outside its own folder Amberkeep writes nothing, except the autostart entry if you switch on *Start with the system*.

To move Amberkeep, move the whole folder. To remove it, use **Settings → Clean up everything** and delete the folder.

## Quick start

1. **New backup** → choose the source folder.
2. **Add target…** → choose a folder on the backup disk (for example `E:\Backup\Photos`).
3. **Save**, then **Analyze** → review the list of changes → **Run**.

The first run copies everything. Later runs copy only what changed.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
dotnet run --project src/Amberkeep.App
```

Release build (portable zip in `artifacts/`):

```bash
powershell -File build/publish.ps1
```

Project layout:

```
src/Amberkeep.Core      scan, diff, execution, safety checks (no UI)
src/Amberkeep.Storage   amberkeep.json, history, target markers
src/Amberkeep.App       Avalonia UI, tray, notifications, scheduler
tests/Amberkeep.Tests   xUnit tests (temporary folders only)
tools/                  developer tools: diff printer, screenshots, icon generator
```

[`SPEC.md`](SPEC.md) describes the behavior in detail (in Slovak).

The code is cross-platform (.NET + Avalonia), but only the Windows build is packaged and tested so far. Linux and macOS builds are planned.

## License

Copyright © 2026 LAMINATKAR.

Amberkeep is free software under the [GNU General Public License v3](LICENSE). It comes with no warranty.

It includes open-source components under their own licenses (MIT, BSD, SIL OFL); see [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt).
