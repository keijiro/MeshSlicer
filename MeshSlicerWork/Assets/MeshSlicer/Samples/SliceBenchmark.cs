using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Samples {

// Slices a hi-poly mesh every frame with an animated plane and renders both halves,
// measuring both the slice-only cost and the whole-frame cost (render included).
// Exposes rolling averages as static fields so a CLI harness can read them.
public sealed class SliceBenchmark : MonoBehaviour
{
    public enum Impl { Naive, Burst }

    [SerializeField] Impl _impl = Impl.Naive;
    [SerializeField] int _subdiv = 5;

    public static double SliceMs;   // rolling average, slice call only
    public static double FrameMs;   // rolling average, full unscaled frame
    public static int TriangleCount;
    public static string ImplName = "";

    Mesh _source;
    MeshFilter _posFilter, _negFilter;
    Mesh _reusePos, _reuseNeg;      // reused targets for the non-allocating Burst path
    float _t;
    int _frames;
    double _accSlice, _accFrame;

    void Start()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;

        _source = ProceduralMeshes.IcoSphere(_subdiv, 0.5f);
        TriangleCount = _source.triangles.Length / 3;
        ImplName = _impl.ToString();

        _posFilter = MakeChild(new Color(0.85f, 0.5f, 0.3f));
        _negFilter = MakeChild(new Color(0.3f, 0.55f, 0.85f));
        _reusePos = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        _reuseNeg = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };

        BuildCameraAndLight();
    }

    void Update()
    {
        _t += Time.deltaTime;
        var n = math.normalize(new float3(math.cos(_t), 0.6f, math.sin(_t * 0.7f)));
        var offset = 0.15f * math.sin(_t * 1.3f);
        var plane = new Plane(n, new float3(offset * n));

        var sw = Stopwatch.StartNew();
        if (_impl == Impl.Burst)
        {
            BurstSlicer.Slice(_source, plane, _reusePos, _reuseNeg);
            _posFilter.sharedMesh = _reusePos;
            _negFilter.sharedMesh = _reuseNeg;
        }
        else
        {
            var r = Slicer.Slice(_source, plane);
            if (_posFilter.sharedMesh != null) Destroy(_posFilter.sharedMesh);
            if (_negFilter.sharedMesh != null) Destroy(_negFilter.sharedMesh);
            _posFilter.sharedMesh = r.Positive;
            _negFilter.sharedMesh = r.Negative;
        }
        sw.Stop();

        _accSlice += sw.Elapsed.TotalMilliseconds;
        _accFrame += Time.unscaledDeltaTime * 1000.0;
        if (++_frames >= 60)
        {
            SliceMs = _accSlice / _frames;
            FrameMs = _accFrame / _frames;
            _accSlice = _accFrame = 0;
            _frames = 0;
        }
    }

    MeshFilter MakeChild(Color c)
    {
        var go = new GameObject("Half");
        go.transform.SetParent(transform, false);
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = new Material(Shader.Find("Standard")) { color = c };
        return mf;
    }

    void BuildCameraAndLight()
    {
        var camGo = new GameObject("BenchCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
        camGo.transform.position = new Vector3(0, 0.4f, -1.6f);
        camGo.transform.LookAt(Vector3.zero);

        var lightGo = new GameObject("BenchLight");
        lightGo.AddComponent<Light>().type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(40, -30, 0);
    }
}

} // namespace MeshSlicer.Samples
