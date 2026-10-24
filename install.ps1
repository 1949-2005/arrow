# Arrow one-line installer and updater (Windows 10/11, no admin rights needed):
#   irm https://raw.githubusercontent.com/1949-2005/arrow/main/install.ps1 | iex
# Downloads the latest ArrowSetup.exe from GitHub Releases, checks its SHA-256, installs Arrow for your user account
# and opens it. Run it again to update: a running Arrow is quit first, and the download list and settings stay.
# (install.cmd is something else: it builds Arrow from this repo's source.)
# Test hooks, as ArrowSetup.exe's: ARROW_SETUP_DIR, _REGKEY, _DESKTOP, _STARTUP; ARROW_SETUP_NO_START=1 doesn't open Arrow.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'      # much faster downloads in Windows PowerShell 5.1
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $repo = '1949-2005/arrow'
    $ua = @{ 'User-Agent' = 'arrow-installer' }
    $root = if ($env:ARROW_SETUP_REGKEY) { $env:ARROW_SETUP_REGKEY } else { 'Software\Microsoft\Windows\CurrentVersion\Uninstall' }
    $key = "HKCU:\$root\Arrow"

    Write-Host ''
    Write-Host '  Arrow installer' -ForegroundColor Cyan
    Write-Host '  Finding the latest release...'
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $ua
    $exe = $rel.assets | Where-Object { $_.name -eq 'ArrowSetup.exe' }
    $sum = $rel.assets | Where-Object { $_.name -eq 'ArrowSetup.exe.sha256' }
    if (-not $exe -or -not $sum) { throw "Release $($rel.tag_name) is missing ArrowSetup.exe or its checksum." }

    $tmp = Join-Path $env:TEMP 'arrow-setup'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $setup = Join-Path $tmp 'ArrowSetup.exe'
    $hashFile = Join-Path $tmp 'ArrowSetup.exe.sha256'
    Write-Host ("  Downloading Arrow {0} ({1:N0} MB)..." -f $rel.tag_name, ($exe.size / 1MB))
    Invoke-WebRequest $exe.browser_download_url -OutFile $setup -Headers $ua -UseBasicParsing
    Invoke-WebRequest $sum.browser_download_url -OutFile $hashFile -Headers $ua -UseBasicParsing

    $expected = ((Get-Content $hashFile -Raw).Trim() -split '\s+')[0].ToLower()
    $actual = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) {
        Remove-Item $setup -Force
        throw 'Checksum mismatch: the download is damaged or was changed. Nothing was installed.'
    }
    Write-Host '  Checksum OK. Installing (getting yt-dlp, FFmpeg and Deno can take a minute)...'

    # --quit-arrow: an update quits the running Arrow first, which saves its download list
    $opts = @('--quiet', '--quit-arrow')
    if (Test-Path $key) {
        # an update keeps a desktop or Start-with-Windows shortcut the user deleted deleted
        $desktop = if ($env:ARROW_SETUP_DESKTOP) { $env:ARROW_SETUP_DESKTOP } else { [Environment]::GetFolderPath('DesktopDirectory') }
        $startup = if ($env:ARROW_SETUP_STARTUP) { $env:ARROW_SETUP_STARTUP } else { [Environment]::GetFolderPath('Startup') }
        if (-not (Test-Path (Join-Path $desktop 'Arrow.lnk'))) { $opts += '--no-desktop' }
        if (-not (Test-Path (Join-Path $startup 'Arrow.lnk'))) { $opts += '--no-startup' }
    }
    $p = Start-Process $setup -ArgumentList $opts -PassThru
    $p.WaitForExit()
    $code = $p.ExitCode
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($code -eq 2) { throw 'Arrow is running and could not be closed. Quit it from its icon next to the clock (right-click, Quit), then run this command again.' }
    if ($code -ne 0 -and $code -ne 3) { throw "Setup failed (exit code $code)." }

    $dir = $env:ARROW_SETUP_DIR
    if (-not $dir) { $dir = (Get-ItemProperty $key -ErrorAction SilentlyContinue).InstallLocation }
    if (-not $dir) { $dir = Join-Path $env:LOCALAPPDATA 'Programs\Arrow' }
    if (-not $env:ARROW_SETUP_NO_START) {
        # winget may have just put yt-dlp, FFmpeg and Deno on the user's PATH: Arrow gets the fresh one
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        Start-Process (Join-Path $dir 'Arrow.exe')
    }

    Write-Host ''
    Write-Host '  Arrow is installed and running (its icon is next to the clock).' -ForegroundColor Green
    if ($code -eq 3) {
        Write-Host '  yt-dlp, FFmpeg and Deno could not be installed, so YouTube and video downloads will not work yet.' -ForegroundColor Yellow
        Write-Host '  Install them with: winget install yt-dlp.yt-dlp' -ForegroundColor Yellow
    }
    Write-Host '  Add Arrow to your browser once (Brave, Chrome or Edge):'
    Write-Host '    1. Open brave://extensions (or chrome://extensions, edge://extensions)'
    Write-Host '    2. Turn on Developer mode'
    Write-Host ('    3. Click Load unpacked and choose ' + (Join-Path $dir 'extension'))
    Write-Host '  Update: run this command again. Uninstall: Settings > Apps > Installed apps > Arrow'
    Write-Host ''
}
