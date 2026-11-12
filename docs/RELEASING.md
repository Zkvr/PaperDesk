# Building and releasing PaperDesk for Windows

## Version and packages

Set `Version` in `PaperDesk.Desktop/PaperDesk.Desktop.csproj`. PaperDesk uses `MAJOR.MINOR.PATCH`. Breaking changes increment `MAJOR`, compatible features increment `MINOR`, and fixes increment `PATCH`. The `-preview.N`, `-beta`, and `-rc` suffixes mark prereleases. The Git tag uses the same version with a `v` prefix. Record changes in the [changelog](../CHANGELOG.md).

Use the SDK pinned in `global.json` and Inno Setup 7.1.1 x64 to reproduce a build. CI checks the hashes of the compiler installer and `ISCC.exe`. The ZIP uses fixed entry ordering and timestamps. Inno Setup excludes source file timestamps. Compare SHA-256 for unsigned builds of the same source with the pinned tools. [Inno Setup download verification](https://jrsoftware.org/isdl-verify.php).

```powershell
./scripts/package-release.ps1 -Compiler '<path to ISCC.exe>' -Output artifacts/rc -ExpectedTag v0.7.0-rc
$manifest = ./scripts/write-winget-manifests.ps1 -Release artifacts/rc
winget validate --manifest $manifest
```

The Windows workflow builds the ZIP for branch and pull request checks. Tag pushes and manual runs also build the installer and WinGet manifests. The tag must match the desktop project version. Download the installer, ZIP, and `SHA256SUMS.txt` from one Actions run and verify their hashes. `release.json` records the version, tag, and prerelease flag.

## GitHub Releases and WinGet

Use an existing tag to create a draft release. Verify the uploaded assets and the **Pre-release** label before publishing. Prereleases have `Latest` disabled. A stable release uses a tag without a suffix. [GitHub CLI documentation](https://cli.github.com/manual/gh_release_create).

```powershell
$build = 'artifacts/rc'
$release = Get-Content "$build/release.json" -Raw | ConvertFrom-Json
$flags = @('--verify-tag', '--draft', '--repo', 'Zkvr/PaperDesk', '--title', "PaperDesk $($release.version)", '--notes-file', "$build/release-notes.md")
if ($release.prerelease) { $flags += @('--prerelease', '--latest=false') }
gh release create $release.tag @flags "$build/PaperDesk-Setup-win-x64.exe" "$build/PaperDesk-Portable-win-x64.zip" "$build/SHA256SUMS.txt"
```

The preview manifest uses `Zkvr.PaperDesk.Preview` and includes the versioned installer URL, SHA-256, and Inno Setup product code. The final installer must be available at the recorded URL before catalog submission. `winget validate` checks the manifest locally. Catalog submission also checks downloading and installing the package. [WinGet validation](https://learn.microsoft.com/en-us/windows/package-manager/package/winget-validation).

## Signing and distribution

Versioned GitHub Releases provide the installer and portable ZIP. The RC uses manual updates. Signed distribution uses Authenticode with a consistent publisher and SHA-256 for the final packages. WinGet can fetch the same installer with its pinned hash.

Azure Artifact Signing depends on publisher location and eligibility. A trusted code-signing certificate is another option. Sign the application EXE and configure `SignTool` in Inno Setup for Setup, its temporary copies, and the uninstaller. Verify the certificate chain, SHA-256, and a trusted timestamp, then regenerate checksums and WinGet manifests. The RC installer is unsigned. [Windows signing options](https://learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options), [Inno Setup SignTool](https://jrsoftware.org/ishelp/topic_setup_signtool.htm).

The Microsoft Store can sign MSIX packages after certification. Submitting the current EXE through the Store would require publisher signatures for the installer and PE files, a permanent versioned HTTPS URL, and a silent offline installation. An MSIX transition would need separate testing of profile migration and startup behavior. [Store requirements for MSI and EXE](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements), [MSIX requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/package-version-numbering).

## Licenses and notices

PaperDesk source licensing is pending. The MIT license permits use, modification, and distribution with the copyright notice. Apache-2.0 adds explicit patent terms. [MIT](https://opensource.org/license/mit), [Apache-2.0](https://www.apache.org/licenses/LICENSE-2.0).

Dependency notices are in `licenses/` and `WebEditor/THIRD-PARTY-NOTICES.txt`. Verify them against the ZIP and installer when dependencies change. Inno Setup is separately licensed. [Inno Setup license](https://github.com/jrsoftware/issrc/blob/is-7_1_1/license.txt).
