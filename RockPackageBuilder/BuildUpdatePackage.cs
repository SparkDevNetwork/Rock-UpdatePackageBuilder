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

namespace RockPackageBuilder
{
    class BuildUpdatePackage
    {
        #region Properties
        //static string PACKAGE_ROOT_SAVE_PATH = @"C:\Misc\_NuGetLocal";
        //static string REPO_PATH = @"C:\Misc\Rock-ChMS";
        //static string REPO_BRANCH = "develop";
        static string LAST_PACKAGE_COMMIT_SHA = "794515bf0f9995b243beda8385bdb24333e8cac2"; // 794515bf0f9995b243beda8385bdb24333e8cac2 is version 0.0.2

        static List<string> NON_WEB_PROJECTS = new List<string> { "rock", "rock.migrations", "rock.rest", "rock.version" };
        #endregion

        // Define a class to receive parsed values
        class Options
        {
            [Option( 's', "saveCurrentCommitSHA", DefaultValue = @"false", HelpText = "Set to true to store the last commit SHA as the starting point for the next run." )]
            public string SaveCurrentCommitSHA { get; set; }

            [Option( 'b', "repoBranch", DefaultValue = @"master", HelpText = "The branch to operate against when performing package builds." )]
            public string RepoBranch { get; set; }

            [Option( 'r', "repoPath", DefaultValue = @"C:\Misc\Rock-ChMS", HelpText = "The path to your local git repository." )]
            public string RepoPath { get; set; }

            [Option( 'p', "packageFolder", DefaultValue = @"C:\Misc\_NuGetLocal", HelpText = "The folder to put the output package." )]
            public string PackageFolder { get; set; }

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
                    Environment.Exit( -2 );
                }
                else if ( !Directory.Exists( options.PackageFolder ) )
                {
                    Console.WriteLine( string.Format( "The given output package folder ({0}) does not exist.", options.PackageFolder ) );
                    Environment.Exit( -2 );
                }
                else
                {
                    Run( options );
                }
            }
            return 0;
        }

        private static void Run( Options options )
        {
            List<string> modifiedPackageFiles = new List<string>();
            List<string> deletedPackageFiles = new List<string>();
            Dictionary<string, bool> modifiedProjects = new Dictionary<string, bool>();

            //TODO -- make sure the Rock.Version project's version number has been updated and commit->pushed
            //        before you do the rest.

            GetRockWebChangedFilesAndProjects( options, modifiedPackageFiles, deletedPackageFiles, modifiedProjects );

            // TODO determine how to increment versions
            // TODO determine where to get description from
            BuildPackage( options.RepoPath, options.PackageFolder, modifiedPackageFiles, deletedPackageFiles, modifiedProjects, "0.0.4", "various changes" );

            // TODO create "RockChMS" wrapper package as per: https://github.com/SparkDevNetwork/Rock-ChMS/wiki/Packaging-Rock-Core-Updates

            // TODO -- remove this
            Console.ReadLine();
        }

        /// <summary>
        /// Determines what package files were changed since the last "package build".
        /// </summary>
        /// <param name="repoPath">the path to the git repository</param>
        /// <param name="repoBranch">the branch in the git repository to operate against</param>
        /// <param name="modifiedPackageFiles">a list of files that were modified in the RockWeb project</param>
        /// <param name="deletedPackageFiles">a list of files that were deleted from the RockWeb project</param>
        /// <param name="modifiedProjects">a list of projects that were modified</param>
        private static void GetRockWebChangedFilesAndProjects( Options options, List<string> modifiedPackageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects )
        {
            int webRootPathLength = @"rockweb\".Length;

            // Open the git repo and get the commits for the given branch.
            using ( var repo = new Repository( options.RepoPath ) )
            {
                Branch branch = repo.Branches[ options.RepoBranch ];

                var commits = branch.Commits;

                // Now go through each commit since the last pagckage commit and
                // determine which projects (dlls) and files from the RockWeb project
                // need to be included in the package.

                // TODO device resonable mechanism to determine which was the last commit SHA for the last "package" 
                foreach ( var c in repo.Commits.StartingAfter( LAST_PACKAGE_COMMIT_SHA ) )
                {
                    Console.WriteLine( string.Format( "id: {0} {1}", c.Id, c.Message ) );

                    TreeChanges changes = repo.Diff.Compare( c.Tree, branch.Tip.Tree );

                    foreach ( var file in changes )
                    {
                        // skip a bunch of known projects we don't care about...
                        if ( file.Path.ToLower().EndsWith( ".gitignore" ) || file.Path.StartsWith( @"Apps\" ) ||
                            file.Path.StartsWith( @"Dev Tools\" ) || file.Path.StartsWith( @"Documentation\" ) ||
                            file.Path.StartsWith( @"RockInstaller\" ) || file.Path.StartsWith( @"Rock Installer\" ) ||
                            file.Path.StartsWith( @"Rock.CodeGeneration\" ) || file.Path.StartsWith( @"libs\" ) || file.Path.StartsWith( @"packages\" ) ||
                            file.Path.StartsWith( @"RockJobSchedulerService\" ) || file.Path.StartsWith( @"RockJobSchedulerServiceInstaller\" ) )
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
                            Console.WriteLine( "{0}\t{1} (ASSEMBLY)", file.Path, file.Status );
                            //modifiedPackageFiles.Add( file.Path );

                            if ( file.Status == ChangeKind.Added || file.Status == ChangeKind.Modified )
                            {
                                var filePathSuffix = file.Path.Substring( webRootPathLength );
                                //modifiedPackageFiles.Add( filePathSuffix );
                                Console.WriteLine( string.Format( "NOTE: {0} DLL changed since last build. You need add it to the lib folder.", filePathSuffix ) );
                            }
                            else if ( file.Status == ChangeKind.Deleted )
                            {
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
        }

        public static void BuildPackage( string repoPath, string packageFolder, List<string> packageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects, string version, string description )
        {
            string dashVersion = version.Replace( '.', '-' );

            // Create a manifest for this package...
            Manifest manifest = new Manifest();
            manifest.Metadata = new ManifestMetadata()
            {
                Authors = "SparkDevelopmentNetwork",
                Version = version,
                Id = "RockUpdate-" + dashVersion,
                Description = description,
            };

            manifest.Files = new List<ManifestFile>();
            string webRootPath = Path.Combine( repoPath, "RockWeb" );
            foreach ( string file in packageFiles )
            {
                // Skip the root web.config
                if ( file.ToLower() == "web.config" )
                {
                    Console.WriteLine( "WARNING: web.config file was changed since last build." );
                    continue;
                }
                
                AddToManifest( manifest, file, webRootPath );
            }

            // write out all the files to delete in the deletefile.lst ) 
            string deleteFileRelativePath = Path.Combine( "App_Data", "deletefile.lst" );
            string deleteFileFullPath = Path.Combine( webRootPath, deleteFileRelativePath );
            if ( deletedPackageFiles.Count > 0 )
            {
                using ( StreamWriter w = File.AppendText( deleteFileFullPath ) )
                {
                    foreach ( string delete in deletedPackageFiles )
                    {
                        w.WriteLine( delete );
                    }
                }

                AddToManifest( manifest, deleteFileRelativePath, webRootPath );
            }

            string packageFileName = Path.Combine( packageFolder, string.Format( "RockUpdate-{0}.{1}.nupkg", dashVersion, version ) );

            PackageBuilder builder = new PackageBuilder();
            builder.PopulateFiles( repoPath, manifest.Files );
            builder.Populate( manifest.Metadata );
            using ( FileStream stream = File.Open( packageFileName, FileMode.OpenOrCreate ) )
            {
                builder.Save( stream );
            }

            foreach( KeyValuePair<string, bool> entry in modifiedProjects )
            {
                Console.WriteLine( string.Format( "NOTE: {0} project files were changed since last build. You need to build and add them to the lib folder.", entry.Key ) );
            }
        }

        //public static void PrintTree( Repository repo, Tree tree )
        //{
        //    foreach ( var item in tree )
        //    {
        //        if ( item.Name == ".gitignore" )
        //        {
        //            var x = item;
        //        }

        //        Console.WriteLine( string.Format( "\t{0} : {1} : {2}", item.Target, item.Path, item.Name ) );
        //        var tree2 = repo.Lookup<Tree>( item.Target.Sha );
        //        if ( tree2 != null )
        //        {
        //            PrintTree( repo, tree2 );
        //        }
        //    }
        //}

        #region NuGet Package Helper Methods

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

    public static class LibGit2Extensions
    {


        public static List<Commit> StartingAfter( this IQueryableCommitLog obj, string sha )
        {
            List<Commit> results = new List<Commit>();
            foreach ( var commit in obj )
            {
                if ( commit.Sha == sha )
                {
                    results.Add( commit );
                    return results;
                }
            }
            return results;
        }
    }
    #endregion
}