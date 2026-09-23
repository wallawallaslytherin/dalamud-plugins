#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$artifacts = Join-Path $root 'artifacts'
$payload = Join-Path $artifacts 'plugin'
$output = Join-Path $artifacts 'packages'
$relay = Join-Path $artifacts 'relay'

function Assert-OrdinaryPath([string]$Base, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[/\\])\.\.([/\\]|$)') { throw 'A package path must stay inside its selected directory.' }
    $full = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
    if (!$full.StartsWith($Base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'A package path escaped its selected directory.' }
    $current = $full
    while ($current.Length -ge $Base.Length) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked package files or directories are not supported.' }
        if ($current -eq $Base) { break }
        $current = Split-Path -Parent $current
    }
    return $full
}
function New-Archive([string]$Base, [string[]]$Files, [string]$Destination, [string]$Prefix = '') {
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $Files | Sort-Object -Unique) {
            $source = Assert-OrdinaryPath $Base $relative
            $entry = $archive.CreateEntry(($Prefix + $relative.Replace('\', '/')), [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $destinationStream = $entry.Open()
            $sourceStream = [IO.File]::OpenRead($source)
            try { $sourceStream.CopyTo($destinationStream) }
            finally { $sourceStream.Dispose(); $destinationStream.Dispose() }
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
}

$manifest = @(Get-Content -LiteralPath (Join-Path $artifacts 'package-manifests\plugin.json') -Raw | ConvertFrom-Json)
if ($manifest.Count -lt 20) { throw 'Run Build.ps1 to create the complete portable payload before packaging.' }
$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $manifest) {
    if (!$expected.Add($file.Path)) { throw 'Duplicate runtime manifest path.' }
    $path = Assert-OrdinaryPath $payload $file.Path
    if ((Get-Item -LiteralPath $path).Length -ne $file.Length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Sha256) {
        throw 'The runtime payload changed after the build inventory was written. Rebuild before packaging.'
    }
}
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
    if (!$expected.Contains([IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/'))) { throw 'Unexpected file in the runtime payload. Rebuild before packaging.' }
}
$sourceFiles = @(Get-Content -LiteralPath (Join-Path $root 'SourceFiles.txt') | Where-Object { $_ -and !($_.StartsWith('#')) })
$sourceManifest = @(Get-Content -LiteralPath (Join-Path $artifacts 'package-manifests\source.json') -Raw | ConvertFrom-Json)
if (@(Compare-Object ($sourceManifest.Path | Sort-Object) ($sourceFiles | Sort-Object)).Count -ne 0) { throw 'The source file list changed after the build. Rebuild before packaging.' }
foreach ($file in $sourceFiles) {
    if ($file -match '(^|[/\\])(bin|obj|artifacts|\.git)([/\\]|$)' -or $file -match '\.(zip|pdb|user|log)$') { throw 'SourceFiles.txt includes a generated or unsupported file.' }
    $null = Assert-OrdinaryPath $root $file
}
foreach ($file in $sourceManifest) {
    if ((Get-FileHash -LiteralPath (Join-Path $root $file.Path) -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'Source changed after the build. Rebuild before packaging.' }
}
$version = (Get-Content -LiteralPath (Join-Path $payload 'Airwave.json') -Raw | ConvertFrom-Json).AssemblyVersion
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid package version.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (Test-Path -LiteralPath $relay) {
    if ([IO.Path]::GetFullPath($relay) -ne [IO.Path]::GetFullPath((Join-Path $root 'artifacts\relay'))) { throw 'Unexpected relay staging path.' }
    $null = Assert-OrdinaryPath $artifacts 'relay'
    if (Get-ChildItem -LiteralPath $relay -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Refusing to replace linked relay staging contents.' }
    Remove-Item -LiteralPath $relay -Recurse -Force
}
New-Item -ItemType Directory -Path $relay -Force | Out-Null
$relayFiles = [Collections.Generic.List[string]]::new()
foreach ($file in $manifest) {
    $relative = $null
    if ($file.Path.StartsWith('relay/')) { $relative = $file.Path.Substring(6) }
    elseif ($file.Path.StartsWith('licenses/') -or $file.Path -in @('LICENSE.md', 'THIRD-PARTY-NOTICES.md')) { $relative = $file.Path }
    if ($relative) {
        $destination = Join-Path $relay $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $payload $file.Path) -Destination $destination
        $relayFiles.Add($relative)
    }
}
$pluginZip = Join-Path $output "Airwave-$version-win-x64.zip"
$relayZip = Join-Path $output "Airwave-Relay-$version-win-x64.zip"
$sourceZip = Join-Path $output "Airwave-$version-source.zip"
New-Archive $payload @($manifest.Path) $pluginZip
New-Archive $relay $relayFiles.ToArray() $relayZip
New-Archive $root $sourceFiles $sourceZip 'Airwave/'
$checksums = foreach ($archive in @($pluginZip, $relayZip, $sourceZip)) { '{0}  {1}' -f (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($archive) }
$checksums | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Output 'Created plugin, standalone relay, and corresponding source archives in artifacts/packages.'
