<#
.SYNOPSIS
  Builds 808 Radio and packages a release zip.

.DESCRIPTION
  1. Builds the native libraries with MSYS2 (rtlsdr.dll, libusb-1.0.dll, libnrsc5.dll), unless already built
     (-Clean rebuilds them).
  2. Publishes the app as a self-contained, single-file 808Radio.exe for win-x64 (no .NET install needed).
  3. Packages dist\808Radio-v<version>-win-x64.zip (an "808Radio" folder) and a .sha256 checksum.

.PARAMETER Msys2
  MSYS2 install folder. Default: C:\msys64

.EXAMPLE
  .\build.ps1
.EXAMPLE
  .\build.ps1 -Clean
#>
param(
    [string]$Msys2 = "C:\msys64",
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = "$root\src\Radio808.App\Radio808.App.csproj"
$native = "$root\native\bin"

function Step($msg) { Write-Host "`n=== $msg" -ForegroundColor Cyan }

# ---- prerequisites ----
Step "Checking prerequisites"
foreach ($sub in 'nrsc5', 'rtl-sdr') {
    if (-not (Test-Path "$root\$sub\CMakeLists.txt")) { throw "Submodule '$sub' is missing. Run: git submodule update --init" }
}
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw ".NET SDK not found. Install the .NET 9 SDK: winget install Microsoft.DotNet.SDK.9" }
if (-not ((& $dotnet --list-sdks) -match '^9\.')) { throw ".NET 9 SDK not found. Install it: winget install Microsoft.DotNet.SDK.9" }

# ---- native libraries ----
if ($Clean) {
    foreach ($d in "$root\native", "$root\nrsc5\build", "$root\rtl-sdr\build") {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
}
$need = 'rtlsdr.dll', 'libusb-1.0.dll', 'libnrsc5.dll' | Where-Object { -not (Test-Path "$native\$_") }
if ($need) {
    Step "Building native libraries with MSYS2 (first build takes several minutes)"
    $bash = "$Msys2\usr\bin\bash.exe"
    if (-not (Test-Path $bash)) { throw "MSYS2 not found at $Msys2. Install it: winget install MSYS2.MSYS2" }
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'
    & $bash -lc 'pacman -S --needed --noconfirm make autoconf automake patch git ${MINGW_PACKAGE_PREFIX}-gcc ${MINGW_PACKAGE_PREFIX}-cmake ${MINGW_PACKAGE_PREFIX}-libusb ${MINGW_PACKAGE_PREFIX}-pkgconf ${MINGW_PACKAGE_PREFIX}-libtool'
    if ($LASTEXITCODE) { throw "MSYS2 package install failed" }
    $unix = '/' + $root.Substring(0, 1).ToLower() + ($root.Substring(2) -replace '\\', '/')   # C:\x\y -> /c/x/y
    $log = "$root\native-build.log"
    # output to a log file: piping MSYS2 output through PowerShell mangles quoting and can hang configure scripts
    & $bash -lc "cd '$unix' && ./build-native.sh > native-build.log 2>&1 < /dev/null"
    $failed = $LASTEXITCODE
    $missing = 'rtlsdr.dll', 'libusb-1.0.dll', 'libnrsc5.dll' | Where-Object { -not (Test-Path "$native\$_") }
    if ($failed -or $missing) {
        Get-Content $log -Tail 30
        throw "Native build failed (see $log)"
    }
    Get-Content $log -Tail 4
} else {
    Step "Native libraries already built (use -Clean to rebuild)"
}

# ---- app ----
[xml]$csproj = Get-Content $proj
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = "$root\dist\stage\808Radio"
Step "Publishing 808 Radio $version (self-contained, single file)"
if (Test-Path "$root\dist\stage") { Remove-Item "$root\dist\stage" -Recurse -Force }
& $dotnet publish $proj -c Release -p:Platform=x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None `
    -o $stage -nologo -v m
if ($LASTEXITCODE) { throw "publish failed" }
foreach ($f in '808Radio.exe', 'rtlsdr.dll', 'libusb-1.0.dll', 'libnrsc5.dll') {
    if (-not (Test-Path "$stage\$f")) { throw "publish output is missing $f" }
}

# ---- package ----
Copy-Item "$root\LICENSE" "$stage\LICENSE.txt"
Copy-Item "$root\THIRD_PARTY_NOTICES.md" $stage
Copy-Item "$root\packaging\README.txt" $stage
$zip = "$root\dist\808Radio-v$version-win-x64.zip"
Step "Packaging $zip"
if (Test-Path $zip) { Remove-Item $zip }
# Build the zip by hand: Compress-Archive in Windows PowerShell 5.1 writes backslashes into entry names,
# which non-Windows unzip tools mishandle.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -File) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "808Radio/$($file.Name)", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Remove-Item "$root\dist\stage" -Recurse -Force
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`nDone: $zip ($mb MB)`nSHA256: $hash" -ForegroundColor Green
