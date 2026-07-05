using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer {

// Naive, correctness-first mesh slicer. Cuts a 2-manifold / watertight mesh
// with an arbitrary plane and returns the two resulting capped meshes.
public static class Slicer
{
    const float PositionWeldGrid = 1e-5f;

    // A vertex staged during triangle clipping.
    struct PolyVert
    {
        public bool IsCut;
        public int Src;       // source vertex index (whole/original vertices)
        public long Edge;     // source-edge key (wall intersection vertices)
        public int Boundary;  // cut-boundary id, or -1 if not on the plane
        public float3 Pos, Nrm;
        public float4 Tan;
        public float2 Uv;
    }

    // Cuts the source mesh with the given plane. The cross section of each half
    // is closed with a flat cap. Vertex attributes carried over: position,
    // normal, tangent and uv0. Cap uv0 is fixed to (0, 0).
    public static SliceResult Slice(Mesh source, Plane plane)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        // --- Read source geometry -----------------------------------------
        var srcPos = source.vertices;
        var srcNrm = source.normals;
        var srcTan = source.tangents;
        var srcUv = source.uv;
        var tris = source.triangles;
        var vc = srcPos.Length;

        var hasN = srcNrm.Length == vc;
        var hasT = srcTan.Length == vc;
        var hasU = srcUv.Length == vc;

        float3 Pos(int i) => srcPos[i];
        float3 Nrm(int i) => hasN ? (float3)srcNrm[i] : new float3(0, 1, 0);
        float4 Tan(int i) => hasT ? (float4)(Vector4)srcTan[i] : new float4(1, 0, 0, 1);
        float2 Uv(int i) => hasU ? (float2)srcUv[i] : float2.zero;

        // Snap vertices lying within a small tolerance of the plane onto it.
        // This removes near-degenerate slivers (and the tiny cap loops they
        // spawn) that appear when the plane nearly grazes a source vertex.
        var eps = math.length((float3)source.bounds.size) * 1e-4f;
        var dist = new float[vc];
        for (var i = 0; i < vc; i++)
        {
            var d = plane.SignedDistanceToPoint(srcPos[i]);
            dist[i] = math.abs(d) < eps ? 0f : d;
        }

        var pos = new MeshBuildBuffer();
        var neg = new MeshBuildBuffer();

        // Cut boundary points welded by position, and the directed cap edges
        // for each half (derived from the on-plane edge of each wall polygon).
        var boundaryMap = new Dictionary<int3, int>();
        var boundaryPos = new List<float3>();
        var posCapEdges = new List<int2>();
        var negCapEdges = new List<int2>();

        int GetBoundary(float3 p)
        {
            var key = (int3)math.round(p / PositionWeldGrid);
            if (boundaryMap.TryGetValue(key, out var id)) return id;
            id = boundaryPos.Count;
            boundaryMap[key] = id;
            boundaryPos.Add(p);
            return id;
        }

        var posPoly = new List<PolyVert>(4);
        var negPoly = new List<PolyVert>(4);

        // --- Classify & split every triangle ------------------------------
        for (var t = 0; t < tris.Length; t += 3)
        {
            int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
            float d0 = dist[i0], d1 = dist[i1], d2 = dist[i2];
            var p0 = d0 >= 0; var p1 = d1 >= 0; var p2 = d2 >= 0;

            if (p0 && p1 && p2) { EmitWhole(pos); continue; }
            if (!p0 && !p1 && !p2) { EmitWhole(neg); continue; }

            posPoly.Clear();
            negPoly.Clear();
            ClipEdge(i0, i1, d0, d1);
            ClipEdge(i1, i2, d1, d2);
            ClipEdge(i2, i0, d2, d0);

            FanEmit(pos, posPoly);
            FanEmit(neg, negPoly);
            ExtractCapEdge(posPoly, posCapEdges);
            ExtractCapEdge(negPoly, negCapEdges);

            void ClipEdge(int a, int b, float da, float db)
            {
                if (da > 0) posPoly.Add(Original(a, false));
                else if (da < 0) negPoly.Add(Original(a, false));
                else { var v = Original(a, true); posPoly.Add(v); negPoly.Add(v); }

                if (da * db >= 0) return; // no strict crossing

                var s = da / (da - db);
                var ip = Interpolate(a, b, s);
                posPoly.Add(ip);
                negPoly.Add(ip);
            }

            void EmitWhole(MeshBuildBuffer buf)
            {
                var a = buf.AddOriginal(i0, Pos(i0), Nrm(i0), Tan(i0), Uv(i0));
                var b = buf.AddOriginal(i1, Pos(i1), Nrm(i1), Tan(i1), Uv(i1));
                var c = buf.AddOriginal(i2, Pos(i2), Nrm(i2), Tan(i2), Uv(i2));
                buf.AddTriangle(a, b, c);
            }
        }

        // --- Build the caps ------------------------------------------------
        var n = math.normalize(plane.Normal);
        var refv = math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
        var u = math.normalize(math.cross(refv, n));
        var w = math.cross(n, u); // (u, w, n) right-handed: cross(u, w) == n
        BuildCap(pos, posCapEdges, boundaryPos, u, w, n);
        BuildCap(neg, negCapEdges, boundaryPos, u, w, n);

        var posMesh = pos.ToMesh(source.name + "_Positive");
        var negMesh = neg.ToMesh(source.name + "_Negative");
        return new SliceResult(posMesh, negMesh);

        // Local helpers that close over the source accessors ----------------

        PolyVert Original(int i, bool onPlane) => new PolyVert
        {
            IsCut = false, Src = i, Boundary = onPlane ? GetBoundary(Pos(i)) : -1,
            Pos = Pos(i), Nrm = Nrm(i), Tan = Tan(i), Uv = Uv(i)
        };

        PolyVert Interpolate(int a, int b, float s)
        {
            var tan = math.lerp(Tan(a), Tan(b), s);
            var txyz = math.normalizesafe(tan.xyz, new float3(1, 0, 0));
            var p = math.lerp(Pos(a), Pos(b), s);
            return new PolyVert
            {
                IsCut = true, Edge = EdgeKey(a, b), Boundary = GetBoundary(p),
                Pos = p,
                Nrm = math.normalizesafe(math.lerp(Nrm(a), Nrm(b), s), new float3(0, 1, 0)),
                Tan = new float4(txyz, Tan(a).w),
                Uv = math.lerp(Uv(a), Uv(b), s)
            };
        }
    }

    static void FanEmit(MeshBuildBuffer buf, List<PolyVert> poly)
    {
        if (poly.Count < 3) return;
        var i0 = Resolve(buf, poly[0]);
        for (var k = 1; k < poly.Count - 1; k++)
        {
            var i1 = Resolve(buf, poly[k]);
            var i2 = Resolve(buf, poly[k + 1]);
            buf.AddTriangle(i0, i1, i2);
        }
    }

    // Records the directed on-plane edge(s) of a wall polygon. The half's cap
    // must traverse these edges in reverse, so the recorded direction already
    // is the reversed (cap-consistent) one.
    static void ExtractCapEdge(List<PolyVert> poly, List<int2> capEdges)
    {
        var m = poly.Count;
        if (m < 3) return;
        for (var i = 0; i < m; i++)
        {
            var c = poly[i];
            var d = poly[(i + 1) % m];
            if (c.Boundary >= 0 && d.Boundary >= 0 && c.Boundary != d.Boundary)
                capEdges.Add(new int2(d.Boundary, c.Boundary)); // reversed: cap opposes wall
        }
    }

    static int Resolve(MeshBuildBuffer buf, in PolyVert v) =>
        v.IsCut
            ? buf.AddWall(v.Edge, v.Pos, v.Nrm, v.Tan, v.Uv)
            : buf.AddOriginal(v.Src, v.Pos, v.Nrm, v.Tan, v.Uv);

    static long EdgeKey(int a, int b)
    {
        int lo = math.min(a, b), hi = math.max(a, b);
        return ((long)lo << 32) | (uint)hi;
    }

    // Assembles directed cap edges into loops and triangulates each, preserving
    // the loop orientation so cap winding stays consistent with the walls.
    static void BuildCap(MeshBuildBuffer buf, List<int2> capEdges,
                         List<float3> boundaryPos, float3 u, float3 w, float3 n)
    {
        if (capEdges.Count == 0) return;

        var outgoing = new Dictionary<int, List<int>>();
        foreach (var e in capEdges)
        {
            if (!outgoing.TryGetValue(e.x, out var l)) { l = new List<int>(2); outgoing[e.x] = l; }
            l.Add(e.y);
        }

        var capTan = new float4(u, 1f);
        var loop = new List<int>();
        var pts = new List<float2>();
        var localTris = new List<int>();
        var starts = new List<int>(outgoing.Keys);

        foreach (var start in starts)
        {
            while (outgoing.TryGetValue(start, out var sl) && sl.Count > 0)
            {
                // Walk a directed cycle back to `start`.
                loop.Clear();
                var cur = start;
                while (outgoing.TryGetValue(cur, out var cl) && cl.Count > 0)
                {
                    var nxt = cl[cl.Count - 1];
                    cl.RemoveAt(cl.Count - 1);
                    loop.Add(cur);
                    cur = nxt;
                    if (cur == start) break;
                }
                if (loop.Count < 3) continue;

                pts.Clear();
                for (var i = 0; i < loop.Count; i++)
                {
                    var p = boundaryPos[loop[i]];
                    pts.Add(new float2(math.dot(p, u), math.dot(p, w)));
                }

                localTris.Clear();
                EarClipping.Triangulate(pts, localTris); // CCW output (+n)
                var loopCcw = SignedArea(pts) > 0f;
                var capNrm = loopCcw ? n : -n;

                for (var i = 0; i < localTris.Count; i += 3)
                {
                    int ka = loop[localTris[i]];
                    int kb = loop[localTris[i + 1]];
                    int kc = loop[localTris[i + 2]];
                    var la = buf.AddCap(ka, boundaryPos[ka], capNrm, capTan, float2.zero);
                    var lb = buf.AddCap(kb, boundaryPos[kb], capNrm, capTan, float2.zero);
                    var lc = buf.AddCap(kc, boundaryPos[kc], capNrm, capTan, float2.zero);
                    // EarClipping is CCW; keep it when the loop is CCW, flip it
                    // to the loop's winding otherwise.
                    if (loopCcw) buf.AddTriangle(la, lb, lc);
                    else buf.AddTriangle(la, lc, lb);
                }
            }
        }
    }

    static float SignedArea(List<float2> pts)
    {
        var a = 0f;
        for (int i = 0, m = pts.Count; i < m; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % m];
            a += p.x * q.y - q.x * p.y;
        }
        return a * 0.5f;
    }
}

} // namespace MeshSlicer
