[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [Parameter(Mandatory)]
    [string]$ExpectedProductVersion
)

$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Path
if ($file.PSIsContainer) { throw 'Expected an executable file.' }
if ($file.VersionInfo.ProductName -ne 'Limen' -or
    $file.VersionInfo.ProductVersion -ne $ExpectedProductVersion) {
    throw 'Signed executable metadata does not match the build.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate) {
    throw "Release signature is not trusted: $($signature.Status)."
}
if ($null -eq $signature.TimeStamperCertificate) {
    throw 'Release signature has no timestamp.'
}
Write-Output "Verified publisher: $($signature.SignerCertificate.Subject)"
