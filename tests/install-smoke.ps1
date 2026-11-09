param([string]$Version = '0.5.3')
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath '.').Path
$installPath = [IO.Path]::GetFullPath((Join-Path $workspace '.research/installer-smoke'))
if (-not $installPath.StartsWith((Join-Path $workspace '.research') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install test path escaped workspace' }
$uninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{AD419A2F-126A-41DF-AC7E-D90DD9C98751}_is1'
if (Test-Path $uninstallKey) { throw 'An existing installed DormCast registration must be preserved' }
$savedFiles = @{}
foreach ($name in @('settings.json','join-identity.json')) { $path = Join-Path "$env:LOCALAPPDATA/DormCast" $name; if (Test-Path -LiteralPath $path) { $savedFiles[$path] = (Get-FileHash -LiteralPath $path).Hash } }
& .tools/innosetup/ISCC.exe /DSmokeTest "/DAppVersion=$Version" "/DPayloadDir=$(Join-Path $workspace "artifacts/DormCast-$Version-win-x64")" scripts/installer.iss | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Isolated smoke-test installer compilation failed' }
$setup = Start-Process -FilePath (Join-Path $workspace "artifacts/DormCast-$Version-SmokeTest.exe") -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS','/TASKS=',('/DIR="' + $installPath + '"'),('/LOG="' + (Join-Path $workspace '.research/install-smoke.log') + '"')) -WindowStyle Hidden -PassThru -Wait
if ($setup.ExitCode -ne 0) { throw "Installer exit $($setup.ExitCode)" }
foreach ($name in @('DormCast.exe','DormCast.Core.dll','DormCast.pri','App.xbf','Microsoft.UI.Xaml.dll','hostfxr.dll','coreclr.dll','Drivers/VBCABLE_Driver_Pack46.zip','Updates/Install-Update.ps1')) { if (-not (Test-Path -LiteralPath (Join-Path $installPath $name))) { throw "Installed dependency missing: $name" } }
if ((Get-FileHash -LiteralPath (Join-Path $installPath 'Drivers/VBCABLE_Driver_Pack46.zip')).Hash -ne (Get-FileHash -LiteralPath .tools/drivers/VBCABLE_Driver_Pack46.zip).Hash) { throw 'Installed official driver archive was modified' }
$stopFlag = '.research/installed-stop-' + [Guid]::NewGuid().ToString('N') + '.txt'
$savedDotnetRoot = $env:DOTNET_ROOT; $savedLookup = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT = Join-Path $workspace '.research/no-global-dotnet'; $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $probe = Start-Process -FilePath (Join-Path $installPath 'DormCast.exe') -ArgumentList @('--headless','--port','18089','--duration','60','--diagnostics','test-results/winui/installed-host.json','--stop-file',$stopFlag) -WindowStyle Hidden -PassThru
} finally { $env:DOTNET_ROOT = $savedDotnetRoot; $env:DOTNET_MULTILEVEL_LOOKUP = $savedLookup }
try {
    Start-Sleep -Seconds 3
    $diagnostic = Get-Content -LiteralPath test-results/winui/installed-host.json -Raw | ConvertFrom-Json
    $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false; $http = [Net.Http.HttpClient]::new($handler)
    try {
        $page = $http.GetStringAsync("$($diagnostic.endpoint)/").GetAwaiter().GetResult()
        if ($page -notmatch 'apple-touch-icon' -or $page -match '音量请使用') { throw 'Installed web page mismatch' }
        $png = $http.GetByteArrayAsync("$($diagnostic.endpoint)/apple-touch-icon.png").GetAwaiter().GetResult()
        if ($png.Length -lt 100 -or $png[0] -ne 137) { throw 'Installed iOS icon missing' }
        if ($diagnostic.audio.frames -lt 1 -or $diagnostic.audio.error) { throw 'Installed audio engine failed' }
    } finally { $http.Dispose() }
} finally {
    Set-Content -LiteralPath $stopFlag -Value stop
    if (-not $probe.WaitForExit(8000)) { throw 'Installed test service did not stop cleanly' }
}
if ($probe.ExitCode -ne 0) { throw 'Installed test service returned failure' }
$uninstaller = Join-Path $installPath 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'Standard uninstaller missing' }
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $workspace '.research/uninstall-smoke.log') + '"')) -WindowStyle Hidden -PassThru -Wait
if ($uninstall.ExitCode -ne 0) { throw 'Uninstall test failed' }
if (Test-Path -LiteralPath (Join-Path $installPath 'DormCast.exe')) { throw 'Uninstall left the application behind' }
$preserved = $true
foreach ($path in $savedFiles.Keys) { if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $savedFiles[$path]) { $preserved = $false } }
@{version=$Version;installExit=$setup.ExitCode;uninstallExit=$uninstall.ExitCode;requiredDependencies=$true;officialDriverArchiveUnchanged=$true;globalDotnetDisabled=$true;httpAndIosIcon=$true;audioFrames=$diagnostic.audio.frames;existingProfilePreserved=$preserved} | ConvertTo-Json | Set-Content -LiteralPath test-results/winui/installer-checks.json
Get-Content -LiteralPath test-results/winui/installer-checks.json
