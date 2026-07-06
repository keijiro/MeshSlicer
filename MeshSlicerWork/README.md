# Mesh Slicer

Cuts a mesh with an arbitrary plane in Unity, producing the two resulting meshes.
Each cut cross section is closed with a cap. Built test-first (TDD) and optimized
with the Burst compiler and the C# Job System.

The source mesh is assumed to satisfy the 2-manifold / watertight condition.
Vertex data supported: position, normal, tangent, uv0. Cap uv0 is fixed to (0, 0).

## API

```csharp
using MeshSlicer;
using Plane = Unity.Mathematics.Geometry.Plane;

// Naive, correctness-first implementation.
SliceResult r = Slicer.Slice(mesh, plane);

// Optimized implementation (Burst + Job System).
SliceResult r = BurstSlicer.Slice(mesh, plane);

// Non-allocating overload for per-frame reuse.
BurstSlicer.Slice(mesh, plane, positiveMesh, negativeMesh);
```

`Slice` returns `SliceResult { Mesh Positive; Mesh Negative; }`, where `Positive`
is the piece on the side the plane normal points to (signed distance >= 0). Either
side is `null` when the mesh lies entirely on one side of the plane. Both pieces
share identical cap geometry, so the result stays watertight. Attributes carried:
position / normal / tangent / uv0.

## Project layout (`Assets/MeshSlicer/`)

- **Runtime/**
  - `Slicer` — naive, correctness-first implementation.
  - `BurstSlicer` — optimized implementation.
  - `CapBuilder` / `PolygonTriangulator` — cross-section triangulation with holes.
  - `ProceduralMeshes` — watertight test-shape generators.
  - `Vertex` / `MeshBuilder` / `SliceResult` — support types.
- **Tests/EditMode/** — 18 EditMode tests (all passing).
- **Samples/**
  - `SliceDemo` — visual verification scene.
  - `SliceBenchmark` — performance measurement scene.

## Tests (Unity Test Runner, EditMode — 18 passing)

- Watertightness (every edge shared by exactly two triangles after welding by
  position), volume conservation, side classification, cap normal orientation,
  cap uv0 = (0, 0).
- Convex shapes (cube / tetrahedron / icosphere), plus **multi-loop / nested
  (holed) cross sections**: torus (annulus), hollow pipe (annulus), open box /
  tray (rectangular frame), bowl (annulus). The cap area is asserted against the
  expected annulus area, so a wrongly filled hole fails the test.
- The bundled `Crate.fbx`, a one-sided plane, and naive vs. Burst parity.

## Visual verification

Seven shapes are sliced and their two halves rendered pulled apart along the cut
normal (Built-in Render Pipeline, Standard shader), then captured from the Game
View. The annular caps close correctly: the pipe's round hole, the tray's
rectangular frame, the torus donut, and the bowl shell are all preserved.

## Performance (20,480-triangle icosphere, Editor)

| Metric | Naive | Burst | Speedup |
|---|---|---|---|
| Method-level slice | 9.16 ms | **1.04 ms** | 8.8x |
| System-level (slice + render every frame) | 9.49 ms/frame | **1.54 ms/frame (~650 fps)** | 6.2x |

### Optimization iterations

1. Burst `IJob` + `NativeArray` + Advanced Mesh API — 2.82 ms.
2. O(n²) linked-list ear clipping (replacing the O(n³) naive rescan) — 1.21 ms.
   This was the largest single win.
3. Parallel two-pass triangle classification (`IJobParallelFor` + prefix sum) —
   1.04 ms.

Array copies are minimized via the Advanced Mesh API (`SetVertexBufferData` /
`SetIndexBufferData`) for output and `AcquireReadOnlyMeshData` for input. The
Burst pipeline is: read → parallel count → prefix-sum → parallel write → cap
triangulation (managed) → upload. The remaining largest phase is cap generation
(managed); porting the cap chaining and triangulation to Burst would be the next
optimization lever.

## Development time

The full implementation — writing the tests first, the naive slicer, the visual
verification scene, the performance baseline, and the three Burst optimization
iterations — took **49 minutes 41 seconds** of active work in a single agent
session. This was an autonomous run driven by an AI coding agent (Claude Code),
including the edit → recompile → run-tests → measure loop against a live Unity
Editor.

