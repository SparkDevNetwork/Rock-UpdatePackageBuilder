using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RockPackageBuilder
{
    internal class RockPackageBuilder
    {
        public static string BuildRockPackage( RockPackageBuilderCommandLineOptions options, List<string> modifiedLibs, List<string> packageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects, string description, out string actionWarnings )
        {
            actionWarnings = string.Empty;
            string version = options.CurrentVersionTag;
            string updatePackageFileName = Path.Combine( options.PackageFolder, $"{version}.rockpkg" );

            if ( File.Exists( updatePackageFileName ) )
            {
                File.Delete( updatePackageFileName );
            }

            using ( var packageFile = ZipFile.Open( updatePackageFileName, ZipArchiveMode.Create ) )
            {

                string webRootPath = Path.Combine( options.RepoPath, "RockWeb" );
                foreach ( string file in packageFiles )
                {
                    // Skip the root web.config, but YELL to let the person know they need to do something with this.
                    if ( file.ToLower() == "web.config" )
                    {
                        var xdtPath = file.Replace( "web.config", "web.config.rock.xdt" );

                        if ( File.Exists( Path.Combine( webRootPath, xdtPath ) ) )
                        {
                            AddContentFileToPackage( packageFile, xdtPath, webRootPath );
                            continue;
                        }

                        actionWarnings += Constants.WEBCONFIG_XDT_MESSAGE;
                        continue;
                    }

                    AddContentFileToPackage( packageFile, file, webRootPath );

                    // if a less file was updated, check to see if a matching css file exists, and if so, add it (these files are ignored by repo)
                    if ( file.ToLower().EndsWith( ".less" ) )
                    {
                        string cssPath = file.Substring( 0, file.Length - 5 ) + ".css";
                        if ( File.Exists( Path.Combine( webRootPath, cssPath ) ) )
                        {
                            AddContentFileToPackage( packageFile, cssPath, webRootPath );
                        }
                    }
                }

                foreach ( string file in modifiedLibs )
                {
                    AddContentFileToPackage( packageFile, file, webRootPath );
                }

                // Add any modified Rock project libs but warn the user that they MUST be recently compiled
                // against the master head we're operating against.
                if ( modifiedProjects.Count > 0 )
                {
                    foreach ( KeyValuePair<string, bool> entry in modifiedProjects )
                    {
                        AddContentFileToPackage( packageFile, Path.Combine( "bin", entry.Key + ".dll" ), webRootPath );

                        // if a dll was updated and there is an associated xml documentation file, include it 
                        string xmlPath = Path.Combine( "bin", entry.Key + ".xml" );
                        if ( File.Exists( Path.Combine( webRootPath, xmlPath ) ) )
                        {
                            AddContentFileToPackage( packageFile, xmlPath, webRootPath );
                        }

                        // if a dll was updated and there is an associated pdb documentation file, include it
                        if ( options.IncludePdb || Constants.DEFAULT_PDB_TO_INCLUDE.Contains( entry.Key.ToLower() ) )
                        {
                            string pdbPath = Path.Combine( "bin", entry.Key + ".pdb" );
                            if ( File.Exists( Path.Combine( webRootPath, pdbPath ) ) )
                            {
                                AddContentFileToPackage( packageFile, pdbPath, webRootPath );
                            }
                        }
                    }
                }

                // write out all the files to delete in the deletefile.lst ) 
                string deleteFileRelativePath = Path.Combine( "App_Data", "deletefile.lst" );
                string deleteFileFullPath = Path.Combine( webRootPath, deleteFileRelativePath );
                if ( File.Exists( deleteFileFullPath ) )
                {
                    File.Delete( deleteFileFullPath );
                }

                if ( deletedPackageFiles.Count > 0 )
                {
                    using ( StreamWriter w = File.AppendText( deleteFileFullPath ) )
                    {
                        foreach ( string delete in deletedPackageFiles )
                        {
                            w.WriteLine( delete );
                        }
                    }

                    AddFileToPackage(
                        packageFile,
                        Path.Combine( webRootPath, deleteFileRelativePath ),
                        Path.Combine( "install", Path.GetFileName( deleteFileRelativePath ) )
                    );
                }

                // Then Run.Migration file is not needed for version 1.11 and up.
                int[] versionParts = options.CurrentVersionTag.Split( '.' ).Select( v => int.Parse( v ) ).ToArray();
                if ( versionParts[0] < 2 && versionParts[1] < 11 )
                {
                    // always add a Run.Migration flag file
                    string migrationFlagFileRelativePath = Path.Combine( "App_Data", "Run.Migration" );
                    string migrationFlagFileFullPath = Path.Combine( webRootPath, migrationFlagFileRelativePath );
                    File.Create( migrationFlagFileFullPath ).Dispose();
                    AddContentFileToPackage( packageFile, migrationFlagFileRelativePath, webRootPath );
                }
            }

            return updatePackageFileName;
        }

        private static void AddContentFileToPackage( ZipArchive packageFile, string filePath, string webRootPath )
        {
            var source = Path.Combine( webRootPath, filePath );
            var target = Path.Combine( "content", filePath );

            AddFileToPackage( packageFile, source, target );
        }

        private static void AddFileToPackage( ZipArchive packageFile, string source, string target )
        {
            if ( !File.Exists( source ) )
            {
                return;
            }

            packageFile.CreateEntryFromFile( source, target );
        }
    }
}
