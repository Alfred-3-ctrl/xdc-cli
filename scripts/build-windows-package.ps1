#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $DotnetPath = 'dotnet.exe',
    [string] $ElectronDistDirectory,
    [string] $FrameworkDependentExe,
    [string] $DesktopRuntimeInstaller
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$output = [IO.Path]::GetFullPath($OutputDirectory)
$desktopPackage = Get-Content -Raw -LiteralPath (Join-Path $repository 'desktop\package.json') | ConvertFrom-Json
$electronLock = Join-Path $repository 'desktop\package-lock.json'
$electronVersion = & node.exe -p "JSON.parse(require('node:fs').readFileSync(process.argv[1], 'utf8')).packages['node_modules/electron'].version" $electronLock
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the locked Electron version.' }
$version = $desktopPackage.version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?$' -or $electronVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Expected a release version and a locked Electron version.'
}
if ([bool]$FrameworkDependentExe -ne [bool]$DesktopRuntimeInstaller) {
    throw 'Provide both the framework-dependent EXE and the official Windows Desktop Runtime installer.'
}
$hostMode = if ($FrameworkDependentExe) { 'framework-dependent' } else { 'self-contained' }
$bundleName = "Xdc-Learning-Desktop-$version-win-x64"
$zipPath = Join-Path $output "$bundleName.zip"
$shaPath = $zipPath + '.sha256'
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $shaPath)) {
    throw "Release files already exist in $output."
}
New-Item -ItemType Directory -Path $output -Force | Out-Null

function Invoke-BuildStep([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Executable exited with code $LASTEXITCODE." }
}

function Copy-RepoFile([string] $RelativePath, [string] $TargetRoot) {
    $source = Join-Path $repository $RelativePath
    $target = Join-Path $TargetRoot $RelativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}

function Get-LockedElectron {
    if (-not $ElectronDistDirectory) {
        $install = Join-Path $stage 'electron-install'
        New-Item -ItemType Directory -Path $install | Out-Null
        foreach ($file in 'package.json','package-lock.json') {
            Copy-Item -LiteralPath (Join-Path $repository "desktop\$file") -Destination $install
        }
        Invoke-BuildStep 'npm.cmd' @('--prefix', $install, 'ci', '--include=dev', '--no-audit', '--no-fund')
        $dist = Join-Path $install 'node_modules\electron\dist'
    } else {
        $dist = (Resolve-Path -LiteralPath $ElectronDistDirectory).ProviderPath
    }
    foreach ($file in 'electron.exe','version','LICENSE','LICENSES.chromium.html','resources.pak') {
        if (-not (Test-Path -LiteralPath (Join-Path $dist $file) -PathType Leaf)) {
            throw "Electron distribution is missing $file."
        }
    }
    $actualVersion = (Get-Content -Raw -LiteralPath (Join-Path $dist 'version')).Trim().TrimStart('v')
    if ($actualVersion -ne $electronVersion) { throw "Expected Electron $electronVersion, found $actualVersion." }
    return $dist
}

function Copy-DotnetNotices([string] $TargetRoot) {
    $sdk = Split-Path -Parent ((Get-Command $DotnetPath -CommandType Application).Source)
    $licenses = Join-Path $TargetRoot 'licenses\dotnet'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    foreach ($file in 'LICENSE.txt','ThirdPartyNotices.txt') {
        Copy-Item -LiteralPath (Join-Path $sdk $file) -Destination $licenses
    }

    $assets = Get-Content -Raw -LiteralPath (Join-Path $repository 'desktop\native\obj\project.assets.json') | ConvertFrom-Json
    $packages = $assets.packageFolders.PSObject.Properties.Name
    foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
        foreach ($dependency in $framework.downloadDependencies) {
            if ($dependency.name -notmatch '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') { continue }
            $runtimeVersion = ($dependency.version.Trim('[',']') -split ',')[0].Trim()
            $relative = Join-Path $dependency.name.ToLowerInvariant() $runtimeVersion
            foreach ($folder in $packages) {
                $pack = Join-Path $folder $relative
                if (-not (Test-Path -LiteralPath $pack -PathType Container)) { continue }
                $noticeDir = Join-Path $licenses $dependency.name
                New-Item -ItemType Directory -Path $noticeDir -Force | Out-Null
                Get-ChildItem -LiteralPath $pack -File | Where-Object Name -Match '^(LICENSE|THIRD.PARTY.NOTICES)' |
                    Copy-Item -Destination $noticeDir
                break
            }
        }
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$stage = Join-Path $tempRoot ('xdc-release-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $bundle = Join-Path $stage $bundleName
    $native = Join-Path $bundle 'desktop\native-bin'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach ($file in 'package.json','package-lock.json','desktop\auth.js','desktop\data.js','desktop\preferences.js','desktop\native-service\main.js','desktop\native-service\package.json') {
        Copy-RepoFile $file $bundle
    }
    Copy-Item -LiteralPath (Join-Path $repository 'src') -Destination $bundle -Recurse
    Invoke-BuildStep 'npm.cmd' @('--prefix', $bundle, 'ci', '--omit=dev', '--ignore-scripts', '--no-audit', '--no-fund')

    $dist = Get-LockedElectron
    $electron = Join-Path $bundle 'desktop\node_modules\electron'
    New-Item -ItemType Directory -Path $electron -Force | Out-Null
    Copy-Item -LiteralPath $dist -Destination (Join-Path $electron 'dist') -Recurse

    $runtimeStep = '本包内含 .NET 运行时。'
    if ($FrameworkDependentExe) {
        $hostPath = (Resolve-Path -LiteralPath $FrameworkDependentExe).ProviderPath
        $runtimePath = (Resolve-Path -LiteralPath $DesktopRuntimeInstaller).ProviderPath
        $signature = Get-AuthenticodeSignature -LiteralPath $runtimePath
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation(?:,|$)') {
            throw 'The Windows Desktop Runtime installer must have a valid Microsoft signature.'
        }
        Copy-Item -LiteralPath $hostPath -Destination (Join-Path $native 'Xdc.NativeDesktop.exe')
        Copy-Item -LiteralPath $runtimePath -Destination (Join-Path $bundle 'WindowsDesktopRuntime-x64.exe')
        $runtimeStep = '首次使用如未安装 .NET 10 Windows Desktop Runtime x64，先运行包内的 WindowsDesktopRuntime-x64.exe。'
    } else {
        Invoke-BuildStep $DotnetPath @(
            'publish', (Join-Path $repository 'desktop\native\NativeDesktop.csproj'), '-c', 'Release',
            '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:DebugType=embedded', '-warnaserror', '-o', $native
        )
        Copy-DotnetNotices $bundle
    }

    foreach ($file in 'LICENSE','LICENSE.txt','NOTICE','NOTICE.md') {
        $source = Join-Path $repository $file
        if (Test-Path -LiteralPath $source -PathType Leaf) { Copy-Item -LiteralPath $source -Destination $bundle }
    }
    $dotnetNotices = @'
The .NET components are distributed under the Microsoft .NET license and applicable third-party notices.
Runtime source and notices: https://github.com/dotnet/runtime
Windows Desktop components: https://github.com/dotnet/winforms
Microsoft Windows SDK interop redistribution terms: https://aka.ms/WinSDKLicenseURL
Electron and Chromium license files are under desktop\node_modules\electron\dist.
Other package licenses ship beside their code under node_modules.
Application upstream: https://github.com/d32gang-steel/zju-cli/tree/main/xzzd-cli
Classroom and WPS integration reference: https://github.com/PeiPei233/zju-learning-assistant
'@
    Set-Content -LiteralPath (Join-Path $bundle 'THIRD-PARTY-NOTICES.txt') -Value $dotnetNotices -Encoding UTF8

    $commit = (& git -C $repository rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot identify the source commit.' }
    $apphost = Join-Path $native 'Xdc.NativeDesktop.exe'
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $apphost).Hash
    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $apphost).Status.ToString()
    [ordered]@{
        version = $version
        source = $commit
        platform = 'win-x64'
        runtime = $hostMode
        electron = $electronVersion
        entryPoint = 'desktop/native-bin/Xdc.NativeDesktop.exe'
        apphostSha256 = $hash
        authenticode = $signatureStatus
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'release.json') -Encoding UTF8

    $readme = @"
学习桌面 $version — Windows x64
--------------------------------
系统要求：Windows 11 22H2 或更新版本，x64。
$runtimeStep

1. 把 ZIP 全部解压到固定位置。保留目录结构。
2. 双击 desktop\native-bin\Xdc.NativeDesktop.exe。
3. 第一次使用时在设置中登录自己的浙大账号。需要开机常驻时，在设置里开启「登录 Windows 时启动」。

此桌面包无需在目标机器安装 Node.js、npm 或 .NET SDK。账号、会话和外观配置由程序在本机生成。
如果 Windows 智能应用控制拦截程序，请使用通过可信签名的发行版。签名状态可参见 release.json。
来源 https://github.com/Alfred-3-ctrl/xdc-cli/commit/$commit
"@
    Set-Content -LiteralPath (Join-Path $bundle 'README.txt') -Value $readme -Encoding UTF8

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $stage "$bundleName.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($bundle, $archive, [IO.Compression.CompressionLevel]::Optimal, $true)
    Move-Item -LiteralPath $archive -Destination $zipPath
    $archiveHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $shaPath -Value "$archiveHash  $bundleName.zip" -Encoding ascii
    [pscustomobject]@{ Archive = $zipPath; Sha256 = $archiveHash; AppHostSha256 = $hash; ReleaseMode = $hostMode }
} finally {
    $created = (Resolve-Path -LiteralPath $stage).ProviderPath
    if ((Split-Path -Parent $created) -eq $tempRoot -and (Split-Path -Leaf $created) -match '^xdc-release-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $created -Recurse -Force
    }
}
