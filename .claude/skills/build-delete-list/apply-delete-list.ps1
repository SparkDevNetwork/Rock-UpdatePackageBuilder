<#
.SYNOPSIS
    Replaces install/deletefile.lst inside a .rockpkg, safely.

.DESCRIPTION
    Refuses if another program has the package open. Backs the package up first, changes only the delete list, then
    confirms every other file in the package is byte-for-byte unchanged. Restores the backup if anything fails.

.EXAMPLE
    pwsh -File apply-delete-list.ps1 -Package RockPackages\19.8.rockpkg -List out\deletefile.lst -BackupFolder out
#>
param(
    [Parameter( Mandatory )] [string] $Package,
    [Parameter( Mandatory )] [string] $List,
    [Parameter( Mandatory )] [string] $BackupFolder
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression

$Package = ( Resolve-Path $Package ).Path
$newBytes = [IO.File]::ReadAllBytes( ( Resolve-Path $List ).Path )

function Get-Snapshot( [string] $path ) {
    $zip = [IO.Compression.ZipFile]::OpenRead( $path )
    try {
        $entries = @{}
        foreach ( $entry in $zip.Entries ) {
            $entries[$entry.FullName] = "$( $entry.Length ):$( $entry.Crc32 )"
        }
        $listEntry = $zip.Entries | Where-Object { $_.FullName -ieq 'install/deletefile.lst' } | Select-Object -First 1
        $listBytes = $null
        if ( $listEntry ) {
            $stream = $listEntry.Open()
            $memory = New-Object IO.MemoryStream
            $stream.CopyTo( $memory )
            $stream.Close()
            $listBytes = $memory.ToArray()
        }
        return @{ Entries = $entries; ListName = $listEntry.FullName; ListBytes = $listBytes }
    }
    finally {
        $zip.Dispose()
    }
}

# Another program holding the file (7-Zip, the builder, Explorer) could save over our change.
try {
    $lock = [IO.File]::Open( $Package, 'Open', 'ReadWrite', 'None' )
    $lock.Close()
}
catch {
    throw "The package is open in another program. Close it and run again."
}

$before = Get-Snapshot $Package
if ( $before.ListBytes -and [Linq.Enumerable]::SequenceEqual( [byte[]] $before.ListBytes, $newBytes ) ) {
    Write-Output "The package's delete list already matches. Nothing changed."
    return
}

$listName = if ( $before.ListName ) { $before.ListName } else { 'install/deletefile.lst' }
$backup = Join-Path ( New-Item -ItemType Directory -Force $BackupFolder ).FullName ( "{0}.{1:yyyyMMdd-HHmmss}.bak" -f ( Split-Path $Package -Leaf ), ( Get-Date ) )
Copy-Item $Package $backup

try {
    $zip = [IO.Compression.ZipFile]::Open( $Package, 'Update' )
    try {
        @( $zip.Entries | Where-Object { $_.FullName -ieq $listName } ) | ForEach-Object { $_.Delete() }
        $stream = $zip.CreateEntry( $listName, 'Optimal' ).Open()
        $stream.Write( $newBytes, 0, $newBytes.Length )
        $stream.Close()
    }
    finally {
        $zip.Dispose()
    }

    $after = Get-Snapshot $Package
    $problems = @()
    foreach ( $name in $before.Entries.Keys ) {
        if ( $name -ieq $listName ) { continue }
        if ( $after.Entries[$name] -ne $before.Entries[$name] ) { $problems += $name }
    }
    if ( $after.Entries.Count -ne ( $before.Entries.Count + [int]( -not $before.ListName ) ) ) { $problems += '(file count changed)' }
    if ( -not [Linq.Enumerable]::SequenceEqual( [byte[]] $after.ListBytes, $newBytes ) ) { $problems += $listName }
    if ( $problems ) { throw "Verification failed: $( $problems -join ', ' )" }
}
catch {
    Copy-Item $backup $Package -Force
    throw "Restored the package from the backup. $_"
}

Write-Output "Replaced $listName in $Package."
Write-Output "Every other file in the package is unchanged. Backup: $backup"
