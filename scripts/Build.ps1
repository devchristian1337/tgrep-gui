param(
    [ValidateSet('Restore','Build','Run','Test','Publish','MSIX')][string]$Task = 'Build',
    [string]$TgrepPath = ''
)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot
$localDotnet = Join-Path $projectDirectory '.tools\dotnet\dotnet.exe'
$dotnetExecutable = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
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
        'Publish' { & $dotnetExecutable publish .\tgrep-gui.csproj -p:PublishProfile=Unpackaged -r win-x64 }
        'MSIX' { & $dotnetExecutable publish .\tgrep-gui.csproj -p:PublishProfile=MSIX -p:WindowsPackageType=MSIX -p:Platform=x64 -r win-x64 }
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet ha restituito il codice $LASTEXITCODE" }
}
finally { Pop-Location }
