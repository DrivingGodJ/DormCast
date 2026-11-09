#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'DormCast.exe'),
    [ValidateRange(1024, 65535)][int]$Port = 8080,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
if ([IO.Path]::GetFileName($resolvedExe) -ne 'DormCast.exe') { throw 'Select the published DormCast.exe.' }
$hashBytes = [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($resolvedExe.ToLowerInvariant()))
$pathKey = ([BitConverter]::ToString($hashBytes) -replace '-', '').Substring(0, 12)
$rulePrefix = "DormCast-$pathKey"
if ($Remove) {
    $rules = Get-NetFirewallRule -Name "$rulePrefix-*" -ErrorAction SilentlyContinue
    if ($rules -and $PSCmdlet.ShouldProcess($resolvedExe, 'Remove only DormCast firewall rules')) { $rules | Remove-NetFirewallRule }
    return
}
if ($PSCmdlet.ShouldProcess($resolvedExe, "Allow Private-profile LocalSubnet TCP $Port and WebRTC UDP")) {
    $common = @{Program=$resolvedExe; Direction='Inbound'; Action='Allow'; Profile='Private'; RemoteAddress='LocalSubnet'; EdgeTraversalPolicy='Block'}
    if (-not (Get-NetFirewallRule -Name "$rulePrefix-TCP-$Port" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule @common -Name "$rulePrefix-TCP-$Port" -DisplayName "DormCast LAN HTTP TCP $Port" -Protocol TCP -LocalPort $Port | Out-Null
    }
    if (-not (Get-NetFirewallRule -Name "$rulePrefix-UDP" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule @common -Name "$rulePrefix-UDP" -DisplayName 'DormCast LAN WebRTC UDP' -Protocol UDP | Out-Null
    }
    Write-Host 'DormCast rules added. Private network + LocalSubnet only. No Public-profile rule was added.'
}
