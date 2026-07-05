using System.Diagnostics;
using UnityEngine;
using PlaneT = Unity.Mathematics.Geometry.Plane;
using Unity.Mathematics;

namespace MeshSlicer.Demo
{
    // Re-slices `sourceMesh` every LateUpdate with a slowly-rotating plane, renders both
    // halves with MeshRenderer, and prints rolling slice/frame timings as an on-screen
    // overlay so we can compare implementations.
    [ExecuteAlways]
    public sealed class SliceBenchmark : MonoBehaviour
    {
        public Mesh sourceMesh;
        public Material material;
        public bool spin = true;
        public float separation = 0.4f;

        public delegate SliceResult Slicer(Mesh src, PlaneT plane);
        public Slicer slicer = NaiveMeshSlicer.Slice;
        public string slicerName = "Naive";

        readonly Stopwatch _sw = new Stopwatch();
        readonly RollingAverage _sliceMs = new RollingAverage(120);
        readonly RollingAverage _frameMs = new RollingAverage(120);

        MeshFilter _posMF, _negMF;
        MeshRenderer _posMR, _negMR;
        GameObject _posGO, _negGO;
        Mesh _prevPos, _prevNeg;
        float _spin;

        void OnEnable()
        {
            _posGO = MakeChild("PosHalf", out _posMF, out _posMR);
            _negGO = MakeChild("NegHalf", out _negMF, out _negMR);
        }

        void OnDisable()
        {
            if (_posGO) DestroyImmediate(_posGO);
            if (_negGO) DestroyImmediate(_negGO);
            DestroyMesh(ref _prevPos); DestroyMesh(ref _prevNeg);
        }

        void LateUpdate() => Tick(Time.deltaTime, Time.unscaledDeltaTime);

        public void Tick(float dt, float udt)
        {
            if (sourceMesh == null) return;
            if (spin) _spin += dt * 60f;

            var n = math.normalize(new float3(
                math.cos(math.radians(_spin)),
                0.5f,
                math.sin(math.radians(_spin))));
            var plane = PlaneT.Normalize(new PlaneT(n, float3.zero));

            _sw.Restart();
            var r = slicer(sourceMesh, plane);
            _sw.Stop();
            _sliceMs.Push((float)_sw.Elapsed.TotalMilliseconds);
            _frameMs.Push(udt * 1000f);

            ReplaceMesh(_posMF, ref _prevPos, r.Positive,  (Vector3)n * separation * 0.5f);
            ReplaceMesh(_negMF, ref _prevNeg, r.Negative, -(Vector3)n * separation * 0.5f);
        }

        GameObject MakeChild(string name, out MeshFilter mf, out MeshRenderer mr)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            mf = go.AddComponent<MeshFilter>();
            mr = go.AddComponent<MeshRenderer>();
            if (material != null) mr.sharedMaterial = material;
            return go;
        }

        void ReplaceMesh(MeshFilter mf, ref Mesh prev, Mesh m, Vector3 localOffset)
        {
            if (prev != null) DestroyImmediate(prev);
            prev = m;
            mf.sharedMesh = m;
            mf.transform.localPosition = localOffset;
        }

        static void DestroyMesh(ref Mesh m) { if (m) DestroyImmediate(m); m = null; }

        void OnGUI()
        {
            GUI.Box(new Rect(10, 10, 360, 84), GUIContent.none);
            GUI.skin.label.fontSize = 14;
            int srcTris = sourceMesh != null ? sourceMesh.triangles.Length / 3 : 0;
            GUI.Label(new Rect(20, 14, 360, 24), $"Slicer: {slicerName}  |  src tris: {srcTris:N0}");
            GUI.Label(new Rect(20, 36, 360, 24), $"Slice: {_sliceMs.Avg,6:F2} ms (max {_sliceMs.Max,6:F2})");
            GUI.Label(new Rect(20, 58, 360, 24), $"Frame: {_frameMs.Avg,6:F2} ms  ({1000f / Mathf.Max(_frameMs.Avg, 0.01f),5:F1} fps)");
        }

        sealed class RollingAverage
        {
            readonly float[] _buf; int _i, _count;
            public RollingAverage(int n) { _buf = new float[n]; }
            public void Push(float v)
            {
                _buf[_i] = v; _i = (_i + 1) % _buf.Length;
                if (_count < _buf.Length) _count++;
            }
            public float Avg
            {
                get { float s = 0; for (int i = 0; i < _count; i++) s += _buf[i]; return _count > 0 ? s / _count : 0; }
            }
            public float Max
            {
                get { float m = 0; for (int i = 0; i < _count; i++) if (_buf[i] > m) m = _buf[i]; return m; }
            }
        }
    }
}
