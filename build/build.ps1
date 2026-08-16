# build.ps1 — NUKE bootstrap (Windows PowerShell).
#
# Bootstraps the .NET SDK if missing, restores the build/_build.csproj project,
# then invokes the NUKE Build class with whatever targets / args were passed
# through. Standard NUKE-generated bootstrap script (lightly customized for
# the Harbor solution layout: build project at .\build\_build.csproj, solution
# file at .\Harbor.slnx).
#
# Examples:
#   .\build.ps1                       # default target (Compile)
#   .\build.ps1 Compile               # build the solution
#   .\build.ps1 Test                  # run all tests
#   .\build.ps1 PublishCliMinimal     # publish minimal CLI variant
#   .\build.ps1 PublishAll            # publish every artifact
#   .\build.ps1 Clean Compile Test    # chain targets
#   .\build.ps1 Compile --configuration Debug

[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory = $false, ValueFromRemainingArguments = $true)]
    [string[]]$Target
)

$ErrorActionPreference = 'Stop'
if ($env:TRACE -eq 'true')
{
    Set-PSDebug -Trace 1
}

# ── Solution-relative paths ──────────────────────────────────────────────────
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RootDir = (Resolve-Path "$ScriptDir\..").Path
$BuildProjectFile = Join-Path $RootDir 'build\_build.csproj'
$SolutionFile = Join-Path $RootDir 'Harbor.slnx'

# ── Artifacts / temp ─────────────────────────────────────────────────────────
$NukeTempDir = Join-Path $RootDir '.nuke\temp'
$NukeBinDir = Join-Path $RootDir '.nuke\bin'
if (-not (Test-Path $NukeTempDir))
{
    New-Item -ItemType Directory -Path $NukeTempDir -Force | Out-Null
}

# ── .NET SDK bootstrap ───────────────────────────────────────────────────────
$DotnetInstallDir = if ($env:DOTNET_INSTALL_DIR)
{
    $env:DOTNET_INSTALL_DIR
}
else
{
    Join-Path $env:USERPROFILE '.dotnet'
}
$DotnetExe = if ($IsWindows -or -not $IsCoreCLR)
{
    Join-Path $DotnetInstallDir 'dotnet.exe'
}
else
{
    Join-Path $DotnetInstallDir 'dotnet'
}

if (-not (Test-Path $DotnetExe))
{
    $dotnetOnPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetOnPath)
    {
        $DotnetExe = $dotnetOnPath.Source
    }
    else
    {
        Write-Error "ERROR: dotnet not found at $DotnetExe and not on PATH.`n       Install the .NET 10 SDK:  https://dot.net"
        exit 1
    }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUKE_TELEMETRY_OPTOUT = '1'

# ── Build the _build project (or use cached output) ─────────────────────────
$BuildProjectFramework = 'net10.0'
$BuildProjectOutput = Join-Path $NukeBinDir $BuildProjectFramework

& $DotnetExe build $BuildProjectFile `
    --framework $BuildProjectFramework `
    --configuration Release `
    --output $BuildProjectOutput `
    -nologo `
    -clp:NoSummary

if ($LASTEXITCODE -ne 0)
{
    Write-Error "Building _build.csproj failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ── Invoke NUKE ──────────────────────────────────────────────────────────────
$BuildDll = Join-Path $BuildProjectOutput '_build.dll'
if ($Target)
{
    & $DotnetExe exec $BuildDll @Target
}
else
{
    & $DotnetExe exec $BuildDll
}
exit $LASTEXITCODE
