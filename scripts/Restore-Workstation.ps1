[CmdletBinding()]
param([string]$BackupPath = 'C:\ProgramData\PrintGate\DeploymentBackup')
$ErrorActionPreference = 'Stop'
$state = Get-Content (Join-Path $BackupPath 'deployment.json') -Raw | ConvertFrom-Json
if (Test-Path "Registry::HKEY_USERS\$($state.Sid)") { throw 'Sign the kiosk account out completely first.' }
$mount = 'PrintGate_Restore'
& reg.exe load "HKU\$mount" "$($state.Profile)\NTUSER.DAT" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Run as administrator and verify the profile path.' }
try {
    foreach ($change in $state.Changes) {
        $key = [Microsoft.Win32.Registry]::Users.CreateSubKey("$mount\$($change.Key)")
        try {
            if ($change.Existed) { $key.SetValue($change.Name, $change.Value, [Microsoft.Win32.RegistryValueKind]::$($change.Type)) }
            else { $key.DeleteValue($change.Name, $false) }
        } finally { $key.Dispose() }
    }
} finally {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    & reg.exe unload "HKU\$mount" | Out-Null
}
Write-Host 'Original user policy values restored. AppLocker policy, application files, and logs are retained; restore AppLocker separately if you applied it.'
