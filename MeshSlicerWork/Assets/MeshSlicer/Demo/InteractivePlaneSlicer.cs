using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Demo {

// Interactive slicing preview. Cuts SourceMesh with the plane defined by the
// Plane transform (its position and forward axis) and shows the two resulting
// halves pulled slightly apart along the plane normal. The halves are rebuilt
// whenever the plane transform changes; obsolete meshes are destroyed. Runs in
// the editor so the plane can be dragged and the result inspected in real time.
[ExecuteAlways]
public sealed class InteractivePlaneSlicer : MonoBehaviour
{
    [SerializeField] Mesh _sourceMesh = null;
    [SerializeField] Transform _plane = null;
    [SerializeField] Material _material = null;
    [SerializeField, Range(0f, 0.5f)] float _gap = 0.12f;
    [SerializeField] bool _useBurst = true;

    Transform _a, _b;
    MeshFilter _mfA, _mfB;
    MeshRenderer _mrA, _mrB;
    Mesh _meshA, _meshB;
    Matrix4x4 _lastPlane;
    bool _valid;

    void OnEnable() => _valid = false;
    void OnValidate() => _valid = false;

    void OnDisable()
    {
        DestroyObj(_meshA); DestroyObj(_meshB);
        _meshA = _meshB = null;
        if (_a != null) DestroyObj(_a.gameObject);
        if (_b != null) DestroyObj(_b.gameObject);
        _a = _b = null;
    }

    void Update()
    {
        if (_sourceMesh == null || _plane == null) return;

        var m = _plane.localToWorldMatrix;
        if (_valid && m == _lastPlane) return; // nothing changed
        _lastPlane = m;
        _valid = true;
        Rebuild();
    }

    public void Rebuild()
    {
        if (_sourceMesh == null || _plane == null) return;
        EnsureChildren();

        // Express the plane in the source mesh's local space.
        var normal = math.normalizesafe(
            (float3)transform.InverseTransformDirection(_plane.forward), new float3(0, 1, 0));
        var point = (float3)transform.InverseTransformPoint(_plane.position);
        var plane = new Plane(normal, point);

        // Discard the previous outputs before creating new ones.
        DestroyObj(_meshA); DestroyObj(_meshB);
        _meshA = _meshB = null;

        SliceResult r;
        try
        {
            r = _useBurst ? BurstSlicer.Slice(_sourceMesh, plane) : Slicer.Slice(_sourceMesh, plane);
        }
        catch (System.Exception e)
        {
            Debug.LogException(e, this);
            return;
        }

        _meshA = r.Positive;
        _meshB = r.Negative;
        if (_meshA != null) _meshA.hideFlags = HideFlags.DontSave;
        if (_meshB != null) _meshB.hideFlags = HideFlags.DontSave;

        var off = (Vector3)(normal * (_gap * _sourceMesh.bounds.size.magnitude));
        _mfA.sharedMesh = _meshA; _mrA.enabled = _meshA != null; _a.localPosition = off;
        _mfB.sharedMesh = _meshB; _mrB.enabled = _meshB != null; _b.localPosition = -off;
    }

    void EnsureChildren()
    {
        if (_a == null) (_a, _mfA, _mrA) = MakeChild("PositiveHalf");
        if (_b == null) (_b, _mfB, _mrB) = MakeChild("NegativeHalf");
        _mrA.sharedMaterial = _material;
        _mrB.sharedMaterial = _material;
    }

    (Transform, MeshFilter, MeshRenderer) MakeChild(string name)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(transform, false);
        return (go.transform, go.AddComponent<MeshFilter>(), go.AddComponent<MeshRenderer>());
    }

    void DestroyObj(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }
}

} // namespace MeshSlicer.Demo
