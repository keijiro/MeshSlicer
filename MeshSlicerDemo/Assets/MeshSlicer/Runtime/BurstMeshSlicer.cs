using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer
{
    // Burst + advanced-mesh-API slicer. Same SliceResult contract as NaiveMeshSlicer.
    //
    // Pipeline:
    //   1. Read source via Mesh.AcquireReadOnlyMeshData (zero-copy).
    //   2. Per-vertex signed distance (Burst job).
    //   3. Per-triangle body emission + cap-edge collection (single Burst job — sequential
    //      because per-side buffers are append-only, but Burst-compiled to keep hot path
    //      branch-free).
    //   4. Cap point welding + loop walking + ear-clip + emission (managed; cap polygon
    //      vertex count is O(sqrt(n)) so it's tiny next to the body work).
    //   5. Output via Mesh.AllocateWritableMeshData / ApplyAndDisposeWritableMeshData.
    public static class BurstMeshSlicer
    {
        // Output vertex layout used by both halves. Tightly packed so the Burst body job
        // can write each interleaved vertex with one store.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Vert
        {
            public float3 Pos;
            public float3 Nrm;
            public float4 Tan;
            public float2 Uv;
        }

        static readonly VertexAttributeDescriptor[] kAttrs =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position,  VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal,    VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Tangent,   VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2),
        };

        public static SliceResult Slice(Mesh source, Plane plane)
        {
            plane = Plane.Normalize(plane);

            using var srcData = Mesh.AcquireReadOnlyMeshData(source);
            var sd = srcData[0];
            int srcVertCount = sd.vertexCount;
            int srcIdxCount = (int)sd.GetSubMesh(0).indexCount;
            int srcTriCount = srcIdxCount / 3;

            var srcPos = new NativeArray<Vector3>(srcVertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcNrm = new NativeArray<Vector3>(srcVertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcTan = new NativeArray<Vector4>(srcVertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcUv  = new NativeArray<Vector2>(srcVertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var srcTri = new NativeArray<int>(srcIdxCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            sd.GetVertices(srcPos);
            if (sd.HasVertexAttribute(VertexAttribute.Normal))    sd.GetNormals(srcNrm);    else FillZero(srcNrm);
            if (sd.HasVertexAttribute(VertexAttribute.Tangent))   sd.GetTangents(srcTan);   else FillZero(srcTan);
            if (sd.HasVertexAttribute(VertexAttribute.TexCoord0)) sd.GetUVs(0, srcUv);      else FillZero(srcUv);
            if (sd.indexFormat == IndexFormat.UInt32)
            {
                var s = sd.GetIndexData<int>();
                NativeArray<int>.Copy(s, srcTri, srcIdxCount);
            }
            else
            {
                var s = sd.GetIndexData<ushort>();
                for (int i = 0; i < srcIdxCount; i++) srcTri[i] = s[i];
            }

            // Phase 1: distance + sign.
            var dist = new NativeArray<float>(srcVertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var sign = new NativeArray<sbyte>(srcVertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var distJob = new DistanceJob
            {
                pos = srcPos.Reinterpret<float3>(),
                planeNormal = plane.Normal,
                planeD = plane.NormalAndDistance.w,
                dist = dist,
                sign = sign,
            };
            var distHandle = distJob.Schedule(srcVertCount, 256);

            // Allocate output buffers. Worst case: every triangle splits, producing at
            // most 4 verts + 4 indices on each side (3 cuts + original) per source tri.
            int maxOutVerts = srcVertCount + srcTriCount * 4;
            int maxOutIdx = srcTriCount * 6;
            int maxCap = srcTriCount * 2;

            var pV = new NativeList<Vert>(maxOutVerts, Allocator.TempJob);
            var pT = new NativeList<int>(maxOutIdx, Allocator.TempJob);
            var nV = new NativeList<Vert>(maxOutVerts, Allocator.TempJob);
            var nT = new NativeList<int>(maxOutIdx, Allocator.TempJob);
            var capPos = new NativeList<float3>(maxCap, Allocator.TempJob);
            var capEdges = new NativeList<int2>(maxCap, Allocator.TempJob);

            var pBodyMap = new NativeArray<int>(srcVertCount, Allocator.TempJob);
            var nBodyMap = new NativeArray<int>(srcVertCount, Allocator.TempJob);
            FillNegOne(pBodyMap); FillNegOne(nBodyMap);

            var capByEdge = new NativeParallelHashMap<long, int>(maxCap, Allocator.TempJob);

            distHandle.Complete();

            var bodyJob = new BodyJob
            {
                srcPos = srcPos.Reinterpret<float3>(),
                srcNrm = srcNrm.Reinterpret<float3>(),
                srcTan = srcTan.Reinterpret<float4>(),
                srcUv  = srcUv.Reinterpret<float2>(),
                srcTri = srcTri,
                dist = dist,
                sign = sign,
                pV = pV, pT = pT, nV = nV, nT = nT,
                pBodyMap = pBodyMap, nBodyMap = nBodyMap,
                capByEdge = capByEdge,
                capPos = capPos, capEdges = capEdges,
            };
            bodyJob.Schedule().Complete();

            // Phase 3: cap building (managed).
            BuildCapsManaged(plane, capPos, capEdges, pV, pT, nV, nT);

            // Phase 4: build meshes via MeshDataArray.
            int outMeshCount = (pT.Length > 0 ? 1 : 0) + (nT.Length > 0 ? 1 : 0);
            Mesh meshPos = null, meshNeg = null;
            if (outMeshCount > 0)
            {
                var outArr = Mesh.AllocateWritableMeshData(outMeshCount);
                int idx = 0;
                if (pT.Length > 0)
                {
                    meshPos = new Mesh { name = "Positive" };
                    FillMeshData(outArr[idx++], pV, pT);
                }
                if (nT.Length > 0)
                {
                    meshNeg = new Mesh { name = "Negative" };
                    FillMeshData(outArr[idx++], nV, nT);
                }
                var meshes = new Mesh[outMeshCount];
                int mi = 0;
                if (meshPos != null) meshes[mi++] = meshPos;
                if (meshNeg != null) meshes[mi++] = meshNeg;
                Mesh.ApplyAndDisposeWritableMeshData(outArr, meshes,
                    MeshUpdateFlags.DontValidateIndices |
                    MeshUpdateFlags.DontNotifyMeshUsers |
                    MeshUpdateFlags.DontRecalculateBounds);
                if (meshPos != null) meshPos.RecalculateBounds();
                if (meshNeg != null) meshNeg.RecalculateBounds();
            }

            srcPos.Dispose(); srcNrm.Dispose(); srcTan.Dispose(); srcUv.Dispose(); srcTri.Dispose();
            dist.Dispose(); sign.Dispose();
            pV.Dispose(); pT.Dispose(); nV.Dispose(); nT.Dispose();
            capPos.Dispose(); capEdges.Dispose();
            pBodyMap.Dispose(); nBodyMap.Dispose();
            capByEdge.Dispose();

            return new SliceResult(meshPos, meshNeg);
        }

        static void FillMeshData(Mesh.MeshData md, NativeList<Vert> verts, NativeList<int> tris)
        {
            md.SetVertexBufferParams(verts.Length, kAttrs);
            var dst = md.GetVertexData<Vert>();
            NativeArray<Vert>.Copy(verts.AsArray(), dst, verts.Length);

            bool needs32 = verts.Length > 65535;
            md.SetIndexBufferParams(tris.Length, needs32 ? IndexFormat.UInt32 : IndexFormat.UInt16);
            if (needs32)
            {
                var dstI = md.GetIndexData<int>();
                NativeArray<int>.Copy(tris.AsArray(), dstI, tris.Length);
            }
            else
            {
                var dstI = md.GetIndexData<ushort>();
                for (int i = 0; i < tris.Length; i++) dstI[i] = (ushort)tris[i];
            }
            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, tris.Length),
                MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
        }

        static void FillZero<T>(NativeArray<T> a) where T : struct
        {
            unsafe
            {
                UnsafeUtility.MemClear(a.GetUnsafePtr(), (long)a.Length * UnsafeUtility.SizeOf<T>());
            }
        }

        static void FillNegOne(NativeArray<int> a)
        {
            for (int i = 0; i < a.Length; i++) a[i] = -1;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct DistanceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> pos;
            public float3 planeNormal;
            public float planeD;
            [WriteOnly] public NativeArray<float> dist;
            [WriteOnly] public NativeArray<sbyte> sign;

            public void Execute(int i)
            {
                float d = math.dot(planeNormal, pos[i]) + planeD;
                dist[i] = d;
                sign[i] = d >= 0f ? (sbyte)1 : (sbyte)-1;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct BodyJob : IJob
        {
            [ReadOnly] public NativeArray<float3> srcPos;
            [ReadOnly] public NativeArray<float3> srcNrm;
            [ReadOnly] public NativeArray<float4> srcTan;
            [ReadOnly] public NativeArray<float2> srcUv;
            [ReadOnly] public NativeArray<int> srcTri;
            [ReadOnly] public NativeArray<float> dist;
            [ReadOnly] public NativeArray<sbyte> sign;

            public NativeList<Vert> pV;
            public NativeList<int>  pT;
            public NativeList<Vert> nV;
            public NativeList<int>  nT;

            public NativeArray<int> pBodyMap;
            public NativeArray<int> nBodyMap;

            public NativeParallelHashMap<long, int> capByEdge;
            public NativeList<float3> capPos;
            public NativeList<int2>   capEdges;

            public void Execute()
            {
                int triCount = srcTri.Length;
                for (int t = 0; t < triCount; t += 3)
                {
                    int i0 = srcTri[t], i1 = srcTri[t + 1], i2 = srcTri[t + 2];
                    sbyte s0 = sign[i0], s1 = sign[i1], s2 = sign[i2];
                    int negCount = (s0 < 0 ? 1 : 0) + (s1 < 0 ? 1 : 0) + (s2 < 0 ? 1 : 0);

                    if (negCount == 0)
                    {
                        EmitTri(pT,
                            Body(i0, pV, pBodyMap),
                            Body(i1, pV, pBodyMap),
                            Body(i2, pV, pBodyMap));
                        continue;
                    }
                    if (negCount == 3)
                    {
                        EmitTri(nT,
                            Body(i0, nV, nBodyMap),
                            Body(i1, nV, nBodyMap),
                            Body(i2, nV, nBodyMap));
                        continue;
                    }

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
                    if (capAB == capCA) continue;

                    if (sa > 0)
                    {
                        int pA  = Body(a, pV, pBodyMap);
                        int pAB = CutBody(a, b, pV, pBodyMap);
                        int pCA = CutBody(c, a, pV, pBodyMap);
                        EmitTri(pT, pA, pAB, pCA);

                        int nAB = CutBody(a, b, nV, nBodyMap);
                        int nB  = Body(b, nV, nBodyMap);
                        int nC  = Body(c, nV, nBodyMap);
                        int nCA = CutBody(c, a, nV, nBodyMap);
                        EmitTri(nT, nAB, nB, nC);
                        EmitTri(nT, nAB, nC, nCA);

                        capEdges.Add(new int2(capAB, capCA));
                    }
                    else
                    {
                        int nA  = Body(a, nV, nBodyMap);
                        int nAB = CutBody(a, b, nV, nBodyMap);
                        int nCA = CutBody(c, a, nV, nBodyMap);
                        EmitTri(nT, nA, nAB, nCA);

                        int pAB = CutBody(a, b, pV, pBodyMap);
                        int pB  = Body(b, pV, pBodyMap);
                        int pC  = Body(c, pV, pBodyMap);
                        int pCA = CutBody(c, a, pV, pBodyMap);
                        EmitTri(pT, pAB, pB, pC);
                        EmitTri(pT, pAB, pC, pCA);

                        capEdges.Add(new int2(capCA, capAB));
                    }
                }
            }

            int Body(int v, NativeList<Vert> verts, NativeArray<int> map)
            {
                int i = map[v];
                if (i >= 0) return i;
                i = verts.Length;
                verts.Add(new Vert { Pos = srcPos[v], Nrm = srcNrm[v], Tan = srcTan[v], Uv = srcUv[v] });
                map[v] = i;
                return i;
            }

            int CapVertex(int a, int b)
            {
                long k = Key(a, b);
                if (capByEdge.TryGetValue(k, out var i)) return i;
                float t = dist[a] / (dist[a] - dist[b]);
                float3 p = math.lerp(srcPos[a], srcPos[b], t);
                i = capPos.Length;
                capPos.Add(p);
                capByEdge.TryAdd(k, i);
                return i;
            }

            int CutBody(int a, int b, NativeList<Vert> verts, NativeArray<int> map)
            {
                // No per-side cut cache: each split triangle gets its own fresh body
                // vertex on each cut edge. Possible duplication factor 2x on cut edges,
                // which is negligible vs. total vertex count and avoids hashmap overhead.
                float t = dist[a] / (dist[a] - dist[b]);
                if (t <= 1e-5f) return Body(a, verts, map);
                if (t >= 1f - 1e-5f) return Body(b, verts, map);
                Vert v;
                v.Pos = math.lerp(srcPos[a], srcPos[b], t);
                v.Nrm = math.normalize(math.lerp(srcNrm[a], srcNrm[b], t));
                float4 ta = srcTan[a], tb = srcTan[b];
                float3 tx = math.normalize(math.lerp(ta.xyz, tb.xyz, t));
                v.Tan = new float4(tx, ta.w);
                v.Uv  = math.lerp(srcUv[a], srcUv[b], t);
                int i = verts.Length;
                verts.Add(v);
                return i;
            }

            static long Key(int a, int b)
                => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

            static void EmitTri(NativeList<int> tris, int i0, int i1, int i2)
            {
                if (i0 == i1 || i1 == i2 || i0 == i2) return;
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
            }
        }

        // ----- Managed cap construction (small data, kept readable) -----

        static void BuildCapsManaged(Plane plane,
                                     NativeList<float3> capPosN, NativeList<int2> capEdgesN,
                                     NativeList<Vert> pV, NativeList<int> pT,
                                     NativeList<Vert> nV, NativeList<int> nT)
        {
            if (capPosN.Length == 0 || capEdgesN.Length == 0) return;

            // Weld coincident points.
            const float kEps = 1e-5f, kEpsSq = kEps * kEps;
            int n = capPosN.Length;
            var remap = new int[n];
            var welded = new List<float3>(n);
            for (int i = 0; i < n; i++)
            {
                float3 p = capPosN[i];
                int found = -1;
                for (int j = 0; j < welded.Count; j++)
                    if (math.distancesq(p, welded[j]) < kEpsSq) { found = j; break; }
                if (found < 0) { remap[i] = welded.Count; welded.Add(p); }
                else remap[i] = found;
            }
            var edges = new List<int2>(capEdgesN.Length);
            for (int i = 0; i < capEdgesN.Length; i++)
            {
                var e = capEdgesN[i];
                int a = remap[e.x], b = remap[e.y];
                if (a != b) edges.Add(new int2(a, b));
            }
            if (welded.Count == 0 || edges.Count == 0) return;

            // 2D projection.
            float3 nn = plane.Normal;
            float3 helper = math.abs(nn.x) > 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            float3 u = math.normalize(math.cross(nn, helper));
            float3 v = math.cross(nn, u);
            var p2d = new Vector2[welded.Count];
            for (int i = 0; i < welded.Count; i++)
            {
                float3 q = welded[i];
                p2d[i] = new Vector2(math.dot(q, u), math.dot(q, v));
            }

            // Walk loops via next-of map.
            var nextOf = new Dictionary<int, int>(edges.Count);
            foreach (var e in edges) if (!nextOf.ContainsKey(e.x)) nextOf[e.x] = e.y;

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
                EmitCap(plane, welded, triIdx, pV, pT, nV, nT);
            }
        }

        static List<int> Reversed(List<int> src) { var r = new List<int>(src); r.Reverse(); return r; }

        static float SignedArea(List<int> loop, Vector2[] p2d)
        {
            float s = 0; int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                var a = p2d[loop[i]]; var b = p2d[loop[(i + 1) % n]];
                s += a.x * b.y - b.x * a.y;
            }
            return s * 0.5f;
        }

        static bool PointInPolygon(Vector2 p, List<int> loop, Vector2[] p2d)
        {
            bool inside = false; int n = loop.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = p2d[loop[i]], b = p2d[loop[j]];
                if (((a.y > p.y) != (b.y > p.y)) &&
                    (p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x))
                    inside = !inside;
            }
            return inside;
        }

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
            if (cross <= 0f) return false;
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
                            NativeList<Vert> pV, NativeList<int> pT,
                            NativeList<Vert> nV, NativeList<int> nT)
        {
            if (tris.Count == 0) return;
            float3 nn = plane.Normal;
            float3 helper = math.abs(nn.x) > 0.9f ? new float3(0, 1, 0) : new float3(1, 0, 0);
            float3 u = math.normalize(math.cross(nn, helper));
            var tan = new float4(u, 1f);
            var nPos = -nn;
            var nNeg =  nn;

            var pMap = new Dictionary<int, int>();
            var nMap = new Dictionary<int, int>();

            int GetP(int i)
            {
                if (pMap.TryGetValue(i, out var x)) return x;
                x = pV.Length;
                pV.Add(new Vert { Pos = capPos[i], Nrm = nPos, Tan = tan, Uv = float2.zero });
                pMap[i] = x; return x;
            }
            int GetN(int i)
            {
                if (nMap.TryGetValue(i, out var x)) return x;
                x = nV.Length;
                nV.Add(new Vert { Pos = capPos[i], Nrm = nNeg, Tan = tan, Uv = float2.zero });
                nMap[i] = x; return x;
            }

            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                nT.Add(GetN(a)); nT.Add(GetN(b)); nT.Add(GetN(c));
                pT.Add(GetP(a)); pT.Add(GetP(c)); pT.Add(GetP(b));
            }
        }
    }
}
