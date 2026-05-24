using System.Diagnostics;
using UnityEngine;
using PlaneT = Unity.Mathematics.Geometry.Plane;
using Unity.Mathematics;

namespace MeshSlicer.Demo
{
    // Synchronous benchmark harness: runs a slicer N times with a sweeping plane and
    // returns timing stats. Suitable for invocation from the MCP RunCommand without
    // needing Play mode.
    public static class SliceBenchmarkRunner
    {
        public delegate SliceResult Slicer(Mesh src, PlaneT plane);

        public struct Stats { public float avgMs, minMs, maxMs; public int iterations; }

        public static Stats Run(Mesh source, Slicer slicer, int iterations, int warmup = 5)
        {
            for (int i = 0; i < warmup; i++)
            {
                var p = PlaneAt(i, warmup);
                var r = slicer(source, p);
                Dispose(r);
            }

            var sw = new Stopwatch();
            float total = 0, min = float.MaxValue, max = 0;
            for (int i = 0; i < iterations; i++)
            {
                var p = PlaneAt(i, iterations);
                sw.Restart();
                var r = slicer(source, p);
                sw.Stop();
                float ms = (float)sw.Elapsed.TotalMilliseconds;
                total += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
                Dispose(r);
            }
            return new Stats { avgMs = total / iterations, minMs = min, maxMs = max, iterations = iterations };
        }

        static PlaneT PlaneAt(int i, int total)
        {
            float t = (float)i / Mathf.Max(1, total);
            float a = t * Mathf.PI * 2f;
            var n = math.normalize(new float3(math.cos(a), 0.6f, math.sin(a)));
            return PlaneT.Normalize(new PlaneT(n, float3.zero));
        }

        static void Dispose(SliceResult r)
        {
            if (r.Positive != null) Object.DestroyImmediate(r.Positive);
            if (r.Negative != null) Object.DestroyImmediate(r.Negative);
        }
    }
}
