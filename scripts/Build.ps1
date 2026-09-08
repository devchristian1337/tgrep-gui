param(
    [ValidateSet('Restore','Build','Run','Test','Publish','Package','MSIX')][string]$Task = 'Build',
    [string]$TgrepPath = ''
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot
$localDotnet = Join-Path $projectDirectory '.tools\dotnet\dotnet.exe'
$dotnetExecutable = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }

function Get-BundledTgrep {
    $toolsTgrep = Join-Path $projectDirectory '.tools\tgrep\tgrep.exe'
    if ($TgrepPath -and (Test-Path -LiteralPath $TgrepPath)) { return (Resolve-Path -LiteralPath $TgrepPath).Path }
    if (Test-Path -LiteralPath $toolsTgrep) { return (Resolve-Path -LiteralPath $toolsTgrep).Path }
    $zipUrl = 'https://github.com/microsoft/tgrep/releases/download/v1.0.4/tgrep-v1.0.4-x86_64-pc-windows-msvc.zip'
    $expected = '9b8d5488b1c342c10f222806de84a78049e8d8e8bdd35e34a0f872560c700b56'
    $downloadDirectory = Join-Path $projectDirectory '.tools\tgrep'
    New-Item -ItemType Directory -Force $downloadDirectory | Out-Null
    $zipPath = Join-Path $downloadDirectory 'tgrep-windows.zip'
    Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath
    $actual = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "tgrep zip hash mismatch: $actual" }
    Expand-Archive -LiteralPath $zipPath -DestinationPath $downloadDirectory -Force
    $downloaded = Join-Path $downloadDirectory 'tgrep.exe'
    if (-not (Test-Path -LiteralPath $downloaded)) { throw "tgrep.exe missing after extract" }
    return (Resolve-Path -LiteralPath $downloaded).Path
}

function Publish-WinX64 {
    & $dotnetExecutable publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}

function New-ReleaseZip {
    Publish-WinX64
    $publishDirectory = Join-Path $projectDirectory 'artifacts\publish\win-x64'
    $exe = Join-Path $publishDirectory 'tgrep-gui.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Published EXE not found: $exe" }
    Copy-Item -LiteralPath (Get-BundledTgrep) -Destination (Join-Path $publishDirectory 'tgrep.exe') -Force
    $stageDirectory = Join-Path $projectDirectory 'artifacts\stage'
    $appDirectory = Join-Path $stageDirectory 'tgrep-gui'
    if (Test-Path -LiteralPath $stageDirectory) { Remove-Item -LiteralPath $stageDirectory -Recurse -Force }
    New-Item -ItemType Directory -Force $appDirectory | Out-Null
    Copy-Item -Path (Join-Path $publishDirectory '*') -Destination $appDirectory -Recurse -Force
    $zipPath = Join-Path $projectDirectory 'artifacts\tgrep-gui-win-x64.zip'
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    tar -a -cf $zipPath -C $stageDirectory tgrep-gui
    if ($LASTEXITCODE -ne 0) { throw "tar exited with code $LASTEXITCODE" }
    Write-Host "Created $zipPath"
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
