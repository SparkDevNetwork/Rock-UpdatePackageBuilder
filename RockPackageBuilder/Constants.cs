using System.Collections.Generic;

namespace RockPackageBuilder
{
    static class Constants
    {
        /// <summary>
        /// Name of the NuGet Package Prefix for the actual update delta packages.
        /// </summary>
        public const string ROCKUPDATE_PACKAGE_PREFIX = "RockUpdate";

        /// <summary>
        /// The warning message for when web.config transformation is needed.
        /// </summary>
        public const string WEBCONFIG_XDT_MESSAGE = @"--> ACTION! web.config file was changed since last build. Figure out
            how you're going to handle that. You'll probably have to
            create a web.config.rock.xdt file. See the Packaging-Rock-Core-Updates
            wiki page for details on doing that.";

        /// <summary>
        /// The projects to ignore. These projects will not be used to create the package and 
        /// files with these names will be excluded in the bin folder compare.
        /// </summary>
        public static readonly List<string> PROJECTS_TO_IGNORE = new List<string>
        {
            "rock.codegeneration",
            "rock.mywell",
            "rock.specs",
            "rock.tests",
            "rock.tests.integration",
            "rock.tests.shared",
            "rock.tests.unittests",
            "rock.tests.performance"
        };

        public static readonly List<string> DEFAULT_PDB_TO_INCLUDE = new List<string>
        {
            "rock",
            "rock.rest"
        };
    }
}
