param(
    [ValidateSet('Dev', 'Build', 'Test', 'Preview', 'Package')][string]$Task = 'Build',
    [string]$TgrepPath = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot
$desktopRoot = Join-Path $repositoryRoot 'desktop'
$localRust = Join-Path $repositoryRoot '.tools\rust'
if (Test-Path (Join-Path $localRust 'cargo\bin\cargo.exe')) {
    $env:CARGO_HOME = Join-Path $localRust 'cargo'
    $env:RUSTUP_HOME = Join-Path $localRust 'rustup'
    $env:PATH = "$(Join-Path $env:CARGO_HOME 'bin');$env:PATH"
}

function Get-BundledTgrep {
    if ($TgrepPath -and (Test-Path -LiteralPath $TgrepPath)) {
        return (Resolve-Path -LiteralPath $TgrepPath).Path
    }
    $version = '1.0.5'
    $expected = '5b6ba08ffddb5bc1b436c5c83b4f0c9e66c70a006b3853ed57daf51e7a75986c'
    $downloadDirectory = Join-Path $repositoryRoot '.tools\tgrep'
    $toolsTgrep = Join-Path $downloadDirectory 'tgrep.exe'
    $zipUrl = "https://github.com/microsoft/tgrep/releases/download/v$version/tgrep-v$version-x86_64-pc-windows-msvc.zip"
    $zipPath = Join-Path $downloadDirectory 'tgrep-windows.zip'
    $zipValid = (Test-Path -LiteralPath $zipPath) -and
        ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected)
    if (-not $zipValid) {
        New-Item -ItemType Directory -Force $downloadDirectory | Out-Null
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "tgrep zip hash mismatch: $actual" }
    }
    if (-not $zipValid -or -not (Test-Path -LiteralPath $toolsTgrep)) {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $downloadDirectory -Force
    }
    if (-not (Test-Path -LiteralPath $toolsTgrep)) { throw 'tgrep.exe missing after extract' }
    return (Resolve-Path -LiteralPath $toolsTgrep).Path
}

function Copy-BundledTgrep {
    $tgrep = Get-BundledTgrep
    $destDir = Join-Path $desktopRoot 'src-tauri\binaries'
    New-Item -ItemType Directory -Force $destDir | Out-Null
    Copy-Item -LiteralPath $tgrep -Destination (Join-Path $destDir 'tgrep.exe') -Force
    return $tgrep
}

function Invoke-Npm([string[]]$NpmArgs) {
    & npm.cmd @NpmArgs
    if ($LASTEXITCODE -ne 0) { throw "npm $($NpmArgs -join ' ') failed." }
}

Push-Location $desktopRoot
try {
    if (!(Test-Path node_modules)) { Invoke-Npm @('ci') }
    switch ($Task) {
        'Preview' { Invoke-Npm @('run', 'dev') }
        'Dev' {
            Get-BundledTgrep | Out-Null
            Invoke-Npm @('run', 'tauri', '--', 'dev')
        }
        'Test' {
            Invoke-Npm @('test')
            $env:TGREP_TEST_EXE = Get-BundledTgrep
            & cargo test --manifest-path src-tauri\Cargo.toml -- --include-ignored
            if ($LASTEXITCODE -ne 0) { throw 'cargo test failed.' }
        }
        'Build' {
            Copy-BundledTgrep | Out-Null
            Invoke-Npm @('run', 'tauri', '--', 'build')
        }
        'Package' {
            $bundled = Copy-BundledTgrep
            Invoke-Npm @('run', 'tauri', '--', 'build')
            $release = Join-Path $desktopRoot 'src-tauri\target\release'
            $exe = Join-Path $release 'tgrep-gui.exe'
            if (-not (Test-Path -LiteralPath $exe)) { throw "Release executable not found: $exe" }
            Copy-Item -LiteralPath $bundled -Destination (Join-Path $release 'tgrep.exe') -Force
            $setup = Get-ChildItem (Join-Path $release 'bundle\nsis') -Filter '*setup.exe' |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if (-not $setup) { throw 'NSIS installer not found.' }
            $artifacts = Join-Path $repositoryRoot 'artifacts'
            New-Item -ItemType Directory -Force $artifacts | Out-Null
            $setupDest = Join-Path $artifacts 'tgrep-gui-win-x64-setup.exe'
            Copy-Item -LiteralPath $setup.FullName -Destination $setupDest -Force
            $stage = Join-Path $artifacts 'stage-win-x64'
            if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
            New-Item -ItemType Directory -Force $stage | Out-Null
            Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'tgrep-gui.exe') -Force
            Copy-Item -LiteralPath $bundled -Destination (Join-Path $stage 'tgrep.exe') -Force
            $zipPath = Join-Path $artifacts 'tgrep-gui-win-x64.zip'
            if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
            tar -a -cf $zipPath -C $stage tgrep-gui.exe tgrep.exe
            if ($LASTEXITCODE -ne 0) { throw 'tar failed.' }
            Write-Host "Created $setupDest"
            Write-Host "Created $zipPath"
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "Task $Task failed. Check the output above." }
}
finally { Pop-Location }
