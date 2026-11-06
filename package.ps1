# Empaqueta dist\SshOpen\SshOpen.dll (generado con build.ps1) para publicarlo:
#   dist\release\SshOpen-<versión>-x64.zip          SshOpen.dll en la raíz + doc\README.md y doc\LICENSE
#   dist\release\SshOpen-<versión>-x64.zip.sha256   huella SHA-256 (el "id" de nppPluginList)
#   dist\release\nppPluginList-entry.json           entrada lista para src/pl.x64.json de nppPluginList
#   dist\release\version.txt                        versión de la DLL (la usa GitHub Actions)
#
# La URL de descarga se construye con el repositorio de GitHub (GITHUB_REPOSITORY en Actions;
# si no, -Repository) y la etiqueta v<versión>.
param(
    [string]$Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'D4vidSP/SshOpen' })
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$dll = Join-Path $PSScriptRoot 'dist\SshOpen\SshOpen.dll'
if (-not (Test-Path $dll)) { throw "No existe $dll; ejecuta antes build.ps1" }

# nppPluginList compara con la versión binaria del recurso (FileVersion fija), no con el texto.
$info = (Get-Item $dll).VersionInfo
$version = '{0}.{1}.{2}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart
if ($info.FilePrivatePart -ne 0) { $version += ".$($info.FilePrivatePart)" }

$out = Join-Path $PSScriptRoot 'dist\release'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

# ZipArchive en vez de Compress-Archive: en PowerShell 5.1 este último guarda las rutas con "\".
$zipName = "SshOpen-$version-x64.zip"
$zipPath = Join-Path $out $zipName
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    $files = [ordered]@{
        'SshOpen.dll'   = $dll
        'doc/README.md' = Join-Path $PSScriptRoot 'README.md'
        'doc/LICENSE'   = Join-Path $PSScriptRoot 'LICENSE'
    }
    foreach ($entry in $files.Keys) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $files[$entry], $entry, 'Optimal') | Out-Null
    }
}
finally {
    $zip.Dispose()
}

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zipPath.sha256", "$hash  $zipName`n")
[IO.File]::WriteAllText((Join-Path $out 'version.txt'), $version)

$entry = [ordered]@{
    'folder-name'             = 'SshOpen'
    'display-name'            = 'SshOpen'
    'version'                 = $version
    'npp-compatible-versions' = '[8.4,]'
    'id'                      = $hash
    'repository'              = "https://github.com/$Repository/releases/download/v$version/$zipName"
    'description'             = 'Open, edit and save files on Linux servers over SSH/SFTP (password or private key, optional sudo). Includes a remote folder browser and a dockable folder tree; files are uploaded back automatically on save.'
    'author'                  = 'D4vidSP'
    'homepage'                = "https://github.com/$Repository"
}
$json = $entry | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $out 'nppPluginList-entry.json'), $json + "`n")

Get-ChildItem $out | Select-Object Name, Length
Write-Host "Version $version, SHA-256 $hash"
