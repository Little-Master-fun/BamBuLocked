[CmdletBinding()]
param(
    [string]$KioskUser = 'Printer',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\workstation-diagnostic.txt')
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
$report = New-Object System.Collections.Generic.List[string]
function Add-Report($Title, $Value) {
    $report.Add("`r`n=== $Title ===")
    $report.Add(($Value | Out-String -Width 240))
}
$mounted = $false
try {
    $user = Get-LocalUser -Name $KioskUser
    $sid = $user.SID.Value
    $profile = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid").ProfileImagePath
    Add-Report 'Account' ($user | Select-Object Name, SID, Enabled)
    $hive = $sid
    Add-Report 'Loaded hives' (Get-ChildItem Registry::HKEY_USERS -Name)
    Add-Report 'Profile' $profile
    if (-not (Test-Path "Registry::HKEY_USERS\$sid")) {
        $hive = 'PG' + $PID
        $ErrorActionPreference = 'Continue'
        $loadOutput = & reg.exe load "HKU\$hive" "$profile\NTUSER.DAT" 2>&1
        $loadExit = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        Add-Report 'Hive load' $loadOutput
        $mounted = $loadExit -eq 0
        if (-not $mounted) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
public static class PrintGateHiveReader {
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode)]
    static extern int RegLoadAppKey(string file, out SafeRegistryHandle key, int access, int options, int reserved);
    public static RegistryKey Open(string file) {
        SafeRegistryHandle handle;
        int result = RegLoadAppKey(file, out handle, 0x20019, 1, 0);
        if (result != 0) throw new System.ComponentModel.Win32Exception(result);
        return RegistryKey.FromHandle(handle);
    }
}
'@
            try {
                $appHive = [PrintGateHiveReader]::Open((Join-Path $profile 'NTUSER.DAT'))
                try {
                    foreach ($relative in @('Software\Microsoft\Windows\CurrentVersion\Policies\System', 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon', 'Software\Microsoft\Windows\CurrentVersion\Run', 'Software\Policies\Microsoft\Windows\System')) {
                        $appKey = $appHive.OpenSubKey($relative)
                        if ($appKey) {
                            try { Add-Report "Offline $relative" ($appKey.GetValueNames() | ForEach-Object { "$_ = $($appKey.GetValue($_))" }) }
                            finally { $appKey.Dispose() }
                        }
                    }
                } finally { $appHive.Dispose() }
            } catch { Add-Report 'Offline hive read error' $_.Exception.Message }
        }
    }
    foreach ($path in @(
        "Registry::HKEY_USERS\$hive\Software\Microsoft\Windows\CurrentVersion\Policies\System",
        "Registry::HKEY_USERS\$hive\Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
        "Registry::HKEY_USERS\$hive\Software\Policies\Microsoft\Windows\System",
        "Registry::HKEY_USERS\$hive\Software\Microsoft\Windows\CurrentVersion\Run",
        "Registry::HKEY_USERS\$hive\Software\Microsoft\Windows\CurrentVersion\RunOnce",
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
    )) {
        if (Test-Path $path) { Add-Report $path (Get-ItemProperty $path) }
    }
    $backup = 'C:\ProgramData\PrintGate\DeploymentBackup\deployment.json'
    Add-Report 'Profile files' (Get-ChildItem -LiteralPath $profile -Force | Select-Object Name,Length,Attributes,LinkType,Target)
    Add-Report 'Profile service events' (Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddDays(-2)} -MaxEvents 500 -ErrorAction SilentlyContinue | Where-Object {$_.ProviderName -match 'User Profiles'} | Select-Object TimeCreated,Id,Message)
    if (Test-Path $backup) {
        $state = Get-Content $backup -Raw | ConvertFrom-Json
        Add-Report 'Deployment' ($state | Select-Object Sid,Profile,InstallPath)
        $configPath = Join-Path $state.InstallPath 'appsettings.json'
        Add-Report 'Executable' (Get-Item (Join-Path $state.InstallPath 'PrintGate.exe') | Select-Object FullName,Length,LastWriteTime)
        Add-Report 'Installed file attributes' (Get-ChildItem -LiteralPath $state.InstallPath -Force | Select-Object Name,Length,Attributes,LinkType,Target)
        Add-Report 'Installed ACL' (& icacls.exe $state.InstallPath)
        Add-Report 'Config ACL' (& icacls.exe $configPath)
        Add-Report 'Config encryption' (& cipher.exe /c $configPath)
        if (Test-Path $configPath) {
            try {
                $config = Get-Content $configPath -Raw | ConvertFrom-Json
                Add-Report 'Paths' ($config | Select-Object StudioPath, FfmpegPath, RecordingsDirectory)
                Add-Report 'Studio exists' (Test-Path -LiteralPath $config.StudioPath)
            } catch { Add-Report 'Config read error' $_.Exception.Message }
        }
    }
    foreach ($folder in @("$profile\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup", 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup')) {
        if (Test-Path $folder) {
            Add-Report $folder (Get-ChildItem -LiteralPath $folder -Force | Select-Object Name,FullName)
            $shell = New-Object -ComObject WScript.Shell
            foreach ($link in Get-ChildItem -LiteralPath $folder -Filter '*.lnk') {
                $shortcut = $shell.CreateShortcut($link.FullName)
                Add-Report $link.Name ($shortcut | Select-Object TargetPath,Arguments,WorkingDirectory)
            }
        }
    }
    Add-Report 'Related scheduled tasks' (Get-ScheduledTask | Where-Object {
        $_.Principal.UserId -in @($sid,$KioskUser,"$env:COMPUTERNAME\$KioskUser") -or
        ($_.Actions.Execute -join ' ') -match 'PrintGate|cmd.exe'
    } | Select-Object TaskName,TaskPath,State, @{n='User';e={$_.Principal.UserId}}, @{n='Actions';e={($_.Actions | Select-Object Execute,Arguments | ConvertTo-Json -Compress)}})
    Add-Report 'Recent application errors' (Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddDays(-2); Level=2} -MaxEvents 100 -ErrorAction SilentlyContinue | Where-Object {$_.Message -match 'PrintGate|coreclr'} | Select-Object TimeCreated,Id,ProviderName,Message)
} catch {
    Add-Report 'Diagnostic error' $_.Exception.Message
} finally {
    if ($mounted) {
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
        & reg.exe unload "HKU\$hive" | Out-Null
        Add-Report 'Hive unload exit code' $LASTEXITCODE
    }
    $report | Set-Content -LiteralPath $OutputPath -Encoding UTF8
}
