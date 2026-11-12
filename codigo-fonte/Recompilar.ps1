$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Join-Path $PSScriptRoot 'FotosParaPdf.cs'
$instanceSource = Join-Path $PSScriptRoot 'Instance.cs'
$target = Join-Path $PSScriptRoot '..\FotosParaPdf.exe'
$icon = Join-Path $PSScriptRoot '..\assets\icone-fotos-para-pdf.ico'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/win32icon:$icon" "/out:$target" $source $instanceSource
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar.' }
Get-Item -LiteralPath $target | Select-Object FullName,Length
