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
if (-not $ElectronDistDirectory) {
    $electronDirectory = Join-Path $output 'electron'
    New-Item -ItemType Directory -Path $electronDirectory | Out-Null
    $manifestPaths = 'desktop\package.json', 'desktop\package-lock.json' | ForEach-Object { Join-Path $root $_ }
    Copy-Item -LiteralPath $manifestPaths -Destination $electronDirectory
    & npm.cmd --prefix $electronDirectory ci --include=dev --ignore-scripts --no-audit --no-fund | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Installing the locked Electron package failed ($LASTEXITCODE)." }
    # Electron 42+ downloads the binary on demand rather than during npm install.
    $electronPackage = Join-Path $electronDirectory 'node_modules\electron'
    & node.exe (Join-Path $electronPackage 'install.js') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Downloading the locked Electron runtime failed ($LASTEXITCODE)." }
    $ElectronDistDirectory = Join-Path $electronPackage 'dist'
}
$productName = 'ZJU Learning Desktop'
$previousProduct = [Environment]::GetEnvironmentVariable('Product', 'Process')
try {
    # Set the PE product metadata through MSBuild without changing the project or assembly name.
    [Environment]::SetEnvironmentVariable('Product', $productName, 'Process')
    $build = & (Join-Path $root 'scripts\build-windows-package.ps1') -OutputDirectory $bundleDirectory `
        -DotnetPath $DotnetPath -ElectronDistDirectory $ElectronDistDirectory
} finally {
    [Environment]::SetEnvironmentVariable('Product', $previousProduct, 'Process')
}
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
if ($metadata.ProductName -cne $productName -or -not $metadata.ProductVersion) {
    throw 'The apphost product metadata does not match the release project.'
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
