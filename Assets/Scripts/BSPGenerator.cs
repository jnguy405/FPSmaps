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

    // cell grid the 3D build reads from, row-major (index = y * width + x)
    public int width, height;
    public CellType[] cells;

    // rooms reachable by walking from the first room (should equal rooms.Count)
    public int reachableRooms;

    // out-of-bounds reads count as empty so callers can check neighbors without bounds checks
    public CellType GetCell(int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return CellType.Empty;
        return cells[y * width + x];
    }

    public bool IsFloor(int x, int y) => GetCell(x, y) != CellType.Empty;

    public bool AllRoomsReachable => reachableRooms == rooms.Count;

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
        var layout = new BSPLayout { seed = seed };

        // step 1: split the whole map into partitions
        layout.root = new BSPNode { bounds = new RectInt(0, 0, settings.width, settings.height) };
        Split(layout.root, rng, settings, layout);

        // step 2: place one room inside each leaf partition
        foreach (var leaf in layout.leaves)
        {
            if (TryPlaceRoom(leaf, rng, settings))
                layout.rooms.Add(leaf.room);
        }

        // step 3: connect rooms, bake everything into the cell grid, then verify it
        ConnectRooms(layout.root, rng, settings, layout);
        Rasterize(layout, settings);
        layout.reachableRooms = CountReachableRooms(layout);
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

        // cut is measured from the partition's left (vertical) or bottom (horizontal) edge
        var b = node.bounds;
        RectInt a, c;
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

        // recurse into both halves, left first so the order is always the same
        node.left = new BSPNode { bounds = a, depth = node.depth + 1 };
        node.right = new BSPNode { bounds = c, depth = node.depth + 1 };
        Split(node.left, rng, s, layout);
        Split(node.right, rng, s, layout);
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
        // compare every room on the left with every room on the right
        RectInt bestA = a[0], bestB = b[0];
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

        // coin flip picks which way the L bends
        int w = s.corridorWidth;
        AddCorridor(CorridorAnchor(bestA, w), CorridorAnchor(bestB, w), rng.NextDouble() < 0.5, w, layout);
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

    // L-shaped corridor: a to the corner, then the corner to b
    static void AddCorridor(Vector2Int a, Vector2Int b, bool horizontalFirst, int w, BSPLayout layout)
    {
        // horizontal first goes along x then up/down to b; otherwise along y then across
        var corner = horizontalFirst ? new Vector2Int(b.x, a.y) : new Vector2Int(a.x, b.y);
        layout.corridors.Add(Segment(a, corner, w));
        layout.corridors.Add(Segment(corner, b, w));
    }

    // axis-aligned strip w cells wide covering both end points
    static RectInt Segment(Vector2Int p, Vector2Int q, int w)
    {
        // p and q share x or y, so one of the two sizes below is just w
        int x = Mathf.Min(p.x, q.x);
        int y = Mathf.Min(p.y, q.y);
        return new RectInt(x, y, Mathf.Abs(p.x - q.x) + w, Mathf.Abs(p.y - q.y) + w);
    }

    // paints rooms and corridors into the cell grid
    static void Rasterize(BSPLayout layout, BSPSettings s)
    {
        layout.width = s.width;
        layout.height = s.height;
        layout.cells = new CellType[s.width * s.height];   // starts all Empty

        // corridors first, then rooms on top, so overlapping cells end up marked as Room
        foreach (var c in layout.corridors) Fill(layout, c, CellType.Corridor);
        foreach (var r in layout.rooms) Fill(layout, r, CellType.Room);
    }

    static void Fill(BSPLayout layout, RectInt r, CellType type)
    {
        // clamp to the grid so a rect hanging off the edge can't write out of bounds
        int xMin = Mathf.Max(r.xMin, 0), xMax = Mathf.Min(r.xMax, layout.width);
        int yMin = Mathf.Max(r.yMin, 0), yMax = Mathf.Min(r.yMax, layout.height);
        for (int y = yMin; y < yMax; y++)
            for (int x = xMin; x < xMax; x++)
                layout.cells[y * layout.width + x] = type;
    }

    // flood fill over floor cells from the first room, then count the rooms it touched
    static int CountReachableRooms(BSPLayout layout)
    {
        if (layout.rooms.Count == 0) return 0;

        // breadth-first search starting from the middle of room 0
        int w = layout.width;
        var visited = new bool[layout.cells.Length];
        var queue = new Queue<Vector2Int>();
        var start = CorridorAnchor(layout.rooms[0], 1);
        visited[start.y * w + start.x] = true;
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
                int i = next.y * w + next.x;
                if (visited[i]) continue;
                visited[i] = true;
                queue.Enqueue(next);
            }
        }

        // a room counts as reachable if the fill reached its middle cell
        int count = 0;
        foreach (var r in layout.rooms)
        {
            var c = CorridorAnchor(r, 1);
            if (visited[c.y * w + c.x]) count++;
        }
        return count;
    }
}
