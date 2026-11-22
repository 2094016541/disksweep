param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$packDir = Join-Path $root ".workbuddy\pack"
$appPub = Join-Path $packDir "app-publish"
$payload = Join-Path $packDir "payload.zip"
$outDir = Join-Path $root "artifacts"
$setupProj = Join-Path $root "tools\DiskSweep.Setup\DiskSweep.Setup.csproj"

New-Item -ItemType Directory -Force -Path $packDir | Out-Null
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "[1/4] Publishing application (self-contained win-x64)..."
dotnet publish (Join-Path $root "src\DiskSweep.App\DiskSweep.App.csproj") -c $Configuration -r win-x64 --self-contained true -o $appPub
if ($LASTEXITCODE -ne 0) { throw "Application publish failed." }

# The optional AI provider is copied by a build target but can be skipped in RID-specific publish.
$provider = Join-Path $root "src\DiskSweep.Ai.Providers\bin\$Configuration\net10.0\DiskSweep.Ai.Providers.dll"
if ((Test-Path $provider) -and -not (Test-Path (Join-Path $appPub "DiskSweep.Ai.Providers.dll"))) {
    Copy-Item $provider $appPub -Force
    Write-Host "      Added optional DiskSweep.Ai.Providers.dll"
}

# WriteRulePackSignature writes to $(TargetDir) (bin\), not the publish folder.
$sigSource = Join-Path $root "src\DiskSweep.App\bin\$Configuration\net10.0-windows\win-x64\rules\builtin-win.json.sha256"
$sigTarget = Join-Path $appPub "rules\builtin-win.json.sha256"
if ((Test-Path $sigSource) -and -not (Test-Path $sigTarget)) {
    Copy-Item $sigSource $sigTarget -Force
    Write-Host "      Added rules\builtin-win.json.sha256"
}

Write-Host "[2/4] Compressing payload..."
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path (Join-Path $appPub "*") -DestinationPath $payload -CompressionLevel Optimal

Write-Host "[3/4] Publishing single-file installer..."
dotnet publish $setupProj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PayloadZip="$payload" -o $outDir
if ($LASTEXITCODE -ne 0) { throw "Installer publish failed." }

$exe = Join-Path $outDir "DiskSweep.Setup.exe"
$final = Join-Path $outDir "DiskSweep-Setup.exe"
if (Test-Path $final) { Remove-Item $final -Force }
Rename-Item -Path $exe -NewName "DiskSweep-Setup.exe"

Write-Host "[4/4] Done."
Get-Item $final | Select-Object FullName, @{ Name = "SizeMB"; Expression = { [math]::Round($_.Length / 1MB, 1) } }
