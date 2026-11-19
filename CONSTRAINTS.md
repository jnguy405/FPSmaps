# Map Constraints

How the constraint solver turns a BSP layout into an attack/defense map: which rules exist, where they're set, and what happens when one can't be met.

All code lives in `Assets/Scripts/Constraints/`:

| File | Holds |
|---|---|
| `MapConstraints.cs` | `MapConstraints` (the few settings), `RoomRole`, and `MapPlan` (the result) |
| `MapConstraintSolver.cs` | The solve itself, retries, shared geometry and route helpers, validation entry point |
| `SpawnConstraints.cs` | `SpawnRules`, spawn picking, resizing, side exits, attacker lobbies, spawn checks |
| `MidConstraints.cs` | `MidRules`, mid picking, growing, routes into mid, mid checks |
| `SiteConstraints.cs` | `SiteRules`, site picking and scoring, entrances, site checks |

`MapConstraintSolver` is a `partial` class, so every file adds its own role's part to the same solver.

## Settings

Only three things are adjustable, on the map's `constraints` field in the inspector (and the Sites slider in the Tab panel):

| Setting | Default | What it does |
|---|---|---|
| `enabled` | on | Off gives a plain BSP map with no roles |
| `siteCount` | 2 | 2 or 3 sites (A, B, and optionally C) |
| `maxAttempts` | 20 | Seed variations to try before settling for the closest attempt |

Everything else is a fixed constant, so every map plays by the same rules. To change a rule, edit the constant in its file.

## How a map is solved

`MapConstraintSolver.Solve` tries the requested seed, then derived seeds (`seed + attempt * 7919`), until an attempt breaks no rules. Each attempt runs these steps:

1. **Generate** the BSP layout (rooms, hallways, no dead ends).
2. **Pick roles** for each of the four orientations (spawns on south/north or west/east edges, either way round) and keep the best scoring one:
   - attacker and defender spawn: the rooms that can best sit on the middle of their edge
   - mid: the room that can best cover the map center and be the biggest area
   - sites: A from the left half, B from the right half, C (if any) from the rest
3. **Repair rooms:** spawns to their fixed size, mid grown to fill its partition, sites grown to the minimum size, every other room trimmed below mid's size. Hallways a resize cuts off are joined back in.
4. **Spawn exits:** at least one hallway out each side wall of each spawn. Hallways out a spawn's front or back wall are rebuilt out a side wall.
5. **Entrances**, all drawn from one random generator so results are repeatable:
   - attacker lobbies: each attacker side exit room gets a hallway to its side's site and one to mid
   - mid routes: a way into mid from each team's own half
   - site entrances: a hallway from mid, a route in for each team, then extras up to the preferred count
6. **Safety net:** any room that can't be walked to, or has only one hallway, gets another hallway. Spawns and mid are locked so this can't break their rules.
7. **Validate:** check every rule, record failures (used to decide whether to retry) and the report (shown in the panel and inspector).

If no attempt passes, the closest one is kept: any attempt with sites beats one without, then fewer failures wins. Failures are never shown on screen, only the report.

## Rules

Each rule is either a **requirement** (breaking it fails the attempt, so the solver retries) or a **target** (it steers which rooms get picked and adds a note to the report, but never fails a map).

### Spawns (`SpawnRules`)

| Rule | Value | Kind |
|---|---|---|
| Spawn size | 12 cells along the edge × 6 cells into the map | Requirement |
| Gap to its edge | at most 0.15 of the map length | Requirement |
| Offset from the edge's middle | at most 0.2 of the edge (keeps spawns out of corners) | Requirement |
| Exits per side | at least 1 out the left wall and 1 out the right wall | Requirement |
| Front or back hallways | none | Requirement |
| Attacker lobbies | each attacker side exit leads to a plain room with a hallway to its site (A left, B right) and one to mid | Requirement |
| Lobby hallway to mid | can't run through a site | Requirement |

### Mid (`MidRules`)

| Rule | Value | Kind |
|---|---|---|
| Distance from the map center | touches it, or within 0.1 of the map size | Requirement |
| Size | the biggest room on the map | Requirement |
| Rooms between attacker spawn and mid | at least 1 | Requirement |
| Rooms between defender spawn and mid | at least 1 | Requirement |
| Team routes | each team has a way in from its own half that leaves its spawn through a side exit and doesn't go through a site | Requirement |
| Attacker route length | passes at most 2 rooms | Requirement |
| Defender lead | defenders reach mid a bit sooner: (attacker walk − defender walk) / (both walks) near 0.1, ± 0.25 | Target |

### Sites (`SiteRules`)

| Rule | Value | Kind |
|---|---|---|
| Size | at least 36 cells | Requirement |
| Sides | A on the left half, B on the right half | Requirement |
| Separation | at least 0.3 of the spawn-to-spawn walk apart | Requirement |
| Entrances per side | at least 1 from the attacker side and 1 from the defender side (2 preferred, a note if fewer) | Requirement |
| Mid entrance | every site gets a hallway from mid, counted as a defender entrance | Built in |
| Team routes | each team can walk in from its own side without passing the other spawn | Requirement |
| Mid access | the site reaches mid without going through a spawn | Requirement |
| Position | aims for 0.58 of the way from the attacker edge to the defender edge | Target |
| Defender share | defender walk between 0.5 and 0.9 of the attacker walk (defenders first, but not instantly) | Target |
| Attacker walk | within 0.75 of the spawn-to-spawn walk | Target |
| Even walks | mid, attacker and defender walks about the same for every site | Target |

### Whole map

| Rule | Kind |
|---|---|
| Every room can be walked to | Requirement |
| No room has only one hallway | Built in (the safety net adds one) |

## Scoring weights

Picking uses costs rather than hard filters wherever it can, so there's always a choice to explain in the report. These constants sit next to the code that uses them:

| Weight | Value | File | Effect |
|---|---|---|---|
| `NoSitesPenalty` | 1000 | solver | an orientation with no valid site set always loses |
| `SpawnSizePenalty` | 1 | spawn | a spawn whose partition can't hold the fixed size |
| `MidGapPenalty` | 1 | mid | a mid candidate too few rooms from a spawn |
| `MidSizeWeight` | 0.5 | mid | a mid candidate by how far it falls short of the biggest room |
| `SitePositionWeight` | 2 | site | per fraction of the map a site sits off its target position |
| `SiteShareWeight` | 1 | site | per fraction the defender share falls outside its window |
| `FarSitePenalty` | 2 | site | per fraction of the spawn walk past the max attacker walk |
| `SiteEvenWeight` | 0.5 | site | uneven walks between sites, a mild preference only |
| `SiteSpreadWeight` | 0.1 | site | tie-breaker: A and B out to their sides, C near the center line |

## Changing a rule

- **Change a value:** edit the constant in `SpawnRules`, `MidRules` or `SiteRules`.
- **Make a target strict:** in the role's `Check...` function, add the message to `plan.failures` instead of `plan.report`.
- **Add a rule:** put its constant in the role's rules class, check it in that role's `Check...` function, and, if the solver should build toward it, add the step to `TrySolve` in `MapConstraintSolver.cs`.

Keep new random draws inside the shared entrance generator and in a fixed order, so the same seed always gives the same map.
