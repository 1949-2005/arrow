# The app's icon next to the clock. desktop.js starts it; Windows ends it together with the app.
# Menu clicks call the app's local API (FDL_URL), like the web page does.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Name Native -Namespace FastDL -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
'@
# Without this, Windows scales a blurry 16 px icon and menu up on high-DPI screens.
[void][FastDL.Native]::SetProcessDPIAware()

function Call($path) { try { [void](New-Object Net.WebClient).UploadString($env:FDL_URL + $path, '') } catch {} }

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Text = $env:FDL_NAME
# Windows' own loader (IMAGE_ICON, LR_LOADFROMFILE) reads PNG icon images; .NET's Icon class draws them as noise.
$size = [System.Windows.Forms.SystemInformation]::SmallIconSize
$icon = [FastDL.Native]::LoadImage([IntPtr]::Zero, $env:FDL_ICON, 1, $size.Width, $size.Height, 0x10)
$tray.Icon = if ($icon -ne [IntPtr]::Zero) { [System.Drawing.Icon]::FromHandle($icon) } else { [System.Drawing.SystemIcons]::Application }
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$open = $menu.Items.Add("Open $env:FDL_NAME", $null, { Call /api/open })
$open.Font = New-Object System.Drawing.Font $open.Font, ([System.Drawing.FontStyle]::Bold) # what a click on the icon does
[void]$menu.Items.Add('Pause all', $null, { Call /api/pause-all })
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
# Hide the icon first: Windows ends this script as soon as the app has quit.
[void]$menu.Items.Add('Quit', $null, { $tray.Visible = $false; Call /api/quit; [System.Windows.Forms.Application]::Exit() })
$tray.ContextMenuStrip = $menu
$tray.add_MouseClick({ if ($_.Button -eq 'Left') { Call /api/open } })
$tray.Visible = $true
[System.Windows.Forms.Application]::Run()
