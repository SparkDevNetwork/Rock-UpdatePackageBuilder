<#
.SYNOPSIS
    Drafts a web.config.rock.xdt that brings each ref's stock web.config to the target's.

.DESCRIPTION
    Covers the two sections that change between releases: binding redirects and compilation assemblies. Each entry that
    differs gets a RemoveAll followed by an Insert, so the transform is safe to run on a config that already matches.
    Entries the target no longer has are removed. Other sections aren't drafted; check-transform.ps1 lists any that
    still differ, to be added by hand. Writes only the -Out file.

.EXAMPLE
    pwsh -File new-transform.ps1 -Repo D:\source\rock\Rock-packaging -Target 19.8.1-alpha -Refs "18.2.4 19.1.8" `
        -Out out\web.config.rock.xdt
#>
param(
    [Parameter( Mandatory )] [string] $Repo,
    [Parameter( Mandatory )] [string] $Target,
    [Parameter( Mandatory )] [string[]] $Refs,
    [Parameter( Mandatory )] [string] $Out
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Refs = @( $Refs | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ } )

$assemblyNamespace = 'urn:schemas-microsoft-com:asm.v1'
$namespaces = New-Object Xml.XmlNamespaceManager( ( New-Object Xml.NameTable ) )
$namespaces.AddNamespace( 'a', $assemblyNamespace )

function Get-WebConfig( [string] $ref ) {
    $document = New-Object Xml.XmlDocument
    $document.LoadXml( ( ( git -C $Repo show "${ref}:RockWeb/web.config" ) -join "`n" ).TrimStart( [char] 0xFEFF ) )
    return , $document
}

# Redirect elements keyed by lowercase assembly name.
function Get-Redirects( [xml] $document ) {
    $redirects = @{}
    foreach ( $dependent in $document.SelectNodes( '//a:dependentAssembly', $namespaces ) ) {
        $name = $dependent.SelectSingleNode( 'a:assemblyIdentity', $namespaces ).GetAttribute( 'name' )
        $redirects[$name.ToLower()] = @{ Name = $name; Xml = $dependent.OuterXml.Replace( " xmlns=`"$assemblyNamespace`"", '' ) }
    }
    return $redirects
}

# Compilation assembly strings keyed by lowercase assembly name (the part before the first comma).
function Get-Assemblies( [xml] $document ) {
    $assemblies = @{}
    foreach ( $add in $document.SelectNodes( '/configuration/system.web/compilation/assemblies/add' ) ) {
        $value = $add.GetAttribute( 'assembly' )
        $assemblies[$value.Split( ',' )[0].Trim().ToLower()] = $value
    }
    return $assemblies
}

function Escape( [string] $value ) {
    return [Security.SecurityElement]::Escape( $value )
}

$targetConfig = Get-WebConfig $Target
$targetRedirects = Get-Redirects $targetConfig
$targetAssemblies = Get-Assemblies $targetConfig
$sources = @( $Refs | ForEach-Object { $config = Get-WebConfig $_; @{ Redirects = Get-Redirects $config; Assemblies = Get-Assemblies $config } } )

# Binding redirects: replace any the target has that a ref lacks or has differently; remove any the target dropped.
$redirectLines = @()
$removedNames = @( $sources | ForEach-Object { $_.Redirects.Values } | Where-Object { -not $targetRedirects.ContainsKey( $_.Name.ToLower() ) } |
    ForEach-Object { $_.Name } | Sort-Object -Unique )
foreach ( $name in $removedNames ) {
    $redirectLines += "      <dependentAssembly xdt:Transform=`"RemoveAll`" xdt:Locator=`"Condition(./_defaultNamespace:assemblyIdentity/@name='$name')`" />"
}
foreach ( $key in ( $targetRedirects.Keys | Sort-Object ) ) {
    $wanted = $targetRedirects[$key]
    $changed = $sources | Where-Object { -not $_.Redirects.ContainsKey( $key ) -or $_.Redirects[$key].Xml -ne $wanted.Xml }
    if ( -not $changed ) { continue }
    $names = @( $wanted.Name ) + @( $sources | Where-Object { $_.Redirects.ContainsKey( $key ) } | ForEach-Object { $_.Redirects[$key].Name } ) | Sort-Object -Unique
    foreach ( $name in $names ) {
        $redirectLines += "      <dependentAssembly xdt:Transform=`"RemoveAll`" xdt:Locator=`"Condition(./_defaultNamespace:assemblyIdentity/@name='$name')`" />"
    }
    $redirectLines += '      ' + ( $wanted.Xml -replace '^<dependentAssembly', '<dependentAssembly xdt:Transform="Insert"' )
}

# Compilation assemblies: same idea, matched on the full assembly string.
$assemblyLines = @()
$allKeys = @( $targetAssemblies.Keys ) + @( $sources | ForEach-Object { $_.Assemblies.Keys } ) | Sort-Object -Unique
foreach ( $key in $allKeys ) {
    $wanted = $targetAssemblies[$key]
    $old = @( $sources | Where-Object { $_.Assemblies.ContainsKey( $key ) -and $_.Assemblies[$key] -ne $wanted } | ForEach-Object { $_.Assemblies[$key] } | Sort-Object -Unique )
    $lacking = $wanted -and ( $sources | Where-Object { $_.Assemblies[$key] -ne $wanted } )
    foreach ( $value in $old ) {
        $assemblyLines += "        <add assembly=`"$( Escape $value )`" xdt:Locator=`"Match(assembly)`" xdt:Transform=`"RemoveAll`" />"
    }
    if ( $lacking ) {
        $assemblyLines += "        <add assembly=`"$( Escape $wanted )`" xdt:Locator=`"Match(assembly)`" xdt:Transform=`"RemoveAll`" />"
        $assemblyLines += "        <add assembly=`"$( Escape $wanted )`" xdt:Transform=`"Insert`" />"
    }
}

if ( -not $redirectLines -and -not $assemblyLines ) {
    Write-Output "No redirect or assembly changes: every ref already matches $Target in those sections."
    exit 0
}

$lines = @( '<?xml version="1.0"?>', '<configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">' )
if ( $assemblyLines ) {
    $lines += '', "  <!-- Compilation assemblies for $Target -->", '  <system.web>', '    <compilation>', '      <assemblies>'
    $lines += $assemblyLines
    $lines += '      </assemblies>', '    </compilation>', '  </system.web>'
}
if ( $redirectLines ) {
    $lines += '', "  <!-- Binding redirects for $Target -->", '  <runtime>', "    <assemblyBinding xmlns=`"$assemblyNamespace`">"
    $lines += $redirectLines
    $lines += '    </assemblyBinding>', '  </runtime>'
}
$lines += '', '</configuration>', ''

[IO.File]::WriteAllText( $Out, ( $lines -join "`r`n" ) )
Write-Output "Drafted $Out ($( $assemblyLines.Count ) assembly lines, $( $redirectLines.Count ) redirect lines)."
