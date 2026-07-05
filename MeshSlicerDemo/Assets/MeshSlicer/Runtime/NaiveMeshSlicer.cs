using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer
{
    // Naive (correctness-first) mesh slicer. Splits a triangle mesh by an arbitrary
    // plane, returning two meshes with caps. Supports concave geometry and concave
    // caps (polygons with holes).
    //
    // Attributes preserved: position, normal, tangent, uv0. Cap uv0 is fixed to (0,0).
    // On-plane vertices are conventionally treated as positive.
    public static class NaiveMeshSlicer
    {
        sealed class Side
        {
            public List<Vector3> Pos;
            public List<Vector3> Nrm;
            public List<Vector4> Tan;
            public List<Vector2> Uv;
            public List<int> Tri;

            public Side(int hint)
            {
                Pos = new List<Vector3>(hint);
                Nrm = new List<Vector3>(hint);
                Tan = new List<Vector4>(hint);
                Uv  = new List<Vector2>(hint);
                Tri = new List<int>(hint);
            }

            public int Add(Vector3 p, Vector3 n, Vector4 t, Vector2 u)
            {
                int i = Pos.Count;
                Pos.Add(p); Nrm.Add(n); Tan.Add(t); Uv.Add(u);
                return i;
            }
        }

        public static SliceResult Slice(Mesh source, Plane plane)
        {
            var srcPos = source.vertices;
            var srcNrm = source.normals;
            var srcTan = source.tangents;
            var srcUv  = source.uv;
            var srcTri = source.triangles;

            bool hasNrm = srcNrm != null && srcNrm.Length == srcPos.Length;
            bool hasTan = srcTan != null && srcTan.Length == srcPos.Length;
            bool hasUv  = srcUv  != null && srcUv.Length  == srcPos.Length;

            plane = Plane.Normalize(plane);

            int n = srcPos.Length;
            var dist = new float[n];
            var sign = new sbyte[n];
            for (int i = 0; i < n; i++)
            {
                dist[i] = plane.SignedDistanceToPoint(srcPos[i]);
                sign[i] = dist[i] >= 0f ? (sbyte)1 : (sbyte)-1;
            }

            int hint = n;
            var pos = new Side(hint);
            var neg = new Side(hint);

            var posBodyMap = new int[n];
            var negBodyMap = new int[n];
            for (int i = 0; i < n; i++) { posBodyMap[i] = -1; negBodyMap[i] = -1; }

            int Body(int v, Side side, int[] map)
            {
                if (map[v] >= 0) return map[v];
                int idx = side.Add(
                    srcPos[v],
                    hasNrm ? srcNrm[v] : Vector3.zero,
                    hasTan ? srcTan[v] : Vector4.zero,
                    hasUv  ? srcUv[v]  : Vector2.zero);
                map[v] = idx;
                return idx;
            }

            // Per-side interpolated body vertex at cut.
            var posCutCache = new Dictionary<long, int>();
            var negCutCache = new Dictionary<long, int>();

            // Unique cap-point id per cut edge (shared 3D point used for cap triangulation).
            var capByEdge = new Dictionary<long, int>();
            var capPos = new List<float3>();

            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            int CapVertex(int a, int b)
            {
                long k = Key(a, b);
                if (capByEdge.TryGetValue(k, out var i)) return i;
                float t = dist[a] / (dist[a] - dist[b]);
                float3 p = math.lerp((float3)srcPos[a], (float3)srcPos[b], t);
                i = capPos.Count;
                capPos.Add(p);
                capByEdge[k] = i;
                return i;
            }

            int CutBody(int a, int b, Side side, Dictionary<long, int> cache, int[] map)
            {
                float t = dist[a] / (dist[a] - dist[b]);
                // Snap a cut that lands on an endpoint to the existing body vertex,
                // so degenerate triangles vanish (their indices collapse, then EmitTri
                // drops them).
                if (t <= 1e-5f) return Body(a, side, map);
                if (t >= 1f - 1e-5f) return Body(b, side, map);

                long k = Key(a, b);
                if (cache.TryGetValue(k, out var i)) return i;
                var p = Vector3.LerpUnclamped(srcPos[a], srcPos[b], t);
                Vector3 nn = Vector3.zero;
                Vector4 tt = Vector4.zero;
                Vector2 uu = Vector2.zero;
                if (hasNrm) nn = Vector3.LerpUnclamped(srcNrm[a], srcNrm[b], t).normalized;
                if (hasTan)
                {
                    Vector4 ta = srcTan[a], tb = srcTan[b];
                    var x3 = Vector3.LerpUnclamped(
                        new Vector3(ta.x, ta.y, ta.z),
                        new Vector3(tb.x, tb.y, tb.z), t).normalized;
                    tt = new Vector4(x3.x, x3.y, x3.z, ta.w);
                }
                if (hasUv) uu = Vector2.LerpUnclamped(srcUv[a], srcUv[b], t);
                i = side.Add(p, nn, tt, uu);
                cache[k] = i;
                return i;
            }

            void EmitTri(Side side, int i0, int i1, int i2)
            {
                if (i0 == i1 || i1 == i2 || i0 == i2) return;
                side.Tri.Add(i0); side.Tri.Add(i1); side.Tri.Add(i2);
            }

            // Cap edges, directed CCW as viewed from +plane.Normal.
            var capEdges = new List<int2>();

            for (int t = 0; t < srcTri.Length; t += 3)
            {
                int i0 = srcTri[t], i1 = srcTri[t + 1], i2 = srcTri[t + 2];
                sbyte s0 = sign[i0], s1 = sign[i1], s2 = sign[i2];
                int negCount = (s0 < 0 ? 1 : 0) + (s1 < 0 ? 1 : 0) + (s2 < 0 ? 1 : 0);

                if (negCount == 0)
                {
                    EmitTri(pos, Body(i0, pos, posBodyMap),
                                 Body(i1, pos, posBodyMap),
                                 Body(i2, pos, posBodyMap));
                    continue;
                }
                if (negCount == 3)
                {
                    EmitTri(neg, Body(i0, neg, negBodyMap),
                                 Body(i1, neg, negBodyMap),
                                 Body(i2, neg, negBodyMap));
                    continue;
                }

                // Rotate so lone vertex is 'a'.
                int a, b, c; sbyte sa;
                if (negCount == 1)
                {
                    if (s0 < 0)      { a = i0; b = i1; c = i2; }
                    else if (s1 < 0) { a = i1; b = i2; c = i0; }
                    else             { a = i2; b = i0; c = i1; }
                    sa = -1;
                }
                else
                {
                    if (s0 > 0)      { a = i0; b = i1; c = i2; }
                    else if (s1 > 0) { a = i1; b = i2; c = i0; }
                    else             { a = i2; b = i0; c = i1; }
                    sa = +1;
                }

                int capAB = CapVertex(a, b);
                int capCA = CapVertex(c, a);
                if (capAB == capCA) continue; // degenerate cut (zero-length cap edge)

                if (sa > 0)
                {
                    int pA  = Body(a, pos, posBodyMap);
                    int pAB = CutBody(a, b, pos, posCutCache, posBodyMap);
                    int pCA = CutBody(c, a, pos, posCutCache, posBodyMap);
                    EmitTri(pos, pA, pAB, pCA);

                    int nAB = CutBody(a, b, neg, negCutCache, negBodyMap);
                    int nB  = Body(b, neg, negBodyMap);
                    int nC  = Body(c, neg, negBodyMap);
                    int nCA = CutBody(c, a, neg, negCutCache, negBodyMap);
                    EmitTri(neg, nAB, nB, nC);
                    EmitTri(neg, nAB, nC, nCA);

                    capEdges.Add(new int2(capAB, capCA));
                }
                else
                {
                    int nA  = Body(a, neg, negBodyMap);
                    int nAB = CutBody(a, b, neg, negCutCache, negBodyMap);
                    int nCA = CutBody(c, a, neg, negCutCache, negBodyMap);
                    EmitTri(neg, nA, nAB, nCA);

                    int pAB = CutBody(a, b, pos, posCutCache, posBodyMap);
                    int pB  = Body(b, pos, posBodyMap);
                    int pC  = Body(c, pos, posBodyMap);
                    int pCA = CutBody(c, a, pos, posCutCache, posBodyMap);
                    EmitTri(pos, pAB, pB, pC);
                    EmitTri(pos, pAB, pC, pCA);

                    capEdges.Add(new int2(capCA, capAB));
                }
            }

            if (capPos.Count > 0 && capEdges.Count > 0)
            {
                WeldCapPoints(capPos, capEdges, out var weldedPos, out var weldedEdges);
                if (weldedPos.Count > 0 && weldedEdges.Count > 0)
                    BuildCaps(plane, weldedPos, weldedEdges, pos, neg);
            }

            return new SliceResult(MakeMesh(pos, "Positive"), MakeMesh(neg, "Negative"));
        }

        static Mesh MakeMesh(Side s, string name)
        {
            if (s.Tri.Count == 0) return null;
            var m = new Mesh { name = name };
            m.indexFormat = s.Pos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(s.Pos);
            m.SetNormals(s.Nrm);
            m.SetTangents(s.Tan);
            m.SetUVs(0, s.Uv);
            m.SetTriangles(s.Tri, 0);
            m.RecalculateBounds();
            return m;
        }

        // ---------- Cap construction ----------

        static void BuildCaps(Plane plane, List<float3> capPos, List<int2> capEdges,
                              Side pos, Side neg)
        {
            float3 nn = plane.Normal;
            float3 helper = math.abs(nn.x) > 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            float3 u = math.normalize(math.cross(nn, helper));
            float3 v = math.cross(nn, u);

            int N = capPos.Count;
            var p2d = new Vector2[N];
            for (int i = 0; i < N; i++)
            {
                float3 p = capPos[i];
                p2d[i] = new Vector2(math.dot(p, u), math.dot(p, v));
            }

            var nextOf = new Dictionary<int, int>(capEdges.Count);
            foreach (var e in capEdges)
                if (!nextOf.ContainsKey(e.x)) nextOf[e.x] = e.y;

            var loops = new List<List<int>>();
            var visited = new HashSet<int>();
            foreach (var startKey in nextOf.Keys)
            {
                if (visited.Contains(startKey)) continue;
                var loop = new List<int>();
                int cur = startKey;
                while (true)
                {
                    if (visited.Contains(cur)) break;
                    visited.Add(cur);
                    loop.Add(cur);
                    if (!nextOf.TryGetValue(cur, out var nx)) break;
                    cur = nx;
                    if (cur == startKey) break;
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            if (loops.Count == 0) return;

            int L = loops.Count;
            var areas = new float[L];
            for (int i = 0; i < L; i++) areas[i] = SignedArea(loops[i], p2d);

            // Classify loops by nesting level (how many other loops contain a sample
            // point of this loop). Even nesting = outer, odd = hole. Robust to the
            // input mesh's winding convention.
            var nesting = new int[L];
            for (int i = 0; i < L; i++)
            {
                Vector2 test = p2d[loops[i][0]];
                int level = 0;
                for (int j = 0; j < L; j++)
                {
                    if (i == j) continue;
                    if (PointInPolygon(test, loops[j], p2d)) level++;
                }
                nesting[i] = level;
            }

            var outers = new List<int>();
            for (int i = 0; i < L; i++) if ((nesting[i] & 1) == 0) outers.Add(i);

            var holesPerOuter = new Dictionary<int, List<int>>();
            foreach (var oi in outers) holesPerOuter[oi] = new List<int>();
            for (int i = 0; i < L; i++)
            {
                if ((nesting[i] & 1) == 0) continue;
                Vector2 test = p2d[loops[i][0]];
                int bestOuter = -1; float bestArea = float.MaxValue;
                foreach (var oi in outers)
                {
                    if (PointInPolygon(test, loops[oi], p2d))
                    {
                        float ar = Mathf.Abs(areas[oi]);
                        if (ar < bestArea) { bestArea = ar; bestOuter = oi; }
                    }
                }
                if (bestOuter >= 0) holesPerOuter[bestOuter].Add(i);
            }

            foreach (var oi in outers)
            {
                var outerLoop = areas[oi] > 0 ? loops[oi] : Reversed(loops[oi]);
                var holeLoops = new List<List<int>>();
                foreach (var hi in holesPerOuter[oi])
                    holeLoops.Add(SignedArea(loops[hi], p2d) < 0 ? loops[hi] : Reversed(loops[hi]));

                var simple = CutHoles(outerLoop, holeLoops, p2d);
                var triIdx = EarClip(simple, p2d);
                EmitCap(plane, capPos, triIdx, pos, neg);
            }
        }

        static List<int> Reversed(List<int> src)
        {
            var r = new List<int>(src);
            r.Reverse();
            return r;
        }

        // Spatially weld cap points within an epsilon. Source vertices that lie
        // exactly on the cut plane spawn multiple coincident cap points (one per
        // incident triangle); without welding, loop walking fragments into many
        // tiny pieces. Naive O(n²) — fine for correctness pass.
        static void WeldCapPoints(List<float3> srcPos, List<int2> srcEdges,
                                  out List<float3> outPos, out List<int2> outEdges)
        {
            const float kWeldEps = 1e-5f;
            const float kWeldEpsSq = kWeldEps * kWeldEps;

            int n = srcPos.Count;
            var remap = new int[n];
            outPos = new List<float3>(n);
            for (int i = 0; i < n; i++)
            {
                int found = -1;
                float3 p = srcPos[i];
                for (int j = 0; j < outPos.Count; j++)
                {
                    if (math.distancesq(p, outPos[j]) < kWeldEpsSq) { found = j; break; }
                }
                if (found < 0) { remap[i] = outPos.Count; outPos.Add(p); }
                else remap[i] = found;
            }

            outEdges = new List<int2>(srcEdges.Count);
            foreach (var e in srcEdges)
            {
                int a = remap[e.x], b = remap[e.y];
                if (a != b) outEdges.Add(new int2(a, b));
            }
        }

        static float SignedArea(List<int> loop, Vector2[] p2d)
        {
            float s = 0;
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                var a = p2d[loop[i]]; var b = p2d[loop[(i + 1) % n]];
                s += a.x * b.y - b.x * a.y;
            }
            return s * 0.5f;
        }

        static bool PointInPolygon(Vector2 p, List<int> loop, Vector2[] p2d)
        {
            bool inside = false;
            int n = loop.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = p2d[loop[i]], b = p2d[loop[j]];
                if (((a.y > p.y) != (b.y > p.y)) &&
                    (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x))
                    inside = !inside;
            }
            return inside;
        }

        // Stitch holes into outer via bridge edges (Mapbox-earcut style).
        static List<int> CutHoles(List<int> outer, List<List<int>> holes, Vector2[] p2d)
        {
            if (holes.Count == 0) return new List<int>(outer);

            var ordered = new List<List<int>>(holes);
            ordered.Sort((a, b) => MaxX(b, p2d).CompareTo(MaxX(a, p2d)));

            var result = new List<int>(outer);
            foreach (var hole in ordered)
            {
                int holeStart = 0; float maxX = float.MinValue;
                for (int i = 0; i < hole.Count; i++)
                    if (p2d[hole[i]].x > maxX) { maxX = p2d[hole[i]].x; holeStart = i; }
                int bridge = FindVisibleOuter(p2d[hole[holeStart]], result, p2d);
                var stitched = new List<int>(result.Count + hole.Count + 2);
                for (int i = 0; i <= bridge; i++) stitched.Add(result[i]);
                for (int i = 0; i < hole.Count; i++) stitched.Add(hole[(holeStart + i) % hole.Count]);
                stitched.Add(hole[holeStart]);
                stitched.Add(result[bridge]);
                for (int i = bridge + 1; i < result.Count; i++) stitched.Add(result[i]);
                result = stitched;
            }
            return result;
        }

        static float MaxX(List<int> loop, Vector2[] p2d)
        {
            float m = float.MinValue;
            foreach (var i in loop) if (p2d[i].x > m) m = p2d[i].x;
            return m;
        }

        // Find outer vertex visible from M by ray-casting +x and returning the edge endpoint
        // with larger x (per Mapbox earcut). Good enough for the naive impl.
        static int FindVisibleOuter(Vector2 M, List<int> outer, Vector2[] p2d)
        {
            float bestX = float.MaxValue; int bestEdge = -1;
            int n = outer.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = p2d[outer[i]], b = p2d[outer[(i + 1) % n]];
                if ((a.y > M.y) == (b.y > M.y)) continue;
                if (a.y == b.y) continue;
                float t = (M.y - a.y) / (b.y - a.y);
                float xCross = a.x + t * (b.x - a.x);
                if (xCross < M.x) continue;
                if (xCross < bestX) { bestX = xCross; bestEdge = i; }
            }
            if (bestEdge < 0) return outer.Count - 1;
            int e1 = bestEdge, e2 = (bestEdge + 1) % n;
            return p2d[outer[e1]].x > p2d[outer[e2]].x ? e1 : e2;
        }

        static List<int> EarClip(List<int> poly, Vector2[] p2d)
        {
            var indices = new List<int>(poly);
            var tris = new List<int>((indices.Count - 2) * 3);
            int guard = indices.Count * indices.Count + 4;
            while (indices.Count > 3 && guard-- > 0)
            {
                bool found = false;
                for (int i = 0; i < indices.Count; i++)
                {
                    int prev = indices[(i - 1 + indices.Count) % indices.Count];
                    int cur  = indices[i];
                    int next = indices[(i + 1) % indices.Count];
                    if (IsEar(prev, cur, next, indices, p2d))
                    {
                        tris.Add(prev); tris.Add(cur); tris.Add(next);
                        indices.RemoveAt(i);
                        found = true;
                        break;
                    }
                }
                if (!found) break;
            }
            if (indices.Count == 3)
            {
                tris.Add(indices[0]); tris.Add(indices[1]); tris.Add(indices[2]);
            }
            return tris;
        }

        static bool IsEar(int a, int b, int c, List<int> poly, Vector2[] p2d)
        {
            Vector2 A = p2d[a], B = p2d[b], C = p2d[c];
            float cross = (B.x - A.x) * (C.y - A.y) - (B.y - A.y) * (C.x - A.x);
            if (cross <= 0f) return false; // non-convex corner in CCW poly
            for (int k = 0; k < poly.Count; k++)
            {
                int p = poly[k];
                if (p == a || p == b || p == c) continue;
                if (PointInTri(p2d[p], A, B, C)) return false;
            }
            return true;
        }

        static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        static void EmitCap(Plane plane, List<float3> capPos, List<int> tris,
                            Side pos, Side neg)
        {
            if (tris.Count == 0) return;
            float3 nn = plane.Normal;
            float3 helper = math.abs(nn.x) > 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            float3 u = math.normalize(math.cross(nn, helper));
            Vector4 tan = new Vector4(u.x, u.y, u.z, 1f);
            Vector3 nP = -(Vector3)nn;
            Vector3 nN =  (Vector3)nn;

            var posMap = new Dictionary<int, int>();
            var negMap = new Dictionary<int, int>();

            int GetP(int i)
            {
                if (posMap.TryGetValue(i, out var x)) return x;
                x = pos.Add((Vector3)capPos[i], nP, tan, Vector2.zero);
                posMap[i] = x; return x;
            }
            int GetN(int i)
            {
                if (negMap.TryGetValue(i, out var x)) return x;
                x = neg.Add((Vector3)capPos[i], nN, tan, Vector2.zero);
                negMap[i] = x; return x;
            }

            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                neg.Tri.Add(GetN(a)); neg.Tri.Add(GetN(b)); neg.Tri.Add(GetN(c));
                pos.Tri.Add(GetP(a)); pos.Tri.Add(GetP(c)); pos.Tri.Add(GetP(b));
            }
        }
    }
}
