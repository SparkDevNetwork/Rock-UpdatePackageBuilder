<#
.SYNOPSIS
    Checks a web.config transform against every web.config version a site could have.

.DESCRIPTION
    For each ref, applies the xdt in memory to that ref's stock web.config and compares the result with the target's:
    every binding redirect must match, and every other section must be identical. Redirect order doesn't matter.
    Takes the xdt from a file (-Xdt) or from a package's content/web.config.rock.xdt (-Package). Changes nothing.
    Exits 1 when any ref fails or there is no xdt.

.EXAMPLE
    pwsh -File check-transform.ps1 -Repo D:\source\rock\Rock-packaging -Package RockPackages\19.8.rockpkg `
        -Target 19.8.1-alpha -Refs "18.2.4 19.1.8"
#>
param(
    [Parameter( Mandatory )] [string] $Repo,
    [Parameter( Mandatory )] [string] $Target,
    [Parameter( Mandatory )] [string[]] $Refs,
    [string] $Xdt,
    [string] $Package
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Refs = @( $Refs | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ } )
Add-Type -AssemblyName System.IO.Compression
Add-Type -Path ( Join-Path $PSScriptRoot '..\..\..\packages\Microsoft.Web.Xdt.1.0.0\lib\net40\Microsoft.Web.XmlTransform.dll' )

$namespaces = New-Object Xml.XmlNamespaceManager( ( New-Object Xml.NameTable ) )
$namespaces.AddNamespace( 'a', 'urn:schemas-microsoft-com:asm.v1' )

function Get-WebConfig( [string] $ref ) {
    $text = ( git -C $Repo show "${ref}:RockWeb/web.config" ) -join "`n"
    return $text.TrimStart( [char] 0xFEFF )
}

# Binding redirects keyed by assembly name, so order doesn't matter.
function Get-Redirects( [xml] $document ) {
    $redirects = @{}
    foreach ( $dependent in $document.SelectNodes( '//a:dependentAssembly', $namespaces ) ) {
        $identity = $dependent.SelectSingleNode( 'a:assemblyIdentity', $namespaces )
        $redirect = $dependent.SelectSingleNode( 'a:bindingRedirect', $namespaces )
        $name = $identity.GetAttribute( 'name' ).ToLower()
        $value = "$( $identity.GetAttribute( 'publicKeyToken' ) ) $( $redirect.GetAttribute( 'oldVersion' ) ) -> $( $redirect.GetAttribute( 'newVersion' ) )"
        $redirects[$name] = if ( $redirects.ContainsKey( $name ) ) { "$( $redirects[$name] ) DUPLICATE $value" } else { $value }
    }
    return $redirects
}

# Everything except the binding redirects, one line per element.
function Get-OtherSections( [xml] $document ) {
    $copy = $document.Clone()
    foreach ( $binding in @( $copy.SelectNodes( '//a:assemblyBinding', $namespaces ) ) ) {
        [void] $binding.ParentNode.RemoveChild( $binding )
    }
    $writer = New-Object IO.StringWriter
    $xmlWriter = New-Object Xml.XmlTextWriter( $writer )
    $xmlWriter.Formatting = 'Indented'
    $copy.WriteTo( $xmlWriter )
    $xmlWriter.Flush()
    return $writer.ToString()
}

if ( $Xdt ) {
    $transform = [IO.File]::ReadAllText( ( Resolve-Path $Xdt ).Path )
}
elseif ( $Package ) {
    $zip = [IO.Compression.ZipFile]::OpenRead( ( Resolve-Path $Package ).Path )
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -ieq 'content/web.config.rock.xdt' } | Select-Object -First 1
        if ( -not $entry ) {
            Write-Output 'FAIL: the package has no content/web.config.rock.xdt.'
            exit 1
        }
        $reader = New-Object IO.StreamReader( $entry.Open() )
        $transform = $reader.ReadToEnd()
        $reader.Close()
    }
    finally {
        $zip.Dispose()
    }
}
else {
    throw 'Pass -Xdt or -Package.'
}

$targetDocument = New-Object Xml.XmlDocument
$targetDocument.LoadXml( ( Get-WebConfig $Target ) )
$targetRedirects = Get-Redirects $targetDocument
$targetOther = Get-OtherSections $targetDocument
$failed = $false

foreach ( $ref in $Refs ) {
    $document = New-Object Microsoft.Web.XmlTransform.XmlTransformableDocument
    $document.LoadXml( ( Get-WebConfig $ref ) )
    $applied = ( New-Object Microsoft.Web.XmlTransform.XmlTransformation( $transform, $false, $null ) ).Apply( $document )
    $redirects = Get-Redirects $document
    $other = Get-OtherSections $document

    $missing = @( $targetRedirects.Keys | Where-Object { -not $redirects.ContainsKey( $_ ) } | Sort-Object )
    $different = @( $targetRedirects.Keys | Where-Object { $redirects.ContainsKey( $_ ) -and $redirects[$_] -ne $targetRedirects[$_] } | Sort-Object )
    $extra = @( $redirects.Keys | Where-Object { -not $targetRedirects.ContainsKey( $_ ) } | Sort-Object )
    # Element order doesn't matter (Insert appends), so compare the lines as sorted lists.
    $otherMatch = ( ( $other -split "`r?`n" | Sort-Object ) -join "`n" ) -eq ( ( $targetOther -split "`r?`n" | Sort-Object ) -join "`n" )

    $pass = $applied -and -not $missing -and -not $different -and -not $extra -and $otherMatch
    $failed = $failed -or -not $pass
    Write-Output ( '{0} {1}: {2} redirects, {3} missing, {4} different, {5} extra; other sections {6}' -f `
        ( $pass ? 'PASS' : 'FAIL' ), $ref, $redirects.Count, $missing.Count, $different.Count, $extra.Count,
        ( $otherMatch ? 'match' : 'DIFFER' ) )
    $missing | Select-Object -First 5 | ForEach-Object { "    missing   $_ ($( $targetRedirects[$_] ))" }
    $different | Select-Object -First 5 | ForEach-Object { "    different $_ (target $( $targetRedirects[$_] ), got $( $redirects[$_] ))" }
    $extra | Select-Object -First 5 | ForEach-Object { "    extra     $_ ($( $redirects[$_] ))" }
    if ( -not $otherMatch ) {
        Compare-Object ( $targetOther -split "`r?`n" ) ( $other -split "`r?`n" ) | Select-Object -First 10 | ForEach-Object {
            "    {0} {1}" -f ( $_.SideIndicator -eq '<=' ? 'target only:' : 'result only:' ), $_.InputObject.Trim()
        }
    }
}

exit [int] $failed
