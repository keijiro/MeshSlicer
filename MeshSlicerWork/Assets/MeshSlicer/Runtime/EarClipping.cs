using System.Collections.Generic;
using Unity.Mathematics;

namespace MeshSlicer {

// Simple-polygon ear clipping in 2D. Handles convex and concave loops.
// Nested loops (holes) are not supported by the naive implementation.
static class EarClipping
{
    // Triangulates the polygon described by pts (in order). The output list
    // receives triples of indices into pts, wound counter-clockwise so the
    // triangle face normal points along +Z of the 2D basis.
    public static void Triangulate(IReadOnlyList<float2> pts, List<int> outTris)
    {
        var n = pts.Count;
        if (n < 3) return;

        // Working index ring; reverse to guarantee CCW.
        var v = new List<int>(n);
        for (var i = 0; i < n; i++) v.Add(i);
        if (SignedArea(pts) < 0) v.Reverse();

        var guard = 0;
        var maxGuard = n * n + 8;
        while (v.Count > 3 && guard++ < maxGuard)
        {
            var eared = false;
            var count = v.Count;
            for (var i = 0; i < count; i++)
            {
                var i0 = v[(i - 1 + count) % count];
                var i1 = v[i];
                var i2 = v[(i + 1) % count];
                if (!IsEar(pts, v, i0, i1, i2)) continue;

                outTris.Add(i0); outTris.Add(i1); outTris.Add(i2);
                v.RemoveAt(i);
                eared = true;
                break;
            }
            if (!eared) break; // degenerate / self-intersecting; bail out
        }

        if (v.Count == 3)
        {
            outTris.Add(v[0]); outTris.Add(v[1]); outTris.Add(v[2]);
        }
    }

    static float SignedArea(IReadOnlyList<float2> pts)
    {
        var a = 0f;
        for (int i = 0, n = pts.Count; i < n; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % n];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5f;
    }

    static bool IsEar(IReadOnlyList<float2> pts, List<int> ring, int i0, int i1, int i2)
    {
        var a = pts[i0];
        var b = pts[i1];
        var c = pts[i2];

        // Convex corner test (CCW winding): cross product must be positive.
        if (Cross(b - a, c - a) <= 0f) return false;

        // No other ring vertex may lie inside the candidate triangle.
        foreach (var idx in ring)
        {
            if (idx == i0 || idx == i1 || idx == i2) continue;
            if (PointInTriangle(pts[idx], a, b, c)) return false;
        }
        return true;
    }

    static float Cross(float2 u, float2 w) => u.x * w.y - u.y * w.x;

    static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
    {
        var d1 = Cross(p - a, b - a);
        var d2 = Cross(p - b, c - b);
        var d3 = Cross(p - c, a - c);
        var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }
}

} // namespace MeshSlicer
