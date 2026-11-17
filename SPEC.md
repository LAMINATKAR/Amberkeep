# Amberkeep – specification

> Status: version 1.1.0 (2026-11-16). Chapter 8 lists what is done and what comes later.

## 1. Purpose

Amberkeep is a general-purpose desktop program for backing up selected folders to one or more targets (internal HDDs, external disks, etc.).

- **The default mode** is one-way mirroring. Two-way synchronization is planned for a later version (chapter 4.3).
- Before execution, the program can show a diff for approval.
- The program is not tied to a specific computer or to specific disks.

## 2. Terms

| Term | Meaning |
|---|---|
| **Job** (backup) | One source folder + one or more targets + mode + exclusions + options. |
| **Target** | A folder on some disk that is backed up to. It is recognized by its marker, not by drive letter. |
| **Marker** | The file `.amberkeep/target.json` in the target folder, holding the unique target ID. |
| **Archive** | Optional storage place **on the target** for files that would otherwise be deleted or overwritten. |
| **Run** | One execution of a job for one target: analysis → (approval) → execution. |
| **One-time run** | A run without a saved job and without any working files (chapter 4.7). |

## 3. Decisions made

- **The source is never written to in Mirror mode.** The source is only read; no working file is created on it. In Two-way mode only synchronized data is written to the source, never working files. The archive and the marker are always only on the target.
- **Job modes:** *Mirror* (one-way). *Two-way sync* is **deferred to a later phase** (after the first version is released). Chapter 4.3 is only a draft so far.
- **Only the selected folder is backed up.** The program ignores everything on the target disk outside the target folder.
- **Archive** is optional (per job) and **off by default**.
- **One source per job.** Several folders = several jobs. The dashboard has a *Run all* button.
- **Default trigger: none** (manual start). Automatic triggers are turned on in the job.
- **Diff** shows categories of changes; every item and folder can be ticked or unticked. Approval is turned on by a switch and **also applies to the first run**: if approval is off, the first run does not ask for approval either.
- **No database.** Configuration is in a JSON file, history in files next to the exe (chapter 5).
- **Portable mode:** everything is next to the exe. Outside the exe folder the program writes only to target folders and, with the start-with-the-system option, to autostart.
- **Working files can be removed at any time** with a single action (chapter 4.8).
- **Technology:** C# / **.NET 10 (LTS)** + **Avalonia UI**. Version 1.0 is for Windows (win-x64). The code is ready for Linux and macOS too; their packaging and verification come after 1.0.
- **Distribution:** a portable folder (zip) with a self-contained, single-file `Amberkeep.exe` and three native libraries. Nothing needs to be installed, not even .NET (chapter 7.1).
- **UI in English.**
- **Background mode** with a tray icon that also shows progress, and with automatic triggers (chapter 4.6).

## 4. Synchronization logic

### 4.1 Comparison

- By default, **size + last modification time** are compared, with a 2 s tolerance (because of FAT/exFAT).
- Optionally, comparison is **by content**: files of equal size are read on both sides and compared **byte by byte** (not by hash); the comparison stops at the first difference. It is slower but exact, and no cache is needed. A file with a different size is always modified.
- Symbolic links and junctions are **not followed**. They are listed under *Skipped*, and nothing under the same path is changed on the target.
- Names are compared according to the platform (Windows and macOS case-insensitive, Linux case-sensitive). This can be switched in the job settings.
- Names are compared in Unicode form NFC. A name stored decomposed (NFD, typically macOS) is the same name as a composed one (NFC, typically Windows). On Windows and Linux, where the file system keeps both forms as different names, a difference only in form is treated as a rename (CaseRename), so that no second folder appears on the target. Not on macOS (1.0.1).
- **Name collision:** if one side has two names in one folder that differ only in letter case or Unicode form (e.g. a case-sensitive folder from WSL or git), it is unknown which is which. Both are listed under *Errors*, and nothing under their path is changed on the target (1.0.1).
- **Exclusions apply on both sides.** Excluded items are not backed up from the source and not deleted on the target.
- **An unreadable folder** (missing permissions, etc.) is listed under *Errors*. Nothing under its path is deleted or written on the target, because its contents are unknown.
- Hidden and system files are backed up.
- **Names Windows cannot handle reliably** (on Windows): a name ending with a dot or a space, or a name the running Windows treats as a device (always `NUL`, on Windows 10 also e.g. `CON` or `aux.c`), can exist on a disk written by another system. Windows would apply an operation on it to a different file (`file.` means `file`). The program asks Windows how it reads each name. Such an entry is listed under *Skipped* and nothing under its path is changed (1.1.0).
- **Exclusion patterns** are matched in linear time (no backtracking), so no pattern, including one from an imported file, can stall an analysis (1.1.0).
- **One-hour shift:** FAT (FAT12/16/32) stores local time, so after a daylight-saving change all times differ by exactly 1 h. If the source or the target is on a FAT volume, files of equal size whose time differs by 1 h (± tolerance) are treated as unchanged. This does not apply on NTFS, exFAT, APFS and ext4 (they store UTC), so that a real edit is not missed. Job option, on by default.

### 4.2 Mirror mode (one-way): categories in the diff

| Category | Situation | Action | Ticked by default |
|---|---|---|---|
| **New** | file is on the source, missing on the target | copy | yes |
| **Modified** | file is on both, differs, source is newer | overwrite (move the old version to the archive, if enabled) | yes |
| **Target newer** | file is on both, differs, target is newer | overwrite with the version from the source | **no** (warning) |
| **Timestamp only** | only in content comparison mode: content is equal, only the time differs | align the time on the target | yes |
| **Case rename** | same content, name differs only in letter case (`Photo.JPG` ↔ `photo.jpg`) | rename on the target | yes |
| **Removed** | file is on the target, not on the source | move to the archive or delete | yes |
| **Unchanged** | equal | nothing | – (hidden, count only) |
| **Error** | file inaccessible or locked, missing permissions | skip and log | – |
| *Moved* (later phase) | same content, different path | move on the target instead of copying again | yes |

- Categories apply to files and folders. **Empty folders are mirrored.** A removed folder is broken down in the diff into individual items.
- If a file differs both in content and in the letter case of its name, it is one *Modified* item, and the name on the target is changed at the same time.
- If a file changed into a folder with the same name (or vice versa), the old one is removed on the target (*Removed*) and the new one is created (*New*).
- Mirror needs no saved state: the diff is created by comparing the source and the target directly.

#### Execution (Mirror)

Only **ticked** items are executed, in this order:

1. **Removals:** files first, then folders from the deepest. A file goes to the archive or is deleted (the read-only attribute is cleared before deletion). A folder is deleted only when it is empty; otherwise it stays and a note is written to the result (e.g. the user unticked something in it). Removals go first to free up both space and the path (a file replaced by a folder).
2. **Renames** (letter case change): folders first, top-down, then files, always through a temporary name.
3. **New folders** top-down. A parent folder needed by a ticked file is created even when it is unticked itself.
4. **Copying** (New, Modified, Target newer): the file is copied to `<target>/.amberkeep/tmp/`, written to disk (flush), optionally verified by comparing it with the source, and gets the modification time, creation time and the ReadOnly/Hidden/System/Archive attributes from the source. The original version on the target goes to the archive or is deleted, and the new one is moved into its place. Without the archive, the replacement is atomic. **ACLs, owner and alternate data streams (ADS) are not copied.**
5. **Times** (Timestamp only).

- **Stale diff check:** before each operation, it is verified that the source and the target still have the same size and time as during analysis, and for *New* that nothing has appeared on the target in the meantime. If something changed, the item is skipped with the error "Changed since analysis – analyze again". This way a file edited in the meantime is never deleted or overwritten.
- Moves and deletions are retried briefly on a transient error (access denied, file in use by another process, typically an antivirus right after writing), at most 3 times within ~0.6 s.
- **Errors** of individual items are recorded and the run continues. This includes a write the write guard refuses: nothing is written, the item fails with the reason, and the history record is saved (1.1.0). **Interruption** stops the run between operations or during copying. A half-written file is never left on the target under the real name.
- Each run copies into its own subfolder `.amberkeep/tmp/<time>-<id>/`, so two runs to the same target do not overwrite each other's temporary files. The folder is deleted at the end of the run. Leftovers from an interrupted or crashed run older than 24 h are deleted by the next run. An empty `.amberkeep` is removed.
- If free space on the target cannot be determined (e.g. network storage), the run continues and the result contains a note about it.
- A rename that changes only letter case goes through a temporary name next to the original (`.<name>.amberkeep-<id>`). If a run crashes right between the two steps, the file stays under this name and the next run fixes it: it removes the temporary name and copies the file from the source again.
- **Write guard:** every write is checked in code to go into the target folder and not into the source.

### 4.3 Two-way sync mode – deferred

> Not part of the first version. The draft is kept here so that the architecture accounts for it. Details will be refined before implementation.

Two-way synchronization needs to know **on which side** a change happened and whether a file was deleted or is new. Therefore, after each successful synchronization the program saves a **state snapshot** (a list of files: path, size, time) for each source–target pair. The snapshot is next to the exe, not on the source (chapter 5).

Each side is compared with the snapshot:

| Source | Target | Action | Ticked by default |
|---|---|---|---|
| new / modified | unchanged | copy → target | yes |
| unchanged | new / modified | copy → source | yes |
| deleted | unchanged | delete on the target (to the archive) | yes |
| unchanged | deleted | delete on the source. If the archive is on, the copy from the source is first moved to the archive **on the target**, so nothing is created on the source. | yes |
| modified | modified (differently) | **Conflict:** according to the job option (see below) | yes, according to the option |
| modified | deleted | the change wins: the file is restored on the target | yes |
| deleted | modified | the change wins: the file is restored on the source | yes |

- **A conflict** occurs when the same file has changed **on both sides** since the last synchronization. Example: you edit `report.docx` on the SSD and at the same time someone edits its copy on the external disk. The program does not know which version is correct. Conflicts do not occur in Mirror mode.
- **Conflict resolution** (job option):
  - *Keep both* (default, nothing is lost): both versions are kept and the older one is renamed to `name (conflict 2026-11-13 2015).ext`,
  - *Newer wins*,
  - *Source wins*,
  - *Ask* (chosen manually in the diff).
- **The first synchronization** (no snapshot exists yet) is a merge: both sides are combined, nothing is deleted. Files that exist on both sides and differ are handled as a conflict.
- **Several targets:** the source is the hub. Each target is synchronized with the source separately, so a change from target A reaches target B at its next synchronization.

### 4.4 Archive

- Location: `<target>/.amberkeep/archive/<yyyy-MM-dd_HHmmss>/<relative path>`. In Two-way mode the archive has subfolders `source/` and `target/`, according to where the file came from.
- The `.amberkeep` folder is always excluded from comparison.
- One snapshot folder per run. The name is in local time; if it already exists, `-2`, `-3`… is appended.
- Cleanup: keep forever, or delete snapshots older than N days (option per job). **Default 30 days**, the same as the recycle bin in most services. Cleanup runs automatically on every run of that job with the archive enabled. The age is determined from the snapshot name; folders with a different name are not deleted.
- The archive can be browsed from the UI and files restored from it.

### 4.5 Safety guards

1. **The source is missing, unreadable, or empty** (after exclusions it has no file while the target has files) → the run stops. This prevents deleting the entire backup, for example when the source disk is not connected. The run also stops if the target folder cannot be read.
   - The run also stops when **the source and the target overlap** (the same folder, or one inside the other; judged by the platform's letter-case rules) or when **the target path is a file**.
   - **Overlap is also judged by the real location on disk** (1.0.1). A junction, a symbolic link, a `subst` drive, a short 8.3 name, the `\\?\` prefix and sharing the computer's own disk over the network all lead to the same folder, and that folder is what is compared (real path via `GetFinalPathNameByHandle` and folder identity: volume serial number and file ID). The check runs during analysis, when a backup is saved, when a target is added, on import, and again right before the first write. Found in code review: a target reached through a junction to the source otherwise deleted source files.
   - **A shared folder of this computer** (`\\PC\share` or a mapped drive) is converted to a local path via the same volume serial number and 128-bit file ID (`OpenFileById`) and compared. If this is not possible, the run is blocked with advice to use a local path (1.0.2).
   - **Amberkeep's own folder** (settings and history next to the exe) must not be the target, and the target must not be inside it or above it: the run stops and the backup cannot be saved. The source must not be inside it. If the program folder is inside the source, it is excluded automatically (not copied and not deleted on the target) and the diff says so (1.0.1).
   - The executor refuses to write if `<target>/.amberkeep`, `tmp` or `archive` is a link or junction. A folder that is itself a link is never deleted "through" the link (1.0.1).
   - A removal is skipped ("analyze again") if the same path has come back to the source in the meantime. Replacing a file whose name differs only in letter case, without the archive, first puts the old file aside in `tmp` and returns it if the new one cannot be put in place (1.0.1). The path is looked up in the source part by part according to the backup's rules (letter case, Unicode form), not the file system's, so that a case-only rename works even in a case-sensitive backup (1.0.2).
   - Before writing, it is verified that no parent folder of the item on the target has become a link in the meantime (1.0.2).
   - **The archive browser, restore and the marker** never work through links either (1.1.0): listing, *Clean up old* and *Delete snapshot* refuse a target whose `.amberkeep` or `archive` is a link; the marker is not written through a linked `.amberkeep`; a restored file must land under the chosen folder by plain names (no empty, `.`/`..` or invalid segments, no names Windows changes) and not through a link in that folder.
2. **Many removals:** the guard is meant to catch a disaster (wrong folder, emptied source), not routine cleanup. If it is exceeded, approval is required even when approval is off.
   - **By default** it triggers when a run removes **more than 50 % of the target's files (at least 20 files) OR at least 1,000 files**. One condition is enough, so it also catches a large deletion in a large folder (e.g. 40,000 of 100,000). The lower limit of 20 files for the percentage avoids a prompt when cleaning up a small folder (decision from testing 1.0; previously AND applied, with values 50 % and 100).
   - Both the percentage and the count can be **set per job**. Each condition can be turned off separately (100 % or 0 files), and the whole guard can be **turned off**.
   - The warning states which condition was exceeded.
   - When the guard triggers, the diff is shown with a clear warning. In the background a notification appears instead of a silent run.
   - Renaming a large folder (until move detection is implemented) looks like removal + new files, so it may trigger the guard. This is intended.
3. **Marker mismatch** → nothing is written to the target; the target is shown as "not connected".
4. **Not enough space** → the required space is checked before starting, and the run does not start.
   - Free space and the mass-removal guard are **recalculated from the ticked items** at execution. If approval is required and the run is not approved, nothing is changed.
5. **Atomic copying:** a file is first copied under a temporary name and renamed only after completion. The modification time is preserved. Optionally, the file is verified after copying by comparing it with the source.
6. **Interruption** is safe at any point. The next run continues according to the new diff. In Two-way mode the snapshot is saved only after successful completion.

**What the guards protect against (1.1.0).** They protect the user's data against mistakes and unexpected states: a missing or wrong disk, aliased paths, links, files changed during a run, damaged or hand-edited files, and content of a foreign disk or an import file. They assume the computer itself is under the user's control. They do not defend against another account or malware on the same computer that can write to the program folder, the target or the source while Amberkeep runs, nor against someone who copies a target's marker to their own disk: the marker identifies a disk, it does not prove it. Securing the computer (accounts, folder permissions) is outside the program.

### 4.6 Background mode

- After the main window is closed (by default; can be changed in Settings), the program keeps running as a **tray icon** (system tray). On Linux, where the tray is not available, it runs without an icon and communicates only through notifications. It is fully exited via the icon menu *Quit*. Only one instance ever runs.
- **Triggers** (configurable per job):
  - **Target connected:** the program detects that a volume with the marker of one of the targets has been connected.
  - **Schedule:**
    - daily at [time],
    - weekly on chosen days (Mon–Sun, several at once) at [time],
    - monthly on day [1–31] at [time]: a month without that day runs on its last day, so the 31st = always the last day,
    - every N hours.

    If a run was missed at the scheduled time, it is performed after the program starts.
- **What a trigger does:**
  - **Approval on:** the program analyzes in the background and shows a notification (e.g. "Photos → External HDD: 120 changes. Review?"). Clicking it opens the diff view.
  - **Approval off:** the backup runs automatically and a notification with the result is shown at the end. The safety guards from chapter 4.5 still apply.
  - If there are no changes, no notification is shown. The run is only recorded in the history.
- **Start with the system** is optional and off by default. It is a write outside the exe folder:
  - Windows: the `HKCU\...\Run` key,
  - Linux: `~/.config/autostart/*.desktop`,
  - macOS: LaunchAgent.

  The entry is removed when the option is turned off.
- **The tray icon shows state and progress:**
  - idle: base icon,
  - analyzing: blue dot,
  - backing up: **an amber progress ring that fills gradually** (redrawn every 5 %),
  - needs attention (waiting for approval, or a background run had problems): red badge.

  The tooltip shows the state, e.g. "Photos → External HDD – 45 %". The icon is drawn at runtime from the vector logo.
- Icon menu: *Open Amberkeep*, *Run now ▸* (list of jobs), *Run all now*, *Pause automatic runs* (checkable), *Quit*. *Run now* from the tray behaves like an automatic run.

#### Implementation (phase 6)

- **Check every 15 s:** detects the connection of targets with the "on connect" trigger (transition from "not connected" to "connected"). Targets already connected when the program starts do not start a run. It also checks scheduled targets that are due.
- **Schedule:** the next due time is calculated from the target's last run in the history (or from the target's creation) in local time, correctly also across daylight-saving changes. A missed due time runs once, as soon as possible. A manual run moves the next due time. A run record with a time in the future (clock once set wrong) is ignored, so that the schedule does not stall (1.0.1).
- **Schedule start** (1.0.2): when the schedule changes, the editor remembers the time (`sinceUtc`), and due times before it are not considered missed, so turning a schedule on does not start a backup right away. A manual backup moves the next due time immediately (the automation drops its cache on a new history record). An error with one target does not stop checking the others.
- **Automatic run:**
  - no changes → nothing is recorded and nothing is notified,
  - approval needed → the run waits for approval (in memory, a bar on the dashboard, red badge in the tray, notification) and is analyzed again when opened,
  - otherwise the default selection of changes is executed and a notification with the result appears.
- **One run at a time:** manual and automatic runs share one lock. A manual run during an automatic one reports "A backup is running"; an automatic one waits for the next check. The lock is also taken by operations that delete or change files on targets: deleting a backup, saving the editor (markers and cleanup of removed targets), *Clean up everything* (the lock is never released, so nothing is written back after the cleanup) and deleting, cleaning up and restoring in the archive (1.0.1).
- **Always the current job** (1.0.1): starting from a card, a notification or the tray reloads the job and target from the saved configuration, so the current settings are used (e.g. a just-enabled "Review changes before every run"). A deleted job or removed target is not run, and a background run does not bring a removed target back. An older dashboard refresh does not overwrite a newer one.
- **Clicking a pending-review notification** (1.0.1): if an editor, one-time run, diff, run or dialog is open, or a backup is running, the window is only shown and the review waits in the bar on the dashboard. It disappears from the bar only when its analysis actually starts.
- **Notifications** are a small window in the corner of the screen (8 s; clicking opens the relevant page). System notification services are not used, so that nothing has to be registered outside the program folder.
- **One instance per program folder** (named mutex + named pipe). A second start shows the window of the first instance, so it can be opened even where there is no tray.
- **Autostart:** the entry starts the program with `--background` (no window, tray only). The entry name contains a short form of the program path. At startup the entry is reconciled with the setting (if the program was moved, the path is fixed). With read-only settings the entry is not changed.
- **Quit** during a run asks first. Then it stops the automation, cancels the running backup, analysis or restore and waits for it at most 10 s, so that it can clean up temporary files and write the history (1.0.1).
- **Windows sign-out and shutdown** are never delayed by the program: closing the window is canceled only when the user closes it normally; when the session ends, a running backup is canceled and the program exits (1.0.1).
- **Unexpected errors** are written to `amberkeep-error.log` next to the exe (up to 1 MB, then it starts over) and shown in a message instead of a silent crash. An automatic run and the check never crash: they report the error with a notification and a red badge in the tray (1.0.1).

### 4.7 One-time run

- The user picks the source and the target directly when starting, without saving a job.
- The mode is Mirror only. Without a snapshot, Two-way cannot tell deleted files from new ones, so it is not available for a one-time run.
- **No working file is created:** no marker, no configuration entry, no history.
  - The archive is off by default. If the user turns it on, the only thing created is the `.amberkeep/archive` folder on the target.
  - The report can be saved manually at the end (*Save report…*).
- The diff and approval are always shown, because the target is not verified by a marker and the path is entered by the user.
- Later, a one-time run can also be started from the command line (chapter 8).

### 4.8 Removing working files (Clean up)

The backed-up data itself is **never deleted** during cleanup. Only files created by the program are removed.

| Action | What it removes |
|---|---|
| **Remove target** (on a target) | removes the target from the configuration. Optionally deletes `.amberkeep` on the target (the marker; the archive only after a separate confirmation) and the Two-way snapshot for this target. |
| **Delete job** | removes the job from the configuration, together with its snapshots. Its history stays (shown as deleted in History, removed by the history limit) unless *Also delete its runs from History* is ticked (change after testing 1.1.0). Optionally also cleans up all its connected targets (as above). |
| **Clean up everything** (in Settings) | removes the autostart entry, optionally `.amberkeep` on all connected targets, and all files Amberkeep wrote next to the exe (`amberkeep.json` with its `.bad-*` and `.tmp` variants, `amberkeep-error.log`, records in `history/`). Foreign files stay, e.g. the user's own copy `amberkeep.json.mine` or another file in `history/`; the `history/` folder is deleted only if it is left empty, and `state/` is not used in 1.0, so it is not touched. Program files (exe, DLL, licenses) stay. After that it is enough to delete the program folder, and nothing of the program is left. |

For targets that are not currently connected, the program warns that `.amberkeep` remained there. It can be removed later after connecting, or manually.

### 4.9 Target identification (marker)

- When a target is created, `<target>/.amberkeep/target.json` is written with the target ID, job ID and creation date.
- The configuration stores the target's ID and its **relative path** without the drive letter or mount point, plus the last known full path.
- Lookup: first the last known path. If it does not match, all connected volumes are searched at the same relative path. If the target is not found, it can be picked manually via "Locate target…".
- Marker format: `{ "format": 1, "app": "Amberkeep", "targetId", "jobId", "jobName", "createdUtc" }`.
- **Target states:**
  - **Connected:** the marker belongs to this target. Only here is anything written.
  - **Not connected:** the target was not found.
  - **Unverified:** there is a folder at the last known path, but the marker is missing, cannot be read, or belongs to another backup. For example, a different disk was connected under the same letter. **Such a folder is never written to.**
- **Locate target… (Relink):** if the manually chosen folder has this target's marker, only the path is updated. If the marker is missing or belongs to another backup, a new marker is written only after explicit confirmation.
- **Adding a target** to a folder that is already a target of another backup also requires confirmation. The original backup will then see this folder as *Unverified*.
- **Right before writing** (after the diff is approved), the marker is verified once more, in case the disk was swapped in the meantime.
- **A target at the root of a disk** (e.g. `E:`) is found after a drive letter change just like the others: the marker is also looked for at the root of every volume (1.0.1).

## 5. Data next to the exe (no database)

```
Amberkeep.exe             ← program (+ av_libglesv2.dll, libHarfBuzzSharp.dll, libSkiaSharp.dll, LICENSE, THIRD-PARTY-NOTICES.txt)
amberkeep.json             ← program settings + jobs, targets, exclusions, options (readable, can be copied to another PC)
history/                   ← one file per run (JSON: summary, errors, list of changes); cleaned up according to the setting (e.g. the last 100 runs)
state/                     ← state snapshots for Two-way, one file per source–target pair (only if Two-way is used)
```

- A database is not needed. The configuration is small, and the history and snapshots are plain files.
- Writes are atomic (write to a temporary file, then rename), so that a file is not corrupted by a crash.
- If the exe folder is not writable (e.g. `Program Files`), the program says so clearly at startup.
- A run report can be exported (HTML or TXT).

### 5.1 `amberkeep.json`

```jsonc
{
  "version": 1,
  "settings": { "historyLimit": 100, "closeToTray": true, "startWithSystem": false, "notifications": true, "automaticRunsPaused": false },
  "jobs": [{
    "id": "…", "name": "Photos", "sourcePath": "D:\\Photos",
    "exclusions": ["$RECYCLE.BIN/", "…"], "compareMode": "SizeAndTime", "caseSensitivity": "Auto", "ignoreOneHourShift": true,
    "massRemovalGuard": { "enabled": true, "percent": 50, "count": 1000 },   // 100 % / 0 = condition off
    "archive": { "enabled": false, "retentionDays": 30 },        // retentionDays: null = forever
    "requireApproval": true, "verifyCopies": false,
    "trigger": { "onTargetConnected": false,                      // run when the target is connected
                 "schedule": { "kind": "None", "time": "20:00:00", "daysOfWeek": ["Monday"], "dayOfMonth": 1, "intervalHours": 6 } },   // None|Daily|Weekly|Monthly|Interval; the old "dayOfWeek" is converted to daysOfWeek on load
    "targets": [{ "id": "…", "name": "External HDD", "relativePath": "Backup\\Photos", "lastKnownPath": "E:\\Backup\\Photos", "createdUtc": "…" }]
  }]
}
```

- **A new job** has the default system exclusions, approval on, archive off and manual start.
- **A corrupted file** is renamed to `amberkeep.json.bad-<time>`, the program starts with default values and warns the user about it.
- **A file from a newer program version** is neither used nor overwritten. The program asks the user to update. This also applies when this version cannot read it (new values): only `version` is read first, and the file is not considered corrupted (1.0.1).
- When a job is saved, the name, full paths and validity of the settings are checked. It is also checked that **no target is the same as, inside, or around another target or source, in any job**. Nested targets would delete each other's backups, and a target inside another job's source would mean writing to a source.
- If a file from a newer version was rejected, the program never overwrites it, not even on a later save.
- `null` values in a manually edited file are replaced with defaults on load.
- **Invalid values** are corrected on load, always in the "delete less" direction, and the program warns about it (1.0.1): `retentionDays` < 1 → the archive is not cleaned up (forever), > 36,500 → 36,500; the guard's percentage and count are clamped to the valid range; an unknown option value → default; a duplicate backup or target ID → the later one gets a new ID (the target must be found again via *Locate*). A backup without a source path does not run and shows a clear error. A path with a character no path may contain (e.g. NUL) is reported as invalid (1.1.0).
- **Import:** a target with an ID that already belongs to another backup gets a new ID (and is usually rejected as an overlap). **A target found at a new location** is checked before use the same way as on save (other backups, the program folder, real paths); on a conflict it is *Unverified* and the path is not saved (1.0.2).
- Corrected values are saved immediately (if writable), so that e.g. new IDs do not change on every start. Also corrected: `intervalHours` 1–744, `dayOfMonth` 1–31, a weekly schedule without days is turned off, a negative guard count → 1,000. The file may contain `//` comments and a trailing comma (1.0.2).

### 5.2 History

- There are two files per run:
  - `history/<yyyyMMdd-HHmmss>-<id>.json` with a summary that loads quickly, also when the dashboard is shown,
  - `<…>.changes.json` with the list of changes, loaded only when the detail is opened.

  The summary contains:
  - job and target,
  - start and end time, result, the reason for rejection if any,
  - operation counts and bytes written,
  - diff summary by category,
  - errors and notes, the archive folder,
  - the list of ticked changes (at most 10,000; the rest are only counted).
- Rejected and interrupted runs are recorded too.
- The last `historyLimit` runs are kept (default 100). A corrupted history file is skipped. Missing values in a readable record (e.g. a `null` list) are filled in, and change entries without a path are dropped (1.1.0).
- Export and import of backups: chapter 6.2.

## 6. UI (draft, refined during implementation)

1. **Dashboard:** list of jobs and their targets. State of each target (connected / not connected, last backup, free space), *Analyze* and *Run* buttons. A **One-time run…** button.
2. **Job editor:**
   - mode (Mirror / Two-way), source, targets (add, remove, Locate),
   - exclusions (patterns like `node_modules/`, `*.tmp`, with preset system exclusions),
   - comparison by size and time or by content (byte by byte), archive and its cleanup, conflict resolution (Two-way),
   - approval, removal guard threshold, triggers.
3. **Diff view:** a tree with colored categories, ticking (a folder ticks or unticks all its contents), filters by category, search and totals (counts, sizes, required space). For Two-way the direction is shown too (→ / ←). Buttons *Approve & run* / *Cancel*.
4. **Progress:** overall and current file, speed, time estimate, *Cancel*, *Continue in background*.
5. **History:** list of runs with result and detail (log, errors) and report export.
6. **Archive browser:** snapshots by date, restoring selected files.
7. **Settings:** start with the system, pausing automatic runs, behavior when closing the window (to tray / quit), notifications, history length, **Clean up everything**.
8. **Tray icon and notifications:** see chapter 4.6.
9. **About:** version and licenses of the libraries used.

### 6.1 Implementation decisions (phase 4)

- **One window**; pages switch within its content. Confirmations are overlay dialogs in the window.
- **Window size** (change after testing 1.0): default 1120 × 720, minimum 900 × 560. If the window does not fit the monitor's work area when first shown (laptop with scaling), it is shrunk and centered.
- **Middle-button autoscroll** like in Windows (change after testing 1.0): works in every scrollable list and page.
  - Holding and dragging ends when the button is released.
  - A click keeps scrolling on until the next click, Esc, a mouse wheel turn or a window switch.
  - Speed grows with the distance from the starting point (10 px dead zone).
- **Input fields** have the text, placeholder and caret vertically centered. Multi-line fields stay top-aligned.
- **One-time run** uses the same labels and explanations as the backup editor (Source folder, Target folder, comparison, exclusions, archive).
- **Design:**
  - Fluent theme, light and dark mode following the system,
  - amber accent color, Inter font,
  - the logo is a vector amber stone,
  - diff category colors: New green, Modified blue, Target newer amber, Removed red, Renamed purple.
- **The diff tree** is custom and virtualized (`ListBox` over flattened rows), so it handles even hundreds of thousands of items. `TreeDataGrid` is not used, because in version 12 it is not under the MIT license.
  - Folders that are not a change serve only as groups.
  - Three-state checkboxes: ticking a folder ticks or unticks all its contents.
  - Totals and the mass-removal guard are recalculated on every selection change.
  - **Row context menu** (right button): *Show in source folder* and *Show in target folder* open the file manager with the item selected (Windows `explorer /select`, macOS `open -R`, Linux only its folder).
    - The option is disabled on the side where the item does not exist (*New* on the target, *Removed* on the source). For *Case rename*, the name on that side is used.
    - If the item has disappeared since the analysis, the nearest existing parent folder is opened.
    - The file is deliberately not opened in its default application: a program cannot be launched by mistake, and an external editor cannot change the source while the diff is being reviewed.
- **Run on the dashboard:**
  - if the job requires approval, or the diff requires approval or is blocked, the diff is shown,
  - otherwise the run proceeds directly,
  - if there are no changes, "Up to date" is shown.
- **Run all:** goes through all connected targets one by one. It continues with the next target when the result or the diff is closed.
- **Progress page** (change after testing 1.1.0):
  - speed and time estimate are separate fixed-width columns with tabular digits, and the speed always has one decimal, so the estimate does not jump when the speed changes,
  - *Continue in background* returns to the dashboard while the backup keeps running. The dashboard then shows a bar with the backup's progress and *Show progress*; after it finishes, its result and *Show result*. The bar disappears when the result is closed with *Done* (only then does *Run all* continue). Opening another backup page replaces it.
- **Lists with a scrollbar** keep a right margin, so the last column (e.g. *New*, *Removed*, sizes) is not covered by the scrollbar when it widens on use.
- **The job editor** writes the markers of new targets and cleans up removed targets only on **Save**. On **Cancel** nothing is left on the disks.
- **Locate…** is on the dashboard next to a target that is not connected or not verified.
- **One-time run** always shows the diff. When it finishes, a text report can be saved (*Save report…*).
- **Developer tool** `tools/Amberkeep.Screenshots` renders all screens (light and dark) to `sandbox/screenshots/`. Paths of the sample data are shown as short example paths (`D:\Photos`, `E:\Backup\Photos`). With `--readme` it also copies the screens used by `README.md` and the social preview image (1280 × 640) to `docs/images/`.

### 6.2 Implementation decisions (phase 5)

- **History:**
  - list of runs with a filter by backup; runs of a deleted backup are marked *Deleted* and have their own filter entry "Name (deleted)",
  - detail with statistics, errors, notes and the list of changes, which is loaded only when opened,
  - **Export report…** saves HTML or TXT according to the chosen extension; the HTML is a single file without external resources.
- **The archive** is opened with the *Archive* button on a connected target that has an archive.
  - Snapshots can be browsed, deleted and cleaned up manually according to the job settings.
  - **Restore copies** the selected files, preserving folders, times and attributes; the archive is not changed.
  - Files can be restored to the original source (*Restore to original folder*) or to a chosen folder. Existing files are overwritten only when the option is ticked.
  - Restoring into the `.amberkeep` folder is not possible.
  - When restoring directly into the backup's target, the program warns that the next run will remove these files again.
- **Export and import of backups** (`amberkeep-backups.json`: `{ "format": 1, "app": "Amberkeep", "jobs": [...] }`):
  - job and target IDs are kept, so disks are recognized by their markers on another PC too,
  - **before importing** (1.1.0), the program shows what each backup in the file would set up: name, source, targets, automatic runs, whether it asks before every run, and a switched-off mass-removal guard. A path on another computer is marked *(network)*, because Windows signs in there with the user's account. Nothing in the file is checked on disk before the user confirms. Confirming the summary is the approval: the backups are imported with their settings as shown,

  - for an existing job the program offers to replace it (an option in the same dialog),
  - an invalid job (e.g. overlap with an existing target) is skipped with a reason,
  - settings and history are not exported.
- **Clean up everything** **closes the program** when finished, so that the settings are not created again.
- **About** shows the version and licenses: .NET, Avalonia and CommunityToolkit.Mvvm (MIT), Inter font (SIL OFL 1.1).
- Settings for start with the system, the tray, notifications and pausing automatic runs came in phase 6, together with the feature.

## 7. Architecture

```
Amberkeep.slnx
├─ src/Amberkeep.Core      scan, diff (Mirror, Two-way), execution, safety guards, file system and volume abstraction
├─ src/Amberkeep.Storage   amberkeep.json, history/, state/ (System.Text.Json, atomic writes)
├─ src/Amberkeep.App       Avalonia UI, MVVM (CommunityToolkit.Mvvm), tray, notifications, scheduler
├─ src/Amberkeep.Cli       (later phase) running jobs and one-time runs without UI
└─ tests/Amberkeep.Tests   xUnit, tests on temporary folders
```

The core does not depend on the UI, so that both the GUI and the CLI can use it.

Developer tools (`tools/`):
- `Amberkeep.DevDiff` is a console diff printer and runs mirror in the sandbox,
- `Amberkeep.Screenshots` renders all screens,
- `Amberkeep.IconGen` produces the program icon and the logo for the README (`docs/images/logo.png`).

### 7.1 Packaging and distribution (phase 7)

- **Output:** `build/publish.ps1` (publish profile `win-x64`) produces `artifacts/publish/win-x64/` and `artifacts/Amberkeep-<version>-win-x64.zip`. Contents:
  - `Amberkeep.exe` (about 40 MB) with the bundled .NET runtime,
  - Avalonia's native libraries, which sit next to the exe: `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`,
  - `LICENSE`, `THIRD-PARTY-NOTICES.txt`.
- **Why not a single file:** the native libraries are deliberately not inside the exe. .NET would extract them to `%TEMP%\.net\…` at startup and they would stay there. This way the program writes nothing outside its folder (decision 2026-11-14).
- **No trimming and no ReadyToRun:** simpler and more reliable. The managed part of the exe is compressed (`EnableCompressionInSingleFile`). Debug symbols (`.pdb`) are not included in the output.
- **Icon:**
  - The logo vector is in `LogoArt`. It is used by the logo in the UI, the tray icon and the program icon, so it is in one place.
  - `tools/Amberkeep.IconGen` produces `src/Amberkeep.App/Assets/amberkeep.ico` from it, which is in git: PNG entries of 16–256 px, simplified without facet lines at ≤ 24 px.
  - The icon serves as both the exe icon (`ApplicationIcon`) and the window icon.
- **Exe metadata** (`Directory.Build.props`): version (`<Version>`, also used in the zip name), product Amberkeep, author and copyright "© 2026 LAMINATKAR".
- **`app.manifest`:** Windows 10/11, DPI PerMonitorV2, long paths.
- **Linux and macOS** are not packaged yet, because they cannot be verified. The code is cross-platform (autostart, single instance, paths).

### Licenses

**Amberkeep** is licensed under the **GNU GPL v3** (`LICENSE`), © 2026 LAMINATKAR. Anyone who redistributes the program must also make the source code available under GPL v3.

The program includes the following components. The full texts of their licenses are in `THIRD-PARTY-NOTICES.txt`, which is also embedded in the exe (About → *View licenses*):

| Component | License |
|---|---|
| .NET 10 runtime and libraries (System.Text.Json, …) | MIT |
| Avalonia UI (core, Fluent theme, TrayIcon) | MIT |
| CommunityToolkit.Mvvm | MIT |
| Inter font | SIL OFL 1.1 |
| SkiaSharp, HarfBuzzSharp | MIT |
| Skia | BSD 3-Clause |
| HarfBuzz | Old MIT |
| ANGLE | BSD 3-Clause |

Development only: xUnit (Apache 2.0), Avalonia.Headless (MIT).

A test checks that every component listed in About has its section in the notices.

Paid Avalonia add-ons (Accelerate, XPF) are not used.

## 8. Implementation phases

1. ✅ **Core – scan and diff (Mirror):** read-only, covered by tests. Done 2026-11-13. The developer tool `tools/Amberkeep.DevDiff` is available for trying it out.
2. ✅ **Core – execution (Mirror):** copying, overwriting, archive and deletion, safety guards, verification. Done 2026-11-14. It can be tried via `DevDiff mirror`.
3. ✅ **Storage + markers:** `amberkeep.json`, finding targets by marker, history, Clean up. Done 2026-11-14 (project `Amberkeep.Storage`: `AppStore`, `TargetRegistry`, `TargetLocator`, `JobRunner`, `HistoryStore`, `CleanupService`).
4. ✅ **UI – basics:** dashboard, job editor, diff view with ticking, progress, one-time run. Done 2026-11-14 (project `Amberkeep.App`, Avalonia 12, CommunityToolkit.Mvvm).
5. ✅ **UI – extras:** history, archive browser and restore, archive cleanup, report export, export and import of backups, settings and About. Done 2026-11-14.
6. ✅ **Background mode:** tray icon with progress, single instance, target connection detection, scheduler, notifications, optional start with the system. Done 2026-11-14.
7. ✅ **Packaging → version 1.0 (Windows):** icon, portable zip for win-x64 (self-contained exe and 3 native DLLs), exe metadata, GPL v3 license and notices in About. Done 2026-11-14 (chapter 7.1).
   - ✅ **Fixes 1.0.1:** changes after the first test (window size, autoscroll, fields, labels, guard OR, weekly and monthly schedule) and fixes from a review of the whole code (path aliases, program folder, Clean up, Unicode forms, name collisions, invalid values, Windows sign-out, Quit, run lock, error log, current job, root target). Done 2026-11-14.
   - ✅ **Fixes 1.0.2:** second review of the whole code: check for a path returning to the source by the backup's rules, shared folders of this computer, links above an item, *Clean up* never locks the program, import and moved target checked against other backups, fixing schedule values, schedule start, the window does not freeze on an unavailable network, the backup screen does not get stuck, *Quit* always exits. Done 2026-11-14.
8. ✅ **Version 1.1.0:** *Show in source folder* / *Show in target folder* in the diff (chapter 6.1). Repository: English SPEC, README with screenshots and `CHANGELOG.md`. Fixes from a security audit: import shows a summary of each backup before importing, archive browser and restore never go through links, names Windows changes are skipped, a refused write fails only its item, exclusion patterns match in linear time, damaged history records are read safely. Done 2026-11-16.

**After version 1.0 (later phases):**

- **Linux (linux-x64) and macOS (osx-arm64):** publish profiles and verification on a real system. For macOS also an `.app` bundle with `.icns`, and signing.
- **Two-way sync:** state snapshots (`state/`), change detection on both sides, conflicts. UI for the mode and the direction in the diff.
- CLI mode, move detection, hash cache.

## 9. Open points

- [x] Archive off by default, retention 30 days.
- [x] Removal guard: > 50 % (at least 20 files) OR ≥ 1,000 files, configurable, can be turned off (changed during testing 1.0, previously AND).
- [x] One source per job.
- [x] Default trigger: none (manual).
- [x] Closing the window: minimize to tray.
- [x] Linux: without a tray icon, notifications are enough.
- [ ] Two-way: default conflict resolution (proposed *Keep both*). To be decided before Two-way is implemented.
