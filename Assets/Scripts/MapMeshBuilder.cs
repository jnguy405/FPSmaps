using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// turns a BSPLayout's cell grid into meshes in the map's local space
// floor/ceiling: one quad per floor cell; walls: one quad per floor-cell edge that borders empty space
// UVs are in world units so tiled textures line up across cells
public static class MapMeshBuilder
{
    // floor sits at y = 0 and faces up
    public static void BuildFloor(BSPLayout layout, float cellSize, float uvScale, Mesh mesh)
    {
        BuildHorizontal(layout, cellSize, 0f, uvScale, true, mesh);
    }

    // ceiling covers the same cells as the floor, at wall height, facing down
    public static void BuildCeiling(BSPLayout layout, float cellSize, float height, float uvScale, Mesh mesh)
    {
        BuildHorizontal(layout, cellSize, height, uvScale, false, mesh);
    }

    public static void BuildWalls(BSPLayout layout, float cellSize, float height, float uvScale, Mesh mesh)
    {
        var data = new MeshData();

        // grid y maps to world z, so walk every cell row by row
        for (int z = 0; z < layout.height; z++)
        {
            for (int x = 0; x < layout.width; x++)
            {
                // walls only grow from floor cells, empty cells have nothing to enclose
                if (!layout.IsFloor(x, z)) continue;

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
        }
        data.Apply(mesh);
    }

    // shared by floor and ceiling: one flat quad per floor cell at height y
    static void BuildHorizontal(BSPLayout layout, float cellSize, float y, float uvScale, bool faceUp, Mesh mesh)
    {
        var data = new MeshData();
        var normal = faceUp ? Vector3.up : Vector3.down;

        for (int z = 0; z < layout.height; z++)
        {
            for (int x = 0; x < layout.width; x++)
            {
                // rooms and corridors both count as floor, empty cells are skipped
                if (!layout.IsFloor(x, z)) continue;

                // cell corners going around: a bottom-left, b top-left, c top-right, d bottom-right (seen from above)
                float x0 = x * cellSize, x1 = (x + 1) * cellSize;
                float z0 = z * cellSize, z1 = (z + 1) * cellSize;
                var a = new Vector3(x0, y, z0);
                var b = new Vector3(x0, y, z1);
                var c = new Vector3(x1, y, z1);
                var d = new Vector3(x1, y, z0);

                // clockwise seen from above faces up; reversed faces down
                if (faceUp) data.AddQuad(a, b, c, d, normal, TopUV(a, uvScale), TopUV(b, uvScale), TopUV(c, uvScale), TopUV(d, uvScale));
                else data.AddQuad(a, d, c, b, normal, TopUV(a, uvScale), TopUV(d, uvScale), TopUV(c, uvScale), TopUV(b, uvScale));
            }
        }
        data.Apply(mesh);
    }

    // planar mapping from above: world x/z become texture u/v
    static Vector2 TopUV(Vector3 p, float uvScale)
    {
        return new Vector2(p.x, p.z) * uvScale;
    }

    // p and q are the wall's two bottom corners, inward points into the floor cell it encloses
    static void AddWall(MeshData data, Vector3 p, Vector3 q, Vector3 inward, float height, float uvScale)
    {
        var up = Vector3.up * height;

        // swap the ends if needed so the quad's front face points into the room
        // cross(up, q - p) is the normal this vertex order would give
        if (Vector3.Dot(Vector3.Cross(up, q - p), inward) < 0f) (p, q) = (q, p);

        // edges run along X or Z, so x + z is the distance along the wall plus a constant
        float u0 = (p.x + p.z) * uvScale, u1 = (q.x + q.z) * uvScale, v = height * uvScale;

        // bottom-left, top-left, top-right, bottom-right as seen from inside the room
        data.AddQuad(p, p + up, q + up, q, inward,
            new Vector2(u0, 0f), new Vector2(u0, v), new Vector2(u1, v), new Vector2(u1, 0f));
    }

    // collects vertex data in lists, then copies it into a Mesh in one go
    class MeshData
    {
        readonly List<Vector3> vertices = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> triangles = new List<int>();

        // a-b-c-d must be clockwise when seen from the front
        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal,
            Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            // index of this quad's first vertex, triangles below are relative to it
            int i = vertices.Count;
            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);

            // flat quad, so all four corners share one normal
            for (int n = 0; n < 4; n++) normals.Add(normal);
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);

            // split the quad into two triangles along the a-c diagonal
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
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
            mesh.SetTriangles(triangles, 0);

            // bounds for culling, tangents so normal-mapped textures light correctly later
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
        }
    }
}
