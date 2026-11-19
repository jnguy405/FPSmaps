using System;
using System.Collections.Generic;
using UnityEngine;

// assigns spawns, mid and sites to BSP rooms, adds their entrances, then checks every rule
// deterministic: the same seed, settings and constraints always give the same plan
// this file runs the solve and holds the helpers every role shares
// SpawnConstraints.cs, MidConstraints.cs and SiteConstraints.cs hold each role's rules, picking, repair and checks
public static partial class MapConstraintSolver
{
    // used for unreachable rooms so they never win a "closest" comparison
    const int Unreachable = int.MaxValue / 4;

    // added to an orientation that finds no valid set of sites, so one that does always wins
    const float NoSitesPenalty = 1000f;

    // which pair of opposite edges the spawns sit on
    // along runs 0 at the attacker edge to 1 at the defender edge, across runs -0.5 to 0.5 side to side
    struct Orientation
    {
        public bool alongX;        // true: spawns on west/east edges, false: south/north edges
        public bool attackerLow;   // true: attackers on the south (or west) edge

        public string Name => alongX
            ? (attackerLow ? "Attackers west, defenders east" : "Attackers east, defenders west")
            : (attackerLow ? "Attackers south, defenders north" : "Attackers north, defenders south");
    }

    static readonly Orientation[] Orientations =
    {
        new Orientation { alongX = false, attackerLow = true },
        new Orientation { alongX = false, attackerLow = false },
        new Orientation { alongX = true, attackerLow = true },
        new Orientation { alongX = true, attackerLow = false },
    };

    // SOLVE

    // tries the seed, then derived seeds, until one meets every constraint
    // falls back to the closest attempt so there is always a map to show
    public static MapPlan Solve(int seed, BSPSettings settings, MapConstraints c)
    {
        // constraints off: plain BSP map with no roles
        if (!c.enabled)
            return new MapPlan { layout = BSPGenerator.Generate(seed, settings) };

        MapPlan best = null;
        for (int attempt = 0; attempt < c.maxAttempts; attempt++)
        {
            // each attempt's seed is derived the same way every time, so retries are repeatable too
            int attemptSeed = unchecked(seed + attempt * 7919);
            var plan = TrySolve(attemptSeed, settings, c);
            plan.attempt = attempt;

            if (plan.Solved) return plan;
            if (IsBetterFallback(plan, best)) best = plan;
        }
        return best;
    }

    // an attempt that stopped early has few failures but no sites, so any map with sites beats it
    // otherwise fewer broken rules wins
    static bool IsBetterFallback(MapPlan plan, MapPlan best)
    {
        if (best == null) return true;
        if ((plan.sites.Count > 0) != (best.sites.Count > 0)) return plan.sites.Count > 0;
        return plan.failures.Count < best.failures.Count;
    }

    // records why an attempt stopped early and hands the plan back
    static MapPlan Fail(MapPlan plan, string why)
    {
        plan.failures.Add(why);
        return plan;
    }

    static MapPlan TrySolve(int seed, BSPSettings settings, MapConstraints c)
    {
        var layout = BSPGenerator.Generate(seed, settings);
        int n = layout.rooms.Count;
        var plan = new MapPlan { layout = layout, roles = new RoomRole[n] };
        int width = settings.corridorWidth;
        if (n < c.RequiredRooms) return Fail(plan, $"Only {n} rooms, need {c.RequiredRooms}");

        // steps 1 to 3: spawns, mid and sites, for whichever orientation scores best
        var pick = ChooseOrientation(layout, RoomDistances(layout), c, settings.roomPadding);
        if (pick.mid < 0) return Fail(plan, "Not enough distinct rooms for spawns and mid");
        AssignRoles(plan, pick);
        if (pick.sites == null) return Fail(plan, $"No {c.siteCount} rooms work as sites (size, sides, separation)");

        // step 4: resize rooms toward what their roles want
        RepairRooms(plan, pick.o, c, settings);

        // step 5: a left and a right exit out of each spawn
        AddSpawnExits(plan, pick.o, width);

        // step 6: attacker lobbies to their site and mid, a way into mid from each team,
        // then entrances into each site from both sides and from mid
        // one rng for all of it, separate from the BSP's so adding entrances never changes the base layout
        var rng = new System.Random(unchecked(seed ^ 0x5A17E));
        AddAttackerLobbies(plan, pick.o, width, rng);
        AddMidRoutes(plan, pick.o, width, rng);
        AddSiteEntrances(plan, pick.o, width, rng);
        BSPGenerator.Bake(layout);

        // step 7: every room has to be walkable with no dead ends after all the resizing and new corridors,
        // then re-measure
        // spawns and mid are locked so the safety net can't break their exits or the mid gap
        int strays = BSPGenerator.ConnectStrayRooms(layout, width, plan.attackerSpawn, plan.defenderSpawn, plan.mid);
        if (strays > 0) plan.report.Add($"Added {strays} hallways for cut-off or dead-end rooms");
        Validate(plan, RoomDistances(layout), pick.o, c);
        return plan;
    }

    // PLACEMENT

    // one orientation's choice of rooms, before anything is written into the plan
    struct Placement
    {
        public Orientation o;
        public int atk, def, mid;
        public List<int> sites;   // null if no set of sites passed the hard rules
        public float cost;
    }

    // tries spawns at edge middles, mid at the center and sites either side for every orientation
    // and keeps whichever scores best
    static Placement ChooseOrientation(BSPLayout layout, int[,] dist, MapConstraints c, int pad)
    {
        var best = new Placement { mid = -1, cost = float.MaxValue };
        foreach (var o in Orientations)
        {
            var p = new Placement { o = o };
            p.atk = PickSpawn(layout, o, 0f, -1, pad);
            p.def = PickSpawn(layout, o, 1f, p.atk, pad);
            p.mid = PickMid(layout, p.atk, p.def, pad);
            if (p.mid < 0) continue;

            // placement cost: how far spawns and mid would sit from where they should, once grown
            p.cost = SpawnCost(layout, o, p.atk, 0f, pad)
                   + SpawnCost(layout, o, p.def, 1f, pad)
                   + MidOffset(layout, GrownMid(layout, p.mid, pad));
            p.sites = PickSites(layout, dist, p, c.siteCount, pad, out float siteCost);
            p.cost += p.sites != null ? siteCost : NoSitesPenalty;
            if (p.cost < best.cost) best = p;
        }
        return best;
    }

    // writes the chosen rooms into the plan, sites only if there are any
    static void AssignRoles(MapPlan plan, Placement p)
    {
        plan.attackerSpawn = p.atk;
        plan.defenderSpawn = p.def;
        plan.mid = p.mid;
        plan.roles[p.atk] = RoomRole.AttackerSpawn;
        plan.roles[p.def] = RoomRole.DefenderSpawn;
        plan.roles[p.mid] = RoomRole.Mid;
        plan.report.Add(p.o.Name);

        if (p.sites == null) return;
        plan.sites.AddRange(p.sites);
        foreach (var s in p.sites) plan.roles[s] = RoomRole.Site;
    }

    // GEOMETRY

    // 0 at the attacker edge, 1 at the defender edge
    static float Along(BSPLayout l, Orientation o, Vector2 p)
    {
        float t = o.alongX ? p.x / l.width : p.y / l.height;
        return o.attackerLow ? t : 1f - t;
    }

    // -0.5 on the left side, 0 on the line between the spawns, 0.5 on the right side
    static float Across(BSPLayout l, Orientation o, Vector2 p) => o.alongX ? p.y / l.height - 0.5f : p.x / l.width - 0.5f;

    static float Along(BSPLayout l, Orientation o, int room) => Along(l, o, l.rooms[room].center);
    static float Across(BSPLayout l, Orientation o, int room) => Across(l, o, l.rooms[room].center);

    static int Area(RectInt r) => r.width * r.height;

    // RectInt only has a position + size constructor, this builds one from its edges instead
    static RectInt FromMinMax(int xMin, int yMin, int xMax, int yMax) => new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);

    // largest rect a room may grow to: its leaf minus padding, except on sides that lie on the map edge
    // (the map border is already walled, so rooms there can run right up to it)
    static RectInt GrowLimit(BSPLayout l, int room, int pad)
    {
        var b = l.roomLeaves[room].bounds;
        int xMin = b.xMin == 0 ? 0 : b.xMin + pad;
        int yMin = b.yMin == 0 ? 0 : b.yMin + pad;
        int xMax = b.xMax == l.width ? l.width : b.xMax - pad;
        int yMax = b.yMax == l.height ? l.height : b.yMax - pad;
        return FromMinMax(xMin, yMin, xMax, yMax);
    }

    // grows r just enough to cover point, or as close as the limit allows
    static RectInt GrowToward(RectInt r, RectInt limit, Vector2 point)
    {
        int px = Mathf.Clamp(Mathf.FloorToInt(point.x), limit.xMin, limit.xMax - 1);
        int py = Mathf.Clamp(Mathf.FloorToInt(point.y), limit.yMin, limit.yMax - 1);
        return FromMinMax(Mathf.Min(r.xMin, px), Mathf.Min(r.yMin, py), Mathf.Max(r.xMax, px + 1), Mathf.Max(r.yMax, py + 1));
    }

    // pushes every side out one cell at a time until the area is reached or the limit stops it
    static RectInt GrowToArea(RectInt r, RectInt limit, int minArea)
    {
        int xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax;
        while ((xMax - xMin) * (yMax - yMin) < minArea)
        {
            bool grew = false;
            if (xMin > limit.xMin) { xMin--; grew = true; }
            if (xMax < limit.xMax) { xMax++; grew = true; }
            if (yMin > limit.yMin) { yMin--; grew = true; }
            if (yMax < limit.yMax) { yMax++; grew = true; }
            if (!grew) break;
        }
        return FromMinMax(xMin, yMin, xMax, yMax);
    }

    // trims the longer side one cell at a time, alternating ends so the room stays centered,
    // until the area is under maxArea
    // stops early rather than let a side drop under minSide or the area under minArea
    static RectInt ShrinkBelowArea(RectInt r, int maxArea, int minSide, int minArea)
    {
        int xMin = r.xMin, yMin = r.yMin, xMax = r.xMax, yMax = r.yMax;
        bool fromMin = false;
        while ((xMax - xMin) * (yMax - yMin) >= maxArea)
        {
            int w = xMax - xMin, h = yMax - yMin;
            bool trimX = w >= h;
            if ((trimX ? w : h) <= minSide) break;
            if ((trimX ? (w - 1) * h : w * (h - 1)) < minArea) break;

            if (trimX) { if (fromMin) xMin++; else xMax--; }
            else { if (fromMin) yMin++; else yMax--; }
            fromMin = !fromMin;
        }
        return FromMinMax(xMin, yMin, xMax, yMax);
    }

    // ROOM GRAPH

    // walking distance between every pair of room centers, one BFS per room
    static int[,] RoomDistances(BSPLayout layout)
    {
        int n = layout.rooms.Count;
        var dist = new int[n, n];
        for (int i = 0; i < n; i++)
        {
            var field = BSPGenerator.DistanceField(layout, BSPGenerator.RoomCenterCell(layout.rooms[i]));
            for (int j = 0; j < n; j++)
            {
                var cell = BSPGenerator.RoomCenterCell(layout.rooms[j]);
                int d = field[layout.Index(cell.x, cell.y)];
                dist[i, j] = d >= 0 ? d : Unreachable;
            }
        }
        return dist;
    }

    // index with the lowest cost, or -1 if every cost is infinite (callers return infinity to skip an index)
    // ties keep the earlier index, so the result only ever depends on room order
    static int ArgMin(int count, Func<int, float> cost)
    {
        int best = -1;
        float bestCost = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            float value = cost(i);
            if (value < bestCost)
            {
                bestCost = value;
                best = i;
            }
        }
        return best;
    }

    // REPAIR

    // resizes rooms toward what their role wants: spawns to the fixed spawn size on their edge's middle,
    // mid filling its leaf, sites up to the minimum size, then everything else trimmed so mid is the biggest
    // every room stays inside its leaf, so they can't collide, and doors a resize leaves outside get joined
    // back in, so every hallway still connects
    static void RepairRooms(MapPlan plan, Orientation o, MapConstraints c, BSPSettings settings)
    {
        var l = plan.layout;
        int pad = settings.roomPadding, width = settings.corridorWidth;
        int resized = RepairSpawns(plan, o, pad, width);
        resized += Resize(l, plan.mid, GrownMid(l, plan.mid, pad), width);
        resized += RepairSites(plan, pad, width);
        resized += TrimBelowMid(plan, settings.minRoomSize, width);

        // the grid is repainted after entrances are added, so no Bake here
        if (resized > 0) plan.report.Add($"Resized {resized} rooms to fit their roles");
    }

    // 1 if the room changed, so callers can count how many were repaired
    static int Resize(BSPLayout l, int room, RectInt rect, int width)
    {
        if (l.rooms[room].Equals(rect)) return 0;
        l.SetRoom(room, rect);
        BSPGenerator.ReattachDoors(l, room, width);
        return 1;
    }

    // index of the connection joining a and b, or -1 if they aren't joined
    static int LinkIndex(BSPLayout l, int a, int b)
        => l.connections.FindIndex(c => (c.x == a && c.y == b) || (c.x == b && c.y == a));

    // the room at the other end of a connection from room
    static int OtherEnd(BSPLayout l, int link, int room)
        => l.connections[link].x == room ? l.connections[link].y : l.connections[link].x;

    // true if any segment of that connection's hallway runs into the room
    static bool RouteCrosses(BSPLayout l, int link, int room) => l.routes[link].Exists(seg => seg.Overlaps(l.rooms[room]));

    // joins a and b with a hallway unless they already are, the coin flip only drawn when a hallway is added
    static void JoinOnce(BSPLayout l, int a, int b, int width, System.Random rng)
    {
        if (!l.AreConnected(a, b)) BSPGenerator.ConnectPair(l, a, b, rng.NextDouble() < 0.5, width);
    }

    // TEAM ROUTES

    // a neighbor counts as an attacker entrance if it sits closer to the attacker edge than the room
    // mid always counts as a defender entrance, it's their rotation route
    static bool IsAttackerSide(MapPlan plan, Orientation o, int room, int neighbor)
        => neighbor != plan.mid && Along(plan.layout, o, neighbor) < Along(plan.layout, o, room);

    // steps from a team's spawn to every room, never walking through the target room or the other spawn
    // routes into mid also can't cut through a site or cross into the other team's half,
    // and have to leave the spawn through one of its side exits, so mid branches off the left or right route
    // instead of sitting straight ahead of the spawn
    static int[] TeamReach(MapPlan plan, Orientation o, int room, bool attackers)
    {
        var l = plan.layout;
        int spawn = attackers ? plan.attackerSpawn : plan.defenderSpawn;
        var blocked = new List<int> { room, attackers ? plan.defenderSpawn : plan.attackerSpawn };
        if (room != plan.mid) return BSPGenerator.RoomHops(l, spawn, blocked.ToArray());

        blocked.AddRange(plan.sites);
        float midAlong = Along(l, o, room);
        for (int i = 0; i < l.rooms.Count; i++)
            if (attackers ? Along(l, o, i) > midAlong : Along(l, o, i) < midAlong) blocked.Add(i);

        // walk out from each side exit room with the spawn itself blocked, keeping the shortest count
        blocked.Add(spawn);
        var reach = new int[l.rooms.Count];
        for (int i = 0; i < reach.Length; i++) reach[i] = -1;
        reach[spawn] = 0;
        foreach (int exit in SideExitRooms(plan, o, spawn))
        {
            if (blocked.Contains(exit)) continue;
            var hops = BSPGenerator.RoomHops(l, exit, blocked.ToArray());
            for (int i = 0; i < reach.Length; i++)
                if (hops[i] >= 0 && (reach[i] < 0 || hops[i] + 1 < reach[i])) reach[i] = hops[i] + 1;
        }
        return reach;
    }

    // rooms a team passes on its shortest route from its spawn into the room through an entrance on its own side,
    // or -1 if there's no such route
    static int TeamRouteRooms(MapPlan plan, Orientation o, int room, bool attackers)
    {
        var reach = TeamReach(plan, o, room, attackers);
        int best = -1;
        foreach (var n in plan.layout.Neighbors(room))
        {
            if (reach[n] < 0 || IsAttackerSide(plan, o, room, n) != attackers) continue;
            if (best < 0 || reach[n] < best) best = reach[n];
        }
        return best;
    }

    static bool HasTeamRoute(MapPlan plan, Orientation o, int room, bool attackers)
        => TeamRouteRooms(plan, o, room, attackers) >= 0;

    // nearest room on the given side that isn't a spawn or already connected to the room
    // reach (optional) limits it to rooms between minSteps and maxSteps from a team's spawn,
    // e.g. rooms it can already get to
    static int NearestOnSide(MapPlan plan, Orientation o, int room, bool attackerSide, int[] reach,
        int minSteps = 0, int maxSteps = int.MaxValue)
    {
        var rooms = plan.layout.rooms;
        return ArgMin(rooms.Count, i =>
        {
            if (i == room || plan.layout.AreConnected(room, i)) return float.PositiveInfinity;
            if (plan.roles[i] == RoomRole.AttackerSpawn || plan.roles[i] == RoomRole.DefenderSpawn) return float.PositiveInfinity;
            if (IsAttackerSide(plan, o, room, i) != attackerSide) return float.PositiveInfinity;
            if (reach != null && (reach[i] < 0 || reach[i] < minSteps || reach[i] > maxSteps)) return float.PositiveInfinity;
            return (rooms[i].center - rooms[room].center).sqrMagnitude;
        });
    }

    // VALIDATION

    // checks every rule against the final map and fills in failures and the report
    static void Validate(MapPlan plan, int[,] dist, Orientation o, MapConstraints c)
    {
        var l = plan.layout;
        if (!l.AllRoomsReachable)
            plan.failures.Add($"Only {l.reachableRooms}/{l.rooms.Count} rooms reachable");

        // same order as the rules: spawns, mid, the sites as a group, then each site
        CheckSpawn(plan, o, plan.attackerSpawn, 0f, "Attacker");
        CheckSpawn(plan, o, plan.defenderSpawn, 1f, "Defender");
        CheckAttackerLobbies(plan);
        CheckMid(plan, dist, o);
        CheckSiteLayout(plan, dist, o);
        foreach (var s in plan.sites) CheckSite(plan, dist, o, s);
    }
}
