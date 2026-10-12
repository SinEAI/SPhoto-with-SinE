param([Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/..").Path
$publish = "$root/publish/windows-x64"
$artifacts = "$root/artifacts"
$testRoot = Join-Path $env:RUNNER_TEMP 'sphoto-release-tests'
New-Item -ItemType Directory -Force $testRoot, $artifacts | Out-Null
$env:SPHOTO_NO_CRASH_DIALOG = '1'
$env:SPHOTO_DATA_DIR = Join-Path $testRoot 'profile'

function Check-NativeExit([string]$Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed with exit code $LASTEXITCODE" }
}

function Test-App([string]$Exe, [string]$Mode, [string]$Name, [int]$Timeout) {
    $log = Join-Path $testRoot "$Name.txt"
    $process = Start-Process -FilePath $Exe -ArgumentList @($Mode, "`"$log`"") -WorkingDirectory (Split-Path $Exe) -PassThru
    if (-not $process.WaitForExit($Timeout * 1000)) {
        $process.Kill()
        throw "$Name timed out"
    }
    if (Test-Path $log) {
        python "$root/tools/check_release.py" --text-file $log
        Check-NativeExit "$Name diagnostics privacy check"
    }
    if ($process.ExitCode -ne 0) { throw "$Name failed with exit code $($process.ExitCode)" }
    if (-not (Test-Path $log) -or -not (Select-String -Path $log -Pattern '^PASSED$' -Quiet)) {
        throw "$Name did not report PASSED"
    }
}

& "$PSScriptRoot/build-native.ps1" -Platform x64
# Internal C++ test helpers are intentionally not exported by the app DLL.
# Compile the test suite against a static core, using the same dependencies.
cmake -S "$root/core" -B "$root/build/windows-tests-x64" -A x64 `
    "-DCMAKE_TOOLCHAIN_FILE=$env:VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake" `
    -DVCPKG_TARGET_TRIPLET=x64-windows "-DVCPKG_MANIFEST_DIR=$root/windows" `
    "-DVCPKG_INSTALLED_DIR=$root/build/windows-x64/vcpkg_installed" `
    -DSPHOTO_SHARED=OFF -DSPHOTO_TESTS=ON -DSPHOTO_CLI=OFF
Check-NativeExit 'native test configure'
cmake --build "$root/build/windows-tests-x64" --config Release
Check-NativeExit 'native test build'
& "$root/build/windows-tests-x64/Release/sphoto_tests.exe"
Check-NativeExit 'native tests'
dotnet test "$PSScriptRoot/SPhoto.Library.Tests" -c Release --verbosity minimal
Check-NativeExit 'library tests'
dotnet publish "$PSScriptRoot/SPhoto.Windows" -c Release -r win-x64 --self-contained true -o $publish `
    -p:Platform=x64 "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true "-p:PathMap=$root=/_/src"
Check-NativeExit 'Windows publish'

# Debug files are deliberately excluded from the distribution.
Get-ChildItem $publish -Recurse -File -Filter '*.pdb' | Remove-Item
$noticeDir = Join-Path $publish 'third-party-notices'
New-Item -ItemType Directory -Force $noticeDir | Out-Null
$licenses = Get-ChildItem "$root/build/windows-x64/vcpkg_installed/x64-windows/share/*/copyright" -File
foreach ($license in $licenses) {
    Copy-Item $license.FullName (Join-Path $noticeDir "$($license.Directory.Name).txt")
}
@'
SPhoto uses ANGLE and the .NET and Windows App SDK runtimes.
ANGLE dependency license notices are included in third-party-notices.
Microsoft runtime license terms and notices:
https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
https://github.com/microsoft/WindowsAppSDK/blob/main/LICENSE
'@ | Set-Content (Join-Path $publish 'THIRD_PARTY_NOTICES.txt') -Encoding utf8

$exe = Join-Path $publish 'SPhoto.exe'
Test-App $exe '--self-test' 'portable-self-test' 300
Test-App $exe '--ui-test' 'portable-ui-test' 540

$zip = Join-Path $artifacts "SPhoto-$Version-windows-x64.zip"
python "$root/tools/check_release.py" --directory $publish --archive-out $zip --manifest "$artifacts/release-manifest.json" --commit $env:GITHUB_SHA
Check-NativeExit 'distribution privacy check'
python "$root/tools/check_release.py" --zip $zip
Check-NativeExit 'portable archive privacy check'

$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'
if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 is not installed' }
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$artifacts" "$PSScriptRoot/installer/SPhoto.iss"
Check-NativeExit 'installer compilation'
$installer = Join-Path $artifacts "SPhoto-Setup-$Version-x64.exe"
$installed = Join-Path $testRoot 'installed'
$process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=`"$installed`"") -PassThru
if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'installer timed out' }
if ($process.ExitCode -ne 0) { throw "installer failed with exit code $($process.ExitCode)" }
Test-App (Join-Path $installed 'SPhoto.exe') '--self-test' 'installed-self-test' 300
$process = Start-Process -FilePath (Join-Path $installed 'unins000.exe') -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru
if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'uninstaller timed out' }
if ($process.ExitCode -ne 0 -or (Test-Path (Join-Path $installed 'SPhoto.exe'))) { throw 'uninstaller failed' }

python -c "import sys; sys.path.insert(0, 'tools'); from pathlib import Path; from check_release import check_files; p=Path(sys.argv[1]); check_files([(p.name,p.read_bytes())]); print('Installer privacy check passed.')" $installer
Check-NativeExit 'installer privacy check'
$hashes = @($zip, $installer, "$artifacts/release-manifest.json") | ForEach-Object {
    $hash = Get-FileHash -Algorithm SHA256 $_
    "$($hash.Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)"
}
[IO.File]::WriteAllText("$artifacts/SHA256SUMS.txt", ($hashes -join "`n") + "`n", [Text.Encoding]::ASCII)
Write-Host "Windows release $Version built, tested and privacy checked."
