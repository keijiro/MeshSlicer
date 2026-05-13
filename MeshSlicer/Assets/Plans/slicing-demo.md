# Project Overview
- Game Title: Mesh Slicer Demo
- High-Level Concept: A physics-based demo where falling objects are sliced by rapid mouse movements.
- Players: Single player (Mouse control).
- Inspiration / Reference Games: Fruit Ninja.
- Tone / Art Direction: Technical demo / Clean.
- Target Platform: PC (Standalone).
- Render Pipeline: URP.

# Game Mechanics
## Core Gameplay Loop
1. Objects (Barrel, Crate, etc.) fall from a spawner above the camera's view.
2. The player moves the mouse rapidly.
3. When the mouse stops, a slicing plane is generated based on the movement vector.
4. Objects intersecting the plane are split into two physics-enabled pieces.
5. The floor remains intact.

## Controls and Input Methods
- **Input System**: New Input System is used. Access via `UnityEngine.InputSystem.Pointer.current` and `UnityEngine.InputSystem.Mouse.current` directly (no Action Maps).
- **Mouse Movement**: Tracked to calculate velocity.
- **Slicing Trigger**: Activated when mouse velocity exceeds a threshold and then drops (stop).

# UI
- **UI Toolkit**: Use `UIDocument` (UI Toolkit) for on-screen information.
- Minimalist: A simple overlay showing slicing instructions or status.
- Performance stats (optional).

# Key Asset & Context
- `Assets/MeshSlicer/Runtime/BurstMeshSlicer.cs`: The core slicing engine.
- `Assets/Models/*.prefab`: Source models for slicing.
- `Assets/Scripts/SlicingController.cs`: Handles input and triggers slicing.
- `Assets/Scripts/ObjectSpawner.cs`: Manages object lifecycle.
- `Assets/Scripts/Sliceable.cs`: Component to identify objects that can be sliced.
- `Assets/UI/SlicingUI.uxml` / `SlicingUI.uss`: UI for the demo.

# Implementation Steps
## 1. Scene Setup
- Create a new scene `Assets/Scenes/SlicingDemo.unity`.
- Add a camera at `(0, 5, -15)` looking at `(0, 2, 0)`.
- Add a floor (Cube) at `(0, -1, 0)` with scale `(40, 1, 40)`. Assign to "Floor" layer.
- Add a Directional Light.
- Add a `UIDocument` GameObject and assign a `PanelSettings` and the new UXML.

## 2. Implement UI
- Create `Assets/UI/SlicingUI.uss` and `Assets/UI/SlicingUI.uxml`.
- Add a simple label for instructions: "Swipe fast and stop to slice!"

## 3. Implement `Sliceable` component
- Create `Assets/Scripts/Sliceable.cs`.
- This component will store references to the original material and provide a method for the slicing logic to apply results.

## 4. Implement `ObjectSpawner`
- Create `Assets/Scripts/ObjectSpawner.cs`.
- Randomly select prefabs from `Assets/Models`.
- Instantiate them at `(Random.Range(-5, 5), 10, 0)` with a `Rigidbody` and `MeshCollider` (convex).
- Ensure they have the `Sliceable` component.

## 5. Implement `SlicingController`
- Create `Assets/Scripts/SlicingController.cs`.
- Use `UnityEngine.InputSystem.Pointer.current` to track mouse position and velocity.
- Track mouse position history (last 5-10 frames).
- Calculate average velocity in pixels/second.
- Logic: 
    - `isSwiping = velocity > StartThreshold`
    - If `isSwiping` and `velocity < StopThreshold`:
        - Perform Slice using the vector between "start of swipe" and "current position".
- Plane calculation:
    - `startRay = cam.ScreenPointToRay(swipeStartPos)`
    - `endRay = cam.ScreenPointToRay(swipeEndPos)`
    - `planeNormal = cross(startRay.direction, endRay.direction)`
    - `plane = new Plane(planeNormal, cam.transform.position)`
- Slicing execution:
    - For each `Sliceable` in scene:
        - Transform world plane to local space.
        - Call `BurstMeshSlicer.Slice`.
        - Create two children GOs with `Rigidbody` and `MeshCollider`.
        - Inherit Rigidbody velocity/angular velocity.
        - Destroy parent.

## 5. Refinement and Physics
- Ensure the floor is on a separate layer and excluded from slicing.
- Add a small "explosion" force to the sliced pieces to separate them visually.

# Verification & Testing
- **Input Check**: Move mouse slowly (no slice), move fast and stop (slice).
- **Physics Check**: Sliced pieces should fall and collide with the floor.
- **Geometry Check**: Check if caps are correctly generated.
- **Boundary Check**: Slice an object multiple times.
