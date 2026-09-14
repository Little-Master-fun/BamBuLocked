[CmdletBinding()]
param([string]$KioskUser = 'Printer')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Set-WorkstationDirectoryAcl.ps1')
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
$sid = (Get-LocalUser -Name $KioskUser).SID.Value
if (Test-Path "Registry::HKEY_USERS\$sid") { throw 'Sign the kiosk account out completely before repair.' }
$state = Get-Content 'C:\ProgramData\PrintGate\DeploymentBackup\deployment.json' -Raw | ConvertFrom-Json
if ($state.Sid -ne $sid) { throw 'Deployment account does not match.' }
$backup = Join-Path 'C:\ProgramData\PrintGate' ('PermissionRepair-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
$log = Join-Path $PSScriptRoot '..\artifacts\permission-repair.txt'
Start-Transcript -Path $log -Force | Out-Null
try {
    $index = 0
    function Repair-Directory([string]$Path, [string]$Access) {
        $resolved = (Resolve-Path -LiteralPath $Path).ProviderPath
        if ($resolved.TrimEnd('\') -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\')) { throw 'Refusing drive-root repair.' }
        $items = @(Get-Item -LiteralPath $resolved) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
        if ($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing to traverse reparse points.' }
        $script:index++
        $aclFile = Join-Path $backup ("acl-$script:index.txt")
        & icacls.exe $resolved '/save' $aclFile '/T' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'ACL backup failed; repair stopped.' }
        [pscustomobject]@{Path=$resolved; Parent=(Split-Path $resolved -Parent); AclFile=$aclFile} |
            ConvertTo-Json -Compress | Add-Content (Join-Path $backup 'restore-map.jsonl') -Encoding UTF8
        Set-WorkstationDirectoryAcl -Path $resolved -Grants @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "*${sid}:(OI)(CI)$Access")
        Write-Output "Repaired: $resolved"
    }
    Repair-Directory $state.InstallPath 'RX'
    $config = Get-Content (Join-Path $state.InstallPath 'appsettings.json') -Raw | ConvertFrom-Json
    Repair-Directory 'C:\ProgramData\PrintGate\Data' 'M'
    if (-not (Test-Path (Join-Path $config.RecordingsDirectory '.printgate-recordings'))) { throw 'Recordings marker missing; inspect directory before repair.' }
    Repair-Directory $config.RecordingsDirectory 'M'
    Write-Output "Studio exists: $(Test-Path -LiteralPath $config.StudioPath)"
    Write-Output "ACL backups: $backup"
    Write-Output 'Permission repair completed.'
} catch {
    Write-Output "Repair stopped: $($_.Exception.Message)"
    throw
} finally { Stop-Transcript | Out-Null }
