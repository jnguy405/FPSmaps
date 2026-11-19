using System;
using System.Collections.Generic;
using UnityEngine;

// spawns: a fixed-size room on the middle of its edge (not a corner), with every hallway leaving through
// its left or right wall (never straight out the front), at least one each side
// the attacker spawn's left and right exits each lead into a lobby room with two ways on:
// one to the site on that side and one hallway straight to mid that doesn't run through a site
// all fixed on purpose, so none of these are settings
public static class SpawnRules
{
    public const int Width = 12;             // cells along the spawn's edge
    public const int Depth = 6;              // cells into the map
    public const float EdgeMargin = 0.15f;   // max gap to its edge, as a fraction of the map length
    public const float Centering = 0.2f;     // max offset from the edge's middle, as a fraction of the edge
    public const int ExitsPerSide = 1;
}

// spawn picking, repair, exits and checks
public static partial class MapConstraintSolver
{
    // added to a spawn whose leaf can't hold the fixed spawn size
    const float SpawnSizePenalty = 1f;

    // SPAWN PLACEMENT

    // how far the rect's nearest side is from the edge at along = edge (0 or 1), as a fraction of the map
    static float EdgeGap(BSPLayout l, Orientation o, RectInt r, float edge)
    {
        float a = Along(l, o, new Vector2(r.xMin, r.yMin));
        float b = Along(l, o, new Vector2(r.xMax, r.yMax));
        return edge < 0.5f ? Mathf.Min(a, b) : 1f - Mathf.Max(a, b);
    }

    // spawns want to touch their edge and sit in its middle
    static float SpawnOffset(BSPLayout l, Orientation o, RectInt r, float edge) => EdgeGap(l, o, r, edge) + Mathf.Abs(Across(l, o, r.center));

    // point in the middle of the map edge at along = edge
    static Vector2 EdgeMiddle(BSPLayout l, Orientation o, float edge)
    {
        // the attacker edge is the low side when attackerLow, the defender edge is the other one
        bool low = (edge < 0.5f) == o.attackerLow;
        return o.alongX
            ? new Vector2(low ? 0f : l.width, l.height / 2f)
            : new Vector2(l.width / 2f, low ? 0f : l.height);
    }

    // spawn size laid out for the orientation: the width runs along the spawn's edge
    static Vector2Int SpawnSize(Orientation o)
        => o.alongX ? new Vector2Int(SpawnRules.Depth, SpawnRules.Width) : new Vector2Int(SpawnRules.Width, SpawnRules.Depth);

    static bool IsSpawnSize(Orientation o, RectInt r) => r.size == SpawnSize(o);

    // where a spawn would end up after repair, used to score rooms before choosing them
    // the fixed spawn size, pushed against its edge and as close to its middle as the leaf allows
    // (hallway doors left outside are joined back in by BSPGenerator.ReattachDoors when it's resized)
    // if the fixed size can't fit, the spawn just grows toward the edge middle instead
    static RectInt GrownSpawn(BSPLayout l, Orientation o, int room, float edge, int pad)
    {
        var limit = GrowLimit(l, room, pad);
        var size = SpawnSize(o);
        var target = EdgeMiddle(l, o, edge);
        if (limit.width < size.x || limit.height < size.y) return GrowToward(l.rooms[room], limit, target);

        // centered on the edge middle, then slid until it sits inside the leaf
        int x = Mathf.Clamp(Mathf.RoundToInt(target.x - size.x / 2f), limit.xMin, limit.xMax - size.x);
        int y = Mathf.Clamp(Mathf.RoundToInt(target.y - size.y / 2f), limit.yMin, limit.yMax - size.y);
        return new RectInt(x, y, size.x, size.y);
    }

    // how well a room works as a spawn once repaired: placement on the edge, plus a penalty
    // if its leaf can't hold the fixed spawn size
    static float SpawnCost(BSPLayout l, Orientation o, int room, float edge, int pad)
    {
        var rect = GrownSpawn(l, o, room, edge, pad);
        return SpawnOffset(l, o, rect, edge) + (IsSpawnSize(o, rect) ? 0f : SpawnSizePenalty);
    }

    // room that can best become a spawn at the middle of the edge at along = edge, skipping the other spawn
    static int PickSpawn(BSPLayout l, Orientation o, float edge, int skip, int pad)
        => ArgMin(l.rooms.Count, i => i == skip ? float.PositiveInfinity : SpawnCost(l, o, i, edge, pad));

    // both spawns to the fixed size on their edge's middle, with every hallway moved onto a side wall,
    // returns how many spawns changed
    static int RepairSpawns(MapPlan plan, Orientation o, int pad, int width)
        => RepairSpawn(plan, o, plan.attackerSpawn, 0f, pad, width) + RepairSpawn(plan, o, plan.defenderSpawn, 1f, pad, width);

    static int RepairSpawn(MapPlan plan, Orientation o, int spawn, float edge, int pad, int width)
    {
        var l = plan.layout;
        var rect = GrownSpawn(l, o, spawn, edge, pad);
        bool changed = !l.rooms[spawn].Equals(rect);
        l.SetRoom(spawn, rect);

        // hallways out the front or back wall are rebuilt out the side facing their other room
        // (straight ahead goes left), collected first since rerouting edits the door list
        var front = new List<int>();
        foreach (var d in l.doors)
            if (d.room == spawn && !IsSideWall(o, d.wall)) front.Add(d.link);
        foreach (int link in front)
            BSPGenerator.Reroute(l, link, spawn, SideWall(o, SideOf(l, o, spawn, OtherEnd(l, link, spawn)) > 0 ? 1 : -1), width);

        // side doors a resize left outside the spawn get joined back in
        BSPGenerator.ReattachDoors(l, spawn, width);
        return changed || front.Count > 0 ? 1 : 0;
    }

    // SPAWN EXITS

    // -1 if room "to" sits to the left of room "from" across the map, +1 to the right, 0 level with it
    static int SideOf(BSPLayout l, Orientation o, int from, int to) => Math.Sign(Across(l, o, to) - Across(l, o, from));

    // outward direction of a spawn's left (-1) or right (+1) wall, the walls running from its edge into the map
    static Vector2Int SideWall(Orientation o, int side) => o.alongX ? new Vector2Int(0, side) : new Vector2Int(side, 0);

    static bool IsSideWall(Orientation o, Vector2Int wall) => wall == SideWall(o, -1) || wall == SideWall(o, 1);

    // exits counted by the wall each spawn door is in, so a hallway out the front never counts as a side exit
    static void CountSpawnExits(MapPlan plan, Orientation o, int spawn, out int left, out int right, out int front)
    {
        left = 0;
        right = 0;
        front = 0;
        foreach (var d in plan.layout.doors)
        {
            if (d.room != spawn) continue;
            if (d.wall == SideWall(o, -1)) left++;
            else if (d.wall == SideWall(o, 1)) right++;
            else front++;
        }
    }

    // rooms a spawn's side exits lead straight to, the only ways a team may leave its spawn toward mid
    // side picks the left (-1) or right (+1) exits only, 0 takes both
    static List<int> SideExitRooms(MapPlan plan, Orientation o, int spawn, int side = 0)
    {
        var rooms = new List<int>();
        foreach (var d in plan.layout.doors)
            if (d.room == spawn && (side == 0 ? IsSideWall(o, d.wall) : d.wall == SideWall(o, side)))
                rooms.Add(OtherEnd(plan.layout, d.link, spawn));
        return rooms;
    }

    // gives both spawns enough exits out each side wall
    // each new exit goes to the nearest plain room on that side that's still far enough from mid to keep the mid gap
    static void AddSpawnExits(MapPlan plan, Orientation o, int corridorWidth)
    {
        var l = plan.layout;
        var fromMid = BSPGenerator.RoomHops(l, plan.mid);
        foreach (int spawn in new[] { plan.attackerSpawn, plan.defenderSpawn })
        {
            int gap = MidGap(spawn == plan.attackerSpawn);
            foreach (int side in new[] { -1, 1 })
            {
                while (true)
                {
                    CountSpawnExits(plan, o, spawn, out int left, out int right, out _);
                    if ((side < 0 ? left : right) >= SpawnRules.ExitsPerSide) break;

                    int target = ArgMin(l.rooms.Count, i =>
                    {
                        if (plan.roles[i] != RoomRole.None || l.AreConnected(spawn, i)) return float.PositiveInfinity;
                        if (SideOf(l, o, spawn, i) != side) return float.PositiveInfinity;

                        // spawn -> target -> ... -> mid, so the rooms in between are the target's steps to mid
                        if (fromMid[i] < gap) return float.PositiveInfinity;
                        return (l.rooms[i].center - l.rooms[spawn].center).sqrMagnitude;
                    });
                    if (target < 0) break;

                    // forced out the side wall, horizontal first when spawns sit on the south/north edges
                    BSPGenerator.ConnectPair(l, spawn, target, !o.alongX, corridorWidth, SideWall(o, side));
                }
            }
        }
    }

    // ATTACKER LOBBIES

    // the plain room out the attacker spawn's exit on that side nearest the spawn, or -1 if there isn't one
    static int FindLobby(MapPlan plan, Orientation o, int side)
    {
        var rooms = plan.layout.rooms;
        int spawn = plan.attackerSpawn, best = -1;
        float bestDist = float.MaxValue;
        foreach (int other in SideExitRooms(plan, o, spawn, side))
        {
            float dist = (rooms[other].center - rooms[spawn].center).sqrMagnitude;
            if (plan.roles[other] != RoomRole.None || dist >= bestDist) continue;
            bestDist = dist;
            best = other;
        }
        return best;
    }

    // site the lobby on that side leads to: A on the left, B on the right
    static int LobbySite(MapPlan plan, int side) => side < 0 ? plan.sites[0] : plan.sites[1];

    // picks the left and right lobbies, then gives each one a hallway to its side's site and one to mid
    static void AddAttackerLobbies(MapPlan plan, Orientation o, int corridorWidth, System.Random rng)
    {
        for (int s = 0; s < 2; s++)
        {
            int side = s == 0 ? -1 : 1;
            int lobby = plan.attackerLobbies[s] = FindLobby(plan, o, side);
            if (lobby < 0) continue;
            JoinOnce(plan.layout, lobby, LobbySite(plan, side), corridorWidth, rng);
            JoinOnce(plan.layout, lobby, plan.mid, corridorWidth, rng);
        }
    }

    // each lobby exists, reaches its site and mid directly, and its hallway to mid never runs through a site
    static void CheckAttackerLobbies(MapPlan plan)
    {
        var l = plan.layout;
        var names = new string[2];
        for (int s = 0; s < 2; s++)
        {
            int side = s == 0 ? -1 : 1;
            string sideName = side < 0 ? "left" : "right";
            int lobby = plan.attackerLobbies[s];
            if (lobby < 0)
            {
                names[s] = "none";
                plan.failures.Add($"Attacker spawn's {sideName} exit doesn't lead to a lobby room");
                continue;
            }

            names[s] = $"room {lobby}";
            if (!l.AreConnected(lobby, LobbySite(plan, side)))
                plan.failures.Add($"Attacker {sideName} lobby has no hallway to Site {plan.Label(LobbySite(plan, side))}");

            int link = LinkIndex(l, lobby, plan.mid);
            if (link < 0)
                plan.failures.Add($"Attacker {sideName} lobby has no hallway to mid");
            else
                foreach (var site in plan.sites)
                    if (RouteCrosses(l, link, site))
                        plan.failures.Add($"Attacker {sideName} lobby's hallway to mid runs through Site {plan.Label(site)}");
        }
        plan.report.Add($"Attacker lobbies: left {names[0]}, right {names[1]}");
    }

    // SPAWN CHECKS

    // on its edge, in the middle of it, the fixed size, and a left and right exit with none out the front
    static void CheckSpawn(MapPlan plan, Orientation o, int room, float edge, string team)
    {
        var r = plan.layout.rooms[room];
        var size = SpawnSize(o);
        float edgeGap = EdgeGap(plan.layout, o, r, edge);
        float offCenter = Mathf.Abs(Across(plan.layout, o, room));
        CountSpawnExits(plan, o, room, out int left, out int right, out int front);
        plan.report.Add($"{team} spawn: {r.width}x{r.height}, {edgeGap:0.00} from edge, {offCenter:0.00} off center, exits {left} left / {right} right");

        if (edgeGap > SpawnRules.EdgeMargin)
            plan.failures.Add($"{team} spawn is {edgeGap:0.00} from its edge, max {SpawnRules.EdgeMargin:0.00}");
        if (offCenter > SpawnRules.Centering)
            plan.failures.Add($"{team} spawn is {offCenter:0.00} off the edge's middle, max {SpawnRules.Centering:0.00}");
        if (!IsSpawnSize(o, r))
            plan.failures.Add($"{team} spawn is {r.width}x{r.height}, should be {size.x}x{size.y}");
        if (left < SpawnRules.ExitsPerSide)
            plan.failures.Add($"{team} spawn has no exit on its left side");
        if (right < SpawnRules.ExitsPerSide)
            plan.failures.Add($"{team} spawn has no exit on its right side");
        if (front > 0)
            plan.failures.Add($"{team} spawn has {front} hallways out its front or back wall");
    }
}
