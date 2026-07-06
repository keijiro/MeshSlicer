using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests {

// Analysis helpers used by the slicer tests.
static class MeshInspect
{
    // Welds vertices by quantized position and reports whether every edge is shared
    // by exactly two triangles (closed 2-manifold). Degenerate triangles are ignored.
    public static bool IsWatertight(Mesh mesh, out int boundaryEdges, out int nonManifoldEdges)
    {
        var quant = math.max(math.cmax((float3)mesh.bounds.size) * 1e-5f, 1e-6f);
        var verts = mesh.vertices;
        var tris = mesh.triangles;

        var map = new Dictionary<int3, int>();
        int Weld(int i)
        {
            var q = new int3(math.round((float3)(Vector3)verts[i] / quant));
            if (map.TryGetValue(q, out var idx)) return idx;
            idx = map.Count;
            map[q] = idx;
            return idx;
        }

        var edgeCount = new Dictionary<long, int>();
        for (var t = 0; t < tris.Length; t += 3)
        {
            int a = Weld(tris[t]), b = Weld(tris[t + 1]), c = Weld(tris[t + 2]);
            if (a == b || b == c || c == a) continue; // degenerate
            Count(edgeCount, a, b);
            Count(edgeCount, b, c);
            Count(edgeCount, c, a);
        }

        boundaryEdges = 0;
        nonManifoldEdges = 0;
        foreach (var kv in edgeCount)
        {
            if (kv.Value == 1) boundaryEdges++;
            else if (kv.Value != 2) nonManifoldEdges++;
        }
        return boundaryEdges == 0 && nonManifoldEdges == 0;
    }

    static void Count(Dictionary<long, int> map, int a, int b)
    {
        var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        map.TryGetValue(key, out var c);
        map[key] = c + 1;
    }

    // Signed volume enclosed by the mesh (divergence theorem over triangles).
    public static double SignedVolume(Mesh mesh)
    {
        var v = mesh.vertices;
        var t = mesh.triangles;
        double vol = 0;
        for (var i = 0; i < t.Length; i += 3)
        {
            float3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            vol += math.dot(a, math.cross(b, c)) / 6.0;
        }
        return vol;
    }

    // Total area of cap triangles (geometric normal aligned with ±planeNormal and
    // sitting on the plane). Also returns whether any cap vertex carried a non-zero UV.
    public static double CapArea(Mesh mesh, Plane plane, out bool anyNonZeroCapUv, out double signedNormalDot)
    {
        signedNormalDot = 0;
        var n = math.normalize(plane.Normal);
        var extent = math.cmax((float3)mesh.bounds.size);
        var tol = math.max(extent * 1e-3f, 1e-5f);
        var v = mesh.vertices;
        var t = mesh.triangles;
        var uv = mesh.uv;
        var hasUv = uv != null && uv.Length == v.Length;

        double area = 0;
        anyNonZeroCapUv = false;
        for (var i = 0; i < t.Length; i += 3)
        {
            float3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            var cr = math.cross(b - a, c - a);
            var len = math.length(cr);
            if (len < 1e-12f) continue;
            var gn = cr / len;
            if (math.abs(math.dot(gn, n)) < 0.999f) continue; // not parallel to plane
            var centroid = (a + b + c) / 3;
            if (math.abs(plane.SignedDistanceToPoint(centroid)) > tol) continue; // not on plane
            area += 0.5 * len;
            signedNormalDot += 0.5 * len * math.sign(math.dot(gn, n));
            if (hasUv)
            {
                if (math.any((float2)uv[t[i]] != 0) || math.any((float2)uv[t[i + 1]] != 0) ||
                    math.any((float2)uv[t[i + 2]] != 0))
                    anyNonZeroCapUv = true;
            }
        }
        return area;
    }

    // Extreme signed distance of any vertex to the plane (min and max).
    public static void SignedDistanceRange(Mesh mesh, Plane plane, out float min, out float max)
    {
        var v = mesh.vertices;
        min = float.MaxValue; max = float.MinValue;
        foreach (var p in v)
        {
            var d = plane.SignedDistanceToPoint(p);
            min = math.min(min, d);
            max = math.max(max, d);
        }
    }
}

} // namespace MeshSlicer.Tests
