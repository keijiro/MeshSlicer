using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MeshSlicer {

// Watertight (2-manifold) primitives used to exercise the slicer, including shapes
// whose cross sections form multiple / nested loops (torus, pipe, tray, bowl).
public static class ProceduralMeshes
{
    // --- Convex primitives ---

    public static Mesh Cube(float size = 1f)
    {
        var h = size * 0.5f;
        var v = new List<Vector3>();
        var t = new List<int>();
        // +X, -X, +Y, -Y, +Z, -Z
        AddQuad(v, t, new(h, -h, -h), new(h, h, -h), new(h, h, h), new(h, -h, h));
        AddQuad(v, t, new(-h, -h, h), new(-h, h, h), new(-h, h, -h), new(-h, -h, -h));
        AddQuad(v, t, new(-h, h, -h), new(-h, h, h), new(h, h, h), new(h, h, -h));
        AddQuad(v, t, new(-h, -h, h), new(-h, -h, -h), new(h, -h, -h), new(h, -h, h));
        AddQuad(v, t, new(h, -h, h), new(h, h, h), new(-h, h, h), new(-h, -h, h));
        AddQuad(v, t, new(-h, -h, -h), new(-h, h, -h), new(h, h, -h), new(h, -h, -h));
        return Build("Cube", v, t);
    }

    public static Mesh Tetrahedron(float size = 1f)
    {
        var s = size * 0.5f;
        float3 a = new(s, s, s), b = new(s, -s, -s), c = new(-s, s, -s), d = new(-s, -s, s);
        var v = new List<Vector3>();
        var t = new List<int>();
        AddTri(v, t, a, b, c);
        AddTri(v, t, a, d, b);
        AddTri(v, t, a, c, d);
        AddTri(v, t, b, d, c);
        return Build("Tetrahedron", v, t);
    }

    public static Mesh IcoSphere(int subdiv = 2, float radius = 0.5f)
    {
        var g = (1f + math.sqrt(5f)) * 0.5f;
        var verts = new List<float3>
        {
            new(-1, g, 0), new(1, g, 0), new(-1, -g, 0), new(1, -g, 0),
            new(0, -1, g), new(0, 1, g), new(0, -1, -g), new(0, 1, -g),
            new(g, 0, -1), new(g, 0, 1), new(-g, 0, -1), new(-g, 0, 1)
        };
        var faces = new List<int3>
        {
            new(0,11,5), new(0,5,1), new(0,1,7), new(0,7,10), new(0,10,11),
            new(1,5,9), new(5,11,4), new(11,10,2), new(10,7,6), new(7,1,8),
            new(3,9,4), new(3,4,2), new(3,2,6), new(3,6,8), new(3,8,9),
            new(4,9,5), new(2,4,11), new(6,2,10), new(8,6,7), new(9,8,1)
        };
        var cache = new Dictionary<long, int>();
        int Mid(int a, int b)
        {
            var key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (cache.TryGetValue(key, out var i)) return i;
            i = verts.Count;
            verts.Add(math.normalize(verts[a] + verts[b]));
            cache[key] = i;
            return i;
        }
        for (var s = 0; s < subdiv; s++)
        {
            var next = new List<int3>();
            foreach (var f in faces)
            {
                var ab = Mid(f.x, f.y);
                var bc = Mid(f.y, f.z);
                var ca = Mid(f.z, f.x);
                next.Add(new(f.x, ab, ca));
                next.Add(new(f.y, bc, ab));
                next.Add(new(f.z, ca, bc));
                next.Add(new(ab, bc, ca));
            }
            faces = next;
        }
        var v = new List<Vector3>();
        foreach (var p in verts) v.Add((Vector3)(math.normalize(p) * radius));
        var t = new List<int>();
        foreach (var f in faces) { t.Add(f.x); t.Add(f.y); t.Add(f.z); }
        return Build("IcoSphere", v, t);
    }

    // --- Shapes with holed / multi-loop cross sections ---

    // A full torus (genus 1). A cut through its equatorial plane yields an annulus.
    public static Mesh Torus(float major = 0.6f, float minor = 0.22f, int majSeg = 48, int minSeg = 24)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        for (var i = 0; i < majSeg; i++)
        {
            var u = i / (float)majSeg * math.PI * 2;
            for (var j = 0; j < minSeg; j++)
            {
                var w = j / (float)minSeg * math.PI * 2;
                var r = major + minor * math.cos(w);
                v.Add(new Vector3(r * math.cos(u), minor * math.sin(w), r * math.sin(u)));
            }
        }
        for (var i = 0; i < majSeg; i++)
        for (var j = 0; j < minSeg; j++)
        {
            var a = i * minSeg + j;
            var b = ((i + 1) % majSeg) * minSeg + j;
            var c = ((i + 1) % majSeg) * minSeg + (j + 1) % minSeg;
            var d = i * minSeg + (j + 1) % minSeg;
            t.Add(a); t.Add(c); t.Add(b);
            t.Add(a); t.Add(d); t.Add(c);
        }
        return Build("Torus", v, t);
    }

    // A hollow pipe (thick-walled tube capped by flat rings at both ends). A cut
    // perpendicular to the axis yields an annulus.
    public static Mesh Pipe(float outer = 0.5f, float inner = 0.3f, float height = 1f, int seg = 48)
    {
        var v = new List<Vector3>();
        var t = new List<int>();
        var hh = height * 0.5f;

        // Ring vertex layout: for each segment i we store 4 verts:
        // outerTop, outerBottom, innerTop, innerBottom.
        int Idx(int i, int k) => (i % seg) * 4 + k;
        for (var i = 0; i < seg; i++)
        {
            var a = i / (float)seg * math.PI * 2;
            float cx = math.cos(a), cz = math.sin(a);
            v.Add(new Vector3(outer * cx, hh, outer * cz));
            v.Add(new Vector3(outer * cx, -hh, outer * cz));
            v.Add(new Vector3(inner * cx, hh, inner * cz));
            v.Add(new Vector3(inner * cx, -hh, inner * cz));
        }
        for (var i = 0; i < seg; i++)
        {
            int oT = Idx(i, 0), oB = Idx(i, 1), iT = Idx(i, 2), iB = Idx(i, 3);
            int oT2 = Idx(i + 1, 0), oB2 = Idx(i + 1, 1), iT2 = Idx(i + 1, 2), iB2 = Idx(i + 1, 3);
            Quad(t, oB, oT, oT2, oB2);   // outer wall (faces out)
            Quad(t, iT, iB, iB2, iT2);   // inner wall (faces in)
            Quad(t, oT, iT, iT2, oT2);   // top ring
            Quad(t, iB, oB, oB2, iB2);   // bottom ring
        }
        return Build("Pipe", v, t);
    }

    // An open box (tray): a solid rectangular basin, open at the top. A horizontal
    // cut through the walls yields a rectangular frame (rectangle with a hole).
    public static Mesh Tray(float width = 1f, float depth = 1f, float height = 0.7f, float wall = 0.18f)
    {
        var wx = width * 0.5f; var wz = depth * 0.5f;
        var ix = wx - wall; var iz = wz - wall;
        var top = height * 0.5f; var bot = -height * 0.5f;
        var floor = bot + wall; // inner cavity floor height

        var v = new List<Vector3>();
        var t = new List<int>();

        // Outer shell: bottom + 4 side walls (no top face).
        AddQuad(v, t, new(-wx, bot, wz), new(-wx, bot, -wz), new(wx, bot, -wz), new(wx, bot, wz)); // bottom (faces -Y)
        AddQuad(v, t, new(wx, bot, wz), new(wx, top, wz), new(-wx, top, wz), new(-wx, bot, wz));   // +Z
        AddQuad(v, t, new(-wx, bot, -wz), new(-wx, top, -wz), new(wx, top, -wz), new(wx, bot, -wz)); // -Z
        AddQuad(v, t, new(-wx, bot, wz), new(-wx, top, wz), new(-wx, top, -wz), new(-wx, bot, -wz)); // -X
        AddQuad(v, t, new(wx, bot, -wz), new(wx, top, -wz), new(wx, top, wz), new(wx, bot, wz));    // +X

        // Inner cavity: floor + 4 inner walls (facing inward).
        AddQuad(v, t, new(-ix, floor, iz), new(ix, floor, iz), new(ix, floor, -iz), new(-ix, floor, -iz)); // inner floor (faces +Y)
        AddQuad(v, t, new(-ix, floor, iz), new(-ix, top, iz), new(ix, top, iz), new(ix, floor, iz));       // inner +Z (faces -Z)
        AddQuad(v, t, new(ix, floor, -iz), new(ix, top, -iz), new(-ix, top, -iz), new(-ix, floor, -iz));   // inner -Z (faces +Z)
        AddQuad(v, t, new(-ix, floor, -iz), new(-ix, top, -iz), new(-ix, top, iz), new(-ix, floor, iz));   // inner -X (faces +X)
        AddQuad(v, t, new(ix, floor, iz), new(ix, top, iz), new(ix, top, -iz), new(ix, floor, -iz));       // inner +X (faces -X)

        // Top rim: 4 quads joining outer top edge to inner top edge (faces +Y).
        AddQuad(v, t, new(-wx, top, wz), new(wx, top, wz), new(ix, top, iz), new(-ix, top, iz));       // +Z rim
        AddQuad(v, t, new(wx, top, -wz), new(-wx, top, -wz), new(-ix, top, -iz), new(ix, top, -iz));   // -Z rim
        AddQuad(v, t, new(-wx, top, -wz), new(-wx, top, wz), new(-ix, top, iz), new(-ix, top, -iz));   // -X rim
        AddQuad(v, t, new(wx, top, wz), new(wx, top, -wz), new(ix, top, -iz), new(ix, top, iz));       // +X rim

        return Build("Tray", v, t);
    }

    // A hemispherical bowl with wall thickness, joined at the rim. A horizontal cut
    // below the rim yields an annulus.
    public static Mesh Bowl(float outer = 0.5f, float thickness = 0.08f, int seg = 48, int rings = 16)
    {
        var inner = outer - thickness;
        var v = new List<Vector3>();
        var t = new List<int>();

        // Outer surface: lower hemisphere (y from -outer..0), faces outward.
        // Inner surface: lower hemisphere radius inner, faces inward.
        // Grid indices.
        int OuterIdx(int r, int s) => r * seg + (s % seg);
        var baseOuter = 0;
        for (var r = 0; r <= rings; r++)
        {
            var phi = (r / (float)rings) * (math.PI * 0.5f); // 0 at bottom pole .. PI/2 at rim
            var y = -outer * math.cos(phi);
            var rad = outer * math.sin(phi);
            for (var s = 0; s < seg; s++)
            {
                var a = s / (float)seg * math.PI * 2;
                v.Add(new Vector3(rad * math.cos(a), y, rad * math.sin(a)));
            }
        }
        var baseInner = v.Count;
        int InnerIdx(int r, int s) => baseInner + r * seg + (s % seg);
        for (var r = 0; r <= rings; r++)
        {
            var phi = (r / (float)rings) * (math.PI * 0.5f);
            var y = -inner * math.cos(phi);
            var rad = inner * math.sin(phi);
            for (var s = 0; s < seg; s++)
            {
                var a = s / (float)seg * math.PI * 2;
                v.Add(new Vector3(rad * math.cos(a), y, rad * math.sin(a)));
            }
        }

        // Outer faces (outward). The bottom pole row (r=0) collapses to a point but we
        // keep distinct verts; degenerate tris there are harmless and stay watertight.
        for (var r = 0; r < rings; r++)
        for (var s = 0; s < seg; s++)
        {
            int a = OuterIdx(r, s), b = OuterIdx(r + 1, s), c = OuterIdx(r + 1, s + 1), d = OuterIdx(r, s + 1);
            Quad(t, a, b, c, d); // outward
        }
        // Inner faces (inward: reversed winding).
        for (var r = 0; r < rings; r++)
        for (var s = 0; s < seg; s++)
        {
            int a = InnerIdx(r, s), b = InnerIdx(r + 1, s), c = InnerIdx(r + 1, s + 1), d = InnerIdx(r, s + 1);
            Quad(t, a, d, c, b); // inward
        }
        // Rim ring at r == rings joining outer to inner (faces up +Y).
        for (var s = 0; s < seg; s++)
        {
            int o0 = OuterIdx(rings, s), o1 = OuterIdx(rings, s + 1);
            int i0 = InnerIdx(rings, s), i1 = InnerIdx(rings, s + 1);
            Quad(t, o0, i0, i1, o1);
        }
        _ = baseOuter;
        return Build("Bowl", v, t);
    }

    // --- helpers ---

    static void AddTri(List<Vector3> v, List<int> t, float3 a, float3 b, float3 c)
    {
        var i = v.Count;
        v.Add((Vector3)a); v.Add((Vector3)b); v.Add((Vector3)c);
        t.Add(i); t.Add(i + 1); t.Add(i + 2);
    }

    static void AddQuad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var i = v.Count;
        v.Add(a); v.Add(b); v.Add(c); v.Add(d);
        t.Add(i); t.Add(i + 1); t.Add(i + 2);
        t.Add(i); t.Add(i + 2); t.Add(i + 3);
    }

    static void Quad(List<int> t, int a, int b, int c, int d)
    {
        t.Add(a); t.Add(b); t.Add(c);
        t.Add(a); t.Add(c); t.Add(d);
    }

    static Mesh Build(string name, List<Vector3> v, List<int> t)
    {
        var mesh = new Mesh { name = name };
        if (v.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.SetVertices(v);
        mesh.SetTriangles(t, 0);

        // Planar UVs from the XZ/XY spread so RecalculateTangents has gradient to work with.
        var b = GetBounds(v);
        var size = math.max(b.size, new float3(1e-4f));
        var uv = new Vector2[v.Count];
        for (var i = 0; i < v.Count; i++)
        {
            var p = ((float3)v[i] - (float3)b.min) / size;
            uv[i] = new Vector2(p.x, p.z);
        }
        mesh.uv = uv;

        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Bounds GetBounds(List<Vector3> v)
    {
        var b = new Bounds(v[0], Vector3.zero);
        foreach (var p in v) b.Encapsulate(p);
        return b;
    }
}

} // namespace MeshSlicer
