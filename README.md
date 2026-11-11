# FPSmaps

A seeded procedural map generator for first-person levels, built in Unity for CMPM 169. Maps are laid out with binary space partitioning (BSP), connected with corridors, and built as walkable 3D geometry. The same seed always gives the same map.


## Controls

| Key | Action |
| --- | --- |
| WASD | Move |
| Shift | Sprint |
| Space | Jump |
| Mouse | Look (click to lock the cursor, Esc to release) |
| M | Toggle the minimap |
| N | Generate a new map from a random seed |
| R | Regenerate the current seed (the hash should not change) |

## Phases

### Phase 1: 2D map and base walls (11/02)

- First-person controller tested in a hand-built cube room with a doorway, crates, stairs and a ramp
- BSP split and room placement, drawn in 2D in the Scene view and as an in-game minimap
- Layout hash and a **Verify Determinism** button to prove a seed always gives the same layout

### Phase 1.5: 2D maps rendered to 3D (11/10)

- L-shaped corridors join sibling partitions, and a flood fill checks that every room is reachable
- Floor and wall meshes are generated from the 2D layout, with world-space UVs and mesh colliders
- The 3D map rebuilds live in the editor when settings change, and the player spawns in the first room
- The seed used in Play mode is kept when you exit Play mode, so a map you like is still there next time
- Code annotated throughout with inline comments

## Project Layout

| File | Purpose |
| --- | --- |
| `Assets/Scripts/BSPGenerator.cs` | BSP split, room placement, corridors, cell grid and reachability check |
| `Assets/Scripts/MapMeshBuilder.cs` | Turns the cell grid into floor, wall and ceiling meshes |
| `Assets/Scripts/baseMap.cs` | Holds the seed and settings, builds the 3D map, draws the minimap and gizmos |
| `Assets/Scripts/FirstPersonController.cs` | Player movement and mouse look |
| `Assets/Editor/FPSmapsEditor.cs` | Inspector buttons, Play mode seed keeper, test scene builder |

## Future Plans

- Tiled textures on floors and walls, with noise variation if time allows
- In-game UI: seed field, Generate button and sliders for the generation settings
- Audio feedback
- WebGL build published on itch.io
- Playtesting, polish and stretch goals in the final week
