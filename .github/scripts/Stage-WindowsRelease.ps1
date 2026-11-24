#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $DotnetPath = 'dotnet.exe',
    [string] $ElectronDistDirectory
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).ProviderPath
$output = [IO.Path]::GetFullPath($OutputDirectory)
$bundleDirectory = Join-Path $output 'bundle'
$hostDirectory = Join-Path $output 'apphost'
$parameters = @{ OutputDirectory = $bundleDirectory; DotnetPath = $DotnetPath }
if ($ElectronDistDirectory) { $parameters.ElectronDistDirectory = $ElectronDistDirectory }
$build = & (Join-Path $root 'scripts\build-windows-package.ps1') @parameters
$hostPath = Join-Path $hostDirectory 'Xdc.NativeDesktop.exe'

if ((Get-FileHash -LiteralPath $build.Archive).Hash -ne $build.Sha256) {
    throw 'The staged ZIP hash does not match the build result.'
}
New-Item -ItemType Directory -Path $hostDirectory | Out-Null
$bundleName = [IO.Path]::GetFileNameWithoutExtension($build.Archive)
$package = [IO.Compression.ZipFile]::OpenRead($build.Archive)
try {
    $entry = $package.GetEntry("$bundleName/desktop/native-bin/Xdc.NativeDesktop.exe")
    if (-not $entry) { throw 'The staged ZIP does not contain the expected apphost.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $hostPath)
} finally {
    $package.Dispose()
}

$unsignedHash = (Get-FileHash -LiteralPath $hostPath).Hash
if ($unsignedHash -ne $build.AppHostSha256) {
    throw 'The apphost in the staged ZIP does not match the build result.'
}
if ((Get-AuthenticodeSignature -LiteralPath $hostPath).Status -ne 'NotSigned') {
    throw 'The SignPath input must be the unsigned apphost produced by this build.'
}
$metadata = (Get-Item -LiteralPath $hostPath).VersionInfo
if (-not $metadata.ProductName -or -not $metadata.ProductVersion) {
    throw 'The apphost must provide both ProductName and ProductVersion.'
}

[pscustomobject]@{
    BundleDirectory = $bundleDirectory
    Archive = $build.Archive
    ArchiveSha256 = $build.Sha256
    UnsignedAppHost = $hostPath
    UnsignedAppHostSha256 = $unsignedHash
    ProductName = $metadata.ProductName
    ProductVersion = $metadata.ProductVersion
}
