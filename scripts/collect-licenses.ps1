$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = @{}
foreach ($lockFile in @('src/DormCast/packages.lock.json','src/DormCast.Desktop/packages.lock.json')) {
    $lock = Get-Content -LiteralPath (Join-Path $root $lockFile) -Raw | ConvertFrom-Json
    foreach ($target in $lock.dependencies.PSObject.Properties) {
        foreach ($package in $target.Value.PSObject.Properties) { if ($package.Value.type -ne 'Project' -and $package.Value.resolved) { $packages[$package.Name] = $package.Value.resolved } }
    }
}
foreach ($key in $packages.Keys) {
    $version = $packages[$key]; $source = Join-Path "$env:USERPROFILE/.nuget/packages" "$($key.ToLowerInvariant())/$version"
    $destination = Join-Path $root "licenses/$key-$version"
    New-Item -ItemType Directory -Force $destination | Out-Null
    Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match 'license|notice|copying|copyright|\.nuspec$' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $destination -Force }
}
Write-Output "Preserved license metadata for $($packages.Count) packages."
