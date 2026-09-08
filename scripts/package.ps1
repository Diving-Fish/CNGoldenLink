param([string]$CelestePath = (Join-Path $PSScriptRoot '../..'))
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
dotnet build (Join-Path $repoPath 'CNGoldenLink.csproj') -c Release "-p:CelestePath=$CelestePath" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Add-Type -AssemblyName System.IO.Compression
$outputPath = Join-Path $repoPath 'artifacts'
New-Item -ItemType Directory -Force $outputPath | Out-Null
$zipPath = Join-Path $outputPath 'CNGoldenLink-0.1.0.zip'
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
