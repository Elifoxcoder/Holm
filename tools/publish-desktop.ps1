<#
.SYNOPSIS
  Publishes HolyNotch for Windows and Linux on HolyNotch's own update server, where every running HolyNotch looks first.

.DESCRIPTION
  Reads the version from Holm.App/Holm.App.csproj, runs the tests (unless -SkipTests), builds both systems
  self-contained and single-file, checks the exe's file version and product name (as the updater does), packs
    HolyNotch-<v>-win-x64.zip     (HolyNotch.exe, LICENSE, README.txt: what the updater installs)
    HolyNotch-<v>-linux-x64.tar.gz (HolyNotch, LICENSE, README.txt)
    <v>/HolyNotch.exe             (for the download page)
  and uploads them to /var/www/holynotch-desktop on the server. latest.json goes last, so an app never sees a version
  whose files aren't there yet:
    {"version","file","size","sha256","linux":{"file","size","sha256"},"exe","notes"}
  nginx serves the folder at https://<HolyNotch server>/desktop/ (server/deploy/nginx-holynotch.conf).
  GitHub releases (holynotch-release.yml) are only a mirror now; nothing here waits for GitHub.

.EXAMPLE
  ./tools/publish-desktop.ps1 -Notes "Own update server, Teams can be muted"
#>
param(
    [string]$Notes = "",
    [switch]$SkipTests,
    [string]$Server = "root@62.83.85.236",
    [string]$Folder = "/var/www/holynotch-desktop"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$csproj = Get-Content "Holm.App/Holm.App.csproj" -Raw
if ($csproj -notmatch '<Version>(\d+\.\d+\.\d+)</Version>') { throw "No x.y.z Version in Holm.App.csproj" }
$version = $Matches[1]
Write-Host "HolyNotch $version"

if (-not $SkipTests) {
    dotnet test Holm.Tests -c Release -m:1 --filter "Category!=Local&Category!=Online&Category!=Manual"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: nothing was published." }
}

$out = Join-Path $root "out/publish-desktop"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
foreach ($rid in "win-x64", "linux-x64") {
    dotnet publish Holm.App -c Release -r $rid --self-contained -m:1 -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:DebugType=none -o "$out/$rid"
    if ($LASTEXITCODE -ne 0) { throw "The $rid build failed: nothing was published." }
}

# The updater installs only an exe whose file version is exactly x.y.z.0 and whose product name is HolyNotch.
$exe = (Resolve-Path "$out/win-x64/Holm.App.exe").Path
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$file = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
if ($file -ne "$version.0") { throw "The exe's file version is $file, not $version.0: the updater would refuse it." }
if ($info.ProductName -ne "HolyNotch") { throw "The exe's product name is '$($info.ProductName)', not 'HolyNotch'." }
if (-not (Test-Path "$out/linux-x64/Holm.App")) { throw "The Linux build has no Holm.App binary." }

$about = "HolyNotch is a fork of Holm by ademerci (MIT License). Downloads: https://sites.elifox.ch/holynotch"
$pkg = Join-Path $out "pkg"
$win = Join-Path $pkg "win"; $linux = Join-Path $pkg "linux"
New-Item -ItemType Directory -Force $win, $linux | Out-Null
Copy-Item $exe "$win/HolyNotch.exe"
Copy-Item LICENSE "$win/LICENSE"
Set-Content "$win/README.txt" "HolyNotch $version for Windows x64`r`n`r`nRun HolyNotch.exe. No .NET installation required.`r`n$about" -NoNewline
Copy-Item "$out/linux-x64/Holm.App" "$linux/HolyNotch"
Copy-Item LICENSE "$linux/LICENSE"
Set-Content "$linux/README.txt" "HolyNotch $version for Linux x64`n`nRun ./HolyNotch. No .NET installation required.`n$about" -NoNewline
$zip = Join-Path $out "HolyNotch-$version-win-x64.zip"
Compress-Archive -Path "$win/*" -DestinationPath $zip
$zipSize = (Get-Item $zip).Length
$zipSha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()

# Upload into a staging folder, pack the Linux tarball there (with the executable bit), then move into place.
$stage = "$Folder/.stage-$version-$([guid]::NewGuid().ToString('N'))"
ssh -o BatchMode=yes $Server "mkdir -p '$stage/linux' && chmod 755 '$Folder' 2>/dev/null; true"
if ($LASTEXITCODE -ne 0) { throw "Couldn't reach $Server." }
scp -q -o BatchMode=yes $zip "$win/HolyNotch.exe" "${Server}:$stage/"
scp -q -o BatchMode=yes "$linux/HolyNotch" "$linux/LICENSE" "$linux/README.txt" "${Server}:$stage/linux/"
if ($LASTEXITCODE -ne 0) { throw "Upload failed: nothing was published." }
$tar = "HolyNotch-$version-linux-x64.tar.gz"
$remote = @"
set -e
cd '$stage'
[ "`$(sha256sum HolyNotch-$version-win-x64.zip | cut -d' ' -f1)" = '$zipSha' ] || { echo 'zip changed in transit'; exit 1; }
chmod 755 linux/HolyNotch
tar -czf '$tar' -C linux .
mkdir -p '$Folder/$version'
mv HolyNotch.exe '$Folder/$version/HolyNotch.exe'
mv HolyNotch-$version-win-x64.zip '$tar' '$Folder/'
cd '$Folder' && rm -rf '$stage'
stat -c %s '$tar'; sha256sum '$tar' | cut -d' ' -f1
"@
# As a file, not piped: Windows PowerShell puts a byte-order mark in front of text it pipes to a program.
$script = Join-Path $out "pack.sh"
[IO.File]::WriteAllText($script, ($remote -replace "`r", ""), [Text.UTF8Encoding]::new($false))
scp -q -o BatchMode=yes $script "${Server}:$stage/../.pack-$version.sh"
$answer = @(ssh -o BatchMode=yes $Server "bash '$Folder/.pack-$version.sh'; s=`$?; rm -f '$Folder/.pack-$version.sh'; exit `$s")
if ($LASTEXITCODE -ne 0 -or $answer.Count -lt 2) { throw "Packing on the server failed: latest.json was not changed." }
$answer = $answer[-2..-1]
$tarSize = [long]$answer[0]; $tarSha = $answer[1].Trim()

$latest = [ordered]@{
    version = $version
    file = "HolyNotch-$version-win-x64.zip"; size = $zipSize; sha256 = $zipSha
    linux = [ordered]@{ file = $tar; size = $tarSize; sha256 = $tarSha }
    exe = "$version/HolyNotch.exe"
    notes = $Notes
} | ConvertTo-Json -Compress
$json = Join-Path $out "latest.json"
[IO.File]::WriteAllText($json, $latest, [Text.UTF8Encoding]::new($false))
scp -q -o BatchMode=yes $json "${Server}:$Folder/latest.json.new"
ssh -o BatchMode=yes $Server "mv '$Folder/latest.json.new' '$Folder/latest.json'"
if ($LASTEXITCODE -ne 0) { throw "Couldn't put latest.json in place." }
Write-Host "Published HolyNotch $version on the update server: $($zip | Split-Path -Leaf) ($zipSha), $tar ($tarSha)"
