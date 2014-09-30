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
        /// Projects who's DLLs need to be included in the package if they changed since the last package.
        /// </summary>
        static List<string> NON_WEB_PROJECTS = new List<string> { "rock", "rock.migrations", "rock.rest", "rock.version", "rock.payflowpro", "dotliquid" };

        static string _previousVersion = string.Empty;

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

            [Option( 'r', "repoPath", DefaultValue = @"C:\Users\dturner\Dropbox\Projects\SparkDevNetwork\Rock", HelpText = "The path to your local git repository." )]
            public string RepoPath { get; set; }

            [Option( 'p', "packageFolder", DefaultValue = @"..\..\..\NuGetLocal", HelpText = "The folder to put the output package." )]
            public string PackageFolder { get; set; }

            [Option( 'i', "installArtifactsFolder", DefaultValue = @"..\..\..\InstallerArtifacts", HelpText = "The folder to put the empty dummy packages for use with the installer." )]
            public string InstallArtifactsFolder { get; set; }

            [Option( 'v', "verbose", DefaultValue = false, HelpText = "Set to true to see a more verbose output of what's changed in the repo." )]
            public bool Verbose { get; set; }

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
            if ( parser.ParseArgumentsStrict( args, options, () => Environment.Exit( -2 ) ) )
            {
                if ( !Directory.Exists( options.RepoPath ) )
                {
                    Console.WriteLine( string.Format( "The given repository folder ({0}) does not exist.", options.RepoPath ) );
                    Console.WriteLine( options.GetUsage() );
                    Environment.Exit( -2 );
                }
                else if ( !Directory.Exists( options.PackageFolder ) )
                {
                    Console.WriteLine( string.Format( "The given output package folder ({0}) does not exist.", options.PackageFolder ) );
                    Console.WriteLine( options.GetUsage() );
                    Environment.Exit( -2 );
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

            // Make sure the Rock.Version project's version number has been updated and commit->pushed
            // before you build from master.
            VerifyVersionNumbers( options.RepoPath, options.CurrentVersionTag );
            
            Console.WriteLine( "" );
            Console.WriteLine( "Make sure you've updated the version numbers, pushed to master and have locally" );
            Console.WriteLine( "built a RELEASE build. (Those assemblies may be added to the package.)" );
            Console.WriteLine( "" );

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
            }

            string changeMessages = GetRockWebChangedFilesAndProjects( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects );

            var updatePackageName = BuildUpdatePackage( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects, "various changes" );

            // Create wrapper Rock.X.Y.Z.nupkg package as per: https://github.com/SparkDevNetwork/Rock-ChMS/wiki/Packaging-Rock-Core-Updates
            BuildRockPackage( updatePackageName, options.RepoPath, options.PackageFolder, options.CurrentVersionTag, "In the future there will be a description and the release notes below will be written for non-developers.", changeMessages );

            BuildEmptyStubPackagesForInstaller( options.RepoPath, options.InstallArtifactsFolder, options.CurrentVersionTag );

            Console.WriteLine( "" );
            Console.Write( "Press any key to quit." );
            Console.ReadKey(true);

            return 0;
        }

        /// <summary>
        /// Verifies that the Rock assemblies version numbers match the update package that's being built.
        /// </summary>
        /// <param name="repoPath">path to your rock repository</param>
        /// <param name="version">version number to verify/match</param>
        private static void VerifyVersionNumbers( string repoPath, string version )
        {
            foreach ( var dll in new string[] {"Rock.dll", "Rock.Migrations.dll", "Rock.Rest.dll", "Rock.Version.dll", "Rock.PayFlowPro.dll" } )
            {
                FileVersionInfo rockDLLfvi = FileVersionInfo.GetVersionInfo( Path.Combine( repoPath, "RockWeb", "bin", dll ) );
                var y = rockDLLfvi.ProductVersion;
                if ( ! rockDLLfvi.FileVersion.StartsWith( version ) )
                {
                    Console.WriteLine( "WARNING:  Version mismatch in {0}.  Is that OK for this release?", dll );
                }
            }
        }

        /// <summary>
        /// Determines what package files were changed since the last "package build".
        /// </summary>
        /// <param name="repoPath">the path to the git repository</param>
        /// <param name="repoBranch">the branch in the git repository to operate against</param>
        /// <param name="modifiedPackageFiles">a list of files that were modified in the RockWeb project</param>
        /// <param name="deletedPackageFiles">a list of files that were deleted from the RockWeb project</param>
        /// <param name="modifiedProjects">a list of projects that were modified</param>
        private static string GetRockWebChangedFilesAndProjects( Options options, List<string> modifiedLibs, List<string> modifiedPackageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects )
        {
            int webRootPathLength = @"rockweb\".Length;
            StringBuilder sbChangeLog = new StringBuilder();

            // Open the git repo and get the commits for the given branch.
            using ( var repo = new Repository( options.RepoPath ) )
            {
                Tag tag = repo.Tags[options.CurrentVersionTag]; // current tag
                if ( tag == null )
                {
                    Console.WriteLine( string.Format( "Error: I don't see a {0} tag.  Did you forget to tag this release?", options.CurrentVersionTag ) );
                    System.Environment.Exit( -3 );
                }

                Tag previousTag = repo.Tags[options.LastVersionTag];
                if (tag == null)
                {
                    Console.WriteLine(string.Format("Error: I don't see a {0} tag.  Did you enter the correct last version tag?", options.LastVersionTag));
                    System.Environment.Exit(-3);
                }

                var previousCommit = (Commit)previousTag.Target;
                var currentCommit = (Commit)tag.Target;

                // Find all the commits and parse their commit messages
                var currentCommits = repo.Commits.QueryBy(new CommitFilter { Since = currentCommit });
                var previousCommits = repo.Commits.QueryBy(new CommitFilter { Since = previousCommit });
                ParseCommitMessages(currentCommits, previousCommits, options.Verbose, sbChangeLog);

                // Now go through each commit since the last pagckage commit and
                // determine which projects (dlls) and files from the RockWeb project
                // need to be included in the package.
                Console.WriteLine("Comparing... (this could take a few minutes)");

                TreeChanges changes = repo.Diff.Compare(previousCommit.Tree, currentCommit.Tree);
                foreach ( var file in changes )
                {
                    // skip a bunch of known projects we don't care about...
                    if ( file.Path.ToLower().EndsWith( ".gitignore" ) || 
                        file.Path.StartsWith( @"Apps\" ) ||
                        file.Path.StartsWith( @"RockWeb\App_Data\Packages" ) ||
                        file.Path.StartsWith( @"Dev Tools\" ) || 
                        file.Path.StartsWith( @"Documentation\" ) ||
                        file.Path.StartsWith( @"RockInstaller\" ) || 
                        file.Path.StartsWith( @"Rock Installer\" ) ||
                        file.Path.StartsWith( @"Rock.CodeGeneration\" ) || 
                        file.Path.StartsWith( @"libs\" ) || 
                        file.Path.StartsWith( @"packages\" ) ||
                        file.Path.StartsWith( @"RockJobSchedulerService\" ) || 
                        file.Path.StartsWith( @"RockJobSchedulerServiceInstaller\" ) ||
                        file.Path.StartsWith( @"Quartz\" )  )
                    {
                        continue;
                    }

                    string projectName = file.Path.Split( Path.DirectorySeparatorChar ).First();
                    if ( options.Verbose )
                    {
                        Console.WriteLine( "{0}\t{1}", file.Path, file.Status );
                    }

                    // any changes with any other non-RockWeb projects?
                    if ( NON_WEB_PROJECTS.Contains( projectName.ToLower() ) )
                    {
                        modifiedProjects[projectName] = true;
                    }
                    else if ( file.Path.ToLower().StartsWith( @"rockweb\bin\" ) ) // && x.Path.ToLower().EndsWith( ".dll" ) )
                    {
                        if ( ( file.Status == ChangeKind.Added || file.Status == ChangeKind.Modified ) && file.Path.ToLower().EndsWith( ".dll" ) )
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
            return sbChangeLog.ToString();
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
                if (!commit.Message.StartsWith("Merge branch 'origin/develop'") &&
                    !commit.Message.StartsWith("Merge branch 'origin/master'") &&
                    !commit.Message.StartsWith("Merge branch 'develop'") &&
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
        /// <param name="packageFiles"></param>
        /// <param name="deletedPackageFiles"></param>
        /// <param name="modifiedProjects"></param>
        /// <param name="description"></param>
        /// <returns></returns>
        private static string BuildUpdatePackage( Options options, List<string> modifiedLibs, List<string> packageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects, string description )
        {
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
                // Skip the root web.config
                if ( file.ToLower() == "web.config" )
                {
                    Console.WriteLine( "" );
                    Console.WriteLine( "--> ACTION! web.config file was changed since last build. Figure out" );
                    Console.WriteLine( "            how you're going to handle that. You'll probably have to" );
                    Console.WriteLine( "            create a web.config.rock.xdt file. See the Packaging-Rock-Core-Updates" );
                    Console.WriteLine( "            wiki page for details on doing that." );
                    continue;
                }
                
                AddToManifest( manifest, file, webRootPath );
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
                Console.WriteLine( "WARNING: Files are designated to be DELETED in this update." );
                Console.WriteLine( "         Review all the files listed in the App_Data\\deletefile.lst" );
                Console.WriteLine( "         as a sanity check.  If you see anything odd, ask someone to verify." );
                Console.WriteLine( "" );

                using ( StreamWriter w = File.AppendText( deleteFileFullPath ) )
                {
                    foreach ( string delete in deletedPackageFiles )
                    {
                        w.WriteLine( delete );
                    }
                }

                AddToManifest( manifest, deleteFileRelativePath, webRootPath );
            }

            // always add a Run.Migration flag file
            string migrationFlagFileRelativePath = Path.Combine( "App_Data", "Run.Migration" );
            string migrationFlagFileFullPath = Path.Combine( webRootPath, migrationFlagFileRelativePath );
            File.Create( migrationFlagFileFullPath ).Dispose();
            AddToManifest( manifest, migrationFlagFileRelativePath, webRootPath );

            // build the package
            PackageBuilder builder = new PackageBuilder();
            builder.PopulateFiles( options.RepoPath, manifest.Files );
            builder.Populate( manifest.Metadata );
            using ( FileStream stream = File.Open( updatePackageFileName, FileMode.OpenOrCreate ) )
            {
                builder.Save( stream );
            }

            return updatePackageId;
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
                    break;
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
            using ( FileStream stream = File.Open( packageFileName, FileMode.OpenOrCreate ) )
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
            using ( FileStream stream = File.Open( updatePackageFileName, FileMode.OpenOrCreate ) )
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
        /// <param name="releaseNotes"></param>
        private static void BuildRockPackage( string updatePackageId, string repoPath, string packageFolder, string version, string description, string releaseNotes )
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
                ReleaseNotes = releaseNotes,
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

            // Must add at least one file.
            string readmeFileRelativePath = "Readme.txt";
            string readmeFileFullPath = Path.Combine( webRootPath, readmeFileRelativePath );
            System.IO.File.WriteAllText( readmeFileFullPath, releaseNotes );
            AddToManifest( manifest, readmeFileRelativePath, webRootPath );

            string packageFileName = FullPathOfRockPackageFile( packageFolder, version );

            PackageBuilder builder = new PackageBuilder();
            builder.PopulateFiles( repoPath, manifest.Files );
            builder.Populate( manifest.Metadata );
            using ( FileStream stream = File.Open( packageFileName, FileMode.OpenOrCreate ) )
            {
                builder.Save( stream );
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
                Console.WriteLine( "ERROR: Unable to find {0} to add to package lib!", filePath );
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
                Console.WriteLine( "ERROR: Unable to find {0} to add to package content!", filePath );
                return;
            }

            // All files need to have a target folder under the "content\"
            // folder and the source path suffix will be the relative path to the file's physical location.
            // ex: `Blocks\Foo\Foo.ascx` and `bin\some.dll`
            var item = new ManifestFile()
            {
                Source = Path.Combine( webRootPath, filePath ),
                Target = Path.Combine( "content", filePath )
            };

            manifest.Files.Add( item );
        }

        #endregion
    }
    
    #region Extensions

    #endregion
}