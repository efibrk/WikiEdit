<#
.SYNOPSIS
    Builds the distribution package for the WikiEdits application.

.DESCRIPTION
    Publishes the application for .NET Framework 4.8, which is part of Windows 10 1903+
    and Windows 11, so nothing has to be installed on the target machine. The result is
    the dist\ folder (executable plus referenced libraries, about 2.3 MB) together with a
    readme for end users, and a .zip of that folder ready to share.

.PARAMETER Version
    Version stamped into the executable and used in the zip file name. Defaults to 1.0.0.

.PARAMETER NoZip
    Produce the dist\ folder only, without packing a .zip.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Version 1.1.0

.NOTES
    This file must stay saved as UTF-8 WITH a BOM. Windows PowerShell 5.1 reads a
    script without a BOM using the ANSI code page, which corrupts the Hebrew
    literals below (the readme file would end up with a mojibake name).
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"

$root    = $PSScriptRoot
$project = Join-Path $root "WikiEdits\WikiEdits.csproj"
$staging = Join-Path $root "artifacts\publish"
$dist    = Join-Path $root "dist"

Write-Host "Publishing WikiEdits $Version ..." -ForegroundColor Cyan

# A running instance locks the executable and makes the copy below fail
Get-Process -Name WikiEdits -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "  closing a running instance (pid $($_.Id))"
    $_.Kill(); $_.WaitForExit(5000) | Out-Null
}

foreach ($dir in @($staging, $dist)) {
    if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
}

dotnet publish $project -c Release -p:PublishProfile=Dist -p:Version=$Version -o $staging --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $staging "WikiEdits.exe"
if (-not (Test-Path $exe)) { throw "WikiEdits.exe was not produced in $staging" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Everything the application needs at run time. Left out:
#   *.pdb / *.xml                     debug symbols and IntelliSense docs
#   Microsoft.Web.WebView2.WinForms   the Windows Forms control; this application uses the WPF one
#   runtimes\                         the NuGet copy of WebView2Loader.dll; the build already places
#                                     the matching one next to the executable, which is where it is loaded from
Get-ChildItem $staging -Recurse -File |
    Where-Object {
        $_.Extension -notin ".pdb", ".xml" -and
        $_.Name -ne "Microsoft.Web.WebView2.WinForms.dll" -and
        $_.FullName -notlike "$staging\runtimes\*"
    } |
    ForEach-Object {
        $target = Join-Path $dist $_.FullName.Substring($staging.Length).TrimStart('\')
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item $_.FullName $target
    }

# Written with a UTF-8 BOM so the Hebrew text opens correctly in Notepad on any Windows version
$readme = Get-Content (Join-Path $root "DIST-README.txt") -Raw -Encoding UTF8
[System.IO.File]::WriteAllText((Join-Path $dist "קרא אותי.txt"), $readme, (New-Object System.Text.UTF8Encoding $true))

$files = Get-ChildItem $dist -Recurse -File
$sizeMb = [Math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB, 2)
Write-Host ("  dist\  ({0} files, {1} MB)" -f $files.Count, $sizeMb) -ForegroundColor Green

if (-not $NoZip) {
    $zip = Join-Path $root "WikiEdits-$Version-win.zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $dist "*") -DestinationPath $zip
    $zipMb = [Math]::Round((Get-Item $zip).Length / 1MB, 2)
    Write-Host ("  {0}  ({1} MB)" -f (Split-Path $zip -Leaf), $zipMb) -ForegroundColor Green
}

Write-Host "Done." -ForegroundColor Cyan
