<#
.SYNOPSIS
  Signs a release APK of Holm for Android with Holm's release key, with a signing lineage from the old debug key.

.DESCRIPTION
  Holm 1.0 and 1.1 were signed with a computer's debug key. From 1.2 on, Holm is signed with its own release key; the
  lineage (APK Signature Scheme v3.1 key rotation) tells Android that the new key follows the old one, so phones with
  1.1 accept 1.2 as an update instead of refusing it.

  The key lives outside the repository, in %LOCALAPPDATA%\HolmDev\keys (holm-release.jks, key.properties with its
  password, lineage.bin). Keep a copy of that folder somewhere safe: without the key no update can be published that
  installed apps accept.

  An unsigned app-release.apk is valid input: this script signs it and then verifies that the primary signer is the
  release key. -VerifyOnly performs that verification without re-signing, so an unsigned or debug-signed APK cannot
  be distributed through the publishing workflow.

.EXAMPLE
  ./tool/sign-release.ps1 build/app/outputs/flutter-apk/app-release.apk

.EXAMPLE
  ./tool/sign-release.ps1 build/app/outputs/flutter-apk/app-release.apk -VerifyOnly
#>
param(
    [Parameter(Mandatory)] [string]$Apk,
    [string]$Keys = "$env:LOCALAPPDATA\HolmDev\keys",
    [string]$OldKeystore = "$env:USERPROFILE\.android\debug.keystore",
    [switch]$VerifyOnly
)
$ErrorActionPreference = "Stop"

$apkPath = (Resolve-Path -LiteralPath $Apk).Path
$propertiesPath = Join-Path $Keys "key.properties"
if (-not (Test-Path -LiteralPath $propertiesPath)) { throw "Release key properties are missing: $propertiesPath" }

$props = @{}
foreach ($line in Get-Content -LiteralPath $propertiesPath) {
    if ($line -match '^\s*([^=]+)=(.*)$') { $props[$Matches[1].Trim()] = $Matches[2].Trim() }
}
$storeFile = if ($props.storeFile) { $props.storeFile } else { $props.store }
$password = if ($props.storePassword) { $props.storePassword } elseif ($props.password) { $props.password } else { $null }
$alias = if ($props.keyAlias) { $props.keyAlias } elseif ($props.alias) { $props.alias } else { $null }
$keyPassword = if ($props.keyPassword) { $props.keyPassword } elseif ($props.password) { $props.password } else { $null }
if ([string]::IsNullOrWhiteSpace($storeFile) -or [string]::IsNullOrWhiteSpace($password) -or
    [string]::IsNullOrWhiteSpace($alias) -or [string]::IsNullOrWhiteSpace($keyPassword)) {
    throw "key.properties must contain storeFile, storePassword/password, keyAlias/alias and keyPassword/password."
}
$keystore = if ([IO.Path]::IsPathRooted($storeFile)) { $storeFile } else { Join-Path $Keys $storeFile }
if (-not (Test-Path -LiteralPath $keystore)) { throw "Release keystore is missing: $keystore" }

$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT }
    else { "$env:LOCALAPPDATA\HolmDev\android-sdk" }
$tools = Get-ChildItem "$sdk\build-tools" -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $tools) { throw "No Android build-tools found under $sdk" }
$apksigner = Join-Path $tools.FullName "apksigner.bat"
if (-not (Test-Path -LiteralPath $apksigner)) { throw "apksigner is missing: $apksigner" }

function Invoke-NativeTool([string]$Path, [string[]]$Arguments) {
    # Windows PowerShell 5.1 turns Java warnings on stderr into terminating errors under Stop. Capture them and
    # decide from the real process exit code instead; cmdlet failures in the rest of this script remain fatal.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Path @Arguments 2>&1
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    [pscustomobject]@{ Output = @($output); ExitCode = $code }
}

$keytoolCommand = Get-Command keytool.exe -ErrorAction SilentlyContinue
$keytoolPath = if ($keytoolCommand) { $keytoolCommand.Source } else { $null }
if (-not $keytoolPath -and $env:JAVA_HOME) {
    $candidate = Join-Path $env:JAVA_HOME "bin\keytool.exe"
    if (Test-Path -LiteralPath $candidate) { $keytoolPath = $candidate }
}
if (-not $keytoolPath) { throw "keytool.exe is required to verify the release signer." }
$keyResult = Invoke-NativeTool $keytoolPath @('-list', '-v', '-keystore', $keystore, '-alias', $alias, '-storepass', $password)
if ($keyResult.ExitCode -ne 0) { throw "Could not read the release certificate from $keystore" }
$certificate = [regex]::Match(($keyResult.Output -join "`n"), '(?m)^\s*SHA256:\s*((?:[0-9a-fA-F]{2}:){31}[0-9a-fA-F]{2}|[0-9a-fA-F]{64})\s*$')
if (-not $certificate.Success) { throw "Could not find the release certificate SHA-256 in keytool output." }
$expectedDigest = $certificate.Groups[1].Value.Replace(':', '').ToLowerInvariant()

function Assert-ReleaseSigner {
    $verified = Invoke-NativeTool $apksigner @('verify', '--print-certs', $apkPath)
    if ($verified.ExitCode -ne 0) {
        throw "APK signature verification failed; unsigned or invalid APKs cannot be published."
    }
    $signer = [regex]::Match(($verified.Output -join "`n"), '(?m)^Signer #1 certificate SHA-256 digest:\s*([0-9a-fA-F]{64})\s*$')
    if (-not $signer.Success) { throw "Could not read the APK's primary signer certificate." }
    $actualDigest = $signer.Groups[1].Value.ToLowerInvariant()
    if ($actualDigest -ne $expectedDigest) {
        throw "APK primary signer $actualDigest is not the Holm release key $expectedDigest; unsigned/debug-signed APKs cannot be published."
    }
    Write-Host "Verified Holm release signer SHA-256: $actualDigest"
}

if ($VerifyOnly) {
    Assert-ReleaseSigner
    return
}

if (-not (Test-Path -LiteralPath $OldKeystore)) { throw "The old debug keystore is required for signing lineage: $OldKeystore" }
$lineage = Join-Path $Keys "lineage.bin"

# Once: the lineage from the debug key to the release key.
if (-not (Test-Path -LiteralPath $lineage)) {
    $rotated = Invoke-NativeTool $apksigner @(
        'rotate', '--out', $lineage,
        '--old-signer', '--ks', $OldKeystore, '--ks-key-alias', 'androiddebugkey', '--ks-pass', 'pass:android', '--key-pass', 'pass:android',
        '--new-signer', '--ks', $keystore, '--ks-key-alias', $alias, '--ks-pass', "pass:$password", '--key-pass', "pass:$keyPassword"
    )
    if ($rotated.ExitCode -ne 0) { $rotated.Output | ForEach-Object { Write-Host $_ }; throw "apksigner rotate failed" }
}

$signed = Invoke-NativeTool $apksigner @(
    'sign',
    '--ks', $OldKeystore, '--ks-key-alias', 'androiddebugkey', '--ks-pass', 'pass:android', '--key-pass', 'pass:android',
    '--next-signer', '--ks', $keystore, '--ks-key-alias', $alias, '--ks-pass', "pass:$password", '--key-pass', "pass:$keyPassword",
    '--lineage', $lineage, $apkPath
)
if ($signed.ExitCode -ne 0) { $signed.Output | ForEach-Object { Write-Host $_ }; throw "apksigner sign failed" }
Assert-ReleaseSigner
