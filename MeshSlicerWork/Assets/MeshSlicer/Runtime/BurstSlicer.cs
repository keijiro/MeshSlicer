using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer {

// Burst + Jobs + NativeArray implementation of the plane slicer. Reads the
// source through the Advanced Mesh API (MeshData) and writes the results with
// SetVertexBufferData / SetIndexBufferData to avoid managed array round-trips.
public static class BurstSlicer
{
    // Allocating API: returns two freshly created meshes (either may be null
    // when the source lies entirely on one side).
    public static SliceResult Slice(Mesh source, Plane plane)
    {
        var pv = new NativeList<SliceVertex>(Allocator.TempJob);
        var pi = new NativeList<int>(Allocator.TempJob);
        var nv = new NativeList<SliceVertex>(Allocator.TempJob);
        var ni = new NativeList<int>(Allocator.TempJob);

        Compute(source, plane, pv, pi, nv, ni);
        var posMesh = pi.Length > 0 ? Fill(new Mesh(), pv, pi, source.name + "_Positive") : null;
        var negMesh = ni.Length > 0 ? Fill(new Mesh(), nv, ni, source.name + "_Negative") : null;

        pv.Dispose(); pi.Dispose(); nv.Dispose(); ni.Dispose();
        return new SliceResult(posMesh, negMesh);
    }

    // Non-allocating API for per-frame use: rewrites the two provided meshes in
    // place (a mesh is cleared when its side is empty). Avoids Mesh allocation
    // and buffer churn across frames.
    public static void Slice(Mesh source, Plane plane, Mesh positive, Mesh negative)
    {
        if (positive == null) throw new ArgumentNullException(nameof(positive));
        if (negative == null) throw new ArgumentNullException(nameof(negative));

        var pv = new NativeList<SliceVertex>(Allocator.TempJob);
        var pi = new NativeList<int>(Allocator.TempJob);
        var nv = new NativeList<SliceVertex>(Allocator.TempJob);
        var ni = new NativeList<int>(Allocator.TempJob);

        Compute(source, plane, pv, pi, nv, ni);
        if (pi.Length > 0) Fill(positive, pv, pi, positive.name); else positive.Clear();
        if (ni.Length > 0) Fill(negative, nv, ni, negative.name); else negative.Clear();

        pv.Dispose(); pi.Dispose(); nv.Dispose(); ni.Dispose();
    }

    static void Compute(Mesh source, Plane plane,
                        NativeList<SliceVertex> posV, NativeList<int> posI,
                        NativeList<SliceVertex> negV, NativeList<int> negI)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        using var mda = Mesh.AcquireReadOnlyMeshData(source);
        var data = mda[0];
        var vc = data.vertexCount;

        var pos = new NativeArray<float3>(vc, Allocator.TempJob);
        data.GetVertices(pos.Reinterpret<Vector3>());

        var nrm = new NativeArray<float3>(vc, Allocator.TempJob);
        if (data.HasVertexAttribute(VertexAttribute.Normal))
            data.GetNormals(nrm.Reinterpret<Vector3>());
        else for (var i = 0; i < vc; i++) nrm[i] = new float3(0, 1, 0);

        var tan = new NativeArray<float4>(vc, Allocator.TempJob);
        if (data.HasVertexAttribute(VertexAttribute.Tangent))
            data.GetTangents(tan.Reinterpret<Vector4>());
        else for (var i = 0; i < vc; i++) tan[i] = new float4(1, 0, 0, 1);

        var uv = new NativeArray<float2>(vc, Allocator.TempJob);
        if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
            data.GetUVs(0, uv.Reinterpret<Vector2>());
        else for (var i = 0; i < vc; i++) uv[i] = float2.zero;

        var total = 0;
        for (var s = 0; s < data.subMeshCount; s++) total += data.GetSubMesh(s).indexCount;
        var idx = new NativeArray<int>(total, Allocator.TempJob);
        var off = 0;
        for (var s = 0; s < data.subMeshCount; s++)
        {
            var sc = data.GetSubMesh(s).indexCount;
            data.GetIndices(idx.GetSubArray(off, sc), s);
            off += sc;
        }

        var eps = math.length((float3)source.bounds.size) * 1e-4f;
        var planeEq = new float4(plane.Normal, plane.SignedDistanceToPoint(float3.zero));

        new SliceJob
        {
            Pos = pos, Nrm = nrm, Tan = tan, Uv = uv, Indices = idx,
            PlaneEq = planeEq, Eps = eps,
            PosV = posV, PosI = posI, NegV = negV, NegI = negI
        }.Run();

        pos.Dispose(); nrm.Dispose(); tan.Dispose(); uv.Dispose(); idx.Dispose();
    }

    static Mesh Fill(Mesh mesh, NativeList<SliceVertex> v, NativeList<int> i, string name)
    {
        mesh.name = name;
        mesh.Clear();
        mesh.SetVertexBufferParams(v.Length, SliceVertex.Layout);
        mesh.SetVertexBufferData(v.AsArray(), 0, 0, v.Length);
        mesh.SetIndexBufferParams(i.Length, IndexFormat.UInt32);
        mesh.SetIndexBufferData(i.AsArray(), 0, 0, i.Length);
        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, i.Length));
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---- Job ------------------------------------------------------------

    struct PV
    {
        public bool IsCut;
        public int Src;
        public long Edge;
        public int Boundary;
        public SliceVertex V;
    }

    struct NativeBuilder
    {
        public NativeList<SliceVertex> V;
        public NativeList<int> I;
        public NativeHashMap<int, int> OrigMap;
        public NativeHashMap<long, int> WallMap;
        public NativeHashMap<int, int> CapMap;

        public int AddOriginal(int src, in SliceVertex v)
        {
            if (OrigMap.TryGetValue(src, out var i)) return i;
            i = V.Length; V.Add(v); OrigMap.Add(src, i); return i;
        }

        public int AddWall(long edge, in SliceVertex v)
        {
            if (WallMap.TryGetValue(edge, out var i)) return i;
            i = V.Length; V.Add(v); WallMap.Add(edge, i); return i;
        }

        public int AddCap(int b, in SliceVertex v)
        {
            if (CapMap.TryGetValue(b, out var i)) return i;
            i = V.Length; V.Add(v); CapMap.Add(b, i); return i;
        }

        public void Tri(int a, int b, int c) { I.Add(a); I.Add(b); I.Add(c); }
    }

    [BurstCompile]
    struct SliceJob : IJob
    {
        const float WeldGrid = 1e-5f;

        [ReadOnly] public NativeArray<float3> Pos;
        [ReadOnly] public NativeArray<float3> Nrm;
        [ReadOnly] public NativeArray<float4> Tan;
        [ReadOnly] public NativeArray<float2> Uv;
        [ReadOnly] public NativeArray<int> Indices;
        public float4 PlaneEq;
        public float Eps;

        public NativeList<SliceVertex> PosV, NegV;
        public NativeList<int> PosI, NegI;

        // Boundary-point welding (cut vertices deduplicated by position).
        struct Weld
        {
            public NativeHashMap<int3, int> Map;
            public NativeList<float3> Pos;

            public int Get(float3 p)
            {
                var key = (int3)math.round(p / WeldGrid);
                if (Map.TryGetValue(key, out var id)) return id;
                id = Pos.Length;
                Map.Add(key, id);
                Pos.Add(p);
                return id;
            }
        }

        public void Execute()
        {
            var vc = Pos.Length;
            var dist = new NativeArray<float>(vc, Allocator.Temp);
            for (var i = 0; i < vc; i++)
            {
                var d = math.dot(PlaneEq.xyz, Pos[i]) + PlaneEq.w;
                dist[i] = math.abs(d) < Eps ? 0f : d;
            }

            var weld = new Weld
            {
                Map = new NativeHashMap<int3, int>(64, Allocator.Temp),
                Pos = new NativeList<float3>(64, Allocator.Temp)
            };

            var pos = NewBuilder(PosV, PosI);
            var neg = NewBuilder(NegV, NegI);
            var capPos = new NativeList<int2>(Allocator.Temp);
            var capNeg = new NativeList<int2>(Allocator.Temp);
            var pp = new NativeList<PV>(Allocator.Temp);
            var np = new NativeList<PV>(Allocator.Temp);

            for (var t = 0; t < Indices.Length; t += 3)
            {
                int i0 = Indices[t], i1 = Indices[t + 1], i2 = Indices[t + 2];
                float d0 = dist[i0], d1 = dist[i1], d2 = dist[i2];
                bool p0 = d0 >= 0, p1 = d1 >= 0, p2 = d2 >= 0;

                if (p0 && p1 && p2) { EmitWhole(ref pos, i0, i1, i2); continue; }
                if (!p0 && !p1 && !p2) { EmitWhole(ref neg, i0, i1, i2); continue; }

                pp.Clear(); np.Clear();
                ClipEdge(pp, np, i0, i1, d0, d1, ref weld);
                ClipEdge(pp, np, i1, i2, d1, d2, ref weld);
                ClipEdge(pp, np, i2, i0, d2, d0, ref weld);
                FanEmit(ref pos, pp);
                FanEmit(ref neg, np);
                ExtractCap(pp, capPos);
                ExtractCap(np, capNeg);
            }

            var n = math.normalize(PlaneEq.xyz);
            var refv = math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            var u = math.normalize(math.cross(refv, n));
            var w = math.cross(n, u);
            BuildCap(ref pos, capPos, u, w, n, weld.Pos);
            BuildCap(ref neg, capNeg, u, w, n, weld.Pos);
        }

        NativeBuilder NewBuilder(NativeList<SliceVertex> v, NativeList<int> i) => new NativeBuilder
        {
            V = v, I = i,
            OrigMap = new NativeHashMap<int, int>(64, Allocator.Temp),
            WallMap = new NativeHashMap<long, int>(64, Allocator.Temp),
            CapMap = new NativeHashMap<int, int>(64, Allocator.Temp)
        };

        SliceVertex VtxOf(int i) => new SliceVertex
        {
            Position = Pos[i], Normal = Nrm[i], Tangent = Tan[i], Uv = Uv[i]
        };

        PV Original(int i, bool onPlane, ref Weld weld) => new PV
        {
            IsCut = false, Src = i, Boundary = onPlane ? weld.Get(Pos[i]) : -1, V = VtxOf(i)
        };

        PV Interp(int a, int b, float s, ref Weld weld)
        {
            var tan = math.lerp(Tan[a], Tan[b], s);
            var txyz = math.normalizesafe(tan.xyz, new float3(1, 0, 0));
            var p = math.lerp(Pos[a], Pos[b], s);
            var v = new SliceVertex
            {
                Position = p,
                Normal = math.normalizesafe(math.lerp(Nrm[a], Nrm[b], s), new float3(0, 1, 0)),
                Tangent = new float4(txyz, Tan[a].w),
                Uv = math.lerp(Uv[a], Uv[b], s)
            };
            return new PV { IsCut = true, Edge = EdgeKey(a, b), Boundary = weld.Get(p), V = v };
        }

        void EmitWhole(ref NativeBuilder buf, int i0, int i1, int i2)
        {
            var a = buf.AddOriginal(i0, VtxOf(i0));
            var b = buf.AddOriginal(i1, VtxOf(i1));
            var c = buf.AddOriginal(i2, VtxOf(i2));
            buf.Tri(a, b, c);
        }

        void ClipEdge(NativeList<PV> pp, NativeList<PV> np, int a, int b, float da, float db, ref Weld weld)
        {
            if (da > 0) pp.Add(Original(a, false, ref weld));
            else if (da < 0) np.Add(Original(a, false, ref weld));
            else { var v = Original(a, true, ref weld); pp.Add(v); np.Add(v); }

            if (da * db >= 0) return;

            var s = da / (da - db);
            var ip = Interp(a, b, s, ref weld);
            pp.Add(ip); np.Add(ip);
        }

        void FanEmit(ref NativeBuilder buf, NativeList<PV> poly)
        {
            if (poly.Length < 3) return;
            var i0 = Resolve(ref buf, poly[0]);
            for (var k = 1; k < poly.Length - 1; k++)
            {
                var i1 = Resolve(ref buf, poly[k]);
                var i2 = Resolve(ref buf, poly[k + 1]);
                buf.Tri(i0, i1, i2);
            }
        }

        static int Resolve(ref NativeBuilder buf, in PV v) =>
            v.IsCut ? buf.AddWall(v.Edge, v.V) : buf.AddOriginal(v.Src, v.V);

        static void ExtractCap(NativeList<PV> poly, NativeList<int2> caps)
        {
            var m = poly.Length;
            if (m < 3) return;
            for (var i = 0; i < m; i++)
            {
                var c = poly[i];
                var d = poly[(i + 1) % m];
                if (c.Boundary >= 0 && d.Boundary >= 0 && c.Boundary != d.Boundary)
                    caps.Add(new int2(d.Boundary, c.Boundary));
            }
        }

        static long EdgeKey(int a, int b)
        {
            int lo = math.min(a, b), hi = math.max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        void BuildCap(ref NativeBuilder buf, NativeList<int2> caps, float3 u, float3 w, float3 n,
                      NativeList<float3> boundaryPos)
        {
            if (caps.Length == 0) return;
            var bc = boundaryPos.Length;
            var next = new NativeArray<int>(bc, Allocator.Temp);
            for (var i = 0; i < bc; i++) next[i] = -1;
            for (var i = 0; i < caps.Length; i++) next[caps[i].x] = caps[i].y;

            var visited = new NativeArray<bool>(bc, Allocator.Temp);
            var loop = new NativeList<int>(Allocator.Temp);
            var pts = new NativeList<float2>(Allocator.Temp);
            var tris = new NativeList<int>(Allocator.Temp);
            var capTan = new float4(u, 1f);

            for (var start = 0; start < bc; start++)
            {
                if (next[start] < 0 || visited[start]) continue;

                loop.Clear();
                var cur = start;
                while (cur >= 0 && !visited[cur])
                {
                    visited[cur] = true;
                    loop.Add(cur);
                    cur = next[cur];
                    if (cur == start) break;
                }
                if (loop.Length < 3) continue;

                pts.Clear();
                for (var i = 0; i < loop.Length; i++)
                {
                    var p = boundaryPos[loop[i]];
                    pts.Add(new float2(math.dot(p, u), math.dot(p, w)));
                }

                tris.Clear();
                EarClip(pts, tris);
                var loopCcw = SignedArea(pts) > 0f;
                var capNrm = loopCcw ? n : -n;

                for (var i = 0; i < tris.Length; i += 3)
                {
                    int ka = loop[tris[i]], kb = loop[tris[i + 1]], kc = loop[tris[i + 2]];
                    var la = buf.AddCap(ka, CapVtx(boundaryPos[ka], capNrm, capTan));
                    var lb = buf.AddCap(kb, CapVtx(boundaryPos[kb], capNrm, capTan));
                    var lc = buf.AddCap(kc, CapVtx(boundaryPos[kc], capNrm, capTan));
                    if (loopCcw) buf.Tri(la, lb, lc);
                    else buf.Tri(la, lc, lb);
                }
            }
        }

        static SliceVertex CapVtx(float3 p, float3 n, float4 tan) => new SliceVertex
        {
            Position = p, Normal = n, Tangent = tan, Uv = float2.zero
        };

        // ---- Ear clipping (2D) ------------------------------------------

        static void EarClip(NativeList<float2> pts, NativeList<int> outTris)
        {
            var n = pts.Length;
            if (n < 3) return;

            var v = new NativeList<int>(n, Allocator.Temp);
            for (var i = 0; i < n; i++) v.Add(i);
            if (SignedArea(pts) < 0)
            {
                for (int i = 0, j = v.Length - 1; i < j; i++, j--)
                    (v[i], v[j]) = (v[j], v[i]);
            }

            var guard = 0;
            var maxGuard = n * n + 8;
            while (v.Length > 3 && guard++ < maxGuard)
            {
                var eared = false;
                var count = v.Length;
                for (var i = 0; i < count; i++)
                {
                    int i0 = v[(i - 1 + count) % count], i1 = v[i], i2 = v[(i + 1) % count];
                    if (!IsEar(pts, v, i0, i1, i2)) continue;
                    outTris.Add(i0); outTris.Add(i1); outTris.Add(i2);
                    v.RemoveAt(i);
                    eared = true;
                    break;
                }
                if (!eared) break;
            }
            if (v.Length == 3) { outTris.Add(v[0]); outTris.Add(v[1]); outTris.Add(v[2]); }
        }

        static bool IsEar(NativeList<float2> pts, NativeList<int> ring, int i0, int i1, int i2)
        {
            float2 a = pts[i0], b = pts[i1], c = pts[i2];
            if (Cross(b - a, c - a) <= 0f) return false;
            for (var k = 0; k < ring.Length; k++)
            {
                var idx = ring[k];
                if (idx == i0 || idx == i1 || idx == i2) continue;
                if (PointInTriangle(pts[idx], a, b, c)) return false;
            }
            return true;
        }

        static float Cross(float2 a, float2 b) => a.x * b.y - a.y * b.x;

        static bool PointInTriangle(float2 p, float2 a, float2 b, float2 c)
        {
            var d1 = Cross(p - a, b - a);
            var d2 = Cross(p - b, c - b);
            var d3 = Cross(p - c, a - c);
            var hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            var hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }

        static float SignedArea(NativeList<float2> pts)
        {
            var a = 0f;
            for (int i = 0, m = pts.Length; i < m; i++)
            {
                float2 p = pts[i], q = pts[(i + 1) % m];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5f;
        }
    }
}

} // namespace MeshSlicer
