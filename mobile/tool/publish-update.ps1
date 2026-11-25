<#
.SYNOPSIS
  Puts a release APK of Holm for Android on Holm's update server; every installed Holm offers it within six hours.

.DESCRIPTION
  Reads the version from pubspec.yaml, signs the APK with Holm's release key (tool/sign-release.ps1), verifies that the
  primary signer is the release key, uploads it as holm-<name>-<code>.apk and then latest.json, or beta.json for a test
  version (versionCode, versionName, file, sha256, notes) into /var/www/holm, served by nginx over HTTPS on port 8473.
  Phones that have "Test versions" on get whichever of the two is newer.
  The app pins that server's certificate (lib/update.dart) and checks the APK's SHA-256 before installing it.
  Raise the version (the number after +) for every upload: the app installs only a higher versionCode.
  Build with the same signing key each time, or Android refuses the update.

  -NoSign keeps the existing parameter but no longer bypasses safety: it only verifies an already release-signed APK.
  Unsigned and debug-signed APKs are rejected before anything is uploaded.

.EXAMPLE
  flutter build apk --release
  ./tool/publish-update.ps1 -Notes "Corners, muting apps and updates"

.EXAMPLE
  ./tool/publish-update.ps1 -Channel beta -Notes "Try the new overview"
#>
param(
    [string]$Apk = "build/app/outputs/flutter-apk/app-release.apk",
    [string]$Notes = "",
    [ValidateSet("stable", "beta")] [string]$Channel = "stable",
    [switch]$NoSign,
    [string]$Server = "root@62.83.85.236",
    [string]$Folder = "/var/www/holm"
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot)

if (-not (Test-Path -LiteralPath $Apk)) { throw "No APK at $Apk (flutter build apk --release)" }
$apkPath = (Resolve-Path -LiteralPath $Apk).Path

# An APK without Flutter's assets (fonts, pictures) starts but shows boxes for every icon: never upload one.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($apkPath)
try {
    $names = $zip.Entries | ForEach-Object FullName
    foreach ($must in "assets/flutter_assets/FontManifest.json", "assets/flutter_assets/fonts/MaterialIcons-Regular.otf") {
        if ($names -notcontains $must) { throw "$Apk has no $must (a broken build): run flutter clean, then build again." }
    }
} finally { $zip.Dispose() }

$versionMatch = Select-String -Path pubspec.yaml -Pattern '^version:\s*(\d+\.\d+\.\d+)\+(\d+)' | Select-Object -First 1
if (-not $versionMatch -or $versionMatch.Matches.Count -eq 0) { throw "No version like 1.2.3+4 in pubspec.yaml" }
$line = $versionMatch.Matches[0]
$name = $line.Groups[1].Value
$code = [int]$line.Groups[2].Value

if ($NoSign) {
    & "$PSScriptRoot/sign-release.ps1" $apkPath -VerifyOnly | Out-Host
} else {
    & "$PSScriptRoot/sign-release.ps1" $apkPath | Out-Host
}

$file = "holm-$name-$code.apk"
$index = if ($Channel -eq "beta") { "beta.json" } else { "latest.json" }
$sha = (Get-FileHash -LiteralPath $apkPath -Algorithm SHA256).Hash.ToLowerInvariant()
$json = [ordered]@{ versionCode = $code; versionName = $name; file = $file; sha256 = $sha; notes = $Notes } | ConvertTo-Json -Compress
$tmp = Join-Path ([IO.Path]::GetTempPath()) "holm-$index"
[IO.File]::WriteAllText($tmp, $json, [Text.UTF8Encoding]::new($false))

# The APK first, then the index in one move: a check never sees a version whose file isn't there yet.
scp $apkPath "${Server}:$Folder/$file"
scp $tmp "${Server}:$Folder/$index.new"
ssh $Server "chmod 644 $Folder/$file $Folder/$index.new && mv $Folder/$index.new $Folder/$index"
Remove-Item $tmp
Write-Host "Published Holm $name ($code) as $Channel ($index): $file, sha256 $sha"
