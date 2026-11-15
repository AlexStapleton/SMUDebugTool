param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$pin = '3bb1dad5b6cccc14f063d6eacbcf891a309944f3'
$revision = (& git -C $Source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -ne $pin) { throw "Expected ZenStates-Core $pin; found $revision." }
$dirty = & git -C $Source status --porcelain --untracked-files=no
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Pinned core source has tracked modifications.' }
function Replace-Once([string]$Text, [string]$Marker, [string]$Replacement) {
    if (($Text.Split(@($Marker), [StringSplitOptions]::None)).Length -ne 2) { throw "Pinned source marker changed: $Marker" }
    return $Text.Replace($Marker, $Replacement)
}
$prepared = [IO.File]::ReadAllText((Join-Path $Source 'Cpu.cs'))
$prepared = Replace-Once $prepared 'public class Cpu : IDisposable' 'public partial class Cpu : IDisposable'
# Honor upstream's module switches at the construction sites. APOB/core identity,
# CPU temperature and the power table stay on the essential path.
$prepared = Replace-Once $prepared 'memoryConfig = new MemoryConfig(this);' 'memoryConfig = Options.Timings.Enabled != false ? new MemoryConfig(this) : null;'
$prepared = Replace-Once $prepared 'info.aod = new AOD(io, this);' 'info.aod = Options.Aod.Enabled != false ? new AOD(io, this) : null;'
$prepared = Replace-Once $prepared '_smbusPiix4 = SmbusProvider.Instance;' '_smbusPiix4 = Options.Timings.Enabled != false ? SmbusProvider.Instance : null;'
$prepared = Replace-Once $prepared '_lpcIO = new LpcIO();' '_lpcIO = Options.Sensors.Enabled != false ? new LpcIO() : null;'
$directory = [IO.Path]::GetDirectoryName($Output)
[IO.Directory]::CreateDirectory($directory) | Out-Null
if (!(Test-Path -LiteralPath $Output) -or [IO.File]::ReadAllText($Output) -ne $prepared) {
    [IO.File]::WriteAllText($Output, $prepared, (New-Object Text.UTF8Encoding($false)))
}
$systemInfo = [IO.File]::ReadAllText((Join-Path $Source 'Hardware\SystemInfo.cs'))
$systemInfo = Replace-Once $systemInfo '_lpcIO = new LpcIO(smbios);' '_lpcIO = CoreOptions.Current.Sensors.Enabled != false ? new LpcIO(smbios) : null;'
$systemInfo = Replace-Once $systemInfo 'i < _lpcIO.SuperIO.Length' 'i < (_lpcIO?.SuperIO.Length ?? 0)'
$systemInfoOutput = Join-Path $directory 'SystemInfo.cs'
if (!(Test-Path -LiteralPath $systemInfoOutput) -or [IO.File]::ReadAllText($systemInfoOutput) -ne $systemInfo) {
    [IO.File]::WriteAllText($systemInfoOutput, $systemInfo, (New-Object Text.UTF8Encoding($false)))
}
