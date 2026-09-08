param(
    [string]$CelestePath = (Join-Path $PSScriptRoot '../..'),
    [string]$CctAssemblyPath = (Join-Path $CelestePath 'Mods/Cache/ConsistencyTracker.ConsistencyTracker.dll')
)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoPath 'CNGoldenLink.csproj'
$version = ([xml](Get-Content -LiteralPath $projectPath -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must be major.minor.patch' }
$manifest = Get-Content -LiteralPath (Join-Path $repoPath 'everest.yaml') -Raw
if ($manifest -notmatch "(?m)^  Version: $([regex]::Escape($version))\s*$") { throw 'everest.yaml version must match project Version' }
dotnet build $projectPath -c Release "-p:CelestePath=$CelestePath" "-p:CctAssemblyPath=$CctAssemblyPath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Add-Type -AssemblyName System.IO.Compression
$outputPath = Join-Path $repoPath 'artifacts'
New-Item -ItemType Directory -Force $outputPath | Out-Null
$zipPath = Join-Path $outputPath "CNGoldenLink-$version.zip"
$fileStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @{
        'bin/CNGoldenLink.dll' = 'bin/Release/net8.0/CNGoldenLink.dll'
        'everest.yaml' = 'everest.yaml'
        'Dialog/English.txt' = 'Dialog/English.txt'
        'Dialog/Simplified Chinese.txt' = 'Dialog/Simplified Chinese.txt'
        'README.md' = 'README.md'
    }
    foreach ($entryName in $files.Keys) {
        $entry = $archive.CreateEntry($entryName)
        $entryStream = $entry.Open()
        $sourceStream = [IO.File]::OpenRead((Join-Path $repoPath $files[$entryName]))
        try { $sourceStream.CopyTo($entryStream) }
        finally { $sourceStream.Dispose(); $entryStream.Dispose() }
    }
}
finally { $archive.Dispose(); $fileStream.Dispose() }
Write-Output $zipPath
