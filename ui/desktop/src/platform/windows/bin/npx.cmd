@ECHO OFF
SETLOCAL EnableDelayedExpansion

if not defined GOOSE_NODE_DIR (
    SET "GOOSE_NODE_DIR=%LOCALAPPDATA%\Goose\node"
)
SET "NODE_VERSION=22.14.0"
SET "NODE_SHA256=55b639295920b219bb2acbcfa00f90393a2789095b7323f79475c9f34795f217"
SET "NODE_MARKER=%GOOSE_NODE_DIR%\node-v%NODE_VERSION%.installed"

if exist "%NODE_MARKER%" if exist "%GOOSE_NODE_DIR%\npx.cmd" (
    SET "PATH=%GOOSE_NODE_DIR%;!PATH!"
    "%GOOSE_NODE_DIR%\npx.cmd" %*
    exit /b !errorlevel!
)

echo [Goose] Node.js not found. Downloading verified portable Node.js v%NODE_VERSION%... 1>&2
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $version=$env:NODE_VERSION; $expected=$env:NODE_SHA256; $target=$env:GOOSE_NODE_DIR; $marker=$env:NODE_MARKER; $mutex=[Threading.Mutex]::new($false, 'Local\GooseNodeRuntime-22.14.0'); $locked=$false; $temp=$null; $stage=$null; $backup=$null; try { $locked=$mutex.WaitOne([TimeSpan]::FromMinutes(2)); if (-not $locked) { throw 'Timed out waiting for another Node.js installation.' }; if ((Test-Path -LiteralPath $marker -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path $target 'npx.cmd') -PathType Leaf)) { exit 0 }; $parent=Split-Path -Parent $target; [IO.Directory]::CreateDirectory($parent) | Out-Null; $id=[Guid]::NewGuid().ToString('N'); $temp=Join-Path $env:TEMP ('goose-node-' + $id); $zip=Join-Path $temp 'node.zip'; $extract=Join-Path $temp 'extract'; $stage=$target + '.staging-' + $id; $backup=$target + '.backup-' + $id; [IO.Directory]::CreateDirectory($extract) | Out-Null; $ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri ('https://nodejs.org/dist/v' + $version + '/node-v' + $version + '-win-x64.zip') -OutFile $zip -UseBasicParsing; $actual=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant(); if ($actual -ne $expected) { throw ('Node.js SHA-256 mismatch. Expected ' + $expected + ', got ' + $actual + '.') }; Expand-Archive -LiteralPath $zip -DestinationPath $extract -Force; $source=Join-Path $extract ('node-v' + $version + '-win-x64'); if (-not (Test-Path -LiteralPath (Join-Path $source 'node.exe') -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $source 'npx.cmd') -PathType Leaf)) { throw 'The verified Node.js archive is incomplete.' }; Copy-Item -LiteralPath $source -Destination $stage -Recurse; if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination $backup }; try { Move-Item -LiteralPath $stage -Destination $target; New-Item -ItemType File -Path $marker -Force | Out-Null } catch { if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) { Move-Item -LiteralPath $backup -Destination $target }; throw }; if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force } } finally { if ($temp -and (Test-Path -LiteralPath $temp)) { Remove-Item -LiteralPath $temp -Recurse -Force }; if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }; if ($locked) { $mutex.ReleaseMutex() }; $mutex.Dispose() }"
if errorlevel 1 (
    echo [Goose] ERROR: Failed to install the verified Node.js runtime. 1>&2
    exit /b 1
)

if exist "%NODE_MARKER%" if exist "%GOOSE_NODE_DIR%\npx.cmd" (
    SET "PATH=%GOOSE_NODE_DIR%;!PATH!"
    echo [Goose] Node.js v%NODE_VERSION% ready. 1>&2
    "%GOOSE_NODE_DIR%\npx.cmd" %*
    exit /b !errorlevel!
)

echo [Goose] ERROR: Node.js installation did not produce npx.cmd. 1>&2
exit /b 1
