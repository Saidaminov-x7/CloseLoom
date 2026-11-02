param([string]$Executable = (Join-Path $PSScriptRoot '..\artifacts\CloseLoom-win-x64\CloseLoom.Desktop.exe'))
$ErrorActionPreference = 'Stop'
$Executable = [IO.Path]::GetFullPath($Executable)
if (-not (Test-Path -LiteralPath $Executable)) { throw "EXE missing: $Executable" }
if (Get-Process -Name 'CloseLoom.Desktop' -ErrorAction SilentlyContinue) { throw 'Existing CloseLoom detected; do not interfere with a user session.' }
$root = Join-Path $env:TEMP ('CloseLoom-activation-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$env:CLOSELOOM_DATA_DIR = $root
$first = $null
try {
    $first = Start-Process -FilePath $Executable -ArgumentList '--background' -PassThru
    Start-Sleep -Seconds 5
    $first.Refresh()
    if ($first.HasExited) { throw 'Background application exited unexpectedly.' }
    $second = Start-Process -FilePath $Executable -PassThru
    if (-not $second.WaitForExit(10000)) {
        Stop-Process -Id $second.Id -Force -ErrorAction SilentlyContinue
        throw 'Second launch did not exit within 10 seconds.'
    }
    Start-Sleep -Seconds 3
    $first.Refresh()
    if ($first.HasExited) { throw 'Original instance exited.' }
    if ($first.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Opening a second instance did not reveal the original window.' }
    Write-Output "PASS: autostart background instance remains running, second launch activates its window. PID=$($first.Id)"
} finally {
    if ($first) {
        $first.Refresh()
        if (-not $first.HasExited) { Stop-Process -Id $first.Id -Force -ErrorAction SilentlyContinue }
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:CLOSELOOM_DATA_DIR -ErrorAction SilentlyContinue
}
