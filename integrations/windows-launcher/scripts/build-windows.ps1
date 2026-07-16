[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Full')]
    [string]$Mode = 'Quick',
    [switch]$Sign,
    [switch]$Install,
    [switch]$RestartExplorer,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RestartExplorer -and -not $Install) {
    throw '-RestartExplorer requires -Install.'
}

$launcherRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $launcherRoot '..\..'))
$cargoBinary = Join-Path $workspaceRoot 'target\release\goose.exe'
$desktopRoot = Join-Path $workspaceRoot 'ui\desktop'
$desktopCli = Join-Path $desktopRoot 'src\bin\goose.exe'
$desktopPackageRoot = Join-Path $desktopRoot 'out\Goose-win32-x64'
$desktopPackageMarker = Join-Path $desktopRoot 'out\.goose-windows-build'
$msixScript = Join-Path $PSScriptRoot 'build-msix.ps1'

function Invoke-Checked([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Get-ApplicationPath(
    [string]$Name,
    [string]$InstallHint,
    [string[]]$AdditionalCandidates = @()
) {
    $command = Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($command) {
        return $command.Source
    }
    foreach ($candidate in $AdditionalCandidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw "$Name was not found. $InstallHint"
}

function Get-FirstExistingFile([string[]]$Candidates, [string]$ErrorMessage) {
    foreach ($candidate in $Candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    throw $ErrorMessage
}

function Add-ExecutableDirectoryToPath([string]$Executable) {
    $directory = [IO.Path]::GetFullPath((Split-Path -Parent $Executable))
    $pathDirectories = [Environment]::GetEnvironmentVariable('Path', 'Process') -split ';'
    if ($pathDirectories -contains $directory) {
        return
    }
    $env:Path = "$directory;$($env:Path)"
}

function Test-FilesEqual([string]$First, [string]$Second) {
    if (-not (Test-Path -LiteralPath $First -PathType Leaf) -or
        -not (Test-Path -LiteralPath $Second -PathType Leaf)) {
        return $false
    }
    if ((Get-Item -LiteralPath $First).Length -ne (Get-Item -LiteralPath $Second).Length) {
        return $false
    }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $First).Hash -eq
        (Get-FileHash -Algorithm SHA256 -LiteralPath $Second).Hash
}

function Test-CliBuildRequired {
    if ($Mode -eq 'Full' -or -not (Test-Path -LiteralPath $cargoBinary -PathType Leaf)) {
        return $true
    }

    $outputTime = (Get-Item -LiteralPath $cargoBinary).LastWriteTimeUtc
    $rootInputs = @(
        'Cargo.toml',
        'Cargo.lock',
        'rust-toolchain',
        'rust-toolchain.toml'
    ) | ForEach-Object { Join-Path $workspaceRoot $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

    foreach ($input in $rootInputs) {
        if ((Get-Item -LiteralPath $input).LastWriteTimeUtc -gt $outputTime) {
            return $true
        }
    }

    $cargoConfigRoot = Join-Path $workspaceRoot '.cargo'
    if (Test-Path -LiteralPath $cargoConfigRoot -PathType Container) {
        foreach ($input in Get-ChildItem -LiteralPath $cargoConfigRoot -Recurse -File) {
            if ($input.LastWriteTimeUtc -gt $outputTime) {
                return $true
            }
        }
    }

    $cratesRoot = Join-Path $workspaceRoot 'crates'
    foreach ($input in Get-ChildItem -LiteralPath $cratesRoot -Recurse -File) {
        if (($input.Extension -eq '.rs' -or $input.Name -eq 'Cargo.toml' -or $input.Name -eq 'build.rs') -and
            $input.LastWriteTimeUtc -gt $outputTime) {
            return $true
        }
    }
    return $false
}

function Test-DesktopPackageRequired([bool]$CliWillChange) {
    if ($Mode -eq 'Full' -or $CliWillChange) {
        return $true
    }

    $requiredFiles = @(
        (Join-Path $desktopPackageRoot 'Goose.exe'),
        (Join-Path $desktopPackageRoot 'resources\bin\goose.exe'),
        (Join-Path $desktopPackageRoot 'resources\bin\uv.exe'),
        (Join-Path $desktopPackageRoot 'resources\bin\uvx.exe'),
        (Join-Path $desktopPackageRoot 'resources\bin\npx.cmd')
    )
    foreach ($requiredFile in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf) -or
            (Get-Item -LiteralPath $requiredFile).Length -eq 0) {
            return $true
        }
    }

    if (-not (Test-FilesEqual $desktopCli (Join-Path $desktopPackageRoot 'resources\bin\goose.exe'))) {
        return $true
    }

    $referencePath = if (Test-Path -LiteralPath $desktopPackageMarker -PathType Leaf) {
        $desktopPackageMarker
    } else {
        Join-Path $desktopPackageRoot 'Goose.exe'
    }
    $outputTime = (Get-Item -LiteralPath $referencePath).LastWriteTimeUtc
    $inputDirectories = @(
        (Join-Path $desktopRoot 'src'),
        (Join-Path $desktopRoot 'scripts'),
        (Join-Path $workspaceRoot 'ui\sdk\src'),
        (Join-Path $workspaceRoot 'ui\sdk\scripts')
    )
    foreach ($inputDirectory in $inputDirectories) {
        if (-not (Test-Path -LiteralPath $inputDirectory -PathType Container)) {
            continue
        }
        foreach ($input in Get-ChildItem -LiteralPath $inputDirectory -Recurse -File) {
            if ($input.LastWriteTimeUtc -gt $outputTime) {
                return $true
            }
        }
    }

    $topLevelInputs = @(
        (Join-Path $desktopRoot 'package.json'),
        (Join-Path $desktopRoot 'forge.config.ts'),
        (Join-Path $desktopRoot 'vite.main.config.ts'),
        (Join-Path $desktopRoot 'vite.preload.config.ts'),
        (Join-Path $desktopRoot 'vite.renderer.config.mts'),
        (Join-Path $workspaceRoot 'ui\package.json'),
        (Join-Path $workspaceRoot 'ui\pnpm-lock.yaml'),
        (Join-Path $workspaceRoot 'ui\pnpm-workspace.yaml'),
        (Join-Path $workspaceRoot 'ui\sdk\package.json')
    )
    foreach ($input in $topLevelInputs) {
        if ((Test-Path -LiteralPath $input -PathType Leaf) -and
            (Get-Item -LiteralPath $input).LastWriteTimeUtc -gt $outputTime) {
            return $true
        }
    }
    return $false
}

function Initialize-RustBuildEnvironment {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw 'Visual Studio Build Tools with Desktop development with C++ are required.'
    }
    $vsInstallation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath |
        Select-Object -First 1
    if (-not $vsInstallation) {
        throw 'MSVC x64 build tools were not found. Install Visual Studio Build Tools with Desktop development with C++.'
    }
    $vsDevCmd = Join-Path $vsInstallation 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $vsDevCmd -PathType Leaf)) {
        throw "Visual Studio developer environment was not found: $vsDevCmd"
    }

    $originalPath = [Environment]::GetEnvironmentVariable('Path', 'Process')
    $vsCommand = "`"$vsDevCmd`" -no_logo -arch=x64 -host_arch=x64 && set"
    $environmentLines = & $env:ComSpec /d /s /c $vsCommand
    if ($LASTEXITCODE -ne 0) {
        throw "Visual Studio developer environment failed with exit code $LASTEXITCODE"
    }
    foreach ($line in $environmentLines) {
        if ($line -match '^([^=]+)=(.*)$' -and $Matches[1] -ine 'PATH') {
            [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
        }
    }

    $cmakeCommand = Get-Command cmake -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $cmake = Get-FirstExistingFile @(
        $(if ($cmakeCommand) { $cmakeCommand.Source }),
        (Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe'),
        (Join-Path $vsInstallation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')
    ) 'CMake was not found. Install it with: winget install --id Kitware.CMake --exact'

    $ninjaCommand = Get-Command ninja -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $ninja = Get-FirstExistingFile @(
        $(if ($ninjaCommand) { $ninjaCommand.Source }),
        (Join-Path $vsInstallation 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe')
    ) 'Ninja was not found. Install Visual Studio CMake tools or Ninja.'

    $libclangCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:LIBCLANG_PATH)) {
        $libclangCandidates += if (Test-Path -LiteralPath $env:LIBCLANG_PATH -PathType Container) {
            Join-Path $env:LIBCLANG_PATH 'libclang.dll'
        } else {
            $env:LIBCLANG_PATH
        }
    }
    $libclangCandidates += Join-Path $env:ProgramFiles 'LLVM\bin\libclang.dll'
    $libclang = Get-FirstExistingFile $libclangCandidates 'libclang.dll was not found. Install it with: winget install --id LLVM.LLVM --exact'

    $sdkBinRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $sdkBin = Get-ChildItem -LiteralPath $sdkBinRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\rc.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\mt.exe') -PathType Leaf)
        } |
        Sort-Object { [Version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $sdkBin) {
        throw 'Windows 10/11 SDK x64 tools (rc.exe and mt.exe) were not found.'
    }

    if ([string]::IsNullOrWhiteSpace($env:VCToolsInstallDir)) {
        throw 'VCToolsInstallDir was not set by the Visual Studio developer environment.'
    }
    $msvcBin = Join-Path $env:VCToolsInstallDir 'bin\Hostx64\x64'
    $toolPath = @(
        (Split-Path -Parent $cmake),
        (Split-Path -Parent $libclang),
        $msvcBin,
        (Split-Path -Parent $ninja),
        (Join-Path $sdkBin.FullName 'x64'),
        $originalPath
    ) -join ';'
    $env:Path = $toolPath
    $env:PATH = $toolPath
    $env:LIBCLANG_PATH = Split-Path -Parent $libclang
    $env:CMAKE = $cmake
    $env:CMAKE_GENERATOR = 'Ninja'
    if ([string]::IsNullOrWhiteSpace($env:CXXFLAGS)) {
        $env:CXXFLAGS = '/utf-8'
    } elseif ($env:CXXFLAGS -notmatch '(^|\s)/utf-8($|\s)') {
        $env:CXXFLAGS = "$($env:CXXFLAGS) /utf-8"
    }

    return [pscustomobject]@{
        CMake = $cmake
        Ninja = $ninja
        LibClang = $libclang
        Msvc = Join-Path $msvcBin 'cl.exe'
        WindowsSdk = $sdkBin.Name
    }
}

function Remove-IncompatibleLlamaCMakeCaches {
    $buildRoot = Join-Path $workspaceRoot 'target\release\build'
    if (-not (Test-Path -LiteralPath $buildRoot -PathType Container)) {
        return
    }
    foreach ($crateBuild in Get-ChildItem -LiteralPath $buildRoot -Directory -Filter 'llama-cpp-sys-2-*') {
        $cmakeBuild = Join-Path $crateBuild.FullName 'out\build'
        $cache = Join-Path $cmakeBuild 'CMakeCache.txt'
        if (-not (Test-Path -LiteralPath $cache -PathType Leaf)) {
            continue
        }
        $generator = Select-String -LiteralPath $cache -Pattern '^CMAKE_GENERATOR:INTERNAL=' |
            Select-Object -First 1
        if (-not $generator -or $generator.Line -eq 'CMAKE_GENERATOR:INTERNAL=Ninja') {
            continue
        }
        $resolvedBuild = [IO.Path]::GetFullPath($cmakeBuild)
        $allowedRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) +
            [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedBuild.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an unexpected CMake directory: $resolvedBuild"
        }
        Write-Host "Removing incompatible llama.cpp CMake cache: $resolvedBuild"
        Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
    }
}

$cliNeedsBuild = Test-CliBuildRequired
$cliNeedsCopy = $cliNeedsBuild -or -not (Test-FilesEqual $cargoBinary $desktopCli)
$desktopNeedsPackage = Test-DesktopPackageRequired $cliNeedsCopy
$dotnet = Get-ApplicationPath 'dotnet' 'Install the .NET 8 SDK or newer.' @(
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
)
Add-ExecutableDirectoryToPath $dotnet
$toolchain = $null
if ($cliNeedsBuild) {
    $cargo = Get-ApplicationPath 'cargo' 'Install Rust through rustup.' @(
        (Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe')
    )
    $toolchain = Initialize-RustBuildEnvironment
}
$pnpm = $null
if ($desktopNeedsPackage) {
    $pnpm = Get-ApplicationPath 'pnpm' 'Install pnpm 10 or newer.' @(
        (Join-Path $env:APPDATA 'npm\pnpm.cmd'),
        (Join-Path $env:LOCALAPPDATA 'pnpm\pnpm.exe')
    )
}

Write-Host "Mode: $Mode"
Write-Host "CLI: $(if ($cliNeedsBuild) { 'build' } else { 'reuse' })"
Write-Host "Desktop: $(if ($desktopNeedsPackage) { 'package' } else { 'reuse' })"
Write-Host "MSIX: build$(if ($Install) { ' and install' } elseif ($Sign) { ' and sign' })"
if ($toolchain) {
    Write-Host "Toolchain: MSVC, CMake, Ninja, LLVM, Windows SDK $($toolchain.WindowsSdk)"
}
if ($PlanOnly) {
    return
}

if ($cliNeedsBuild) {
    Remove-IncompatibleLlamaCMakeCaches
    Push-Location $workspaceRoot
    try {
        Invoke-Checked $cargo @('build', '--release', '-p', 'goose-cli', '--bin', 'goose')
    } finally {
        Pop-Location
    }
}
if (-not (Test-Path -LiteralPath $cargoBinary -PathType Leaf) -or
    (Get-Item -LiteralPath $cargoBinary).Length -eq 0) {
    throw "Goose CLI was not produced: $cargoBinary"
}
if (-not (Test-FilesEqual $cargoBinary $desktopCli)) {
    Copy-Item -LiteralPath $cargoBinary -Destination $desktopCli -Force
}

if ($desktopNeedsPackage) {
    Push-Location $desktopRoot
    try {
        Invoke-Checked $pnpm @('run', 'package')
    } finally {
        Pop-Location
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $desktopPackageMarker) -Force | Out-Null
    [IO.File]::WriteAllText($desktopPackageMarker, (Get-Date).ToUniversalTime().ToString('O'))
}

$msixParameters = @{}
if ($Sign) { $msixParameters.Sign = $true }
if ($Install) { $msixParameters.Install = $true }
if ($RestartExplorer) { $msixParameters.RestartExplorer = $true }
& $msixScript @msixParameters
