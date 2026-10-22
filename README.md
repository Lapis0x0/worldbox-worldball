# WorldBall

English | [中文](README.zh-CN.md)

<img src="GameResources/ui/globe_template_planet.png" alt="WorldBall" width="100">

WorldBox gives you a world in a box; this mod turns it into a ball. It adds a Planet world template: the map wraps around from east to west, and zooming out reveals the whole planet, which you can spin.

- Requires: [NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader)

## Features

- Planet template: a 2:1 map where x is longitude and y is latitude. Terrain is generated directly on the sphere, so continents keep natural shapes on the globe; mountains form ranges, and the poles are covered by permanent ice
- East-west wraparound: the left and right edges of the map are connected. People walk straight across the seam, cities and kingdoms expand across it, and finding enemies, finding prey and attack ranges all use the real distance across the seam
- Globe view: zoom out past the vanilla limit and the map curls into a sphere. Drag with the right or middle mouse button to spin it, or drag with a finger on mobile
- The full vanilla picture on the globe: terrain, borders, buildings, war lines, city lights and era filters all appear on the sphere; armies, kings, battles and other markers stand upright on it, and name plates follow the globe and hide on the far side
- Play as usual on the globe: god powers, selecting units and tooltips work just like on the flat map
- The flat view is exactly the same as vanilla

Only worlds created with the Planet template are affected.

## Manual installation

1. Install [NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader)
2. Download this repository (Code → Download ZIP)
3. Extract it into the `Mods` folder in the game directory and rename the extracted folder to `WorldBall`, so that you get `Mods/WorldBall/mod.json`. For the Steam version, right-click WorldBox in your library → Manage → Browse local files
4. Start the game; NML compiles and loads the mod automatically
