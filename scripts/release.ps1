<#
.SYNOPSIS
    One-command release: build, commit, tag, push, package, upload to GitHub Release.

.DESCRIPTION
    NOTE: this file MUST stay pure ASCII. Windows PowerShell 5.1 reads a BOM-less
    .ps1 as ANSI, so any non-ASCII literal or comment turns into mojibake -- and a
    mangled line can silently swallow the next statement. Chinese release notes live
    in release-notes/*.md and are read explicitly as UTF-8.

    Order matters: the Windows build runs BEFORE the tag is created, so a build
    failure never leaves a tag pointing at a version with no artifact.

    Steps (each skippable):
      1. Preflight (token, repo, editor)
      2. Bump ProjectSettings bundleVersion
      3. Compile check (WindowsBuild.CheckCompile) -- fail fast on script errors
      4. Build StandaloneWindows64 via Tuanjie batchmode
      5. Zip Build/ -> dist/WorldTerrainMapViewer.zip
      6. git add -A + commit
      7. git tag V<Version>
      8. git push origin <branch> + tag
      9. Create/find the GitHub Release and upload the zip

.PARAMETER Version
    Version without leading V, e.g. 2.2.0  (tag becomes V2.2.0).

.PARAMETER NotesFile
    UTF-8 markdown file with the release body.

.PARAMETER Token
    GitHub token. Defaults to $env:GITHUB_TOKEN, then to the token cached by
    Git Credential Manager for github.com.

.EXAMPLE
    ./scripts/release.ps1 -Version 2.2.0 -NotesFile ./release-notes/V2.2.0.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$NotesFile = "",
    [string]$Branch = "main",
    [string]$Repo = "SlipperSoar/WorldTerrainMapViewer",
    [string]$EditorPath = "",
    [string]$Token = "",
    [switch]$SkipCommit,
    [switch]$SkipPush,
    [switch]$SkipBuild,
    [switch]$SkipRelease,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- paths ----------------------------------------------------------------
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
Set-Location $RepoRoot

$Tag       = if ($Version.StartsWith("V")) { $Version } else { "V$Version" }
$PlainVer  = $Tag.TrimStart("V")
$BuildDir  = Join-Path $RepoRoot "Build"
$DistDir   = Join-Path $RepoRoot "dist"
$ZipPath   = Join-Path $DistDir "WorldTerrainMapViewer.zip"
$AssetName = "WorldTerrainMapViewer.zip"
$ApiBase   = "https://api.github.com/repos/$Repo"

function Step([string]$text) { Write-Host ""; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Info([string]$text) { Write-Host "  $text" }
function Fail([string]$text) { Write-Host "  ERROR: $text" -ForegroundColor Red; exit 1 }

# ---- 1. preflight ---------------------------------------------------------
Step "Preflight"
Info "repo    : $Repo ($Branch)"
Info "version : $Tag"
Info "root    : $RepoRoot"

$dirty = (git status --porcelain)
if ($dirty -and $SkipCommit -and -not $AllowDirty) {
    Fail "working tree is dirty but -SkipCommit was given; commit first or pass -AllowDirty"
}

if (-not $Token) { $Token = $env:GITHUB_TOKEN }
if (-not $Token) {
    try {
        $payload = "protocol=https`nhost=github.com`n`n"
        $out = $payload | git credential fill 2>$null
        $line = $out | Where-Object { $_ -like 'password=*' }
        if ($line) { $Token = ($line -replace '^password=', '') }
    } catch { }
}
if (-not $Token -and -not $SkipRelease) { Fail "no GitHub token (set GITHUB_TOKEN or pass -Token)" }

$GhHeaders = @{
    Authorization          = "token $Token"
    Accept                 = "application/vnd.github+json"
    "User-Agent"           = "wtmv-release-script"
    "X-GitHub-Api-Version" = "2022-11-28"
}

# ---- 2. bundleVersion -----------------------------------------------------
Step "Set bundleVersion = $PlainVer"
$projSettings = Join-Path $RepoRoot "ProjectSettings/ProjectSettings.asset"
if (Test-Path $projSettings) {
    $text = Get-Content $projSettings -Raw -Encoding UTF8
    $updated = [regex]::Replace($text, '(?m)^(\s*)bundleVersion:\s*\S+', "`${1}bundleVersion: $PlainVer")
    if ($updated -ne $text) {
        [System.IO.File]::WriteAllText($projSettings, $updated, (New-Object System.Text.UTF8Encoding($false)))
        Info "bundleVersion updated"
    } else {
        Info "bundleVersion already $PlainVer"
    }
}

# ---- 3. build -------------------------------------------------------------
if (-not $SkipBuild) {
    Step "Build Windows player"
    if (-not $EditorPath) {
        $EditorPath = "C:\Program Files\Tuanjie\Hub\Editor\2022.3.62t11\Editor\Tuanjie.exe"
    }
    if (-not (Test-Path $EditorPath)) { Fail "editor not found: $EditorPath" }
    Info "editor: $EditorPath"

    if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null
    New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

    $logPath = Join-Path $DistDir "build.log"
    if (Test-Path $logPath) { Remove-Item $logPath -Force }
    $checkLog = Join-Path $DistDir "compile-check.log"
    if (Test-Path $checkLog) { Remove-Item $checkLog -Force }

    $env:WTMV_BUILD_DIR = $BuildDir

    # Compile check first: a build that silently reuses a stale assembly is worse
    # than a build that fails loudly.
    $checkArgs = @(
        "-batchmode", "-nographics", "-quit",
        "-projectPath", "`"$RepoRoot`"",
        "-executeMethod", "WindowsBuild.CheckCompile",
        "-logFile", "`"$checkLog`""
    )
    Info "checking script compilation"
    $check = Start-Process -FilePath $EditorPath -ArgumentList $checkArgs -Wait -PassThru -NoNewWindow

    $checkOk = $false
    if (Test-Path $checkLog) {
        $checkOk = [bool](Select-String -Path $checkLog -Pattern 'COMPILE_OK' -Quiet)
    }
    if ($check.ExitCode -ne 0 -or -not $checkOk) {
        if (Test-Path $checkLog) {
            Select-String -Path $checkLog -Pattern 'error CS' | Select-Object -First 20 |
                ForEach-Object { Write-Host ("    " + $_.Line) }
        }
        Fail "script compilation failed; see $checkLog"
    }
    Info "scripts compile OK"

    $editorArgs = @(
        "-batchmode", "-nographics", "-quit",
        "-projectPath", "`"$RepoRoot`"",
        "-executeMethod", "WindowsBuild.Build",
        "-buildTarget", "StandaloneWindows64",
        "-logFile", "`"$logPath`""
    )
    Info "launching batchmode build (several minutes)"
    $proc = Start-Process -FilePath $EditorPath -ArgumentList $editorArgs -Wait -PassThru -NoNewWindow
    Info "editor exit code: $($proc.ExitCode)"

    if (Test-Path $logPath) {
        $tail = Select-String -Path $logPath -Pattern 'BUILD_OK|\[Build\]' |
                Select-Object -Last 12
        if ($tail) {
            Info "log tail:"
            $tail | ForEach-Object { Write-Host ("    " + $_.Line) }
        }
    }

    if ($proc.ExitCode -ne 0) { Fail "build failed (exit $($proc.ExitCode)); see $logPath" }
    $exe = Join-Path $BuildDir "WorldTerrainMapViewer.exe"
    if (-not (Test-Path $exe)) { Fail "build reported success but $exe is missing" }
    Info "built: $exe"
}

# ---- 4. package -----------------------------------------------------------
Step "Package"
$exePath = Join-Path $BuildDir "WorldTerrainMapViewer.exe"
if (-not (Test-Path $exePath)) { Fail "no build output in $BuildDir" }

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $BuildDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipMb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Info "zip: $ZipPath ($zipMb MB)"

# ---- 5. commit ------------------------------------------------------------
if (-not $SkipCommit) {
    Step "Commit"
    git add -A
    $staged = (git diff --cached --name-only)
    if ($staged) {
        Info ("staged files: " + ($staged | Measure-Object).Count)
        git commit -m "release: $Tag" | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "git commit failed" }
        Info "committed"
    } else {
        Info "nothing to commit"
    }
}

# ---- 6. tag ---------------------------------------------------------------
Step "Tag $Tag"
$existingTag = (git tag --list $Tag)
if ($existingTag) {
    Info "tag already exists"
} else {
    git tag -a $Tag -m "Release $Tag"
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }
    Info "tag created"
}

# ---- 7. push --------------------------------------------------------------
if (-not $SkipPush) {
    Step "Push"
    git push origin $Branch
    if ($LASTEXITCODE -ne 0) { Fail "push branch failed" }
    git push origin $Tag
    if ($LASTEXITCODE -ne 0) { Fail "push tag failed" }
    Info "pushed $Branch and $Tag"
}

# ---- 8. release -----------------------------------------------------------
if ($SkipRelease) { Info "release upload skipped"; exit 0 }

Step "GitHub Release $Tag"
$body = ""
if ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { Fail "notes file not found: $NotesFile" }
    # ReadAllText, not Get-Content -Raw: the latter returns a string carrying
    # PSPath/PSDrive note properties, and ConvertTo-Json then serializes it as an
    # object instead of a string -> GitHub rejects body with 422.
    $body = [System.IO.File]::ReadAllText((Resolve-Path $NotesFile).Path,
                                          [System.Text.Encoding]::UTF8)
}

$release = $null
try {
    $release = Invoke-RestMethod -Uri "$ApiBase/releases/tags/$Tag" -Headers $GhHeaders -Method Get
    Info "release already exists (id $($release.id))"
} catch {
    Info "creating release"
    $releaseName = "Release $((Get-Date).ToString('yyyy.MM.dd')) $Tag"
    $payload = @{
        tag_name = $Tag
        name     = $releaseName
        body     = $body
        draft    = $false
        prerelease = $false
    } | ConvertTo-Json -Depth 5
    $release = Invoke-RestMethod -Uri "$ApiBase/releases" -Headers $GhHeaders -Method Post `
        -ContentType "application/json; charset=utf-8" `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($payload))
    Info "created release id $($release.id)"
}

foreach ($asset in $release.assets) {
    if ($asset.name -eq $AssetName) {
        Info "deleting existing asset $($asset.id)"
        Invoke-RestMethod -Uri "$ApiBase/releases/assets/$($asset.id)" -Headers $GhHeaders -Method Delete | Out-Null
    }
}

$uploadUrl = $release.upload_url -replace '\{.*\}', ''
$uploadUri = "$uploadUrl`?name=$AssetName"
Info "uploading $AssetName ($zipMb MB)"
$bytes = [System.IO.File]::ReadAllBytes($ZipPath)
$uploadHeaders = @{ Authorization = "token $Token"; "User-Agent" = "wtmv-release-script" }
$result = Invoke-RestMethod -Uri $uploadUri -Headers $uploadHeaders -Method Post `
    -ContentType "application/zip" -Body $bytes
Info "uploaded: $($result.name) ($([math]::Round($result.size/1MB,1)) MB)"
Info "download: $($result.browser_download_url)"

Write-Host ""
Write-Host "Release $Tag done." -ForegroundColor Green
