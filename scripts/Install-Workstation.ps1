# Run in 64-bit elevated Windows PowerShell 5.1 from a DIFFERENT administrator account.
# Does not enable automatic Windows login or AppLocker enforcement.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KioskUser,
    [Parameter(Mandatory)][string]$PackagePath,
    [string]$InstallPath = 'C:\Program Files\PrintGate',
    [string]$BackupPath = 'C:\ProgramData\PrintGate\DeploymentBackup'
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
$user = Get-LocalUser -Name $KioskUser
$sid = $user.SID.Value
if ($sid -eq [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { throw 'Use a separate administrator account.' }
$admins = Get-LocalGroupMember -SID 'S-1-5-32-544'
if ($admins.SID.Value -contains $sid) { throw 'The kiosk account must not be an administrator.' }
if (Test-Path "Registry::HKEY_USERS\$sid") { throw 'Sign the kiosk account out completely before deployment.' }
if (-not (Test-Path (Join-Path $PackagePath 'PrintGate.exe'))) { throw 'Publish the Windows package first.' }
$config = Get-Content (Join-Path $PackagePath 'appsettings.json') -Raw | ConvertFrom-Json
$encoder = $config.FfmpegPath
if (-not [IO.Path]::IsPathRooted($encoder)) { $encoder = Join-Path $PackagePath $encoder }
if (-not (Test-Path $encoder)) { throw 'Run Install-Recorder.ps1 against the package before deployment.' }
$recordings = [IO.Path]::GetFullPath($config.RecordingsDirectory)
if (-not [IO.Path]::IsPathRooted($config.RecordingsDirectory) -or $recordings.StartsWith('\\') -or $recordings.TrimEnd('\') -eq [IO.Path]::GetPathRoot($recordings).TrimEnd('\')) { throw 'Choose a dedicated local recordings directory, not a drive root.' }
if ((Test-Path $recordings) -and -not (Test-Path (Join-Path $recordings '.printgate-recordings')) -and @(Get-ChildItem $recordings -Force).Count -gt 0) { throw 'The recordings directory contains unrelated files. Choose a new dedicated directory.' }
if (Test-Path (Join-Path $BackupPath 'deployment.json')) { throw 'Deployment backup exists. Restore or review it before reinstalling.' }
$profile = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid").ProfileImagePath
if (-not (Test-Path "$profile\NTUSER.DAT")) { throw 'Sign in to the kiosk account once, then sign out, to create its profile.' }
if (Test-Path $InstallPath) { throw 'Install directory already exists. Choose a fresh path or perform a reviewed update.' }
New-Item -ItemType Directory -Force $InstallPath, $BackupPath, 'C:\ProgramData\PrintGate\Data' | Out-Null
New-Item -ItemType Directory -Force $recordings | Out-Null
New-Item -ItemType File -Force (Join-Path $recordings '.printgate-recordings') | Out-Null
& icacls.exe $recordings '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${sid}:(OI)(CI)M" /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure recordings directory.' }
Copy-Item (Join-Path $PackagePath '*') $InstallPath -Recurse -Force
# Config and binaries: SYSTEM/admin write; kiosk read/execute only.
& icacls.exe $InstallPath '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${sid}:(OI)(CI)RX" /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to protect installation directory.' }
& icacls.exe $BackupPath '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to protect deployment backup.' }
# MVP logs are writable by the kiosk account; they are NOT tamper-proof audit storage.
& icacls.exe 'C:\ProgramData\PrintGate\Data' '/inheritance:r' '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*${sid}:(OI)(CI)M" /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure data directory.' }
$mount = "PrintGate_$($user.Name)"
& reg.exe load "HKU\$mount" "$profile\NTUSER.DAT" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to load kiosk registry hive.' }
$changes = @(
    @{ Key='Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name='Shell'; Value=('"' + (Join-Path $InstallPath 'PrintGate.exe') + '"'); Type='String' },
    @{ Key='Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name='DisableTaskMgr'; Value=1; Type='DWord' },
    @{ Key='Software\Microsoft\Windows\CurrentVersion\Policies\System'; Name='DisableRegistryTools'; Value=1; Type='DWord' },
    @{ Key='Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name='NoRun'; Value=1; Type='DWord' },
    @{ Key='Software\Microsoft\Windows\CurrentVersion\Policies\Explorer'; Name='NoWinKeys'; Value=1; Type='DWord' },
    @{ Key='Software\Policies\Microsoft\Windows\System'; Name='DisableCMD'; Value=1; Type='DWord' }
)
try {
    $saved = foreach ($change in $changes) {
        $key = [Microsoft.Win32.Registry]::Users.OpenSubKey("$mount\$($change.Key)")
        try {
            $exists = $key -and ($key.GetValueNames() -contains $change.Name)
            @{ Key=$change.Key; Name=$change.Name; Existed=[bool]$exists;
               Value=$(if ($exists) { $key.GetValue($change.Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null });
               Type=$(if ($exists) { $key.GetValueKind($change.Name).ToString() } else { $null }) }
        } finally { if ($key) { $key.Dispose() } }
    }
    @{ Sid=$sid; Profile=$profile; InstallPath=$InstallPath; Changes=@($saved) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $BackupPath 'deployment.json') -Encoding UTF8
    foreach ($change in $changes) {
        $key = [Microsoft.Win32.Registry]::Users.CreateSubKey("$mount\$($change.Key)")
        try { $key.SetValue($change.Name, $change.Value, [Microsoft.Win32.RegistryValueKind]::$($change.Type)) }
        finally { $key.Dispose() }
    }
} finally {
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    & reg.exe unload "HKU\$mount" | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Hive unload failed. Reboot before signing in.' }
}
Write-Host 'Installed custom user interface for the kiosk account. Complete AppLocker audit/enforcement and Windows acceptance tests before unattended use.'
Write-Host 'Automatic Windows login is not configured. No Windows or campus passwords are stored by this script.'
