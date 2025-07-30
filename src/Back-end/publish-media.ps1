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
    
    Write-Host "dotnet restore "$_\$packageName.csproj" --no-cache"
    dotnet restore "$_\$packageName.csproj" --no-cache
    
    Write-Host "dotnet pack "$_\$packageName.csproj" -c Release /p:Version=$Version"
    dotnet pack "$_\$packageName.csproj" -c Release /p:Version=$Version --include-symbols
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
    dotnet nuget push --source "https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/nuget/v3/index.json" --api-key az "$_/bin/Release/$packageName.$Version.snupkg"
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
  ".\Libraries\NewHeap.Platform.Media.Core"
  ".\Libraries\NewHeap.Platform.Media.FileStructureStorage.SqlServer"
  ,".\Libraries\NewHeap.Platform.Media.Http"
  ,".\Libraries\NewHeap.Platform.Media.MediaStorage.FileSystem"  
);


PackAndPublish $projectPaths $Version

$mediaProjectFile = Join-Path -Path $PSScriptRoot -ChildPath ".\Libraries\NewHeap.Platform.Media\NewHeap.Platform.Media.csproj"
$xml = [xml](Get-Content -Path $mediaProjectFile)

$xml.Project.ItemGroup.PackageReference | where {$_.Include.StartsWith("NewHeap.Platform.Media.")} | ForEach {
  $_.Version = $Version
}

$xml.Save($mediaProjectFile)

#Write-Host "";
#Write-Host "Core packages published. Waiting 10 seconds before building and publishing bundle package so it's dependencies can be resolved."
#Write-Host "";

#Start-Sleep -Seconds 10

$projectPaths = @(".\Libraries\NewHeap.Platform.Media")

PackAndPublish $projectPaths $Version

Write-Host "Package built successfully: Version $Version"
