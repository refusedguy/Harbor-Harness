# Release Engineering & Distribution Analysis: Modern .NET/CLI Tools
## Focus: Harbor-Harness v0.5 → v1.0

**Date:** 2026-08-27  
**Scope:** Packaging, versioning, update mechanisms, platform distribution, CI/CD, and concrete recommendations for Harbor.  
**Sources:** Harbor-Harness repo (`/mnt/projects/Harbor-Harness`, branch `dev`), .NET 10 SDK docs, Andrew Lock's .NET 10 packaging deep-dive, Homebrew/Chocolatey/WinGet/Scoop packaging guides, Squirrel/Clowd.Squirrel/Velopack docs.

---

## 1. Packaging Strategies

### 1.1 The .NET Deployment Spectrum

| Mode | Size (CLI) | Runtime Req | Startup | Use Case |
|---|---|---|---|---|
| **Framework-dependent (FDD)** | ~5 MB | .NET 10 required | Fast | Developers, CI |
| **Self-contained (SCD)** | ~80 MB | None | Slower | End users |
| **Single-file SCD** | ~80 MB | None | Slower (extract) | Distribution simplicity |
| **Trimmed SCD** | ~30–50 MB | None | Fast | Size-sensitive |
| **Native AOT** | ~5–10 MB | None | <50 ms | Performance-critical |
| **dotnet tool (NuGet)** | ~5–80 MB | Varies by RID | Varies | `dotnet tool install` |
| **RID-specific tool (NuGet)** | Varies | None for SC | Varies | .NET 10+ multi-platform tool |

### 1.2 dotnet tool vs. Self-Contained vs. Global Tool

**dotnet tool (NuGet-based):**
- Pros: Installs via `dotnet tool install -g`, auto-resolves RID, integrates with NuGet ecosystem, version-pinned via `dotnet-tools.json`.
- Cons: Requires .NET 10 SDK for RID-specific packages (as of .NET 10), slower install (NuGet download + extraction), global tools live in `~/.dotnet/tools` which can be polluted, self-update is awkward (process must exit).
- Harbor already has `PackAsTool=true` in `Harbor.App.Cli.csproj` and is a functional NuGet-packaged tool.

**Self-contained publish (folder/archive):**
- Pros: No SDK/runtime dependency, trivial to host on GitHub Releases, works on any OS with a binary, simplest for end-users.
- Cons: Large (~80 MB per RID), no built-in update mechanism, user must manually replace binary.
- Harbor's NUKE build already produces these via `ReleaseTarget`.

**RID-specific .NET 10 tools:**
- `.NET 10` introduces `ToolPackageRuntimeIdentifiers` and `PublishAot` support for tools.
- A single `dotnet pack` can emit `mytool.win-x64.nupkg`, `mytool.linux-x64.nupkg`, `mytool.osx-arm64.nupkg`, plus an `any` framework-dependent fallback.
- The .NET CLI automatically selects the correct RID package.
- **Caveat:** Consumers must have .NET 10 SDK to install these new tool package formats. This is a chicken-and-egg problem for early adoption.

### 1.3 Recommended Packaging Strategy for Harbor

**v0.5–v0.9 (pre-1.0):**
- **Primary:** Self-contained single-file archives per RID (`linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`, `win-arm64`).
  - Rationale: No runtime dependency, simplest UX, GitHub Releases artifact, works today.
- **Secondary:** Framework-dependent zip for developers who already have .NET 10.
- **Tertiary:** `dotnet tool install --global` via NuGet (package to nuget.org or GitHub Packages).
  - Rationale: Low friction for .NET developers, version-pinned.

**v1.0+ (post-stabilization):**
- Adopt **RID-specific tool packaging** (`.NET 10+`) once the ecosystem has broadly adopted .NET 10 SDK.
- Offer **Native AOT** builds for `win-x64` and `linux-x64` as a "fast" variant.
- Keep self-contained archives as the universal fallback.

### 1.4 Desktop App (Avalonia) Packaging

`Harbor.App.Avalonia` already declares `RuntimeIdentifiers` for all major platforms. For v1.0:
- **macOS:** Produce a `.app` bundle via `dotnet publish` + `dotnet bundle` (if using MAUI) or a notarized zip/disk image. For Avalonia, the standard is a self-contained app bundle.
- **Windows:** Produce an `.msix` or `.exe` installer. MSIX enables auto-update via `.appinstaller` but is complex. A self-contained zip is the MVP.
- **Linux:** Produce an `AppDir` (AppImage) or a `.deb`/`.rpm`. Flatpak is overkill for v1.0.

---

## 2. Versioning Approaches

### 2.1 Current State

Harbor currently uses:
- `Version=0.4.0-alpha` in `Directory.Build.props`
- `AssemblyVersion=0.4.0.0`
- `FileVersion=0.4.0.0`
- CHANGELOG follows Keep a Changelog + SemVer
- No automated version bumping (no `release-please`, `semantic-release`, or `Nerdbank.GitVersioning`)

### 2.2 SemVer for CLI Tools

For CLI tools, SemVer applies to the **public interface**: flags, exit codes, JSON output, config file schema, and plugin API.

| Segment | When to bump | Harbor example |
|---|---|---|
| **MAJOR** (X.0.0) | Remove flag, change exit code, break plugin API | `1.0.0`: remove `--old-flag`, change `HarborMode` enum |
| **MINOR** (0.X.0) | New flag, new tool, new provider, backward-compatible config | `0.5.0`: add `--json` flag, new `time` tool |
| **PATCH** (0.0.X) | Bug fix, internal refactor, doc fix | `0.5.1`: fix BashTool quoting |
| **Pre-release** | Alpha/beta/rc | `0.5.0-alpha.1`, `0.5.0-rc.1` |

**Harbor-specific note:** Since Harbor is at `0.4.0-alpha`, anything can break until `1.0.0`. The recommendation is:
- `0.5.0`: first "feature complete" milestone with stable plugin API.
- `0.9.0`: feature freeze, only bug fixes.
- `1.0.0`: public API stable, deprecation policy in effect.

### 2.3 Recommended Versioning Tooling

- **`Nerdbank.GitVersioning`** or **`GitVersion`**: derive version from git tags automatically.
- **`release-please`** (Google): Conventional Commits → auto-generated changelog + version bump + GitHub Release.
- **Simpler alternative:** Keep version in `Directory.Build.props`, bump manually via NUKE target, tag with `v0.5.0`.

**Recommendation for v0.5–v1.0:**
- Use **Conventional Commits** (`feat:`, `fix:`, `BREAKING CHANGE:`) from day one.
- Add a NUKE target `./build.sh Version` that reads the last tag and bumps based on commit messages (or use `release-please`).
- Automate changelog generation from commit history.

---

## 3. Update Mechanisms

### 3.1 The CLI Update Problem

CLI tools have a fundamental update problem: **the binary is locked while running.** You cannot overwrite `harbor.exe` while `harbor.exe` is executing.

| Approach | How it works | Pros | Cons |
|---|---|---|---|
| **Manual download** | User downloads new zip/archive | Simple, universal | Friction, no notification |
| **`dotnet tool update`** | NuGet-based, replaces tool on next run | Built into SDK | Requires SDK, slow, process must exit |
| **Self-update shim** | Spawn helper to replace binary after exit | Works for any binary | Complex, platform-specific edge cases |
| **Package manager** | `brew upgrade`, `choco upgrade`, `winget upgrade` | Native to OS | Requires separate package per manager |
| **Auto-updater library** | Velopack, Squirrel, NetSparkle | Seamless UX | Adds dependency, complexity |

### 3.2 Auto-Updater Landscape

**Velopack** (`velopack.io`):
- Modern .NET auto-updater + installer.
- Creates `.exe`/`.app`/AppImage installers with delta updates.
- Integrates with GitHub Releases.
- Cross-platform (Windows, macOS, Linux).
- **Status:** Appears less actively maintained recently; community fork activity is unclear. Risk: abandonment.

**Squirrel.Windows / Clowd.Squirrel:**
- Windows-only. Uses NuGet packages as update payloads.
- Delta (diff) updates reduce download size.
- `Update.exe` runs alongside the app.
- **Status:** Intermittently maintained. macOS support (`Squirrel.Mac`) is effectively dead.
- **Verdict:** Only viable if Harbor is Windows-first, which it is not.

**ClickOnce + `.appinstaller`:**
- Windows-only, built into .NET.
- `.appinstaller` XML file enables auto-update checks.
- **Limitation:** `.appinstaller` URI protocol is disabled by default since Dec 2023; users must download and open manually.

**NetSparkle:**
- Cross-platform (WinForms, WPF, Avalonia).
- Ed25519 signed updates.
- **Verdict:** Desktop-app only, not relevant to CLI.

**Custom self-update (Harbor-specific):**
- For CLI: spawn a background process that waits for the main process to exit, then downloads and replaces the binary.
- For Avalonia: use `WebClient`/`HttpClient` to check GitHub Releases API, download new zip, extract to app directory, prompt user to restart.
- This is the most flexible approach but requires implementation effort.

### 3.3 Recommended Update Strategy

**CLI (`Harbor.App.Cli`):**
- **v0.5–v0.9:** No auto-update. Document `harbor version` + manual download from GitHub Releases.
- **v1.0:** Add `harbor upgrade` command that:
  1. Calls GitHub Releases API for latest tag.
  2. Downloads the appropriate archive for current OS/RID.
  3. Extracts to a temp dir.
  4. Spawns a shell script (`harbor-upgrade.sh` / `harbor-upgrade.ps1`) that waits for the main process to exit and replaces the binary.
  5. This is a common pattern (used by `gh`, `docker`, `kubectl` plugins, etc.).

**Avalonia Desktop (`Harbor.App.Avalonia`):**
- **v0.5–v0.9:** No auto-update.
- **v1.0:** Implement a lightweight update checker using GitHub Releases API. If a new version is found, prompt the user to download and install. This avoids the complexity of Velopack/Squirrel.

---

## 4. Platform-Specific Distribution

### 4.1 Windows

| Channel | Mechanism | Auto-Update | Audience |
|---|---|---|---|
| **GitHub Releases** | `.zip` / `.exe` self-contained | Manual | Power users, devs |
| **WinGet** | YAML manifest → `winget install` | Yes (via WinGet) | General Windows users |
| **Chocolatey** | `.nuspec` → `choco install` | Yes (via Chocolatey) | Enterprise, admins |
| **MSIX + `.appinstaller`** | Windows Store / sideload | Yes (but URI disabled) | Enterprise |
| **Scoop** | JSON manifest → `scoop install` | Yes (bucket auto-update) | Devs, power users |

**Recommendation:**
- Ship **self-contained `.zip`** on GitHub Releases (MVP).
- Maintain a **Chocolatey package** for Windows (enterprise-friendly, scriptable).
- Submit a **WinGet manifest** to `microsoft/winget-pkgs` (PR process).
- Maintain a **Scoop manifest** in a dedicated bucket (e.g., `harbor-sh/scoop-bucket`).

### 4.2 macOS

| Channel | Mechanism | Auto-Update | Audience |
|---|---|---|---|
| **GitHub Releases** | `.tar.gz` self-contained | Manual | Devs |
| **Homebrew** | Ruby formula in tap | Yes (`brew upgrade`) | macOS users |
| **Homebrew Cask** | Ruby cask (if GUI) | Yes | GUI app users |

**Recommendation:**
- Ship **self-contained `.tar.gz`** on GitHub Releases.
- Maintain a **Homebrew tap** (`harbor-sh/homebrew-tap`) with a formula for the CLI.
- For Avalonia, also maintain a **Homebrew Cask** if distributing as a `.app` bundle.

**Automation tip:** Use a GitHub Action to auto-update the Homebrew formula from GitHub Releases assets. Tools like `homebrew/autoupdate` or custom Python scripts can push formula changes to the tap repo.

### 4.3 Linux

| Channel | Mechanism | Auto-Update | Audience |
|---|---|---|---|
| **GitHub Releases** | `.tar.gz` / `.AppImage` | Manual | Devs |
| **Homebrew (Linux)** | Formula in tap | Yes | Linuxbrew users |
| **apt (deb)** | `.deb` package | Yes | Debian/Ubuntu users |
| **Snap** | Snapcraft | Yes | Ubuntu-centric |
| **Flatpak** | Flathub | Yes | Desktop app |

**Recommendation:**
- Ship **self-contained `.tar.gz`** on GitHub Releases.
- Maintain a **Homebrew (Linux) formula** in the same tap.
- Produce a **`.deb`** for Debian/Ubuntu (can be automated via `dpkg-deb` in CI).
- **Skip Snap/Flatpak** for v0.5–v1.0 unless Avalonia adoption demands it.

### 4.4 Cross-Platform Summary Matrix

| Platform | GitHub Release | Package Manager 1 | Package Manager 2 | Installer | Auto-Update |
|---|---|---|---|---|---|
| **Windows x64/arm64** | `.zip` | Chocolatey | WinGet | `.exe` (future) | Yes (via pkg mgr) |
| **macOS x64/arm64** | `.tar.gz` | Homebrew | — | `.dmg` (future) | Yes (brew) |
| **Linux x64/arm64** | `.tar.gz` | Homebrew | apt (deb) | `.rpm` (future) | Yes (pkg mgr) |

---

## 5. CI/CD Patterns

### 5.1 Current Harbor Build System

Harbor uses **NUKE** (`build/_build/`) with:
- `PublishTarget`: runs `dotnet publish` with variant-specific settings.
- `ArchiveTarget`: compresses publish output (TarGz, Zip).
- `ReleaseTarget`: orchestrates Publish → Archive → Upload.
- `GitHubReleaseUploader`: creates/updates GitHub Release, uploads assets via REST API.
- `FeatureFlags`: granular control (`HarborWithPlugins`, `HarborWithAllProviders`, `HARBOR_MINIMAL`).
- No `.github/workflows` directory exists yet — CI is presumably defined externally or not yet committed.

### 5.2 Recommended GitHub Actions Workflow

```yaml
name: Release
on:
  push:
    tags: ['v*']

jobs:
  build:
    strategy:
      matrix:
        include:
          - os: ubuntu-latest
            rid: linux-x64
          - os: ubuntu-latest
            rid: linux-arm64
          - os: macos-latest
            rid: osx-arm64
          - os: windows-latest
            rid: win-x64
          - os: windows-latest
            rid: win-arm64
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: ./build.sh Release --app Harbor.App.Cli --rid ${{ matrix.rid }}
        env:
          GH_TOKEN: ${{ secrets.GH_TOKEN }}
```

**Key patterns:**
- **Matrix build** per OS/RID.
- **Self-contained single-file** for distribution.
- **Archive** each output (`.tar.gz` on Unix, `.zip` on Windows).
- **Upload to GitHub Releases** using the existing `GitHubReleaseUploader` or `softprops/action-gh-release`.
- **Signing:** On Windows, sign the `.exe` with a code-signing certificate. On macOS, notarize the `.app` bundle. Skip for v0.5, add before v1.0.

### 5.3 Release Checklist per Version

1. **Tag:** `git tag v0.5.0 && git push origin v0.5.0`
2. **Build matrix:** All 5 RIDs succeed.
3. **Smoke test:** Run `harbor version` and `harbor ask "ping"` on each platform.
4. **GitHub Release:** Auto-created with assets.
5. **Package managers:** Update Chocolatey, WinGet, Scoop, Homebrew manifests (can be automated).
6. **Changelog:** Generated from Conventional Commits.

---

## 6. What Harbor Should Adopt for v0.5–v1.0

### 6.1 v0.5 (Immediate Priorities)

| Area | Action | Rationale |
|---|---|---|
| **Packaging** | Ship self-contained single-file archives per RID on GitHub Releases. | Zero-runtime dependency, simplest UX. |
| **Versioning** | Move version to `0.5.0` (drop `-alpha`). Start Conventional Commits. | Signal maturity; enable automated tooling. |
| **CI/CD** | Add `.github/workflows/release.yml` using the existing NUKE `ReleaseTarget`. | Automate the matrix build + GitHub Release upload. |
| **Package Managers** | Create `harbor-sh/homebrew-tap` (formula) + `harbor-sh/scoop-bucket` (manifest). | Reach macOS/Linuxbrew and Windows dev users. |
| **Docs** | Update `README.md` and `docs/BUILD.md` with install commands for all channels. | Users need a single source of truth. |
| **Update Mechanism** | None. Document manual update path. | Premature optimization; focus on stability. |

### 6.2 v0.6–v0.9 (Stabilization)

| Area | Action | Rationale |
|---|---|---|
| **Packaging** | Add framework-dependent zip for developers. Add Chocolatey package. | Reduce friction for .NET devs and Windows enterprise. |
| **Versioning** | Add `release-please` or custom NUKE target for automated changelog + tagging. | Reduce release overhead. |
| **Native AOT** | Produce `linux-x64` and `win-x64` AOT builds as a separate "fast" download. | Benchmark and promote the performance story. |
| **RID-specific tools** | Experiment with `ToolPackageRuntimeIdentifiers` + publish to GitHub Packages. | .NET 10 ecosystem is stabilizing; get early feedback. |
| **Code Signing** | Sign Windows binaries; notarize macOS `.app`. | Required for trust and some distribution channels. |
| **WinGet** | Submit manifest PR to `microsoft/winget-pkgs`. | Reach Windows power users. |
| **Update Mechanism (CLI)** | Implement `harbor upgrade` self-update command. | Close the loop on distribution. |
| **Update Mechanism (Avalonia)** | Add in-app update checker using GitHub Releases API. | Desktop UX parity. |

### 6.3 v1.0 (Stable Release)

| Area | Action | Rationale |
|---|---|---|
| **Packaging** | Ship RID-specific `.NET 10` tool packages as the **primary** distribution. Keep archives as fallback. | Leverage .NET ecosystem tooling. |
| **Versioning** | Enforce SemVer strictly. Deprecation warnings before removal. | API stability promise. |
| **Package Managers** | Maintain all four: Homebrew, Chocolatey, WinGet, Scoop. Automate manifest updates via CI. | Maximum reach. |
| **Desktop Installers** | Windows: MSIX or signed EXE. macOS: notarized DMG. Linux: AppImage or deb/rpm. | Professional distribution. |
| **Auto-Update** | CLI: `harbor upgrade`. Avalonia: built-in updater. | Seamless user experience. |
| **Telemetry** | Opt-in usage reporting to prioritize platforms and RIDs. | Data-driven packaging decisions. |

### 6.4 What to Avoid

| Anti-Pattern | Why |
|---|---|
| **Velopack / Squirrel as primary** | Both have maintenance uncertainty; Harbor is cross-platform and CLI-first. |
| **NuGet.org as sole distribution** | NuGet is for libraries; CLI users expect binaries, not `dotnet tool` invocation. |
| **Single archive for all platforms** | Forces users to download irrelevant binaries; violates "download what you need." |
| **Auto-update before v1.0** | The update surface is unstable; breaking changes make auto-update dangerous. |
| **MSIX-only Windows distribution** | Too restrictive for a CLI tool; blocks scriptable/CI usage. |

---

## 7. Concrete Next Steps (Action Items)

1. **Add `.github/workflows/release.yml`** using NUKE's `ReleaseTarget` for a 5-RID matrix build.
2. **Create `harbor-sh/homebrew-tap`** repo with a formula for `harbor` CLI.
3. **Create `harbor-sh/scoop-bucket`** repo with a manifest for `harbor` CLI.
4. **Bump version to `0.5.0`** in `Directory.Build.props`.
5. **Add Conventional Commits enforcement** (e.g., `commitlint` in CI or NUKE check).
6. **Document install commands** for all channels in `README.md` and a new `docs/DISTRIBUTION.md`.
7. **Prototype `harbor upgrade`** as a spike in `Harbor.Tools.Builtin` or a separate `Harbor.Tools.Update` project.

---

*Report generated from direct repo inspection at `/mnt/projects/Harbor-Harness` (branch `dev`) and contemporary .NET 10 packaging/CI/CD documentation.*
