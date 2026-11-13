param([string]$BuildDirectory = (Join-Path $PSScriptRoot '..\bin\Release'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PackageManifestReader {
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
 [DllImport("kernel32.dll", EntryPoint="FindResourceW", SetLastError=true)] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
 [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
 [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr resource);
 [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr resource);
 [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
 public static string Read(string path) {
  var module=LoadLibraryEx(path,IntPtr.Zero,2); if(module==IntPtr.Zero) throw new Exception("Cannot inspect executable resources.");
  try { var resource=FindResource(module,new IntPtr(1),new IntPtr(24)); if(resource==IntPtr.Zero) throw new Exception("Missing executable manifest.");
   var bytes=new byte[SizeofResource(module,resource)]; Marshal.Copy(LockResource(LoadResource(module,resource)),bytes,0,bytes.Length); return System.Text.Encoding.UTF8.GetString(bytes);
  } finally {FreeLibrary(module);}
 }
}
'@
$manifestText = [PackageManifestReader]::Read((Join-Path (Resolve-Path $BuildDirectory).Path 'SMUDebugTool.exe'))
if ($manifestText -notmatch 'level="requireAdministrator"' -or $manifestText -notmatch '8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a') { throw 'Packaged executable lost UAC/Windows DPI compatibility metadata.' }
[xml]$configuration = Get-Content -LiteralPath (Join-Path $BuildDirectory 'SMUDebugTool.exe.config') -Raw
if (($configuration.configuration.'System.Windows.Forms.ApplicationConfigurationSection'.add | Where-Object key -eq DpiAwareness).value -ne 'PerMonitorV2') { throw 'Packaged DPI configuration missing.' }
$scratch = Join-Path $root ('TestResults\package-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$package = Join-Path $root 'scripts\Package-Release.ps1'
$verify = Join-Path $root 'scripts\Verify-Package.ps1'
& $package -BuildDirectory $BuildDirectory -OutputDirectory (Join-Path $scratch 'first') | Out-Null
& $package -BuildDirectory $BuildDirectory -OutputDirectory (Join-Path $scratch 'second') | Out-Null
$first = (Get-ChildItem (Join-Path $scratch 'first') -Filter '*.zip').FullName
$second = (Get-ChildItem (Join-Path $scratch 'second') -Filter '*.zip').FullName
if ((Get-FileHash $first).Hash -ne (Get-FileHash $second).Hash) { throw 'Repeated packaging changed archive bytes.' }
function ExpectFailure([scriptblock]$Operation, [string]$Name) {
    $failed = $false
    try { & $Operation | Out-Null } catch { $failed = $true }
    if (!$failed) { throw "Expected failure: $Name" }
}
ExpectFailure { & $package -BuildDirectory $BuildDirectory -OutputDirectory (Join-Path $scratch 'wrong-version') -Version '0.0.1' } 'stale version'
$fixture = Join-Path $scratch 'build'
Copy-Item -LiteralPath $BuildDirectory -Destination $fixture -Recurse
Move-Item -LiteralPath (Join-Path $fixture 'inpoutx64.dll') -Destination (Join-Path $fixture 'missing-native.dll')
ExpectFailure { & $package -BuildDirectory $fixture -OutputDirectory (Join-Path $scratch 'missing-native') } 'missing native dependency'
Copy-Item -LiteralPath (Join-Path $BuildDirectory 'inpoutx64.dll') -Destination (Join-Path $fixture 'inpoutx64.dll')
Move-Item -LiteralPath (Join-Path $fixture 'de\Microsoft.Win32.TaskScheduler.resources.dll') -Destination (Join-Path $fixture 'missing-satellite.dll')
ExpectFailure { & $package -BuildDirectory $fixture -OutputDirectory (Join-Path $scratch 'missing-satellite') } 'missing satellite'
ExpectFailure { & $package -BuildDirectory $BuildDirectory -OutputDirectory (Join-Path $scratch 'missing-notes') -ReleaseNotes (Join-Path $scratch 'absent.md') } 'missing notes'
$corrupt = Join-Path $scratch 'corrupt.zip'
Copy-Item -LiteralPath $first -Destination $corrupt
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($corrupt, [IO.Compression.ZipArchiveMode]::Update)
try { $null = $zip.CreateEntry('../unexpected.txt') } finally { $zip.Dispose() }
ExpectFailure { & $verify -Archive $corrupt } 'unexpected traversal entry'
$tampered = Join-Path $scratch 'tampered.zip'
Copy-Item -LiteralPath $first -Destination $tampered
$zip = [IO.Compression.ZipFile]::Open($tampered, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.GetEntry('SMUDebugTool.exe'); $entry.Delete()
    $writer = New-Object IO.StreamWriter($zip.CreateEntry('SMUDebugTool.exe').Open())
    try { $writer.Write('not the tested executable') } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
ExpectFailure { & $verify -Archive $tampered } 'content checksum mismatch'
Write-Output 'Package checks passed: deterministic archive, complete package, missing inputs, version mismatch, unsafe entries, and corruption.'
