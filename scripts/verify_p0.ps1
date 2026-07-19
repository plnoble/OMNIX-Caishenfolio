$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "== dotnet build =="
dotnet build "$Root\Caishenfolio.slnx"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== dotnet test =="
dotnet test "$Root\tests\Caishenfolio.Host.Tests\Caishenfolio.Host.Tests.csproj" --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== python unittest =="
$env:PYTHONPATH = Join-Path $Root "python"
python -m unittest discover -s tests/python -v
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== python compileall =="
python -m compileall python tests/python
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "P0 verification passed."
