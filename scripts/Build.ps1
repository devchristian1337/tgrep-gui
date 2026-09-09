param(
    [ValidateSet('Restore','Build','Run','Test','Publish','Package','MSIX')][string]$Task = 'Build',
    [string]$TgrepPath = ''
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot
$localDotnet = Join-Path $projectDirectory '.tools\dotnet\dotnet.exe'
$dotnetExecutable = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }

function Get-BundledTgrep {
    param([string]$Rid = 'win-x64')
    $version = '1.0.5'
    if ($Rid -eq 'win-arm64') {
        $arch = 'aarch64'
        $expected = 'f49b68f97810530688a8fe71282dfc384ed4ff99ffe7a8a769e43e68a7c633fe'
        $downloadDirectory = Join-Path $projectDirectory '.tools\tgrep-arm64'
    }
    else {
        if ($TgrepPath -and (Test-Path -LiteralPath $TgrepPath)) { return (Resolve-Path -LiteralPath $TgrepPath).Path }
        $arch = 'x86_64'
        $expected = '5b6ba08ffddb5bc1b436c5c83b4f0c9e66c70a006b3853ed57daf51e7a75986c'
        $downloadDirectory = Join-Path $projectDirectory '.tools\tgrep'
    }
    $toolsTgrep = Join-Path $downloadDirectory 'tgrep.exe'
    $zipUrl = "https://github.com/microsoft/tgrep/releases/download/v$version/tgrep-v$version-$arch-pc-windows-msvc.zip"
    $zipPath = Join-Path $downloadDirectory 'tgrep-windows.zip'
    $zipValid = (Test-Path -LiteralPath $zipPath) -and
        ((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected)
    if (-not $zipValid) {
        New-Item -ItemType Directory -Force $downloadDirectory | Out-Null
        Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
        $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "tgrep zip hash mismatch ($Rid): $actual" }
    }
    if (-not $zipValid -or -not (Test-Path -LiteralPath $toolsTgrep)) {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $downloadDirectory -Force
    }
    if (-not (Test-Path -LiteralPath $toolsTgrep)) { throw "tgrep.exe missing after extract ($Rid)" }
    return (Resolve-Path -LiteralPath $toolsTgrep).Path
}

function Publish-Rid([string]$Rid, [string]$Platform) {
    $tgrep = Get-BundledTgrep -Rid $Rid
    & $dotnetExecutable publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -p:Platform=$Platform -r $Rid "-p:BundledTgrepExe=$tgrep"
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}

function New-RidRelease([string]$Rid, [string]$Platform) {
    Publish-Rid $Rid $Platform
    $publishDirectory = Join-Path $projectDirectory "artifacts\publish\$Rid"
    $exe = Join-Path $publishDirectory 'tgrep-gui.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Published EXE not found: $exe" }
    $artifactsDirectory = Join-Path $projectDirectory 'artifacts'
    New-Item -ItemType Directory -Force $artifactsDirectory | Out-Null
    $stageDirectory = Join-Path $projectDirectory "artifacts\stage-$Rid"
    if (Test-Path -LiteralPath $stageDirectory) { Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
    New-Item -ItemType Directory -Force $stageDirectory | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $stageDirectory 'tgrep-gui.exe') -Force
    $zipPath = Join-Path $artifactsDirectory "tgrep-gui-$Rid.zip"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    tar -a -cf $zipPath -C $stageDirectory tgrep-gui.exe
    if ($LASTEXITCODE -ne 0) { throw "tar exited with code $LASTEXITCODE" }
    Write-Host "Created $zipPath"
}

function Publish-WinX64 { Publish-Rid 'win-x64' 'x64' }

function New-ReleaseZip {
    New-RidRelease 'win-x64' 'x64'
    New-RidRelease 'win-arm64' 'ARM64'
}

Push-Location $projectDirectory
try {
    switch ($Task) {
        'Restore' { & $dotnetExecutable restore .\tgrep-gui.csproj }
        'Build' { & $dotnetExecutable build .\tgrep-gui.csproj }
        'Run' { & $dotnetExecutable run --project .\tgrep-gui.csproj }
        'Test' {
            if ($TgrepPath) { & $dotnetExecutable run --project .\Tests\Tests.csproj -- $TgrepPath }
            else { & $dotnetExecutable run --project .\Tests\Tests.csproj }
        }
        'Publish' { Publish-WinX64; return }
        'Package' { New-ReleaseZip; return }
        'MSIX' { & $dotnetExecutable publish .\tgrep-gui.csproj -p:PublishProfile=MSIX -p:WindowsPackageType=MSIX -p:Platform=x64 -r win-x64 }
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}
finally { Pop-Location }
