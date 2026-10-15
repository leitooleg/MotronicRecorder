param([string]$OutputName = 'MotronicRecorder-0.2.5.exe')
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compilerPath)) { throw '.NET Framework 4.x compiler was not found.' }
$binaryDirectory = Join-Path $projectDirectory 'bin'
New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$testFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Runtime.Serialization.dll')
$references += '/resource:' + (Join-Path $projectDirectory 'ПОДКЛЮЧЕНИЕ.txt') + ',MotronicRecorder.ConnectionGuide.txt'
& $compilerPath /nologo /utf8output /codepage:65001 /optimize+ /target:winexe /main:MotronicRecorder.Program "/out:$(Join-Path $binaryDirectory $OutputName)" $references $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
& $compilerPath /nologo /utf8output /codepage:65001 /optimize+ /target:exe /main:MotronicRecorder.TestRunner "/out:$(Join-Path $binaryDirectory 'MotronicRecorder.Tests.exe')" $references $sourceFiles $testFiles
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
Write-Output (Join-Path $binaryDirectory $OutputName)
