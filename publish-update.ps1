#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the installer, writes update.json and (optionally) publishes both as a GitHub release.

.EXAMPLE
  .\publish-update.ps1 -Notes "Fixed X. Added Y."            # build + create dist\installer\update.json
  .\publish-update.ps1 -Notes "Fixed X." -Upload             # ...and publish the GitHub release
  .\publish-update.ps1 -Upload -SkipBuild                    # re-publish an installer that is already built

.NOTES
  Uploading needs the GitHub CLI (https://cli.github.com) signed in with: gh auth login
  Keep the release repository PUBLIC, otherwise the app cannot download updates without a token.
#>
param(
    [string]$Repo = "sajith1994/WindowsUtilityBySajith",
    [string]$Notes = "",
    [switch]$Upload,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$propsText = Get-Content (Join-Path $root "Directory.Build.props") -Raw
if ($propsText -notmatch "<AppVersion>([^<]+)</AppVersion>") {
    throw "AppVersion was not found in Directory.Build.props."
}
$version = $Matches[1].Trim()
Write-Host "Publishing version $version" -ForegroundColor Cyan

if (-not $SkipBuild) {
    & (Join-Path $root "build-release.ps1") -CreateInstaller
    if ($LASTEXITCODE -ne 0) { throw "build-release.ps1 failed." }
}

$installerName = "WindowsUtilityBySajith-Setup-$version.exe"
$installerPath = Join-Path $root "dist\installer\$installerName"
if (-not (Test-Path $installerPath)) {
    throw "Installer not found: $installerPath"
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $installerPath).Hash.ToUpperInvariant()

if ([string]::IsNullOrWhiteSpace($Notes)) {
    # Fall back to the matching CHANGELOG section (everything under its heading up to the next one).
    $changelog = Get-Content (Join-Path $root "CHANGELOG.md") -Raw
    $pattern = "(?ms)^##\s*\[?$([regex]::Escape($version))\]?[^\r\n]*\r?\n(.*?)(?=^##\s|\z)"
    if ($changelog -match $pattern) { $Notes = $Matches[1].Trim() }
}

$manifest = [ordered]@{
    version  = $version
    url      = "https://github.com/$Repo/releases/download/v$version/$installerName"
    sha256   = $hash
    notes    = $Notes
    released = (Get-Date).ToString("yyyy-MM-dd")
}

$manifestPath = Join-Path $root "dist\installer\update.json"
$json = $manifest | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($manifestPath, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Installer : $installerPath"
Write-Host "SHA-256   : $hash"
Write-Host "Manifest  : $manifestPath" -ForegroundColor Green

if (-not $Upload) {
    Write-Host ""
    Write-Host "Not uploaded. Re-run with -Upload, or create release v$version on GitHub and attach both files." -ForegroundColor Yellow
    return
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) is not installed. Install it from https://cli.github.com and run 'gh auth login'."
}

$notesFile = Join-Path $env:TEMP "wus-release-notes.txt"
[System.IO.File]::WriteAllText($notesFile, $(if ($Notes) { $Notes } else { "Windows Utility by Sajith $version" }), (New-Object System.Text.UTF8Encoding($false)))

& gh release create "v$version" $installerPath $manifestPath --repo $Repo --title "Windows Utility by Sajith $version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }

Write-Host ""
Write-Host "Published. Installed copies will see version $version the next time they check for updates." -ForegroundColor Green
