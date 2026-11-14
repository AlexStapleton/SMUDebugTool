param(
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '..\bin\Release'),
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version,
    [string]$SourceCommit,
    [string]$ReleaseNotes = (Join-Path $PSScriptRoot '..\docs\release-notes-1.49.0.md'),
    [switch]$RequireClean
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$exe = Join-Path $build 'SMUDebugTool.exe'
$fileVersion = [Version]([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion)
$actualVersion = "$($fileVersion.Major).$($fileVersion.Minor).$($fileVersion.Build)"
if (!$Version) { $Version = $actualVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $Version -ne $actualVersion) { throw 'Tag/package version does not match the executable.' }
if (!$SourceCommit) { $SourceCommit = (& git -C $root rev-parse HEAD).Trim() }
if ($SourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'A complete source commit is required.' }
$coreRevision = (& git -C (Join-Path $root 'vendor\ZenStates-Core') rev-parse HEAD).Trim()
$dependencyLock = Get-Content -LiteralPath (Join-Path $root 'dependencies.lock.json') -Raw | ConvertFrom-Json
if ($coreRevision -ne $dependencyLock.coreRevision) { throw 'Unexpected core revision.' }
$dirty = [bool](& git -C $root status --porcelain)
if ($RequireClean -and $dirty) { throw 'Release packaging requires a clean source checkout.' }
$required = @('SMUDebugTool.exe', 'SMUDebugTool.exe.config', 'ZenStates-Core.dll', 'inpoutx64.dll',
 'Microsoft.Win32.TaskScheduler.dll', 'Newtonsoft.Json.dll', 'System.Buffers.dll', 'System.Memory.dll',
 'System.Numerics.Vectors.dll', 'System.Resources.Extensions.dll', 'System.Runtime.CompilerServices.Unsafe.dll')
$inputs = [ordered]@{}
foreach ($name in $required) { $inputs[$name] = Join-Path $build $name }
foreach ($culture in @('de','es','fr','it','pl','ru','zh-CN','zh-Hant')) {
    $relative = "$culture/Microsoft.Win32.TaskScheduler.resources.dll"
    $inputs[$relative] = Join-Path $build $relative
}
$inputs['zh-CN/SMUDebugTool.resources.dll'] = Join-Path $build 'zh-CN/SMUDebugTool.resources.dll'
$inputs['LICENSE.md'] = Join-Path $root 'LICENSE.md'
$inputs['licenses/ZenStates-Core.txt'] = Join-Path $root 'vendor/ZenStates-Core/LICENSE'
$inputs['licenses/InpOut.txt'] = Join-Path $root 'vendor/ZenStates-Core/External/InpOut/license.txt'
$inputs['licenses/PawnIO.txt'] = Join-Path $root 'vendor/ZenStates-Core/Resources/PawnIo/COPYING'
$inputs['RELEASE-NOTES.md'] = $ReleaseNotes
foreach ($path in $inputs.Values) { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required package input missing: $path" } }
if ((Get-FileHash -LiteralPath (Join-Path $build 'inpoutx64.dll')).Hash.ToLowerInvariant() -ne $dependencyLock.inpoutx64Sha256) { throw 'Native DLL does not match the reviewed dependency lock.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$archive = Join-Path $output "SMUDebugTool-v$Version.zip"
if (Test-Path -LiteralPath $archive) { throw "Output already exists: $archive" }
$files = @($inputs.Keys | Sort-Object | ForEach-Object {
    $item = Get-Item -LiteralPath $inputs[$_]
    [ordered]@{ Path = $_; Size = $item.Length; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ Version = $Version; SourceCommit = $SourceCommit; SourceDirty = $dirty; CoreRevision = $coreRevision; CoreCompatibility = $dependencyLock.coreCompatibility;
    Framework = 'net48'; Configuration = 'Release'; Sdk = (& dotnet --version).Trim(); Files = $files } | ConvertTo-Json -Depth 8
$manifestPath = Join-Path $output "SMUDebugTool-v$Version-build-info.json"
[IO.File]::WriteAllText($manifestPath, $manifest, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $output 'RELEASE-NOTES.md'), [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseNotes).Path) + "`n`nBuilt from commit: $SourceCommit.`n", (New-Object Text.UTF8Encoding($false)))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew)
$zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
try {
    foreach ($name in $inputs.Keys | Sort-Object) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($inputs[$name]); $destination = $entry.Open()
        try { $input.CopyTo($destination) } finally { $input.Dispose(); $destination.Dispose() }
    }
    $entry = $zip.CreateEntry('BUILD-INFO.json'); $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
    $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
    try { $writer.Write($manifest) } finally { $writer.Dispose() }
} finally { $zip.Dispose(); $stream.Dispose() }
& (Join-Path $PSScriptRoot 'Verify-Package.ps1') -Archive $archive
$digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output "SMUDebugTool-v$Version.sha256"), "$digest  $([IO.Path]::GetFileName($archive))`n")
Write-Output $archive
