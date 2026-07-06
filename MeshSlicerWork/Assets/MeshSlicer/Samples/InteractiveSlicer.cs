using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Samples {

// Interactive, edit-time slicing tool. Attach to the object that holds the source
// mesh, point _plane at a Transform used as the cutting plane (position = a point
// on the plane, up = plane normal), and drag that Transform in the Editor: the two
// halves are rebuilt live and drawn slightly pulled apart. Old halves and their
// meshes are destroyed before each rebuild, so nothing leaks.
[ExecuteAlways]
public sealed class InteractiveSlicer : MonoBehaviour
{
    [SerializeField] Mesh _sourceMesh;
    [SerializeField] Transform _plane;
    [SerializeField] float _explode = 0.15f;
    [SerializeField] Color _positiveColor = new(0.85f, 0.5f, 0.3f);
    [SerializeField] Color _negativeColor = new(0.3f, 0.55f, 0.85f);

    const string PosName = "__slice_positive";
    const string NegName = "__slice_negative";

    GameObject _posGO, _negGO;
    Mesh _posMesh, _negMesh;
    Material _posMat, _negMat;

    // Cached state used to detect when a rebuild is needed.
    Matrix4x4 _lastPlane, _lastSelf;
    float _lastExplode;
    Mesh _lastMesh;
    bool _built;

    void OnEnable()
    {
        DestroyLeftovers();
        _built = false;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update += EditorTick;
#endif
    }

    void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorTick;
#endif
        Cleanup();
    }

    // Play-mode driver.
    void Update() { if (Application.isPlaying) Tick(); }

#if UNITY_EDITOR
    // Edit-mode driver: fires continuously in the Editor so dragging the plane
    // rebuilds the halves live without needing play mode.
    void EditorTick() { if (this != null && !Application.isPlaying) Tick(); }
#endif

    void Tick()
    {
        if (_sourceMesh == null || _plane == null) { Cleanup(); return; }

        var planeM = _plane.localToWorldMatrix;
        var selfM = transform.localToWorldMatrix;
        if (_built && planeM == _lastPlane && selfM == _lastSelf &&
            _lastExplode == _explode && _lastMesh == _sourceMesh) return;

        Rebuild();

        _lastPlane = planeM; _lastSelf = selfM; _lastExplode = _explode; _lastMesh = _sourceMesh;
        _built = true;
        if (_plane != null) _plane.hasChanged = false;
    }

    void Rebuild()
    {
        // Plane expressed in this object's (the source mesh's) local space.
        var nLocal = math.normalize((float3)transform.InverseTransformDirection(_plane.up));
        var pLocal = (float3)transform.InverseTransformPoint(_plane.position);
        var plane = new Plane(nLocal, pLocal);

        var r = Slicer.Slice(_sourceMesh, plane);

        ReplaceMesh(ref _posMesh, r.Positive);
        ReplaceMesh(ref _negMesh, r.Negative);

        EnsureMaterials();
        _posGO = EnsureChild(_posGO, PosName, _posMesh, _posMat, (Vector3)(nLocal * _explode));
        _negGO = EnsureChild(_negGO, NegName, _negMesh, _negMat, (Vector3)(-nLocal * _explode));
    }

    static void ReplaceMesh(ref Mesh slot, Mesh replacement)
    {
        if (slot != null) SafeDestroy(slot);
        slot = replacement;
        if (slot != null) slot.hideFlags = HideFlags.DontSave;
    }

    GameObject EnsureChild(GameObject go, string name, Mesh mesh, Material mat, Vector3 localOffset)
    {
        if (mesh == null)
        {
            if (go != null) SafeDestroy(go);
            return null;
        }
        if (go == null)
        {
            go = new GameObject(name) { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
        }
        go.transform.localPosition = localOffset;
        go.transform.localRotation = Quaternion.identity;
        go.GetComponent<MeshFilter>().sharedMesh = mesh;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    void EnsureMaterials()
    {
        var shader = Shader.Find("Standard");
        if (_posMat == null) _posMat = new Material(shader) { hideFlags = HideFlags.DontSave };
        if (_negMat == null) _negMat = new Material(shader) { hideFlags = HideFlags.DontSave };
        _posMat.color = _positiveColor;
        _negMat.color = _negativeColor;
    }

    void Cleanup()
    {
        if (_posGO != null) SafeDestroy(_posGO);
        if (_negGO != null) SafeDestroy(_negGO);
        if (_posMesh != null) SafeDestroy(_posMesh);
        if (_negMesh != null) SafeDestroy(_negMesh);
        _posGO = _negGO = null;
        _posMesh = _negMesh = null;
        _built = false;
    }

    // Remove any generated children orphaned by a domain reload / scene reopen.
    void DestroyLeftovers()
    {
        for (var i = transform.childCount - 1; i >= 0; i--)
        {
            var c = transform.GetChild(i);
            if (c.name == PosName || c.name == NegName) SafeDestroy(c.gameObject);
        }
    }

    static void SafeDestroy(Object o)
    {
        if (o == null) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }
}

} // namespace MeshSlicer.Samples
