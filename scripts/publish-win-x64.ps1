param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $projectRoot 'src/CloseLoom.Desktop/CloseLoom.Desktop.csproj'
$outDir = Join-Path $projectRoot 'artifacts/CloseLoom-win-x64'
$zip = Join-Path $projectRoot 'artifacts/CloseLoom-win-x64-portable.zip'
$dotnet = Get-Command dotnet -ErrorAction Stop
if (-not (Test-Path -LiteralPath $project)) { throw "Desktop project not found" }
if ((Test-Path -LiteralPath $outDir) -or (Test-Path -LiteralPath $zip)) { throw "Output exists already; review old artifacts before rerunning" }
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
& $dotnet.Source publish $project -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Publish command failed" }
if (-not (Test-Path -LiteralPath (Join-Path $outDir 'CloseLoom.Desktop.exe'))) { throw "Executable missing" }
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Output ("PORTABLE_ZIP=" + $zip)
Write-Output "Unsigned preview. Dry Run only. Not tested on other PCs."
