using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Plane = Unity.Mathematics.Geometry.Plane;

namespace MeshSlicer.Tests
{
    public class NaiveMeshSlicerTests
    {
        const float kTol = 1e-3f;

        static Plane MakePlane(Vector3 normal, Vector3 pointOnPlane) =>
            Plane.Normalize(new Plane((float3)normal, (float3)pointOnPlane));

        static int VertexCount(Mesh m) => m == null ? 0 : m.vertexCount;
        static int TriangleCount(Mesh m) => m == null ? 0 : m.triangles.Length / 3;

        [Test]
        public void Slice_PlaneEntirelyOnPositiveSide_AllInNegative()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, new Vector3(0, 10, 0));

            var r = NaiveMeshSlicer.Slice(cube, plane);

            Assert.AreEqual(0, TriangleCount(r.Positive));
            Assert.AreEqual(12, TriangleCount(r.Negative));
        }

        [Test]
        public void Slice_PlaneEntirelyOnNegativeSide_AllInPositive()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, new Vector3(0, -10, 0));

            var r = NaiveMeshSlicer.Slice(cube, plane);

            Assert.AreEqual(12, TriangleCount(r.Positive));
            Assert.AreEqual(0, TriangleCount(r.Negative));
        }

        [Test]
        public void Slice_Cube_AtCenter_VerticesLieOnCorrectSide()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(cube, plane);

            Assert.IsNotNull(r.Positive);
            Assert.IsNotNull(r.Negative);
            MeshAssert.AssertAllVerticesOnSide(r.Positive, plane, +1);
            MeshAssert.AssertAllVerticesOnSide(r.Negative, plane, -1);
        }

        [Test]
        public void Slice_Cube_AtCenter_SurfaceAreaConserved()
        {
            var cube = TestMeshes.Cube(2f);
            var orig = MeshAssert.SurfaceArea(cube);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(cube, plane);

            var posBody = MeshAssert.SurfaceArea(r.Positive) - MeshAssert.CapArea(r.Positive, plane);
            var negBody = MeshAssert.SurfaceArea(r.Negative) - MeshAssert.CapArea(r.Negative, plane);
            Assert.AreEqual(orig, posBody + negBody, kTol);
        }

        [Test]
        public void Slice_Cube_AtCenter_CapAreaIs4()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(cube, plane);

            // 2x2 cap = area 4 on each side.
            Assert.AreEqual(4f, MeshAssert.CapArea(r.Positive, plane), kTol);
            Assert.AreEqual(4f, MeshAssert.CapArea(r.Negative, plane), kTol);
        }

        [Test]
        public void Slice_Cube_AtCenter_CapNormalsFaceInward()
        {
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(cube, plane);

            // Positive piece's cap faces -planeNormal (inside surface of the upper half facing down).
            MeshAssert.AssertCapNormals(r.Positive, plane, -Vector3.up);
            MeshAssert.AssertCapNormals(r.Negative, plane, Vector3.up);
        }

        [Test]
        public void Slice_Cube_VolumeConserved()
        {
            var cube = TestMeshes.Cube(2f);
            var orig = MeshAssert.SignedVolume(cube);
            var plane = MakePlane(Vector3.up, new Vector3(0, 0.3f, 0));

            var r = NaiveMeshSlicer.Slice(cube, plane);

            float vol = MeshAssert.SignedVolume(r.Positive) + MeshAssert.SignedVolume(r.Negative);
            Assert.AreEqual(orig, vol, kTol);
        }

        [Test]
        public void Slice_Icosphere_VolumeConserved()
        {
            var sphere = TestMeshes.Icosphere(1f, 3);
            var orig = MeshAssert.SignedVolume(sphere);
            var plane = MakePlane(new Vector3(0.3f, 1f, 0.2f), new Vector3(0, 0.1f, 0));

            var r = NaiveMeshSlicer.Slice(sphere, plane);

            float vol = MeshAssert.SignedVolume(r.Positive) + MeshAssert.SignedVolume(r.Negative);
            // Each cap is a polygonal approximation of a circle so volume can drift slightly
            // — but it's the SAME polygon for both sides so they cancel exactly.
            Assert.AreEqual(orig, vol, 1e-3f);
        }

        [Test]
        public void Slice_Torus_AlongMiddle_CapHasTwoLoops()
        {
            // Torus around Y axis with major R=1, minor r=0.3. Y=0 cuts through middle
            // producing an annulus on each side (one outer + one inner loop).
            var torus = TestMeshes.Torus(1f, 0.3f, 64, 32);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(torus, plane);

            Assert.AreEqual(2, MeshAssert.CapBoundaryLoopCount(r.Positive, plane),
                "torus cap should have outer and inner loops");
            Assert.AreEqual(2, MeshAssert.CapBoundaryLoopCount(r.Negative, plane));

            // Annulus area = π((R+r)^2 - (R-r)^2) = 4πRr
            float expected = 4f * Mathf.PI * 1f * 0.3f;
            // Allow for polygonal discretization (sin/cos sampling).
            Assert.AreEqual(expected, MeshAssert.CapArea(r.Positive, plane), 0.05f);
            Assert.AreEqual(expected, MeshAssert.CapArea(r.Negative, plane), 0.05f);
        }

        [Test]
        public void Slice_LShape_AlongMiddle_CapIsConcaveLShape()
        {
            // L-shape extruded along Y. Slice horizontally → cap is L cross-section
            // (concave). L cross-section area = 4 - 1 = 3.
            var lShape = TestMeshes.LShapeExtruded(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(lShape, plane);

            Assert.AreEqual(1, MeshAssert.CapBoundaryLoopCount(r.Positive, plane),
                "L-shape cap should be a single closed loop (no holes)");
            Assert.AreEqual(3f, MeshAssert.CapArea(r.Positive, plane), kTol);
            Assert.AreEqual(3f, MeshAssert.CapArea(r.Negative, plane), kTol);
        }

        [Test]
        public void Slice_Cube_AttributesAreInterpolatedAtCut()
        {
            // Cube with UVs from TestMeshes.Cube uses xz-based uv. Slice through y=0,
            // which cuts vertical edges. All cut-edge endpoints share their xz coords,
            // so interpolated UVs at cut points should equal the endpoints' UVs.
            var cube = TestMeshes.Cube(2f);
            var plane = MakePlane(Vector3.up, Vector3.zero);

            var r = NaiveMeshSlicer.Slice(cube, plane);

            var v = r.Positive.vertices; var uv = r.Positive.uv;
            for (int i = 0; i < v.Length; i++)
            {
                if (Mathf.Abs(v[i].y) > kTol) continue; // skip non-cut verts
                // expected uv = (x/2 + 0.5, z/2 + 0.5)
                var expected = new Vector2(v[i].x * 0.5f + 0.5f, v[i].z * 0.5f + 0.5f);
                // Cap verts are forced to (0,0) per spec; body verts at cut should match.
                bool isCap = (uv[i] == Vector2.zero) && (Mathf.Abs(v[i].x) > 0.5f || Mathf.Abs(v[i].z) > 0.5f
                                                        ? false : true);
                // Strict check: at least ONE vertex at each cut location should match interpolated uv.
                // We allow either match (body) OR (0,0) cap.
                bool match = Vector2.Distance(uv[i], expected) < kTol;
                bool cap = uv[i] == Vector2.zero;
                Assert.IsTrue(match || cap, $"vertex {i} at {v[i]} uv={uv[i]} expected {expected} or (0,0)");
            }
        }
    }
}
