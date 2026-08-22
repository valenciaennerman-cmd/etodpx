# dpetoos - arayuzu derler, acar ve pencerenin ekran goruntusunu alir.
# Gelistirme araci; yonetici gerektirmez, motoru baslatmaz.
param(
    [string]$State = 'off',
    [string]$Out   = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Bin  = Join-Path $Root 'bin'
$Exe  = Join-Path $Bin 'dpetoos-uipreview.exe'

if (-not $Out) { $Out = Join-Path $env:TEMP ("dpetoos-ui-$State.png") }

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw 'WPF'

if (-not (Test-Path $Exe) -or
    ((Get-ChildItem (Join-Path $Root 'src') -Recurse -Filter *.cs |
        Measure-Object -Property LastWriteTime -Maximum).Maximum -gt (Get-Item $Exe).LastWriteTime)) {

    & "$fw\csc.exe" /nologo /target:winexe /platform:x64 /unsafe+ /optimize+ /debug- `
        /warn:3 /nowarn:1701,1702 `
        /out:$Exe /main:Dpetoos.Preview.UiPreview `
        /r:"$fw\System.Xaml.dll" `
        /r:"$wpf\WindowsBase.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\PresentationFramework.dll" `
        "$Root\src\*.cs" "$Root\src\engine\*.cs" "$Root\src\ui\*.cs" `
        "$Root\tools\uipreview\UiPreview.cs"
    if ($LASTEXITCODE -ne 0) { throw 'onizleme derlenemedi' }
}

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    // PrintWindow, pencerenin kendi icerigini cizer: ustunde baska pencere olsa
    // bile dogru goruntuyu verir ve ekranin geri kalanini hic okumaz.
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
    public const uint RENDER_FULL_CONTENT = 2;
}
'@

$p = Start-Process -FilePath $Exe -ArgumentList $State -PassThru
try {
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'pencere acilmadi' }

    Start-Sleep -Milliseconds 1600          # animasyonlar otursun

    $r = New-Object W+R
    [void][W]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Rr - $r.L
    $h = $r.B - $r.T
    if ($w -le 0 -or $h -le 0) { throw "gecersiz pencere olculeri: $w x $h" }

    $bmp = New-Object Drawing.Bitmap $w, $h
    $g = [Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $ok = [W]::PrintWindow($hwnd, $hdc, [W]::RENDER_FULL_CONTENT)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    if (-not $ok) { throw 'PrintWindow basarisiz' }
    $bmp.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()

    Write-Output "$Out  ($w x $h)"
}
finally {
    if (-not $p.HasExited) { $p.Kill() }
}
