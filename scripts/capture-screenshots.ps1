<#
.SYNOPSIS
    Captures the frames the README animation is built from.

.DESCRIPTION
    Runs Fjord against an empty profile and this repository, drives it with the keys a
    reader would press, and saves one PNG per scene. Feed the output directory to
    scripts/make-gif.py.

    The isolated profile matters for more than repeatability: without it the shots would
    show whatever tabs and folders the person running this had open.

.NOTES
    Refuses to send keys unless Fjord is the foreground window, so a dialog stealing
    focus part-way through cannot turn this into typing at whatever took it.
#>
param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$Out = (Join-Path ([System.IO.Path]::GetTempPath()) "fjord-shots")
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool repaint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    // GetWindowRect includes the invisible resize border; this is what the eye sees.
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

# Without this, GetWindowRect and CopyFromScreen both report virtualised coordinates on a
# scaled display, and the capture lands somewhere else at the wrong magnification.
[Win]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

# Cleared, not merely created: a frame left over from a previous run is picked up by
# the GIF builder and turns up in the middle of the animation.
Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$profileDir = Join-Path $env:TEMP "fjord-shots-profile"
Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null

$exe = Join-Path $Repo "src/Fjord.App/bin/Debug/net10.0/Fjord.exe"
if (-not (Test-Path $exe)) { throw "Build first: dotnet build. No executable at $exe" }
$env:LOCALAPPDATA = $profileDir
$env:APPDATA = $profileDir
$env:FJORD_CONFIG_DIR = Join-Path $profileDir "config"

$proc = Start-Process $exe -ArgumentList $Repo -PassThru
$deadline = (Get-Date).AddSeconds(30)
while ($proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    $proc.Refresh()
}
if ($proc.MainWindowHandle -eq 0) { throw "Fjord never showed a window." }

$hwnd = $proc.MainWindowHandle
Start-Sleep -Seconds 3
[Win]::ShowWindow($hwnd, 9) | Out-Null
[Win]::MoveWindow($hwnd, 160, 160, 1800, 1150, $true) | Out-Null
Start-Sleep -Milliseconds 800

function Assert-Focus {
    [Win]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 400
    if ([Win]::GetForegroundWindow() -ne $hwnd) {
        throw "Something else took focus. Refusing to send keys."
    }
}

function Send([string]$keys, [int]$pause = 700) {
    Assert-Focus
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds $pause
}

function Shot([string]$name) {
    Assert-Focus
    $r = New-Object Win+RECT
    if ([Win]::DwmGetWindowAttribute($hwnd, 9, [ref]$r, 16) -ne 0) {
        [Win]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    }
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $path = Join-Path $Out "$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "  $name  ${w}x${h}"
}

try {
    # 1. An ordinary listing.
    Shot "01-listing"

    # 2. Visual mode over a few rows.
    Send "jjv"; Send "jjj"
    Shot "02-visual"
    Send "{ESC}"

    # 3. The command line, completing.
    Send ":"; Send "list" 900
    Shot "03-command"
    Send "{ESC}"; Send "{ESC}"

    # 4. Fuzzy search, committed, so the shot shows the narrowed listing.
    Send "/"
    foreach ($c in "dbp".ToCharArray()) { Send "$c" 250 }
    Send "{ENTER}" 900
    Shot "04-search"
    Send ":search-clear{ENTER}" 700

    # 5. Inside an archive. The zip rather than a tar.gz: a tar has no central directory,
    #    so listing a 41 MB one means decompressing the whole thing first.
    Send ":cd dist{ENTER}" 1500
    Send "G" 500
    Send "l" 2500
    Shot "05-archive"
    Send "h" 900
    Send ":cd ..{ENTER}" 1200

    # 6. Recursive folder sizes. bench holds ~95k files, so give the walk time.
    Send "gg"
    Send "^d" 7000
    Shot "06-sizes"

    # 7. Help.
    Send "?" 900
    Shot "07-help"
    Send "{ESC}"
}
finally {
    Start-Sleep -Milliseconds 300
    if (-not $proc.HasExited) { $proc.Kill() }
}

Write-Host "Done -> $Out"
