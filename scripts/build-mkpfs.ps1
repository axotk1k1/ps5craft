# Builds a standalone console mkpfs.exe (no Python needed at runtime) from _refs\MkPFS
# and places it in tools\mkpfs. MkPFS is GPL-3.0; its LICENSE is copied next to the exe.
param(
    [string]$Python = "",
    [string]$Source = "$PSScriptRoot\..\_refs\MkPFS"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$work = Join-Path $root "_build\mkpfs"
$venv = Join-Path $work "venv"
$out = Join-Path $root "tools\mkpfs"

if (-not $Python) {
    $Python = (& py -3 -c "import sys; print(sys.executable)").Trim()
}

New-Item -ItemType Directory -Force -Path $work, $out | Out-Null
if (-not (Test-Path "$venv\Scripts\python.exe")) {
    & $Python -m venv $venv
}
$vpy = "$venv\Scripts\python.exe"
& $vpy -m pip install --disable-pip-version-check -q --upgrade pip
& $vpy -m pip install --disable-pip-version-check -q pyinstaller (Resolve-Path $Source).Path
if ($LASTEXITCODE -ne 0) { throw "pip install failed" }

& $vpy -m PyInstaller --noconfirm --clean --onefile --console `
    --name mkpfs `
    --distpath $out `
    --workpath (Join-Path $work "build") `
    --specpath $work `
    --collect-submodules mkpfs `
    --collect-all zlib_ng --collect-all isal `
    --hidden-import zlib_ng.zlib_ng --hidden-import isal.isal_zlib `
    --exclude-module customtkinter --exclude-module tkinter --exclude-module PIL `
    (Join-Path $PSScriptRoot "mkpfs_entry.py")
if ($LASTEXITCODE -ne 0) { throw "PyInstaller failed" }

Copy-Item (Join-Path $Source "LICENSE") (Join-Path $out "LICENSE-MkPFS.txt") -Force
& (Join-Path $out "mkpfs.exe") -V
