[CmdletBinding()]
param(
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '..\GearsetOrganizer\bin\Release\net10.0-windows')
)

# Read PE metadata without loading Dalamud or connecting to a running game.
$ErrorActionPreference = 'Stop'
$buildRoot = [System.IO.Path]::GetFullPath($BuildDirectory)
$assemblyPath = Join-Path $buildRoot 'GearsetOrganizer.dll'
$manifestPath = Join-Path $buildRoot 'GearsetOrganizer.json'
$dependencyPath = Join-Path $buildRoot 'GearsetOrganizer.deps.json'
$packagePath = Join-Path $buildRoot 'GearsetOrganizer\latest.zip'
foreach ($path in @($assemblyPath, $manifestPath, $dependencyPath, $packagePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build output missing: $path" }
}

$assemblyStream = [System.IO.File]::OpenRead($assemblyPath)
$peReader = [System.Reflection.PortableExecutable.PEReader]::new($assemblyStream)
try {
    $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($peReader)
    $assemblyName = $metadata.GetString($metadata.GetAssemblyDefinition().Name)
    $assemblyVersion = $metadata.GetAssemblyDefinition().Version.ToString()
    if ($assemblyName -ne 'GearsetOrganizer') { throw "Unexpected assembly: $assemblyName" }

    $references = @($metadata.AssemblyReferences | ForEach-Object {
        $metadata.GetString($metadata.GetAssemblyReference($_).Name)
    })
    $allowedReferences = @(
        'System.Runtime', 'System.Text.Json', 'System.Collections', 'System.Linq',
        'System.Diagnostics.Process', 'System.Numerics.Vectors', 'System.Memory',
        'System.Security.Cryptography', 'System.Threading',
        'Dalamud', 'Dalamud.Bindings.ImGui', 'FFXIVClientStructs',
        'InteropGenerator.Runtime', 'Lumina', 'Lumina.Excel'
    )
    foreach ($reference in $references) {
        if ($reference -notin $allowedReferences) {
            throw "Unexpected assembly reference: $reference"
        }
    }

    $allowedTypes = @(
        'GearsetOrganizer.Plugin', 'GearsetOrganizer.GearsetEngine',
        'GearsetOrganizer.GearsetAutomationSignal', 'GearsetOrganizer.GearsetAutomationState',
        'GearsetOrganizer.GearsetAutomationPolicy', 'GearsetOrganizer.GearsetItemSnapshot',
        'GearsetOrganizer.GearsetEntrySnapshot', 'GearsetOrganizer.GearsetHotbarSlotSnapshot',
        'GearsetOrganizer.GearsetMacroSnapshot', 'GearsetOrganizer.GearsetEquippedItemSnapshot',
        'GearsetOrganizer.GearsetSaveState', 'GearsetOrganizer.GearsetSnapshot',
        'GearsetOrganizer.GearsetSnapshotReader', 'GearsetOrganizer.OrganizerIpc',
        'GearsetOrganizer.JsonValue', 'GearsetOrganizer.GearsetRow',
        'GearsetOrganizer.OrganizerView', 'GearsetOrganizer.OrganizerWindow',
        'GearsetOrganizer.Core.GearsetMacroLineResult', 'GearsetOrganizer.Core.GearsetMacroReferences',
        'GearsetOrganizer.Core.GearsetIdentity', 'GearsetOrganizer.Core.GearsetAssignment',
        'GearsetOrganizer.Core.GearsetMove', 'GearsetOrganizer.Core.GearsetOrganizationPlan',
        'GearsetOrganizer.Core.GearsetOrganization'
    )
    $types = @($metadata.TypeDefinitions | ForEach-Object {
        $definition = $metadata.GetTypeDefinition($_)
        if (-not $definition.IsNested) {
            $namespace = $metadata.GetString($definition.Namespace)
            $name = $metadata.GetString($definition.Name)
            if ($namespace -eq '' -and
                ($name -in @('<Module>', '<PrivateImplementationDetails>') -or
                 $name -match '^<>f__AnonymousType\d+`\d+$')) {
                # Compiler-generated top-level metadata has no application namespace.
            } else {
                $namespace + '.' + $name
            }
        }
    })
    foreach ($requiredType in $allowedTypes) {
        if ($requiredType -notin $types) { throw "Native implementation missing from plugin assembly: $requiredType" }
    }
    foreach ($type in $types) {
        if ($type -notin $allowedTypes) { throw "Unexpected top-level type in plugin assembly: $type" }
    }
} finally {
    $peReader.Dispose()
    $assemblyStream.Dispose()
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.InternalName -ne 'GearsetOrganizer' -or
    $manifest.AssemblyVersion -ne $assemblyVersion -or $manifest.DalamudApiLevel -ne 15) {
    throw 'Build manifest does not match the plugin identity, assembly version, and API level.'
}
$deps = Get-Content -LiteralPath $dependencyPath -Raw | ConvertFrom-Json
$libraries = @($deps.libraries.PSObject.Properties)
if ($libraries.Count -ne 1 -or $libraries[0].Name -notmatch '^GearsetOrganizer/\d+\.\d+\.\d+(?:\.\d+)?$' -or
    $libraries[0].Value.type -ne 'project') {
    throw 'The runtime dependency manifest must contain only the plugin project.'
}
$target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
$targetLibraries = @($target.PSObject.Properties)
if ($targetLibraries.Count -ne 1 -or $targetLibraries[0].Name -ne $libraries[0].Name) {
    throw 'The runtime target does not match the plugin project.'
}
$runtimeFiles = @($targetLibraries[0].Value.runtime.PSObject.Properties.Name)
if ($runtimeFiles.Count -ne 1 -or $runtimeFiles[0] -ne 'GearsetOrganizer.dll' -or
    @($targetLibraries[0].Value.PSObject.Properties.Name | Where-Object { $_ -ne 'runtime' }).Count -ne 0) {
    throw 'The runtime target must contain only GearsetOrganizer.dll.'
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entryNames = @($archive.Entries.FullName)
    if (@($entryNames | Select-Object -Unique).Count -ne $entryNames.Count) {
        throw 'The plugin package contains duplicate paths.'
    }
    $allowedFiles = @('GearsetOrganizer.dll', 'GearsetOrganizer.json', 'GearsetOrganizer.deps.json', 'GearsetOrganizer.pdb')
    foreach ($entry in $archive.Entries) {
        if ($entry.FullName -notin $allowedFiles) {
            throw "Unexpected packaged dependency or file: $($entry.FullName)"
        }
        $entryStream = $entry.Open()
        try {
            if ($entry.FullName -eq 'GearsetOrganizer.json') {
                # DalamudPackager normalizes manifests and may add default fields.
                $reader = [System.IO.StreamReader]::new($entryStream)
                try { $packagedManifest = $reader.ReadToEnd() | ConvertFrom-Json }
                finally { $reader.Dispose() }
                foreach ($field in @('InternalName', 'Name', 'AssemblyVersion', 'Description', 'DalamudApiLevel')) {
                    if ($packagedManifest.$field -ne $manifest.$field) {
                        throw "Packaged manifest differs from the built manifest: $field"
                    }
                }
                continue
            }
            $zipHash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($entryStream))
            $diskHash = (Get-FileHash -LiteralPath (Join-Path $buildRoot $entry.FullName) -Algorithm SHA256).Hash
            if ($zipHash -ne $diskHash) { throw "Package differs from built output: $($entry.FullName)" }
        } finally { $entryStream.Dispose() }
    }
    foreach ($requiredFile in @('GearsetOrganizer.dll', 'GearsetOrganizer.json', 'GearsetOrganizer.deps.json')) {
        if ($requiredFile -notin $entryNames) { throw "Runtime payload missing from package: $requiredFile" }
    }
} finally { $archive.Dispose() }

[pscustomobject]@{
    Success = $true
    Assembly = $assemblyName
    Version = $assemblyVersion
    EmbeddedEngine = $true
    ReferencesAllowed = $true
    PluginTypesVerified = $true
    PackageMatchesBuild = $true
    Package = $packagePath
    RuntimeReferences = $references
} | ConvertTo-Json -Depth 4
