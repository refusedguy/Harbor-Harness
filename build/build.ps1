# build.ps1 — Fallout bootstrap (Windows PowerShell).
#
# Bootstraps the .NET SDK if missing, builds the primary
# build\_build.fallout\_build.fallout.csproj project, then invokes the Fallout
# Build class with whatever targets / args were passed through. Adapted from
# the generated bootstrap for the Harbor solution layout: build project at
# .\build\_build.fallout\_build.fallout.csproj, solution file at .\Harbor.slnx.
#
# Examples (never invent commands — ask the build itself):
#   .\build.ps1                                  # default target (Compile)
#   .\build.ps1 list --format json               # machine-readable target catalog
#   .\build.ps1 doctor --format json             # environment health checks
#   .\build.ps1 what --path src\Foo\Bar.cs       # what to run after editing a file
#   .\build.ps1 Compile --dry-run --format json  # plan without executing
#   .\build.ps1 Compile                          # build the solution
#   .\build.ps1 Test                             # run all tests
#   .\build.ps1 Clean Compile Test               # chain targets
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
$BuildProjectFile = Join-Path $RootDir 'build\_build.fallout\_build.fallout.csproj'
$SolutionFile = Join-Path $RootDir 'Harbor.slnx'

# ── Artifacts / temp ─────────────────────────────────────────────────────────
$FalloutTempDir = Join-Path $RootDir '.fallout\temp'
$FalloutBinDir = Join-Path $RootDir '.fallout\bin'
if (-not (Test-Path $FalloutTempDir))
{
    New-Item -ItemType Directory -Path $FalloutTempDir -Force | Out-Null
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

# ── Build the Fallout orchestrator (or use cached output) ───────────────────
$BuildProjectFramework = 'net10.0'
$BuildProjectOutput = Join-Path $FalloutBinDir $BuildProjectFramework

& $DotnetExe build $BuildProjectFile `
    --framework $BuildProjectFramework `
    --configuration Release `
    --output $BuildProjectOutput `
    -nologo `
    -clp:NoSummary

if ($LASTEXITCODE -ne 0)
{
    Write-Error "Building _build.fallout.csproj failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ── Invoke Fallout ───────────────────────────────────────────────────────────
$BuildDll = Join-Path $BuildProjectOutput '_build.fallout.dll'
if ($Target)
{
    & $DotnetExe exec $BuildDll @Target
}
else
{
    & $DotnetExe exec $BuildDll
}
exit $LASTEXITCODE
