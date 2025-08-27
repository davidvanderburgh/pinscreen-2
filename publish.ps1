param(
    [ValidateSet('win-x64','osx-arm64','osx-x64','linux-x64')]
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$Zip
)

Write-Host "Publishing Pinscreen2 for $Runtime ($Configuration)" -ForegroundColor Cyan

$outDir = Join-Path (Get-Location) ("publish/" + $Runtime)
Remove-Item -Recurse -Force $outDir -ErrorAction Ignore | Out-Null
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

dotnet publish .\Pinscreen2.App\Pinscreen2.App.csproj -c $Configuration -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
  -o $outDir
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed"; exit 1 }

if ($Zip) {
  $zipName = "Pinscreen2-$Runtime.zip"
  if (Test-Path $zipName) { Remove-Item $zipName -Force }
  Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipName -Force
  if ($LASTEXITCODE -ne 0) { Write-Error "Compress-Archive failed"; exit 1 }
  Write-Host "Created $zipName" -ForegroundColor Green
} else {
  Write-Host "Publish complete: $outDir" -ForegroundColor Green
}


