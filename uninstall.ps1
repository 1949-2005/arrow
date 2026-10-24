# Arrow one-line uninstaller:
#   irm https://raw.githubusercontent.com/1949-2005/arrow/main/uninstall.ps1 | iex
# Quits Arrow, then removes it, its shortcuts and its Apps entry. Downloaded files, the download list and settings
# stay (to also remove the list and settings, uninstall from Settings > Apps instead). Test hook: ARROW_SETUP_REGKEY.

& {
    $root = if ($env:ARROW_SETUP_REGKEY) { $env:ARROW_SETUP_REGKEY } else { 'Software\Microsoft\Windows\CurrentVersion\Uninstall' }
    $entry = Get-ItemProperty "HKCU:\$root\Arrow" -ErrorAction SilentlyContinue
    if (-not $entry) { Write-Host 'Arrow is not installed.' -ForegroundColor Yellow; return }
    $un = Join-Path $entry.InstallLocation 'Uninstall Arrow.exe'
    if (-not (Test-Path $un)) { Write-Host "Can't find $un" -ForegroundColor Red; return }
    $p = Start-Process $un -ArgumentList '--quiet', '--quit-arrow' -PassThru
    $p.WaitForExit()
    switch ($p.ExitCode) {
        0 {
            Write-Host 'Arrow was removed. Downloaded files, the download list and settings stay.' -ForegroundColor Green
            Write-Host "Also remove Arrow from your browser's extensions page (brave://extensions or chrome://extensions)."
        }
        2 { Write-Host 'Arrow is running and could not be closed. Quit it from its icon next to the clock, then run this command again.' -ForegroundColor Yellow }
        default { Write-Host "Uninstall failed (exit code $($p.ExitCode))." -ForegroundColor Red }
    }
}
