# Instellingen voor versienummering
param (
    [string]$Major = "0",
    [string]$Minor = "1",
    [string]$Patch = "0"
)


function PackAndPublish {
  param (
    [string[]]$ProjectPaths,
    [string]$Version    
  )

  $ProjectPaths | ForEach-Object {
    $packageName = (Get-Item $_).Name  
    Write-Host "dotnet pack "$_\$packageName.csproj" -c Release /p:Version=$Version"
    dotnet pack "$_\$packageName.csproj" -c Release /p:Version=$Version    
    # Controleer of het packen is geslaagd
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Packing failed!"
        exit 1
    }
  }

  $ProjectPaths | ForEach-Object {
    $packageName = (Get-Item $_).Name

    Write-Host "dotnet nuget push --source ""https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/nuget/v3/index.json"" --interactive --api-key az ""$_/bin/Release/$packageName.$Version.nupkg"""

    dotnet nuget push --source "https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/nuget/v3/index.json" --api-key az "$_/bin/Release/$packageName.$Version.nupkg"
    # Controleer of het packen is geslaagd
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Publishing failed!"
        exit 1
    }
  }

}

# Paths zijn relatief tot dit script. Zorg dat we in de correcte directory zitten.
cd $PSScriptRoot

# Genereer het timestamp-gedeelte (YYYYMMDD-HHMM)
$timestamp = Get-Date -Format "yyyyMMdd-HHmm"

# Bouw de complete versie string
$Version = "$Major.$Minor.$Patch-ci-$timestamp"

Write-Host "Building package with version: $Version"

$projectPaths = @(  
  ".\Libraries\NewHeap.Platform.AspNet.Caching"
);


PackAndPublish $projectPaths $Version

Write-Host "Package built successfully: Version $Version"
