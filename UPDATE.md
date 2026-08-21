# MDM Auto-Update

## Apps

| File | Role |
|------|------|
| `MDM.Updater.exe` | Checks GitHub releases, installs update, launches main app |
| `DownloadMuck.exe` | Main download manager (no self-update) |

## Flow

1. User starts `DownloadMuck.exe` (or `MDM.Updater.exe`).
2. Main app immediately starts **MDM.Updater** and exits (so files are not locked).
3. Updater reads latest release from `oguzbeymain/MDM-App`.
4. If newer: downloads zip, replaces files, starts `DownloadMuck.exe --from-updater`.
5. If same: starts main app directly.

Debug builds skip the updater redirect.

## Publish

```powershell
.\scripts\Publish-Release.ps1 -Version 1.0.1 -Notes "Separate updater"
```

Zip contains both executables.
