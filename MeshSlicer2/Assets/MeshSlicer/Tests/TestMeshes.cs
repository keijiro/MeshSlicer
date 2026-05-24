using System.Collections.Generic;
using UnityEngine;

namespace MeshSlicer.Tests
{
    internal static class TestMeshes
    {
        // Axis-aligned cube centered at origin with the given full side length.
        public static Mesh Cube(float side = 2f)
        {
            float h = side * 0.5f;
            var v = new[]
            {
                new Vector3(-h,-h,-h), new Vector3( h,-h,-h),
                new Vector3( h, h,-h), new Vector3(-h, h,-h),
                new Vector3(-h,-h, h), new Vector3( h,-h, h),
                new Vector3( h, h, h), new Vector3(-h, h, h),
            };
            int[] t =
            {
                0,2,1, 0,3,2,   // -Z
                4,5,6, 4,6,7,   // +Z
                0,1,5, 0,5,4,   // -Y
                3,7,6, 3,6,2,   // +Y
                0,4,7, 0,7,3,   // -X
                1,2,6, 1,6,5,   // +X
            };
            var uv = new Vector2[8];
            for (int i = 0; i < 8; i++) uv[i] = new Vector2(v[i].x / side + 0.5f, v[i].z / side + 0.5f);

            return Build(v, t, uv);
        }

        // Icosphere by subdividing an icosahedron 'subdiv' times.
        public static Mesh Icosphere(float radius = 1f, int subdiv = 2)
        {
            // Initial icosahedron
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3( 1, t, 0), new Vector3(-1,-t, 0), new Vector3( 1,-t, 0),
                new Vector3( 0,-1, t), new Vector3( 0, 1, t), new Vector3( 0,-1,-t), new Vector3( 0, 1,-t),
                new Vector3( t, 0,-1), new Vector3( t, 0, 1), new Vector3(-t, 0,-1), new Vector3(-t, 0, 1),
            };
            var tris = new List<int>
            {
                0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11,
                1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9,
                4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1
            };

            for (int s = 0; s < subdiv; s++)
            {
                var mid = new Dictionary<long, int>();
                int Midpoint(int a, int b)
                {
                    long key = (long)Mathf.Min(a, b) << 32 | (uint)Mathf.Max(a, b);
                    if (mid.TryGetValue(key, out var idx)) return idx;
                    var m = ((verts[a] + verts[b]) * 0.5f).normalized;
                    idx = verts.Count; verts.Add(m); mid[key] = idx; return idx;
                }
                var newTris = new List<int>(tris.Count * 4);
                for (int i = 0; i < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                    newTris.Add(a); newTris.Add(ab); newTris.Add(ca);
                    newTris.Add(b); newTris.Add(bc); newTris.Add(ab);
                    newTris.Add(c); newTris.Add(ca); newTris.Add(bc);
                    newTris.Add(ab); newTris.Add(bc); newTris.Add(ca);
                }
                tris = newTris;
            }

            var pos = new Vector3[verts.Count];
            var uv = new Vector2[verts.Count];
            for (int i = 0; i < verts.Count; i++)
            {
                pos[i] = verts[i].normalized * radius;
                uv[i] = new Vector2(pos[i].x * 0.5f / radius + 0.5f, pos[i].z * 0.5f / radius + 0.5f);
            }
            return Build(pos, tris.ToArray(), uv);
        }

        // Torus around Y axis: major radius R, tube radius r.
        // segMajor = around major circle, segMinor = around tube cross-section.
        public static Mesh Torus(float R = 1f, float r = 0.3f, int segMajor = 32, int segMinor = 16)
        {
            int vCount = segMajor * segMinor;
            var pos = new Vector3[vCount];
            var uv = new Vector2[vCount];
            for (int i = 0; i < segMajor; i++)
            {
                float u = (float)i / segMajor;
                float a = u * Mathf.PI * 2f;
                Vector3 center = new Vector3(Mathf.Cos(a) * R, 0, Mathf.Sin(a) * R);
                Vector3 outRad = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
                for (int j = 0; j < segMinor; j++)
                {
                    float v = (float)j / segMinor;
                    float b = v * Mathf.PI * 2f;
                    Vector3 p = center + outRad * (Mathf.Cos(b) * r) + Vector3.up * (Mathf.Sin(b) * r);
                    int idx = i * segMinor + j;
                    pos[idx] = p;
                    uv[idx] = new Vector2(u, v);
                }
            }
            var tris = new int[segMajor * segMinor * 6];
            int ti = 0;
            for (int i = 0; i < segMajor; i++)
            {
                int ni = (i + 1) % segMajor;
                for (int j = 0; j < segMinor; j++)
                {
                    int nj = (j + 1) % segMinor;
                    int a = i * segMinor + j;
                    int b = ni * segMinor + j;
                    int c = ni * segMinor + nj;
                    int d = i * segMinor + nj;
                    tris[ti++] = a; tris[ti++] = b; tris[ti++] = c;
                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = d;
                }
            }
            return Build(pos, tris, uv);
        }

        // L-shape extruded prism: cross-section is L in XZ-plane, extruded along Y.
        // Cross-section (XZ): outer rect [-1,1] x [-1,1] minus inner notch [0,1] x [0,1].
        // Concave. Useful to test concave caps after slicing along Y.
        public static Mesh LShapeExtruded(float height = 2f)
        {
            // L outline. Unity is left-handed (+X × +Z = -Y), so for +Y outward normal on
            // the top cap, outline must be CW in (X-right, Z-up) view.
            Vector2[] outline =
            {
                new Vector2(-1f, -1f),
                new Vector2(-1f,  1f),
                new Vector2( 0f,  1f),
                new Vector2( 0f,  0f),
                new Vector2( 1f,  0f),
                new Vector2( 1f, -1f),
            };
            // Extra inner corner needed to triangulate the L cap into two rectangles.
            Vector2 extra = new Vector2(0f, -1f);

            float yLo = -height * 0.5f, yHi = height * 0.5f;
            int n = outline.Length;
            var pos = new List<Vector3>();
            var uv  = new List<Vector2>();
            var tris = new List<int>();

            for (int i = 0; i < n; i++) { pos.Add(new Vector3(outline[i].x, yLo, outline[i].y)); uv.Add(Vector2.zero); }
            for (int i = 0; i < n; i++) { pos.Add(new Vector3(outline[i].x, yHi, outline[i].y)); uv.Add(Vector2.zero); }
            int botExtra = pos.Count; pos.Add(new Vector3(extra.x, yLo, extra.y)); uv.Add(Vector2.zero);
            int topExtra = pos.Count; pos.Add(new Vector3(extra.x, yHi, extra.y)); uv.Add(Vector2.zero);

            int Lo(int i) => i;
            int Hi(int i) => n + i;

            // Side faces. Outline is CW from above → for outward side normals, wind each
            // quad as (lo_i, hi_i, hi_(i+1), lo_(i+1)).
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                int a = Lo(i), b = Hi(i), c = Hi(j), d = Lo(j);
                tris.Add(a); tris.Add(b); tris.Add(c);
                tris.Add(a); tris.Add(c); tris.Add(d);
            }

            // Top cap (+Y normal): L = rect [0,1,2,topExtra] ∪ rect [topExtra,3,4,5].
            // Outline 0=(-1,-1), 1=(-1,1), 2=(0,1), 3=(0,0), 4=(1,0), 5=(1,-1), extra=(0,-1).
            // Rect A: (-1,-1)→(-1,1)→(0,1)→(0,-1). CW from above → +Y normal.
            // Rect B: (0,-1)→(0,0)→(1,0)→(1,-1). CW from above → +Y normal.
            tris.Add(Hi(0)); tris.Add(Hi(1)); tris.Add(Hi(2));
            tris.Add(Hi(0)); tris.Add(Hi(2)); tris.Add(topExtra);
            tris.Add(topExtra); tris.Add(Hi(3)); tris.Add(Hi(4));
            tris.Add(topExtra); tris.Add(Hi(4)); tris.Add(Hi(5));

            // Bottom cap (-Y normal): reverse winding.
            tris.Add(Lo(0)); tris.Add(Lo(2)); tris.Add(Lo(1));
            tris.Add(Lo(0)); tris.Add(botExtra); tris.Add(Lo(2));
            tris.Add(botExtra); tris.Add(Lo(4)); tris.Add(Lo(3));
            tris.Add(botExtra); tris.Add(Lo(5)); tris.Add(Lo(4));

            return Build(pos.ToArray(), tris.ToArray(), uv.ToArray());
        }

        static Mesh Build(Vector3[] pos, int[] tri, Vector2[] uv)
        {
            var m = new Mesh { name = "TestMesh" };
            m.indexFormat = pos.Length > 65535
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.vertices = pos;
            m.uv = uv;
            m.triangles = tri;
            m.RecalculateNormals();
            m.RecalculateTangents();
            m.RecalculateBounds();
            return m;
        }
    }
}
