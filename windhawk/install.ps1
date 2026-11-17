#Requires -Version 5.1
<#
    Installs (or removes) the komorebi-taskbar-info Windhawk mod.

    This writes HKLM (Windhawk's mod registry) and copies the compiled DLL into
    Windhawk's engine folder, so it self-elevates. It also restarts explorer so
    the mod is loaded into the taskbar process.
#>

param(
    [switch]$Uninstall,
    [switch]$RestartExplorer
)

$ErrorActionPreference = 'Stop'

$modId   = 'komorebi-taskbar-info'
$version = '1.0.0'
$dllName = "$modId`_$version.dll"
$srcDll  = Join-Path $PSScriptRoot "$modId.dll"
$destDir = Join-Path $env:ProgramData 'Windhawk\Engine\Mods\64'
$destDll = Join-Path $destDir $dllName
$regBase = "HKLM:\SOFTWARE\Windhawk\Engine\Mods\$modId"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Uninstall) { $argList += '-Uninstall' }
    if ($RestartExplorer) { $argList += '-RestartExplorer' }
    Write-Host 'elevating...'
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    return
}

if ($Uninstall) {
    if (Test-Path $regBase) { Remove-Item $regBase -Recurse -Force }
    if (Test-Path $destDll) { Remove-Item $destDll -Force }
    Write-Host 'uninstalled'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    return
}

if (-not (Test-Path $srcDll)) {
    throw "missing $srcDll - run build.ps1 first"
}

if (-not (Test-Path $destDir)) {
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
}
Copy-Item $srcDll $destDll -Force

New-Item -Path $regBase -Force | Out-Null
Set-ItemProperty $regBase -Name 'LibraryFileName' -Value $dllName
Set-ItemProperty $regBase -Name 'Version'         -Value $version
Set-ItemProperty $regBase -Name 'Architecture'    -Value 'x86-64'
Set-ItemProperty $regBase -Name 'Include'         -Value 'explorer.exe'
Set-ItemProperty $regBase -Name 'Disabled'        -Value 0 -Type DWord

$settings = Join-Path $regBase 'Settings'
New-Item -Path $settings -Force | Out-Null

function Set-Str([string]$name, [string]$value) {
    Set-ItemProperty $settings -Name $name -Value $value
}
function Set-Int([string]$name, [int]$value) {
    Set-ItemProperty $settings -Name $name -Value $value -Type DWord
}

Set-Str 'TopLine'    '%date%  %time%'
Set-Str 'BottomLine' '%web1%'
Set-Str 'TooltipLine' '%web2_full%'
Set-Int 'ShowSeconds' 0
Set-Int 'MaxWidth' 360
Set-Int 'DateStyle.FontSize' 15
Set-Int 'TimeStyle.FontSize' 15
Set-Int 'WebContentsUpdateInterval' 1
Set-Str 'WebContentsItems[0].Url' 'http://127.0.0.1:17321/'
Set-Int 'WebContentsItems[0].MaxLength' 90
Set-Str 'WebContentsItems[1].Url' 'http://127.0.0.1:17321/full'
Set-Int 'WebContentsItems[1].MaxLength' 180

Write-Host "installed $modId $version"

if ($RestartExplorer) {
    Write-Host 'restarting explorer...'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
}
