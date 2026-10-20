# Shaker Router: build, install, and relaunch in one step.
#
#   1. Builds the source in this folder into a staging folder.
#   2. Build fails: stops there. The installed version is untouched and keeps running.
#   3. Build succeeds: closes the running app (it saves settings on close),
#      swaps in the new files, makes shortcuts, relaunches.
#
# Installed app:  %LOCALAPPDATA%\ShakerRouter\app\ShakerRouter.exe
# Settings:       %APPDATA%\ShakerRouter\settings.json   (never touched)
# Log:            build-log.txt next to this project

param([switch]$NoLaunch, [switch]$NoPause)

# Native tools (dotnet) write progress to stderr. With 'Stop', Windows PowerShell 5.1
# turns that into a terminating error, so keep 'Continue' and check exit codes instead.
$ErrorActionPreference = 'Continue'

$projectDir = Split-Path -Parent $PSScriptRoot
$root       = Join-Path $env:LOCALAPPDATA 'ShakerRouter'
$stageDir   = Join-Path $root 'staging'
$installDir = Join-Path $root 'app'
$exePath    = Join-Path $installDir 'ShakerRouter.exe'
$logPath    = Join-Path $projectDir 'build-log.txt'

function Log($text) {
    Write-Host $text
    Add-Content -Path $logPath -Value $text
}

function Fail($text) {
    Log ''
    Log "RESULT: FAILED. $text"
    if (-not $NoPause) {
        Write-Host ''
        Write-Host 'Press any key to close.' -ForegroundColor Red
        [void][System.Console]::ReadKey($true)
    }
    exit 1
}

Set-Content -Path $logPath -Value "Shaker Router update $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"

# 1. SDK
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail '.NET SDK not found on PATH. Install it from https://dotnet.microsoft.com/en-us/download/dotnet/10.0 then open a new window.'
}
$sdks = (& dotnet --list-sdks 2>&1 | Out-String).Trim()
Log "== .NET SDKs`n$sdks"
if (-not $sdks) { Fail 'dotnet is installed but no SDK was found. Install the SDK, not just the runtime.' }

# 2. Build into staging. The running app is not touched yet.
Log '== Building'
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
$csproj = Join-Path $projectDir 'ShakerRouter.csproj'
& dotnet publish "$csproj" -c Release -r win-x64 --self-contained false -o "$stageDir" -nologo 2>&1 |
    ForEach-Object { $line = "$_"; Write-Host $line; Add-Content -Path $logPath -Value $line }
if ($LASTEXITCODE -ne 0) { Fail "Build failed (exit $LASTEXITCODE). The installed version was left alone." }

# 3. Close the running app so its files can be replaced
Log '== Closing Shaker Router if it is running'
foreach ($p in @(Get-Process -Name 'ShakerRouter' -ErrorAction SilentlyContinue)) {
    try {
        [void]$p.CloseMainWindow()   # lets it save settings
        if (-not $p.WaitForExit(5000)) { $p.Kill(); [void]$p.WaitForExit(2000) }
    } catch { }
}

# 4. Swap in the new build
Log "== Installing to $installDir"
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
& robocopy "$stageDir" "$installDir" /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "Copying files failed (robocopy $LASTEXITCODE). Is a file locked?" }

# 5. Shortcuts (Start Menu + Desktop)
try {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop'))) {
        $lnk = $shell.CreateShortcut((Join-Path $folder 'Shaker Router.lnk'))
        $lnk.TargetPath = $exePath
        $lnk.WorkingDirectory = $installDir
        $lnk.Description = 'Route game audio to bass shakers'
        $lnk.Save()
    }
} catch {
    Log "Shortcut warning: $($_.Exception.Message)"
}

# 6. Launch
$version = (Get-Item $exePath).VersionInfo.ProductVersion
if (-not $NoLaunch) {
    Log '== Launching'
    Start-Process -FilePath $exePath -WorkingDirectory $installDir
}
Log "RESULT: OK. Installed v$version"
Start-Sleep -Seconds 2
