# Run before Install-Workstation, against an extracted/published package.
# Downloads the Windows encoder from the FFmpeg project's linked build provider.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $PackagePath 'PrintGate.exe'))) { throw 'Select a published PrintGate app directory.' }
$target = Join-Path $PackagePath 'Tools'
if (Test-Path (Join-Path $target 'ffmpeg.exe')) { throw 'FFmpeg already exists. Review updates before replacing it.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('PrintGate-FFmpeg-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $url = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
    $archive = Join-Path $work 'ffmpeg.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
    $hashText = (Invoke-WebRequest -UseBasicParsing -Uri ($url + '.sha256')).Content
    if ($hashText -is [byte[]]) { $hashText = [Text.Encoding]::UTF8.GetString($hashText) }
    $match = [regex]::Match([string]$hashText, '(?i)\b[0-9a-f]{64}\b')
    if (-not $match.Success -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $match.Value) { throw 'FFmpeg checksum verification failed.' }
    $expanded = Join-Path $work 'expanded'
    Expand-Archive -Path $archive -DestinationPath $expanded
    $executables = @(Get-ChildItem $expanded -Filter ffmpeg.exe -Recurse)
    if ($executables.Count -ne 1) { throw 'Unexpected FFmpeg archive layout.' }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item $executables[0].FullName (Join-Path $target 'ffmpeg.exe')
    $bundle = $executables[0].Directory.Parent.FullName
    foreach ($name in @('LICENSE', 'LICENSE.txt', 'README.txt')) {
        if (Test-Path (Join-Path $bundle $name)) { Copy-Item (Join-Path $bundle $name) $target }
    }
    @{ DownloadUrl=$url; ArchiveSHA256=$match.Value; InstalledAt=[DateTimeOffset]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content (Join-Path $target 'ffmpeg-source.json') -Encoding UTF8
    Write-Host 'Encoder installed and archive hash verified. Continue workstation deployment; recording must still be tested on Windows.'
} finally { Remove-Item $work -Recurse -Force }
