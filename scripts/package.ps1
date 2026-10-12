<#
.SYNOPSIS
    Builds the plugin and creates the release zips in .\dist.

.DESCRIPTION
    Produces two archives, both meant to be extracted straight into the Silver Pines game folder:
      SilverPines-SaveAnywhere-v<version>.zip                 - the plugin only (BepInEx already installed)
      SilverPines-SaveAnywhere-v<version>-with-BepInEx.zip    - plugin + BepInEx 6 IL2CPP, all-in-one

.EXAMPLE
    .\scripts\package.ps1
    .\scripts\package.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Silver Pines"
#>
param(
    [string]$GameDir,
    [string]$BepInExUrl = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ZipFile.CreateFromDirectory on Windows PowerShell 5.1 writes '\' into entry names, which breaks
# extraction on Linux/Steam Deck. Build the archive by hand with '/' separators instead.
function New-ZipFromDirectory([string]$Source, [string]$Destination) {
    $Source = (Resolve-Path $Source).Path.TrimEnd('\')
    $zip = [IO.Compression.ZipFile]::Open($Destination, 'Create')
    try {
        foreach ($file in Get-ChildItem $Source -Recurse -File -Force) {
            $entry = $file.FullName.Substring($Source.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, 'Optimal') | Out-Null
        }
    }
    finally { $zip.Dispose() }
}

$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SilverPines.SaveAnywhere\SilverPines.SaveAnywhere.csproj'
$dist    = Join-Path $root 'dist'

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Could not read <Version> from the csproj.' }

$buildArgs = @('build', $project, '-c', 'Release', '-nologo', '-p:DeployToGame=false')
if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$dll = Join-Path $root 'src\SilverPines.SaveAnywhere\bin\Release\net6.0\SilverPines.SaveAnywhere.dll'

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$staging = Join-Path $dist 'staging'
$pluginDir = Join-Path $staging 'BepInEx\plugins\SilverPines.SaveAnywhere'
New-Item -ItemType Directory -Force $pluginDir | Out-Null
Copy-Item $dll $pluginDir
Copy-Item (Join-Path $root 'README.md') $pluginDir
Copy-Item (Join-Path $root 'LICENSE') $pluginDir

$pluginZip = Join-Path $dist "SilverPines-SaveAnywhere-v$version.zip"
New-ZipFromDirectory $staging $pluginZip

# All-in-one: BepInEx first, then the plugin on top.
$bepZip = Join-Path $dist 'bepinex.zip'
Invoke-WebRequest -Uri $BepInExUrl -OutFile $bepZip -UseBasicParsing
$aio = Join-Path $dist 'aio'
[IO.Compression.ZipFile]::ExtractToDirectory($bepZip, $aio)
Copy-Item (Join-Path $staging 'BepInEx') $aio -Recurse -Force
$aioZip = Join-Path $dist "SilverPines-SaveAnywhere-v$version-with-BepInEx.zip"
New-ZipFromDirectory $aio $aioZip

Remove-Item $staging, $aio, $bepZip -Recurse -Force
Get-ChildItem $dist | Select-Object Name, Length
