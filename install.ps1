# Copia dist\SshOpen\SshOpen.dll a la carpeta de plugins de Notepad++.
# Notepad++ debe estar cerrado (bloquea las DLL cargadas). Pide elevación si hace falta.
param([string]$NppDir = "$env:ProgramFiles\Notepad++")
$ErrorActionPreference = 'Stop'
$NppDir = $NppDir.TrimEnd('\')   # una barra final escaparía la comilla al relanzar elevado

$dll = Join-Path $PSScriptRoot 'dist\SshOpen\SshOpen.dll'
if (-not (Test-Path $dll)) { throw "No existe $dll. Ejecuta antes build.ps1" }
if (Get-Process notepad++ -ErrorAction SilentlyContinue) { throw "Cierra Notepad++ antes de instalar el plugin" }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $elevated = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-NppDir', "`"$NppDir`"")
    if ($elevated.ExitCode -ne 0) { throw "La instalación con permisos de administrador ha fallado (código $($elevated.ExitCode))" }
    Write-Host "Instalado en $(Join-Path $NppDir 'plugins\SshOpen')"
    return
}

$target = Join-Path $NppDir 'plugins\SshOpen'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll $target -Force
Write-Host "Instalado en $target"

# Nombre anterior del plugin.
$legacy = Join-Path $NppDir 'plugins\NppSshOpen'
if (Test-Path $legacy) {
    Remove-Item $legacy -Recurse -Force
    Write-Host "Eliminada la versión anterior en $legacy"
}
