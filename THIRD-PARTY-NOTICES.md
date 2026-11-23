# Third-party notices

OpenFairway's own code is MIT-licensed (see `LICENSE`). The release package also bundles the following, unmodified.

| Component | Licence | Where | Source |
|---|---|---|---|
| Unity Doorstop 4.6.0 | GNU LGPL 2.1 | `files\payload\winhttp.dll` (installed next to the game as `version.dll`), licence in `Doorstop-LICENSE.txt` | https://github.com/NeighTools/UnityDoorstop |
| .NET 8 runtime | MIT, with third-party notices | `files\payload\dotnet\`, licences in `dotnet-LICENSE.txt` and `dotnet-ThirdPartyNotices.txt` | https://github.com/dotnet/runtime |

Unity Doorstop is used as a separate, replaceable library: it is the unmodified upstream build, loaded next to the
game, and can be swapped for any compatible build of the same version.

Tools in `tools/` use Python packages (NumPy, SciPy, Pillow, moderngl) and ffmpeg at development time only; none of
them is part of the package.

PGA TOUR 2K25 is a trademark of its owners. OpenFairway contains no code, assets or data from the game; it reads
what it needs from the running game at run time.
