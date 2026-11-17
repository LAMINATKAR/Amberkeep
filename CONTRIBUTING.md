# Contributing

Thanks for your interest in Amberkeep. Bug reports and ideas are welcome as [issues](../../issues). For security problems, see [SECURITY.md](SECURITY.md) instead.

## Reporting a bug

Please include the Amberkeep version (*Settings → About*), the Windows version, the steps to reproduce and what you expected. A run report from *History → Export report…* helps a lot; remove anything private from it first.

## Pull requests

For anything larger than a small fix, please open an issue first, so we can agree on the approach before you spend time on it.

Build and test:

```bash
dotnet build
dotnet test
```

The build must pass with no warnings (warnings are errors) and all tests must be green. [`SPEC.md`](SPEC.md) describes the intended behavior; if a change alters it, update `SPEC.md` in the same pull request, and add a line to [`CHANGELOG.md`](CHANGELOG.md) under *Unreleased*.

Rules that keep backups safe:

1. **Never write to a source folder.** No temporary files, markers, caches or archives on the source.
2. On a target, the program's own files live only in `<target>/.amberkeep/`.
3. Settings, history and state live next to the executable. No AppData, no registry (except the optional autostart entry).
4. Copy to a temporary name, then rename. Program files are written atomically.
5. Every safety guard (SPEC chapter 4.5) is covered by tests. Tests work only in temporary folders, never on real data.
6. Nothing is specific to one computer: no hard-coded drives or paths.

Other conventions:

- Code, comments, UI texts and commit messages are in English.
- View models do not depend on Avalonia, so they can be tested without the UI. Views use compiled bindings (`x:DataType`).
- New dependencies must have a permissive license (MIT, Apache 2.0, BSD) and be listed in `THIRD-PARTY-NOTICES.txt` if they ship with the program.
- After UI changes, render the screens with `dotnet run --project tools/Amberkeep.Screenshots` and look at them in light and dark.

By contributing you agree that your contribution is licensed under the [GNU GPL v3](LICENSE), like the rest of Amberkeep.
