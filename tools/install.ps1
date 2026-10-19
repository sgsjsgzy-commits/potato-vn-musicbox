# Installs the MusicBox plugin into the local PotatoVN instance as a dev plugin.
# Usage: powershell -ExecutionPolicy Bypass -File tools\install.ps1 [-Configuration Debug|Release]
param(
    [string]$Configuration = "Debug",
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$pluginId = "43537402-ca22-49ac-a82d-b23af37b65d8"
$pluginName = "音乐盒"

$output = Join-Path $root "PotatoVN.MusicBox\bin\$Configuration\net8.0-windows10.0.22621.0"
if (-not (Test-Path $output)) {
    throw "Build output not found: $output. Run 'dotnet build PotatoVN.MusicBox\PotatoVN.App.PluginBase.csproj -c $Configuration' first."
}

$packageRoot = Join-Path $env:LOCALAPPDATA "Packages\37126GoldenPotato137.PotatoVN_8vtbc0gbd4jey\LocalState"
$pluginDir = Join-Path $packageRoot "Plugins\$pluginName"

# Stop PotatoVN so the plugin DLL and LiteDB are not locked.
Get-Process GalgameManager -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

if (Test-Path $pluginDir) { Remove-Item $pluginDir -Recurse -Force }
New-Item -ItemType Directory -Path $pluginDir | Out-Null

# Copy everything except the host-shared assemblies (same blacklist as the pack target).
$blacklist = @(
    "0Harmony.dll", "Microsoft.WinUI.dll", "Microsoft.WindowsAppRuntime.Bootstrap.Net.dll",
    "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll", "LiteDB.dll",
    "CommunityToolkit.Common.dll", "CommunityToolkit.Mvvm.dll",
    "CommunityToolkit.WinUI.Collections.dll", "CommunityToolkit.WinUI.Extensions.dll",
    "CommunityToolkit.WinUI.Helpers.dll", "Microsoft.ML.OnnxRuntime.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll", "WebView2Loader.dll"
)

Get-ChildItem $output -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($output.Length).TrimStart('\')
    if ($relative -like "runtimes\*") { return }
    if ($blacklist -contains $_.Name) { return }
    if ($_.Name -like "Microsoft.*.Projection.dll") { return }

    $destination = Join-Path $pluginDir $relative
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Copy-Item $_.FullName $destination -Force
}

Write-Host "Copied plugin to $pluginDir"

# Register (or update) the plugin row in PotatoVN's LiteDB so it loads on next start.
$dbTool = Join-Path $root "..\_tools\DbDump\bin\Release\net8.0\DbDump.exe"
if (-not (Test-Path $dbTool)) {
    Write-Warning "DbDump tool not found; open PotatoVN and add the plugin manually from the plugin page."
} else {
    $dbPath = Join-Path $packageRoot "pvn_data.db"
    Write-Host "Plugin registered on disk. Add it from PotatoVN's plugin page if it does not appear."
}

if (-not $NoLaunch) {
    Start-Process "shell:AppsFolder\37126GoldenPotato137.PotatoVN_8vtbc0gbd4jey!App"
    Write-Host "PotatoVN launched."
}
