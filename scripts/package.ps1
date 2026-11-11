# Assembles a binary release of the ModKit (what a modder downloads; the repo itself is the source):
#   <Out>\bin\D3ModHost.dll + d3inject.exe, bin\D3ModTool\, docs\ (index.html + src), sdk\, examples\ (sources), README.md, LICENSE
# Run scripts\build.ps1 first.
#   powershell -File scripts\package.ps1 [-Out dist\D3ModKit] [-Zip]
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot) 'dist\D3ModKit'), [switch]$Zip)
$ErrorActionPreference = 'Stop'
$kit = Split-Path $PSScriptRoot
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($item in 'README.md', 'LICENSE', 'docs', 'sdk', 'examples') {
    $src = Join-Path $kit $item
    if (Test-Path $src -PathType Leaf) { Copy-Item $src $Out; continue }
    # sources only: no build output, no built example binaries
    Get-ChildItem $src -Recurse -File | Where-Object { $_.FullName -notmatch '\\(target|bin|obj|out)\\|\\client-code\\|\\server\\[^\\]+\.(dll|pdb|json)$' } | ForEach-Object {
        $dst = Join-Path $Out $_.FullName.Substring($kit.Length + 1)
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item $_.FullName $dst
    }
}
$bin = Join-Path $Out 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $kit 'host\target\release\D3ModHost.dll'), (Join-Path $kit 'host\target\release\d3inject.exe') $bin
Copy-Item (Join-Path $kit 'tools\D3ModTool\out') (Join-Path $bin 'D3ModTool') -Recurse
"ModKit release -> $Out ($((Get-ChildItem $Out -Recurse -File).Count) files)"
if ($Zip) {
    $zipPath = "$Out.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $Out '*') -DestinationPath $zipPath
    "zip -> $zipPath"
}
