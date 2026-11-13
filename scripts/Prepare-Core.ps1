param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$pin = '3bb1dad5b6cccc14f063d6eacbcf891a309944f3'
$revision = (& git -C $Source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -ne $pin) { throw "Expected ZenStates-Core $pin; found $revision." }
$dirty = & git -C $Source status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Pinned core source has tracked modifications.' }
$original = [IO.File]::ReadAllText((Join-Path $Source 'Cpu.cs'))
$marker = 'public class Cpu : IDisposable'
if (($original.Split(@($marker), [StringSplitOptions]::None)).Length -ne 2) { throw 'Core Cpu declaration changed.' }
$prepared = $original.Replace($marker, 'public partial class Cpu : IDisposable')
$directory = [IO.Path]::GetDirectoryName($Output)
[IO.Directory]::CreateDirectory($directory) | Out-Null
if (!(Test-Path -LiteralPath $Output) -or [IO.File]::ReadAllText($Output) -ne $prepared) {
    [IO.File]::WriteAllText($Output, $prepared, (New-Object Text.UTF8Encoding($false)))
}
