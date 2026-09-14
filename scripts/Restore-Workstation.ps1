[CmdletBinding()]
param([string]$BackupPath = 'C:\ProgramData\PrintGate\DeploymentBackup', [switch]$AllowLoadedProfile)
$ErrorActionPreference = 'Stop'
$state = Get-Content (Join-Path $BackupPath 'deployment.json') -Raw | ConvertFrom-Json
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
if ($state.Sid -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Use a separate administrator account.' }
$loaded = Test-Path "Registry::HKEY_USERS\$($state.Sid)"
if ($loaded -and -not $AllowLoadedProfile) { throw 'Sign the kiosk account out completely first, or use -AllowLoadedProfile for recovery of a stuck session.' }
$mount = 'PrintGate_Restore'
if ($loaded) { $mount = $state.Sid }
else {
    & reg.exe load "HKU\$mount" "$($state.Profile)\NTUSER.DAT" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Run as administrator and verify the profile path.' }
}
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
    if (-not $loaded) { & reg.exe unload "HKU\$mount" | Out-Null }
}
Write-Host 'Original user policy values restored. AppLocker policy, application files, and logs are retained; restore AppLocker separately if you applied it.'
