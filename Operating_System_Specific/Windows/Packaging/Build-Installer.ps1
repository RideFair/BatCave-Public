param(
    [string]$DotNet = 'dotnet',
    [Parameter(Mandatory=$true)][string]$InnoCompiler,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository '.build\installer\packages' }
# A fresh directory prevents stale symbols or local files entering a package.
$publishDirectory = Join-Path $repository ('.build\installer\win-x64-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $repository 'Operating_System_Specific\Windows\BatCaveWindowsAgent'
[xml]$projectXml = Get-Content -LiteralPath (Join-Path $project 'BatCaveWindowsAgent.csproj')
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw 'Invalid package version' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
foreach ($test in @('BridgeRegressionTests', 'SecureTransportTests', 'ModifierRegressionTests', 'TopologyRegressionTests')) {
    & $DotNet run --project (Join-Path $repository ('Operating_System_Specific\Windows\' + $test))
    if ($LASTEXITCODE -ne 0) { throw ('Regression checks failed: ' + $test) }
}
& $DotNet publish $project -c Release -r win-x64 --self-contained true -p:OutputType=WinExe -p:DefineConstants=SECURE_DESKTOP_ONLY -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& $InnoCompiler ('/DPublishDirectory=' + $publishDirectory) ('/DPackageDirectory=' + $OutputDirectory) ('/DAppVersion=' + $version) (Join-Path $PSScriptRoot 'BatCave.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$installer = Get-Item (Join-Path $OutputDirectory ('BatCave-' + $version + '-Windows-x64-Setup.exe'))
$checksum = (Get-FileHash $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($installer.FullName + '.sha256') -Value ($checksum + '  ' + $installer.Name) -Encoding ascii
$sourceCommit = (& git -C $repository rev-parse HEAD).Trim()
$dirty = [bool](& git -C $repository status --porcelain)
@{ version=$version; sourceCommit=$sourceCommit; sourceDirty=$dirty;
   runtime='self-contained .NET 10'; architecture='win-x64'; minimumWindows='Windows 11';
   sha256=$checksum; signed=$false; macInteropVerified=$false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-manifest.json') -Encoding utf8
Get-Item $installer.FullName
