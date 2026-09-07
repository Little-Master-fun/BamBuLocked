[CmdletBinding()]
param([string]$Output = (Join-Path $PSScriptRoot '..\artifacts\win-x64'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\PrintGate.Windows\PrintGate.Windows.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Windows x64 package: $Output"
