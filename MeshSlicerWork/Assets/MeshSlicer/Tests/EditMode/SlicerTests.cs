using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests {

public sealed class SlicerTests
{
    // --- source generator sanity: every primitive must be watertight ---

    static Mesh[] AllPrimitives() => new[]
    {
        ProceduralMeshes.Cube(),
        ProceduralMeshes.Tetrahedron(),
        ProceduralMeshes.IcoSphere(2),
        ProceduralMeshes.Torus(),
        ProceduralMeshes.Pipe(),
        ProceduralMeshes.Tray(),
        ProceduralMeshes.Bowl()
    };

    [Test]
    public void SourcePrimitivesAreWatertight()
    {
        foreach (var m in AllPrimitives())
        {
            Assert.IsTrue(MeshInspect.IsWatertight(m, out var b, out var nm),
                $"{m.name} not watertight (boundary={b}, nonmanifold={nm})");
            Assert.Greater(MeshInspect.SignedVolume(m), 0, $"{m.name} has non-positive volume (bad winding)");
        }
    }

    // --- generic slice invariants applied to every convex + concave primitive ---

    static System.Func<Mesh, Plane, SliceResult> _slice = Slicer.Slice;

    [SetUp] public void ResetImpl() => _slice = Slicer.Slice;

    static void AssertSliceInvariants(Mesh src, Plane plane, bool expectBothSides = true)
    {
        var extent = math.cmax((float3)src.bounds.size);
        var tol = math.max(extent * 2e-3f, 1e-5f);

        var r = _slice(src, plane);

        if (expectBothSides)
        {
            Assert.IsNotNull(r.Positive, $"{src.name}: positive piece missing");
            Assert.IsNotNull(r.Negative, $"{src.name}: negative piece missing");
        }

        // Watertightness of each produced piece.
        if (r.Positive != null)
            Assert.IsTrue(MeshInspect.IsWatertight(r.Positive, out var pb, out var pn),
                $"{src.name} positive not watertight (boundary={pb}, nonmanifold={pn})");
        if (r.Negative != null)
            Assert.IsTrue(MeshInspect.IsWatertight(r.Negative, out var nb, out var nn),
                $"{src.name} negative not watertight (boundary={nb}, nonmanifold={nn})");

        // Volume conservation.
        var vSrc = MeshInspect.SignedVolume(src);
        var vPos = r.Positive != null ? MeshInspect.SignedVolume(r.Positive) : 0;
        var vNeg = r.Negative != null ? MeshInspect.SignedVolume(r.Negative) : 0;
        Assert.AreEqual(vSrc, vPos + vNeg, math.max(vSrc * 0.01, 1e-4),
            $"{src.name}: volume not conserved (src={vSrc:F5}, pos+neg={vPos + vNeg:F5})");

        // Side classification: no vertex should stray across the plane.
        if (r.Positive != null)
        {
            MeshInspect.SignedDistanceRange(r.Positive, plane, out var min, out _);
            Assert.GreaterOrEqual(min, -tol, $"{src.name}: positive piece has vertices on the negative side");
        }
        if (r.Negative != null)
        {
            MeshInspect.SignedDistanceRange(r.Negative, plane, out _, out var max);
            Assert.LessOrEqual(max, tol, $"{src.name}: negative piece has vertices on the positive side");
        }

        // Caps face outward (positive piece cap faces -n, negative faces +n) and use zero UVs.
        if (r.Positive != null)
        {
            var area = MeshInspect.CapArea(r.Positive, plane, out var nzUv, out var sdot);
            Assert.Greater(area, 0, $"{src.name}: positive piece has no cap");
            Assert.Less(sdot, 0, $"{src.name}: positive cap faces the wrong way");
            Assert.IsFalse(nzUv, $"{src.name}: positive cap UVs are not (0,0)");
        }
        if (r.Negative != null)
        {
            var area = MeshInspect.CapArea(r.Negative, plane, out var nzUv, out var sdot);
            Assert.Greater(area, 0, $"{src.name}: negative piece has no cap");
            Assert.Greater(sdot, 0, $"{src.name}: negative cap faces the wrong way");
            Assert.IsFalse(nzUv, $"{src.name}: negative cap UVs are not (0,0)");
        }
    }

    [Test]
    public void Cube_DiagonalPlane()
        => AssertSliceInvariants(ProceduralMeshes.Cube(), PlaneFrom(new float3(0.3f, 0, 0), math.normalize(new float3(1, 0.5f, 0.2f))));

    [Test]
    public void Cube_AxisAlignedPlane()
        => AssertSliceInvariants(ProceduralMeshes.Cube(), PlaneFrom(float3.zero, new float3(0, 1, 0)));

    [Test]
    public void Tetrahedron_Plane()
        => AssertSliceInvariants(ProceduralMeshes.Tetrahedron(), PlaneFrom(new float3(0, 0.05f, 0), math.normalize(new float3(0.2f, 1, 0.1f))));

    [Test]
    public void IcoSphere_Plane()
        => AssertSliceInvariants(ProceduralMeshes.IcoSphere(3), PlaneFrom(new float3(0.05f, 0, 0), math.normalize(new float3(0.3f, 1, 0.4f))));

    // --- nested / multi-loop cross sections ---

    [Test]
    public void Torus_EquatorialCut_ProducesAnnulusCap()
    {
        var src = ProceduralMeshes.Torus(0.6f, 0.22f);
        var plane = PlaneFrom(float3.zero, new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, math.PI * 4 * 0.6f * 0.22f);
    }

    [Test]
    public void Pipe_PerpendicularCut_ProducesAnnulusCap()
    {
        var src = ProceduralMeshes.Pipe(0.5f, 0.3f, 1f);
        var plane = PlaneFrom(float3.zero, new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, math.PI * (0.5f * 0.5f - 0.3f * 0.3f));
    }

    [Test]
    public void Tray_HorizontalCut_ProducesFrameCap()
    {
        var src = ProceduralMeshes.Tray(1f, 1f, 0.7f, 0.18f);
        var plane = PlaneFrom(float3.zero, new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        var frame = 1f * 1f - (1f - 0.36f) * (1f - 0.36f);
        AssertAnnulusCap(src, plane, frame);
    }

    [Test]
    public void Bowl_HorizontalCut_ProducesAnnulusCap()
    {
        var src = ProceduralMeshes.Bowl(0.5f, 0.08f);
        var plane = PlaneFrom(new float3(0, -0.2f, 0), new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, math.PI * (0.5f * 0.5f - 0.42f * 0.42f));
    }

    static void AssertAnnulusCap(Mesh src, Plane plane, float expectedArea)
    {
        var r = _slice(src, plane);
        var posArea = MeshInspect.CapArea(r.Positive, plane, out _, out _);
        var negArea = MeshInspect.CapArea(r.Negative, plane, out _, out _);
        Assert.AreEqual(expectedArea, negArea, expectedArea * 0.03,
            $"{src.name}: cap area {negArea:F4} != expected annulus {expectedArea:F4} (hole not handled?)");
        Assert.AreEqual(posArea, negArea, expectedArea * 0.01,
            $"{src.name}: positive and negative cap areas differ");
    }

    // --- one-sided plane leaves the mesh intact ---

    [Test]
    public void PlaneOutsideMesh_LeavesOnePiece()
    {
        var src = ProceduralMeshes.Cube(1f);
        var r = Slicer.Slice(src, PlaneFrom(new float3(0, 5, 0), new float3(0, 1, 0)));
        Assert.IsNull(r.Positive);
        Assert.IsNotNull(r.Negative);
        Assert.AreEqual(MeshInspect.SignedVolume(src), MeshInspect.SignedVolume(r.Negative), 1e-4);
    }

    // --- imported FBX asset ---

    [Test]
    public void CrateFbx_Slices()
    {
        var mesh = LoadFbxMesh("Assets/Models/Crate.fbx");
        Assert.IsNotNull(mesh, "Crate.fbx mesh not found");

        var plane = PlaneFrom((float3)mesh.bounds.center, math.normalize(new float3(0.4f, 1, 0.2f)));
        var r = Slicer.Slice(mesh, plane);

        Assert.IsNotNull(r.Positive);
        Assert.IsNotNull(r.Negative);
        Assert.Greater(r.Positive.triangles.Length, 0);
        Assert.Greater(r.Negative.triangles.Length, 0);

        var extent = math.cmax((float3)mesh.bounds.size);
        var tol = extent * 3e-3f;
        MeshInspect.SignedDistanceRange(r.Positive, plane, out var pmin, out _);
        MeshInspect.SignedDistanceRange(r.Negative, plane, out _, out var nmax);
        Assert.GreaterOrEqual(pmin, -tol, "Crate positive piece strays to negative side");
        Assert.LessOrEqual(nmax, tol, "Crate negative piece strays to positive side");

        // Only assert conservation when the imported asset is actually watertight.
        if (MeshInspect.IsWatertight(mesh, out _, out _))
        {
            var v = MeshInspect.SignedVolume(mesh);
            Assert.AreEqual(v, MeshInspect.SignedVolume(r.Positive) + MeshInspect.SignedVolume(r.Negative),
                math.max(math.abs(v) * 0.02, 1e-4), "Crate volume not conserved");
        }
    }

    // --- Burst implementation: same invariants + parity with the naive slicer ---

    [Test] public void Burst_Cube()      { _slice = BurstSlicer.Slice; AssertSliceInvariants(ProceduralMeshes.Cube(), PlaneFrom(new float3(0.3f, 0, 0), math.normalize(new float3(1, 0.5f, 0.2f)))); }
    [Test] public void Burst_IcoSphere() { _slice = BurstSlicer.Slice; AssertSliceInvariants(ProceduralMeshes.IcoSphere(3), PlaneFrom(new float3(0.05f, 0, 0), math.normalize(new float3(0.3f, 1, 0.4f)))); }

    [Test] public void Burst_Torus_Annulus()
    {
        _slice = BurstSlicer.Slice;
        var src = ProceduralMeshes.Torus(0.6f, 0.22f);
        var plane = PlaneFrom(float3.zero, new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, math.PI * 4 * 0.6f * 0.22f);
    }

    [Test] public void Burst_Pipe_Annulus()
    {
        _slice = BurstSlicer.Slice;
        var src = ProceduralMeshes.Pipe(0.5f, 0.3f, 1f);
        var plane = PlaneFrom(float3.zero, new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, math.PI * (0.5f * 0.5f - 0.3f * 0.3f));
    }

    [Test] public void Burst_Tray_Frame()
    {
        _slice = BurstSlicer.Slice;
        var src = ProceduralMeshes.Tray(1f, 1f, 0.7f, 0.18f);
        var plane = PlaneFrom(float3.zero, new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, 1f * 1f - (1f - 0.36f) * (1f - 0.36f));
    }

    [Test] public void Burst_Bowl_Annulus()
    {
        _slice = BurstSlicer.Slice;
        var src = ProceduralMeshes.Bowl(0.5f, 0.08f);
        var plane = PlaneFrom(new float3(0, -0.2f, 0), new float3(0, 1, 0));
        AssertSliceInvariants(src, plane);
        AssertAnnulusCap(src, plane, math.PI * (0.5f * 0.5f - 0.42f * 0.42f));
    }

    [Test]
    public void Burst_MatchesNaive_Volumes()
    {
        var shapes = new[]
        {
            ProceduralMeshes.Cube(), ProceduralMeshes.IcoSphere(3),
            ProceduralMeshes.Torus(), ProceduralMeshes.Pipe(),
            ProceduralMeshes.Tray(), ProceduralMeshes.Bowl()
        };
        var plane = PlaneFrom(new float3(0, -0.05f, 0), math.normalize(new float3(0.2f, 1, 0.15f)));
        foreach (var s in shapes)
        {
            var a = Slicer.Slice(s, plane);
            var b = BurstSlicer.Slice(s, plane);
            var va = a.Positive != null ? MeshInspect.SignedVolume(a.Positive) : 0;
            var vb = b.Positive != null ? MeshInspect.SignedVolume(b.Positive) : 0;
            Assert.AreEqual(va, vb, math.max(math.abs(va) * 0.01, 1e-4), $"{s.name}: Burst/naive positive volume mismatch");
        }
    }

    // Regression: an imported (tiny, detailed) asset must produce watertight pieces
    // across many plane positions/orientations — including cuts that graze vertices
    // (degeneracy -> nudge) and the tiny-scale triangulation case that once left a
    // small hole. Runs for both the naive and Burst implementations.
    [Test] public void CrateFbx_ManyPlanes_Watertight_Naive() => AssertCrateAlwaysWatertight(Slicer.Slice);
    [Test] public void CrateFbx_ManyPlanes_Watertight_Burst() => AssertCrateAlwaysWatertight(BurstSlicer.Slice);

    static void AssertCrateAlwaysWatertight(System.Func<Mesh, Plane, SliceResult> slice)
    {
        var mesh = LoadFbxMesh("Assets/Models/Crate.fbx");
        Assert.IsNotNull(mesh, "Crate.fbx mesh not found");
        if (!MeshInspect.IsWatertight(mesh, out _, out _)) Assert.Ignore("source crate is not watertight");

        var ext = math.cmax((float3)mesh.bounds.size);
        var center = (float3)mesh.bounds.center;

        var planes = new System.Collections.Generic.List<Plane>();
        var rng = new System.Random(12345);
        for (var i = 0; i < 40; i++)
        {
            var dir = new float3((float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1), (float)(rng.NextDouble() * 2 - 1));
            if (math.length(dir) < 1e-3f) continue;
            var n = math.normalize(dir);
            var off = (float)(rng.NextDouble() * 0.6 - 0.3);
            planes.Add(new Plane(n, center + n * (off * ext)));
        }
        // The exact orientation/offset that used to leave a hole.
        var nBad = math.normalize(new float3(-0.259f, 0.341f, 0.904f));
        planes.Add(new Plane(nBad, center + nBad * (0.07f * ext)));

        foreach (var plane in planes)
        {
            var r = slice(mesh, plane);
            if (r.Positive != null)
                Assert.IsTrue(MeshInspect.IsWatertight(r.Positive, out var pb, out var pn),
                    $"positive not watertight (boundary={pb}, nonmanifold={pn}) normal={plane.Normal}");
            if (r.Negative != null)
                Assert.IsTrue(MeshInspect.IsWatertight(r.Negative, out var nb, out var nn),
                    $"negative not watertight (boundary={nb}, nonmanifold={nn}) normal={plane.Normal}");
        }
    }

    static Mesh LoadFbxMesh(string path)
    {
        foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
            if (obj is Mesh m) return m;
        return null;
    }

    static Plane PlaneFrom(float3 point, float3 normal) => new Plane(math.normalize(normal), point);
}

} // namespace MeshSlicer.Tests
