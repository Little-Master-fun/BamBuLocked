param([string]$AppPath = 'C:\Program Files\PrintGate\PrintGate.exe')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) { throw "PrintGate.exe not found: $AppPath" }
Start-Process -FilePath (Resolve-Path -LiteralPath $AppPath).Path -ArgumentList '--admin' -Verb RunAs
