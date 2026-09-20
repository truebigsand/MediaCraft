# publish-fd.ps1 -- framework-dependent build (small, requires .NET 10 Desktop Runtime)
# ASCII ONLY: Windows PowerShell 5.1 reads BOM-less UTF-8 as GBK and would corrupt non-ASCII text.

param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\MediaCraft\MediaCraft.csproj'
$output = Join-Path $repoRoot 'dist\framework-dependent'

Write-Output "publishing $project ($Configuration / $Runtime / framework-dependent)"
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $project -c $Configuration -r $Runtime --self-contained false -o $output

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$dest = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude Outputs\MediaCraft\framework-dependent'
if (-not (Test-Path $dest)) { New-Item -ItemType Directory -Path $dest -Force | Out-Null }

Get-ChildItem $output -File | ForEach-Object { Copy-Item $_.FullName $dest -Force }

$exe = Join-Path $dest 'MediaCraft.exe'
$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 2)
Write-Output "done: $exe ($sizeMb MB, requires .NET 10 Desktop Runtime)"
