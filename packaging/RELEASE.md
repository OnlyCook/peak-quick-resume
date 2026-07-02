# Release checklist

Steps to cut a new PEAK Quick Resume release. Do the version bump in **every** place
below — they are not auto-synced.

## 1. Bump the version number (must match everywhere)

| # | File | What to change | Why it matters |
|---|------|----------------|----------------|
| 1 | `src/PeakQuickResume/PluginInfo.cs` | `public const string Version` | **In-game F1 help screen** version line + the BepInEx plugin version |
| 2 | `src/PeakQuickResume/PeakQuickResume.csproj` | `<Version>` | Compiled assembly metadata |
| 3 | `packaging/manifest.json` | `version_number` | Thunderstore package version + the release zip filename (`build-release.sh` reads it) |
| 4 | `packaging/README.md` | the `> **Beta (vX.Y.Z).**` line | Shown on Thunderstore and (generated) on GitHub |

> The repo-root `README.md` is **generated** from `packaging/README.md` — do **not**
> edit it or bump a version in it by hand.

## 2. Update the changelog

- Add a new top entry to `packaging/CHANGELOG.md` describing the changes.

## 3. Regenerate the root README (if either README source changed)

```bash
bash packaging/gen-readme.sh
```

(Also runs automatically as step 0 of `build-release.sh`.)

## 4. Build the release zip

```bash
bash packaging/build-release.sh      # -> dist/PEAKQuickResume-<version>.zip
```

## 5. Commit, tag, upload

- Commit the version bump + changelog + regenerated `README.md`.
- Upload `dist/PEAKQuickResume-<version>.zip` to Thunderstore.
