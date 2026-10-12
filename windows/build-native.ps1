# Builds sphoto_core.dll with ANGLE (from vcpkg) and copies it, libEGL.dll,
# libGLESv2.dll and the Visual C++ runtime they need into
# windows/native/<Platform>/ for the WinUI app.
#   pwsh windows/build-native.ps1 [-Platform x64|ARM64]
param([ValidateSet("x64", "ARM64")][string]$Platform = "x64")
$ErrorActionPreference = "Stop"

$root = Resolve-Path "$PSScriptRoot/.."
if (-not $env:VCPKG_ROOT) { throw "Set VCPKG_ROOT to your vcpkg checkout." }
$triplet = if ($Platform -eq "ARM64") { "arm64-windows" } else { "x64-windows" }
$arch = if ($Platform -eq "ARM64") { "ARM64" } else { "x64" }
$build = "$root/build/windows-$Platform"

# Quote whole -D arguments: PowerShell does not expand variables inside a
# bare argument that starts with "-".
cmake -S "$root/core" -B $build -A $arch `
  "-DCMAKE_TOOLCHAIN_FILE=$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" `
  "-DVCPKG_TARGET_TRIPLET=$triplet" `
  "-DVCPKG_MANIFEST_DIR=$root/windows" `
  "-DSPHOTO_SHARED=ON" "-DSPHOTO_TESTS=OFF" "-DSPHOTO_CLI=OFF"
if ($LASTEXITCODE) { throw "cmake configure failed" }
cmake --build $build --config Release
if ($LASTEXITCODE) { throw "cmake build failed" }

$out = "$PSScriptRoot/native/$Platform"
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item "$build/Release/*.dll" $out

# App-local Visual C++ runtime, so the app starts on a PC without the VC++
# Redistributable. Microsoft documents this as "local deployment":
# https://learn.microsoft.com/cpp/windows/redistributing-visual-cpp-files
$vs = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -property installationPath
$crt = Get-ChildItem "$vs/VC/Redist/MSVC/*/$($arch.ToLower())/Microsoft.VC14*.CRT" -Directory |
  Sort-Object FullName -Descending | Select-Object -First 1
if (-not $crt) { throw "Visual C++ runtime redistributable not found under $vs" }
Copy-Item "$($crt.FullName)/*.dll" $out
Write-Host "Native libraries in $out"
