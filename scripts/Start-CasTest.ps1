$ErrorActionPreference = 'Stop'
$node = Get-Command node -ErrorAction SilentlyContinue
if (-not $node) { throw 'Node.js was not found. Install Node.js or add it to PATH.' }
$server = Join-Path $PSScriptRoot 'cas-test-server.cjs'
Start-Process -FilePath $node.Source -ArgumentList ('"' + $server + '"') -WindowStyle Hidden
Write-Host 'Open http://127.0.0.1:18765/ in your browser. Keep credentials out of screenshots.'
