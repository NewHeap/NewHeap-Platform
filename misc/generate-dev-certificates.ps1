# Array met domeinnamen
$domeinen = @(
    "opg.local"
)

# Output directory (folder waarin het script wordt uitgevoerd)
$outputDir = "$PSScriptRoot\dev-certificates"

# Controleer of de output directory bestaat, maak anders de map aan
if (-not (Test-Path -Path $outputDir)) {
    New-Item -Path $outputDir -ItemType Directory
}

# Loop door elke domeinnaam in de array
foreach ($domein in $domeinen) {
    # Vervang punten in de domeinnaam door lage streepjes voor de foldernaam
    $folderName = $domein -replace "\.", "_"
    $domainDir = "$outputDir\$folderName"

    # Maak een subfolder aan voor de huidige domeinnaam
    if (-not (Test-Path -Path $domainDir)) {
        New-Item -Path $domainDir -ItemType Directory
    }

    # Bestandspaden voor de key en certificaat
    $privateKey = "$domainDir\cert.key"
    $csrFile = "$domainDir\cert.csr"
    $certFile = "$domainDir\cert.crt"

    # Wildcard domein (bijv. *.example.com)
    $wildcardDomain = "*.$domein"

    # OpenSSL commando's om de key, CSR en het certificaat te genereren
    Write-Host "Genereren van wildcard certificaat voor $wildcardDomain..."

    # Genereer een private key
    openssl genrsa -out $privateKey 2048

    # Genereer een Certificate Signing Request (CSR) voor het wildcard domein
    openssl req -new -key $privateKey -out $csrFile -subj "/CN=$wildcardDomain"

    # Genereer een zelfondertekend wildcard certificaat dat 10 jaar geldig is
    openssl x509 -req -in $csrFile -signkey $privateKey -out $certFile -days 3650

    Write-Host "Wildcard certificaat gegenereerd voor $wildcardDomain en opgeslagen in $domainDir"
}

Write-Host "Alle wildcard certificaten zijn succesvol gegenereerd en opgeslagen in de 'certificates' map."