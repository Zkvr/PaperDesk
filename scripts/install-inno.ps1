param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $destinationPath) { throw 'Choose a new directory for the pinned Inno Setup toolchain.' }
$installer = Join-Path ([IO.Path]::GetTempPath()) ('innosetup-' + [Guid]::NewGuid().ToString('N') + '.exe')
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe'
$expectedHash = '8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0'
try {
    Invoke-WebRequest -Uri $url -OutFile $installer
    if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Inno Setup download hash mismatch.' }
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', '/TASKS=""', ('/DIR="' + $destinationPath + '"'))
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { throw 'Inno Setup toolchain installation timed out.' }
    if ($process.ExitCode) { throw "Inno Setup toolchain installation failed ($($process.ExitCode))." }
    $compiler = Join-Path $destinationPath 'ISCC.exe'
    if ((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash -ne 'e0f9f1dc32032650c6629f6dee41eb6d20ba140e4e3c07c25d8a24f9ba781644') {
        throw 'Unexpected Inno Setup compiler.'
    }
    $compiler
} finally {
    if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }
}
