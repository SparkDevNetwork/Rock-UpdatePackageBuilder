using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibGit2Sharp;
using CommandLine;
using CommandLine.Text;
using NuGet;
using System.Reflection;
using System.Diagnostics;

namespace RockPackageBuilder
{
    /// <summary>
    /// The Rock Update package builder.  Read about it here:
    /// https://github.com/SparkDevNetwork/Rock-ChMS/wiki/Packaging-Rock-Core-Updates
    /// </summary>
    class BuildPackages
    {
        #region Properties
        
        /// <summary>
        /// Name of the NuGet Package Prefix for the actual update delta packages.
        /// </summary>
        static string ROCKUPDATE_PACKAGE_PREFIX = "RockUpdate";

        /// <summary>
        /// The warning message for when web.config transformation is needed.
        /// </summary>
        static readonly string WEBCONFIG_XDT_MESSAGE = @"--> ACTION! web.config file was changed since last build. Figure out
            how you're going to handle that. You'll probably have to
            create a web.config.rock.xdt file. See the Packaging-Rock-Core-Updates
            wiki page for details on doing that.";

        /// <summary>
        /// The projects to ignore. These projects will not be used to create the package and 
        /// files with these names will be excluded in the bin folder compare.
        /// </summary>
        static List<string> PROJECTS_TO_IGNORE = new List<string>
        {
            "rock.codegeneration",
            "rock.mywell",
            "rock.specs",
            "rock.tests",
            "rock.tests.integration",
            "rock.tests.shared",
            "rock.tests.unittests"
        };

        static string _previousVersion = string.Empty;

        static string _defaultDescription = "##TODO##";

        #endregion

        // Define a class to receive parsed values
        class Options
        {
            /// <summary>
            /// Future use.  Will record the current head SHA so that the next package can be built using it.
            /// </summary>

            [Option( 'l', "lastVersionTag", Required = true, HelpText = "The last tag to compare with the current tag to build the delta for the package. (Ex: 0.1.3)" )]
            public string LastVersionTag { get; set; }

            [Option( 'c', "currentVersionTag", Required = true, HelpText = "The current tag to compare with the last tag to build the delta for the package. (Ex: 0.1.4)" )]
            public string CurrentVersionTag { get; set; }

            [Option( 'h', "currentVersionCommitHash", Required = false, HelpText = "Use only for testing! The hash of the current commit to compare with the last tag to build the delta for the package. NOTE: Supplying this will OVERRIDE the given current tag. (Ex: afd14572e3f1529ce0007fe0b7524becee626e55)" )]
            public string CurrentVersionCommitHash { get; set; }

            [Option( 'r', "repoPath", DefaultValue = @"C:\Users\dturner\Dropbox\Projects\SparkDevNetwork\Rock", HelpText = "The path to your local git repository." )]
            public string RepoPath { get; set; }

            [Option( 'p', "packageFolder", DefaultValue = @"..\..\..\NuGetLocal", HelpText = "The folder to put the output package." )]
            public string PackageFolder { get; set; }

            [Option( 'i', "installArtifactsFolder", DefaultValue = @"..\..\..\InstallerArtifacts", HelpText = "The folder to put the empty dummy packages for use with the installer." )]
            public string InstallArtifactsFolder { get; set; }

            [Option( 'v', "verbose", DefaultValue = false, HelpText = "Set to true to see a more verbose output of what's changed in the repo." )]
            public bool Verbose { get; set; }

            [Option( 't', "testing", DefaultValue = false, HelpText = "Set to true to just see the list of changed files between the two tagged releases." )]
            public bool Testing { get; set; }

            [Option( 'b', "previousVersionBinFolderPath", Required = false, HelpText = "The path to a clean bin folder of the previous version. Used to compare bin folders and get files that are not included in the solution." )]
            public string PreviousVersionBinFolderPath { get; set; }

            public List<string> ProjectsInSolution { get; set; }

            [ParserState]
            public IParserState LastParserState { get; set; }

            [HelpOption]
            public string GetUsage()
            {
                return HelpText.AutoBuild( this, ( HelpText current ) => HelpText.DefaultParsingErrorsHandler( this, current ) );
            }
        }

        /// <summary>
        /// Entry point and check command line parameters.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static int Main( string[] args )
        {
            Console.BufferHeight = 9999;
            var options = new Options();
            var parser = new CommandLine.Parser( with => with.HelpWriter = Console.Error );
            if ( parser.ParseArgumentsStrict( args, options, () => Exit() ) )
            {
                if ( !Directory.Exists( options.RepoPath ) )
                {
                    Console.WriteLine( string.Format( "The given repository folder ({0}) does not exist.", options.RepoPath ) );
                    Console.WriteLine( options.GetUsage() );
                    Exit();
                }
                else if ( !Directory.Exists( options.PackageFolder ) )
                {
                    Console.WriteLine( string.Format( "The given output package folder ({0}) does not exist.", options.PackageFolder ) );
                    Console.WriteLine( options.GetUsage() );
                    Exit();
                }
                else if ( !Directory.Exists( options.InstallArtifactsFolder ) )
                {
                    Directory.CreateDirectory( options.InstallArtifactsFolder );
                }
                else
                {
                    return Run( options );
                }
            }
            return 0;
        }

        /// <summary>
        /// The main runner.
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static int Run( Options options )
        {
            List<string> modifiedLibs = new List<string>();
            List<string> modifiedPackageFiles = new List<string>();
            List<string> deletedPackageFiles = new List<string>();
            Dictionary<string, bool> modifiedProjects = new Dictionary<string, bool>();

            options.ProjectsInSolution = GetProjects( options );

            if ( ! ContinueAfterReadingSplashScreen( options ) )
            {
                return 1;
            }

            string publicVersion = options.CurrentVersionTag.Substring( 2 );

            _defaultDescription = $"Rock McKinley {publicVersion} fixes issues that were reported during the previous release(s) (See <a href='https://www.rockrms.com/releasenotes?version#v{publicVersion}'>Release Notes</a> for details).";

            string packagePath = FullPathOfRockPackageFile( options.PackageFolder, options.CurrentVersionTag );
            if ( File.Exists( packagePath ) )
            {
                Console.Write( "The package {0} already exists.{1}Do you want to overwrite it? Y/N (n to quit) ", packagePath, Environment.NewLine );
                ConsoleKeyInfo choice = Console.ReadKey(true);
                Console.Write( "\b" );
                Console.WriteLine( "" );
                if ( choice.KeyChar.ToString().ToLowerInvariant() != "y" )
                {
                    return 1;
                }

                var existingManifestPackage = Path.ChangeExtension( packagePath, "nuspec" );
                if ( File.Exists( existingManifestPackage ) )
                {
                    try
                    {
                        using ( var existingManifestStream = File.OpenRead( existingManifestPackage ) )
                        {
                            var existingManifest = Manifest.ReadFrom( existingManifestStream, false );
                            _defaultDescription = existingManifest.Metadata.Description;
                        }
                    }
                    catch
                    {
                        // intentionally ignore
                    }
                }
            }

            GetRockWebChangedFilesAndProjects( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects );
            GetUpdatedFilesNotInSolution( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects );
            
            /*
                6/29/2020 - NA
                The Rock.Common.Mobile dll is not part of the Rock solution, and it must always be
                pulled from NuGet, therefore it should always be included in the list of files to be
                put into the RockUpdate package.

                Reason: Assembly is not part of Rock Solution but needs to be in the UpdatePackage. 
            */
            //modifiedProjects[ "Rock.Common.Mobile" ] = true;

            // Make sure the Rock.Version project's version number has been updated and commit->pushed
            // before you build from master.
            if ( !VerifyVersionNumbers( options.RepoPath, options.CurrentVersionTag, modifiedProjects ) )
            {
                return 1;
            }

            string actionWarnings = string.Empty;

            if ( !options.Testing )
            {
                var updatePackageName = BuildUpdatePackage( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects, "various changes", out actionWarnings );

                // Create wrapper Rock.X.Y.Z.nupkg package as per: https://github.com/SparkDevNetwork/Rock-ChMS/wiki/Packaging-Rock-Core-Updates
                BuildRockPackage( updatePackageName, options.RepoPath, options.PackageFolder, options.CurrentVersionTag, _defaultDescription );
                BuildEmptyStubPackagesForInstaller( options.RepoPath, options.InstallArtifactsFolder, options.CurrentVersionTag );
            }
            else
            {
                OnlyOutputChanges( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects, "various changes", out actionWarnings );
            }

            if ( ! string.IsNullOrEmpty( actionWarnings ) )
            {
                Console.WriteLine( "" );
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine( actionWarnings );
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            if ( string.IsNullOrWhiteSpace( options.PreviousVersionBinFolderPath ) )
            {
                Console.ForegroundColor = ConsoleColor.Blue;
                Console.WriteLine( "" );
                Console.WriteLine( "CRITICAL NOTE: There are many assemblies that Rock needs which are" );
                Console.WriteLine( "no longer managed in our Github, so it's up to you to verify that the right" );
                Console.WriteLine( "DLLs and versions are included in the update package's lib folder." );
                Console.ResetColor();
            }

            Console.WriteLine( "" );
            Console.Write( "Press any key to finish." );
            Console.ReadKey(true);

            return 0;
        }

        /// <summary>
        /// Parse the solution file to get a list of the projects
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns></returns>
        private static List<string> GetProjects( Options options )
        {
            string solutionPath = Path.Combine( options.RepoPath, "Rock.sln" );
            var solutionText = File.ReadAllText( solutionPath );

            Regex projReg = new Regex( "Project\\(\"\\{[\\w-]*\\}\"\\) = \"([\\w _]*.*)\", \"(.*\\.(cs|vcx|vb)proj)\"", RegexOptions.Compiled );
            var matches = projReg.Matches( solutionText ).Cast<Match>();
            var projects = matches.Select( x => x.Groups[1].Value.ToLower() ).ToList();

            return projects.Where( p => !PROJECTS_TO_IGNORE.Contains( p ) ).ToList();
        }

        /// <summary>
        /// An important message for the packager to read before continuing.
        /// </summary>
        /// <returns>False if packaging should not continue.</returns>
        private static bool ContinueAfterReadingSplashScreen( Options options )
        {
            string repoPath = options.RepoPath;

            Console.Write( "Building update package from version " ); 
            Console.BackgroundColor = ConsoleColor.Blue;
            Console.Write( options.LastVersionTag );
            Console.ResetColor();
            Console.Write( " to version " );
            Console.BackgroundColor = ConsoleColor.Blue;
            Console.Write( options.CurrentVersionTag );
            Console.ResetColor();

            Console.WriteLine( "" );
            Console.WriteLine( "Make sure you've updated the version numbers, pushed to master and have locally" );
            Console.WriteLine( "built a RELEASE build. (Those assemblies may be added to the package.)" );
            Console.WriteLine( "" );

            // Look for any Rock.* folders in the repo path and show each one that is not
            // included in the NON_WEB_PROJECTS to the packager.  Because any of these would not
            // otherwise be included in the package -- even if there are changes to the files/code
            // in those projects.

            var missing = new List<string>();
            var dir = new System.IO.DirectoryInfo( repoPath );
            foreach ( var folder in dir.GetDirectories( "Rock.*", SearchOption.TopDirectoryOnly ) )
            {
                var name = folder.Name.ToLower();
                if ( PROJECTS_TO_IGNORE.Contains( name ) )
                {
                    continue;
                }

                if ( name.StartsWith( "rock" ) && ! options.ProjectsInSolution.Contains( folder.Name.ToLower() ) &&  File.Exists( Path.Combine( folder.FullName, folder + ".csproj" ) ) )
                {
                    missing.Add( folder.Name );
                }
            }

            if ( missing.Any() )
            {
                Console.WriteLine( "WARNING! The following are Rock.* projects that are not included in the " );
                Console.WriteLine( "         NON_WEB_PROJECTS list.  That means changes to these projects " );
                Console.Write(     "         " );
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write( "will not be included " );
                Console.ResetColor();
                Console.WriteLine( "as updated DLLs in the bin/lib folder:" );

                foreach ( var name in missing )
                {
                    Console.WriteLine( "\t\t* " + name );
                }
                Console.WriteLine();
                Console.Write( "         " );
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine( "Please VERIFY this is what you expect." );
                Console.ResetColor();
                Console.WriteLine();

            }

            Console.Write( "Do you want to continue? Y/N (n to quit) ", Environment.NewLine );
            ConsoleKeyInfo choice = Console.ReadKey( true );
            Console.Write( "\b" );
            Console.WriteLine( "" );
            if ( choice.KeyChar.ToString().ToLowerInvariant() != "y" )
            {
                Console.WriteLine( "" );
                return false;
            }
            Console.WriteLine( "" );
            return true;
        }

        /// <summary>
        /// Verifies that the Rock assemblies version numbers match the update package that's being built.
        /// </summary>
        /// <param name="repoPath">path to your rock repository</param>
        /// <param name="version">version number to verify/match</param>
        /// <param name="modifiedProjects">The modified projects.</param>
        /// <returns></returns>
        private static bool VerifyVersionNumbers( string repoPath, string version, Dictionary<string, bool> modifiedProjects )
        {
            foreach ( string projectName in modifiedProjects
                .Where( p =>
                    p.Value &&
                    p.Key.ToLower().StartsWith( "rock" ) )
                .Select( p => p.Key ) )
            {
                string dll = projectName + ".dll";
                FileVersionInfo rockDLLfvi = FileVersionInfo.GetVersionInfo( Path.Combine( repoPath, "RockWeb", "bin", dll ) );
                var y = rockDLLfvi.ProductVersion;
                if ( !rockDLLfvi.FileVersion.StartsWith( version ) )
                {
                    Console.Write( "{0}WARNING:  Version mismatch in {1} (v{2}).{0}Is that OK for this release? Y/N (n to quit) ", Environment.NewLine, dll, rockDLLfvi.FileVersion );
                    ConsoleKeyInfo choice = Console.ReadKey( true );
                    Console.Write( "\b" );
                    Console.WriteLine( "" );
                    if ( choice.KeyChar.ToString().ToLowerInvariant() != "y" )
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Determines what package files were changed since the last "package build".
        /// </summary>
        /// <param name="repoPath">the path to the git repository</param>
        /// <param name="repoBranch">the branch in the git repository to operate against</param>
        /// <param name="modifiedPackageFiles">a list of files that were modified in the RockWeb project</param>
        /// <param name="deletedPackageFiles">a list of files that were deleted from the RockWeb project</param>
        /// <param name="modifiedProjects">a list of projects that were modified</param>
        private static void GetRockWebChangedFilesAndProjects( Options options, List<string> modifiedLibs, List<string> modifiedPackageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects )
        {
            int webRootPathLength = @"rockweb\".Length;

            // Open the git repo and get the commits for the given branch.
            using ( var repo = new Repository( options.RepoPath ) )
            {
                Tag tag = repo.Tags[options.CurrentVersionTag]; // current tag
                if ( tag == null )
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine( string.Format( "Error: I don't see a {0} tag.  Did you forget to tag this release?", options.CurrentVersionTag ) );
                    Exit();
                }

                Tag previousTag = repo.Tags[options.LastVersionTag];
                if ( previousTag == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(string.Format("Error: I don't see a {0} tag.  Did you enter the correct last version tag?", options.LastVersionTag));
                    Exit();
                }
                
                var previousCommit = (Commit)previousTag.Target;

                Commit currentCommit = null;
                if ( string.IsNullOrWhiteSpace( options.CurrentVersionCommitHash )  )
                {
                    currentCommit = ( Commit ) tag.Target;
                }
                else
                {
                    currentCommit = repo.Lookup<Commit>( options.CurrentVersionCommitHash );
                    if ( currentCommit == null )
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine( string.Format( "Error: I don't see a commit with Id {0}.  Did you copy the hash correctly?", options.CurrentVersionTag ) );
                        Exit();
                    }
                }

                // Now go through each commit since the last pagckage commit and
                // determine which projects (dlls) and files from the RockWeb project
                // need to be included in the package.
                Console.WriteLine("Comparing... (this could take a few minutes)");

                TreeChanges changes = repo.Diff.Compare(previousCommit.Tree, currentCommit.Tree);

                var filesToIgnoreButOnlyIfModify = new string[] 
                {
                    "_variable-overrides.less",
                    "_css-overrides.less"
                };

                var extensionsToIgnore = new string[] 
                {
                    ".gitignore"
                };

                var foldersToIgnore = new string[]
                {
                     @"Apps\",
                     @"RockWeb\App_Data\Packages",
                     @"Dev Tools\",
                     @"Documentation\",
                     @"RockInstaller\",
                     @"Rock Installer\",
                     @"Rock.CodeGeneration\",
                     @"libs\",
                     @"packages\",
                     @"RockJobSchedulerService\",
                     @"RockJobSchedulerServiceInstaller\",
                     @"Quartz\"
                };

                foreach ( var file in changes )
                {
                    // skip a bunch of known projects we don't care about...
                    if ( foldersToIgnore.Any( x => file.Path.StartsWith( x ) ) )
                    {
                        continue;
                    }

                    // skip some files
                    if ( filesToIgnoreButOnlyIfModify.Any( x => Path.GetFileName( file.Path ).Equals( x, StringComparison.OrdinalIgnoreCase ) ) )
                    {
                        // If v9.0, don't skip the RockWeb\Themes\DashboardStark\Styles\_css-overrides.less, RockWeb\Themes\KioskStark\Styles\_css-overrides.less, RockWeb\Themes\LandingPage\Styles\_variable-overrides.less, and RockWeb\Themes\LandingPage\Styles\_css-overrides.less
                        if ( options.CurrentVersionTag == "1.9.0" &&
                            ( 
                                file.Path == @"RockWeb\Themes\DashboardStark\Styles\_css-overrides.less" ||
                                file.Path == @"RockWeb\Themes\KioskStark\Styles\_css-overrides.less" ||
                                file.Path == @"RockWeb\Themes\LandingPage\Styles\_css-overrides.less" ||
                                file.Path == @"RockWeb\Themes\LandingPage\Styles\_variable-overrides.less"
                            ) )
                        {
                            // don't skip; otherwise...
                        }
                        else if ( file.Status == ChangeKind.Modified )
                        {
                            // ...only skip if changed 
                            continue;
                        }
                    }

                    // skip some files
                    if ( extensionsToIgnore.Any( x => Path.GetExtension( file.Path ).Equals( x, StringComparison.OrdinalIgnoreCase ) ) )
                    {
                        continue;
                    }

                    string projectName = file.Path.Split( Path.DirectorySeparatorChar ).First();

                    if ( options.Verbose )
                    {
                        Console.WriteLine( "{0}\t{1}", file.Path, file.Status );
                    }

                    // any changes with any other non-RockWeb projects?
                    if ( options.ProjectsInSolution.Contains( projectName, StringComparer.OrdinalIgnoreCase ) )
                    {
                        // Ignore output files in bin folder as they should also be in the rockweb\bin folder
                        if ( !file.Path.ToLower().StartsWith( projectName.ToLower() + "\\bin" ) )
                        {
                            modifiedProjects[projectName] = true;
                        }
                    }
                    else if ( file.Path.ToLower().StartsWith( @"rockweb\bin\" ) ) // && x.Path.ToLower().EndsWith( ".dll" ) )
                    {
                        if ( ( file.Status == ChangeKind.Added || file.Status == ChangeKind.Modified ) &&
                             (
                                file.Path.ToLower().EndsWith( ".dll" ) ||
                                ( file.Path.ToLower().StartsWith( @"rockweb\bin\roslyn\" ) && !file.Path.ToLower().EndsWith( ".refresh" ) )
                             )
                           )
                        {
                            // Modified or newly added assemblies need to be added differently.  They go into a "lib" folder in the NuGet package.
                            var filePathSuffix = file.Path.Substring( webRootPathLength );
                            modifiedLibs.Add( filePathSuffix );
                        }
                        else if ( file.Status == ChangeKind.Deleted )
                        {
                            // Deleted assemblies can be just deleted simply by adding them to the deletedPackageFiles list.
                            Console.WriteLine( "{0}\t{1} (ASSEMBLY)", file.Path, file.Status );
                            deletedPackageFiles.Add( file.Path );
                        }
                    }
                    else if ( file.Path.ToLower().StartsWith( @"rockweb\" ) )
                    {
                        if ( file.Status == ChangeKind.Added || file.Status == ChangeKind.Modified )
                        {
                            var filePathSuffix = file.Path.Substring( webRootPathLength );
                            modifiedPackageFiles.Add( filePathSuffix );
                        }
                        else if ( file.Status == ChangeKind.Deleted )
                        {
                            deletedPackageFiles.Add( file.Path );
                        }
                    }
                }
            }
        }

        private static void GetUpdatedFilesNotInSolution( Options options, List<string> modifiedLibs, List<string> modifiedPackageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects )
        {
            if ( string.IsNullOrWhiteSpace( options.PreviousVersionBinFolderPath ) )
            {
                // There is nothing to compare to so the user will get a message to do this manually.
                return;
            }

            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine( "" );
            Console.WriteLine( "================================================================================" );
            Console.WriteLine( "Starting byte compare. This is to get new and updated files that are not part" );
            Console.WriteLine( "of the solution." );
            Console.WriteLine( "================================================================================" );
            Console.WriteLine( "" );
            Console.ResetColor();

            // Ignore files with these extensions
            List<string> ignoreFilePatterns = new List<string>() { ".pdb", ".refresh" };

            // Get a list of files from the previous version directory.
            // Filter out files already identified as updated, solution files, and ignore files
            List<string> oldFileList = Directory.GetFiles( options.PreviousVersionBinFolderPath )
                // Filter out files by extension
                .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) )
                // Filter out files for ignored projects
                .Where( f => !PROJECTS_TO_IGNORE.Any( p => f.ToLower().Contains( p.ToLower() ) ) )
                // Filter out files that have already been flagged as updated by other checks
                .Where( f => !modifiedLibs.Any( p => Path.GetFileName( f ).Equals( Path.GetFileName( p ), StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !modifiedPackageFiles.Any( p => Path.Combine("Bin", Path.GetFileName( f ) ).Equals( Path.Combine( "Bin", Path.GetFileName( p ) ), StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !options.ProjectsInSolution.Any( p => Path.GetFileNameWithoutExtension( f ).Equals( p, StringComparison.OrdinalIgnoreCase ) ) )
                .ToList();

            // Get a list of files from the repo directory that contains the version being created
            // Filter out files already identified as updated, solution files, and ignore files
            List<string> newFileList = Directory.GetFiles( Path.Combine( options.RepoPath, "RockWeb", "Bin" ) )
                // Filter out files by extension
                .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) )
                // Filter out files for ignored projects
                .Where( f => !PROJECTS_TO_IGNORE.Any( p => f.ToLower().Contains( p.ToLower() ) ) )
                // Filter out files that have already been flagged as updated by other checks
                .Where( f => !modifiedLibs.Any( p => Path.GetFileName( f ).Equals( Path.GetFileName( p ), StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !modifiedPackageFiles.Any( p => Path.Combine("RockWeb", "Bin", Path.GetFileName( f ) ).Equals( Path.Combine( "RockWeb", "Bin", Path.GetFileName( p ) ), StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !options.ProjectsInSolution.Any( p => Path.GetFileNameWithoutExtension( f ).Equals( p, StringComparison.OrdinalIgnoreCase ) ) )
                .ToList();
          
            // loop through the old file list and compare to new file version
            foreach( var oldFile in oldFileList )
            {
                FileInfo oldFileInfo = new FileInfo( oldFile );
                FileInfo newFileInfo = new FileInfo( Path.Combine( options.RepoPath, "RockWeb", "Bin", oldFileInfo.Name ) );

                // If exists in old but not new then add to deleted list
                if( !newFileInfo.Exists )
                {
                    string file = Path.Combine( "RockWeb", "Bin", oldFileInfo.Name );
                    if ( !deletedPackageFiles.Where( d => d.Equals( file, StringComparison.OrdinalIgnoreCase ) ).Any() )
                    {
                        deletedPackageFiles.Add( file );
                    }

                    continue;
                }

                // Compare the old and new Bytes and move on if they are the same
                if( oldFileInfo.Length == newFileInfo.Length
                    && File.ReadAllBytes(oldFileInfo.FullName).SequenceEqual( File.ReadAllBytes( newFileInfo.FullName ) ) )
                {
                    continue;
                }

                string relativeFilePath = "Bin\\" + oldFileInfo.Name;

                // The file has changed. If dll then add to modifiedLibs (filename), otherwise add to modifiedPackageFiles (bin\filename)
                if( oldFileInfo.Extension == ".dll" )
                {
                    modifiedLibs.Add( relativeFilePath );
                }
                else
                {
                    modifiedPackageFiles.Add( relativeFilePath );
                }
            }

            // If exists in new but not old then we need to add the file
            var filesToAdd = newFileList
                .Where( o => !oldFileList.Any( n => Path.GetFileName( o ).Equals( Path.GetFileName( n ), StringComparison.OrdinalIgnoreCase ) ) )
                .Select( o => o.Replace( Path.Combine( options.RepoPath, "RockWeb" ) + "\\", string.Empty ) );

            foreach( var newFile in filesToAdd )
            {
                // If dll then add to modifiedLibs (filename), otherwise add to modifiedPackageFiles (bin\filename)
                if( Path.GetExtension( newFile ) == ".dll" )
                {
                    modifiedLibs.Add( newFile );
                }
                else
                {
                    modifiedPackageFiles.Add( newFile );
                }
            }
        }

        /// <summary>
        /// Finds all the commit messages and outputs them to the given StringBuilder.
        /// Additionally, if it encounters a "[update-" commit message it will create
        /// special badges for each at the bottom of the output.
        /// </summary>
        /// <param name="currentCommits"></param>
        /// <param name="previousCommits"></param>
        /// <param name="verbose"></param>
        /// <param name="sb">the StringBuilder to output the formatted commit messages to.</param>
        private static void ParseCommitMessages(ICommitLog currentCommits, ICommitLog previousCommits, bool verbose, StringBuilder sb)
        {
            Regex regUpdate = new Regex(@"\[update-([^\]]+\] (.*))", RegexOptions.IgnoreCase);
            List<string> appUpdateBadges = new List<string>();

            var previousShas = previousCommits.Select(c => c.Sha).ToList();
            var validCommits = currentCommits.Where(c => !previousShas.Contains(c.Sha)).ToList();
            foreach ( var commit in validCommits )
            {
                if (!commit.Message.StartsWith("Merge pull request" ) && 
                    !commit.Message.StartsWith("Merge branch") &&
                    !commit.Message.StartsWith("Merge remote-tracking") &&
                    !commit.Message.StartsWith("-"))
                {
                    Match match = regUpdate.Match(commit.Message);
                    if (match.Success)
                    {
                        appUpdateBadges.Add(string.Format("<span title=\"This application needs to be updated. {1}\" class=\"label label-warning\">{0}</span>",
                            match.Groups[1].Value, match.Groups[2].Value.Replace("\"", "\\\"")));
                    }
                    else if (!(commit.Message.StartsWith("+") || commit.Message.StartsWith(" +")))
                    {
                        // append the commit message an prefix with a + if there isn't one already.
                        sb.AppendFormat("+ {0}", commit.Message);
                    }
                    else
                    {
                        sb.AppendFormat("{0}", commit.Message);
                    }

                    if ( !commit.Message.EndsWith( "\n" ))
                    {
                        sb.Append( '\n' );
                    }
                }

                if (verbose)
                {
                    Console.WriteLine(string.Format("id: {0} {1}", commit.Id, commit.Message));
                }

            }

            // add any app update badges to bottom of the sb
            if (appUpdateBadges.Count > 0)
            {
                sb.AppendFormat("<br/><h4>{0}</h4>", string.Join("&nbsp;", appUpdateBadges));
            }

            Console.WriteLine("Found {0} commits.", validCommits.Count());
        }

        /// <summary>
        /// Builds the RockUpdate-X-Y-Z.x.y.z.nupkg package which has all the modified files and a file that contains
        /// the paths to all the files that are to be deleted.
        /// </summary>
        /// <param name="options">The options object created upon execution.</param>
        /// <param name="modifiedLibs">The modified libs.</param>
        /// <param name="packageFiles">The package files.</param>
        /// <param name="deletedPackageFiles">The deleted package files.</param>
        /// <param name="modifiedProjects">The modified projects.</param>
        /// <param name="description">The description.</param>
        /// <param name="actionWarnings">The action warnings (if any) which are useful to display again as the very last thing before quitting.</param>
        /// <returns></returns>
        private static string BuildUpdatePackage( Options options, List<string> modifiedLibs, List<string> packageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects, string description, out string actionWarnings )
        {
            actionWarnings = string.Empty;
            string version = options.CurrentVersionTag;
            string dashVersion = version.Replace( '.', '-' );
            string updatePackageId = ROCKUPDATE_PACKAGE_PREFIX + "-" + dashVersion;
            string updatePackageFileName = Path.Combine( options.PackageFolder, string.Format( "{0}.{1}.nupkg", updatePackageId, version ) );

            // Create a manifest for this package...
            Manifest manifest = new Manifest();
            manifest.Metadata = new ManifestMetadata()
            {
                Authors = "SparkDevelopmentNetwork",
                Version = version,
                Id = updatePackageId,
                Description = description,
            };

            AddPreviousUpdatePackageAsDependency( options, manifest, updatePackageFileName );

            manifest.Files = new List<ManifestFile>();
            string webRootPath = Path.Combine( options.RepoPath, "RockWeb" );
            foreach ( string file in packageFiles )
            {
                // Skip the root web.config, but YELL to let the person know they need to do something with this.
                if ( file.ToLower() == "web.config" )
                {
                    Console.WriteLine( "" );
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine( WEBCONFIG_XDT_MESSAGE );
                    Console.ForegroundColor = ConsoleColor.Gray;

                    actionWarnings += WEBCONFIG_XDT_MESSAGE;
                    continue;
                }
                
                AddToManifest( manifest, file, webRootPath );

                // if a less file was updated, check to see if a matching css file exists, and if so, add it (these files are ignored by repo)
                if ( file.ToLower().EndsWith( ".less" ))
                {
                    string cssPath = file.Substring( 0, file.Length - 5 ) + ".css";
                    if ( File.Exists( Path.Combine( webRootPath, cssPath )))
                    {
                        AddToManifest( manifest, cssPath, webRootPath );
                    }
                }
            }

            foreach ( string file in modifiedLibs )
            {
                AddLibToManifest( manifest, file, webRootPath );
            }

            // Add any modified Rock project libs but warn the user that they MUST be recently compiled
            // against the master head we're operating against.
            if ( modifiedProjects.Count > 0 )
            {
                Console.WriteLine( "" );
                Console.WriteLine( "NOTE: The following assembly(s) are being added because their project files" );
                Console.WriteLine( "      were changed since the last tag. You MUST ensure they were built in" );
                Console.WriteLine( "      RELEASE mode and are currently in the RockWeb/bin folder. Otherwise" );
                Console.WriteLine( "      you must do that manually and replace the ones I just put into the" );
                Console.WriteLine( "      package's 'lib' nuget package folder." );
                Console.WriteLine( "" );
                foreach ( KeyValuePair<string, bool> entry in modifiedProjects )
                {
                    Console.WriteLine( string.Format( "\t * {0}", entry.Key + ".dll" ) );
                    AddLibToManifest( manifest, Path.Combine("bin", entry.Key + ".dll" ), webRootPath );

                    // if a dll was updated and there is an associated xml documentation file, include it 
                    string xmlPath = Path.Combine( "bin", entry.Key + ".xml" );
                    if ( File.Exists( Path.Combine( webRootPath, xmlPath ) ) )
                    {
                        AddToManifest( manifest, xmlPath, webRootPath );
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
                Console.WriteLine( "" );
                Console.Write    ( "WARNING! Files are designated" );
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write    ( " to be DELETED " );
                Console.ResetColor();
                Console.WriteLine( "in this update." );
                Console.WriteLine( "         Review all the files listed in the App_Data\\deletefile.lst" );
                Console.WriteLine( "         as a sanity check.  If you see anything odd, ask someone to verify." );
                Console.WriteLine( "" );

                foreach ( var name in deletedPackageFiles )
                {
                    Console.WriteLine( "\t * " + name ); 
                }

                using ( StreamWriter w = File.AppendText( deleteFileFullPath ) )
                {
                    foreach ( string delete in deletedPackageFiles )
                    {
                        w.WriteLine( delete );
                    }
                }

                AddToManifest( manifest, deleteFileRelativePath, webRootPath );
            }

            // Then Run.Migration file is not needed for version 1.11 and up.
            int[] versionParts = options.CurrentVersionTag.Split( '.' ).Select( v => int.Parse( v ) ).ToArray();
            if ( versionParts[0] < 2 && versionParts[1] < 11 )
            {
                // always add a Run.Migration flag file
                string migrationFlagFileRelativePath = Path.Combine( "App_Data", "Run.Migration" );
                string migrationFlagFileFullPath = Path.Combine( webRootPath, migrationFlagFileRelativePath );
                File.Create( migrationFlagFileFullPath ).Dispose();
                AddToManifest( manifest, migrationFlagFileRelativePath, webRootPath );
            }

            manifest.Files = manifest.Files.OrderBy( a => a.Source ).ToList();

            // build the package
            PackageBuilder builder = new PackageBuilder();
            builder.PopulateFiles( options.RepoPath, manifest.Files );
            builder.Populate( manifest.Metadata );
            using ( FileStream stream = File.Open( updatePackageFileName, FileMode.Create ) )
            {
                builder.Save( stream );
            }

            var updatePackageManifestFileName = Path.ChangeExtension( updatePackageFileName, "nuspec" );
            using ( FileStream stream = File.Open( updatePackageManifestFileName, FileMode.Create ) )
            {
                manifest.Save( stream );
            }

            return updatePackageId;
        }

        /// <summary>
        /// Outputs the changes to the console (used for testing/seeing what's going to be in a release without actually creating it yet).
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="modifiedLibs">The modified libs.</param>
        /// <param name="packageFiles">The package files.</param>
        /// <param name="deletedPackageFiles">The deleted package files.</param>
        /// <param name="modifiedProjects">The modified projects.</param>
        /// <param name="description">The description.</param>
        private static void OnlyOutputChanges( Options options, List<string> modifiedLibs, List<string> packageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects, string description, out string actionWarnings )
        {
            actionWarnings = string.Empty;
            string webRootPath = Path.Combine( options.RepoPath, "RockWeb" );

            foreach ( string file in packageFiles )
            {
                // Skip the root web.config, but YELL to let the person know they need to do something with this.
                if ( file.ToLower() == "web.config" )
                {
                    Console.WriteLine( "" );
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine( WEBCONFIG_XDT_MESSAGE );
                    Console.ForegroundColor = ConsoleColor.Gray;

                    actionWarnings += WEBCONFIG_XDT_MESSAGE;
                    continue;
                }

                // if a less file was updated, check to see if a matching css file exists, and if so, add it (these files are ignored by repo)
                if ( file.ToLower().EndsWith( ".less" ) )
                {
                    string cssPath = file.Substring( 0, file.Length - 5 ) + ".css";
                    if ( File.Exists( Path.Combine( webRootPath, cssPath ) ) )
                    {
                        Console.WriteLine( Path.Combine( webRootPath, cssPath ) );
                    }
                }
            }

            foreach ( string file in modifiedLibs )
            {
                Console.WriteLine( file );
            }

            // Add any modified Rock project libs but warn the user that they MUST be recently compiled
            // against the master head we're operating against.
            if ( modifiedProjects.Count > 0 )
            {
                Console.WriteLine( "" );
                Console.WriteLine( "NOTE: The following assembly(s) are being added because their project files" );
                Console.WriteLine( "      were changed since the last tag. You MUST ensure they were built in" );
                Console.WriteLine( "      RELEASE mode and are currently in the RockWeb/bin folder. Otherwise" );
                Console.WriteLine( "      you must do that manually and replace the ones I just put into the" );
                Console.WriteLine( "      package's 'lib' nuget package folder." );
                Console.WriteLine( "" );
                foreach ( KeyValuePair<string, bool> entry in modifiedProjects )
                {
                    Console.WriteLine( string.Format( "\t * {0}", entry.Key + ".dll" ) );

                    // if a dll was updated and there is an associated xml documentation file, include it 
                    string xmlPath = Path.Combine( "bin", entry.Key + ".xml" );
                    if ( File.Exists( Path.Combine( webRootPath, xmlPath ) ) )
                    {
                        Console.WriteLine( Path.Combine( webRootPath, xmlPath ) );
                    }
                }
            }

            // write out all the files to delete in the deletefile.lst ) 
            string deleteFileRelativePath = Path.Combine( "App_Data", "deletefile.lst" );
            string deleteFileFullPath = Path.Combine( webRootPath, deleteFileRelativePath );

            if ( deletedPackageFiles.Count > 0 )
            {
                Console.WriteLine( "" );
                Console.WriteLine( "WARNING! Files are designated to be DELETED in this update." );
                Console.WriteLine( "         Review all the files listed in the App_Data\\deletefile.lst" );
                Console.WriteLine( "         as a sanity check.  If you see anything odd, ask someone to verify." );
                Console.WriteLine( "" );

                foreach ( string delete in deletedPackageFiles )
                {
                    Console.WriteLine( delete );
                }

                Console.WriteLine( deleteFileRelativePath );
            }
        }

        /// <summary>
        /// Looks in the packageFolder for any previous RockUpdate packages and
        /// adds the last (latest) one to the manifest as a dependency
        /// </summary>
        /// <param name="options"></param>
        /// <param name="manifest"></param>
        /// <param name="currentPackageName"></param>
        private static void AddPreviousUpdatePackageAsDependency( Options options, Manifest manifest, string currentPackageName )
        {
            string previousUpdatePackageId = null;
            string previousUpdatePackageVersion = null;

            DirectoryInfo di = new DirectoryInfo( options.PackageFolder );
            FileSystemInfo[] files = di.GetFileSystemInfos( ROCKUPDATE_PACKAGE_PREFIX + "*.nupkg" );

            foreach ( var packageFile in files.OrderByDescending( f => f.LastWriteTime ) )
            {
                if ( options.Verbose )
                {
                    Console.WriteLine( "Found a package called: " + packageFile );
                }

                if ( currentPackageName.EndsWith( packageFile.Name ) )
                {
                    continue;
                }

                if ( options.Verbose )
                {
                    Console.WriteLine( "This is the prior package: " + packageFile );
                }

                var match = Regex.Match( packageFile.ToString(), @"(RockUpdate-[-\d]+)\.([\.\d]+).nupkg" );
                if ( match.Groups.Count >= 2 )
                {
                    previousUpdatePackageId = match.Groups[1].Value;
                    previousUpdatePackageVersion = match.Groups[2].Value;
                    if ( previousUpdatePackageVersion == options.LastVersionTag )
                    {
                        break;
                    }
                }
            }

            // If we didn't find one, we're done...
            if ( previousUpdatePackageId == null )
            {
                return;
            }

            Console.WriteLine( "" );
            Console.WriteLine( "NOTE: I added a \"{0}\" dependency to the {1} package. If this is not correct remove it manually.", previousUpdatePackageId, currentPackageName );

            // Otherwise, add it as a dependency
            // If there was a previous package, add it as a dependency to the update package

            manifest.Metadata.DependencySets = new List<ManifestDependencySet>
            {
                new ManifestDependencySet
                {
                    TargetFramework = null,
                    Dependencies = new List<ManifestDependency> 
                    {
                        new ManifestDependency { Id = previousUpdatePackageId, Version = previousUpdatePackageVersion }
                    }
                }
            };

            _previousVersion = previousUpdatePackageVersion;
        }

        /// <summary>
        /// Builds the two empty stub packages (Rock.x.y.z.nupkg and RockUpdate-X-Y-Z.x.y.z.nupkg)
        /// needed for fresh installs via the installer as described here:
        /// https://github.com/SparkDevNetwork/Rock/blob/develop/Installers/RockInstaller/readme.txt
        /// </summary>
        /// <param name="repoPath"></param>
        /// <param name="packageFolder"></param>
        /// <param name="version"></param>
        private static void BuildEmptyStubPackagesForInstaller( string repoPath, string installerArtifactsPath, string version )
        {
            string absolutePathOfCurrentDirectory = System.IO.Directory.GetCurrentDirectory();
            string fullPathToInstallerArtifactsPath = Path.GetFullPath( ( new Uri( Path.Combine( absolutePathOfCurrentDirectory, installerArtifactsPath) ) ).LocalPath );

            FileVersionInfo rockDLLfvi = FileVersionInfo.GetVersionInfo( Path.Combine( repoPath, "RockWeb", "bin", "Rock.Version.dll" ) );
            // Create a manifest for the empty stub Rock.x.y.z package...
            Manifest manifest = new Manifest();
            manifest.Files = new List<ManifestFile>();
            manifest.Metadata = new ManifestMetadata()
            {
                Authors = "SparkDevelopmentNetwork",
                Version = version,
                Title = rockDLLfvi.ProductVersion,
                Id = "Rock",
                Copyright = rockDLLfvi.LegalCopyright,
                Description = "Installer Updater Stub",
            };

            // Must add at least one file.
            string readmeFileRelativePath = "Readme.txt";
            string readmeFileFullPath = Path.Combine( fullPathToInstallerArtifactsPath, readmeFileRelativePath );
            System.IO.File.WriteAllText( readmeFileFullPath, "This package is a stub to be included in the installer to assist the Rock Updater to know the current release." );
            AddToManifest( manifest, readmeFileRelativePath, fullPathToInstallerArtifactsPath );

            string packageFileName = FullPathOfRockPackageFile( installerArtifactsPath, version );

            PackageBuilder builder = new PackageBuilder();
            builder.PopulateFiles( fullPathToInstallerArtifactsPath, manifest.Files );
            builder.Populate( manifest.Metadata );
            using ( FileStream stream = File.Open( packageFileName, FileMode.Create ) )
            {
                builder.Save( stream );
            }

            // Now create the RockUpdate-X-Y-Z.x.y.z.nupkg
            string dashVersion = version.Replace( '.', '-' );
            string updatePackageId = ROCKUPDATE_PACKAGE_PREFIX + "-" + dashVersion;
            string updatePackageFileName = Path.Combine( installerArtifactsPath, string.Format( "{0}.{1}.nupkg", updatePackageId, version ) );

            // Create a manifest for this package...
            Manifest rockUpdateManifest = new Manifest();
            rockUpdateManifest.Files = new List<ManifestFile>();
            rockUpdateManifest.Metadata = new ManifestMetadata()
            {
                Authors = "SparkDevelopmentNetwork",
                Version = version,
                Id = updatePackageId,
                Title = updatePackageId,
                Copyright = rockDLLfvi.LegalCopyright,
                Description = "Installer Updater Stub",
            };
            AddToManifest( rockUpdateManifest, readmeFileRelativePath, fullPathToInstallerArtifactsPath );

            PackageBuilder builder2 = new PackageBuilder();
            builder2.PopulateFiles( fullPathToInstallerArtifactsPath, rockUpdateManifest.Files );
            builder2.Populate( rockUpdateManifest.Metadata );
            using ( FileStream stream = File.Open( updatePackageFileName, FileMode.Create ) )
            {
                builder2.Save( stream );
            }
        }

        /// <summary>
        /// Builds the Rock package (v version) which references the RockUpdate-version.version.nupkg.
        /// </summary>
        /// <param name="updatePackageName"></param>
        /// <param name="repoPath"></param>
        /// <param name="packageFolder"></param>
        /// <param name="version"></param>
        /// <param name="description"></param>
        private static void BuildRockPackage( string updatePackageId, string repoPath, string packageFolder, string version, string description )
        {
            FileVersionInfo rockDLLfvi = FileVersionInfo.GetVersionInfo( Path.Combine( repoPath, "RockWeb", "bin", "Rock.Version.dll" ) );
            // Create a manifest for this package...
            Manifest manifest = new Manifest();
            manifest.Metadata = new ManifestMetadata()
            {
                Authors = "SparkDevelopmentNetwork",
                Version = version,
                Title = rockDLLfvi.ProductVersion,
                Id = "Rock",
                Copyright = rockDLLfvi.LegalCopyright,
                Description = description,
                DependencySets = new List<ManifestDependencySet>
                {
                    new ManifestDependencySet
                    {
                        TargetFramework = null,
                        Dependencies = new List<ManifestDependency> 
                        {
                            new ManifestDependency { Id = updatePackageId, Version = version }
                        }
                    }
                }
            };


            if ( !string.IsNullOrWhiteSpace( _previousVersion ) )
            {
                // add a "requires-X.Y.Z" tag to force rock to update one at a time.
                manifest.Metadata.Tags = String.Format( "requires-{0}", _previousVersion );
            }

            manifest.Files = new List<ManifestFile>();
            string webRootPath = Path.Combine( repoPath, "RockWeb" );

            string publicVersion = version.Substring( 2 );

            manifest.Metadata.ReleaseNotes = $"<br /> You can read the details on the official <a href='https://www.rockrms.com/releasenotes?version#v{publicVersion}'>Release Notes</a> page.";

            string tempReadmeFileRelativePath = Path.Combine( "Readme.txt" );
            string tempReadmeFileFullPath = Path.Combine( webRootPath, tempReadmeFileRelativePath );
            File.WriteAllText( tempReadmeFileFullPath, "You can read the details on the official https://www.rockrms.com/releasenotes page." );

            AddToManifest( manifest, tempReadmeFileRelativePath, webRootPath );
            manifest.Files = manifest.Files.OrderBy(a => a.Source).ToList();

            string packageFileName = FullPathOfRockPackageFile( packageFolder, version );

            PackageBuilder builder = new PackageBuilder();
            builder.PopulateFiles( repoPath, manifest.Files );
            builder.Populate( manifest.Metadata );
            using ( FileStream stream = File.Open( packageFileName, FileMode.Create ) )
            {
                builder.Save( stream );
            }

            var packageManifestFileName = Path.ChangeExtension( packageFileName, "nuspec" );
            using ( FileStream stream = File.Open( packageManifestFileName, FileMode.Create ) )
            {
                manifest.Save( stream );
            }
        }

        /// <summary>
        /// Builds the full path to the Rock.x.y.z.nupkg package file.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        private static string FullPathOfRockPackageFile( string packageFolder, string version )
        {
            return Path.Combine( packageFolder, string.Format( "Rock.{0}.nupkg", version ) );
        }

        private static void Exit()
        {
            Console.WriteLine( "Hit any key to quit." );
            Console.ReadKey();
            System.Environment.Exit( -3 );
        }

        #region NuGet Package Helper Methods
        
        /// <summary>
        /// Add the given files (matching the given file filter and search options)
        /// to the manifest.
        /// </summary>
        /// <param name="manifest">A NuGet Manifest</param>
        /// <param name="filePath">the path to the file (relative to the webroot)</param>
        /// <param name="webRootPath">the physical path to the app's webroot</param>
        private static void AddLibToManifest( Manifest manifest, string filePath, string webRootPath )
        {
            if ( !File.Exists( Path.Combine( webRootPath, filePath ) ) )
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine( "ERROR: Unable to find {0} to add to package lib!", filePath );
                Console.ForegroundColor = ConsoleColor.Gray;
                return;
            }

            // remove the "bin\" from the path...
            string filePathWithoutBinFolder = filePath.Substring( filePath.IndexOf( Path.DirectorySeparatorChar ) + 1 );

            // All files need to have a target folder under the "content\"
            // folder and the source path suffix will be the relative path to the file's physical location.
            // ex: `Blocks\Foo\Foo.ascx` and `bin\some.dll`
            var item = new ManifestFile()
            {
                Source = Path.Combine( webRootPath, filePath ),
                Target = Path.Combine( "lib", filePathWithoutBinFolder )
            };

            manifest.Files.Add( item );
        }

        /// <summary>
        /// Add the given files (matching the given file filter and search options)
        /// to the manifest.
        /// </summary>
        /// <param name="manifest">A NuGet Manifest</param>
        /// <param name="filePath">the path to the file (relative to the webroot)</param>
        /// <param name="webRootPath">the physical path to the app's webroot</param>
        private static void AddToManifest( Manifest manifest, string filePath, string webRootPath )
        {
            if ( !File.Exists( Path.Combine( webRootPath, filePath ) ) )
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine( "ERROR: Unable to find {0} to add to package content!", filePath );
                Console.ForegroundColor = ConsoleColor.Gray;
                return;
            }

            // All files need to have a target folder under the "content\"
            // folder and the source path suffix will be the relative path to the file's physical location.
            // ex: `Blocks\Foo\Foo.ascx` and `bin\some.dll`
            var item = new ManifestFile()
            {
                Source = Path.Combine( webRootPath, filePath ),
                Target = Path.Combine( "content", filePath ),
            };

            manifest.Files.Add( item );
        }

        #endregion
    }
    
    #region Extensions

    #endregion
}