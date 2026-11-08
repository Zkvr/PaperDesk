$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $ProjectRoot '.tools\dotnet'
$env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $ProjectRoot '.tools\nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $ProjectRoot '.tools\nuget-http'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:npm_config_cache = Join-Path $ProjectRoot '.tools\npm-cache'
$env:TEMP = Join-Path $ProjectRoot '.tools\tmp'
$env:TMP = $env:TEMP
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
New-Item -ItemType Directory -Force -Path $env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_PACKAGES | Out-Null
