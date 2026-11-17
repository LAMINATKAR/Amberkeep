# Security policy

Amberkeep writes to backup disks and reads your folders, so a bug that could change or delete data in the wrong place is treated as a security issue.

## Supported versions

Only the latest release gets fixes. Please check that the problem still happens with it.

## Reporting a vulnerability

Please **do not open a public issue**. Use **Report a vulnerability** on the repository's *Security* tab instead; only the maintainer sees the report.

Helpful details:

- Amberkeep version (*Settings → About*) and Windows version,
- what you did, what happened and what you expected,
- the file systems involved (NTFS, exFAT, network share, …) and any links, junctions or `subst` drives on the paths.

Please leave out the contents of your files and real folder names you do not want to share; a small example folder that shows the problem is best.

You will get an answer within a few days. Once a fix is released, the report can be made public together with the release notes.

## Scope

[SPEC.md, chapter 4.5](SPEC.md#45-safety-guards) describes what the safety guards protect against. In short: Amberkeep must never write to a source folder, never write outside the target folder and its own `.amberkeep` folder, and never act through links or aliased paths in a way that touches other data. It assumes the computer itself is under the user's control; another account or malware that can write to the program folder or the disks while Amberkeep runs is outside its scope.
