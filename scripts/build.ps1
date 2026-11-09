[CmdletBinding()]
param([switch]$SkipChecks)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $dotnetExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_ROOT = Split-Path -Parent $dotnetExe
    $desktopProject = 'src/DormCast.Desktop/DormCast.Desktop.csproj'
    [xml]$project = Get-Content -LiteralPath $desktopProject -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    $outputDirectory = "artifacts/DormCast-$version-win-x64"
    & $dotnetExe restore $desktopProject --locked-mode -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    if (-not $SkipChecks) {
        & $dotnetExe run --project tests/DormCast.Checks -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
        & node tests/sync-math-checks.cjs
        if ($LASTEXITCODE -ne 0) { throw 'Browser synchronization checks failed.' }
    }
    & $dotnetExe publish $desktopProject -c Release -r win-x64 --self-contained true -o $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath scripts/firewall.ps1 -Destination "$outputDirectory/firewall.ps1"
    Copy-Item -LiteralPath docs/GUIDE.zh-CN.md -Destination "$outputDirectory/使用说明.md"
    Copy-Item -LiteralPath README.md,README.en.md -Destination $outputDirectory
    Copy-Item -LiteralPath LICENSE -Destination "$outputDirectory/LICENSE"
    New-Item -ItemType Directory -Force "$outputDirectory/docs" | Out-Null
    Get-ChildItem -LiteralPath docs -File | Where-Object { $_.Name -notmatch '^(winui-|sync-|features-)?test-report\.md$' } | Copy-Item -Destination "$outputDirectory/docs"
    New-Item -ItemType Directory -Force "$outputDirectory/Updates" | Out-Null
    Copy-Item -LiteralPath scripts/Install-Update.ps1 -Destination "$outputDirectory/Updates/Install-Update.ps1"
    & "$PSScriptRoot/collect-licenses.ps1"
    Copy-Item -LiteralPath licenses -Destination $outputDirectory -Recurse -Force
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination "$outputDirectory/THIRD-PARTY-NOTICES.md"
    $driverArchive = Join-Path $projectRoot '.tools/drivers/VBCABLE_Driver_Pack46.zip'
    if (-not (Test-Path -LiteralPath $driverArchive)) { throw 'Place the unchanged official VBCABLE_Driver_Pack46.zip in .tools/drivers before packaging.' }
    New-Item -ItemType Directory -Force "$outputDirectory/Drivers" | Out-Null
    Copy-Item -LiteralPath $driverArchive -Destination "$outputDirectory/Drivers/VBCABLE_Driver_Pack46.zip"
    Copy-Item -LiteralPath docs/driver-first-use.txt -Destination "$outputDirectory/Drivers/首次使用说明.txt"
    Compress-Archive -Path "$outputDirectory/*" -DestinationPath "artifacts/DormCast-$version-win-x64.zip" -Force
    & "$PSScriptRoot/package-source.ps1"
    $iscc = Join-Path $projectRoot '.tools/innosetup/ISCC.exe'
    if (-not (Test-Path -LiteralPath $iscc)) { $iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source }
    & $iscc "/DAppVersion=$version" "/DPayloadDir=$(Join-Path $projectRoot $outputDirectory)" "$PSScriptRoot/installer.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installerName = "DormCast-$version-Setup-x64.exe"
    $installer = Get-Item -LiteralPath "artifacts/$installerName"
    $installerHash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = @{ tag_name = "v$version"; draft = $false; prerelease = $false; body = "DormCast $version"; assets = @(@{ name = $installerName; size = $installer.Length; browser_download_url = "https://github.com/DrivingGodJ/DormCast/releases/download/v$version/$installerName"; digest = "sha256:$installerHash" }) } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/update.json'), $manifest, [Text.UTF8Encoding]::new($false))
    $releaseFiles = @("artifacts/$installerName", "artifacts/DormCast-$version-win-x64.zip", "artifacts/DormCast-$version-source.zip", 'artifacts/update.json')
    $checksums = foreach ($file in $releaseFiles) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file) }
    [IO.File]::WriteAllLines((Join-Path $projectRoot 'artifacts/SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
    Write-Host "Portable build: artifacts/DormCast-$version-win-x64.zip"
}
finally { Pop-Location }
