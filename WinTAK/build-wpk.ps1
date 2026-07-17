<#
  build-wpk.ps1 - Package ICU VideoStreamer as a WinTAK .wpk file.

  WPK layout expected by WinTAK 5.6+:
      manifest.xml                                      plugin metadata
      icon.png                                          plugin manager icon
      plugin/x64/ICUVideoStreamer/ICUVideoStreamer.dll  the plugin DLL
      plugin/x64/ICUVideoStreamer/ffmpeg/ffmpeg.exe     bundled FFmpeg (runtime dep)

  Invoked by the Release MSBuild target, or run manually.
#>
param(
    [string]$Version     = "1.0",
    [string]$SdkVersion  = "5.6.0.151",
    [Parameter(Mandatory=$true)][string]$DllPath,
    [string]$FfmpegPath  = "",
    [string]$IconPath    = "",
    [Parameter(Mandatory=$true)][string]$ManifestPath,
    [Parameter(Mandatory=$true)][string]$OutPath
)

$ErrorActionPreference = "Stop"
$PluginName = "ICUVideoStreamer"

if (-not (Test-Path $DllPath))      { throw "DLL not found: $DllPath" }
if (-not (Test-Path $ManifestPath)) { throw "MANIFEST.xml not found: $ManifestPath" }

$outDir = Split-Path -Parent $OutPath
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
if (Test-Path $OutPath) { Remove-Item $OutPath -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($OutPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    function Add-Entry($src, $arc) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $src, $arc, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        Write-Host ("[build-wpk]  + " + $arc)
    }

    Add-Entry $ManifestPath "manifest.xml"
    Add-Entry $DllPath      "plugin/x64/$PluginName/$PluginName.dll"

    if ($FfmpegPath -and (Test-Path $FfmpegPath)) {
        Add-Entry $FfmpegPath "plugin/x64/$PluginName/ffmpeg/ffmpeg.exe"
    } else {
        Write-Host ("[build-wpk]  WARNING: ffmpeg.exe not found (" + $FfmpegPath + ")")
    }

    if ($IconPath -and (Test-Path $IconPath)) {
        Add-Entry $IconPath "icon.png"
    }
}
finally {
    $zip.Dispose()
}

$sizeMb = [math]::Round((Get-Item $OutPath).Length / 1MB, 1)
Write-Host ("[build-wpk] Created: " + $OutPath + "  (" + $sizeMb + " MB)")
