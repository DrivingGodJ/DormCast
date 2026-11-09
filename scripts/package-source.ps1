$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'src/DormCast/DormCast.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$zipPath=Join-Path $projectRoot "artifacts/DormCast-$version-source.zip"
New-Item -ItemType Directory -Force (Split-Path -Parent $zipPath) | Out-Null
$files=@()
foreach($dir in @('src','tests','scripts','docs','licenses')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $dir) -File -Recurse -Force | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|evidence[^\\/]*)[\\/]' -and $_.Name -notmatch '^(winui-|sync-|features-)?test-report\.md$' }
}
foreach($file in @('.gitignore','global.json','README.md','README.en.md','LICENSE','THIRD-PARTY-NOTICES.md')) { $files+=Get-Item -LiteralPath (Join-Path $projectRoot $file) -Force }
$stream=[IO.File]::Open($zipPath,[IO.FileMode]::Create)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false,[Text.Encoding]::UTF8)
try {
    foreach($file in $files) {
        $relative=[IO.Path]::GetRelativePath($projectRoot,$file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,"DormCast-source/$relative",[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $zip.Dispose(); $stream.Dispose() }
Write-Output "Packaged $($files.Count) source/docs/license files."
