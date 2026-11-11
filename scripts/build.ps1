# Builds everything in the ModKit: the client mod host (host\, Rust), D3ModTool (tools\D3ModTool, .NET 8) and the docs.
#   powershell -File scripts\build.ps1 [-NoHost] [-NoTool] [-NoDocs]
# Needs: Rust (x86_64-pc-windows-msvc), the .NET 8 SDK. Outputs: host\target\release\{D3ModHost.dll,d3inject.exe},
# tools\D3ModTool\out\D3ModTool.exe, docs\index.html.
param([switch]$NoHost, [switch]$NoTool, [switch]$NoDocs)
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot
if (-not $NoHost) {
    Push-Location (Join-Path $kit 'host')
    # through cmd: cargo writes progress to stderr, which PowerShell would turn into errors
    cmd /c "cargo build --release 2>&1" | Select-String '^(error|warning)' -Context 0,6
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { throw 'host build failed' }
    'host: host\target\release\D3ModHost.dll + d3inject.exe'
}
$tool = Join-Path $kit 'tools\D3ModTool\out\D3ModTool.exe'
if (-not $NoTool) {
    cmd /c "dotnet build `"$(Join-Path $kit 'tools\D3ModTool')`" -c Release -o `"$(Join-Path $kit 'tools\D3ModTool\out')`" -nologo -v q 2>&1" | Select-String ' error ' -Context 0,2
    if ($LASTEXITCODE -ne 0) { throw 'D3ModTool build failed' }
    'D3ModTool: tools\D3ModTool\out\D3ModTool.exe'
}
if (-not $NoDocs) {
    & $tool docs-build (Join-Path $kit 'docs\src') (Join-Path $kit 'docs\index.html') | Select-Object -Last 1
}
