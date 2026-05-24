using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests
{
    // Mirrors NaiveMeshSlicerTests against BurstMeshSlicer to make sure the optimized
    // path remains a drop-in replacement.
    public class BurstMeshSlicerTests
    {
        const float kTol = 1e-3f;

        static Plane MakePlane(Vector3 normal, Vector3 pointOnPlane) =>
            Plane.Normalize(new Plane((float3)normal, (float3)pointOnPlane));

        static int TriangleCount(Mesh m) => m == null ? 0 : m.triangles.Length / 3;

        [Test]
        public void Burst_PlaneEntirelyOnPositive_AllInNegative()
        {
            var cube = TestMeshes.Cube(2f);
            var r = BurstMeshSlicer.Slice(cube, MakePlane(Vector3.up, new Vector3(0, 10, 0)));
            Assert.AreEqual(0, TriangleCount(r.Positive));
            Assert.AreEqual(12, TriangleCount(r.Negative));
        }

        [Test]
        public void Burst_Cube_VerticesOnCorrectSide()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);
            var r = BurstMeshSlicer.Slice(cube, plane);
            Assert.IsNotNull(r.Positive); Assert.IsNotNull(r.Negative);
            MeshAssert.AssertAllVerticesOnSide(r.Positive, plane, +1);
            MeshAssert.AssertAllVerticesOnSide(r.Negative, plane, -1);
        }

        [Test]
        public void Burst_Cube_CapAreaIs4()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);
            var r = BurstMeshSlicer.Slice(cube, plane);
            Assert.AreEqual(4f, MeshAssert.CapArea(r.Positive, plane), kTol);
            Assert.AreEqual(4f, MeshAssert.CapArea(r.Negative, plane), kTol);
        }

        [Test]
        public void Burst_Cube_CapNormalsFaceInward()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);
            var r = BurstMeshSlicer.Slice(cube, plane);
            MeshAssert.AssertCapNormals(r.Positive, plane, -Vector3.up);
            MeshAssert.AssertCapNormals(r.Negative, plane,  Vector3.up);
        }

        [Test]
        public void Burst_Cube_VolumeConserved()
        {
            var cube = TestMeshes.Cube(2f);
            var orig = MeshAssert.SignedVolume(cube);
            var r = BurstMeshSlicer.Slice(cube, MakePlane(Vector3.up, new Vector3(0, 0.3f, 0)));
            Assert.AreEqual(orig, MeshAssert.SignedVolume(r.Positive) + MeshAssert.SignedVolume(r.Negative), kTol);
        }

        [Test]
        public void Burst_Icosphere_VolumeConserved()
        {
            var sphere = TestMeshes.Icosphere(1f, 3);
            var orig = MeshAssert.SignedVolume(sphere);
            var r = BurstMeshSlicer.Slice(sphere, MakePlane(new Vector3(0.3f, 1f, 0.2f), new Vector3(0, 0.1f, 0)));
            Assert.AreEqual(orig, MeshAssert.SignedVolume(r.Positive) + MeshAssert.SignedVolume(r.Negative), 1e-3f);
        }

        [Test]
        public void Burst_Torus_AlongMiddle_CapHasTwoLoops()
        {
            var torus = TestMeshes.Torus(1f, 0.3f, 64, 32);
            var plane = MakePlane(Vector3.up, Vector3.zero);
            var r = BurstMeshSlicer.Slice(torus, plane);
            Assert.AreEqual(2, MeshAssert.CapBoundaryLoopCount(r.Positive, plane));
            Assert.AreEqual(2, MeshAssert.CapBoundaryLoopCount(r.Negative, plane));
            float expected = 4f * Mathf.PI * 1f * 0.3f;
            Assert.AreEqual(expected, MeshAssert.CapArea(r.Positive, plane), 0.05f);
        }

        [Test]
        public void Burst_LShape_AlongMiddle_CapIsConcaveLShape()
        {
            var lShape = TestMeshes.LShapeExtruded(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);
            var r = BurstMeshSlicer.Slice(lShape, plane);
            Assert.AreEqual(1, MeshAssert.CapBoundaryLoopCount(r.Positive, plane));
            Assert.AreEqual(3f, MeshAssert.CapArea(r.Positive, plane), kTol);
        }
    }
}
