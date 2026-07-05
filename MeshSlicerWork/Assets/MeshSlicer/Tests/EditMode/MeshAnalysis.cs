using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MeshSlicer.Tests {

// Geometry/topology utilities used by the slicer tests.
static class MeshAnalysis
{
    // Signed volume enclosed by a closed, consistently wound (outward) mesh,
    // via the divergence theorem. Positive for outward winding.
    public static double SignedVolume(Mesh mesh)
    {
        var v = mesh.vertices;
        var t = mesh.triangles;
        double vol = 0;
        for (var i = 0; i < t.Length; i += 3)
        {
            var a = (double3)(float3)v[t[i]];
            var b = (double3)(float3)v[t[i + 1]];
            var c = (double3)(float3)v[t[i + 2]];
            vol += math.dot(a, math.cross(b, c)) / 6.0;
        }
        return vol;
    }

    // Quantizes a position to merge coincident vertices.
    static int3 Quantize(float3 p, float grid) =>
        (int3)math.round(p / grid);

    // Remaps triangles onto position-deduplicated vertices.
    public static int[] WeldedTriangles(Mesh mesh, out int weldedVertexCount, float grid = 1e-4f)
        => WeldedTriangles(mesh, out weldedVertexCount, out _, grid);

    public static int[] WeldedTriangles(Mesh mesh, out int weldedVertexCount,
                                        out List<float3> weldedPos, float grid = 1e-4f)
    {
        var v = mesh.vertices;
        var t = mesh.triangles;
        var map = new Dictionary<int3, int>();
        var remap = new int[v.Length];
        weldedPos = new List<float3>();
        for (var i = 0; i < v.Length; i++)
        {
            var key = Quantize(v[i], grid);
            if (!map.TryGetValue(key, out var id))
            {
                id = map.Count;
                map[key] = id;
                weldedPos.Add(v[i]);
            }
            remap[i] = id;
        }
        weldedVertexCount = map.Count;
        var outTris = new int[t.Length];
        for (var i = 0; i < t.Length; i++) outTris[i] = remap[t[i]];
        return outTris;
    }

    // True when the mesh is a closed 2-manifold with consistent winding:
    // every undirected edge is shared by exactly two triangles, and every
    // directed edge appears exactly once. Operates on position-welded topology.
    public static bool IsClosedManifold(Mesh mesh, out string reason)
    {
        var tris = WeldedTriangles(mesh, out _, out var wpos);
        var directed = new Dictionary<(int, int), int>();
        for (var i = 0; i < tris.Length; i += 3)
        {
            AddDirected(directed, tris[i], tris[i + 1]);
            AddDirected(directed, tris[i + 1], tris[i + 2]);
            AddDirected(directed, tris[i + 2], tris[i]);
        }

        var undirected = new Dictionary<(int, int), int>();
        foreach (var kv in directed)
        {
            if (kv.Value != 1)
            {
                var pa = wpos[kv.Key.Item1];
                var pb = wpos[kv.Key.Item2];
                reason = $"directed edge {kv.Key} appears {kv.Value} times (winding not consistent); " +
                         $"a={pa} b={pb}";
                return false;
            }
            var key = kv.Key.Item1 < kv.Key.Item2
                ? (kv.Key.Item1, kv.Key.Item2)
                : (kv.Key.Item2, kv.Key.Item1);
            undirected.TryGetValue(key, out var c);
            undirected[key] = c + 1;
        }

        foreach (var kv in undirected)
        {
            if (kv.Value != 2)
            {
                reason = $"undirected edge {kv.Key} shared by {kv.Value} triangles (not watertight)";
                return false;
            }
        }

        reason = null;
        return true;
    }

    static void AddDirected(Dictionary<(int, int), int> map, int a, int b)
    {
        if (a == b) return; // degenerate; ignored
        var key = (a, b);
        map.TryGetValue(key, out var c);
        map[key] = c + 1;
    }
}

} // namespace MeshSlicer.Tests
