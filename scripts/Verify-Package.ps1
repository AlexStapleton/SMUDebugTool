param([Parameter(Mandatory)][string]$Archive)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    $manifestEntry = $zip.GetEntry('BUILD-INFO.json')
    if (!$manifestEntry) { throw 'Package has no build manifest.' }
    $reader = New-Object IO.StreamReader($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $expected = @('BUILD-INFO.json') + @($manifest.Files | ForEach-Object Path)
    $actual = @($zip.Entries | ForEach-Object FullName)
    if (($actual | Select-Object -Unique).Count -ne $actual.Count -or (Compare-Object ($expected | Sort-Object) ($actual | Sort-Object))) { throw 'Unexpected or duplicate package entries.' }
    foreach ($file in $manifest.Files) {
        if ($file.Path -match '(^[/\\]|(^|[/\\])\.\.([/\\]|$)|:)' -or $file.Path.Contains('\')) { throw 'Unsafe package entry path.' }
        $entry = $zip.GetEntry($file.Path)
        $sha = [Security.Cryptography.SHA256]::Create(); $stream = $entry.Open()
        try { $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($digest -ne $file.Sha256 -or $entry.Length -ne $file.Size) { throw "Package verification failed: $($file.Path)" }
    }
    if (!$manifest.Version -or !$manifest.SourceCommit -or !$manifest.CoreRevision) { throw 'Incomplete provenance.' }
    Write-Output "Verified $($manifest.Files.Count) files for v$($manifest.Version) ($($manifest.SourceCommit))."
} finally { $zip.Dispose() }
