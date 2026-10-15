$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$releaseDirectory = $PSScriptRoot
$parentDirectory = Split-Path $releaseDirectory -Parent
$sourceArchive = Join-Path $parentDirectory 'MotronicRecorder-0.2.5.zip'
$standaloneArchive = Join-Path $parentDirectory 'MotronicRecorder-0.2.5-Standalone-x64.zip'
foreach ($path in @($sourceArchive, $standaloneArchive)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite archive: $path" }
}

$files = @(Get-ChildItem -LiteralPath $releaseDirectory -File | Where-Object { $_.Extension -in @('.txt','.md','.ps1','.png') })
foreach ($directory in @('src','tests','Examples','analysis')) {
    $files += @(Get-ChildItem -LiteralPath (Join-Path $releaseDirectory $directory) -File -Recurse)
}
$files += Get-Item -LiteralPath (Join-Path $releaseDirectory 'bin/MotronicRecorder-0.2.5.exe')
$files += @(Get-ChildItem -LiteralPath (Join-Path $releaseDirectory 'standalone') -File | Where-Object { $_.Name -ne 'publish-log.txt' })
$zip = [IO.Compression.ZipFile]::Open($sourceArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files | Sort-Object FullName -Unique) {
        $relative = $file.FullName.Substring($releaseDirectory.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ('MotronicRecorder-0.2.5/' + $relative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }

$zip = [IO.Compression.ZipFile]::Open($standaloneArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    $standaloneFiles = @(
        (Join-Path $parentDirectory 'MotronicRecorder-0.2.5-Standalone-x64.exe'),
        (Join-Path $releaseDirectory 'ПОДКЛЮЧЕНИЕ.txt'),
        (Join-Path $releaseDirectory 'DISCLAIMER.txt'),
        (Join-Path $releaseDirectory 'README.ru.txt'),
        (Join-Path $releaseDirectory 'CHANGES-0.2.5.txt'),
        (Join-Path $releaseDirectory 'VALIDATION.txt'),
        (Join-Path $releaseDirectory 'SHA256SUMS.txt'),
        (Join-Path $releaseDirectory 'standalone/ПРОЧИТАТЬ-STANDALONE.txt')
    )
    foreach ($path in $standaloneFiles) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, [IO.Path]::GetFileName($path), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
Get-FileHash -LiteralPath $sourceArchive,$standaloneArchive -Algorithm SHA256
