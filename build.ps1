$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force dist | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = 'dist/OBSChzzk-1.1.9-windows-x64-setup.exe'
$refs = @('/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Net.Http.dll','/reference:System.Web.Extensions.dll')
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:app.manifest "/out:$output" @refs Installer.cs
if ($LASTEXITCODE -ne 0) { throw 'Compile failed' }
$hash = (Get-FileHash $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $PWD 'dist/SHA256SUMS.txt'), "$hash  OBSChzzk-1.1.9-windows-x64-setup.exe`n")
Write-Host "Built: $output"
