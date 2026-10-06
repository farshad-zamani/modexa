<#
    publish.ps1 - Build the final hardened, obfuscated Modexa.exe (single-file, self-contained).

    Pipeline:
      1) dotnet build (Release)        -> compiles Modexa.App + Modexa.Core (with ExpectedExeSize baked in)
      2) Obfuscar (HideStrings)        -> encrypts string literals in BOTH assemblies (WPF-safe: no renaming)
      3) dotnet publish --no-build     -> bundles the obfuscated assemblies into one self-contained exe
      4) size-stabilize + pad          -> bake the real exe size into SecurityHelper, rebuild, pad to a fixed T
      5) sanity grep                   -> fail if known secret strings are still plaintext in the exe

    The source keeps SecurityHelper.ExpectedExeSize = 0 (check disabled for dev). This script injects the
    real size temporarily and restores 0 at the end.

    Requires the Obfuscar global tool (installed automatically if missing).
    NOTE: adapt this at first real release; it is written to match the Backup Manager's proven approach.
#>

[CmdletBinding()]
param(
    [string]$OutputDir = "$PSScriptRoot\..\dist\Modexa-release",
    [int]$PadMargin = 262144  # 256 KB; must exceed size jitter between builds/obfuscation runs
)

$ErrorActionPreference = "Stop"

$root    = (Resolve-Path "$PSScriptRoot\..").Path
$appProj = Join-Path $root "src\Modexa.App\Modexa.App.csproj"
$secFile = Join-Path $root "src\Modexa.Core\Security\SecurityHelper.cs"
$tfm     = "net8.0-windows"
$rid     = "win-x64"
$exeName = "Modexa.exe"
$appDll  = "Modexa.dll"
$coreDll = "Modexa.Core.dll"
$binDir  = Join-Path $root "src\Modexa.App\bin\Release\$tfm\$rid"
$staging = Join-Path $env:TEMP ("modexa_pub_" + [Guid]::NewGuid().ToString("N").Substring(0,8))
$obfOut  = Join-Path $staging "obf"
$obfXml  = Join-Path $staging "obfuscar.xml"

function Ensure-Obfuscar {
    $tool = Join-Path $env:USERPROFILE ".dotnet\tools\obfuscar.console.exe"
    if (Test-Path $tool) { return $tool }
    Write-Host "==> Installing Obfuscar.GlobalTool..." -ForegroundColor Yellow
    dotnet tool install --global Obfuscar.GlobalTool | Out-Null
    if (-not (Test-Path $tool)) { throw "Failed to install Obfuscar.GlobalTool." }
    return $tool
}

function Write-ObfuscarConfig {
    if (-not (Test-Path $staging)) { New-Item -ItemType Directory -Force -Path $staging | Out-Null }
    $lines = @(
        "<?xml version='1.0'?>",
        "<Obfuscator>",
        ("  <Var name=""InPath"" value=""" + $binDir + """ />"),
        ("  <Var name=""OutPath"" value=""" + $obfOut + """ />"),
        "  <Var name=""KeepPublicApi"" value=""true"" />",
        "  <Var name=""HidePrivateApi"" value=""false"" />",
        "  <Var name=""HideStrings"" value=""true"" />",
        "  <Var name=""Optimize"" value=""false"" />",
        "  <Var name=""SuppressIldasm"" value=""true"" />",
        "  <Module file=""`$(InPath)\$appDll"" />",
        "  <Module file=""`$(InPath)\$coreDll"" />",
        "</Obfuscator>"
    )
    Set-Content -Path $obfXml -Value $lines -Encoding UTF8
}

function Set-ExpectedExeSize([long]$value) {
    $c  = Get-Content $secFile -Raw -Encoding UTF8
    $c2 = [regex]::Replace($c, 'private const long ExpectedExeSize = \d+;', "private const long ExpectedExeSize = $value;")
    Set-Content $secFile -Value $c2 -Encoding UTF8 -NoNewline
}

# Inject (or clear) the obfuscated FSLM API key. The key lives OUTSIDE source control:
# secret\fslm-key.txt or the MODEXA_FSLM_KEY env var. Source always keeps the empty placeholder.
$secretsFile = Join-Path $root "src\Modexa.Core\Licensing\Secrets.cs"
function Set-FslmKey([string]$b64) {
    $c  = Get-Content $secretsFile -Raw -Encoding UTF8
    $c2 = [regex]::Replace($c, 'private const string EncryptedApiKey = "[^"]*"; /\*AUTO_FSLM_KEY\*/',
                           "private const string EncryptedApiKey = ""$b64""; /*AUTO_FSLM_KEY*/")
    Set-Content $secretsFile -Value $c2 -Encoding UTF8 -NoNewline
}

function Invoke-Build {
    dotnet build $appProj -c Release -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}

function Invoke-ObfuscateAndPublish {
    param([string]$tool)
    & $tool $obfXml
    if ($LASTEXITCODE -ne 0) { throw "Obfuscar failed." }
    # Copy obfuscated assemblies back over the build output, then publish without rebuilding.
    Copy-Item (Join-Path $obfOut $appDll)  (Join-Path $binDir $appDll)  -Force
    Copy-Item (Join-Path $obfOut $coreDll) (Join-Path $binDir $coreDll) -Force
    dotnet publish $appProj -c Release --no-build -p:PublishSingleFile=true -o $OutputDir -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

try {
    $tool = Ensure-Obfuscar
    Write-ObfuscarConfig

    # Inject the FSLM key for this build (kept out of source control).
    $keyFile = Join-Path $root "secret\fslm-key.txt"
    $fslm = if ($env:MODEXA_FSLM_KEY) { $env:MODEXA_FSLM_KEY } elseif (Test-Path $keyFile) { (Get-Content $keyFile -Raw).Trim() } else { "" }
    if ($fslm) { Set-FslmKey $fslm; Write-Host "==> FSLM key injected for this build" -ForegroundColor DarkGray }
    else { Write-Host "==> WARNING: no FSLM key (secret\fslm-key.txt); licensing will be disabled in this build." -ForegroundColor Yellow }

    Write-Host "==> Pass 1: build + obfuscate + publish (measure size)" -ForegroundColor Cyan
    Set-ExpectedExeSize 0
    Invoke-Build
    Invoke-ObfuscateAndPublish $tool

    $exePath = Join-Path $OutputDir $exeName
    $raw = (Get-Item $exePath).Length
    $target = [math]::Ceiling(($raw + $PadMargin) / 4096) * 4096

    Write-Host "==> Pass 2: bake ExpectedExeSize=$target, rebuild, pad" -ForegroundColor Cyan
    Set-ExpectedExeSize $target
    Invoke-Build
    Invoke-ObfuscateAndPublish $tool

    # Pad the exe with trailing zero bytes up to exactly $target (harmless for a .NET single-file exe).
    $cur = (Get-Item $exePath).Length
    if ($cur -gt $target) { throw "Exe grew beyond target ($cur > $target). Increase PadMargin." }
    if ($cur -lt $target) {
        $fs = [System.IO.File]::Open($exePath, 'Append')
        try { $fs.Write((New-Object byte[] ($target - $cur)), 0, ($target - $cur)) } finally { $fs.Close() }
    }

    Write-Host "==> Sanity check: secret strings must not be plaintext" -ForegroundColor Cyan
    $bytes = [System.IO.File]::ReadAllBytes($exePath)
    $text  = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($needle in @("fslm_v2_api_request", "MODEXA_API_KEY_PLACEHOLDER")) {
        if ($text.Contains($needle)) { throw "Plaintext secret '$needle' found in exe - obfuscation failed." }
    }

    Write-Host "==> Done: $exePath ($((Get-Item $exePath).Length) bytes)" -ForegroundColor Green
}
finally {
    Set-ExpectedExeSize 0   # always restore the dev defaults
    Set-FslmKey ""          # never leave the real key in source
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
