param([ValidateSet('Dev', 'Build', 'Test', 'Preview', 'Package')][string]$Task = 'Dev')
$ErrorActionPreference = 'Stop'
& (Join-Path (Split-Path $PSScriptRoot) 'scripts\Build.ps1') -Task $Task
