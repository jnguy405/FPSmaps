using System;
using System.Collections.Generic;
using UnityEngine;

// Tunable parameters for a BSP layout
// All sizes are in grid cells
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

    public void Validate()
    {
        minLeafSize = Mathf.Max(minLeafSize, minRoomSize + roomPadding * 2);
        width = Mathf.Max(width, minLeafSize);
        height = Mathf.Max(height, minLeafSize);
    }
}

public class BSPNode
{
    public RectInt bounds;
    public int depth;
    public BSPNode left, right;
    public bool hasRoom;
    public RectInt room;

    public bool IsLeaf => left == null;
}

public class BSPLayout
{
    public int seed;
    public BSPNode root;
    public readonly List<BSPNode> nodes = new List<BSPNode>();   // every node, depth-first
    public readonly List<BSPNode> leaves = new List<BSPNode>();
    public readonly List<RectInt> rooms = new List<RectInt>();

    // Fingerprint of the room layout
    // Same seed + settings must give the same hash
    public int Hash
    {
        get
        {
            unchecked
            {
                int h = (int)2166136261;
                foreach (var r in rooms)
                {
                    h = (h ^ r.x) * 16777619;
                    h = (h ^ r.y) * 16777619;
                    h = (h ^ r.width) * 16777619;
                    h = (h ^ r.height) * 16777619;
                }
                return h;
            }
        }
    }
}

// Binary space partitioning: recursively cut the map into partitions, then place one room per leaf
// Uses System.Random so a seed gives identical results on every platform (incl. WebGL)
public static class BSPGenerator
{
    public static BSPLayout Generate(int seed, BSPSettings settings)
    {
        settings.Validate();
        var rng = new System.Random(seed);
        var layout = new BSPLayout { seed = seed };

        layout.root = new BSPNode { bounds = new RectInt(0, 0, settings.width, settings.height) };
        Split(layout.root, rng, settings, layout);

        foreach (var leaf in layout.leaves)
        {
            if (TryPlaceRoom(leaf, rng, settings))
                layout.rooms.Add(leaf.room);
        }
        return layout;
    }

    static void Split(BSPNode node, System.Random rng, BSPSettings s, BSPLayout layout)
    {
        layout.nodes.Add(node);

        if (node.depth >= s.maxDepth || !TryChooseCut(node.bounds, rng, s, out bool vertical, out int cut))
        {
            layout.leaves.Add(node);
            return;
        }

        var b = node.bounds;
        RectInt a, c;
        if (vertical)
        {
            a = new RectInt(b.x, b.y, cut, b.height);
            c = new RectInt(b.x + cut, b.y, b.width - cut, b.height);
        }
        else
        {
            a = new RectInt(b.x, b.y, b.width, cut);
            c = new RectInt(b.x, b.y + cut, b.width, b.height - cut);
        }

        node.left = new BSPNode { bounds = a, depth = node.depth + 1 };
        node.right = new BSPNode { bounds = c, depth = node.depth + 1 };
        Split(node.left, rng, s, layout);
        Split(node.right, rng, s, layout);
    }

    // vertical = cut along X (left/right halves); otherwise cut along Y (bottom/top halves)
    static bool TryChooseCut(RectInt b, System.Random rng, BSPSettings s, out bool vertical, out int cut)
    {
        bool canX = b.width >= s.minLeafSize * 2;
        bool canY = b.height >= s.minLeafSize * 2;
        vertical = false;
        cut = 0;
        if (!canX && !canY) return false;

        if (canX && canY)
        {
            // Prefer cutting the long side so partitions stay roughly square
            float aspect = (float)b.width / b.height;
            if (aspect > 1.25f) vertical = true;
            else if (aspect < 0.8f) vertical = false;
            else vertical = rng.NextDouble() < 0.5;
        }
        else
        {
            vertical = canX;
        }

        int length = vertical ? b.width : b.height;
        int min = Mathf.Max(s.minLeafSize, Mathf.CeilToInt(length * s.minSplitRatio));
        int max = length - min;
        if (max < min) return false;

        cut = rng.Next(min, max + 1);
        return true;
    }

    static bool TryPlaceRoom(BSPNode leaf, System.Random rng, BSPSettings s)
    {
        var b = leaf.bounds;
        int maxW = b.width - s.roomPadding * 2;
        int maxH = b.height - s.roomPadding * 2;
        if (maxW < s.minRoomSize || maxH < s.minRoomSize) return false;

        int minW = Mathf.Clamp(Mathf.CeilToInt(maxW * s.minRoomFill), s.minRoomSize, maxW);
        int minH = Mathf.Clamp(Mathf.CeilToInt(maxH * s.minRoomFill), s.minRoomSize, maxH);
        int w = rng.Next(minW, maxW + 1);
        int h = rng.Next(minH, maxH + 1);
        int x = rng.Next(b.x + s.roomPadding, b.xMax - s.roomPadding - w + 1);
        int y = rng.Next(b.y + s.roomPadding, b.yMax - s.roomPadding - h + 1);

        leaf.room = new RectInt(x, y, w, h);
        leaf.hasRoom = true;
        return true;
    }
}
