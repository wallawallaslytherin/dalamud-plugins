#Requires -Version 7.0
[CmdletBinding()]
param([switch]$SkipTests, [string]$NativeBuildDirectory)
# Requires PowerShell 7. Build helpers as separate, inspectable runtime files.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed with exit code $LASTEXITCODE." }
}
$root = [IO.Path]::GetFullPath($PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($NativeBuildDirectory)) {
    $localCache = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localCache)) { throw 'The local build cache directory is unavailable.' }
    $NativeBuildDirectory = Join-Path $localCache 'Airwave\Build\CaptureHost'
}
$NativeBuildDirectory = [IO.Path]::GetFullPath($NativeBuildDirectory)
if ($NativeBuildDirectory.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
    $NativeBuildDirectory.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'NativeBuildDirectory must be outside the source tree.'
}
foreach ($project in @('Airwave\Airwave.csproj', 'Airwave.AudioHost\Airwave.AudioHost.csproj', 'Airwave.Relay\Airwave.Relay.csproj')) {
    Invoke-Checked 'dotnet' @('clean', (Join-Path $root $project), '-c', 'Release', '--nologo', '-v', 'quiet')
}
$cmake = (Get-Command cmake -ErrorAction Stop).Source
Invoke-Checked $cmake @('-S', (Join-Path $root 'Airwave.CaptureHost'), '-B', $NativeBuildDirectory, '-A', 'x64')
Invoke-Checked $cmake @('--build', $NativeBuildDirectory, '--config', 'Release')
Invoke-Checked 'dotnet' @('build', (Join-Path $root 'Airwave\Airwave.csproj'), '-c', 'Release', '--nologo', '-warnaserror')
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
$payload = [IO.Path]::GetFullPath((Join-Path $artifacts 'plugin'))
if ((Test-Path -LiteralPath $payload) -and ($payload -eq [IO.Path]::GetFullPath((Join-Path $root 'artifacts\plugin')))) {
    if ((Get-Item -LiteralPath $payload).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing to replace a linked output directory.' }
    Remove-Item -LiteralPath $payload -Recurse -Force
}
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$publishArguments = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:IsTransformWebConfigDisabled=true', '-p:DebugType=none', '-p:DebugSymbols=false', '--nologo', '-warnaserror')
Invoke-Checked 'dotnet' (@('publish', (Join-Path $root 'Airwave.AudioHost\Airwave.AudioHost.csproj'), '-o', (Join-Path $payload 'audio')) + $publishArguments)
Invoke-Checked 'dotnet' (@('publish', (Join-Path $root 'Airwave.Relay\Airwave.Relay.csproj'), '-o', (Join-Path $payload 'relay')) + $publishArguments)
foreach ($helper in @('audio', 'relay')) {
    foreach ($required in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload "$helper\$required") -PathType Leaf)) { throw "The $helper runtime is incomplete." }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $payload $helper) -Recurse -File) {
        if ($file.Extension -notin @('.dll', '.exe', '.json')) { throw "Unexpected publish output type: $($file.Extension)." }
    }
}
New-Item -ItemType Directory -Path (Join-Path $payload 'capture') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $NativeBuildDirectory 'Release\Airwave.Capture.exe') -Destination (Join-Path $payload 'capture')
Copy-Item -LiteralPath (Join-Path $root 'Airwave.CaptureHost\LICENSE.md') -Destination (Join-Path $payload 'capture')
Copy-Item -LiteralPath (Join-Path $root 'Airwave.CaptureHost\README.md') -Destination (Join-Path $payload 'capture')
Copy-Item -LiteralPath (Join-Path $root 'Airwave.AudioHost\README.md') -Destination (Join-Path $payload 'audio')
Copy-Item -LiteralPath (Join-Path $root 'Airwave.Relay\README.md') -Destination (Join-Path $payload 'relay')
foreach ($file in @('Setup-Relay.cmd', 'Start-Relay.cmd', 'Stop-Relay.cmd', 'Copy-Listener-Invite.cmd', 'Copy-Broadcast-Key.cmd')) {
    Copy-Item -LiteralPath (Join-Path $root "Airwave.Relay\$file") -Destination (Join-Path $payload 'relay')
}
$pluginOutput = Join-Path $root 'Airwave\bin\Release\net10.0-windows'
foreach ($file in @('Airwave.dll', 'Airwave.json', 'Airwave.deps.json', 'Airwave.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $pluginOutput $file) -Destination $payload
}
# The plugin and relay use the same protected-settings package version.
Copy-Item -LiteralPath (Join-Path $payload 'relay\System.Security.Cryptography.ProtectedData.dll') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'LICENSE.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $payload -Recurse
# Retain the notices from the exact runtime packs selected by this publish.
$runtimeVersions = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($helper in @('audio', 'relay')) {
    $name = if ($helper -eq 'audio') { 'Airwave.AudioHost' } else { 'Airwave.Relay' }
    $config = Get-Content -LiteralPath (Join-Path $payload "$helper\$name.runtimeconfig.json") -Raw | ConvertFrom-Json
    foreach ($framework in $config.runtimeOptions.includedFrameworks) { $runtimeVersions[$framework.name] = $framework.version }
}
$assets = Get-Content -LiteralPath (Join-Path $root 'Airwave.Relay\obj\project.assets.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtimeVersions.Keys) {
    $packageName = ($framework + '.Runtime.win-x64').ToLowerInvariant()
    $version = $runtimeVersions[$framework]
    $pack = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder "$packageName\$version"
        if (Test-Path -LiteralPath $candidate -PathType Container) { $pack = $candidate; break }
    }
    if (!$pack) { throw "The $framework runtime pack notices could not be located." }
    $prefix = if ($framework -eq 'Microsoft.NETCore.App') { 'DotNet-Runtime' } else { 'AspNetCore-Runtime' }
    foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $found = Get-ChildItem -LiteralPath $pack -File | Where-Object Name -IEQ $notice | Select-Object -First 1
        if (!$found) { throw "The $framework runtime pack is missing $notice." }
        Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $payload "licenses\$prefix-$notice")
    }
}
$runtimeVersions | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'licenses\runtime-versions.json') -Encoding utf8
$runtimeReadme = (Get-Content -LiteralPath (Join-Path $root 'README.md') -Raw).
    Replace('(Airwave.Relay/README.md)', '(relay/README.md)').
    Replace('(Airwave.CaptureHost/README.md)', '(capture/README.md)')
[IO.File]::WriteAllText((Join-Path $payload 'README.md'), $runtimeReadme)
if (!$SkipTests) {
    Invoke-Checked 'dotnet' @('test', (Join-Path $root 'Airwave.Transport.Tests\Airwave.Transport.Tests.csproj'), '-c', 'Release', '--nologo', '-warnaserror')
    Invoke-Checked 'dotnet' @('test', (Join-Path $root 'Airwave.Audio.Tests\Airwave.Audio.Tests.csproj'), '-c', 'Release', '--nologo', '-warnaserror')
    Invoke-Checked 'dotnet' @('run', '--project', (Join-Path $root 'Airwave.Lifecycle.Tests\Airwave.Lifecycle.Tests.csproj'), '-c', 'Release', '--no-launch-profile')
    Invoke-Checked (Join-Path $payload 'audio\Airwave.AudioHost.exe') @('--mode', 'test')
}
$manifestDirectory = Join-Path $artifacts 'package-manifests'
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
$inventory = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked runtime files cannot be packaged.' }
    [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/'); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $manifestDirectory 'plugin.json') -Encoding utf8
$sourceInventory = @(Get-Content -LiteralPath (Join-Path $root 'SourceFiles.txt') | Where-Object { $_ -and !($_.StartsWith('#')) } | ForEach-Object {
    $file = Join-Path $root $_
    [pscustomobject]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
})
$sourceInventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $manifestDirectory 'source.json') -Encoding utf8
Write-Output "Built payload: $payload"
