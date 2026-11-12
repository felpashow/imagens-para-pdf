$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
$target = Join-Path $PSScriptRoot '..\FotosParaPdf.exe'
$icon = Join-Path $PSScriptRoot '..\assets\icone-fotos-para-pdf.ico'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/win32icon:$icon" "/out:$target" $sources
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar.' }
Get-Item -LiteralPath $target | Select-Object FullName,Length
