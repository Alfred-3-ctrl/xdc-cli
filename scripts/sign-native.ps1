#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Verify')]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]] $Artifact,

    [Parameter(ParameterSetName = 'Verify')]
    [switch] $VerifyOnly,

    [Parameter(Mandatory, ParameterSetName = 'Store')]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Thumbprint,

    [Parameter(ParameterSetName = 'Store')]
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string] $StoreLocation = 'CurrentUser',

    [Parameter(Mandatory, ParameterSetName = 'ArtifactSigning')]
    [string] $ArtifactSigningDlib,

    [Parameter(Mandatory, ParameterSetName = 'ArtifactSigning')]
    [string] $ArtifactSigningMetadata,

    [Parameter(ParameterSetName = 'Store')]
    [Parameter(ParameterSetName = 'ArtifactSigning')]
    [uri] $TimestampUrl = 'http://timestamp.acs.microsoft.com/',

    [string] $SignToolPath
)

$ErrorActionPreference = 'Stop'

function Get-RequiredFile([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label not found at $Path."
    }
    (Resolve-Path -LiteralPath $Path).ProviderPath
}

function Find-SignTool {
    if ($SignToolPath) {
        return Get-RequiredFile $SignToolPath 'SignTool'
    }

    $command = Get-Command -Name 'signtool.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }

    $sdkBin = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $sdkBin -PathType Container) {
        $versions = Get-ChildItem -LiteralPath $sdkBin -Directory |
            Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
            Sort-Object { [version] $_.Name } -Descending
        foreach ($version in $versions) {
            $candidate = Join-Path $version.FullName 'x64\signtool.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
        }
    }
    throw 'SignTool is unavailable. Install Windows SDK signing tools or provide -SignToolPath.'
}

function Invoke-SignTool([string[]] $ToolArguments) {
    & $signTool @ToolArguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool $($ToolArguments[0]) failed or warned (exit $LASTEXITCODE)."
    }
}

function Confirm-ArtifactSignature([string] $Path) {
    Invoke-SignTool @('verify', '/pa', '/all', '/tw', '/v', $Path)
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        -not $signature.SignerCertificate -or -not $signature.TimeStamperCertificate) {
        throw "A valid embedded timestamped Authenticode signature is required for $Path."
    }
    if ($Thumbprint -and $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "The embedded signature on $Path has an unexpected signing certificate."
    }
}

$artifacts = @($Artifact | ForEach-Object { Get-RequiredFile $_ 'Native build output' } | Sort-Object -Unique)
if ($artifacts.Where({ [IO.Path]::GetExtension($_) -ne '.exe' }).Count) {
    throw 'Select native EXE build outputs.'
}
$artifactNames = @($artifacts | ForEach-Object { [IO.Path]::GetFileName($_) })
$outputDirectories = @($artifacts | ForEach-Object { Split-Path -Parent $_ } | Sort-Object -Unique)
if ($outputDirectories.Count -ne 1) {
    throw 'Select outputs in one directory per invocation.'
}

$mode = $PSCmdlet.ParameterSetName
if ($mode -ne 'Verify' -and (-not $TimestampUrl.IsAbsoluteUri -or $TimestampUrl.Scheme -notin 'http', 'https')) {
    throw 'Provide an absolute HTTP or HTTPS RFC 3161 timestamp URL.'
}

$signArgs = @()
if ($mode -eq 'Store') {
    $certificate = Get-Item -LiteralPath "Cert:\$StoreLocation\My\$Thumbprint" -ErrorAction SilentlyContinue
    if (-not $certificate -or -not $certificate.HasPrivateKey) {
        throw "No accessible signing key for that thumbprint in $StoreLocation\My."
    }
    if ($certificate.Subject -eq $certificate.Issuer) {
        throw 'A trusted CA-issued code signing certificate is required. A self-issued certificate is unsuitable.'
    }
    $signArgs = @('/sha1', $Thumbprint, '/s', 'My')
    if ($StoreLocation -eq 'LocalMachine') { $signArgs += '/sm' }
}
elseif ($mode -eq 'ArtifactSigning') {
    $dlib = Get-RequiredFile $ArtifactSigningDlib 'Artifact Signing dlib'
    $metadata = Get-RequiredFile $ArtifactSigningMetadata 'Artifact Signing metadata'
    $signArgs = @('/dlib', $dlib, '/dmdf', $metadata)
}

$signTool = Find-SignTool
if ($mode -eq 'Verify') {
    foreach ($artifact in $artifacts) { Confirm-ArtifactSignature $artifact }
    return
}

# Sign and verify staged copies before replacing any selected build output.
$stageDirectory = Join-Path (Split-Path -Parent $artifacts[0]) ('.signing-' + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $stageDirectory)
$published = @()
$keepBackups = $false
try {
    foreach ($artifact in $artifacts) {
        $staged = Join-Path $stageDirectory (Split-Path -Leaf $artifact)
        Copy-Item -LiteralPath $artifact -Destination $staged
        Invoke-SignTool (@('sign', '/v', '/fd', 'SHA256', '/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256') + $signArgs + @($staged))
        Confirm-ArtifactSignature $staged
    }

    foreach ($artifact in $artifacts) {
        $staged = Join-Path $stageDirectory (Split-Path -Leaf $artifact)
        [IO.File]::Replace($staged, $artifact, "$staged.original")
        $published += $artifact
    }
}
catch {
    foreach ($artifact in $published) {
        $name = Split-Path -Leaf $artifact
        $backup = Join-Path $stageDirectory "$name.original"
        try {
            [IO.File]::Replace($backup, $artifact, (Join-Path $stageDirectory "$name.rollback"))
        }
        catch {
            $keepBackups = $true
            Write-Warning "Could not restore $artifact. Backups are available at $stageDirectory."
        }
    }
    throw
}
finally {
    if (-not $keepBackups) {
        foreach ($name in $artifactNames) {
            foreach ($suffix in '', '.original', '.rollback') {
                [IO.File]::Delete((Join-Path $stageDirectory "$name$suffix"))
            }
        }
        [IO.Directory]::Delete($stageDirectory)
    }
}
