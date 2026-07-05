using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Demo {

// Slices a set of primitives with a plane and renders each half as an exploded
// pair so the generated caps are visible. Runs in edit mode.
[ExecuteAlways]
public sealed class SliceDemo : MonoBehaviour
{
    public float3 PlaneNormal = new(0.15f, 1f, 0.35f);
    public float PlaneOffset = 0.05f;
    public float Explode = 0.35f;
    public bool UseBurst = true;

    Material _matA, _matB;

    void OnEnable() => Rebuild();
    void OnValidate() { if (isActiveAndEnabled) Rebuild();  }

    void Rebuild()
    {
        for (var i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);

        _matA = new Material(Shader.Find("Standard")) { color = new Color(0.9f, 0.35f, 0.25f) };
        _matB = new Material(Shader.Find("Standard")) { color = new Color(0.25f, 0.55f, 0.9f) };

        var shapes = new[]
        {
            DemoMeshes.Cube(1.4f),
            DemoMeshes.Icosphere(0.85f, 3),
            DemoMeshes.Cylinder(0.6f, 1.5f, 40),
        };

        var n = math.normalize(PlaneNormal);
        var plane = new Plane(n, PlaneOffset);
        var x = -2.6f;
        foreach (var src in shapes)
        {
            var r = UseBurst ? BurstSlicer.Slice(src, plane) : Slicer.Slice(src, plane);
            var root = new GameObject(src.name);
            root.transform.SetParent(transform, false);
            root.transform.localPosition = new Vector3(x, 0, 0);
            Spawn(root.transform, r.Positive, _matA, (float3)n * Explode);
            Spawn(root.transform, r.Negative, _matB, -(float3)n * Explode);
            x += 2.6f;
        }
    }

    void Spawn(Transform parent, Mesh mesh, Material mat, float3 offset)
    {
        if (mesh == null) return;
        var go = new GameObject(mesh.name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = (Vector3)offset;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
    }
}

} // namespace MeshSlicer.Demo
