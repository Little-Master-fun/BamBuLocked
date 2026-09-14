# Set inheritable permissions on the directory, then let descendants inherit them.
# Passing inheritance-only grants to every file with /T can leave files with an empty DACL.
function Set-WorkstationDirectoryAcl {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$Grants)
    & icacls.exe $Path '/inheritance:r' '/grant:r' @Grants | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Failed to protect directory: $Path" }
    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
        & icacls.exe (Join-Path $Path '*') '/reset' '/T' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Failed to configure descendant permissions: $Path" }
    }
}
