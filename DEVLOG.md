# Devlog

A dated history of FPSmaps, built from the commit log. Newest entries are at the bottom.

## Mon 11/02: Project setup and Phase 1

- Created the Unity 6 project (URP, Input System) and fixed up the folder layout
- First-person controller: walk, sprint, jump and mouse look, using the Input System only
- Hand-built test room (doorway, crates, stairs, ramp) to tune the controller against
- BSP generator: recursive splits, one room per partition, corridors joining sibling partitions
- Layout drawn in 2D in the Scene view and as an in-game minimap
- Seeded generation with `System.Random`, so a seed always gives the same layout
- Editor menu **FPSmaps → Build Test Scene** to set up the room, player and map in one click

## Tue 11/10: Phase 1.5, 2D to 3D

- `MapMeshBuilder`: the 2D cell grid becomes 3D floor and wall meshes with UVs and collision
- The seed used in Play mode is carried back into the scene afterwards, so a map you like stays
- Went back over the earlier code and annotated it with comments
- First README, covering controls, phases and project layout

## Mon 11/10 - 11/16: Constraint solver and in-game panel

- `MapConstraintSolver`: gives rooms attack/defense roles (attacker spawn, defender spawn, mid, sites A/B/C)
  - spawns on the middle of opposite edges, mid over the map center, sites on opposite sides
  - grows rooms to fit their role, adds site entrances, checks every rule and writes a report
  - retries derived seeds until a map passes, keeping the closest attempt otherwise
- `MapUI`: Tab panel with a seed field (numbers or words), Generate and Random buttons, layout and constraint sliders, and the report
- Refactored the generator and map functions into smaller pieces

## Wed 11/18: Refactor and polish

- Cleaned up the solver's loose values into structs and tidier helpers
- `MapStyle`: one shared palette and set of GUI styles for the minimap, panel and HUD
- Role-tinted floors and darker wall tops, so the layout reads at a glance
- Designer view (**V**): overhead orbit camera over the whole map, with an adjustable wall height
- HUD crosshair and a label for the area the player is standing in

## Thu 11/19: Playability rules and cleanup

- Split the solver into `Assets/Scripts/Constraints/` as one partial class across core, spawn, mid and site files
- Made every rule a fixed constant (`SpawnRules`, `MidRules`, `SiteRules`); only on/off, site count and max attempts are settings
- **Spawns:** fixed 12×6 size, hallways only out the side walls (front ones rerouted), and each attacker side exit leads to a lobby with one hallway to its site and one to mid
- **Mid:** fills its partition and stays the biggest room, with a route in from each team's own half that never goes through a site; attackers reach it within 2 rooms
- **Sites:** picked by position (just past the middle, 0.58 across the map) instead of early walk distances, which had been pushing them onto the defender spawn
- Walk balance and distance targets became report notes instead of failures, so far more maps pass: 200 out of 200 test seeds, up from about 30 of 200 before
- **Hallways:** connect door to door on facing walls, spaced so two never run side by side; no dead-end or cut-off rooms
- Failure messages hidden from the panel, inspector and minimap (the solver still uses them to choose between attempts)
- Removed the hand-built test room, since the player now spawns straight into the map
- Trimmed the constraint code without changing its output (checked against 350 saved results)
- After the commit: added `CONSTRAINTS.md` (every rule, its value and how to change it) and this devlog

## Next

- UVs and textures for floors and walls
- Audio
- WebGL build for itch.io
