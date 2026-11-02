param(
    [string]$Configuration = 'Release',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts'),
    [string]$DotNet = 'dotnet',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $projectRoot 'src/CloseLoom.Desktop/CloseLoom.Desktop.csproj'
$outputRootFull = [IO.Path]::GetFullPath($OutputRoot)
$outDir = Join-Path $outputRootFull 'CloseLoom-win-x64'
$zip = Join-Path $outputRootFull 'CloseLoom-win-x64.zip'
$checksum = "$zip.sha256"
foreach ($path in @($outDir, $zip, $checksum)) {
    if (Test-Path -LiteralPath $path) {
        if (-not $Force) { throw "Output already exists: $path. Choose another -OutputRoot or explicitly use -Force." }
    }
}
if (-not (Test-Path -LiteralPath $project)) { throw 'Desktop project not found.' }
New-Item -ItemType Directory -Path $outputRootFull -Force | Out-Null
if ($Force) {
    foreach ($path in @($outDir, $zip, $checksum)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}
New-Item -ItemType Directory -Path $outDir | Out-Null
& $DotNet publish $project -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --nologo -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath (Join-Path $outDir 'CloseLoom.Desktop.exe'))) { throw 'Published executable missing.' }
foreach ($name in @('install.cmd', 'uninstall.cmd', 'install.ps1', 'uninstall.ps1', 'README.md', 'README.ru.md', 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $outDir
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination (Join-Path $outDir 'docs') -Recurse
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksum, "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.Encoding]::ASCII)
Write-Output "RELEASE_ZIP=$zip"
Write-Output "SHA256_FILE=$checksum"
