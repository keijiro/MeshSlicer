using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests
{
    internal static class MeshAssert
    {
        public static float SurfaceArea(Mesh m)
        {
            var v = m.vertices; var t = m.triangles; float s = 0;
            for (int i = 0; i < t.Length; i += 3)
                s += Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).magnitude * 0.5f;
            return s;
        }

        public static float SignedVolume(Mesh m)
        {
            var v = m.vertices; var t = m.triangles; float s = 0;
            for (int i = 0; i < t.Length; i += 3)
                s += Vector3.Dot(v[t[i]], Vector3.Cross(v[t[i + 1]], v[t[i + 2]])) / 6f;
            return s;
        }

        // All vertices reachable from triangles must satisfy plane sign within tolerance.
        public static void AssertAllVerticesOnSide(Mesh m, Plane plane, int sign, float tol = 1e-4f)
        {
            var v = m.vertices; var t = m.triangles;
            var used = new HashSet<int>();
            for (int i = 0; i < t.Length; i++) used.Add(t[i]);
            foreach (var idx in used)
            {
                float d = plane.SignedDistanceToPoint(v[idx]);
                if (sign > 0) Assert.GreaterOrEqual(d, -tol, $"vertex {idx} on wrong side d={d}");
                else if (sign < 0) Assert.LessOrEqual(d, tol, $"vertex {idx} on wrong side d={d}");
            }
        }

        // Returns triangles whose all-3-vertices distance to the plane is <= tol (those are cap triangles).
        public static List<int> CapTriangleIndices(Mesh m, Plane plane, float tol = 1e-4f)
        {
            var v = m.vertices; var t = m.triangles; var res = new List<int>();
            for (int i = 0; i < t.Length; i += 3)
            {
                var d0 = plane.SignedDistanceToPoint(v[t[i]]);
                var d1 = plane.SignedDistanceToPoint(v[t[i + 1]]);
                var d2 = plane.SignedDistanceToPoint(v[t[i + 2]]);
                if (Mathf.Abs(d0) <= tol && Mathf.Abs(d1) <= tol && Mathf.Abs(d2) <= tol) res.Add(i);
            }
            return res;
        }

        public static float CapArea(Mesh m, Plane plane, float tol = 1e-4f)
        {
            var v = m.vertices; var t = m.triangles;
            var caps = CapTriangleIndices(m, plane, tol);
            float s = 0;
            foreach (var i in caps)
                s += Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).magnitude * 0.5f;
            return s;
        }

        // All-cap-triangle vertex normals should align with expectedNormal within tol radians.
        public static void AssertCapNormals(Mesh m, Plane plane, Vector3 expectedNormal, float dotTol = 0.95f)
        {
            var n = m.normals; var t = m.triangles;
            var caps = CapTriangleIndices(m, plane);
            Assert.Greater(caps.Count, 0, "expected at least one cap triangle");
            foreach (var i in caps)
            {
                for (int k = 0; k < 3; k++)
                {
                    var nv = n[t[i + k]].normalized;
                    var d = Vector3.Dot(nv, expectedNormal.normalized);
                    Assert.GreaterOrEqual(d, dotTol, $"cap normal {nv} not aligned with {expectedNormal}");
                }
            }
        }

        // Count unique boundary loops of the cap submesh: edges that are used by exactly 1 cap triangle.
        public static int CapBoundaryLoopCount(Mesh m, Plane plane, float tol = 1e-4f)
        {
            var t = m.triangles;
            var caps = CapTriangleIndices(m, plane, tol);
            var edgeCount = new Dictionary<(int, int), int>();
            void AddEdge(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                edgeCount.TryGetValue(key, out var c);
                edgeCount[key] = c + 1;
            }
            foreach (var i in caps)
            {
                AddEdge(t[i], t[i + 1]);
                AddEdge(t[i + 1], t[i + 2]);
                AddEdge(t[i + 2], t[i]);
            }

            // Build adjacency among boundary edges (degree-2 graph) and count connected components.
            var adj = new Dictionary<int, List<int>>();
            foreach (var kv in edgeCount)
            {
                if (kv.Value != 1) continue;
                var (a, b) = kv.Key;
                if (!adj.TryGetValue(a, out var la)) { la = new List<int>(); adj[a] = la; }
                if (!adj.TryGetValue(b, out var lb)) { lb = new List<int>(); adj[b] = lb; }
                la.Add(b); lb.Add(a);
            }
            var visited = new HashSet<int>();
            int loops = 0;
            foreach (var v in adj.Keys)
            {
                if (visited.Contains(v)) continue;
                loops++;
                var stack = new Stack<int>(); stack.Push(v);
                while (stack.Count > 0)
                {
                    var x = stack.Pop();
                    if (!visited.Add(x)) continue;
                    foreach (var y in adj[x]) if (!visited.Contains(y)) stack.Push(y);
                }
            }
            return loops;
        }
    }
}
