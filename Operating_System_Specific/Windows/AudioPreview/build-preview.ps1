param(
    [string]$DotNet = 'dotnet',
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $PSScriptRoot 'AudioPreview.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw 'Invalid audio preview version.' }

# Each build starts with an empty publication directory. Do not copy an installed
# application or any profile, pairing cards, certificates or local settings.
$buildRoot = Join-Path $repository ('.build\audio-preview\' + [Guid]::NewGuid().ToString('N'))
$bundleDirectory = Join-Path $buildRoot 'BatCave-Audio-Preview'
$outputRoot = [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory))
if (-not (Test-Path -LiteralPath $outputRoot)) { New-Item -ItemType Directory -Path $outputRoot | Out-Null }
if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) { throw 'OutputDirectory must be a directory.' }
$packageName = 'BatCave-Audio-' + $version + '-Windows-x64.zip'
$packagePath = Join-Path $outputRoot $packageName
$manifestPath = Join-Path $outputRoot ($packageName + '.manifest.json')
foreach ($target in @($packagePath, ($packagePath + '.sha256'), $manifestPath)) {
    if (Test-Path -LiteralPath $target) { throw 'The output package already exists. Choose a fresh output directory.' }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $DotNet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false -o $bundleDirectory
if ($LASTEXITCODE -ne 0) { throw 'Audio preview publication failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $bundleDirectory 'BatCave.AudioPreview.exe') -PathType Leaf)) {
    throw 'Publication did not produce the audio preview executable.'
}

$privateExtensions = @('.pdb', '.pfx', '.p12', '.pem', '.key', '.cer', '.crt', '.log', '.conf')
$privateNames = @('identity.json', 'approved-peers.json', 'desktop-settings.json',
    'audio-settings.json', 'topology.secure.json', 'topology.local.json')
foreach ($file in Get-ChildItem -LiteralPath $bundleDirectory -Recurse -File) {
    if ($file.Extension.ToLowerInvariant() -in $privateExtensions -or $file.Name -in $privateNames -or
        $file.Name -like '*.pairing-card.json' -or $file.Name -like '*.runtimeconfig.dev.json') {
        throw 'Publication contains a private state file or development artifact.'
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $bundleDirectory 'README.md')
Compress-Archive -LiteralPath $bundleDirectory -DestinationPath $packagePath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($packagePath + '.sha256') -Value ($checksum + '  ' + $packageName) -Encoding ascii

$sourceCommit = $null
$sourceDirty = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $candidateCommit = & git -C $repository rev-parse HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        $sourceCommit = ([string]$candidateCommit).Trim()
        $sourceDirty = [bool](& git -C $repository status --porcelain)
    }
}
@{
    application = 'BatCave Audio Preview'
    version = $version
    sourceCommit = $sourceCommit
    sourceDirty = $sourceDirty
    architecture = 'win-x64'
    runtime = 'self-contained .NET 10'
    minimumWindows = 'Windows 11'
    sha256 = $checksum
    signed = $false
    liveCapturePlaybackVerified = $false
    macOSAudioAdapterReady = $false
    linuxAudioAdapterReady = $false
} | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
Get-Item -LiteralPath $packagePath
