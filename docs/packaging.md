# Packaging & Distribution

NubMouse supports four distribution channels from the same zero-dependency C# codebase:

1. **Microsoft Store (MSIX)** — free Microsoft code signing (zero SmartScreen warnings) + automatic Store updates.
2. **GitHub Releases** — standalone `NubMouse.exe` + zip + `SHA256SUMS.txt`.
3. **Winget** — `winget install dstarikov.NubMouse` (`x64` and `arm64`).
4. **Scoop** — portable install with persisted `NubMouse.ini`.

---

## ARM64 (Windows on ARM)

`build.ps1` compiles with `-platform:anycpu` (architecture-independent MSIL). On **Windows 11 ARM64** (22H2+, e.g. Snapdragon ThinkPad T14s Gen 6), Windows ships a native **ARM64 .NET Framework 4.8.1** CLR, so `NubMouse.exe` executes **natively as ARM64** without x86/x64 emulation.

Because the same binary runs natively on both `x64` and `arm64`, both the MSIX packaging script (`-Arch all`) and the Winget manifest ship `x64` and `arm64` entries backed by that single build.

---

## 1. Microsoft Store (MSIX — free signing, no SmartScreen)

Microsoft waived registration fees for individual Microsoft Store developer accounts (`storedeveloper.microsoft.com`), and automatically re-signs approved MSIX packages with Microsoft's trusted Store certificate at no cost.

### How the app adapts when packaged as MSIX

At startup, `Installer.IsPackaged` checks `GetCurrentPackageFullName`. When running inside an MSIX package:
- The standalone install dialog is skipped.
- The **Updates** submenu and automatic GitHub update checks are hidden/disabled (Store policy forbids self-updaters; the Store delivers updates automatically).
- **Run as administrator** (`schtasks` elevation) is hidden (Store certification restriction).
- **Start with Windows** uses the native `windows.startupTask` manifest extension and links to Windows Settings (`ms-settings:startupapps`).
- Toast notifications and the `nubmouse:` protocol use the package's native identity from `AppxManifest.xml` without touching `HKCU\Software\Classes`.

### Building the MSIX

```powershell
.\packaging\msix\build-msix.ps1 -Version 0.1.0 -Arch all `
  -IdentityName "<Your Partner Center Package/Identity/Name>" `
  -Publisher "<Your Partner Center Package/Identity/Publisher>" `
  -PublisherDisplayName "<Your Publisher Display Name>"
```

This generates the required Store tile PNGs from `docs\images\app-icon.png` and packs:
- `dist\NubMouse-0.1.0-x64.msix`
- `dist\NubMouse-0.1.0-arm64.msix`

(You can also set `MSIX_IDENTITY_NAME`, `MSIX_PUBLISHER`, and `MSIX_PUBLISHER_DISPLAY_NAME` as GitHub Actions repository secrets so CI builds Store-ready `.msix` artifacts on every push/tag.)

### Partner Center submission tips (`Notes for Certification`)

Because the manifest declares `runFullTrust` and uses `SetWindowsHookEx(WH_MOUSE_LL, ...)`, include a clear note in Partner Center's **Notes for Certification** box:

> NubMouse is a ThinkPad TrackPoint & pointing-stick utility that lets users use the middle mouse button for both standard middle-click and smooth vertical/horizontal scrolling, plus optional 3D/CAD middle-drag shortcuts. It requires `runFullTrust` and a low-level mouse hook (`WH_MOUSE_LL`) + Raw Input (`RIDEV_INPUTSINK`) to distinguish between a lone middle-click and holding the middle button while pushing the pointing stick, and `SendInput` to emit standard mouse-wheel events. It never installs a keyboard hook, never logs keys or window titles (it only checks `GetAsyncKeyState` for user-configured drag shortcut keys), collects zero telemetry, and makes no network requests in the Microsoft Store build (self-updating is disabled when packaged). To test with any mouse: hold the middle mouse button and move the pointer to scroll; press and release without moving to perform a normal middle-click.

### Enabling optional "Run as administrator" in the Store build later (`allowElevation`)

Windows blocks MSIX packages from elevating unless `<rescap:Capability Name="allowElevation" />` is declared in `AppxManifest.xml` (users cannot force elevation externally without breaking the Store signature). Until `allowElevation` is approved, users who need scrolling inside elevated windows (Task Manager, admin terminals, etc.) should use the GitHub Releases, Winget, or Scoop build, where the built-in **Run as administrator** setting works out of the box.

To enable optional elevation in the Store build later, request approval from Microsoft Partner Center support first:

1. **Keep `asInvoker` in `src/app.manifest`** — never force UAC on normal launch.
2. **Request `allowElevation` approval** in Partner Center, noting:
   > Optional elevation only when the user explicitly chooses "Run as administrator" so TrackPoint scrolling can work inside elevated windows (Task Manager, admin terminals, Registry Editor). Normal launch remains unelevated (`asInvoker`).
3. **Once approved**, add `<rescap:Capability Name="allowElevation" />` under `<Capabilities>` in `packaging/msix/AppxManifest.template.xml`.
4. Users can then right-click **NubMouse → Run as administrator** in the Start menu, or create a Task Scheduler logon task pointing at the version-independent execution alias `%LOCALAPPDATA%\Microsoft\WindowsApps\nubmouse.exe` (which survives Microsoft Store updates automatically).

---

## 2. Winget (`microsoft/winget-pkgs`)

Manifest templates live in [`packaging/winget/`](../packaging/winget).

After publishing a GitHub release (e.g. `v0.1.0`):

```powershell
.\packaging\update-manifests.ps1 -Version 0.1.0 -ExePath dist\NubMouse.exe
winget validate --manifest .\packaging\winget
```

Then either:
- Submit via `wingetcreate submit .\packaging\winget`, or
- Open a PR to `microsoft/winget-pkgs` copying the three `.yaml` files into `manifests/d/dstarikov/NubMouse/0.1.0/`.

---

## 3. Scoop

The Scoop manifest lives at [`packaging/scoop/nubmouse.json`](../packaging/scoop/nubmouse.json).

- Uses `--portable` and persists `NubMouse.ini` in Scoop's `persist` folder so settings survive upgrades and stay out of the Windows registry.
- `autoupdate` automatically tracks GitHub releases and verifies against `SHA256SUMS.txt`.
- Drop `nubmouse.json` into your own Scoop bucket (`scoop bucket add dstarikov https://github.com/dstarikov/scoop-bucket`) or submit it to `ScoopInstaller/Extras`.
