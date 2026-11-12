param([switch]$Interactive)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $root '.tools\dotnet\dotnet.exe')) {
    . "$PSScriptRoot\env.ps1"
}

function Invoke-Checked([scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "Command failed with code ${LASTEXITCODE}: $Command" }
}

Push-Location $root
try {
    $editors = @('PaperDesk.Desktop\WebEditor')
    if (Test-Path -LiteralPath 'paperdesk-web\package.json') { $editors += 'paperdesk-web' }
    foreach ($directory in $editors) {
        Push-Location $directory
        try {
            Invoke-Checked { npm ci }
            Invoke-Checked { npm audit --audit-level=low }
            Invoke-Checked { npm test }
            Invoke-Checked { npm run build }
        } finally { Pop-Location }
    }

    $build = Join-Path $root 'artifacts\windows-check-build'
    $fixtures = Join-Path $root ('artifacts\windows-check-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Checked {
        dotnet restore PaperDesk.sln --artifacts-path $build --force-evaluate -p:NuGetAudit=true -p:NuGetAuditMode=all '-warnaserror:NU1900,NU1901,NU1902,NU1903,NU1904,NU1905'
    }
    Invoke-Checked { dotnet build PaperDesk.sln -c Release --no-restore --artifacts-path $build }
    $core = Join-Path $build 'bin\PaperDesk.Tests\release\PaperDesk.Tests.dll'
    $desktop = Join-Path $build 'bin\PaperDesk.Desktop.Tests\release\PaperDesk.Desktop.Tests.dll'
    Invoke-Checked { dotnet $core }
    Invoke-Checked { dotnet $desktop --profile-storage (Join-Path $fixtures 'profiles') }
    Invoke-Checked { dotnet $desktop --error-log (Join-Path $fixtures 'logs') }
    Invoke-Checked { dotnet $desktop --display-geometry }
    Invoke-Checked { dotnet $desktop --updates (Join-Path $fixtures 'updates') }

    if ($Interactive) {
        Invoke-Checked { dotnet $desktop (Join-Path $fixtures 'wpf') }
        Invoke-Checked { dotnet $desktop --appearance (Join-Path $fixtures 'appearance') }
        Invoke-Checked { dotnet $desktop --manager-scale (Join-Path $fixtures 'manager-scale') }
        Invoke-Checked { dotnet $desktop --usability (Join-Path $fixtures 'usability') }
        Invoke-Checked { dotnet $desktop --windows-shell (Join-Path $fixtures 'shell') }
        Invoke-Checked { dotnet $desktop --tray-exit (Join-Path $fixtures 'tray-exit') }
        Invoke-Checked { dotnet $desktop --tray-exit (Join-Path $fixtures 'tray-exit') }
        Invoke-Checked { dotnet $desktop --web-editor (Join-Path $fixtures 'webview') }
        Invoke-Checked { dotnet $desktop --web-editor-fallback (Join-Path $fixtures 'fallback') }
        Invoke-Checked { dotnet $desktop --native-editor (Join-Path $fixtures 'native-editor') }
        Invoke-Checked { dotnet $desktop --editor-lifetime (Join-Path $fixtures 'lifetime') }
    }
} finally { Pop-Location }
