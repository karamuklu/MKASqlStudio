param(
    [string]$Package = ""
)

$ErrorActionPreference = "Stop"
$installPath = Join-Path $env:LOCALAPPDATA "MkaSqlStudio"
$packagePath = if ($Package) { (Resolve-Path $Package).Path } else { Join-Path $PSScriptRoot "WindowsMssqlStudio-release.zip" }

if (-not (Test-Path $packagePath)) {
    throw "Kurulum paketi bulunamadı: $packagePath"
}

New-Item -ItemType Directory -Force -Path $installPath | Out-Null
$extractPath = Join-Path $env:TEMP ("MkaSqlStudioSetup-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $extractPath | Out-Null
Expand-Archive -LiteralPath $packagePath -DestinationPath $extractPath -Force
Copy-Item -Path (Join-Path $extractPath "*") -Destination $installPath -Recurse -Force

$shortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "MKA SQL Studio.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installPath "WindowsMssqlStudio.exe"
$shortcut.WorkingDirectory = $installPath
$shortcut.Description = "MKA SQL Studio"
$shortcut.Save()

Start-Process -FilePath (Join-Path $installPath "WindowsMssqlStudio.exe") -WorkingDirectory $installPath
