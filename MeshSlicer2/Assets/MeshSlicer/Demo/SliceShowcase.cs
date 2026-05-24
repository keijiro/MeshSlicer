using System.Collections.Generic;
using UnityEngine;
using PlaneT = Unity.Mathematics.Geometry.Plane;
using Unity.Mathematics;

namespace MeshSlicer.Demo
{
    // Drop this on a GameObject and assign a Mesh + Material. On enable it slices the
    // mesh along the local-space plane and renders the two halves pushed apart along
    // the plane normal so the cut faces are visible.
    [ExecuteAlways]
    public sealed class SliceShowcase : MonoBehaviour
    {
        public Mesh sourceMesh;
        public Material material;
        public Vector3 planeNormal = Vector3.up;
        public float planeOffset = 0f;
        public float separation = 0.4f;

        GameObject _positiveGO, _negativeGO;

        void OnEnable() => Rebuild();
        void OnValidate() { if (isActiveAndEnabled) Rebuild(); }
        void OnDisable() => Cleanup();

        public void Rebuild()
        {
            Cleanup();
            if (sourceMesh == null) return;

            var n = planeNormal.sqrMagnitude > 1e-12f ? planeNormal.normalized : Vector3.up;
            var plane = PlaneT.Normalize(new PlaneT((float3)n, (float3)(n * planeOffset)));
            var result = NaiveMeshSlicer.Slice(sourceMesh, plane);

            if (result.Positive != null)
                _positiveGO = MakeChild("PositiveHalf", result.Positive,  n * separation * 0.5f);
            if (result.Negative != null)
                _negativeGO = MakeChild("NegativeHalf", result.Negative, -n * separation * 0.5f);
        }

        void Cleanup()
        {
            if (_positiveGO != null) DestroyImmediate(_positiveGO);
            if (_negativeGO != null) DestroyImmediate(_negativeGO);
            _positiveGO = _negativeGO = null;
        }

        GameObject MakeChild(string name, Mesh mesh, Vector3 localOffset)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localOffset;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (material != null) mr.sharedMaterial = material;
            return go;
        }
    }
}
