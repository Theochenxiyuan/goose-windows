[CmdletBinding()]
param(
    [string]$Publisher = 'CN=GooseWindows.Dev',
    [string]$DesktopSourceDir,
    [switch]$Sign,
    [switch]$Install,
    [switch]$RestartExplorer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$launcherRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $launcherRoot '..\..'))
$artifactRoot = Join-Path $launcherRoot 'artifacts\msix'
$publishDir = Join-Path $artifactRoot 'publish'
$stageDir = Join-Path $artifactRoot 'stage'
$launcherStageDir = Join-Path $stageDir 'launcher'
$priInputDir = Join-Path $artifactRoot 'pri-input'
$launcherPriInputDir = Join-Path $priInputDir 'launcher'
$manifestTemplatePath = Join-Path $launcherRoot 'packaging\Package.appxmanifest.template'
$desktopPackagePath = Join-Path $workspaceRoot 'ui\desktop\package.json'
$desktopImagesDir = Join-Path $workspaceRoot 'ui\desktop\src\images'
if ([string]::IsNullOrWhiteSpace($DesktopSourceDir)) {
    $DesktopSourceDir = Join-Path $workspaceRoot 'ui\desktop\out\Goose-win32-x64'
}
$DesktopSourceDir = [IO.Path]::GetFullPath($DesktopSourceDir)

$desktopPackage = Get-Content -LiteralPath $desktopPackagePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($desktopPackage.version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
    throw "Desktop version is not compatible with MSIX: $($desktopPackage.version)"
}
$packageVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$(if ($Matches[4]) { $Matches[4] } else { '0' })"
$packagePath = Join-Path $artifactRoot "Goose_${packageVersion}_x64.msix"

function Assert-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $workspaceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the workspace: $resolved"
    }
}

function Reset-Directory([string]$Path) {
    Assert-WorkspacePath $Path
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE" }
}

function New-ResizedPng([string]$Source, [string]$Destination, [int]$Size) {
    Add-Type -AssemblyName System.Drawing
    $sourceImage = [Drawing.Image]::FromFile($Source)
    try {
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($sourceImage, 0, 0, $Size, $Size)
            } finally { $graphics.Dispose() }
            $bitmap.Save($Destination, [Drawing.Imaging.ImageFormat]::Png)
        } finally { $bitmap.Dispose() }
    } finally { $sourceImage.Dispose() }
}

if (-not (Test-Path -LiteralPath (Join-Path $DesktopSourceDir 'Goose.exe'))) {
    throw "Packaged Goose Desktop was not found in $DesktopSourceDir. Run pnpm run package first."
}
$desktopRuntimeDir = Join-Path $DesktopSourceDir 'resources\bin'
$requiredDesktopRuntimeFiles = @('goose.exe', 'uv.exe', 'uvx.exe', 'npx.cmd')
foreach ($runtimeFile in $requiredDesktopRuntimeFiles) {
    $runtimePath = Join-Path $desktopRuntimeDir $runtimeFile
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf) -or (Get-Item -LiteralPath $runtimePath).Length -eq 0) {
        throw "Packaged Goose runtime is incomplete: $runtimePath is missing or empty. Run pnpm run package first."
    }
}

Reset-Directory $publishDir
Reset-Directory $stageDir
Reset-Directory $priInputDir
New-Item -ItemType Directory -Path $launcherStageDir,$launcherPriInputDir -Force | Out-Null
Copy-Item -Path (Join-Path $DesktopSourceDir '*') -Destination $stageDir -Recurse -Force

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Build Tools with MSVC v143 are required to build the Explorer command.' }
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild could not be located.' }

Invoke-Checked $msbuild @(
    (Join-Path $launcherRoot 'src\GooseLauncher.ShellExtension\GooseLauncher.ShellExtension.vcxproj'),
    '/m', '/p:Configuration=Release', '/p:Platform=x64', '/verbosity:minimal'
)
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $launcherRoot 'src\GooseLauncher.App\GooseLauncher.App.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:WindowsAppSDKSelfContained=true', '-p:GooseLauncherPackaged=true', '-o', $publishDir
)
Copy-Item -Path (Join-Path $publishDir '*') -Destination $launcherStageDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $launcherRoot 'src\GooseLauncher.ShellExtension\x64\Release\GooseLauncher.ShellExtension.dll') -Destination $launcherStageDir -Force
Remove-Item -LiteralPath (Join-Path $launcherStageDir 'GooseLauncher.pdb') -Force -ErrorAction SilentlyContinue

$assets = Join-Path $stageDir 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$sourceLogo = Join-Path $desktopImagesDir 'icon-512.png'
Copy-Item -LiteralPath (Join-Path $desktopImagesDir 'icon.ico') -Destination (Join-Path $assets 'Goose.ico') -Force
New-ResizedPng $sourceLogo (Join-Path $assets 'Square44x44Logo.png') 44
New-ResizedPng $sourceLogo (Join-Path $assets 'Square150x150Logo.png') 150
New-ResizedPng $sourceLogo (Join-Path $assets 'StoreLogo.png') 50
foreach ($scale in @(125, 150, 200, 400)) {
    New-ResizedPng $sourceLogo (Join-Path $assets "Square44x44Logo.scale-$scale.png") ([Math]::Round(44 * $scale / 100))
    New-ResizedPng $sourceLogo (Join-Path $assets "Square150x150Logo.scale-$scale.png") ([Math]::Round(150 * $scale / 100))
    New-ResizedPng $sourceLogo (Join-Path $assets "StoreLogo.scale-$scale.png") ([Math]::Round(50 * $scale / 100))
}

$appIntermediate = Join-Path $launcherRoot 'src\GooseLauncher.App\obj\Release\net8.0-windows10.0.19041.0\win-x64'
$compiledXaml = @(Get-ChildItem -LiteralPath $appIntermediate -File -Filter '*.xbf')
if ($compiledXaml.Count -eq 0) { throw "No compiled XAML resources were found under $appIntermediate" }
foreach ($xbf in $compiledXaml) {
    Copy-Item -LiteralPath $xbf.FullName -Destination $launcherStageDir -Force
    Copy-Item -LiteralPath $xbf.FullName -Destination $launcherPriInputDir -Force
}
Copy-Item -LiteralPath $assets -Destination $priInputDir -Recurse -Force
Get-ChildItem -LiteralPath $launcherStageDir -File -Filter '*.pri' |
    Where-Object Name -ne 'resources.pri' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $launcherPriInputDir -Force }

$manifestTemplate = Get-Content -LiteralPath $manifestTemplatePath -Raw -Encoding UTF8
$manifest = $manifestTemplate.Replace('CN=REPLACE_WITH_SIGNING_IDENTITY', $Publisher).Replace('__GOOSE_PACKAGE_VERSION__', $packageVersion)
[IO.File]::WriteAllText((Join-Path $stageDir 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

$sdkBinRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$sdkVersion = Get-ChildItem -LiteralPath $sdkBinRoot | Where-Object { $_.PSIsContainer -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) } |
    Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdkVersion) { throw 'Windows SDK MakeAppx could not be located.' }
$makeAppx = Join-Path $sdkVersion.FullName 'x64\makeappx.exe'
$makePri = Join-Path $sdkVersion.FullName 'x64\makepri.exe'
$signTool = Join-Path $sdkVersion.FullName 'x64\signtool.exe'
$priConfig = Join-Path $artifactRoot 'priconfig.xml'
$makePriLog = Join-Path $artifactRoot 'makepri.log'
& $makePri createconfig /cf $priConfig /dq en-US /o *> $makePriLog
if ($LASTEXITCODE -ne 0) { throw "MakePri createconfig failed with exit code $LASTEXITCODE. See $makePriLog" }
& $makePri new /pr $priInputDir /cf $priConfig `
    /mn (Join-Path $stageDir 'AppxManifest.xml') `
    /of (Join-Path $stageDir 'resources.pri') /o *>> $makePriLog
if ($LASTEXITCODE -ne 0) { throw "MakePri new failed with exit code $LASTEXITCODE. See $makePriLog" }
Write-Output 'Package resources generated.'
Invoke-Checked $makeAppx @('pack', '/d', $stageDir, '/p', $packagePath, '/o', '/h', 'SHA256')

if ($Install) { $Sign = $true }
if ($Sign) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate -Type Custom -Subject $Publisher -FriendlyName 'Goose Windows development signing' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(3) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    Invoke-Checked $signTool @('sign', '/fd', 'SHA256', '/s', 'My', '/sha1', $certificate.Thumbprint, $packagePath)
}

if ($Install) {
    $certificateFile = Join-Path $artifactRoot 'GooseWindows.Dev.cer'
    Export-Certificate -Cert $certificate -FilePath $certificateFile -Force | Out-Null
    if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
        $escapedCertificatePath = $certificateFile.Replace("'", "''")
        $elevatedCommand = "Import-Certificate -FilePath '$escapedCertificatePath' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null"
        $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevatedCommand))
        $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encodedCommand -WindowStyle Hidden -Wait -PassThru
        if ($elevated.ExitCode -ne 0) { throw "Development certificate installation failed with exit code $($elevated.ExitCode)" }
    }
    Get-Process -Name 'GooseLauncher' -ErrorAction SilentlyContinue | Stop-Process -Force
    foreach ($packageName in @('GooseWindows', 'GooseLauncher')) {
        Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage
    }
    Add-AppxPackage -Path $packagePath
    if ($RestartExplorer) {
        Get-Process -Name explorer -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Process explorer.exe
    }
}

Write-Output "MSIX: $packagePath"
