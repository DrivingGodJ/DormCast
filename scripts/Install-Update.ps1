param([Parameter(Mandatory=$true)][int]$ParentId, [Parameter(Mandatory=$true)][string]$Installer, [Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{64}$')][string]$Sha256)
$ErrorActionPreference = 'Stop'
try {
    if (-not [IO.Path]::IsPathRooted($Installer) -or [IO.Path]::GetExtension($Installer) -ne '.exe') { throw 'Invalid update path' }
    $parent = Get-Process -Id $ParentId -ErrorAction SilentlyContinue
    if ($parent) { if (-not $parent.WaitForExit(60000)) { throw 'DormCast did not exit; update stopped' } }
    if ((Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -ine $Sha256) { throw 'Installer integrity check failed' }
    Start-Process -FilePath $Installer
} catch {
    $log = Join-Path $env:LOCALAPPDATA 'DormCast/updates/update-error.txt'
    New-Item -ItemType Directory -Force (Split-Path -Parent $log) | Out-Null
    $_.Exception.Message | Set-Content -LiteralPath $log -Encoding UTF8
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show('更新未能启动。请重新打开宿舍之音检查更新，或从 GitHub Release 手动下载安装包。','宿舍之音更新') | Out-Null
    exit 1
}
