# Used by build-msix.bat (mode "test"): returns the thumbprint of a self-signed
# code signing certificate whose subject equals the Publisher in
# Package.appxmanifest. Created once in Cert:\CurrentUser\My; the public part is
# exported to AppPackages\Hometray-Test.cer (import it into "Vertrauenswuerdige
# Personen" of the local computer to install the test package).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$publisher = ([xml](Get-Content -Raw 'Hometray\Package.appxmanifest')).Package.Identity.Publisher
$friendlyName = 'Hometray MSIX Test'

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.FriendlyName -eq $friendlyName -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName $friendlyName -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}

New-Item -ItemType Directory -Force AppPackages | Out-Null
Export-Certificate -Cert $cert -FilePath AppPackages\Hometray-Test.cer | Out-Null
$cert.Thumbprint
