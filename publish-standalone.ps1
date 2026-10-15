param(
    [string]$DotNetPath = '',
    [switch]$DownloadSdk,
    [switch]$RunTests
)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$buildWorkspace = Join-Path (Split-Path $projectDirectory -Parent) 'build-toolchains/dotnet8'
$sdkVersion = '8.0.426'
$localSdkDirectory = Join-Path $buildWorkspace $sdkVersion
$localDotNet = Join-Path $localSdkDirectory 'dotnet.exe'

if (!$DotNetPath) {
    if (Test-Path -LiteralPath $localDotNet) { $DotNetPath = $localDotNet }
    elseif ($DownloadSdk) {
        New-Item -ItemType Directory -Path $buildWorkspace -Force | Out-Null
        $metadataPath = Join-Path $buildWorkspace 'releases.json'
        Invoke-WebRequest -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json' -OutFile $metadataPath
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        $sdk = @($metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $sdkVersion | Select-Object -First 1)
        if (!$sdk.Count) { throw "Official metadata does not contain pinned SDK $sdkVersion" }
        $file = @($sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1)
        if (!$file.Count) { throw 'Official SDK ZIP metadata is missing.' }
        $archive = Join-Path $buildWorkspace "dotnet-sdk-$sdkVersion-win-x64.zip"
        Invoke-WebRequest -Uri $file[0].url -OutFile $archive -TimeoutSec 300
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file[0].hash) { throw 'SDK SHA512 verification failed.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $localSdkDirectory -Force
        $DotNetPath = $localDotNet
    }
    else {
        $command = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($command) { $DotNetPath = $command.Source }
        else { throw 'Supply -DotNetPath for a .NET 8 SDK or use -DownloadSdk. No system installation is performed.' }
    }
}
if (!(Test-Path -LiteralPath $DotNetPath)) { throw "dotnet.exe not found: $DotNetPath" }
$sdkOutput = & $DotNetPath --list-sdks
if ($LASTEXITCODE -ne 0 -or !($sdkOutput -match '^8\.0\.')) { throw 'A .NET 8 SDK is required; a runtime-only dotnet.exe cannot build this application.' }

# These variables affect this process and its children only. No system settings
# or PATH entries are installed. Restore caches also stay inside the workspace.
$variableNames = @('DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_NOLOGO','DOTNET_CLI_HOME','NUGET_PACKAGES','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_GENERATE_ASPNET_CERTIFICATE')
$previousVariables = @{}
foreach ($name in $variableNames) { $previousVariables[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = '0'
    $env:DOTNET_CLI_HOME = Join-Path $buildWorkspace 'cli-home'
    $env:NUGET_PACKAGES = Join-Path $buildWorkspace 'packages'
    $standaloneDirectory = Join-Path $projectDirectory 'standalone'
    $outputDirectory = Join-Path $projectDirectory 'standalone-out'
    $config = Join-Path $standaloneDirectory 'NuGet.Config'
    $project = Join-Path $standaloneDirectory 'MotronicRecorder.Standalone.csproj'
    & $DotNetPath restore $project --configfile $config --runtime win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Standalone dependency restore failed.' }
    & $DotNetPath publish $project --no-restore --configuration Release --runtime win-x64 --self-contained true --output $outputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Standalone publication failed.' }
    $executable = Join-Path $outputDirectory 'MotronicRecorder-0.2.5-Standalone-x64.exe'
    if (!(Test-Path -LiteralPath $executable)) { throw 'Published standalone EXE is missing.' }
    Get-FileHash -LiteralPath $executable -Algorithm SHA256 | Format-List
    if ($RunTests) {
        $testProject = Join-Path $standaloneDirectory 'MotronicRecorder.Standalone.Tests.csproj'
        $testDirectory = Join-Path $projectDirectory 'standalone-tests'
        & $DotNetPath restore $testProject --configfile $config --runtime win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Standalone test dependency restore failed.' }
        & $DotNetPath publish $testProject --no-restore --configuration Release --runtime win-x64 --self-contained true --output $testDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Standalone test publication failed.' }
        & (Join-Path $testDirectory 'MotronicRecorder-Standalone.Tests.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Standalone offline tests failed.' }
    }
    Write-Output $executable
}
finally { foreach ($name in $variableNames) { [Environment]::SetEnvironmentVariable($name, $previousVariables[$name], 'Process') } }
