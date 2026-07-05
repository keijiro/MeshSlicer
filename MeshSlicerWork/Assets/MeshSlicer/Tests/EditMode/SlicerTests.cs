using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests {

public sealed class SlicerTests
{
    const float Eps = 1e-3f;

    static Plane PlaneThrough(float3 normal, float3 point) =>
        new Plane(math.normalize(normal), point);

    static void DestroyMesh(Mesh m)
    {
        if (m != null) UnityEngine.Object.DestroyImmediate(m);
    }

    // ---- Argument handling ----------------------------------------------

    [Test]
    public void Slice_NullSource_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => Slicer.Slice(null, new Plane(new float3(0, 1, 0), 0f)));
    }

    // ---- No intersection cases ------------------------------------------

    [Test]
    public void Slice_PlaneAbove_ReturnsWholeOnNegativeSide()
    {
        var cube = TestMeshFactory.CreateCube();
        // Plane well above the cube: entire cube is on the negative side.
        var plane = PlaneThrough(new float3(0, 1, 0), new float3(0, 5, 0));
        var r = Slicer.Slice(cube, plane);

        Assert.IsNull(r.Positive, "nothing should be on the positive side");
        Assert.IsNotNull(r.Negative);
        Assert.AreEqual(cube.triangles.Length, r.Negative.triangles.Length,
            "whole mesh should pass through unchanged");

        DestroyMesh(cube);
        DestroyMesh(r.Negative);
    }

    [Test]
    public void Slice_PlaneBelow_ReturnsWholeOnPositiveSide()
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(0, 1, 0), new float3(0, -5, 0));
        var r = Slicer.Slice(cube, plane);

        Assert.IsNotNull(r.Positive);
        Assert.IsNull(r.Negative);
        Assert.AreEqual(cube.triangles.Length, r.Positive.triangles.Length);

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
    }

    // ---- Basic slicing ---------------------------------------------------

    [Test]
    public void Slice_CubeThroughCenter_ProducesTwoHalves()
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(0, 1, 0), float3.zero);
        var r = Slicer.Slice(cube, plane);

        Assert.IsNotNull(r.Positive);
        Assert.IsNotNull(r.Negative);
        Assert.Greater(r.Positive.triangles.Length, 0);
        Assert.Greater(r.Negative.triangles.Length, 0);

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    // ---- Watertightness of results --------------------------------------

    [TestCase(0f, 1f, 0f)]
    [TestCase(1f, 0f, 0f)]
    [TestCase(1f, 1f, 1f)]
    [TestCase(0.3f, 1f, -0.6f)]
    public void Slice_Cube_HalvesAreClosedManifolds(float nx, float ny, float nz)
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(nx, ny, nz), float3.zero);
        var r = Slicer.Slice(cube, plane);

        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Positive, out var rp),
            "positive half not watertight: " + rp);
        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Negative, out var rn),
            "negative half not watertight: " + rn);

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    [Test]
    public void Slice_Sphere_HalvesAreClosedManifolds()
    {
        var sphere = TestMeshFactory.CreateSphere();
        var plane = PlaneThrough(new float3(0.2f, 1f, 0.1f), new float3(0, 0.15f, 0));
        var r = Slicer.Slice(sphere, plane);

        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Positive, out var rp),
            "positive half not watertight: " + rp);
        Assert.IsTrue(MeshAnalysis.IsClosedManifold(r.Negative, out var rn),
            "negative half not watertight: " + rn);

        DestroyMesh(sphere);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    // ---- Volume conservation (caps + walls correctness) -----------------

    [Test]
    public void Slice_Cube_VolumeIsConserved()
    {
        var cube = TestMeshFactory.CreateCube(2f);
        var original = MeshAnalysis.SignedVolume(cube);
        var plane = PlaneThrough(new float3(0.4f, 1f, 0.2f), new float3(0.1f, 0.2f, 0));
        var r = Slicer.Slice(cube, plane);

        var sum = MeshAnalysis.SignedVolume(r.Positive) + MeshAnalysis.SignedVolume(r.Negative);
        Assert.AreEqual(original, sum, 1e-3, "sum of half volumes must equal original");
        Assert.Greater(MeshAnalysis.SignedVolume(r.Positive), 0, "positive half must have outward winding");
        Assert.Greater(MeshAnalysis.SignedVolume(r.Negative), 0, "negative half must have outward winding");

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    [Test]
    public void Slice_Sphere_VolumeIsConserved()
    {
        var sphere = TestMeshFactory.CreateSphere(1.5f);
        var original = MeshAnalysis.SignedVolume(sphere);
        var plane = PlaneThrough(new float3(0.3f, 1f, -0.2f), new float3(0, 0.2f, 0.1f));
        var r = Slicer.Slice(sphere, plane);

        var sum = MeshAnalysis.SignedVolume(r.Positive) + MeshAnalysis.SignedVolume(r.Negative);
        Assert.AreEqual(original, sum, 1e-2, "sum of half volumes must equal original");

        DestroyMesh(sphere);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    // ---- Side classification --------------------------------------------

    [Test]
    public void Slice_AllVerticesOnCorrectSide()
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(0.5f, 1f, 0.3f), new float3(0.05f, 0.1f, 0));
        var r = Slicer.Slice(cube, plane);

        foreach (var v in r.Positive.vertices)
            Assert.GreaterOrEqual(plane.SignedDistanceToPoint(v), -Eps,
                "positive vertex on wrong side");
        foreach (var v in r.Negative.vertices)
            Assert.LessOrEqual(plane.SignedDistanceToPoint(v), Eps,
                "negative vertex on wrong side");

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    // ---- Cap attributes --------------------------------------------------

    [Test]
    public void Slice_CapNormalsFacePlane_AndUVsAreZero()
    {
        var cube = TestMeshFactory.CreateCube();
        var normal = math.normalize(new float3(0.2f, 1f, 0.1f));
        var plane = new Plane(normal, 0f);
        var r = Slicer.Slice(cube, plane);

        AssertCap(r.Positive, plane, -normal);
        AssertCap(r.Negative, plane, normal);

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    // Verifies cap vertices (those lying on the plane whose normal points along
    // the expected cap direction) have uv0 == (0,0), and that at least one such
    // vertex exists.
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
                Assert.AreEqual(0f, uvs[i].x, 1e-4f, "cap uv.x must be 0");
                Assert.AreEqual(0f, uvs[i].y, 1e-4f, "cap uv.y must be 0");
            }
        }
        Assert.Greater(found, 0, "no cap vertices found");
    }

    // ---- Attribute integrity --------------------------------------------

    [Test]
    public void Slice_NormalsAndTangentsAreUnitLength()
    {
        var sphere = TestMeshFactory.CreateSphere();
        var plane = PlaneThrough(new float3(0.1f, 1f, 0.2f), float3.zero);
        var r = Slicer.Slice(sphere, plane);

        foreach (var mesh in new[] { r.Positive, r.Negative })
        {
            foreach (var n in mesh.normals)
                Assert.AreEqual(1f, math.length((float3)n), 1e-2f,
                    "normal must be unit length");
            foreach (var t in mesh.tangents)
                Assert.AreEqual(1f, math.length(((float4)(Vector4)t).xyz), 1e-2f,
                    "tangent xyz must be unit length");
        }

        DestroyMesh(sphere);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    [Test]
    public void Slice_PreservesAttributeChannels()
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(0, 1, 0), float3.zero);
        var r = Slicer.Slice(cube, plane);

        foreach (var mesh in new[] { r.Positive, r.Negative })
        {
            Assert.AreEqual(mesh.vertexCount, mesh.normals.Length, "normals present");
            Assert.AreEqual(mesh.vertexCount, mesh.tangents.Length, "tangents present");
            Assert.AreEqual(mesh.vertexCount, mesh.uv.Length, "uv0 present");
        }

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }

    // ---- Interpolation correctness on a straddling edge ------------------

    [Test]
    public void Slice_IntersectionVertices_LieOnPlane()
    {
        var cube = TestMeshFactory.CreateCube();
        var plane = PlaneThrough(new float3(0, 1, 0), float3.zero);
        var r = Slicer.Slice(cube, plane);

        // Every new intersection vertex introduced by the cut must sit on the
        // plane. Cap and wall boundary vertices both qualify.
        var onPlaneCount = 0;
        foreach (var v in r.Positive.vertices)
            if (math.abs(plane.SignedDistanceToPoint(v)) < 1e-3f) onPlaneCount++;
        Assert.Greater(onPlaneCount, 0, "expected vertices exactly on the cut plane");

        DestroyMesh(cube);
        DestroyMesh(r.Positive);
        DestroyMesh(r.Negative);
    }
}

} // namespace MeshSlicer.Tests
