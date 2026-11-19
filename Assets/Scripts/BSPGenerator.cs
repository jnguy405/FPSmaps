using System;
using System.Collections.Generic;
using UnityEngine;

// tunable parameters for a BSP layout
// all sizes are in grid cells
[Serializable]
public class BSPSettings
{
    [Min(8)] public int width = 64;
    [Min(8)] public int height = 64;

    [Tooltip("Smallest a partition may be on either axis.")]
    [Min(4)] public int minLeafSize = 10;

    [Tooltip("How many times the space may be split. 2^depth is the max number of rooms.")]
    [Range(1, 10)] public int maxDepth = 5;

    [Tooltip("Cuts land between this ratio and (1 - ratio) of the partition length.")]
    [Range(0.1f, 0.5f)] public float minSplitRatio = 0.35f;

    [Min(2)] public int minRoomSize = 4;

    [Tooltip("Empty cells kept between a room and its partition edge (space for walls/corridors).")]
    [Min(0)] public int roomPadding = 1;

    [Tooltip("Rooms fill at least this fraction of their partition on each axis.")]
    [Range(0f, 1f)] public float minRoomFill = 0.5f;

    [Tooltip("Corridor width in cells.")]
    [Min(1)] public int corridorWidth = 2;

    // keeps settings consistent with each other so generation can't fail
    public void Validate()
    {
        // a corridor wider than the smallest room would stick out of it
        corridorWidth = Mathf.Clamp(corridorWidth, 1, minRoomSize);

        // every leaf must fit a minimum room plus padding on both sides
        minLeafSize = Mathf.Max(minLeafSize, minRoomSize + roomPadding * 2);

        // the map itself must be at least one leaf
        width = Mathf.Max(width, minLeafSize);
        height = Mathf.Max(height, minLeafSize);
    }
}

public enum CellType : byte { Empty, Room, Corridor }

// one partition in the BSP tree, internal nodes have two children, leaves hold a room
public class BSPNode
{
    public RectInt bounds;
    public int depth;
    public BSPNode left, right;
    public bool hasRoom;
    public RectInt room;

    // nodes are always split into two, so checking one child is enough
    public bool IsLeaf => left == null;
}

public class BSPLayout
{
    public int seed;
    public BSPNode root;
    public readonly List<BSPNode> nodes = new List<BSPNode>();   // every node, depth-first
    public readonly List<BSPNode> leaves = new List<BSPNode>();
    public readonly List<RectInt> rooms = new List<RectInt>();
    public readonly List<RectInt> corridors = new List<RectInt>();   // straight segments, two per L-shaped corridor
    public readonly List<Vector2Int> connections = new List<Vector2Int>();   // room index pairs joined by a corridor
    public readonly List<BSPNode> roomLeaves = new List<BSPNode>();   // leaf each room sits in, same order as rooms

    // where hallways leave each room: the room, the outward direction of the wall it's in,
    // the doorway's bottom-left cell (the opening is corridor width square, just inside that wall),
    // and which connection the hallway belongs to
    public readonly List<(int room, Vector2Int wall, Vector2Int cell, int link)> doors = new List<(int, Vector2Int, Vector2Int, int)>();

    // the segments each connection's hallway was built from, by connection index, so one can be rerouted later
    public readonly List<List<RectInt>> routes = new List<List<RectInt>>();

    // cell grid the 3D build reads from, row-major (index = y * width + x)
    public int width, height;
    public CellType[] cells;

    // rooms reachable by walking from the first room (should equal rooms.Count)
    public int reachableRooms;

    // position in the cells array for grid cell (x, y)
    public int Index(int x, int y) => y * width + x;

    // out-of-bounds reads count as empty so callers can check neighbors without bounds checks
    public CellType GetCell(int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return CellType.Empty;
        return cells[Index(x, y)];
    }

    public bool IsFloor(int x, int y) => GetCell(x, y) != CellType.Empty;

    // every room or corridor cell, row by row from the bottom
    public IEnumerable<Vector2Int> FloorCells()
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (IsFloor(x, y)) yield return new Vector2Int(x, y);
    }

    public bool AllRoomsReachable => reachableRooms == rooms.Count;

    // rooms joined to this one by a corridor, once per corridor
    // connections are stored as pairs, so this returns whichever end isn't the room itself
    public IEnumerable<int> Neighbors(int room)
    {
        foreach (var c in connections)
        {
            if (c.x == room) yield return c.y;
            else if (c.y == room) yield return c.x;
        }
    }

    // replaces a room's rect, keeping its leaf in sync (call BSPGenerator.Bake afterwards)
    public void SetRoom(int room, RectInt rect)
    {
        rooms[room] = rect;
        roomLeaves[room].room = rect;
    }

    public bool AreConnected(int a, int b)
    {
        foreach (var n in Neighbors(a))
            if (n == b) return true;
        return false;
    }

    // fingerprint of the rooms and corridors
    // same seed + settings must give the same hash
    public int Hash
    {
        get
        {
            // FNV-1a offset basis, the standard starting value
            int h = unchecked((int)2166136261);

            // order matters, so the lists must be built in the same order every time
            foreach (var r in rooms) h = HashRect(h, r);
            foreach (var c in corridors) h = HashRect(h, c);
            return h;
        }
    }

    // mixes each field into the hash: xor it in, then multiply by the FNV prime
    static int HashRect(int h, RectInt r)
    {
        unchecked
        {
            h = (h ^ r.x) * 16777619;
            h = (h ^ r.y) * 16777619;
            h = (h ^ r.width) * 16777619;
            h = (h ^ r.height) * 16777619;
            return h;
        }
    }
}

// binary space partitioning: recursively cut the map into partitions, place one room per leaf,
// then join sibling partitions with corridors so every room is connected
// uses System.Random so a seed gives identical results on every platform (incl. WebGL)
public static class BSPGenerator
{
    public static BSPLayout Generate(int seed, BSPSettings settings)
    {
        settings.Validate();

        // one rng for the whole run, every step below must use it in the same order to stay deterministic
        var rng = new System.Random(seed);
        var layout = new BSPLayout { seed = seed, width = settings.width, height = settings.height };

        // step 1: split the whole map into partitions
        layout.root = new BSPNode { bounds = new RectInt(0, 0, settings.width, settings.height) };
        Split(layout.root, rng, settings, layout);

        // step 2: place one room inside each leaf partition
        foreach (var leaf in layout.leaves)
        {
            if (TryPlaceRoom(leaf, rng, settings))
            {
                layout.rooms.Add(leaf.room);
                layout.roomLeaves.Add(leaf);
            }
        }

        // step 3: connect rooms, bake everything into the cell grid, then make sure every room is walkable
        ConnectRooms(layout.root, rng, settings, layout);
        ConnectStrayRooms(layout, settings.corridorWidth);
        return layout;
    }

    static void Split(BSPNode node, System.Random rng, BSPSettings s, BSPLayout layout)
    {
        layout.nodes.Add(node);

        // stop splitting at max depth or when the partition is too small to cut
        if (node.depth >= s.maxDepth || !TryChooseCut(node.bounds, rng, s, out bool vertical, out int cut))
        {
            layout.leaves.Add(node);
            return;
        }

        SplitRect(node.bounds, vertical, cut, out RectInt a, out RectInt c);

        // recurse into both halves, left first so the order is always the same
        node.left = new BSPNode { bounds = a, depth = node.depth + 1 };
        node.right = new BSPNode { bounds = c, depth = node.depth + 1 };
        Split(node.left, rng, s, layout);
        Split(node.right, rng, s, layout);
    }

    // cut is measured from the rect's left (vertical) or bottom (horizontal) edge
    static void SplitRect(RectInt b, bool vertical, int cut, out RectInt a, out RectInt c)
    {
        if (vertical)
        {
            a = new RectInt(b.x, b.y, cut, b.height);                 // left half
            c = new RectInt(b.x + cut, b.y, b.width - cut, b.height); // right half
        }
        else
        {
            a = new RectInt(b.x, b.y, b.width, cut);                  // bottom half
            c = new RectInt(b.x, b.y + cut, b.width, b.height - cut); // top half
        }
    }

    // vertical = cut along X (left/right halves); otherwise cut along Y (bottom/top halves)
    static bool TryChooseCut(RectInt b, System.Random rng, BSPSettings s, out bool vertical, out int cut)
    {
        // an axis can only be cut if both halves would still be at least minLeafSize
        bool canX = b.width >= s.minLeafSize * 2;
        bool canY = b.height >= s.minLeafSize * 2;
        vertical = false;
        cut = 0;
        if (!canX && !canY) return false;

        if (canX && canY)
        {
            // prefer cutting the long side so partitions stay roughly square
            float aspect = (float)b.width / b.height;
            if (aspect > 1.25f) vertical = true;
            else if (aspect < 0.8f) vertical = false;
            else vertical = rng.NextDouble() < 0.5;   // close to square, pick at random
        }
        else
        {
            // only one axis is big enough, so use that one
            vertical = canX;
        }

        // keep the cut away from both edges by minLeafSize and by the split ratio
        int length = vertical ? b.width : b.height;
        int min = Mathf.Max(s.minLeafSize, Mathf.CeilToInt(length * s.minSplitRatio));
        int max = length - min;
        if (max < min) return false;

        // rng.Next's upper bound is exclusive, hence the + 1
        cut = rng.Next(min, max + 1);
        return true;
    }

    static bool TryPlaceRoom(BSPNode leaf, System.Random rng, BSPSettings s)
    {
        // largest room that fits once padding is kept on both sides
        var b = leaf.bounds;
        int maxW = b.width - s.roomPadding * 2;
        int maxH = b.height - s.roomPadding * 2;
        if (maxW < s.minRoomSize || maxH < s.minRoomSize) return false;

        // smallest allowed room: at least minRoomFill of the space, and never below minRoomSize
        int minW = Mathf.Clamp(Mathf.CeilToInt(maxW * s.minRoomFill), s.minRoomSize, maxW);
        int minH = Mathf.Clamp(Mathf.CeilToInt(maxH * s.minRoomFill), s.minRoomSize, maxH);

        // random size first, then a random position where that size still fits inside the padding
        int w = rng.Next(minW, maxW + 1);
        int h = rng.Next(minH, maxH + 1);
        int x = rng.Next(b.x + s.roomPadding, b.xMax - s.roomPadding - w + 1);
        int y = rng.Next(b.y + s.roomPadding, b.yMax - s.roomPadding - h + 1);

        leaf.room = new RectInt(x, y, w, h);
        leaf.hasRoom = true;
        return true;
    }

    // post-order: each subtree is connected internally first, then joined to its sibling with one corridor
    static void ConnectRooms(BSPNode node, System.Random rng, BSPSettings s, BSPLayout layout)
    {
        if (node.IsLeaf) return;
        ConnectRooms(node.left, rng, s, layout);
        ConnectRooms(node.right, rng, s, layout);

        // all rooms on each side of this split
        var a = new List<RectInt>();
        var b = new List<RectInt>();
        CollectRooms(node.left, a);
        CollectRooms(node.right, b);
        if (a.Count == 0 || b.Count == 0) return;

        // join the closest pair of rooms so corridors stay short
        FindClosestPair(a, b, out RectInt bestA, out RectInt bestB);

        // coin flip picks which way the L bends
        ConnectPair(layout, layout.rooms.IndexOf(bestA), layout.rooms.IndexOf(bestB), rng.NextDouble() < 0.5, s.corridorWidth);
    }

    // joins two rooms (by index) with a hallway and records the connection
    // the hallway leaves each room through the wall facing the other room, at a spot clear of that room's other
    // doors, and its route is picked to keep off other hallways and out of other rooms
    // horizontalFirst only breaks ties between equally good routes
    // wallA (optional) forces which wall the hallway leaves room a through, e.g. a spawn's side wall
    // also used by the constraint solver to add entrances after generation, call Bake afterwards
    public static void ConnectPair(BSPLayout layout, int a, int b, bool horizontalFirst, int width, Vector2Int? wallA = null)
    {
        layout.connections.Add(new Vector2Int(a, b));
        layout.routes.Add(null);
        BuildHallway(layout, layout.connections.Count - 1, a, wallA, horizontalFirst, width);
    }

    // rebuilds an existing connection's hallway so it leaves room through the given wall,
    // replacing its old segments and doors (stubs ReattachDoors added for the old doors are left in place)
    public static void Reroute(BSPLayout layout, int link, int room, Vector2Int wall, int width)
    {
        foreach (var seg in layout.routes[link]) layout.corridors.Remove(seg);
        layout.doors.RemoveAll(d => d.link == link);
        BuildHallway(layout, link, room, wall, true, width);
    }

    // routes connection link's hallway from room a to the other end and records its segments and doors
    static void BuildHallway(BSPLayout layout, int link, int a, Vector2Int? forcedWall, bool horizontalFirst, int width)
    {
        var c = layout.connections[link];
        int b = c.x == a ? c.y : c.x;
        var ra = layout.rooms[a];
        var rb = layout.rooms[b];
        var wallA = forcedWall ?? FacingWall(ra, rb.center);
        var wallB = FacingWall(rb, ra.center);
        var doorA = DoorCell(layout, a, wallA, rb.center, width);
        var doorB = DoorCell(layout, b, wallB, ra.center, width);

        var route = BestRoute(layout, a, b, doorA, wallA, doorB, wallB, horizontalFirst, width);
        layout.corridors.AddRange(route);
        layout.routes[link] = route;
        layout.doors.Add((a, wallA, doorA, link));
        layout.doors.Add((b, wallB, doorB, link));
    }

    // after a room is resized, any of its doorways left outside it get a short corridor back in,
    // so the hallways that ended there still reach the room
    public static void ReattachDoors(BSPLayout layout, int room, int width)
    {
        var r = layout.rooms[room];
        foreach (var d in layout.doors)
        {
            if (d.room != room) continue;

            // still at least partly inside, so still connected
            if (r.Overlaps(new RectInt(d.cell.x, d.cell.y, width, width))) continue;

            // L from the old doorway to the nearest spot just inside the room
            var inside = new Vector2Int(Mathf.Clamp(d.cell.x, r.xMin, r.xMax - width), Mathf.Clamp(d.cell.y, r.yMin, r.yMax - width));
            var corner = new Vector2Int(inside.x, d.cell.y);
            layout.corridors.Add(Segment(d.cell, corner, width));
            layout.corridors.Add(Segment(corner, inside, width));
        }
    }

    // HALLWAY ROUTING

    // outward direction of the room wall that faces a point: east (1, 0), west (-1, 0), north (0, 1) or south (0, -1)
    static Vector2Int FacingWall(RectInt r, Vector2 toward)
    {
        // how far past the room's edges the point sits on each axis, 0 while it's within the room's span
        float dx = toward.x > r.xMax ? toward.x - r.xMax : toward.x < r.xMin ? toward.x - r.xMin : 0f;
        float dy = toward.y > r.yMax ? toward.y - r.yMax : toward.y < r.yMin ? toward.y - r.yMin : 0f;

        // a point within the room's span on both axes falls back to the offset from the center
        if (dx == 0f && dy == 0f)
        {
            dx = toward.x - r.center.x;
            dy = toward.y - r.center.y;
        }
        if (Mathf.Abs(dx) >= Mathf.Abs(dy)) return new Vector2Int(dx >= 0f ? 1 : -1, 0);
        return new Vector2Int(0, dy >= 0f ? 1 : -1);
    }

    // bottom-left cell of a w x w doorway just inside the given wall, as close to the target as it can sit
    // while staying clear of the room's other doors on that wall
    static Vector2Int DoorCell(BSPLayout layout, int room, Vector2Int wall, Vector2 toward, int w)
    {
        var r = layout.rooms[room];
        bool alongY = wall.x != 0;   // east/west walls run along y, so the door slides along y

        // the fixed coordinate puts the doorway flush against that wall, inside the room
        int fixedPos = wall.x > 0 ? r.xMax - w : wall.x < 0 ? r.xMin : wall.y > 0 ? r.yMax - w : r.yMin;

        // the range it can slide in, kept a cell off the corners when the wall is long enough
        int lo = alongY ? r.yMin : r.xMin;
        int hi = (alongY ? r.yMax : r.xMax) - w;
        if (hi - lo >= 2) { lo++; hi--; }
        int want = Mathf.Clamp(Mathf.RoundToInt((alongY ? toward.y : toward.x) - w / 2f), lo, hi);

        // the wanted spot first, then step outward both ways until one is clear
        // a wall with no clear spot left shares the wanted one
        int pos = want;
        for (int step = 0; step <= hi - lo; step++)
        {
            if (want + step <= hi && DoorIsClear(layout, room, wall, want + step, w)) { pos = want + step; break; }
            if (want - step >= lo && DoorIsClear(layout, room, wall, want - step, w)) { pos = want - step; break; }
        }
        return alongY ? new Vector2Int(fixedPos, pos) : new Vector2Int(pos, fixedPos);
    }

    // true if a door at pos keeps at least a two-cell gap from the room's other doors on the same wall,
    // so their hallways can't leave side by side
    static bool DoorIsClear(BSPLayout layout, int room, Vector2Int wall, int pos, int w)
    {
        foreach (var d in layout.doors)
        {
            if (d.room != room || d.wall != wall) continue;
            int other = wall.x != 0 ? d.cell.y : d.cell.x;
            if (Mathf.Abs(other - pos) < w + 2) return false;
        }
        return true;
    }

    // best of the candidate routes between two doors:
    // doors facing each other across a gap get a Z shape, trying every position for its middle leg,
    // anything else gets an L shape, preferring the one that leaves room a straight out of its wall
    static List<RectInt> BestRoute(BSPLayout layout, int a, int b, Vector2Int doorA, Vector2Int wallA,
        Vector2Int doorB, Vector2Int wallB, bool horizontalFirst, int w)
    {
        List<RectInt> best = null;
        float bestScore = float.MaxValue;
        void Consider(List<RectInt> route, float bias)
        {
            float score = RouteScore(layout, a, b, route) + bias;
            if (score < bestScore)
            {
                bestScore = score;
                best = route;
            }
        }

        bool sideways = wallA.x != 0 && wallB.x != 0;   // both doors in east/west walls
        bool stacked = wallA.y != 0 && wallB.y != 0;    // both doors in north/south walls
        if (sideways || stacked)
        {
            // out of a, across on the middle leg, into b
            int from = sideways ? doorA.x : doorA.y, to = sideways ? doorB.x : doorB.y;
            int middle = (from + to) / 2;
            for (int m = Mathf.Min(from, to); m <= Mathf.Max(from, to); m++)
            {
                var p = sideways ? new Vector2Int(m, doorA.y) : new Vector2Int(doorA.x, m);
                var q = sideways ? new Vector2Int(m, doorB.y) : new Vector2Int(doorB.x, m);

                // a tiny pull toward the middle so equal scores make tidy, centered bends
                Consider(new List<RectInt> { Segment(doorA, p, w), Segment(p, q, w), Segment(q, doorB, w) },
                    0.001f * Mathf.Abs(m - middle));
            }
        }
        else
        {
            bool outOfA = wallA.x != 0;   // a's door is in an east/west wall, so leaving straight out is horizontal
            foreach (bool h in new[] { outOfA, !outOfA })
            {
                var corner = h ? new Vector2Int(doorB.x, doorA.y) : new Vector2Int(doorA.x, doorB.y);
                float bias = (h == outOfA ? 0f : 0.5f) + (h == horizontalFirst ? 0f : 0.0001f);
                Consider(new List<RectInt> { Segment(doorA, corner, w), Segment(corner, doorB, w) }, bias);
            }
        }
        return best;
    }

    // lower is better: 10 for every other room a segment runs through (that would open an unplanned doorway),
    // 1 for every existing hallway it touches or crosses outside the two rooms it joins
    static float RouteScore(BSPLayout layout, int a, int b, List<RectInt> route)
    {
        var ra = layout.rooms[a];
        var rb = layout.rooms[b];
        float score = 0f;
        foreach (var seg in route)
        {
            for (int i = 0; i < layout.rooms.Count; i++)
                if (i != a && i != b && layout.rooms[i].Overlaps(seg)) score += 10f;

            // one cell of margin, so running right alongside a hallway counts the same as crossing it
            var near = new RectInt(seg.x - 1, seg.y - 1, seg.width + 2, seg.height + 2);
            foreach (var c in layout.corridors)
            {
                if (!near.Overlaps(c)) continue;

                // touching inside either of the two rooms doesn't matter, that's floor anyway
                var shared = Intersect(near, c);
                if (ContainsRect(ra, shared) || ContainsRect(rb, shared)) continue;
                score += 1f;
            }
        }
        return score;
    }

    static RectInt Intersect(RectInt p, RectInt q)
    {
        int xMin = Mathf.Max(p.xMin, q.xMin), yMin = Mathf.Max(p.yMin, q.yMin);
        int xMax = Mathf.Min(p.xMax, q.xMax), yMax = Mathf.Min(p.yMax, q.yMax);
        return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
    }

    static bool ContainsRect(RectInt outer, RectInt inner)
    {
        return inner.xMin >= outer.xMin && inner.yMin >= outer.yMin && inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;
    }

    // room-to-room steps from start along recorded connections, -1 where unreachable
    // rooms in blocked are never walked through (e.g. the other team's spawn), start itself always counts
    public static int[] RoomHops(BSPLayout layout, int start, params int[] blocked)
    {
        var hops = new int[layout.rooms.Count];
        for (int i = 0; i < hops.Length; i++) hops[i] = -1;
        hops[start] = 0;

        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int room = queue.Dequeue();
            foreach (var next in layout.Neighbors(room))
            {
                if (hops[next] >= 0 || Array.IndexOf(blocked, next) >= 0) continue;
                hops[next] = hops[room] + 1;
                queue.Enqueue(next);
            }
        }
        return hops;
    }

    // compares every room in a with every room in b, keeps the pair whose centers are nearest
    static void FindClosestPair(List<RectInt> a, List<RectInt> b, out RectInt bestA, out RectInt bestB)
    {
        bestA = a[0];
        bestB = b[0];
        float bestDist = float.MaxValue;
        foreach (var ra in a)
        {
            foreach (var rb in b)
            {
                // squared distance is enough for comparing and skips the square root
                float d = (ra.center - rb.center).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    bestA = ra;
                    bestB = rb;
                }
            }
        }
    }

    // gathers every room in a subtree, left to right
    static void CollectRooms(BSPNode node, List<RectInt> rooms)
    {
        if (node.IsLeaf)
        {
            if (node.hasRoom) rooms.Add(node.room);
            return;
        }
        CollectRooms(node.left, rooms);
        CollectRooms(node.right, rooms);
    }

    // bottom-left cell of a w x w square centered in the room, so a corridor starting there stays inside it
    static Vector2Int CorridorAnchor(RectInt room, int w)
    {
        return new Vector2Int(room.x + (room.width - w) / 2, room.y + (room.height - w) / 2);
    }

    // the single cell in the middle of a room
    public static Vector2Int RoomCenterCell(RectInt room) => CorridorAnchor(room, 1);

    // axis-aligned strip w cells wide covering both end points
    static RectInt Segment(Vector2Int p, Vector2Int q, int w)
    {
        // p and q share x or y, so one of the two sizes below is just w
        int x = Mathf.Min(p.x, q.x);
        int y = Mathf.Min(p.y, q.y);
        return new RectInt(x, y, Mathf.Abs(p.x - q.x) + w, Mathf.Abs(p.y - q.y) + w);
    }

    // paints rooms and corridors into the cell grid, then re-checks reachability
    // call again after adding corridors so the grid matches
    public static void Bake(BSPLayout layout)
    {
        layout.cells = new CellType[layout.width * layout.height];   // starts all Empty

        // corridors first, then rooms on top, so overlapping cells end up marked as Room
        foreach (var c in layout.corridors) Fill(layout, c, CellType.Corridor);
        foreach (var r in layout.rooms) Fill(layout, r, CellType.Room);
        layout.reachableRooms = CountReachableRooms(layout);
    }

    // safety net: bakes the grid, then gives any room the player can't actually walk to a corridor
    // to the nearest room they can, until every room connects
    // checked on the baked cells rather than the recorded connections, so it also catches links that
    // broke after they were made (e.g. a room resized later)
    // then any room left with a single hallway gets a second one, so no room is a dead end
    // the new corridors don't need to line up with any other entrance, they just have to join the two rooms
    // locked rooms (e.g. spawns and mid) only take a stray link if nothing else is in reach, and never a dead-end one
    // returns how many corridors were added
    public static int ConnectStrayRooms(BSPLayout layout, int width, params int[] locked)
    {
        Bake(layout);
        int added = 0;
        bool IsLocked(int room) => Array.IndexOf(locked, room) >= 0;

        // each pass joins at least one stray room, so rooms.Count passes is always enough
        while (!layout.AllRoomsReachable && added < layout.rooms.Count)
        {
            var dist = DistanceField(layout, RoomCenterCell(layout.rooms[0]));
            bool Reached(int room)
            {
                var c = RoomCenterCell(layout.rooms[room]);
                return dist[layout.Index(c.x, c.y)] >= 0;
            }

            // closest pair of one stray room and one reachable room, by center distance
            int stray = -1, target = -1;
            float best = float.MaxValue;
            for (int s = 0; s < layout.rooms.Count; s++)
            {
                if (Reached(s)) continue;
                for (int r = 0; r < layout.rooms.Count; r++)
                {
                    if (!Reached(r)) continue;

                    // locked rooms sort after every unlocked one
                    float d = (layout.rooms[s].center - layout.rooms[r].center).sqrMagnitude;
                    if (IsLocked(r)) d += 1e7f;
                    if (d < best)
                    {
                        best = d;
                        stray = s;
                        target = r;
                    }
                }
            }
            if (stray < 0) break;

            // a new hallway between the two always joins them, wherever their other entrances are
            ConnectPair(layout, stray, target, true, width);
            Bake(layout);
            added++;
        }

        // dead ends: in room order, so the result stays deterministic
        int before = added;
        for (int room = 0; room < layout.rooms.Count; room++)
        {
            if (IsLocked(room) || CountNeighbors(layout, room) >= 2) continue;

            // nearest unlocked room it isn't joined to yet
            int target = -1;
            float best = float.MaxValue;
            for (int r = 0; r < layout.rooms.Count; r++)
            {
                if (r == room || IsLocked(r) || layout.AreConnected(room, r)) continue;
                float d = (layout.rooms[room].center - layout.rooms[r].center).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    target = r;
                }
            }
            if (target < 0) continue;
            ConnectPair(layout, room, target, true, width);
            added++;
        }
        if (added > before) Bake(layout);
        return added;
    }

    static int CountNeighbors(BSPLayout layout, int room)
    {
        int count = 0;
        foreach (var _ in layout.Neighbors(room)) count++;
        return count;
    }

    static void Fill(BSPLayout layout, RectInt r, CellType type)
    {
        // clamp to the grid so a rect hanging off the edge can't write out of bounds
        int xMin = Mathf.Max(r.xMin, 0), xMax = Mathf.Min(r.xMax, layout.width);
        int yMin = Mathf.Max(r.yMin, 0), yMax = Mathf.Min(r.yMax, layout.height);
        for (int y = yMin; y < yMax; y++)
            for (int x = xMin; x < xMax; x++)
                layout.cells[layout.Index(x, y)] = type;
    }

    // flood fill over floor cells from the first room, then count the rooms it touched
    static int CountReachableRooms(BSPLayout layout)
    {
        if (layout.rooms.Count == 0) return 0;
        var dist = DistanceField(layout, RoomCenterCell(layout.rooms[0]));

        // a room counts as reachable if the fill reached its middle cell
        int count = 0;
        foreach (var r in layout.rooms)
        {
            var c = RoomCenterCell(r);
            if (dist[layout.Index(c.x, c.y)] >= 0) count++;
        }
        return count;
    }

    // breadth-first search over floor cells: walking distance in cells from start, -1 where unreachable
    public static int[] DistanceField(BSPLayout layout, Vector2Int start)
    {
        var dist = new int[layout.cells.Length];
        for (int k = 0; k < dist.Length; k++) dist[k] = -1;
        var queue = new Queue<Vector2Int>();
        dist[layout.Index(start.x, start.y)] = 0;
        queue.Enqueue(start);

        // 4-way movement only, matching how walls are built (no diagonal squeezing)
        var steps = new[] { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var step in steps)
            {
                var next = cell + step;

                // skip walls/out of bounds and cells already queued
                if (!layout.IsFloor(next.x, next.y)) continue;
                int i = layout.Index(next.x, next.y);
                if (dist[i] >= 0) continue;

                // BFS reaches each cell by the shortest route first, so one step further than its parent
                dist[i] = dist[layout.Index(cell.x, cell.y)] + 1;
                queue.Enqueue(next);
            }
        }
        return dist;
    }
}
