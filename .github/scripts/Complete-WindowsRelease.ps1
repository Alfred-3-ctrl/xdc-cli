#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $StagedBundle,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $SignedAppHost,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$bundle = Get-Item -LiteralPath $StagedBundle
$signed = Get-Item -LiteralPath $SignedAppHost
if (-not $bundle.PSIsContainer -or $bundle.PSProvider.Name -ne 'FileSystem') {
    throw 'StagedBundle must be a Windows release directory.'
}
if ($signed.PSIsContainer -or $signed.PSProvider.Name -ne 'FileSystem' -or $signed.Name -ne 'Xdc.NativeDesktop.exe') {
    throw 'SignedAppHost must be the returned Xdc.NativeDesktop.exe.'
}

$manifestPath = Join-Path $bundle.FullName 'release.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$relativeHost = 'desktop/native-bin/Xdc.NativeDesktop.exe'
if ($manifest.version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$' -or
    $manifest.platform -cne 'win-x64' -or $manifest.entryPoint -cne $relativeHost -or
    $manifest.runtime -notin 'self-contained', 'framework-dependent' -or
    $manifest.apphostSha256 -notmatch '^[a-fA-F0-9]{64}$' -or -not $manifest.authenticode) {
    throw 'Staged release.json does not match the Windows package format.'
}
$originalHost = Join-Path $bundle.FullName $relativeHost
$originalHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $originalHost).Hash
if ($originalHash -ne $manifest.apphostSha256) {
    throw 'The staged apphost SHA256 does not match release.json.'
}

$originalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($originalHost)
$signedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($signed.FullName)
if (-not $originalVersion.ProductName -or -not $originalVersion.ProductVersion -or
    $originalVersion.ProductName -cne $signedVersion.ProductName -or
    $originalVersion.ProductVersion -cne $signedVersion.ProductVersion) {
    throw 'The signed apphost product name or product version differs from the staged apphost.'
}
$signedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $signed.FullName).Hash
$signature = Get-AuthenticodeSignature -LiteralPath $signed.FullName
if ($signature.Status -ne 'Valid') {
    throw "The returned apphost has no valid Authenticode signature ($($signature.Status))."
}
$verifier = Join-Path $PSScriptRoot '..\..\scripts\sign-native.ps1'
& $verifier -Artifact $signed.FullName -VerifyOnly

$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$output = [IO.Path]::GetFullPath($output)
$separator = [IO.Path]::DirectorySeparatorChar
$bundlePath = $bundle.FullName.TrimEnd($separator)
if ($output.TrimEnd($separator) -eq $bundlePath -or
    $output.StartsWith($bundlePath + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an output directory outside the original staging bundle.'
}
$bundleName = "Xdc-Learning-Desktop-$($manifest.version)-win-x64"
$archiveName = "$bundleName.zip"
$zipPath = Join-Path $output $archiveName
$shaPath = $zipPath + '.sha256'
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $shaPath)) {
    throw "Signed release files already exist in $output."
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
$stageName = '.xdc-signed-release-' + [guid]::NewGuid().ToString('N')
$stage = Join-Path $output $stageName
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $copy = Join-Path $stage $bundleName
    Copy-Item -LiteralPath $bundle.FullName -Destination $copy -Recurse -Force
    $copyHost = Join-Path $copy $relativeHost
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $copyHost).Hash -ne $originalHash) {
        throw 'The staged apphost changed while the bundle was being copied.'
    }
    Copy-Item -LiteralPath $signed.FullName -Destination $copyHost -Force
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $copyHost).Hash -ne $signedHash) {
        throw 'The returned apphost changed during signature verification or copying.'
    }
    $manifest.apphostSha256 = $signedHash
    $manifest.authenticode = $signature.Status.ToString()
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $copy 'release.json') -Encoding UTF8

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporaryZip = Join-Path $stage $archiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($copy, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        $hostEntries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq "$bundleName/$relativeHost" })
        $manifestEntries = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq "$bundleName/release.json" })
        if ($hostEntries.Count -ne 1 -or $manifestEntries.Count -ne 1) {
            throw 'The signed ZIP does not contain the expected apphost and release manifest.'
        }
        $hostBytes = $hostEntries[0].Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $readbackHash = [BitConverter]::ToString($hasher.ComputeHash($hostBytes)).Replace('-', '') }
        finally {
            $hasher.Dispose()
            $hostBytes.Dispose()
        }
        $manifestReader = New-Object IO.StreamReader($manifestEntries[0].Open())
        try { $readback = $manifestReader.ReadToEnd() | ConvertFrom-Json }
        finally { $manifestReader.Dispose() }
        if ($readbackHash -ne $signedHash -or $readback.apphostSha256 -ne $signedHash -or
            $readback.authenticode -cne 'Valid') {
            throw 'The signed ZIP does not contain the verified apphost and its expected metadata.'
        }
    }
    finally { $archive.Dispose() }

    $archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryZip).Hash.ToLowerInvariant()
    $temporarySha = $temporaryZip + '.sha256'
    $checksum = "$archiveHash  $archiveName"
    Set-Content -LiteralPath $temporarySha -Value $checksum -Encoding ascii
    [IO.File]::Move($temporarySha, $shaPath)
    try { [IO.File]::Move($temporaryZip, $zipPath) }
    catch {
        if ((Test-Path -LiteralPath $shaPath -PathType Leaf) -and
            (Get-Content -Raw -LiteralPath $shaPath).TrimEnd() -ceq $checksum) {
            [IO.File]::Delete($shaPath)
        }
        throw
    }
    [pscustomobject]@{
        Archive = $zipPath
        Sha256 = $archiveHash
        AppHostSha256 = $signedHash
        ReleaseMode = $manifest.runtime
    }
}
finally {
    $created = (Resolve-Path -LiteralPath $stage).ProviderPath
    if ((Split-Path -Parent $created).TrimEnd($separator) -eq $output.TrimEnd($separator) -and
        (Split-Path -Leaf $created) -ceq $stageName) {
        Remove-Item -LiteralPath $created -Recurse -Force
    }
}
