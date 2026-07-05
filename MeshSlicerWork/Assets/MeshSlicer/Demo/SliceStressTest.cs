using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Demo {

// System-level benchmark: slices a hi-poly mesh with a moving plane every frame
// and renders both halves, measuring both the slice cost and the whole frame.
public sealed class SliceStressTest : MonoBehaviour
{
    public int Subdivisions = 5; // icosphere: 5 -> 20480 tris, 6 -> 81920
    public bool UseBurst = true;

    // Latest measurements (read by the benchmark driver).
    public static double LastAvgSliceMs;
    public static double LastAvgFrameMs;
    public static int SourceTriangles;
    public static int WindowFrames;

    Mesh _source;
    MeshFilter _a, _b;
    MeshRenderer _ra, _rb;
    readonly Stopwatch _sw = new();

    double _sliceAccum, _frameAccum;
    int _frames;

    void OnEnable()
    {
        _source = DemoMeshes.Icosphere(1f, Subdivisions);
        SourceTriangles = _source.triangles.Length / 3;

        (_a, _ra) = MakeChild("HalfA", new Color(0.9f, 0.35f, 0.25f));
        (_b, _rb) = MakeChild("HalfB", new Color(0.25f, 0.55f, 0.9f));
    }

    void OnDisable()
    {
        if (_source != null) DestroyImmediate(_source);
        for (var i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    (MeshFilter, MeshRenderer) MakeChild(string name, Color c)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = new Material(Shader.Find("Standard")) { color = c };
        return (mf, mr);
    }

    void Update()
    {
        var time = Application.isPlaying ? Time.time : (float)(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        var n = math.normalize(new float3(math.cos(time), 1f, math.sin(time * 0.7f)));
        var plane = new Plane(n, 0.1f * math.sin(time * 1.3f));

        if (UseBurst)
        {
            // Reuse the two output meshes to avoid per-frame allocation churn.
            if (_a.sharedMesh == null) _a.sharedMesh = new Mesh { name = "HalfA" };
            if (_b.sharedMesh == null) _b.sharedMesh = new Mesh { name = "HalfB" };
            _sw.Restart();
            BurstSlicer.Slice(_source, plane, _a.sharedMesh, _b.sharedMesh);
            _sw.Stop();
        }
        else
        {
            if (_a.sharedMesh != null) DestroyImmediate(_a.sharedMesh);
            if (_b.sharedMesh != null) DestroyImmediate(_b.sharedMesh);
            _sw.Restart();
            var r = Slicer.Slice(_source, plane);
            _sw.Stop();
            _a.sharedMesh = r.Positive;
            _b.sharedMesh = r.Negative;
        }

        _sliceAccum += _sw.Elapsed.TotalMilliseconds;
        _frameAccum += Time.unscaledDeltaTime * 1000.0;
        if (++_frames >= 60)
        {
            LastAvgSliceMs = _sliceAccum / _frames;
            LastAvgFrameMs = _frameAccum / _frames;
            WindowFrames = _frames;
            _sliceAccum = _frameAccum = 0;
            _frames = 0;
        }
    }
}

} // namespace MeshSlicer.Demo
