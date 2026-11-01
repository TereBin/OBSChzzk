param([switch]$Network)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force test-output | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$refs=@('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Net.Http.dll','/reference:System.Web.Extensions.dll')
& $compiler /nologo /target:exe /platform:x64 /main:Tests /out:test-output/Tests.exe @refs Installer.cs Tests.cs
if ($LASTEXITCODE -ne 0) { throw 'Test compile failed' }
& ./test-output/Tests.exe
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
if ($Network) {
    & $compiler /nologo /target:exe /platform:x64 /main:Program /out:test-output/NetworkCheck.exe @refs Installer.cs
    if ($LASTEXITCODE -ne 0) { throw 'Network check compile failed' }
    & ./test-output/NetworkCheck.exe --check-downloads (Join-Path $PWD 'test-output/latest')
    if ($LASTEXITCODE -ne 0) { throw 'Download verification failed; see test-output/latest/error.txt' }
    Write-Host 'PASS: latest lookup and SHA-256 verification for all three plugins (no installation)'
}
