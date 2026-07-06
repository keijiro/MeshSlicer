using System.Collections.Generic;
using Unity.Mathematics;

namespace MeshSlicer {

// Triangulates one or more closed loops that live on a common plane, projected to
// 2D. Handles nested loops (holes / annuli) using the even-odd containment rule,
// bridges holes into their outer contour, then ear-clips the resulting simple
// polygon. Output triangles reference the incoming point ids and wind CCW in the
// supplied 2D coordinate system.
static class PolygonTriangulator
{
    const float Eps = 1e-9f;

    // loops:   each loop is an ordered list of point ids (a closed ring).
    // points2D: point id -> 2D position on the plane.
    // outTris: appended with CCW triangles (triples of point ids).
    public static void Triangulate(List<List<int>> loops, IReadOnlyList<float2> points2D, List<int> outTris)
    {
        var count = loops.Count;
        if (count == 0) return;

        // Signed area (and hence orientation) of each loop.
        var areas = new float[count];
        for (var i = 0; i < count; i++) areas[i] = SignedArea(loops[i], points2D);

        // Containment depth: how many other loops contain this loop's sample point.
        var depth = new int[count];
        for (var i = 0; i < count; i++)
        {
            var p = points2D[loops[i][0]];
            var d = 0;
            for (var j = 0; j < count; j++)
            {
                if (i == j) continue;
                if (math.abs(areas[j]) <= math.abs(areas[i])) continue; // a container is larger
                if (PointInPolygon(p, loops[j], points2D)) d++;
            }
            depth[i] = d;
        }

        // Each even-depth loop is a solid outer contour; odd-depth loops are holes.
        for (var i = 0; i < count; i++)
        {
            if ((depth[i] & 1) != 0) continue; // hole, handled by its parent

            // Immediate child holes: odd depth == depth[i]+1 and contained by loop i.
            var holes = new List<List<int>>();
            var outerPt = points2D[loops[i][0]];
            for (var j = 0; j < count; j++)
            {
                if (depth[j] != depth[i] + 1) continue;
                if (!PointInPolygon(points2D[loops[j][0]], loops[i], points2D)) continue;
                holes.Add(loops[j]);
            }

            TriangulateWithHoles(loops[i], areas[i], holes, points2D, outTris);
        }
    }

    static void TriangulateWithHoles(List<int> outer, float outerArea, List<List<int>> holes,
                                     IReadOnlyList<float2> points2D, List<int> outTris)
    {
        // Work on a mutable copy oriented CCW.
        var poly = new List<int>(outer);
        if (outerArea < 0) poly.Reverse();

        // Bridge holes (oriented CW) into the outer polygon, processing them by
        // descending maximum-x so each bridge target is already part of the polygon.
        holes.Sort((a, b) => MaxX(b, points2D).CompareTo(MaxX(a, points2D)));
        foreach (var hole in holes)
        {
            var h = new List<int>(hole);
            if (SignedArea(h, points2D) > 0) h.Reverse(); // holes must be CW
            BridgeHole(poly, h, points2D);
        }

        EarClip(poly, points2D, outTris);
    }

    // Splices a hole into the outer polygon by inserting a two-way bridge between a
    // mutually visible pair of vertices (Eberly's algorithm).
    static void BridgeHole(List<int> poly, List<int> hole, IReadOnlyList<float2> points2D)
    {
        // Hole vertex with maximum x is the ray origin.
        var m = 0;
        for (var i = 1; i < hole.Count; i++)
            if (points2D[hole[i]].x > points2D[hole[m]].x) m = i;

        var pIndex = FindVisibleVertex(poly, points2D[hole[m]], points2D);

        // Build the merged ring: poly[0..pIndex], hole starting at m all the way
        // around back to m, then poly[pIndex..end]. The duplicated poly[pIndex] and
        // hole[m] form the coincident bridge edges.
        var merged = new List<int>(poly.Count + hole.Count + 2);
        for (var i = 0; i <= pIndex; i++) merged.Add(poly[i]);
        for (var k = 0; k < hole.Count; k++) merged.Add(hole[(m + k) % hole.Count]);
        merged.Add(hole[m]);
        merged.Add(poly[pIndex]);
        for (var i = pIndex + 1; i < poly.Count; i++) merged.Add(poly[i]);

        poly.Clear();
        poly.AddRange(merged);
    }

    static int FindVisibleVertex(List<int> poly, float2 m, IReadOnlyList<float2> points2D)
    {
        // Cast a ray from m toward +x, find the closest polygon edge it crosses.
        var bestT = float.MaxValue;
        var edgeA = -1;
        var edgeB = -1;
        var hit = new float2(float.MaxValue, m.y);
        for (var i = 0; i < poly.Count; i++)
        {
            var a = points2D[poly[i]];
            var b = points2D[poly[(i + 1) % poly.Count]];
            // Edge must span the ray's y and lie (partly) to the right.
            if ((a.y > m.y) == (b.y > m.y)) continue;
            var t = (m.y - a.y) / (b.y - a.y);
            var x = a.x + t * (b.x - a.x);
            if (x < m.x - Eps) continue;
            if (x < bestT)
            {
                bestT = x;
                hit = new float2(x, m.y);
                edgeA = poly[i];
                edgeB = poly[(i + 1) % poly.Count];
            }
        }

        if (edgeA < 0) return 0; // degenerate; shouldn't happen for valid input

        // Candidate P: the edge endpoint with the larger x.
        var pId = points2D[edgeA].x > points2D[edgeB].x ? edgeA : edgeB;
        var pIdx = poly.IndexOf(pId);

        // If a reflex vertex lies inside triangle (m, hit, P), the visible vertex is
        // the reflex vertex minimizing the angle to the +x axis from m.
        var p = points2D[pId];
        var bestCos = -2f;
        var bestIdx = pIdx;
        for (var i = 0; i < poly.Count; i++)
        {
            var id = poly[i];
            if (id == pId) continue;
            var r = points2D[id];
            if (!PointInTriangle(r, m, hit, p)) continue;
            if (!IsReflex(poly, i, points2D)) continue;
            var dir = math.normalizesafe(r - m);
            var c = dir.x; // cos of angle to +x
            if (c > bestCos)
            {
                bestCos = c;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    static bool IsReflex(List<int> poly, int i, IReadOnlyList<float2> points2D)
    {
        var n = poly.Count;
        var a = points2D[poly[(i - 1 + n) % n]];
        var b = points2D[poly[i]];
        var c = points2D[poly[(i + 1) % n]];
        return Cross(b - a, c - b) < 0; // CCW polygon: reflex when turning right
    }

    // O(n^2) ear clipping over a doubly linked list. Each vertex is classified as
    // convex / reflex / collinear; only reflex vertices can block an ear (Meisters),
    // collinear vertices (incl. the coincident hole-bridge duplicates) are dropped.
    static void EarClip(List<int> poly, IReadOnlyList<float2> points2D, List<int> outTris)
    {
        var n = poly.Count;
        if (n < 3) return;

        var next = new int[n];
        var prev = new int[n];
        var kind = new byte[n]; // 0 convex, 1 reflex, 2 collinear
        for (var i = 0; i < n; i++) { next[i] = (i + 1) % n; prev[i] = (i - 1 + n) % n; }
        for (var i = 0; i < n; i++) kind[i] = Classify(poly, points2D, prev[i], i, next[i]);

        var remaining = n;
        var cur = 0;
        var guard = 0;
        var maxGuard = 2 * n * n + 64;
        while (remaining > 3 && guard++ < maxGuard)
        {
            int p = prev[cur], nx = next[cur];
            // A convex or collinear corner is an ear when no reflex vertex is strictly
            // inside it. Collinear corners give zero-area triangles that keep the two
            // boundary edges (which are shared with the walls) intact — dropping them
            // would tear the cap away from the wall.
            if (kind[cur] != 1 && IsEar(poly, points2D, kind, next, p, cur, nx))
            {
                outTris.Add(poly[p]); outTris.Add(poly[cur]); outTris.Add(poly[nx]);
                next[p] = nx; prev[nx] = p;
                kind[p] = Classify(poly, points2D, prev[p], p, next[p]);
                kind[nx] = Classify(poly, points2D, prev[nx], nx, next[nx]);
                remaining--;
                cur = p;
            }
            else cur = nx;
        }
        // final triangle
        outTris.Add(poly[cur]);
        outTris.Add(poly[next[cur]]);
        outTris.Add(poly[next[next[cur]]]);
    }

    static byte Classify(List<int> poly, IReadOnlyList<float2> pts, int p, int i, int n)
    {
        var cr = Cross(pts[poly[i]] - pts[poly[p]], pts[poly[n]] - pts[poly[i]]);
        return cr > Eps ? (byte)0 : cr < -Eps ? (byte)1 : (byte)2;
    }

    static bool IsEar(List<int> poly, IReadOnlyList<float2> pts, byte[] kind, int[] next, int p, int i, int nx)
    {
        float2 a = pts[poly[p]], b = pts[poly[i]], c = pts[poly[nx]];
        for (var j = next[nx]; j != p; j = next[j])
            if (kind[j] == 1 && StrictlyInside(pts[poly[j]], a, b, c)) return false;
        return true;
    }

    static bool StrictlyInside(float2 p, float2 a, float2 b, float2 c)
        => Cross(b - a, p - a) > Eps && Cross(c - b, p - b) > Eps && Cross(a - c, p - c) > Eps;

    // --- geometry helpers ---

    static float SignedArea(List<int> loop, IReadOnlyList<float2> points2D)
    {
        var area = 0f;
        for (var i = 0; i < loop.Count; i++)
        {
            var a = points2D[loop[i]];
            var b = points2D[loop[(i + 1) % loop.Count]];
            area += a.x * b.y - b.x * a.y;
        }
        return area * 0.5f;
    }

    static float MaxX(List<int> loop, IReadOnlyList<float2> points2D)
    {
        var mx = float.MinValue;
        foreach (var id in loop) mx = math.max(mx, points2D[id].x);
        return mx;
    }

    static bool PointInPolygon(float2 p, List<int> loop, IReadOnlyList<float2> points2D)
    {
        var inside = false;
        var n = loop.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = points2D[loop[i]];
            var b = points2D[loop[j]];
            if ((a.y > p.y) != (b.y > p.y))
            {
                var x = a.x + (p.y - a.y) / (b.y - a.y) * (b.x - a.x);
                if (p.x < x) inside = !inside;
            }
        }
        return inside;
    }

    static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
    {
        var d1 = Cross(b - a, p - a);
        var d2 = Cross(c - b, p - b);
        var d3 = Cross(a - c, p - c);
        var hasNeg = d1 < -Eps || d2 < -Eps || d3 < -Eps;
        var hasPos = d1 > Eps || d2 > Eps || d3 > Eps;
        return !(hasNeg && hasPos);
    }

    static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;
}

} // namespace MeshSlicer
