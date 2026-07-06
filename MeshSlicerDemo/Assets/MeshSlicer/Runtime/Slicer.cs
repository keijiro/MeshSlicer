using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer {

// Naive, correctness-first plane slicer. Given a watertight (2-manifold) mesh and a
// plane (in the mesh's local space), returns the two pieces on either side of the
// plane, each closed with a cap on the cut cross section. No performance tuning.
public static class Slicer
{
    public static SliceResult Slice(Mesh source, Plane plane)
    {
        var verts = ReadVertices(source);
        var indices = ReadIndices(source);

        var n = math.normalize(plane.Normal);
        var extent = math.cmax(source.bounds.size);
        var eps = math.max(extent * 1e-4f, 1e-6f);

        var pos = new MeshBuilder();
        var neg = new MeshBuilder();
        var cap = new CapBuilder(n, extent);

        // Signed distances, snapped to the plane when within eps.
        var dist = new float[verts.Length];
        for (var i = 0; i < verts.Length; i++)
        {
            var d = plane.SignedDistanceToPoint(verts[i].Position);
            dist[i] = math.abs(d) <= eps ? 0f : d;
        }

        var scratch = new List<Vertex>(4);
        for (var t = 0; t < indices.Length; t += 3)
        {
            int ia = indices[t], ib = indices[t + 1], ic = indices[t + 2];
            SliceTriangle(verts[ia], verts[ib], verts[ic], dist[ia], dist[ib], dist[ic],
                          pos, neg, cap, scratch);
        }

        BuildCaps(cap, n, pos, neg);

        return new SliceResult
        {
            Positive = pos.TriangleCount > 0 ? pos.ToMesh(source.name + "_Positive") : null,
            Negative = neg.TriangleCount > 0 ? neg.ToMesh(source.name + "_Negative") : null
        };
    }

    static void SliceTriangle(in Vertex a, in Vertex b, in Vertex c,
                              float da, float db, float dc,
                              MeshBuilder pos, MeshBuilder neg, CapBuilder cap,
                              List<Vertex> scratch)
    {
        var strictPos = da > 0 || db > 0 || dc > 0;
        var strictNeg = da < 0 || db < 0 || dc < 0;

        // Collect the (at most two) points where the triangle boundary meets the plane.
        Vertex cut0 = default, cut1 = default;
        var cutCount = 0;
        void AddCut(in Vertex v) { if (cutCount == 0) cut0 = v; else if (cutCount == 1) cut1 = v; cutCount++; }

        if (da == 0) AddCut(a);
        if (db == 0) AddCut(b);
        if (dc == 0) AddCut(c);
        if ((da > 0 && db < 0) || (da < 0 && db > 0)) AddCut(Vertex.Lerp(a, b, da / (da - db)));
        if ((db > 0 && dc < 0) || (db < 0 && dc > 0)) AddCut(Vertex.Lerp(b, c, db / (db - dc)));
        if ((dc > 0 && da < 0) || (dc < 0 && da > 0)) AddCut(Vertex.Lerp(c, a, dc / (dc - da)));

        if (cutCount == 2) cap.AddSegment(cut0.Position, cut1.Position);

        if (strictPos && strictNeg)
        {
            ClipHalf(a, b, c, da, db, dc, true, scratch);
            EmitFan(pos, scratch);
            ClipHalf(a, b, c, da, db, dc, false, scratch);
            EmitFan(neg, scratch);
        }
        else if (strictPos)
        {
            pos.AddTriangle(a, b, c);
        }
        else if (strictNeg)
        {
            neg.AddTriangle(a, b, c);
        }
        // else: fully coplanar (all d == 0) -> skip degenerate face.
    }

    // Sutherland-Hodgman clip of the triangle to one half-space (on-plane kept).
    static void ClipHalf(in Vertex a, in Vertex b, in Vertex c,
                         float da, float db, float dc, bool keepPositive, List<Vertex> outPoly)
    {
        outPoly.Clear();
        ClipEdge(a, b, da, db, keepPositive, outPoly);
        ClipEdge(b, c, db, dc, keepPositive, outPoly);
        ClipEdge(c, a, dc, da, keepPositive, outPoly);
    }

    static void ClipEdge(in Vertex cur, in Vertex nxt, float dc, float dn, bool keepPositive, List<Vertex> outPoly)
    {
        var curIn = keepPositive ? dc >= 0 : dc <= 0;
        if (curIn) outPoly.Add(cur);
        if ((dc > 0 && dn < 0) || (dc < 0 && dn > 0))
        {
            var s = dc / (dc - dn);
            outPoly.Add(Vertex.Lerp(cur, nxt, s));
        }
    }

    static void EmitFan(MeshBuilder builder, List<Vertex> poly)
    {
        for (var i = 1; i + 1 < poly.Count; i++)
            builder.AddTriangle(poly[0], poly[i], poly[i + 1]);
    }

    static void BuildCaps(CapBuilder cap, float3 n, MeshBuilder pos, MeshBuilder neg)
    {
        var tris = cap.BuildCapTriangles(); // CCW in (u,v) -> normal +n
        if (tris.Count == 0) return;
        var pts = cap.Points;

        var uAxis = math.abs(n.x) < 0.9f ? math.cross(n, new float3(1, 0, 0)) : math.cross(n, new float3(0, 1, 0));
        var tangent = new float4(math.normalize(uAxis), 1);

        Vertex Make(int i, float3 normal) => new Vertex
        {
            Position = pts[i], Normal = normal, Tangent = tangent, UV = float2.zero
        };

        for (var i = 0; i < tris.Count; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            // Positive piece: cap faces -n, so reverse the +n winding.
            pos.AddTriangle(Make(i0, -n), Make(i2, -n), Make(i1, -n));
            // Negative piece: cap faces +n, keep winding.
            neg.AddTriangle(Make(i0, n), Make(i1, n), Make(i2, n));
        }
    }

    // --- mesh reading ---

    static Vertex[] ReadVertices(Mesh mesh)
    {
        var count = mesh.vertexCount;
        var p = mesh.vertices;
        var nrm = mesh.normals;
        var tan = mesh.tangents;
        var uv = mesh.uv;
        var hasN = nrm != null && nrm.Length == count;
        var hasT = tan != null && tan.Length == count;
        var hasUV = uv != null && uv.Length == count;

        var verts = new Vertex[count];
        for (var i = 0; i < count; i++)
        {
            verts[i] = new Vertex
            {
                Position = p[i],
                Normal = hasN ? (float3)(Vector3)nrm[i] : new float3(0, 1, 0),
                Tangent = hasT ? (float4)(Vector4)tan[i] : new float4(1, 0, 0, 1),
                UV = hasUV ? (float2)uv[i] : float2.zero
            };
        }
        return verts;
    }

    static int[] ReadIndices(Mesh mesh)
    {
        if (mesh.subMeshCount == 1) return mesh.triangles;
        var all = new List<int>();
        for (var s = 0; s < mesh.subMeshCount; s++) all.AddRange(mesh.GetTriangles(s));
        return all.ToArray();
    }
}

} // namespace MeshSlicer
