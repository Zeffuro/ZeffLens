$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$projectRoot = Join-Path $repoRoot "ZeffLens"

function Invoke-Git
{
    param([Parameter(Mandatory)] [string[]] $Arguments)

    & git -C $repoRoot @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Test-PropsContains
{
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Pattern
    )

    $fullPath = Join-Path $projectRoot $Path
    (Test-Path $fullPath) -and (Select-String -Path $fullPath -Pattern $Pattern -Quiet)
}

function Add-Submodule
{
    param(
        [Parameter(Mandatory)] [string] $Url,
        [Parameter(Mandatory)] [string] $Path
    )

    $targetPath = Join-Path $repoRoot $Path
    if (Test-Path $targetPath)
    {
        Write-Host "$Path already exists; skipping."
        return
    }

    Invoke-Git -Arguments @("submodule", "add", $Url, $Path)
}

if (-not (Get-Command git -ErrorAction SilentlyContinue))
{
    throw "git is required to add submodules."
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& git -C $repoRoot rev-parse --show-toplevel *> $null
$gitRepositoryExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference

if ($gitRepositoryExitCode -ne 0)
{
    Invoke-Git -Arguments @("init")
}

if (Test-PropsContains "Build\KamiToolKit.props" "KamiToolKit\\KamiToolKit\.csproj")
{
    Add-Submodule "https://github.com/MidoriKami/KamiToolKit" "ZeffLens/KamiToolKit"
}

if (Test-PropsContains "Build\KamiToolKit.Components.props" "KamiToolKit\.Components\\KamiToolKit\.Components\.csproj")
{
    Add-Submodule "https://github.com/MidoriKami/KamiToolKit.Components" "ZeffLens/KamiToolKit.Components"
}

if (Test-PropsContains "Build\Lumina.Excel.props" "Lumina\.Excel\\src\\Lumina\.Excel\\Lumina\.Excel\.csproj")
{
    Add-Submodule "https://github.com/NotAdam/Lumina.Excel" "ZeffLens/Lumina.Excel"
}

if (Test-Path (Join-Path $projectRoot "Build\ManualFFXIVClientStructs.props"))
{
    Add-Submodule "https://github.com/aers/FFXIVClientStructs" "ZeffLens/FFXIVClientStructs"
}

Invoke-Git -Arguments @("submodule", "update", "--init", "--recursive")

Remove-Item -LiteralPath $PSCommandPath -Force
