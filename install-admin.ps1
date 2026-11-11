# Makes ControllerHome start as administrator when you sign in, so the controller can drive Game Bar and Lenovo's
# Quick Settings inside games that run as administrator (Windows drops key presses from ordinary programs there).
#
#   powershell -ExecutionPolicy Bypass -File .\install-admin.ps1              set up, or update after a rebuild
#   powershell -ExecutionPolicy Bypass -File .\install-admin.ps1 -Uninstall   go back to the ordinary start-up shortcut
#
# What it changes:
#   - copies ControllerHome.exe and SDL3.dll to "C:\Program Files\ControllerHome" (only administrators can change
#     files there, so nothing can swap the program that gets administrator rights)
#   - adds a scheduled task "ControllerHome (administrator)" that starts that copy at sign-in with highest privileges
#   - removes the ControllerHome shortcut from your Startup folder (the task replaces it)
# Windows asks for permission once (the usual Yes/No prompt).
param([switch]$Uninstall, [string]$User, [string]$StartupFolder)
$ErrorActionPreference = 'Stop'

$taskName = 'ControllerHome (administrator)'
$src = Join-Path $PSScriptRoot 'ControllerHome'
$dst = Join-Path $env:ProgramFiles 'ControllerHome'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  # Remember who you are and where your Startup folder is, then run this script again with administrator rights.
  $relaunch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-User', "`"$($identity.Name)`"",
                '-StartupFolder', "`"$([Environment]::GetFolderPath('Startup'))`"")
  if ($Uninstall) { $relaunch += '-Uninstall' }
  Start-Process powershell.exe -Verb RunAs -ArgumentList $relaunch
  return
}
if (-not $User) { $User = $identity.Name }
if (-not $StartupFolder) { $StartupFolder = [Environment]::GetFolderPath('Startup') }
$shortcut = Join-Path $StartupFolder 'ControllerHome.lnk'

try {
  Get-Process ControllerHome -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Seconds 1

  if ($Uninstall) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    $exe = Join-Path $src 'ControllerHome.exe'
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
    $lnk.TargetPath = $exe; $lnk.WorkingDirectory = $src; $lnk.Save()
    Start-Process explorer.exe -ArgumentList "`"$exe`""      # started through Explorer so it runs without administrator rights
    Write-Host 'Removed. ControllerHome starts from your Startup folder again, without administrator rights.'
  }
  else {
    foreach ($f in 'ControllerHome.exe', 'SDL3.dll') { if (-not (Test-Path (Join-Path $src $f))) { throw "Missing $f in $src. Run .\build.ps1 first." } }
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item (Join-Path $src 'ControllerHome.exe'), (Join-Path $src 'SDL3.dll') $dst -Force

    $action = New-ScheduledTaskAction -Execute (Join-Path $dst 'ControllerHome.exe') -WorkingDirectory $dst
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $User
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

    if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 2
    if (Get-Process ControllerHome -ErrorAction SilentlyContinue) { Write-Host "Done. ControllerHome is running as administrator from $dst and will start that way at sign-in." }
    else { Write-Host 'The task was created but ControllerHome did not start. Check Task Scheduler.' }
    Write-Host "Its log is now $dst\log.txt. After rebuilding with build.ps1, run this script again to update that copy."
  }
}
catch { Write-Host "Failed: $($_.Exception.Message)" -ForegroundColor Red }
Read-Host 'Press Enter to close'
