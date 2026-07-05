using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MeshSlicer.Tests {

// Builds watertight test meshes with full vertex attributes
// (position, normal, tangent, uv0) for slicer tests.
static class TestMeshFactory
{
    // Axis-aligned cube centered at the origin, face-split (24 verts, 12 tris),
    // with per-face normals, tangents and 0..1 uvs. Geometrically watertight.
    public static Mesh CreateCube(float size = 1f)
    {
        var h = size * 0.5f;
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tans = new List<Vector4>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        void Face(Vector3 origin, Vector3 right, Vector3 up)
        {
            var n = Vector3.Cross(right, up).normalized;
            var t = new Vector4(right.normalized.x, right.normalized.y, right.normalized.z, 1f);
            var b = verts.Count;
            verts.Add(origin);
            verts.Add(origin + right);
            verts.Add(origin + right + up);
            verts.Add(origin + up);
            for (var i = 0; i < 4; i++) { norms.Add(n); tans.Add(t); }
            uvs.Add(new Vector2(0, 0));
            uvs.Add(new Vector2(1, 0));
            uvs.Add(new Vector2(1, 1));
            uvs.Add(new Vector2(0, 1));
            tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
            tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
        }

        // +X, -X, +Y, -Y, +Z, -Z with outward winding.
        Face(new Vector3(h, -h, h), new Vector3(0, 0, -size), new Vector3(0, size, 0));   // +X
        Face(new Vector3(-h, -h, -h), new Vector3(0, 0, size), new Vector3(0, size, 0));  // -X
        Face(new Vector3(-h, h, h), new Vector3(size, 0, 0), new Vector3(0, 0, -size));   // +Y
        Face(new Vector3(-h, -h, -h), new Vector3(size, 0, 0), new Vector3(0, 0, size));  // -Y
        Face(new Vector3(-h, -h, h), new Vector3(size, 0, 0), new Vector3(0, size, 0));   // +Z
        Face(new Vector3(h, -h, -h), new Vector3(-size, 0, 0), new Vector3(0, size, 0));  // -Z

        var mesh = new Mesh { name = "TestCube" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetTangents(tans);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Icosphere (subdivided icosahedron) centered at the origin. Clean shared-
    // vertex 2-manifold with no pole degeneracies; produces a convex circular
    // cross-section with many split triangles. subdivisions: 0 -> 20 tris,
    // 1 -> 80, 2 -> 320, 3 -> 1280, ...
    public static Mesh CreateSphere(float radius = 1f, int subdivisions = 3)
    {
        var t = (1f + math.sqrt(5f)) * 0.5f;
        var verts = new List<float3>
        {
            math.normalize(new float3(-1, t, 0)), math.normalize(new float3(1, t, 0)),
            math.normalize(new float3(-1, -t, 0)), math.normalize(new float3(1, -t, 0)),
            math.normalize(new float3(0, -1, t)), math.normalize(new float3(0, 1, t)),
            math.normalize(new float3(0, -1, -t)), math.normalize(new float3(0, 1, -t)),
            math.normalize(new float3(t, 0, -1)), math.normalize(new float3(t, 0, 1)),
            math.normalize(new float3(-t, 0, -1)), math.normalize(new float3(-t, 0, 1)),
        };
        var faces = new List<int3>
        {
            new(0, 11, 5), new(0, 5, 1), new(0, 1, 7), new(0, 7, 10), new(0, 10, 11),
            new(1, 5, 9), new(5, 11, 4), new(11, 10, 2), new(10, 7, 6), new(7, 1, 8),
            new(3, 9, 4), new(3, 4, 2), new(3, 2, 6), new(3, 6, 8), new(3, 8, 9),
            new(4, 9, 5), new(2, 4, 11), new(6, 2, 10), new(8, 6, 7), new(9, 8, 1),
        };

        var midCache = new Dictionary<long, int>();
        int Midpoint(int a, int b)
        {
            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (midCache.TryGetValue(key, out var i)) return i;
            i = verts.Count;
            verts.Add(math.normalize((verts[a] + verts[b]) * 0.5f));
            midCache[key] = i;
            return i;
        }

        for (var s = 0; s < subdivisions; s++)
        {
            var next = new List<int3>(faces.Count * 4);
            foreach (var f in faces)
            {
                var a = Midpoint(f.x, f.y);
                var b = Midpoint(f.y, f.z);
                var c = Midpoint(f.z, f.x);
                next.Add(new int3(f.x, a, c));
                next.Add(new int3(f.y, b, a));
                next.Add(new int3(f.z, c, b));
                next.Add(new int3(a, b, c));
            }
            faces = next;
        }

        var vpos = new List<Vector3>(verts.Count);
        var norms = new List<Vector3>(verts.Count);
        var tans = new List<Vector4>(verts.Count);
        var uvs = new List<Vector2>(verts.Count);
        foreach (var n in verts)
        {
            vpos.Add(n * radius);
            norms.Add(n);
            var tangent = math.normalizesafe(math.cross(new float3(0, 1, 0), n), new float3(1, 0, 0));
            tans.Add(new Vector4(tangent.x, tangent.y, tangent.z, 1f));
            uvs.Add(new Vector2(math.atan2(n.z, n.x) / (2f * math.PI) + 0.5f, math.asin(n.y) / math.PI + 0.5f));
        }

        var tris = new List<int>(faces.Count * 3);
        foreach (var f in faces) { tris.Add(f.x); tris.Add(f.y); tris.Add(f.z); }

        var mesh = new Mesh { name = "TestSphere" };
        mesh.SetVertices(vpos);
        mesh.SetNormals(norms);
        mesh.SetTangents(tans);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}

} // namespace MeshSlicer.Tests
