# Third-party notices

OpenFairway's own code is MIT-licensed (see `LICENSE`). The release package also bundles the following, unmodified.

| Component | Licence | Where | Source |
|---|---|---|---|
| Unity Doorstop 4.6.0 | GNU LGPL 2.1 | `files\payload\winhttp.dll` (installed next to the game as `version.dll`), licence in `Doorstop-LICENSE.txt` | https://github.com/NeighTools/UnityDoorstop |
| .NET 8 runtime | MIT, with third-party notices | `files\payload\dotnet\`, licences in `dotnet-LICENSE.txt` and `dotnet-ThirdPartyNotices.txt` | https://github.com/dotnet/runtime |

Unity Doorstop is used as a separate, replaceable library: it is the unmodified upstream build, loaded next to the
game, and can be swapped for any compatible build of the same version.

Tools in `tools/` use Python packages (NumPy, SciPy, Pillow, moderngl, rasterio, laspy, requests, shapely) and ffmpeg at
development time only; none of them is part of the package.

## Data

The golf hole that turns in the launcher is a real one, rendered by `tools/render-hole.py`: the scan from two
public-domain datasets of the United States government, and the blueprint around it from OpenStreetMap. What ships
is the scene the launcher draws (`src/OpenFairway.Launcher/Assets/hole.scene`): the ground's heights on a 0.5 m grid,
the photo and the drawing of the course. The downloaded data itself is not in this repository.

| Data | Owner | Licence | Source |
|---|---|---|---|
| Lidar point cloud, FL Peninsular FDEM Orange 2018 (3D Elevation Program) | U.S. Geological Survey | Public domain | https://registry.opendata.aws/usgs-lidar/ |
| National Agriculture Imagery Program aerial photography | USDA Farm Production and Conservation Business Center | Public domain | https://planetarycomputer.microsoft.com/dataset/naip |
| Map data: the course's fairways, greens, bunkers, tees, water, paths, roads, buildings and hole routings | OpenStreetMap contributors | Open Database License (ODbL) 1.0 | https://www.openstreetmap.org/copyright |

The blueprint is a work produced from OpenStreetMap data; the launcher credits it on the sheet itself
("MAP © OPENSTREETMAP CONTRIBUTORS"). Neither agency, nor the OpenStreetMap Foundation, endorses OpenFairway.

PGA TOUR 2K25 and PGA TOUR 2K23 are trademarks of their owners. OpenFairway contains no code, assets or data from
either game; it reads what it needs from the running game at run time.
