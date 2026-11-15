param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$Output,
    [string]$Source = (Join-Path $PSScriptRoot '..\Tests\Performance\PerformanceProbe.cs'),
    [switch]$ExportOnly
)
$ErrorActionPreference = 'Stop'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$exe = Join-Path $build 'PerformanceProbe.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 ('/out:' + $exe) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll ('/reference:' + (Join-Path $build 'SMUDebugTool.exe')) ('/reference:' + (Join-Path $build 'ZenStates-Core.dll')) ('/reference:' + (Join-Path $build 'Newtonsoft.Json.dll')) $Source
if ($LASTEXITCODE -ne 0) { throw 'Performance fixture compilation failed.' }
if ($ExportOnly) { & $exe ([IO.Path]::GetFullPath($Output)) 'export-only' }
else { & $exe ([IO.Path]::GetFullPath($Output)) }
if ($LASTEXITCODE -ne 0) { throw 'Performance fixture failed.' }
