# Instellingen voor versienummering
param (
    [string]$Major = "1",
    [string]$Minor = "0",
    [string]$Patch = "0"
)

# Genereer het timestamp-gedeelte (YYYYMMDD-HHMM)
$timestamp = Get-Date -Format "yyyyMMdd-HHmm"

# Bouw de complete versie string
$Version = "$Major.$Minor.$Patch-ci-$timestamp"

Write-Host "Building package with version: $Version"

# Voer `dotnet pack` uit met de juiste versie
dotnet pack .\Libraries\NewHeap.Platform.Common -c Release /p:Version=$Version -Symbols -SymbolPackageFormat snupkg
dotnet pack .\Libraries\NewHeap.Platform.AspNet.Common -c Release /p:Version=$Version -Symbols -SymbolPackageFormat snupkg

# Controleer of het packen is geslaagd
if ($LASTEXITCODE -ne 0) {
    Write-Host "Packing failed!"
    exit 1
}

Write-Host "Package built successfully: Version $Version"

dotnet nuget push --source "https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/nuget/v3/index.json" --api-key az ./Libraries/NewHeap.Platform.Common/bin/Release/NewHeap.Platform.Common.$Version.nupkg

dotnet nuget push --source "https://pkgs.dev.azure.com/NewHeap/NewHeap-Platform/_packaging/NewHeap-Platform/nuget/v3/index.json" --api-key az ./Libraries/NewHeap.Platform.AspNet.Common/bin/Release/NewHeap.Platform.AspNet.Common.$Version.nupkg

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publishing failed!"
    exit 1
}

Write-Host "Published successfully: Version $Version"