﻿using System.Collections.Generic;
using CommandLine;
using CommandLine.Text;

namespace RockPackageBuilder
{
    // Define a class to receive parsed values
    public class RockPackageBuilderCommandLineOptions
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

        [Option( 'r', "repoPath", DefaultValue = @"\src\SparkDevNetwork\Rock", HelpText = "The path to your local git repository." )]
        public string RepoPath { get; set; }

        [Option( 'p', "packageFolder", DefaultValue = @"..\..\..\NuGetLocal", HelpText = "The folder to put the output package." )]
        public string PackageFolder { get; set; }

        [Option( "rockPackageFolder", DefaultValue = @"..\..\..\RockPackages", HelpText = "The folder to put the output rock package." )]
        public string RockPackageFolder { get; set; }

        [Option( 'i', "installArtifactsFolder", DefaultValue = @"..\..\..\InstallerArtifacts", HelpText = "The folder to put the empty dummy packages for use with the installer." )]
        public string InstallArtifactsFolder { get; set; }

        [Option( 'v', "verbose", DefaultValue = false, HelpText = "Set to true to see a more verbose output of what's changed in the repo." )]
        public bool Verbose { get; set; }

        [Option( 't', "testing", DefaultValue = false, HelpText = "Set to true to just see the list of changed files between the two tagged releases." )]
        public bool Testing { get; set; }

        [Option( 'd', "includepdb", DefaultValue = false, HelpText = "Set to true to include all of the pdb files otherwise just the Rock and Rock.Rest pdbs will be included." )]
        public bool IncludePdb { get; set; }

        [Option( 'b', "previousVersionBinFolderPath", Required = true, HelpText = "The path to a clean bin folder of the previous version. Used to compare bin folders and get files that are not included in the solution." )]
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
}