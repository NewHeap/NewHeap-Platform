# Instellingen voor versienummering
param (
    [string]$Major = "0",
    [string]$Minor = "1",
    [string]$Patch = "0"
)

# Paths zijn relatief tot dit script. Zorg dat we in de correcte directory zitten.
cd $PSScriptRoot

# Genereer het timestamp-gedeelte (YYYYMMDD-HHMM)
$timestamp = Get-Date -Format "yyyyMMdd-HHmm"

# Bouw de complete versie string
$Version = "$Major.$Minor.$Patch-ci-$timestamp"

Write-Host "Building package with version: $Version"

$projectPaths = @(
  ".\Libraries\NewHeap.Media",
  ".\Libraries\NewHeap.Media.FileStructureStorage.SqlServer",
  ".\Libraries\NewHeap.Media.Http",
  ".\Libraries\NewHeap.Media.MediaStorage.FileSystem"
);

# Voer `dotnet pack` uit met de juiste versie

$projectPaths | ForEach-Object {
  dotnet pack "$_" -c Release /p:Version=$Version    
  # Controleer of het packen is geslaagd
  if ($LASTEXITCODE -ne 0) {
      Write-Host "Packing failed!"
      exit 1
  }
}

<#
$projectPaths | ForEach-Object {
  $packageName = (Get-Item $_).Name
  dotnet nuget push --source "https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/nuget/v3/index.json" --api-key az "$_/bin/Release/$packageName.$Version.nupkg"
  # Controleer of het packen is geslaagd
  if ($LASTEXITCODE -ne 0) {
      Write-Host "Publishing failed!"
      exit 1
  }
}

Write-Host "Package built successfully: Version $Version"
#>
