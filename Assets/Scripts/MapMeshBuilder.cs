using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// turns a BSPLayout's cell grid into meshes in the map's local space
// floor/ceiling: one quad per floor cell; walls: one quad per floor-cell edge that borders empty space
// wall blocks: tops and outer faces over the empty cells around the map, so walls look solid from above
// UVs are in world units so tiled textures line up across cells
public static class MapMeshBuilder
{
    // floor sits at y = 0 and faces up
    // groupOf picks a submesh per cell (e.g. one per room role) so each can get its own material
    public static void BuildFloor(BSPLayout layout, float cellSize, float uvScale, Mesh mesh,
        Func<Vector2Int, int> groupOf = null, int groupCount = 1)
    {
        var data = new MeshData(groupCount);
        foreach (var cell in layout.FloorCells())
            AddFlatQuad(data, cell, cellSize, 0f, uvScale, true, groupOf != null ? groupOf(cell) : 0);
        data.Apply(mesh);
    }

    // ceiling covers the same cells as the floor, at wall height, facing down
    public static void BuildCeiling(BSPLayout layout, float cellSize, float height, float uvScale, Mesh mesh)
    {
        var data = new MeshData(1);
        foreach (var cell in layout.FloorCells())
            AddFlatQuad(data, cell, cellSize, height, uvScale, false, 0);
        data.Apply(mesh);
    }

    public static void BuildWalls(BSPLayout layout, float cellSize, float height, float uvScale, Mesh mesh)
    {
        var data = new MeshData(1);

        // walls only grow from floor cells, empty cells have nothing to enclose
        foreach (var cell in layout.FloorCells())
        {
            // grid y maps to world z
            int x = cell.x, z = cell.y;

            // world-space corners of this cell
            float x0 = x * cellSize, x1 = (x + 1) * cellSize;
            float z0 = z * cellSize, z1 = (z + 1) * cellSize;

            // each side bordering empty space gets a wall facing back into this cell
            // IsFloor returns false outside the grid, so the map edge is walled off too
            if (!layout.IsFloor(x - 1, z)) AddWall(data, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), Vector3.right, height, uvScale);     // west side
            if (!layout.IsFloor(x + 1, z)) AddWall(data, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), Vector3.left, height, uvScale);      // east side
            if (!layout.IsFloor(x, z - 1)) AddWall(data, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), Vector3.forward, height, uvScale);   // south side
            if (!layout.IsFloor(x, z + 1)) AddWall(data, new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), Vector3.back, height, uvScale);      // north side
        }
        data.Apply(mesh);
    }

    // treats every empty cell touching the map as a solid one-cell block
    // submesh 0: flat tops at wall height, submesh 1: outer faces where a block meets open space
    // the player never sees these (tops face up, outer faces face away), they're for the designer view
    public static void BuildWallBlocks(BSPLayout layout, float cellSize, float height, float uvScale, Mesh mesh)
    {
        // list for a fixed build order, set for fast neighbor lookups
        var cells = new List<Vector2Int>(WallCells(layout));
        var blocks = new HashSet<Vector2Int>(cells);
        var data = new MeshData(2);
        foreach (var cell in cells)
        {
            AddFlatQuad(data, cell, cellSize, height, uvScale, true, 0);

            // outer faces only where the neighbor is neither floor nor another block
            int x = cell.x, z = cell.y;
            float x0 = x * cellSize, x1 = (x + 1) * cellSize;
            float z0 = z * cellSize, z1 = (z + 1) * cellSize;
            if (IsOpen(layout, blocks, x - 1, z)) AddWall(data, new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), Vector3.left, height, uvScale, 1);
            if (IsOpen(layout, blocks, x + 1, z)) AddWall(data, new Vector3(x1, 0f, z0), new Vector3(x1, 0f, z1), Vector3.right, height, uvScale, 1);
            if (IsOpen(layout, blocks, x, z - 1)) AddWall(data, new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0), Vector3.back, height, uvScale, 1);
            if (IsOpen(layout, blocks, x, z + 1)) AddWall(data, new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), Vector3.forward, height, uvScale, 1);
        }
        data.Apply(mesh);
    }

    // empty cells next to any floor cell (diagonals too, so corners are filled)
    // the loop runs one cell past the grid so the map's outer edge gets blocks as well
    static IEnumerable<Vector2Int> WallCells(BSPLayout layout)
    {
        for (int y = -1; y <= layout.height; y++)
            for (int x = -1; x <= layout.width; x++)
                if (!layout.IsFloor(x, y) && TouchesFloor(layout, x, y))
                    yield return new Vector2Int(x, y);
    }

    static bool TouchesFloor(BSPLayout layout, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (layout.IsFloor(x + dx, y + dy)) return true;
        return false;
    }

    static bool IsOpen(BSPLayout layout, HashSet<Vector2Int> blocks, int x, int y)
    {
        return !layout.IsFloor(x, y) && !blocks.Contains(new Vector2Int(x, y));
    }

    // one flat quad covering a cell at height y, facing up or down
    static void AddFlatQuad(MeshData data, Vector2Int cell, float cellSize, float y, float uvScale, bool faceUp, int submesh)
    {
        // cell corners going around: a bottom-left, b top-left, c top-right, d bottom-right (seen from above)
        float x0 = cell.x * cellSize, x1 = (cell.x + 1) * cellSize;
        float z0 = cell.y * cellSize, z1 = (cell.y + 1) * cellSize;
        var a = new Vector3(x0, y, z0);
        var b = new Vector3(x0, y, z1);
        var c = new Vector3(x1, y, z1);
        var d = new Vector3(x1, y, z0);

        // clockwise seen from above faces up; reversed faces down
        if (faceUp) data.AddQuad(a, b, c, d, Vector3.up, TopUV(a, uvScale), TopUV(b, uvScale), TopUV(c, uvScale), TopUV(d, uvScale), submesh);
        else data.AddQuad(a, d, c, b, Vector3.down, TopUV(a, uvScale), TopUV(d, uvScale), TopUV(c, uvScale), TopUV(b, uvScale), submesh);
    }

    // planar mapping from above: world x/z become texture u/v
    static Vector2 TopUV(Vector3 p, float uvScale)
    {
        return new Vector2(p.x, p.z) * uvScale;
    }

    // p and q are the wall's two bottom corners, facing is the direction its front side looks toward
    static void AddWall(MeshData data, Vector3 p, Vector3 q, Vector3 facing, float height, float uvScale, int submesh = 0)
    {
        var up = Vector3.up * height;

        // swap the ends if needed so the quad's front face points the right way
        // cross(up, q - p) is the normal this vertex order would give
        if (Vector3.Dot(Vector3.Cross(up, q - p), facing) < 0f) (p, q) = (q, p);

        // edges run along X or Z, so x + z is the distance along the wall plus a constant
        float u0 = (p.x + p.z) * uvScale, u1 = (q.x + q.z) * uvScale, v = height * uvScale;

        // bottom-left, top-left, top-right, bottom-right as seen from the front
        data.AddQuad(p, p + up, q + up, q, facing,
            new Vector2(u0, 0f), new Vector2(u0, v), new Vector2(u1, v), new Vector2(u1, 0f), submesh);
    }

    // collects vertex data in lists, then copies it into a Mesh in one go
    class MeshData
    {
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int>[] triangles;   // one index list per submesh

        public MeshData(int submeshCount)
        {
            triangles = new List<int>[submeshCount];
            for (int i = 0; i < submeshCount; i++) triangles[i] = new List<int>();
        }

        // a-b-c-d must be clockwise when seen from the front
        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal,
            Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, int submesh = 0)
        {
            // index of this quad's first vertex, triangles below are relative to it
            int i = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);

            // flat quad, so all four corners share one normal
            for (int n = 0; n < 4; n++) normals.Add(normal);
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);

            // split the quad into two triangles along the a-c diagonal
            var tris = triangles[submesh];
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        public void Apply(Mesh mesh)
        {
            // reuse the same Mesh object between rebuilds instead of allocating a new one
            mesh.Clear();

            // 16-bit indices cap out at 65535 vertices, big maps need 32-bit
            mesh.indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = triangles.Length;
            for (int s = 0; s < triangles.Length; s++) mesh.SetTriangles(triangles[s], s);

            // bounds for culling, tangents so normal-mapped textures light correctly later
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
        }
    }
}
