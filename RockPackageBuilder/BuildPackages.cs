﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LibGit2Sharp;
using NuGet;

namespace RockPackageBuilder
{
    /// <summary>
    /// The Rock Update package builder.  Read about it here:
    /// https://github.com/SparkDevNetwork/Rock-ChMS/wiki/Packaging-Rock-Core-Updates
    /// </summary>
    class BuildPackages
    {
        #region Properties

        static string _previousVersion = string.Empty;
        static string _defaultDescription = "##TODO##";

        static readonly char[] _spinnerChars = { '|', '/', '-', '\\' };
        static int _spinnerIndex = 0;

        #endregion

        /// <summary>
        /// Entry point and check command line parameters.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        static int Main( string[] args )
        {
            Console.BufferHeight = 9999;
            var options = new RockPackageBuilderCommandLineOptions();
            var parser = new CommandLine.Parser( with => with.HelpWriter = Console.Error );
            if ( parser.ParseArgumentsStrict( args, options, () => Exit() ) )
            {
                if ( !Directory.Exists( options.RepoPath ) )
                {
                    Console.WriteLine( $"The given repository folder ({options.RepoPath}) does not exist." );
                    Console.WriteLine( options.GetUsage() );
                    Exit();
                }

                if ( !Directory.Exists( options.RockPackageFolder ) )
                {
                    Console.WriteLine( $"The given output rock package folder ({options.RockPackageFolder}) does not exist." );
                    Console.WriteLine( options.GetUsage() );
                    Exit();
                }

                if ( !Directory.Exists( options.InstallArtifactsFolder ) )
                {
                    Directory.CreateDirectory( options.InstallArtifactsFolder );
                }

                // This is where the installer will store pdb files and where it will look for xdt files.
                var artifactsFolderForVersion = Path.Combine( options.ArtifactsFolder, options.PublicVersion );
                if ( !Directory.Exists( artifactsFolderForVersion ) )
                {
                    Directory.CreateDirectory( artifactsFolderForVersion );
                }
                else
                {
                    // Delete any old pdb files since they may be out of sync with the dlls in the repo
                    Directory.EnumerateFiles( artifactsFolderForVersion, "*.pdb" ).ToList().ForEach( x => File.Delete( x ) );
                }

                return Run( options );
            }

            return 0;
        }

        /// <summary>
        /// The main runner.
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        private static int Run( RockPackageBuilderCommandLineOptions options )
        {
            List<string> modifiedLibs = new List<string>();
            List<string> modifiedPackageFiles = new List<string>();
            List<string> deletedPackageFiles = new List<string>();
            Dictionary<string, bool> modifiedProjects = new Dictionary<string, bool>();

            options.ProjectsInSolution = GetProjects( options );

            if ( !ContinueAfterReadingSplashScreen( options ) )
            {
                return 1;
            }

            string publicVersion = options.PublicVersion;

            _defaultDescription = $"Rock {publicVersion} fixes issues that were reported during the previous release(s) (See <a href='https://www.rockrms.com/releasenotes#v{publicVersion}'>Release Notes</a> for details).";

            string rockUpdatePackageFileName = Path.Combine( options.RockPackageFolder, $"{options.PublicVersion}.rockpkg" );
            if ( File.Exists( rockUpdatePackageFileName ) )
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write( "WARNING: The package {0} already exists.{1}Do you want to overwrite it? Y/N (n to quit) ", rockUpdatePackageFileName, Environment.NewLine );
                Console.ResetColor();

                ConsoleKeyInfo choice = Console.ReadKey( true );
                Console.Write( "\b" );
                Console.WriteLine( "" );
                if ( choice.KeyChar.ToString().ToLowerInvariant() != "y" )
                {
                    return 1;
                }
            }

            GetRockWebChangedFilesAndProjects( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects );
            GetUpdatedFilesNotInSolution( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects );
            VerifyDeletedFiles( options, ref deletedPackageFiles );

            // Make sure the Rock.Version project's version number has been updated and commit->pushed before you build from master.
            if ( !VerifyVersionNumbers( options.RepoPath, options.CurrentVersionTag, modifiedProjects ) )
            {
                return 1;
            }

            string actionWarnings = string.Empty;

            if ( !options.Testing )
            {
                _ = RockPackageBuilder.BuildRockPackage( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects, out actionWarnings );
            }
            else
            {
                OnlyOutputChanges( options, modifiedLibs, modifiedPackageFiles, deletedPackageFiles, modifiedProjects, "various changes", out actionWarnings );
            }

            if ( !string.IsNullOrEmpty( actionWarnings ) )
            {
                Console.WriteLine( "" );
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine( actionWarnings );
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            Console.WriteLine( "" );
            Console.ForegroundColor = ConsoleColor.DarkGreen;
            Console.WriteLine( "The package {0} is ready for you to deploy.{1}", rockUpdatePackageFileName, Environment.NewLine );
            Console.ResetColor();

            Console.WriteLine( "" );

            Console.Write( "Press any key to finish." );
            Console.ReadKey( true );

            return 0;
        }

        /// <summary>
        /// Parse the solution file to get a list of the projects
        /// </summary>
        /// <param name="options">The options.</param>
        /// <returns></returns>
        private static List<string> GetProjects( RockPackageBuilderCommandLineOptions options )
        {
            string solutionPath = Path.Combine( options.RepoPath, "Rock.sln" );
            var solutionText = File.ReadAllText( solutionPath );

            Regex projReg = new Regex( "Project\\(\"\\{[\\w-]*\\}\"\\) = \"([\\w _]*.*)\", \"(.*\\.(cs|vcx|vb)proj)\"", RegexOptions.Compiled );
            var matches = projReg.Matches( solutionText ).Cast<Match>();
            var projects = matches.Select( x => x.Groups[1].Value.ToLower() ).ToList();

            return projects.Where( p => !Constants.PROJECTS_TO_IGNORE.Contains( p ) ).ToList();
        }

        /// <summary>
        /// An important message for the packager to read before continuing.
        /// </summary>
        /// <returns>False if packaging should not continue.</returns>
        private static bool ContinueAfterReadingSplashScreen( RockPackageBuilderCommandLineOptions options )
        {
            string repoPath = options.RepoPath;

            Console.Write( "Building update package from version " );
            Console.BackgroundColor = ConsoleColor.Blue;
            Console.Write( options.LastVersionTag );
            Console.ResetColor();
            Console.Write( " to version " );
            Console.BackgroundColor = ConsoleColor.Blue;
            Console.Write( options.PublicVersion );
            Console.ResetColor();
            Console.Write( " (from tag " + options.CurrentVersionTag + ") " );
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
                if ( Constants.PROJECTS_TO_IGNORE.Contains( name ) )
                {
                    continue;
                }

                if ( name.StartsWith( "rock" ) && !options.ProjectsInSolution.Contains( folder.Name.ToLower() ) && File.Exists( Path.Combine( folder.FullName, folder + ".csproj" ) ) )
                {
                    missing.Add( folder.Name );
                }
            }

            if ( missing.Any() )
            {
                Console.WriteLine( "WARNING! The following are Rock.* projects that are not included in the " );
                Console.WriteLine( "         NON_WEB_PROJECTS list.  That means changes to these projects " );
                Console.Write( "         " );
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
            string versionPrefix = version.Split( '-' )[0];

            foreach ( string projectName in modifiedProjects
                .Where( p =>
                    p.Value &&
                    p.Key.ToLower().StartsWith( "rock" ) )
                .Select( p => p.Key ) )
            {
                string dll = projectName + ".dll";
                FileVersionInfo rockDLLfvi = FileVersionInfo.GetVersionInfo( Path.Combine( repoPath, "RockWeb", "bin", dll ) );
                var y = rockDLLfvi.ProductVersion;
                if ( !rockDLLfvi.FileVersion.StartsWith( versionPrefix ) )
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
        private static void GetRockWebChangedFilesAndProjects( RockPackageBuilderCommandLineOptions options, List<string> modifiedLibs, List<string> modifiedPackageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects )
        {
            int webRootPathLength = @"rockweb\".Length;

            // Open the git repo and get the commits for the given branch.
            using ( var repo = new Repository( options.RepoPath ) )
            {
                Tag tag = repo.Tags[options.CurrentVersionTag]; // current tag
                if ( tag == null && string.IsNullOrWhiteSpace( options.CurrentVersionCommitHash ) )
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine( $"Error: I don't see a {options.CurrentVersionTag} tag.  Did you forget to tag this release?" );
                    Exit();
                }

                Tag previousTag = repo.Tags[options.LastVersionTag];
                if ( previousTag == null )
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine( $"Error: I don't see a {options.LastVersionTag} tag.  Did you enter the correct last version tag?" );
                    Exit();
                }

                var previousCommit = ( Commit ) previousTag.Target;

                Commit currentCommit = null;
                if ( string.IsNullOrWhiteSpace( options.CurrentVersionCommitHash ) )
                {
                    currentCommit = ( Commit ) tag.Target;
                }
                else
                {
                    currentCommit = repo.Lookup<Commit>( options.CurrentVersionCommitHash );
                    if ( currentCommit == null )
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine( $"Error: I don't see a commit with Id {options.CurrentVersionTag}.  Did you copy the hash correctly?" );
                        Exit();
                    }
                }

                // Now go through each commit since the last pagckage commit and
                // determine which projects (dlls) and files from the RockWeb project
                // need to be included in the package.
                Console.WriteLine( "Comparing... (this could take a few minutes)" );

                TreeChanges changes = repo.Diff.Compare( previousCommit.Tree, currentCommit.Tree );

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

                    if ( options.ExpandedVerbose )
                    {
                        //Console.WriteLine( "{0}\t{1}", file.Path, file.Status );
                        System.Diagnostics.Debug.WriteLine( $"{file.Path}    {file.Status}" );
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

        private static void GetUpdatedFilesNotInSolution( RockPackageBuilderCommandLineOptions options, List<string> modifiedLibs, List<string> modifiedPackageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects )
        {
            if ( string.IsNullOrWhiteSpace( options.PreviousVersionRockWebFolderPath ) )
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
            List<string> ignoreFilePatterns = new List<string>() { ".pdb", ".refresh", ".js.map" };

            // Get a list of files from the previous version directory.
            // Filter out files already identified as updated, solution files, and ignore files
            List<string> oldFileList = Directory.GetFiles( Path.Combine( options.PreviousVersionRockWebFolderPath, "Bin" ) )
                .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) // Filter out files by extension
                .Where( f => !Constants.PROJECTS_TO_IGNORE.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) // Filter out files for ignored projects
                // Filter out files that have already been flagged as updated by other checks
                .Where( f => !modifiedLibs.Any( p => Path.Combine( "Bin", Path.GetFileName( f ) ).Equals( p, StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !modifiedPackageFiles.Any( p => Path.Combine( "Bin", Path.GetFileName( f ) ).Equals( Path.Combine( "Bin", Path.GetFileName( p ) ), StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !options.ProjectsInSolution.Any( p => Path.GetFileNameWithoutExtension( f ).Equals( p, StringComparison.OrdinalIgnoreCase ) ) )
                .ToList();

            // The entire Obsidian folder is not in source control, so get every file except the ones that should be ignored.
            if ( Directory.Exists( Path.Combine( options.PreviousVersionRockWebFolderPath, "Obsidian" ) ) )
            {
                oldFileList.AddRange( Directory.GetFiles( Path.Combine( options.PreviousVersionRockWebFolderPath, "Obsidian" ), "*", SearchOption.AllDirectories )
                    .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) );
            }

            // Get the script files, which may or may not be in source control, except the ones that should be ignored.
            if ( Directory.Exists( Path.Combine( options.PreviousVersionRockWebFolderPath, "Scripts" ) ) )
            {
                oldFileList.AddRange( Directory.GetFiles( Path.Combine( options.PreviousVersionRockWebFolderPath, "Scripts" ), "*", SearchOption.AllDirectories )
                    .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) );
            }

            // Get a list of files from the repo directory that contains the version being created
            // Filter out files already identified as updated, solution files, and ignore files
            List<string> newFileList = Directory.GetFiles( Path.Combine( options.RepoPath, "RockWeb", "Bin" ) )
                .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) // Filter out files by extension
                .Where( f => !Constants.PROJECTS_TO_IGNORE.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) // Filter out files for ignored projects
                // Filter out files that have already been flagged as updated by other checks
                .Where( f => !modifiedLibs.Any( p => Path.Combine( "Bin", Path.GetFileName( f ) ).Equals( p, StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !modifiedPackageFiles.Any( p => Path.Combine( "RockWeb", "Bin", Path.GetFileName( f ) ).Equals( Path.Combine( "RockWeb", "Bin", Path.GetFileName( p ) ), StringComparison.OrdinalIgnoreCase ) ) )
                .Where( f => !options.ProjectsInSolution.Any( p => Path.GetFileNameWithoutExtension( f ).Equals( p, StringComparison.OrdinalIgnoreCase ) ) )
                .ToList();

            // A list of all new Obsidian files except the ones that should be ignored.
            if ( Directory.Exists( Path.Combine( options.RepoPath, "RockWeb", "Obsidian" ) ) )
            {
                newFileList.AddRange( Directory.GetFiles( Path.Combine( options.RepoPath, "RockWeb", "Obsidian" ), "*", SearchOption.AllDirectories )
                    .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) );
            }

            // A list of script files filtered by "ignored" and ones that are not already in the "Modified" list.
            if ( Directory.Exists( Path.Combine( options.RepoPath, "RockWeb", "Scripts" ) ) )
            {
                newFileList.AddRange( Directory.GetFiles( Path.Combine( options.RepoPath, "RockWeb", "Scripts" ), "*", SearchOption.AllDirectories )
                    .Where( f => !ignoreFilePatterns.Any( p => f.ToLower().Contains( p.ToLower() ) ) ) 
                    .Where( f => !modifiedPackageFiles.Any( p => p.Equals( f.Replace( Path.Combine( options.RepoPath, "RockWeb" ), string.Empty ).TrimStart( Path.DirectorySeparatorChar ), StringComparison.OrdinalIgnoreCase ) ) ) );
            }

            // loop through the old file list and compare to new file version
            var i = 0;
            foreach ( var oldFile in oldFileList )
            {
                i++;
                ShowProgress( i, oldFileList.Count );
                FileInfo oldFileInfo = new FileInfo( oldFile );

                var relativeDirectory = Path.GetDirectoryName( oldFile ).Replace( options.PreviousVersionRockWebFolderPath, string.Empty ).TrimStart( Path.DirectorySeparatorChar );
                var NewFileAbsoluteDirectory = Path.Combine( options.RepoPath, "RockWeb", relativeDirectory );
                var newFileInfo = new FileInfo( Path.Combine( NewFileAbsoluteDirectory, oldFileInfo.Name ) );

                // Never delete any org.mywell.MyWellGateway.dll from the bin folder.
                if ( relativeDirectory.ToLower() == "bin" && newFileInfo.Name.ToLower().StartsWith( "org.mywell." ) )
                {
                    continue;
                }

                // If exists in old but not new then add to deleted list
                if ( !newFileInfo.Exists )
                {
                    string file = newFileInfo.FullName.Replace( options.RepoPath, string.Empty ).TrimStart( Path.DirectorySeparatorChar );
                    if ( !deletedPackageFiles.Where( d => d.Equals( file, StringComparison.OrdinalIgnoreCase ) ).Any() )
                    {
                        deletedPackageFiles.Add( file );
                    }

                    continue;
                }

                // Compare the old and new Bytes and move on if they are the same
                if ( oldFileInfo.Length == newFileInfo.Length && File.ReadAllBytes( oldFileInfo.FullName ).SequenceEqual( File.ReadAllBytes( newFileInfo.FullName ) ) )
                {
                   continue;
                }

                var oldFileVersionInfo = FileVersionInfo.GetVersionInfo( oldFileInfo.FullName );
                var newFileVersionInfo = FileVersionInfo.GetVersionInfo( newFileInfo.FullName );

                if ( oldFile.EndsWith( ".dll" ) && oldFileVersionInfo.FileVersion == newFileVersionInfo.FileVersion)
                {
                    Console.WriteLine( string.Format( "Skipping .dll file due to matching version: {0} - {1}", oldFileInfo.FullName, oldFileVersionInfo.FileVersion ) );
                    continue;
                }

                string relativeFilePath = Path.Combine( relativeDirectory, oldFileInfo.Name );

                // The file has changed. If dll in the bin folder then add to modifiedLibs, otherwise add to modifiedPackageFiles. This is needed for nuget, once that is not needed then this logic can be removed.
                if ( relativeFilePath.IndexOf("Bin", StringComparison.OrdinalIgnoreCase ) >= 0 && oldFileInfo.Extension == ".dll" )
                {
                    modifiedLibs.Add( relativeFilePath );
                }
                else if ( !modifiedPackageFiles.Contains( relativeFilePath ) )
                {
                    modifiedPackageFiles.Add( relativeFilePath );
                }
            }

            // If exists in new but not old then we need to add the file. Needs to compare relative paths due to different obsidian files having the same name but in different directories.
            var filesToAdd = newFileList
                .Where( n => !oldFileList.Any( o => o.Replace( options.PreviousVersionRockWebFolderPath, string.Empty ).Equals( n.Replace( options.RepoPath + "\\RockWeb", string.Empty ), StringComparison.OrdinalIgnoreCase ) ) )
                .Select( n => n.Replace( Path.Combine( options.RepoPath, "RockWeb" ) + "\\", string.Empty ) )
                .ToList();

            foreach ( var newFile in filesToAdd )
            {
                // If dll then add to modifiedLibs (filename), otherwise add to modifiedPackageFiles (bin\filename)
                if ( Path.GetExtension( newFile ) == ".dll" )
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
        /// Shows a simple progress bar.
        /// </summary>
        /// <param name="currentIndex"></param>
        /// <param name="totalCount"></param>
        private static void ShowProgress( int currentIndex, int totalCount )
        {
            int percent = ( int ) ( ( currentIndex / ( double ) totalCount ) * 100 );
            char spinner = _spinnerChars[_spinnerIndex++ % _spinnerChars.Length];

            if ( percent >= 100 )
            {
                Console.WriteLine( $"\rDone.                 " );
            }
            else
            {
                Console.Write( $"\rProgress: {percent,3}% {spinner}" );
            }
        }

        /// <summary>
        /// Check to see if any of the deleted files actually exist on the updated code. This can happen if a generated file was removed from source control.
        /// </summary>
        /// <param name="deletedFiles">The deleted files.</param>
        private static void VerifyDeletedFiles( RockPackageBuilderCommandLineOptions options, ref List<string> deletedFiles )
        {
            if ( string.IsNullOrWhiteSpace( options.RepoPath ) || !deletedFiles.Any() )
            {
                // There is nothing to compare to so the user will get a message to do this manually.
                return;
            }

            List<string> filesToKeep = new List<string>();

            foreach ( var deletedFile in deletedFiles )
            {
                var path = Path.Combine( options.RepoPath, deletedFile );
                var exists = File.Exists( path );
                if( exists )
                {
                    filesToKeep.Add( deletedFile );
                }
            }

            deletedFiles = deletedFiles.Where( f => !filesToKeep.Contains( f ) ).ToList();
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
        private static void ParseCommitMessages( ICommitLog currentCommits, ICommitLog previousCommits, bool verbose, StringBuilder sb )
        {
            Regex regUpdate = new Regex( @"\[update-([^\]]+\] (.*))", RegexOptions.IgnoreCase );
            List<string> appUpdateBadges = new List<string>();

            var previousShas = previousCommits.Select( c => c.Sha ).ToList();
            var validCommits = currentCommits.Where( c => !previousShas.Contains( c.Sha ) ).ToList();
            foreach ( var commit in validCommits )
            {
                if ( !commit.Message.StartsWith( "Merge pull request" ) &&
                    !commit.Message.StartsWith( "Merge branch" ) &&
                    !commit.Message.StartsWith( "Merge remote-tracking" ) &&
                    !commit.Message.StartsWith( "-" ) )
                {
                    Match match = regUpdate.Match( commit.Message );
                    if ( match.Success )
                    {
                        appUpdateBadges.Add( $"<span title=\"This application needs to be updated. {match.Groups[2].Value.Replace( "\"", "\\\"" )}\" class=\"label label-warning\">{match.Groups[1].Value}</span>" );
                    }
                    else if ( !( commit.Message.StartsWith( "+" ) || commit.Message.StartsWith( " +" ) ) )
                    {
                        // append the commit message an prefix with a + if there isn't one already.
                        sb.AppendFormat( "+ {0}", commit.Message );
                    }
                    else
                    {
                        sb.AppendFormat( "{0}", commit.Message );
                    }

                    if ( !commit.Message.EndsWith( "\n" ) )
                    {
                        sb.Append( '\n' );
                    }
                }

                if ( verbose )
                {
                    Console.WriteLine( $"id: {commit.Id} {commit.Message}" );
                }

            }

            // add any app update badges to bottom of the sb
            if ( appUpdateBadges.Count > 0 )
            {
                sb.AppendFormat( "<br/><h4>{0}</h4>", string.Join( "&nbsp;", appUpdateBadges ) );
            }

            Console.WriteLine( "Found {0} commits.", validCommits.Count() );
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
        private static void OnlyOutputChanges( RockPackageBuilderCommandLineOptions options, List<string> modifiedLibs, List<string> packageFiles, List<string> deletedPackageFiles, Dictionary<string, bool> modifiedProjects, string description, out string actionWarnings )
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
                    Console.WriteLine( Constants.WEBCONFIG_XDT_MESSAGE );
                    Console.ForegroundColor = ConsoleColor.Gray;

                    actionWarnings += Constants.WEBCONFIG_XDT_MESSAGE;
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
                    Console.WriteLine( $"\t * {entry.Key}.dll" );

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

        private static void Exit()
        {
            Console.WriteLine( "Hit any key to quit." );
            Console.ReadKey();
            System.Environment.Exit( -3 );
        }
    }
}