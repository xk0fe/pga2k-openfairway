# Third-party notices

OpenFairway's own code is MIT-licensed (see `LICENSE`). The release package also bundles the following, unmodified.

| Component | Licence | Where | Source |
|---|---|---|---|
| Unity Doorstop 4.6.0 | GNU LGPL 2.1 | `files\payload\winhttp.dll` (installed next to the game as `version.dll`), licence in `Doorstop-LICENSE.txt` | https://github.com/NeighTools/UnityDoorstop |
| .NET 8 runtime | MIT, with third-party notices | `files\payload\dotnet\`, licences in `dotnet-LICENSE.txt` and `dotnet-ThirdPartyNotices.txt` | https://github.com/dotnet/runtime |

Unity Doorstop is used as a separate, replaceable library: it is the unmodified upstream build, loaded next to the
game, and can be swapped for any compatible build of the same version.

Tools in `tools/` use Python packages (NumPy, SciPy, Pillow, moderngl, rasterio, laspy, requests) and ffmpeg at
development time only; none of them is part of the package.

## Data

The golf hole that turns in the launcher is a real one, rendered by `tools/render-hole.py` from two public-domain
datasets of the United States government. Only the rendered frames ship (`src/OpenFairway.Launcher/Assets/hole.bin`);
the data itself is downloaded at build time and is not in this repository.

| Data | Owner | Licence | Source |
|---|---|---|---|
| Lidar point cloud, FL Peninsular FDEM Orange 2018 (3D Elevation Program) | U.S. Geological Survey | Public domain | https://registry.opendata.aws/usgs-lidar/ |
| National Agriculture Imagery Program aerial photography | USDA Farm Production and Conservation Business Center | Public domain | https://planetarycomputer.microsoft.com/dataset/naip |

Neither agency endorses OpenFairway.

PGA TOUR 2K25 is a trademark of its owners. OpenFairway contains no code, assets or data from the game; it reads
what it needs from the running game at run time.
