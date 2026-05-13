# MeshSlicer Project Technical Overview

## 1. Project Description
**MeshSlicer** is a high-performance mesh manipulation utility for Unity designed to perform real-time planar slicing of 3D geometry. The project demonstrates advanced usage of the **Unity Burst Compiler**, **Job System**, and the **Advanced Mesh API** (`AcquireReadOnlyMeshData` / `AllocateWritableMeshData`) to achieve zero-managed-allocation slicing. It is intended for developers needing efficient, per-frame mesh splitting for destructible environments, gameplay mechanics, or procedural generation.

**Core Pillars:**
*   **Performance First:** Leverages Burst-compiled C# jobs to process vertex and index data at native speeds.
*   **Zero-Copy Workflow:** Uses direct GPU buffer access to minimize memory overhead and garbage collection pressure.
*   **Dual Implementation:** Provides both a "Naive" reference implementation for clarity and a "Burst" implementation for production performance.
*   **Procedural Capping:** Automatically generates "cap" geometry (fill faces) at the intersection plane to maintain the appearance of solid objects.

## 2. Gameplay Flow / User Loop
The project is primarily a technical demonstration and utility library. The typical user flow within the demo scenes is as follows:
1.  **Scene Initialization:** The `SliceDemo` or `StressController` initializes by loading or procedurally generating a source mesh (e.g., a UV sphere or primitive).
2.  **Plane Definition:** A slicing plane is defined by a normal vector and a point in space.
3.  **Slicing Execution:**
    *   The system passes the source mesh and plane to the `BurstMeshSlicer`.
    *   Vertices are categorized into "Positive" (above plane) and "Negative" (below plane) groups.
    *   Triangles intersecting the plane are split into new sub-triangles.
    *   Intersection points are tracked to build a closed "cap" for both resulting halves.
4.  **Result Application:** The `SliceResult` (containing two new `Mesh` objects) is returned and assigned to `MeshFilter` components on sibling GameObjects.
5.  **Per-Frame Update (Stress Test):** In the `StressDemo`, this process repeats every frame with rotating planes to demonstrate the capability of the Burst implementation.

## 3. Architecture
The project follows a functional utility pattern where the slicing logic is decoupled from the Unity Scene Graph.

*   **Static Utility Pattern:** The core logic resides in static classes (`BurstMeshSlicer`, `NaiveMeshSlicer`), making them easily accessible from any controller without singleton overhead.
*   **Job-Based Data Flow:** The Burst implementation uses a two-stage job pipeline:
    1.  `SliceJob`: Iterates through the source triangles, categorizing vertices and identifying intersection edges.
    2.  `CapJob`: Performs fan-triangulation on the collected intersection points to seal the mesh.
*   **Advanced Mesh API:** Uses `Mesh.MeshData` to read and write directly to the underlying mesh buffers, bypassing the slower `mesh.vertices` and `mesh.triangles` managed arrays.

`Location: Assets/MeshSlicer/Runtime`

## 4. Game Systems & Domain Concepts

### Mesh Slicing Engine
The heart of the project, responsible for the geometric math of splitting triangles against a plane.
*   `BurstMeshSlicer`: The optimized implementation using `IJob` and `BurstCompile`.
*   `NaiveMeshSlicer`: A managed-code version used for comparison and debugging.
*   `PlaneBasis`: Calculates local 2D coordinates on the slicing plane for UV mapping and triangulation.
*   `SliceResult`: A lightweight container for the two generated `Mesh` objects.

**Extension:** To support multi-material meshes, the `SliceJob` would need to be modified to iterate per sub-mesh and maintain separate index lists for each sub-mesh descriptor.

### Procedural Geometry Generation
Handles the creation of "caps" to seal the holes left by a slice.
*   `CapJob`: Uses a centroid-based fan-triangulation approach. It calculates the centroid of all intersection points, sorts them by angle in plane-local space, and creates triangles.
*   `BenchmarkMeshes`: A utility for creating high-poly spheres and other primitives for testing.

`Location: Assets/MeshSlicer/Runtime`

## 5. Scene Overview
*   **SliceDemo.unity:** A simple demonstration scene showing a single primitive (Cube, Sphere, etc.) being sliced once. Users can adjust the plane normal and separation in the Inspector.
*   **StressDemo.unity:** A benchmarking scene that slices dozens of high-poly spheres every frame. It includes an on-screen GUI to switch between Naive and Burst backends and monitor millisecond timings.
*   **Main.unity:** The entry point scene, often used for overall project setup or as a landing page for the demos.

## 6. UI System
The project uses a minimal UI approach:
*   **Immediate Mode GUI (IMGUI):** Used in `StressController.cs` to display real-time performance metrics (FPS, slicing time in ms, triangle count).
*   **UI Toolkit:** The project includes `Main.uxml` and `DefaultTheme.tss`, suggesting the intention to move towards a modern UI Toolkit interface for demo controls, though current logic relies on Inspector-driven changes and IMGUI overlays.

`Location: Assets/UI`

## 7. Asset & Data Model
*   **Prefabs:** Located in `Assets/Models`, these are standard GameObjects with `MeshFilter` and `MeshRenderer` (e.g., Barrel, Crate) used as targets for slicing.
*   **Materials:** Uses URP Lit shaders (`Assets/MeshSlicer/Demo`). The `Cap` material is often assigned to the procedurally generated faces.
*   **Vertex Data:** Defined by the `Vertex` struct in `BurstMeshSlicer.cs`, which includes Position (float3), Normal (float3), Tangent (float4), and UV (float2).

`Location: Assets/Models`, `Assets/Textures`

## 8. Notes, Caveats & Gotchas
*   **Non-Manifold Geometry:** The current slicer assumes manifold (watertight) input for the best capping results. Slicing open meshes may result in visually incorrect or "broken" caps.
*   **Centroid Capping:** The `CapJob` uses a simple fan-triangulation. While fast, this only works reliably for convex intersection shapes. Complex concave intersections may result in overlapping triangles on the cap.
*   **Memory Management:** The project uses `Allocator.TempJob` for internal arrays. Since the slicing results in new `Mesh` objects every frame in the stress test, `Destroy()` must be called on previous meshes to avoid a massive memory leak in the Unity Managed Heap.
*   **Burst Compatibility:** Ensure that any custom vertex data added to the `Vertex` struct is `blittable` (contains no managed references) to maintain Burst compatibility.