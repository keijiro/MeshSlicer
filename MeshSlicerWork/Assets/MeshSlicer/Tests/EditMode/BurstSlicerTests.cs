using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests {

// The Burst slicer must satisfy the same correctness guarantees as the naive
// reference implementation.
public sealed class BurstSlicerTests
{
    const float Eps = 1e-3f;

    static Plane PlaneThrough(float3 normal, float3 point) =>
        new Plane(math.normalize(normal), point);

    static void Destroy(Mesh m) { if (m != null) UnityEngine.Object.DestroyImmediate(m); }

    [TestCase(0f, 1f, 0f)]
    [TestCase(1f, 0f, 0f)]
    [TestCase(1f, 1f, 1f)]
    [TestCase(0.3f, 1f, -0.6f)]
    public void Burst_Cube_HalvesAreClosedManifolds(float nx, float ny, float nz)
    {
        var cube = TestMeshFactory.CreateCube();
        var r = BurstSlicer.Slice(cube, PlaneThrough(new float3(nx, ny, nz), float3.zero));

        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Positive, out var rp), "positive: " + rp);
        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Negative, out var rn), "negative: " + rn);

        Destroy(cube); Destroy(r.Positive); Destroy(r.Negative);
    }

    [Test]
    public void Burst_Sphere_HalvesAreClosedManifolds()
    {
        var sphere = TestMeshFactory.CreateSphere();
        var r = BurstSlicer.Slice(sphere, PlaneThrough(new float3(0.2f, 1f, 0.1f), new float3(0, 0.15f, 0)));

        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Positive, out var rp), "positive: " + rp);
        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Negative, out var rn), "negative: " + rn);

        Destroy(sphere); Destroy(r.Positive); Destroy(r.Negative);
    }

    [Test]
    public void Burst_Cube_VolumeIsConserved()
    {
        var cube = TestMeshFactory.CreateCube(2f);
        var original = MeshAnalysis.SignedVolume(cube);
        var r = BurstSlicer.Slice(cube, PlaneThrough(new float3(0.4f, 1f, 0.2f), new float3(0.1f, 0.2f, 0)));

        var sum = MeshAnalysis.SignedVolume(r.Positive) + MeshAnalysis.SignedVolume(r.Negative);
        Assert.AreEqual(original, sum, 1e-3);
        Assert.Greater(MeshAnalysis.SignedVolume(r.Positive), 0);
        Assert.Greater(MeshAnalysis.SignedVolume(r.Negative), 0);

        Destroy(cube); Destroy(r.Positive); Destroy(r.Negative);
    }

    [Test]
    public void Burst_Sphere_VolumeIsConserved()
    {
        var sphere = TestMeshFactory.CreateSphere(1.5f);
        var original = MeshAnalysis.SignedVolume(sphere);
        var r = BurstSlicer.Slice(sphere, PlaneThrough(new float3(0.3f, 1f, -0.2f), new float3(0, 0.2f, 0.1f)));

        var sum = MeshAnalysis.SignedVolume(r.Positive) + MeshAnalysis.SignedVolume(r.Negative);
        Assert.AreEqual(original, sum, 1e-2);

        Destroy(sphere); Destroy(r.Positive); Destroy(r.Negative);
    }

    [Test]
    public void Burst_AllVerticesOnCorrectSide()
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(0.5f, 1f, 0.3f), new float3(0.05f, 0.1f, 0));
        var r = BurstSlicer.Slice(cube, plane);

        foreach (var v in r.Positive.vertices)
            Assert.GreaterOrEqual(plane.SignedDistanceToPoint(v), -Eps);
        foreach (var v in r.Negative.vertices)
            Assert.LessOrEqual(plane.SignedDistanceToPoint(v), Eps);

        Destroy(cube); Destroy(r.Positive); Destroy(r.Negative);
    }

    [Test]
    public void Burst_CapNormalsFacePlane_AndUVsAreZero()
    {
        var cube = TestMeshFactory.CreateCube();
        var normal = math.normalize(new float3(0.2f, 1f, 0.1f));
        var r = BurstSlicer.Slice(cube, new Plane(normal, 0f));

        AssertCap(r.Positive, new Plane(normal, 0f), -normal);
        AssertCap(r.Negative, new Plane(normal, 0f), normal);

        Destroy(cube); Destroy(r.Positive); Destroy(r.Negative);
    }

    static void AssertCap(Mesh mesh, Plane plane, float3 expectedNormal)
    {
        var verts = mesh.vertices;
        var norms = mesh.normals;
        var uvs = mesh.uv;
        var found = 0;
        for (var i = 0; i < verts.Length; i++)
        {
            var onPlane = math.abs(plane.SignedDistanceToPoint(verts[i])) < 1e-3f;
            var alongCap = math.dot((float3)norms[i], expectedNormal) > 0.99f;
            if (onPlane && alongCap)
            {
                found++;
                Assert.AreEqual(0f, uvs[i].x, 1e-4f);
                Assert.AreEqual(0f, uvs[i].y, 1e-4f);
            }
        }
        Assert.Greater(found, 0, "no cap vertices found");
    }

    [Test]
    public void Burst_MatchesNaive_TriangleCounts()
    {
        var sphere = TestMeshFactory.CreateSphere();
        var plane = PlaneThrough(new float3(0.2f, 1f, 0.35f), new float3(0, 0.1f, 0));
        var a = Slicer.Slice(sphere, plane);
        var b = BurstSlicer.Slice(sphere, plane);

        Assert.AreEqual(a.Positive.triangles.Length, b.Positive.triangles.Length, "positive tri count");
        Assert.AreEqual(a.Negative.triangles.Length, b.Negative.triangles.Length, "negative tri count");

        Destroy(sphere);
        Destroy(a.Positive); Destroy(a.Negative);
        Destroy(b.Positive); Destroy(b.Negative);
    }
}

} // namespace MeshSlicer.Tests
