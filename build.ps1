# Build the WinForms port. No SDK - this compiler ships with Windows.
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $dir "Polyclicker.exe"
$src = Get-ChildItem -Path $dir -Filter *.cs | ForEach-Object { $_.FullName }
$ico = Join-Path $dir "app.ico"
$manifest = Join-Path $dir "app.manifest"
# /win32icon puts it on the exe itself (Explorer, taskbar pin); the embedded
# resource is the same file read back at runtime for the window and the tray
& $csc /nologo /optimize+ /target:winexe /out:$out `
    /win32icon:$ico "/resource:$ico,Polyclicker.app.ico" `
    "/win32manifest:$manifest" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $src
if ($LASTEXITCODE -eq 0) {
  "built: $out  ({0:N0} bytes)" -f (Get-Item $out).Length
} else {
  "BUILD FAILED ($LASTEXITCODE)"
}
exit $LASTEXITCODE
