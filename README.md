Rock-BuildUpdatePackage
=======================

The code that builds the update packages.

See [Packaging Rock Core Updates](https://github.com/SparkDevNetwork/Rock/wiki/Packaging-Rock-Core-Updates)
for details.

Usage:

    RockPackageBuilder.exe -r <path to local repo> -l <last version> -c <current version>
    
Example:

    RockPackageBuilder.exe -r C:\Misc\Rock-ChMS -l 0.1.6 -c 0.1.7