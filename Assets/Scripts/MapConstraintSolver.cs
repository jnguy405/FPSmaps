using System;
using System.Collections.Generic;
using UnityEngine;

public enum RoomRole : byte { None, AttackerSpawn, DefenderSpawn, Mid, Site }

// rules a generated map must follow to play like an attack/defense FPS map
// positions are fractions of the map size and distances are walking distances, so rules scale with the map
[Serializable]
public class MapConstraints
{
    public bool enabled = true;

    [Header("Spawns")]
    [Tooltip("Spawn rooms must reach within this fraction of the map length from their edge.")]
    [Range(0f, 0.5f)] public float spawnEdgeMargin = 0.15f;

    [Tooltip("Spawn centers must be within this fraction of the edge length from the edge's middle (keeps them out of corners).")]
    [Range(0f, 0.5f)] public float spawnCentering = 0.2f;

    [Header("Mid")]
    [Tooltip("Mid must touch the map center, or be within this fraction of the map size from it.")]
    [Range(0f, 0.5f)] public float midCentering = 0.1f;

    [Tooltip("How uneven the walk to mid may be: |attacker - defender| / (attacker + defender).")]
    [Range(0f, 1f)] public float midBalance = 0.25f;

    [Header("Sites")]
    [Range(2, 3)] public int siteCount = 2;

    [Tooltip("Smallest floor area (in cells) a room needs to be a site.")]
    [Min(4)] public int minSiteArea = 36;

    [Tooltip("Mid-to-site, attacker-to-site and defender-to-site walks may differ between sites by at most this fraction.")]
    [Range(0f, 1f)] public float siteBalance = 0.25f;

    [Tooltip("Defenders must reach each site within this fraction of the attackers' walking distance.")]
    [Range(0.3f, 1f)] public float defenderAdvantage = 0.85f;

    [Tooltip("Sites must be at least this far apart, as a fraction of the spawn-to-spawn distance.")]
    [Range(0f, 1f)] public float minSiteSeparation = 0.3f;

    [Header("Site Entrances")]
    [Tooltip("Entrances each site must have from the attacker side and from the defender side.")]
    [Range(1, 3)] public int minEntrancesPerSide = 1;

    [Tooltip("Entrances the solver tries to add on each side. Falling short is reported but allowed.")]
    [Range(1, 3)] public int preferredEntrancesPerSide = 2;

    [Tooltip("Give every site a direct corridor from mid, counted as a defender entrance.")]
    public bool midEntrance = true;

    [Header("Search")]
    [Tooltip("Seed variations to try before settling for the closest attempt.")]
    [Range(1, 50)] public int maxAttempts = 20;

    // two spawns, mid, and the sites
    public int RequiredRooms => 3 + siteCount;
}

// a generated layout plus the role each room plays and how well it met the constraints
public class MapPlan
{
    public BSPLayout layout;
    public int attempt;   // 0 means the requested seed worked first try
    public RoomRole[] roles;
    public int attackerSpawn = -1, defenderSpawn = -1, mid = -1;
    public readonly List<int> sites = new List<int>();         // A (left side), B (right side), then C
    public readonly List<string> failures = new List<string>(); // broken rules, empty when solved
    public readonly List<string> report = new List<string>();   // distances and entrances for the inspector

    public bool Solved => failures.Count == 0;

    // short tag for a room, e.g. "ATK", "MID" or "B", or null for an unassigned room
    public string Label(int room)
    {
        if (roles == null || room < 0 || room >= roles.Length) return null;
        switch (roles[room])
        {
            case RoomRole.AttackerSpawn: return "ATK";
            case RoomRole.DefenderSpawn: return "DEF";
            case RoomRole.Mid: return "MID";
            case RoomRole.Site: return ((char)('A' + sites.IndexOf(room))).ToString();
            default: return null;
        }
    }

    // readable name for the HUD, e.g. "Site B" or "Attacker Spawn", or null for an unassigned room
    public string FullName(int room)
    {
        string label = Label(room);
        if (label == null) return null;
        switch (roles[room])
        {
            case RoomRole.AttackerSpawn: return "Attacker Spawn";
            case RoomRole.DefenderSpawn: return "Defender Spawn";
            case RoomRole.Mid: return "Mid";
            default: return "Site " + label;
        }
    }

    // layout hash mixed with the attempt and roles, so a change in either shows up
    public int Hash
    {
        get
        {
            unchecked
            {
                int h = (layout.Hash ^ attempt) * 16777619;
                if (roles != null)
                    foreach (var r in roles) h = (h ^ (int)r) * 16777619;
                return h;
            }
        }
    }
}

// assigns spawns, mid and sites to BSP rooms, adds site entrances, then checks every rule
// deterministic: the same seed, settings and constraints always give the same plan
public static class MapConstraintSolver
{
    // used for unreachable rooms so they never win a "closest" comparison
    const int Unreachable = int.MaxValue / 4;

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

    // tries the seed, then derived seeds, until one meets every constraint
    // falls back to the attempt with the fewest failures so there is always a map to show
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
            if (best == null || plan.failures.Count < best.failures.Count) best = plan;
        }
        return best;
    }

    static MapPlan TrySolve(int seed, BSPSettings settings, MapConstraints c)
    {
        var layout = BSPGenerator.Generate(seed, settings);
        int n = layout.rooms.Count;
        var plan = new MapPlan { layout = layout, roles = new RoomRole[n] };

        if (n < c.RequiredRooms)
        {
            plan.failures.Add($"Only {n} rooms, need {c.RequiredRooms}");
            return plan;
        }

        // steps 1 to 3 for every orientation: spawns at edge middles, mid at the center, sites either side
        // keep whichever orientation scores best
        var dist = RoomDistances(layout);
        int pad = settings.roomPadding;
        Orientation bestO = Orientations[0];
        int bestAtk = -1, bestDef = -1, bestMid = -1;
        List<int> bestSites = null;
        float bestCost = float.MaxValue;
        foreach (var o in Orientations)
        {
            int atk = PickSpawn(layout, o, 0f, -1, pad);
            int def = PickSpawn(layout, o, 1f, atk, pad);
            int mid = PickMid(layout, atk, def, pad);
            if (mid < 0) continue;

            // placement cost: how far spawns and mid would sit from where they should, once grown
            float cost = SpawnOffset(layout, o, GrownSpawn(layout, o, atk, 0f, pad), 0f)
                       + SpawnOffset(layout, o, GrownSpawn(layout, o, def, 1f, pad), 1f)
                       + MidOffset(layout, GrownMid(layout, mid, pad));
            var sites = PickSites(layout, dist, o, atk, def, mid, c, pad, out float siteCost);

            // an orientation that finds sites always beats one that doesn't
            cost += sites != null ? siteCost : 1000f;
            if (cost < bestCost)
            {
                bestCost = cost;
                bestO = o;
                bestAtk = atk;
                bestDef = def;
                bestMid = mid;
                bestSites = sites;
            }
        }

        if (bestMid < 0)
        {
            plan.failures.Add("Not enough distinct rooms for spawns and mid");
            return plan;
        }

        plan.attackerSpawn = bestAtk;
        plan.defenderSpawn = bestDef;
        plan.mid = bestMid;
        plan.roles[bestAtk] = RoomRole.AttackerSpawn;
        plan.roles[bestDef] = RoomRole.DefenderSpawn;
        plan.roles[bestMid] = RoomRole.Mid;
        plan.report.Add(bestO.Name);

        if (bestSites == null)
        {
            plan.failures.Add($"No {c.siteCount} rooms work as sites (size, sides, defender advantage, separation)");
            return plan;
        }
        plan.sites.AddRange(bestSites);
        foreach (var s in bestSites) plan.roles[s] = RoomRole.Site;

        // step 4: grow rooms toward where their roles want them
        RepairRooms(plan, bestO, c, pad);

        // step 5: corridors so each site has entrances from both sides and from mid, then re-measure
        AddSiteEntrances(plan, bestO, c, settings.corridorWidth, seed);
        Validate(plan, RoomDistances(layout), bestO, c);
        return plan;
    }

    // GEOMETRY

    // 0 at the attacker edge, 1 at the defender edge
    static float Along(BSPLayout l, Orientation o, Vector2 p)
    {
        float t = o.alongX ? p.x / l.width : p.y / l.height;
        return o.attackerLow ? t : 1f - t;
    }

    // -0.5 on the left side, 0 on the line between the spawns, 0.5 on the right side
    static float Across(BSPLayout l, Orientation o, Vector2 p)
    {
        return o.alongX ? p.y / l.height - 0.5f : p.x / l.width - 0.5f;
    }

    static float Along(BSPLayout l, Orientation o, int room) => Along(l, o, l.rooms[room].center);
    static float Across(BSPLayout l, Orientation o, int room) => Across(l, o, l.rooms[room].center);

    // how far the rect's nearest side is from the edge at along = edge (0 or 1), as a fraction of the map
    static float EdgeGap(BSPLayout l, Orientation o, RectInt r, float edge)
    {
        float a = Along(l, o, new Vector2(r.xMin, r.yMin));
        float b = Along(l, o, new Vector2(r.xMax, r.yMax));
        return edge < 0.5f ? Mathf.Min(a, b) : 1f - Mathf.Max(a, b);
    }

    // spawns want to touch their edge and sit in its middle
    static float SpawnOffset(BSPLayout l, Orientation o, RectInt r, float edge)
    {
        return EdgeGap(l, o, r, edge) + Mathf.Abs(Across(l, o, r.center));
    }

    static Vector2 MapCenter(BSPLayout l) => new Vector2(l.width / 2f, l.height / 2f);

    // point in the middle of the map edge at along = edge
    static Vector2 EdgeMiddle(BSPLayout l, Orientation o, float edge)
    {
        // the attacker edge is the low side when attackerLow, the defender edge is the other one
        bool low = (edge < 0.5f) == o.attackerLow;
        return o.alongX
            ? new Vector2(low ? 0f : l.width, l.height / 2f)
            : new Vector2(l.width / 2f, low ? 0f : l.height);
    }

    // cells from the map center to the rect's nearest point, 0 if the rect covers the center
    static float CenterGap(BSPLayout l, RectInt r)
    {
        var center = MapCenter(l);
        float dx = Mathf.Max(r.xMin - center.x, 0f, center.x - r.xMax);
        float dy = Mathf.Max(r.yMin - center.y, 0f, center.y - r.yMax);
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // center gap as a fraction of the map size, so it adds up with the other placement costs
    static float MidOffset(BSPLayout l, RectInt r) => CenterGap(l, r) / Mathf.Min(l.width, l.height);

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

    // RectInt only has a position + size constructor, this builds one from its edges instead
    static RectInt FromMinMax(int xMin, int yMin, int xMax, int yMax)
    {
        return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
    }

    // grows r just enough to cover point, or as close as the limit allows
    static RectInt GrowToward(RectInt r, RectInt limit, Vector2 point)
    {
        int px = Mathf.Clamp(Mathf.FloorToInt(point.x), limit.xMin, limit.xMax - 1);
        int py = Mathf.Clamp(Mathf.FloorToInt(point.y), limit.yMin, limit.yMax - 1);
        return FromMinMax(Mathf.Min(r.xMin, px), Mathf.Min(r.yMin, py), Mathf.Max(r.xMax, px + 1), Mathf.Max(r.yMax, py + 1));
    }

    // where a spawn or mid room would end up after repair, used to score rooms before choosing them
    static RectInt GrownSpawn(BSPLayout l, Orientation o, int room, float edge, int pad)
        => GrowToward(l.rooms[room], GrowLimit(l, room, pad), EdgeMiddle(l, o, edge));

    static RectInt GrownMid(BSPLayout l, int room, int pad)
        => GrowToward(l.rooms[room], GrowLimit(l, room, pad), MapCenter(l));

    // ROLE PICKING

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

    // room that can best reach the middle of the edge at along = edge, skipping the other spawn
    static int PickSpawn(BSPLayout l, Orientation o, float edge, int skip, int pad)
    {
        int best = -1;
        float bestCost = float.MaxValue;
        for (int i = 0; i < l.rooms.Count; i++)
        {
            if (i == skip) continue;
            float cost = SpawnOffset(l, o, GrownSpawn(l, o, i, edge, pad), edge);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = i;
            }
        }
        return best;
    }

    // room that can best cover the map center once grown, skipping both spawns
    static int PickMid(BSPLayout l, int atk, int def, int pad)
    {
        int best = -1;
        float bestGap = float.MaxValue;
        var center = MapCenter(l);
        for (int i = 0; i < l.rooms.Count; i++)
        {
            if (i == atk || i == def) continue;

            // ties (several rooms able to reach the center) go to the room whose center is closest
            float gap = CenterGap(l, GrownMid(l, i, pad)) + 0.001f * (l.rooms[i].center - center).magnitude;
            if (gap < bestGap)
            {
                bestGap = gap;
                best = i;
            }
        }
        return best;
    }

    // A from the left side, B from the right side, C (if any) from what's left
    // returns the set with the most even distances, or null if no set passes the hard rules
    static List<int> PickSites(BSPLayout l, int[,] dist, Orientation o, int atk, int def, int mid,
        MapConstraints c, int pad, out float bestCost)
    {
        bestCost = float.MaxValue;
        float spawnDist = dist[atk, def];
        float minSep = c.minSiteSeparation * spawnDist;

        // candidates: room to grow to site size, and defenders get there first by the required margin
        var left = new List<int>();
        var right = new List<int>();
        var all = new List<int>();
        for (int i = 0; i < l.rooms.Count; i++)
        {
            if (i == atk || i == def || i == mid) continue;
            var limit = GrowLimit(l, i, pad);
            if (limit.width * limit.height < c.minSiteArea) continue;
            if (dist[def, i] > dist[atk, i] * c.defenderAdvantage) continue;

            all.Add(i);
            if (Across(l, o, i) < 0f) left.Add(i);
            else if (Across(l, o, i) > 0f) right.Add(i);
        }

        List<int> best = null;
        foreach (var a in left)
        {
            foreach (var b in right)
            {
                if (dist[a, b] < minSep) continue;

                // two sites: just A and B
                if (c.siteCount == 2)
                {
                    TryKeep(new List<int> { a, b }, dist, l, o, atk, def, mid, ref best, ref bestCost);
                    continue;
                }

                // three sites: add a C that's far enough from both
                foreach (var s in all)
                {
                    if (s == a || s == b || dist[a, s] < minSep || dist[b, s] < minSep) continue;
                    TryKeep(new List<int> { a, b, s }, dist, l, o, atk, def, mid, ref best, ref bestCost);
                }
            }
        }
        return best;
    }

    static void TryKeep(List<int> sites, int[,] dist, BSPLayout l, Orientation o, int atk, int def, int mid,
        ref List<int> best, ref float bestCost)
    {
        // main cost: the least even of the three distance comparisons
        float cost = SiteImbalance(sites, dist, atk, def, mid);

        // tie-breakers: A and B pushed out to their sides, C kept near the center line
        cost -= 0.1f * (Mathf.Abs(Across(l, o, sites[0])) + Mathf.Abs(Across(l, o, sites[1])));
        if (sites.Count > 2) cost += 0.1f * Mathf.Abs(Across(l, o, sites[2]));

        if (cost < bestCost)
        {
            bestCost = cost;
            best = sites;
        }
    }

    // worst spread between sites over mid-to-site, attacker-to-site and defender-to-site walks
    // 0 when every site is exactly as far as the others on all three
    static float SiteImbalance(List<int> sites, int[,] dist, int atk, int def, int mid)
    {
        float worst = 0f;
        foreach (int from in new[] { mid, atk, def })
        {
            int min = Unreachable, max = 0;
            foreach (var s in sites)
            {
                min = Mathf.Min(min, dist[from, s]);
                max = Mathf.Max(max, dist[from, s]);
            }
            worst = Mathf.Max(worst, (max - min) / (float)Mathf.Max(1, max));
        }
        return worst;
    }

    // REPAIR

    // grows rooms toward where their role wants them: spawns to their edge's middle, mid over the center,
    // sites up to the minimum size
    // rooms only grow, so every corridor still connects, and stay inside their leaf, so they can't collide
    static void RepairRooms(MapPlan plan, Orientation o, MapConstraints c, int pad)
    {
        var l = plan.layout;
        int grown = 0;
        grown += Resize(l, plan.attackerSpawn, GrownSpawn(l, o, plan.attackerSpawn, 0f, pad));
        grown += Resize(l, plan.defenderSpawn, GrownSpawn(l, o, plan.defenderSpawn, 1f, pad));
        grown += Resize(l, plan.mid, GrownMid(l, plan.mid, pad));
        foreach (var s in plan.sites)
            grown += Resize(l, s, GrowToArea(l.rooms[s], GrowLimit(l, s, pad), c.minSiteArea));

        // the grid is repainted after entrances are added, so no Bake here
        if (grown > 0) plan.report.Add($"Grew {grown} rooms to fit their roles");
    }

    // 1 if the room changed, so callers can count how many were repaired
    static int Resize(BSPLayout l, int room, RectInt rect)
    {
        if (l.rooms[room].Equals(rect)) return 0;
        l.SetRoom(room, rect);
        return 1;
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

    // ENTRANCES

    // a neighbor counts as an attacker entrance if it sits closer to the attacker edge than the site
    // mid always counts as a defender entrance, it's their rotation route
    static bool IsAttackerSide(MapPlan plan, Orientation o, int site, int neighbor)
    {
        if (neighbor == plan.mid) return false;
        return Along(plan.layout, o, neighbor) < Along(plan.layout, o, site);
    }

    static void CountEntrances(MapPlan plan, Orientation o, int site, out int attacker, out int defender, out bool fromMid)
    {
        attacker = 0;
        defender = 0;
        fromMid = false;
        foreach (var conn in plan.layout.connections)
        {
            // connections are stored as pairs, find the room on the other end
            int other;
            if (conn.x == site) other = conn.y;
            else if (conn.y == site) other = conn.x;
            else continue;

            if (other == plan.mid) fromMid = true;
            if (IsAttackerSide(plan, o, site, other)) attacker++;
            else defender++;
        }
    }

    // links each site to mid, then to its nearest rooms on each side until it has the preferred count
    static void AddSiteEntrances(MapPlan plan, Orientation o, MapConstraints c, int corridorWidth, int seed)
    {
        var layout = plan.layout;

        // separate rng from the BSP's so adding entrances never changes the base layout
        var rng = new System.Random(unchecked(seed ^ 0x5A17E));
        foreach (var site in plan.sites)
        {
            if (c.midEntrance && !layout.AreConnected(site, plan.mid))
                BSPGenerator.ConnectPair(layout, site, plan.mid, rng.NextDouble() < 0.5, corridorWidth);

            // attacker side first, then defender side, each until full or out of rooms
            foreach (bool attackerSide in new[] { true, false })
            {
                while (true)
                {
                    CountEntrances(plan, o, site, out int atkCount, out int defCount, out _);
                    if ((attackerSide ? atkCount : defCount) >= c.preferredEntrancesPerSide) break;

                    int target = NearestOnSide(plan, o, site, attackerSide);
                    if (target < 0) break;
                    BSPGenerator.ConnectPair(layout, site, target, rng.NextDouble() < 0.5, corridorWidth);
                }
            }
        }

        // repaint the grid so the new corridors are walkable
        BSPGenerator.Bake(layout);
    }

    // nearest room on the given side that isn't a spawn or already connected to the site
    static int NearestOnSide(MapPlan plan, Orientation o, int site, bool attackerSide)
    {
        var rooms = plan.layout.rooms;
        int best = -1;
        float bestDist = float.MaxValue;
        for (int i = 0; i < rooms.Count; i++)
        {
            if (i == site || plan.layout.AreConnected(site, i)) continue;
            if (plan.roles[i] == RoomRole.AttackerSpawn || plan.roles[i] == RoomRole.DefenderSpawn) continue;
            if (IsAttackerSide(plan, o, site, i) != attackerSide) continue;

            float d = (rooms[i].center - rooms[site].center).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    // VALIDATION

    // checks every rule against the final map and fills in failures and the report
    static void Validate(MapPlan plan, int[,] dist, Orientation o, MapConstraints c)
    {
        var l = plan.layout;
        int atk = plan.attackerSpawn, def = plan.defenderSpawn, mid = plan.mid;

        if (!l.AllRoomsReachable)
            plan.failures.Add($"Only {l.reachableRooms}/{l.rooms.Count} rooms reachable");

        // rule 4: spawns on their edge, in the middle of it
        CheckSpawn(plan, o, atk, 0f, "Attacker", c);
        CheckSpawn(plan, o, def, 1f, "Defender", c);

        // rule 1: mid in the middle of the map, and fair to walk to
        float gap = MidOffset(l, l.rooms[mid]);
        int mA = dist[atk, mid], mD = dist[def, mid];
        float balance = Mathf.Abs(mA - mD) / (float)Mathf.Max(1, mA + mD);
        plan.report.Add($"Mid: {gap:0.00} from center, attackers {mA}, defenders {mD}");
        if (gap > c.midCentering)
            plan.failures.Add($"Mid is {gap:0.00} from the map center, max {c.midCentering:0.00}");
        if (balance > c.midBalance)
            plan.failures.Add($"Mid walk is uneven ({balance:0.00}, max {c.midBalance:0.00})");

        // rule 2: A and B on opposite sides, with even distances
        if (Across(l, o, plan.sites[0]) >= 0f || Across(l, o, plan.sites[1]) <= 0f)
            plan.failures.Add("Sites A and B are not on opposite sides");
        float imbalance = SiteImbalance(plan.sites, dist, atk, def, mid);
        plan.report.Add($"Site distance spread: {imbalance:0.00} (max {c.siteBalance:0.00})");
        if (imbalance > c.siteBalance)
            plan.failures.Add($"Site distances are uneven ({imbalance:0.00}, max {c.siteBalance:0.00})");

        // per site: size, defender advantage, rule 3 entrances
        foreach (var s in plan.sites)
        {
            string name = plan.Label(s);
            var r = l.rooms[s];
            int dA = dist[atk, s], dD = dist[def, s], dM = dist[mid, s];
            CountEntrances(plan, o, s, out int inAtk, out int inDef, out bool fromMid);
            plan.report.Add($"Site {name}: mid {dM}, attackers {dA}, defenders {dD} | entrances {inAtk} atk, {inDef} def{(fromMid ? " (1 from mid)" : "")}");

            if (r.width * r.height < c.minSiteArea)
                plan.failures.Add($"Site {name} is {r.width * r.height} cells, need {c.minSiteArea}");
            if (dD > dA * c.defenderAdvantage)
                plan.failures.Add($"Site {name}: defenders ({dD}) not ahead of attackers ({dA})");
            if (inAtk < c.minEntrancesPerSide)
                plan.failures.Add($"Site {name} has {inAtk} attacker entrances, need {c.minEntrancesPerSide}");
            if (inDef < c.minEntrancesPerSide)
                plan.failures.Add($"Site {name} has {inDef} defender entrances, need {c.minEntrancesPerSide}");
            if (c.midEntrance && !fromMid)
                plan.failures.Add($"Site {name} has no entrance from mid");

            // preferred counts are a guideline, so only noted
            if (inAtk < c.preferredEntrancesPerSide || inDef < c.preferredEntrancesPerSide)
                plan.report.Add($"Site {name} is below the preferred {c.preferredEntrancesPerSide} entrances per side");
        }

        // sites far enough apart to play as separate sites
        float minSep = c.minSiteSeparation * dist[atk, def];
        for (int p = 0; p < plan.sites.Count; p++)
        {
            for (int q = p + 1; q < plan.sites.Count; q++)
            {
                int d = dist[plan.sites[p], plan.sites[q]];
                if (d < minSep)
                    plan.failures.Add($"Sites {plan.Label(plan.sites[p])} and {plan.Label(plan.sites[q])} only {d} cells apart");
            }
        }
    }

    static void CheckSpawn(MapPlan plan, Orientation o, int room, float edge, string team, MapConstraints c)
    {
        float edgeGap = EdgeGap(plan.layout, o, plan.layout.rooms[room], edge);
        float offCenter = Mathf.Abs(Across(plan.layout, o, room));
        plan.report.Add($"{team} spawn: {edgeGap:0.00} from edge, {offCenter:0.00} off center");
        if (edgeGap > c.spawnEdgeMargin)
            plan.failures.Add($"{team} spawn is {edgeGap:0.00} from its edge, max {c.spawnEdgeMargin:0.00}");
        if (offCenter > c.spawnCentering)
            plan.failures.Add($"{team} spawn is {offCenter:0.00} off the edge's middle, max {c.spawnCentering:0.00}");
    }
}
