using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Samples {

// Slices a row of representative meshes and renders the two halves pulled apart
// along the cut normal so the caps (and holes) are visible. Self-contained: builds
// its own camera and light so a scene only needs this one component.
public sealed class SliceDemo : MonoBehaviour
{
    [SerializeField] Mesh _crateMesh;   // assigned from the imported FBX
    [SerializeField] float _explode = 0.35f;
    [SerializeField] float _spacing = 1.9f;

    void Start()
    {
        var items = new List<(Mesh mesh, float3 nrm, float3 pt, float scale)>
        {
            (ProceduralMeshes.Cube(1f),         new float3(0.3f, 1, 0.15f), float3.zero,          1f),
            (ProceduralMeshes.IcoSphere(3, 0.5f), new float3(0.25f, 1, 0.4f), float3.zero,         1f),
            (ProceduralMeshes.Torus(0.6f, 0.22f), new float3(0, 1, 0),        float3.zero,          1f),
            (ProceduralMeshes.Pipe(0.5f, 0.3f, 1f), new float3(0, 1, 0),      float3.zero,          1f),
            (ProceduralMeshes.Tray(1f, 1f, 0.7f, 0.18f), new float3(0, 1, 0), float3.zero,          1f),
            (ProceduralMeshes.Bowl(0.5f, 0.08f), new float3(0, 1, 0),         new float3(0, -0.15f, 0), 1f),
        };
        if (_crateMesh != null)
        {
            var c = (float3)_crateMesh.bounds.center;
            var s = 1f / math.max(math.cmax((float3)_crateMesh.bounds.size), 1e-3f);
            items.Add((_crateMesh, new float3(0.2f, 1, 0.1f), c, s));
        }

        var matPos = MakeMaterial(new Color(0.85f, 0.5f, 0.3f));
        var matNeg = MakeMaterial(new Color(0.3f, 0.55f, 0.85f));

        var startX = -(items.Count - 1) * 0.5f * _spacing;
        for (var i = 0; i < items.Count; i++)
        {
            var (mesh, nrm, pt, scale) = items[i];
            var n = math.normalize(nrm);
            var plane = new Plane(n, pt);
            var r = Slicer.Slice(mesh, plane);

            var root = new GameObject($"Slice_{mesh.name}");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = new Vector3(startX + i * _spacing, 0, 0);
            root.transform.localScale = Vector3.one * scale;

            if (r.Positive != null) Spawn(root.transform, r.Positive, matPos, (Vector3)(n * _explode / scale), pt);
            if (r.Negative != null) Spawn(root.transform, r.Negative, matNeg, (Vector3)(-n * _explode / scale), pt);
        }

        BuildCameraAndLight(items.Count);
    }

    static void Spawn(Transform parent, Mesh mesh, Material mat, Vector3 offset, float3 pivot)
    {
        var go = new GameObject(mesh.name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = offset - (Vector3)pivot;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
    }

    static Material MakeMaterial(Color c)
    {
        var m = new Material(Shader.Find("Standard")) { color = c };
        m.SetFloat("_Glossiness", 0.2f);
        return m;
    }

    void BuildCameraAndLight(int count)
    {
        var width = count * _spacing;

        var camGo = new GameObject("DemoCamera");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.12f, 0.14f);
        cam.fieldOfView = 42;
        camGo.transform.position = new Vector3(0, width * 0.32f, -width * 0.62f);
        camGo.transform.LookAt(new Vector3(0, -0.05f, 0));

        var lightGo = new GameObject("DemoLight");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        lightGo.transform.rotation = Quaternion.Euler(38, -35, 0);
        RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.4f);
    }
}

} // namespace MeshSlicer.Samples
