# Build ScreenSnap.exe bằng csc.exe có sẵn trong .NET Framework 4.x (không cần cài Visual Studio)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "Không tìm thấy csc.exe (.NET Framework 4.x)" }

New-Item -ItemType Directory -Force dist | Out-Null
$common = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', '/platform:anycpu',
            '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll')

# Bước 1: build tạm để sinh file icon
& $csc @common '/out:dist\ScreenSnap.tmp.exe' 'src\ScreenSnap.cs'
if ($LASTEXITCODE -ne 0) { throw "Biên dịch thất bại" }
Start-Process -FilePath 'dist\ScreenSnap.tmp.exe' -ArgumentList '--make-icon', 'dist\app.ico' -Wait
Remove-Item 'dist\ScreenSnap.tmp.exe' -Force

# Bước 2: build bản chính có icon
& $csc @common '/win32icon:dist\app.ico' '/out:dist\ScreenSnap.exe' 'src\ScreenSnap.cs'
if ($LASTEXITCODE -ne 0) { throw "Biên dịch thất bại" }
Write-Host "OK -> $(Resolve-Path dist\ScreenSnap.exe)"
