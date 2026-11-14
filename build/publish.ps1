# Builds the portable Windows release: artifacts/publish/win-x64/ and artifacts/Amberkeep-<version>-win-x64.zip.
# Usage (from any folder):  pwsh build/publish.ps1     or     powershell -File build/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Amberkeep.App/Amberkeep.App.csproj'
$output = Join-Path $root 'artifacts/publish/win-x64'
$artifacts = Join-Path $root 'artifacts'

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}

dotnet publish $project -p:PublishProfile=win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $output 'Amberkeep.exe'
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$zip = Join-Path $artifacts "Amberkeep-$version-win-x64.zip"
if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip
Write-Host ''
Get-ChildItem $output | Format-Table Name, @{ Name = 'Size (MB)'; Expression = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
Write-Host "Zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
