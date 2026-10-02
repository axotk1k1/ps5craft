# Publishes a slim Windows x64 package (framework-dependent, win-x64 natives only).
# Requires: .NET 10 Desktop Runtime on the target PC.
param(
    [string]$Configuration = "Release",
    [string]$Output = "$PSScriptRoot\..\publish\win-x64",
    [string]$ZipPath = "$PSScriptRoot\..\PS5Craft-win-x64.zip",
    # Optional semantic version (e.g. 1.2.0). When set, overrides Directory.Build.props
    # so the GitHub tag and the baked-in app version stay in sync for the updater.
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$Output = [IO.Path]::GetFullPath($Output)
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
$updaterOut = Join-Path $root "publish\updater"

$versionArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be MAJOR.MINOR.PATCH, got: $Version"
    }
    Write-Host "==> Stamping assembly version $Version"
    $versionArgs = @(
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version.0",
        "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version"
    )
}

function Remove-ForeignRuntimes([string]$baseDir) {
    $runtimeRoots = @(
        (Join-Path $baseDir "runtimes"),
        (Join-Path $baseDir "tools\fpkg\runtimes")
    )
    foreach ($rr in $runtimeRoots) {
        if (-not (Test-Path $rr)) { continue }
        Get-ChildItem $rr -Directory | Where-Object { $_.Name -ne "win-x64" } | ForEach-Object {
            Write-Host "Removing $($_.FullName)"
            Remove-Item $_.FullName -Recurse -Force
        }
    }
}

Write-Host "==> Slim tools\fpkg source (keep win-x64 Magick only)"
Remove-ForeignRuntimes (Join-Path $root "tools\fpkg")

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
if (Test-Path $updaterOut) { Remove-Item $updaterOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Output, $updaterOut | Out-Null

Write-Host "==> Publish PS5Craft (framework-dependent win-x64)"
dotnet publish (Join-Path $root "src\PS5Craft.App\PS5Craft.App.csproj") `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishReadyToRun=true `
    @versionArgs `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "publish app failed" }

Write-Host "==> Publish updater (framework-dependent, tiny)"
dotnet publish (Join-Path $root "src\PS5Craft.Updater\PS5Craft.Updater.csproj") `
    -c $Configuration -r win-x64 --self-contained false `
    @versionArgs `
    -o $updaterOut
if ($LASTEXITCODE -ne 0) { throw "publish updater failed" }

Copy-Item (Join-Path $updaterOut "PS5Craft.Updater.exe") $Output -Force
Copy-Item (Join-Path $updaterOut "PS5Craft.Updater.dll") $Output -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $updaterOut "PS5Craft.Updater.deps.json") $Output -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $updaterOut "PS5Craft.Updater.runtimeconfig.json") $Output -Force -ErrorAction SilentlyContinue

Write-Host "==> Trim non-Windows Magick / RID payloads"
Remove-ForeignRuntimes $Output

$total = [math]::Round(((Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Write-Host "Publish folder: $total MB -> $Output"

if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path (Join-Path $Output "*") -DestinationPath $ZipPath -CompressionLevel Optimal
$hash = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path ($ZipPath + ".sha256") -Value "$hash  $([IO.Path]::GetFileName($ZipPath))" -NoNewline
$zipMb = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
Write-Host "ZIP: $zipMb MB -> $ZipPath"
Write-Host "SHA256: $hash"
Write-Host ""
Write-Host "NOTE: Users need .NET 10 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/10.0"
