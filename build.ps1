# Builds ControllerHome and Controller Media with the C# compiler that ships with Windows (.NET Framework 4.x).
# Usage:   .\build.ps1            build both (fails if an app is running)
#          .\build.ps1 -Restart   stop the running apps, build, and start them again
param([switch]$Restart)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc (.NET Framework 4.x is required)." }

$apps = @(
  @{ Name = 'ControllerHome'; Libs = @('SDL3.dll'); Refs = @() },
  @{ Name = 'ControllerMedia'; Libs = @('SDL3.dll', 'WebView2Loader.dll', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll')
     Refs = @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll') }
)

foreach ($app in $apps) {
  $dir = Join-Path $root $app.Name
  $missing = $app.Libs | Where-Object { -not (Test-Path (Join-Path $dir $_)) }
  if ($missing) { throw "$($app.Name): missing $($missing -join ', ') in $dir. See 'Libraries' in README.md." }

  $running = Get-Process $app.Name -ErrorAction SilentlyContinue
  # A copy started as administrator (install-admin.ps1) runs from Program Files and can't be stopped from here.
  # It doesn't lock the files in this folder, so build anyway and say how to update it.
  $adminCopy = $running -and -not ($running | Select-Object -First 1).Path
  if ($adminCopy) { $running = $null }
  if ($running -and -not $Restart) { throw "$($app.Name) is running. Close it, or run .\build.ps1 -Restart." }

  $out = Join-Path $dir "$($app.Name).new.exe"
  $cscArgs = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', "/out:$out")
  foreach ($r in $app.Refs) { $cscArgs += "/r:$(Join-Path $dir $r)" }
  if ($app.Name -eq 'ControllerMedia') {
    $cscArgs += "/r:$(Join-Path $fw 'System.Web.Extensions.dll')", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll'
    # App icon: shown by Explorer (/win32icon) and used for the window (/resource). Made by assets\make-icon.ps1.
    $ico = Join-Path $root 'assets\app.ico'
    if (Test-Path $ico) { $cscArgs += "/win32icon:$ico", "/resource:$ico,app.ico" }
  }
  $cscArgs += (Join-Path $dir "src\$($app.Name).cs")

  & $csc @cscArgs
  if ($LASTEXITCODE -ne 0) { throw "$($app.Name): build failed." }

  if ($running) { $running | Stop-Process -Force; Start-Sleep -Seconds 2 }
  Move-Item $out (Join-Path $dir "$($app.Name).exe") -Force
  Write-Host "Built $($app.Name)"
  # Restart through Explorer, so the program belongs to the desktop and not to whatever ran this script
  # (a program started directly from a script's shell can be closed again when that shell goes away).
  if ($running) { Start-Process explorer.exe -ArgumentList "`"$(Join-Path $dir "$($app.Name).exe")`""; Write-Host "Restarted $($app.Name)" }
  if ($adminCopy) { Write-Host "$($app.Name) is running as administrator from Program Files; run install-admin.ps1 to update that copy." }
}
