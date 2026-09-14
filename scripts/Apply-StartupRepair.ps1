# Apply the locally built startup fix to this workstation; retain the self-contained runtime/config.
[CmdletBinding()]
param([string]$BuildPath = (Join-Path $PSScriptRoot '..\artifacts\fixed-build'))
$ErrorActionPreference = 'Stop'
$reportPath = Join-Path $PSScriptRoot '..\artifacts\startup-repair-result.txt'
$report = New-Object System.Collections.Generic.List[string]
try {
    $sid = (Get-LocalUser Printer).SID.Value
    $state = Get-Content 'C:\ProgramData\PrintGate\DeploymentBackup\deployment.json' -Raw | ConvertFrom-Json
    if ($sid -ne $state.Sid) { throw 'Deployment account mismatch.' }
    if (-not (Test-Path "Registry::HKEY_USERS\$sid")) { throw 'Printer session must remain signed in for its interactive probe.' }
    if (Get-Process PrintGate -ErrorAction SilentlyContinue) { throw 'PrintGate is still running; do not replace active binaries.' }
    $backup = Join-Path 'C:\ProgramData\PrintGate' ('StartupRepair-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    $targets = @($state.InstallPath, 'C:\Program Files\PrintGate-win-x64-offline\app')
    for ($i=0; $i -lt $targets.Count; $i++) {
        $saved = Join-Path $backup "app-$i"
        New-Item -ItemType Directory -Path $saved | Out-Null
        foreach ($name in @('PrintGate.dll','PrintGate.pdb')) {
            Copy-Item -LiteralPath (Join-Path $targets[$i] $name) -Destination (Join-Path $saved $name)
            Copy-Item -LiteralPath (Join-Path $BuildPath $name) -Destination (Join-Path $targets[$i] $name) -Force
            if ((Get-FileHash (Join-Path $targets[$i] $name)).Hash -ne (Get-FileHash (Join-Path $BuildPath $name)).Hash) { throw 'Updated binary hash mismatch.' }
        }
        $report.Add("Updated: $($targets[$i])")
    }
    $taskName = 'PrintGate-StartupProbe-20260909'
    $action = New-ScheduledTaskAction -Execute (Join-Path $state.InstallPath 'PrintGate.exe') -Argument '--diagnose-startup' -WorkingDirectory $state.InstallPath
    $principal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
    $previous = (Get-ScheduledTaskInfo $taskName).LastRunTime
    Start-ScheduledTask $taskName
    $deadline = (Get-Date).AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        $info = Get-ScheduledTaskInfo $taskName
        $running = (Get-ScheduledTask $taskName).State
        $completed = $info.LastRunTime -ne $previous -and $running -eq 'Ready'
    } until ($completed -or (Get-Date) -gt $deadline)
    if (-not $completed -or $info.LastTaskResult -ne 0) { throw "Installed startup probe failed: $($info.LastTaskResult). Normal desktop policy is retained." }
    $report.Add('Installed startup probe under Printer: exit 0.')
    $policies = Get-Content 'C:\ProgramData\PrintGate\black-screen-policy-backup.json' -Raw | ConvertFrom-Json
    foreach ($policy in $policies) {
        $key = [Microsoft.Win32.Registry]::Users.CreateSubKey("$sid\$($policy.Key)")
        try {
            if ($policy.Type) { $key.SetValue($policy.Name,$policy.Value,[Microsoft.Win32.RegistryValueKind]::$($policy.Type)) }
            else { $key.DeleteValue($policy.Name,$false) }
        } finally {$key.Dispose()}
    }
    $report.Add('Printer custom shell and restrictions restored for the next full sign-in.')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Restore-Workstation.ps1') -Destination 'C:\Program Files\PrintGate-win-x64-offline\scripts\Restore-Workstation.ps1' -Force
    foreach ($name in @($taskName,'PrintGate-RecoverDesktop-20260909')) { Unregister-ScheduledTask -TaskName $name -Confirm:$false }
    $report.Add("Backup: $backup")
    $logs = Join-Path $state.Profile 'AppData\Local\PrintGate\Diagnostics'
    Get-ChildItem $logs -Filter '*.log' | Copy-Item -Destination (Join-Path $PSScriptRoot '..\artifacts\printer-startup') -Force
} catch {
    $report.Add("ERROR: $($_.Exception.Message)")
    throw
} finally { $report | Set-Content -LiteralPath $reportPath -Encoding UTF8 }
