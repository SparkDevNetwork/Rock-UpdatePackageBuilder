<#
.SYNOPSIS
    Puts a web.config.rock.xdt into a .rockpkg (content/web.config.rock.xdt) and the target's Artifacts folder, safely.

.DESCRIPTION
    Refuses if another program has the package open. Backs up the package (and any different xdt already in the
    Artifacts folder) first, changes only the xdt, then confirms every other file in the package is byte-for-byte
    unchanged. Restores the backup if anything fails.

.EXAMPLE
    pwsh -File apply-transform.ps1 -Package RockPackages\19.8.rockpkg -Xdt out\web.config.rock.xdt `
        -ArtifactsFolder Artifacts\19.8 -BackupFolder out
#>
param(
    [Parameter( Mandatory )] [string] $Package,
    [Parameter( Mandatory )] [string] $Xdt,
    [Parameter( Mandatory )] [string] $ArtifactsFolder,
    [Parameter( Mandatory )] [string] $BackupFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$Package = ( Resolve-Path $Package ).Path
$newBytes = [IO.File]::ReadAllBytes( ( Resolve-Path $Xdt ).Path )
$entryName = 'content/web.config.rock.xdt'
$backupRoot = ( New-Item -ItemType Directory -Force $BackupFolder ).FullName
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

function Test-SameBytes( [byte[]] $first, [byte[]] $second ) {
    return $first -and $second -and [Linq.Enumerable]::SequenceEqual( $first, $second )
}

function Get-Snapshot( [string] $path ) {
    $zip = [IO.Compression.ZipFile]::OpenRead( $path )
    try {
        $entries = @{}
        foreach ( $entry in $zip.Entries ) {
            $entries[$entry.FullName] = "$( $entry.Length ):$( $entry.Crc32 )"
        }
        $xdtEntry = $zip.Entries | Where-Object { $_.FullName -ieq $entryName } | Select-Object -First 1
        $bytes = $null
        if ( $xdtEntry ) {
            $stream = $xdtEntry.Open()
            $memory = New-Object IO.MemoryStream
            $stream.CopyTo( $memory )
            $stream.Close()
            $bytes = $memory.ToArray()
        }
        return @{ Entries = $entries; Name = $xdtEntry.FullName; Bytes = $bytes }
    }
    finally {
        $zip.Dispose()
    }
}

# Artifacts: keep a copy of a different xdt before replacing it.
$artifactsPath = Join-Path ( New-Item -ItemType Directory -Force $ArtifactsFolder ).FullName 'web.config.rock.xdt'
if ( ( Test-Path $artifactsPath ) -and ( Test-SameBytes ( [IO.File]::ReadAllBytes( $artifactsPath ) ) $newBytes ) ) {
    Write-Output "Artifacts already has this xdt: $artifactsPath"
}
else {
    if ( Test-Path $artifactsPath ) {
        Copy-Item $artifactsPath ( Join-Path $backupRoot "web.config.rock.xdt.$stamp.bak" )
    }
    [IO.File]::WriteAllBytes( $artifactsPath, $newBytes )
    Write-Output "Saved the xdt to $artifactsPath"
}

# Another program holding the package (7-Zip, the builder, Explorer) could save over our change.
try {
    $lock = [IO.File]::Open( $Package, 'Open', 'ReadWrite', 'None' )
    $lock.Close()
}
catch {
    throw "The package is open in another program. Close it and run again."
}

$before = Get-Snapshot $Package
if ( Test-SameBytes $before.Bytes $newBytes ) {
    Write-Output "The package already has this xdt. Nothing changed."
    return
}

$name = if ( $before.Name ) { $before.Name } else { $entryName }
$backup = Join-Path $backupRoot ( "{0}.{1}.bak" -f ( Split-Path $Package -Leaf ), $stamp )
Copy-Item $Package $backup

try {
    $zip = [IO.Compression.ZipFile]::Open( $Package, 'Update' )
    try {
        @( $zip.Entries | Where-Object { $_.FullName -ieq $name } ) | ForEach-Object { $_.Delete() }
        $stream = $zip.CreateEntry( $name, 'Optimal' ).Open()
        $stream.Write( $newBytes, 0, $newBytes.Length )
        $stream.Close()
    }
    finally {
        $zip.Dispose()
    }

    $after = Get-Snapshot $Package
    $problems = @()
    foreach ( $key in $before.Entries.Keys ) {
        if ( $key -ieq $name ) { continue }
        if ( $after.Entries[$key] -ne $before.Entries[$key] ) { $problems += $key }
    }
    if ( $after.Entries.Count -ne ( $before.Entries.Count + [int]( -not $before.Name ) ) ) { $problems += '(file count changed)' }
    if ( -not ( Test-SameBytes $after.Bytes $newBytes ) ) { $problems += $name }
    if ( $problems ) { throw "Verification failed: $( $problems -join ', ' )" }
}
catch {
    Copy-Item $backup $Package -Force
    throw "Restored the package from the backup. $_"
}

Write-Output "Put the xdt into $Package as $name."
Write-Output "Every other file in the package is unchanged. Backup: $backup"
