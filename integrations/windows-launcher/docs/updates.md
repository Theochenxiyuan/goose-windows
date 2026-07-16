# Windows package updates

## Identities and channels

The package identities are release compatibility contracts:

| Channel | Standard | CUDA |
| --- | --- | --- |
| Stable | `GooseWindows` | `GooseWindows.CUDA` |
| Canary | `GooseWindows.Canary` | `GooseWindows.CUDA.Canary` |

Do not rename an identity or change its production publisher after publication. Production builds take the publisher from the protected `WINDOWS_PACKAGE_PUBLISHER` Actions variable; it must exactly match the Azure Trusted Signing certificate subject.

Standard and CUDA packages intentionally have different identities so either variant can be installed at the same product version. They expose the same protocols, Explorer command, process mutexes, and activation files, so they are mutually exclusive: uninstall the current variant before installing the other one. User data is retained.

## App Installer feeds

Tagged releases publish signed MSIX files and these stable feed manifests:

- `https://github.com/Theochenxiyuan/goose-windows/releases/download/stable/Goose.appinstaller`
- `https://github.com/Theochenxiyuan/goose-windows/releases/download/stable/Goose.CUDA.appinstaller`

Install through the matching `.appinstaller` file rather than the raw MSIX to enroll the package in OS-managed updates. Windows checks on launch and through the App Installer background task, and only installs package versions higher than the installed version. Electron's updater remains disabled because replacing only Desktop would let Desktop, CLI, Launcher, and the Explorer extension drift apart.

The release workflow uploads the versioned MSIX before publishing the stable feed manifest. Stable versions use `major.minor.patch.0`. Canary builds use an independent identity and put the GitHub run number in the fourth MSIX version field.

Canary package identities and manifests are generated for release testing, but the public canary workflow does not publish an App Installer feed while its Windows artifacts remain unsigned.

## Migrating from ZIP builds

1. Close every Goose Desktop and Launcher process.
2. Install the desired standard or CUDA `.appinstaller` feed.
3. Start the packaged app and verify settings and sessions before deleting the old extracted ZIP directory.
4. Remove shortcuts or startup entries that point at the old ZIP copy.

The MSIX build keeps Goose's existing per-user data locations, including Electron user data and `%LOCALAPPDATA%\Goose`, so migration does not move or delete user data. The package replaces application binaries only. Keep the old ZIP directory until the first packaged launch succeeds; rollback consists of uninstalling the MSIX and starting that retained copy.
