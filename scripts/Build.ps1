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
    if ($Rid -eq 'win-arm64') {
        $toolsTgrep = Join-Path $projectDirectory '.tools\tgrep-arm64\tgrep.exe'
        $zipUrl = 'https://github.com/microsoft/tgrep/releases/download/v1.0.4/tgrep-v1.0.4-aarch64-pc-windows-msvc.zip'
        $expected = 'e1d1137c893edf7bd97eeaf93d95483f7d3f46f9b4c0923c5a2c2037c4125742'
        $downloadDirectory = Join-Path $projectDirectory '.tools\tgrep-arm64'
        $zipPath = Join-Path $downloadDirectory 'tgrep-windows.zip'
    }
    else {
        $toolsTgrep = Join-Path $projectDirectory '.tools\tgrep\tgrep.exe'
        if ($TgrepPath -and (Test-Path -LiteralPath $TgrepPath)) { return (Resolve-Path -LiteralPath $TgrepPath).Path }
        $zipUrl = 'https://github.com/microsoft/tgrep/releases/download/v1.0.4/tgrep-v1.0.4-x86_64-pc-windows-msvc.zip'
        $expected = '9b8d5488b1c342c10f222806de84a78049e8d8e8bdd35e34a0f872560c700b56'
        $downloadDirectory = Join-Path $projectDirectory '.tools\tgrep'
        $zipPath = Join-Path $downloadDirectory 'tgrep-windows.zip'
    }
    if (Test-Path -LiteralPath $toolsTgrep) { return (Resolve-Path -LiteralPath $toolsTgrep).Path }
    New-Item -ItemType Directory -Force $downloadDirectory | Out-Null
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "tgrep zip hash mismatch ($Rid): $actual" }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $downloadDirectory -Force
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
