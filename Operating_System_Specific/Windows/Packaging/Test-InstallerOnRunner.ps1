param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
# This changes the runner's installation registry. Never run on a working PC.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer smoke checks require a disposable GitHub-hosted runner'
}
$runnerRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
$destination = Join-Path $runnerRoot ('BatCave-install-' + [Guid]::NewGuid().ToString('N'))
$uninstallRegistry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7132C2B3-A038-4A5A-B164-5CF1395B9BD3}_is1'
$profile = Join-Path $env:LOCALAPPDATA 'BatCave\SecurePreview'
if ((Test-Path $uninstallRegistry) -or (Test-Path $profile) -or (Test-Path $destination)) { throw 'Refusing to change existing BatCave state' }
$installArgs = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $destination + '"'))
$installed = Start-Process -FilePath $Installer -ArgumentList $installArgs -WindowStyle Hidden -PassThru -Wait
if ($installed.ExitCode -ne 0) { throw ('Install failed: ' + $installed.ExitCode) }
$resolved = (Resolve-Path -LiteralPath $destination).Path
if (-not $resolved.StartsWith($runnerRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected installed directory' }
$executable = Join-Path $resolved 'BatCave.WindowsAgent.exe'
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.ArgumentList.Add('--verify-install'); $start.UseShellExecute = $false
$start.CreateNoWindow = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
$check = [Diagnostics.Process]::Start($start)
if (-not $check.WaitForExit(10000)) { $check.Kill(); throw 'Runtime check timed out' }
$result = $check.StandardOutput.ReadToEnd()
if ($check.ExitCode -ne 0 -or $result -notlike '*BATCAVE_INSTALL_OK runtime=10.*') { throw 'Installed runtime did not pass' }
$check.Dispose()
if (Test-Path $profile) { throw 'Silent installation or verification unexpectedly created a user profile' }
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BatCave Desktop\BatCave - Start fresh.lnk'
if (-not (Test-Path $shortcut)) { throw 'Fresh setup shortcut missing' }
New-Item -ItemType Directory -Path $profile | Out-Null
$marker = Join-Path $profile 'installation-test-marker'
Set-Content -LiteralPath $marker -Value 'retain synthetic setup' -Encoding ascii
$removed = Start-Process -FilePath (Join-Path $resolved 'unins000.exe') -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -PassThru -Wait
if ($removed.ExitCode -ne 0 -or (Test-Path $executable) -or (Test-Path $uninstallRegistry) -or (Test-Path $shortcut)) { throw 'Uninstall did not cleanly remove the app' }
if ((Get-Content $marker -Raw).Trim() -ne 'retain synthetic setup') { throw 'Uninstall did not preserve saved setup' }
Write-Output 'PASS: exact installer installs, self-checks and uninstalls on the disposable runner; profile is not created by install and existing state is preserved by uninstall.'
