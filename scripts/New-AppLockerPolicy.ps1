# Generates an AUDIT-ONLY starting policy. Does not apply it to Windows.
# Add observed, reviewed Bambu helper executables before enforcement.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$KioskUser,
    [string]$InstallPath = 'C:\Program Files\PrintGate',
    [string]$StudioPath = 'C:\Program Files\Bambu Studio\bambu-studio.exe',
    [string[]]$AdditionalExecutables = @(),
    [string]$OutputPath = '.\PrintGate-AppLocker.audit.xml'
)
$ErrorActionPreference = 'Stop'
$sid = (Get-LocalUser -Name $KioskUser).SID.Value
$paths = @((Join-Path $InstallPath 'PrintGate.exe'), (Join-Path $InstallPath 'Tools\ffmpeg.exe'), $StudioPath,
    "$env:windir\System32\userinit.exe", "$env:windir\System32\ctfmon.exe") + $AdditionalExecutables
$xml = New-Object System.Xml.XmlDocument
$root = $xml.CreateElement('AppLockerPolicy'); $root.SetAttribute('Version', '1'); $xml.AppendChild($root) | Out-Null
foreach ($type in @('Exe','Script','Msi','Dll')) {
    $collection = $xml.CreateElement('RuleCollection'); $collection.SetAttribute('Type',$type); $collection.SetAttribute('EnforcementMode','AuditOnly'); $root.AppendChild($collection) | Out-Null
    $rules = @(@{Sid='S-1-5-32-544'; Path='*'; Name='Administrators'}, @{Sid='S-1-5-18'; Path='*'; Name='SYSTEM'})
    if ($type -eq 'Exe') { $rules += @($paths | ForEach-Object { @{Sid=$sid; Path=$_; Name=[IO.Path]::GetFileName($_)} }) }
    if ($type -eq 'Dll') {
        # Scope to protected install folders. Verify ACLs and all plugin/update locations.
        $rules += @(@{Sid=$sid; Path="$InstallPath\*"; Name='PrintGate libraries'},
            @{Sid=$sid; Path="$([IO.Path]::GetDirectoryName($StudioPath))\*"; Name='Studio libraries'},
            @{Sid=$sid; Path='%WINDIR%\System32\*'; Name='Windows libraries'},
            @{Sid=$sid; Path='%WINDIR%\WinSxS\*'; Name='Windows side-by-side libraries'})
    }
    foreach ($rule in $rules) {
        $node = $xml.CreateElement('FilePathRule'); $node.SetAttribute('Id',[guid]::NewGuid().ToString()); $node.SetAttribute('Name',$rule.Name)
        $node.SetAttribute('Description','PrintGate initial audit policy; validate before enforcement.')
        $node.SetAttribute('UserOrGroupSid',$rule.Sid); $node.SetAttribute('Action','Allow')
        $conditions = $xml.CreateElement('Conditions'); $condition = $xml.CreateElement('FilePathCondition'); $condition.SetAttribute('Path',$rule.Path)
        $conditions.AppendChild($condition) | Out-Null; $node.AppendChild($conditions) | Out-Null; $collection.AppendChild($node) | Out-Null
    }
}
$xml.Save([IO.Path]::GetFullPath($OutputPath))
Write-Host "Audit-only policy generated: $OutputPath. This policy is for a dedicated local workstation, not a shared/domain policy baseline."
