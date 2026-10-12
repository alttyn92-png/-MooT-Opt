Set-Location -LiteralPath $PSScriptRoot
$mootAgentPath = Join-Path $env:LOCALAPPDATA 'Programs/MooT/MooT-Agent.exe'
if (Test-Path -LiteralPath $mootAgentPath) {
    Start-Process -FilePath $mootAgentPath -ArgumentList '--server http://127.0.0.1:8765' -WindowStyle Hidden
} else {
    Start-Process -FilePath (Join-Path $PSScriptRoot 'downloads/MooT-Local-Setup.exe')
}
