using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MeshSlicer.Demo {

// Compact watertight primitive builders for the slicer demo.
public static class DemoMeshes
{
    public static Mesh Cube(float size = 1f)
    {
        var h = size * 0.5f;
        var v = new List<Vector3>();
        var nn = new List<Vector3>();
        var tt = new List<Vector4>();
        var uv = new List<Vector2>();
        var ix = new List<int>();

        void Face(Vector3 o, Vector3 r, Vector3 up)
        {
            var n = Vector3.Cross(r, up).normalized;
            var tan = new Vector4(r.normalized.x, r.normalized.y, r.normalized.z, 1f);
            var b = v.Count;
            v.Add(o); v.Add(o + r); v.Add(o + r + up); v.Add(o + up);
            for (var i = 0; i < 4; i++) { nn.Add(n); tt.Add(tan); }
            uv.Add(new(0, 0)); uv.Add(new(1, 0)); uv.Add(new(1, 1)); uv.Add(new(0, 1));
            ix.Add(b); ix.Add(b + 1); ix.Add(b + 2);
            ix.Add(b); ix.Add(b + 2); ix.Add(b + 3);
        }

        Face(new(h, -h, h), new(0, 0, -size), new(0, size, 0));
        Face(new(-h, -h, -h), new(0, 0, size), new(0, size, 0));
        Face(new(-h, h, h), new(size, 0, 0), new(0, 0, -size));
        Face(new(-h, -h, -h), new(size, 0, 0), new(0, 0, size));
        Face(new(-h, -h, h), new(size, 0, 0), new(0, size, 0));
        Face(new(h, -h, -h), new(-size, 0, 0), new(0, size, 0));

        return Build("DemoCube", v, nn, tt, uv, ix);
    }

    public static Mesh Icosphere(float radius = 1f, int subdivisions = 3)
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

        var mid = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (mid.TryGetValue(key, out var i)) return i;
            i = verts.Count;
            verts.Add(math.normalize((verts[a] + verts[b]) * 0.5f));
            mid[key] = i;
            return i;
        }

        for (var s = 0; s < subdivisions; s++)
        {
            var next = new List<int3>(faces.Count * 4);
            foreach (var f in faces)
            {
                int a = Mid(f.x, f.y), b = Mid(f.y, f.z), c = Mid(f.z, f.x);
                next.Add(new(f.x, a, c)); next.Add(new(f.y, b, a));
                next.Add(new(f.z, c, b)); next.Add(new(a, b, c));
            }
            faces = next;
        }

        var v = new List<Vector3>();
        var nn = new List<Vector3>();
        var tt = new List<Vector4>();
        var uv = new List<Vector2>();
        foreach (var p in verts)
        {
            v.Add(p * radius); nn.Add(p);
            var tan = math.normalizesafe(math.cross(new float3(0, 1, 0), p), new float3(1, 0, 0));
            tt.Add(new Vector4(tan.x, tan.y, tan.z, 1f));
            uv.Add(new Vector2(math.atan2(p.z, p.x) / (2f * math.PI) + 0.5f, math.asin(p.y) / math.PI + 0.5f));
        }
        var ix = new List<int>(faces.Count * 3);
        foreach (var f in faces) { ix.Add(f.x); ix.Add(f.y); ix.Add(f.z); }

        return Build("DemoIcosphere", v, nn, tt, uv, ix);
    }

    public static Mesh Cylinder(float radius = 0.5f, float height = 1.4f, int segments = 32)
    {
        var v = new List<Vector3>();
        var nn = new List<Vector3>();
        var tt = new List<Vector4>();
        var uv = new List<Vector2>();
        var ix = new List<int>();
        var h = height * 0.5f;

        // Side wall (duplicated seam-free ring per cap for clean normals).
        for (var i = 0; i <= segments; i++)
        {
            var a = (float)i / segments * 2f * math.PI;
            float cx = math.cos(a), cz = math.sin(a);
            var n = new Vector3(cx, 0, cz);
            var tan = new Vector4(-cz, 0, cx, 1f);
            v.Add(new Vector3(cx * radius, -h, cz * radius)); nn.Add(n); tt.Add(tan); uv.Add(new((float)i / segments, 0));
            v.Add(new Vector3(cx * radius, h, cz * radius)); nn.Add(n); tt.Add(tan); uv.Add(new((float)i / segments, 1));
        }
        for (var i = 0; i < segments; i++)
        {
            var b = i * 2;
            ix.Add(b); ix.Add(b + 1); ix.Add(b + 2);
            ix.Add(b + 2); ix.Add(b + 1); ix.Add(b + 3);
        }

        // Top and bottom disks.
        void Disk(float y, Vector3 n, bool flip)
        {
            var center = v.Count;
            v.Add(new Vector3(0, y, 0)); nn.Add(n); tt.Add(new Vector4(1, 0, 0, 1)); uv.Add(new(0.5f, 0.5f));
            var ring = v.Count;
            for (var i = 0; i <= segments; i++)
            {
                var a = (float)i / segments * 2f * math.PI;
                float cx = math.cos(a), cz = math.sin(a);
                v.Add(new Vector3(cx * radius, y, cz * radius)); nn.Add(n); tt.Add(new Vector4(1, 0, 0, 1));
                uv.Add(new(cx * 0.5f + 0.5f, cz * 0.5f + 0.5f));
            }
            for (var i = 0; i < segments; i++)
            {
                if (flip) { ix.Add(center); ix.Add(ring + i + 1); ix.Add(ring + i); }
                else { ix.Add(center); ix.Add(ring + i); ix.Add(ring + i + 1); }
            }
        }
        Disk(h, Vector3.up, false);
        Disk(-h, Vector3.down, true);

        return Build("DemoCylinder", v, nn, tt, uv, ix);
    }

    static Mesh Build(string name, List<Vector3> v, List<Vector3> nn,
                      List<Vector4> tt, List<Vector2> uv, List<int> ix)
    {
        var mesh = new Mesh { name = name };
        if (v.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(v);
        mesh.SetNormals(nn);
        mesh.SetTangents(tt);
        mesh.SetUVs(0, uv);
        mesh.SetTriangles(ix, 0);
        mesh.RecalculateBounds();
        return mesh;
    }
}

} // namespace MeshSlicer.Demo
