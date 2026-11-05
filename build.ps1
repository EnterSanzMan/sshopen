# Compila SshOpen.dll (NativeAOT, x64) en .\dist\SshOpen\
# Requiere el SDK de .NET 10 y Visual Studio / Build Tools con "Desarrollo para el escritorio con C++".
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# NativeAOT localiza link.exe con vswhere.exe, que no suele estar en el PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

dotnet publish -c Release -o dist\SshOpen
if ($LASTEXITCODE -ne 0) { throw "La compilación ha fallado" }

Get-ChildItem dist\SshOpen -Exclude SshOpen.dll | Remove-Item -Force
Get-Item dist\SshOpen\SshOpen.dll | Select-Object FullName, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
