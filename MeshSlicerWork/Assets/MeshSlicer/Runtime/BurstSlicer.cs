using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer {

// Optimized plane slicer. The heavy per-triangle classification and splitting runs
// in a Burst-compiled job over NativeArrays; the (comparatively tiny) cap
// triangulation stays in managed code and reuses CapBuilder/PolygonTriangulator.
// Results are uploaded through the Advanced Mesh API (SetVertexBufferData /
// SetIndexBufferData) to avoid the managed array round-trips of the naive path.
public static class BurstSlicer
{
    static readonly VertexAttributeDescriptor[] Layout =
    {
        new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        new(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
        new(VertexAttribute.Tangent, VertexAttributeFormat.Float32, 4),
        new(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2)
    };

    public static SliceResult Slice(Mesh source, Plane plane)
    {
        var pos = new Mesh { name = source.name + "_Positive" };
        var neg = new Mesh { name = source.name + "_Negative" };
        var (hasPos, hasNeg) = Slice(source, plane, pos, neg);
        if (!hasPos) { Object.DestroyImmediate(pos); pos = null; }
        if (!hasNeg) { Object.DestroyImmediate(neg); neg = null; }
        return new SliceResult { Positive = pos, Negative = neg };
    }

    // Non-allocating overload: writes into caller-owned meshes for per-frame reuse.
    // Returns whether each piece received any geometry.
    // Optional phase timings (ms) for profiling; negligible overhead.
    public static double ReadMs, JobMs, CapMs, UploadMs;

    public static (bool positive, bool negative) Slice(Mesh source, Plane plane, Mesh positiveOut, Mesh negativeOut)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ReadSource(source, out var srcVerts, out var srcIndices);
        ReadMs = sw.Elapsed.TotalMilliseconds; sw.Restart();

        var n = math.normalize(plane.Normal);
        var extent = math.cmax((float3)source.bounds.size);
        var eps = math.max(extent * 1e-4f, 1e-6f);

        var numTris = srcIndices.Length / 3;
        var posCount = new NativeArray<int>(numTris, Allocator.TempJob);
        var negCount = new NativeArray<int>(numTris, Allocator.TempJob);
        var cutCount = new NativeArray<int>(numTris, Allocator.TempJob);

        // Pass 1 (parallel): classify each triangle and count its output.
        new CountJob
        {
            Verts = srcVerts, Indices = srcIndices, PlaneN = n, PlaneD = plane.Distance, Eps = eps,
            PosCount = posCount, NegCount = negCount, CutCount = cutCount
        }.Schedule(numTris, 128).Complete();

        // Exclusive prefix sums give each triangle a disjoint output range.
        var posOff = new NativeArray<int>(numTris, Allocator.TempJob);
        var negOff = new NativeArray<int>(numTris, Allocator.TempJob);
        var cutOff = new NativeArray<int>(numTris, Allocator.TempJob);
        var totals = new NativeArray<int>(3, Allocator.TempJob);
        new PrefixJob
        {
            PosCount = posCount, NegCount = negCount, CutCount = cutCount,
            PosOff = posOff, NegOff = negOff, CutOff = cutOff, Totals = totals
        }.Run();
        int totalPos = totals[0], totalNeg = totals[1], totalCut = totals[2];

        var posBody = new NativeArray<Vertex>(totalPos, Allocator.TempJob);
        var negBody = new NativeArray<Vertex>(totalNeg, Allocator.TempJob);
        var cutArr = new NativeArray<float3>(totalCut, Allocator.TempJob);

        // Pass 2 (parallel): write each triangle's split into its range.
        new WriteJob
        {
            Verts = srcVerts, Indices = srcIndices, PlaneN = n, PlaneD = plane.Distance, Eps = eps,
            PosOff = posOff, NegOff = negOff, CutOff = cutOff,
            Pos = posBody, Neg = negBody, Cut = cutArr
        }.Schedule(numTris, 128).Complete();
        JobMs = sw.Elapsed.TotalMilliseconds; sw.Restart();

        var posVerts = new NativeList<Vertex>(totalPos + 256, Allocator.TempJob);
        var negVerts = new NativeList<Vertex>(totalNeg + 256, Allocator.TempJob);
        posVerts.AddRange(posBody);
        negVerts.AddRange(negBody);
        AppendCaps(n, extent, cutArr, posVerts, negVerts);
        CapMs = sw.Elapsed.TotalMilliseconds; sw.Restart();

        var hasPos = posVerts.Length >= 3;
        var hasNeg = negVerts.Length >= 3;
        Upload(positiveOut, posVerts);
        Upload(negativeOut, negVerts);
        UploadMs = sw.Elapsed.TotalMilliseconds;

        srcVerts.Dispose(); srcIndices.Dispose();
        posCount.Dispose(); negCount.Dispose(); cutCount.Dispose();
        posOff.Dispose(); negOff.Dispose(); cutOff.Dispose(); totals.Dispose();
        posBody.Dispose(); negBody.Dispose(); cutArr.Dispose();
        posVerts.Dispose(); negVerts.Dispose();
        return (hasPos, hasNeg);
    }

    // --- input ---

    static void ReadSource(Mesh mesh, out NativeArray<Vertex> verts, out NativeArray<int> indices)
    {
        using var dataArray = Mesh.AcquireReadOnlyMeshData(mesh);
        var data = dataArray[0];
        var vc = data.vertexCount;

        var pos = new NativeArray<Vector3>(vc, Allocator.TempJob);
        var nrm = new NativeArray<Vector3>(vc, Allocator.TempJob);
        var tan = new NativeArray<Vector4>(vc, Allocator.TempJob);
        var uv = new NativeArray<Vector2>(vc, Allocator.TempJob);
        data.GetVertices(pos);
        if (data.HasVertexAttribute(VertexAttribute.Normal)) data.GetNormals(nrm);
        if (data.HasVertexAttribute(VertexAttribute.Tangent)) data.GetTangents(tan);
        if (data.HasVertexAttribute(VertexAttribute.TexCoord0)) data.GetUVs(0, uv);

        verts = new NativeArray<Vertex>(vc, Allocator.TempJob);
        new PackJob { P = pos, N = nrm, T = tan, U = uv, Out = verts }.Run(vc);

        var total = 0;
        for (var s = 0; s < data.subMeshCount; s++) total += data.GetSubMesh(s).indexCount;
        indices = new NativeArray<int>(total, Allocator.TempJob);
        var offset = 0;
        var tmp = new NativeArray<int>(total, Allocator.Temp);
        for (var s = 0; s < data.subMeshCount; s++)
        {
            var sub = data.GetSubMesh(s);
            var slice = tmp.GetSubArray(0, sub.indexCount);
            data.GetIndices(slice, s);
            NativeArray<int>.Copy(slice, 0, indices, offset, sub.indexCount);
            offset += sub.indexCount;
        }
        tmp.Dispose();

        pos.Dispose(); nrm.Dispose(); tan.Dispose(); uv.Dispose();
    }

    // --- caps (managed, reuses the naive triangulator) ---

    static void AppendCaps(float3 n, float extent, NativeArray<float3> cuts,
                           NativeList<Vertex> pos, NativeList<Vertex> neg)
    {
        if (cuts.Length < 2) return;
        var cap = new CapBuilder(n, extent);
        for (var i = 0; i + 1 < cuts.Length; i += 2) cap.AddSegment(cuts[i], cuts[i + 1]);

        var tris = cap.BuildCapTriangles(); // CCW in (u,v) -> normal +n
        if (tris.Count == 0) return;
        var pts = cap.Points;

        var uAxis = math.abs(n.x) < 0.9f ? math.cross(n, new float3(1, 0, 0)) : math.cross(n, new float3(0, 1, 0));
        var tangent = new float4(math.normalize(uAxis), 1);

        Vertex Make(int i, float3 nr) => new() { Position = pts[i], Normal = nr, Tangent = tangent, UV = float2.zero };

        for (var i = 0; i < tris.Count; i += 3)
        {
            int a = tris[i], b = tris[i + 1], c = tris[i + 2];
            pos.Add(Make(a, -n)); pos.Add(Make(c, -n)); pos.Add(Make(b, -n)); // reversed -> faces -n
            neg.Add(Make(a, n)); neg.Add(Make(b, n)); neg.Add(Make(c, n));     // faces +n
        }
    }

    // --- output ---

    static void Upload(Mesh mesh, NativeList<Vertex> verts)
    {
        mesh.Clear();
        var count = verts.Length;
        if (count < 3) return;

        mesh.SetVertexBufferParams(count, Layout);
        mesh.SetVertexBufferData(verts.AsArray(), 0, 0, count);

        var indices = new NativeArray<int>(count, Allocator.TempJob);
        new IdentityJob { Out = indices }.Run(count);
        mesh.SetIndexBufferParams(count, IndexFormat.UInt32);
        mesh.SetIndexBufferData(indices, 0, 0, count);
        indices.Dispose();

        mesh.subMeshCount = 1;
        mesh.SetSubMesh(0, new SubMeshDescriptor(0, count));
        mesh.RecalculateBounds();
    }

    // --- jobs ---

    [BurstCompile]
    struct PackJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> P, N;
        [ReadOnly] public NativeArray<Vector4> T;
        [ReadOnly] public NativeArray<Vector2> U;
        [WriteOnly] public NativeArray<Vertex> Out;
        public void Execute(int i) => Out[i] = new Vertex
        {
            Position = P[i], Normal = N[i], Tangent = T[i], UV = U[i]
        };
    }

    [BurstCompile]
    struct IdentityJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<int> Out;
        public void Execute(int i) => Out[i] = i;
    }

    // Pass 1: per-triangle output sizes.
    [BurstCompile]
    struct CountJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vertex> Verts;
        [ReadOnly] public NativeArray<int> Indices;
        public float3 PlaneN; public float PlaneD; public float Eps;
        [WriteOnly] public NativeArray<int> PosCount, NegCount, CutCount;

        public void Execute(int t)
        {
            var i = t * 3;
            Geo.Plan(Verts[Indices[i]].Position, Verts[Indices[i + 1]].Position, Verts[Indices[i + 2]].Position,
                     PlaneN, PlaneD, Eps, out var p, out var ng, out var cu);
            PosCount[t] = p; NegCount[t] = ng; CutCount[t] = cu;
        }
    }

    // Exclusive prefix sums + totals.
    [BurstCompile]
    struct PrefixJob : IJob
    {
        [ReadOnly] public NativeArray<int> PosCount, NegCount, CutCount;
        [WriteOnly] public NativeArray<int> PosOff, NegOff, CutOff;
        [WriteOnly] public NativeArray<int> Totals;

        public void Execute()
        {
            int ap = 0, an = 0, ac = 0;
            for (var t = 0; t < PosCount.Length; t++)
            {
                PosOff[t] = ap; ap += PosCount[t];
                NegOff[t] = an; an += NegCount[t];
                CutOff[t] = ac; ac += CutCount[t];
            }
            Totals[0] = ap; Totals[1] = an; Totals[2] = ac;
        }
    }

    // Pass 2: write each triangle's split into its reserved range.
    [BurstCompile]
    unsafe struct WriteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vertex> Verts;
        [ReadOnly] public NativeArray<int> Indices;
        public float3 PlaneN; public float PlaneD; public float Eps;
        [ReadOnly] public NativeArray<int> PosOff, NegOff, CutOff;
        [NativeDisableParallelForRestriction] public NativeArray<Vertex> Pos, Neg;
        [NativeDisableParallelForRestriction] public NativeArray<float3> Cut;

        public void Execute(int t)
        {
            var i = t * 3;
            Geo.Write(Verts[Indices[i]], Verts[Indices[i + 1]], Verts[Indices[i + 2]],
                      PlaneN, PlaneD, Eps, PosOff[t], NegOff[t], CutOff[t], Pos, Neg, Cut);
        }
    }

    // Shared per-triangle geometry, used identically by the count and write passes.
    static class Geo
    {
        static float Dist(float3 p, float3 pn, float pd, float eps)
        {
            var d = math.dot(pn, p) + pd;
            return math.abs(d) <= eps ? 0f : d;
        }

        public static void Plan(float3 a, float3 b, float3 c, float3 pn, float pd, float eps,
                                out int posV, out int negV, out int cutV)
        {
            float da = Dist(a, pn, pd, eps), db = Dist(b, pn, pd, eps), dc = Dist(c, pn, pd, eps);
            var sp = da > 0 || db > 0 || dc > 0;
            var sn = da < 0 || db < 0 || dc < 0;

            var cn = 0;
            if (da == 0) cn++;
            if (db == 0) cn++;
            if (dc == 0) cn++;
            if ((da > 0 && db < 0) || (da < 0 && db > 0)) cn++;
            if ((db > 0 && dc < 0) || (db < 0 && dc > 0)) cn++;
            if ((dc > 0 && da < 0) || (dc < 0 && da > 0)) cn++;
            cutV = cn == 2 ? 2 : 0;

            posV = 0; negV = 0;
            if (sp && sn)
            {
                var mp = ClipCount(da, db, dc, true);
                var mn = ClipCount(da, db, dc, false);
                posV = mp >= 3 ? (mp - 2) * 3 : 0;
                negV = mn >= 3 ? (mn - 2) * 3 : 0;
            }
            else if (sp) posV = 3;
            else if (sn) negV = 3;
        }

        public static unsafe void Write(in Vertex a, in Vertex b, in Vertex c, float3 pn, float pd, float eps,
                                        int posOff, int negOff, int cutOff,
                                        NativeArray<Vertex> pos, NativeArray<Vertex> neg, NativeArray<float3> cut)
        {
            float da = Dist(a.Position, pn, pd, eps), db = Dist(b.Position, pn, pd, eps), dc = Dist(c.Position, pn, pd, eps);
            var sp = da > 0 || db > 0 || dc > 0;
            var sn = da < 0 || db < 0 || dc < 0;

            var cp = stackalloc Vertex[2];
            var cn = 0;
            if (da == 0 && cn < 2) cp[cn++] = a;
            if (db == 0 && cn < 2) cp[cn++] = b;
            if (dc == 0 && cn < 2) cp[cn++] = c;
            if (((da > 0 && db < 0) || (da < 0 && db > 0)) && cn < 2) cp[cn++] = Vertex.Lerp(a, b, da / (da - db));
            if (((db > 0 && dc < 0) || (db < 0 && dc > 0)) && cn < 2) cp[cn++] = Vertex.Lerp(b, c, db / (db - dc));
            if (((dc > 0 && da < 0) || (dc < 0 && da > 0)) && cn < 2) cp[cn++] = Vertex.Lerp(c, a, dc / (dc - da));
            if (cn == 2) { cut[cutOff] = cp[0].Position; cut[cutOff + 1] = cp[1].Position; }

            if (sp && sn)
            {
                var poly = stackalloc Vertex[4];
                var m = ClipHalf(a, b, c, da, db, dc, true, poly);
                var w = posOff;
                for (var k = 1; k + 1 < m; k++) { pos[w++] = poly[0]; pos[w++] = poly[k]; pos[w++] = poly[k + 1]; }
                m = ClipHalf(a, b, c, da, db, dc, false, poly);
                w = negOff;
                for (var k = 1; k + 1 < m; k++) { neg[w++] = poly[0]; neg[w++] = poly[k]; neg[w++] = poly[k + 1]; }
            }
            else if (sp) { pos[posOff] = a; pos[posOff + 1] = b; pos[posOff + 2] = c; }
            else if (sn) { neg[negOff] = a; neg[negOff + 1] = b; neg[negOff + 2] = c; }
        }

        static int ClipCount(float da, float db, float dc, bool keepPos)
            => EdgeCount(da, db, keepPos) + EdgeCount(db, dc, keepPos) + EdgeCount(dc, da, keepPos);

        static int EdgeCount(float dc, float dn, bool keepPos)
        {
            var n = (keepPos ? dc >= 0 : dc <= 0) ? 1 : 0;
            if ((dc > 0 && dn < 0) || (dc < 0 && dn > 0)) n++;
            return n;
        }

        static unsafe int ClipHalf(in Vertex a, in Vertex b, in Vertex c, float da, float db, float dc, bool keepPos, Vertex* outPoly)
        {
            var n = 0;
            ClipEdge(a, b, da, db, keepPos, outPoly, ref n);
            ClipEdge(b, c, db, dc, keepPos, outPoly, ref n);
            ClipEdge(c, a, dc, da, keepPos, outPoly, ref n);
            return n;
        }

        static unsafe void ClipEdge(in Vertex cur, in Vertex nxt, float dc, float dn, bool keepPos, Vertex* outPoly, ref int n)
        {
            var curIn = keepPos ? dc >= 0 : dc <= 0;
            if (curIn && n < 4) outPoly[n++] = cur;
            if (((dc > 0 && dn < 0) || (dc < 0 && dn > 0)) && n < 4)
                outPoly[n++] = Vertex.Lerp(cur, nxt, dc / (dc - dn));
        }
    }
}

} // namespace MeshSlicer
