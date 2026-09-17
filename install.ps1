<#
.SYNOPSIS
    Installs (or uninstalls) Taskbar Tool for the current user.

.DESCRIPTION
    Copies the published single-file executable to %LOCALAPPDATA%\Programs\Taskbar Tool and adds a
    Start Menu shortcut. Per-user by design — no elevation, no HKLM, no Program Files — matching how
    the app's own "Run at startup" toggle writes to HKCU. Nothing here needs admin rights.

    Autostart is deliberately NOT configured by this script: use the tray icon's "Run at startup"
    item, which is the single source of truth for that setting and stays in sync with Task Manager.

.PARAMETER Uninstall
    Removes the installed copy, its Start Menu shortcut, and any autostart entry it left behind.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$AppName      = 'Taskbar Tool'
$ExeName      = 'TaskbarTool.exe'
$ProcessName  = 'TaskbarTool'
$InstallDir   = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$InstalledExe = Join-Path $InstallDir $ExeName
$ShortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$RunKeyPath   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Stop-RunningInstance {
    $running = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if (-not $running) { return }

    Write-Host "Stopping the running copy..." -ForegroundColor Yellow
    $running | Stop-Process -Force
    # The tray icon is removed on process exit; give the shell a moment before replacing the file.
    Start-Sleep -Milliseconds 800
}

if ($Uninstall) {
    Write-Host "Uninstalling $AppName..." -ForegroundColor Cyan
    Stop-RunningInstance

    foreach ($valueName in @($AppName, 'TaskbarTool', 'TaskbarMediaWidget')) {
        if ($null -ne (Get-ItemProperty -Path $RunKeyPath -Name $valueName -ErrorAction SilentlyContinue)) {
            Remove-ItemProperty -Path $RunKeyPath -Name $valueName
            Write-Host "  removed autostart entry '$valueName'"
        }
    }

    if (Test-Path $ShortcutPath) {
        Remove-Item $ShortcutPath -Force
        Write-Host "  removed Start Menu shortcut"
    }

    if (Test-Path $InstallDir) {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "  removed $InstallDir"
    }

    Write-Host "Done. Logs in %LOCALAPPDATA%\$AppName were left in place." -ForegroundColor Green
    return
}

# --- install -------------------------------------------------------------------------------------

$SourceExe = Join-Path $PSScriptRoot "dist\$ExeName"
if (-not (Test-Path $SourceExe)) {
    throw "Couldn't find $SourceExe. Build it first with:`n" +
          "  dotnet publish src/TaskbarMediaWidget/TaskbarMediaWidget.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist"
}

$version = (Get-Item $SourceExe).VersionInfo.ProductVersion -replace '\+.*$', ''
Write-Host "Installing $AppName $version..." -ForegroundColor Cyan

Stop-RunningInstance

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $SourceExe $InstalledExe -Force
Write-Host "  installed to $InstalledExe"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($ShortcutPath)
$shortcut.TargetPath       = $InstalledExe
$shortcut.WorkingDirectory = $InstallDir
$shortcut.IconLocation     = $InstalledExe
$shortcut.Description      = 'Now-playing media widget embedded in the Windows 11 taskbar'
$shortcut.Save()
Write-Host "  added Start Menu shortcut"

Start-Process $InstalledExe
Write-Host ""
Write-Host "$AppName $version is running." -ForegroundColor Green
Write-Host "Play something to make the widget appear at the left edge of your taskbar."
Write-Host "Right-click its tray icon for 'Run at startup' and 'Exit'."
