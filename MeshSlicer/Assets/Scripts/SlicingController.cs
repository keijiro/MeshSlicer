using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using System.Collections.Generic;
using MeshSlicer;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;

public class SlicingController : MonoBehaviour
{
    [Header("Slicing Sensitivity (Normalized)")]
    [Tooltip("Min velocity to start a swipe (normalized by screen height per second)")]
    public float startThreshold = 2.0f; 
    [Tooltip("Velocity below which a swipe is considered finished")]
    public float stopThreshold = 0.5f;
    [Tooltip("Number of frames to average for velocity calculation")]
    public int historyCount = 3;
    public Material planeMaterial;

    private Camera _cam;
    private Vector2 _swipeStartPos;
    private bool _isSwiping;
    private Vector2 _lastPos;
    private float _currentVelocity;
    private Queue<float> _velocityHistory = new Queue<float>();

    void Start()
    {
        _cam = Camera.main;
        var pointer = Pointer.current;
        if (pointer != null)
        {
            _lastPos = pointer.position.ReadValue();
        }
    }

    void Update()
    {
        var pointer = Pointer.current;
        if (pointer == null) return;

        Vector2 currentPos = pointer.position.ReadValue();
        float dt = Time.deltaTime;
        if (dt > 0)
        {
            // Normalize delta by screen height for resolution independence
            Vector2 delta = (currentPos - _lastPos) / Screen.height;
            float instantVelocity = delta.magnitude / dt;
            
            _velocityHistory.Enqueue(instantVelocity);
            if (_velocityHistory.Count > historyCount) _velocityHistory.Dequeue();

            float avgVelocity = 0;
            foreach (var v in _velocityHistory) avgVelocity += v;
            avgVelocity /= _velocityHistory.Count;
            _currentVelocity = avgVelocity;
        }

        if (!_isSwiping && _currentVelocity > startThreshold)
        {
            _isSwiping = true;
            _swipeStartPos = currentPos;
        }
        else if (_isSwiping && _currentVelocity < stopThreshold)
        {
            ExecuteSlice(_swipeStartPos, currentPos);
            _isSwiping = false;
        }

        _lastPos = currentPos;
    }

    void ExecuteSlice(Vector2 start, Vector2 end)
    {
        if (Vector2.Distance(start, end) < 10f) return;

        // Screen center point for the plane to pass through
        Vector2 mid = (start + end) * 0.5f;
        Ray midRay = _cam.ScreenPointToRay(mid);
        
        Ray startRay = _cam.ScreenPointToRay(start);
        Ray endRay = _cam.ScreenPointToRay(end);

        // The plane normal is the cross product of the two rays from the camera
        float3 normal = math.normalize(math.cross(startRay.direction, endRay.direction));
        
        // Plane passing through the camera position (camera-centric slice)
        Plane worldPlane = Plane.CreateFromUnitNormalAndPointInPlane(normal, (float3)_cam.transform.position);

        // Visualize as a line following the swipe
        VisualizeLine(start, end);

        // Find all sliceables
        var sliceables = Object.FindObjectsByType<Sliceable>(FindObjectsSortMode.None);
        foreach (var s in sliceables)
        {
            s.Slice(worldPlane);
        }
    }

    void VisualizeLine(Vector2 start, Vector2 end)
    {
        GameObject lineGo = new GameObject("SlicingLineVisual");
        var line = lineGo.AddComponent<LineRenderer>();
        
        line.sharedMaterial = planeMaterial;
        line.startWidth = 0.05f;
        line.endWidth = 0.05f;
        line.positionCount = 2;
        line.useWorldSpace = true;

        // Points in 3D space
        float dist = 5.0f;
        Vector3 pStart = _cam.ScreenToWorldPoint(new Vector3(start.x, start.y, dist));
        Vector3 pEnd = _cam.ScreenToWorldPoint(new Vector3(end.x, end.y, dist));

        StartCoroutine(AnimateLine(line, pStart, pEnd));
    }

    IEnumerator AnimateLine(LineRenderer line, Vector3 pStart, Vector3 pEnd)
    {
        float duration = 0.35f; // Total duration: 0.1s (grow) + 0.25s (shrink)
        float elapsed = 0f;
        float growDuration = 0.1f;
        
        while (elapsed < duration)
        {
            if (line == null) yield break;

            elapsed += Time.deltaTime;
            float t = elapsed / duration;
            
            // 1. Color Animation
            Color c;
            if (t < 0.4f) // White to Red (40%)
            {
                c = Color.Lerp(Color.white, Color.red, t / 0.4f);
            }
            else // Red to Transparent (60%)
            {
                float t2 = (t - 0.4f) / 0.6f;
                c = Color.Lerp(Color.red, new Color(1, 0, 0, 0), t2);
            }
            
            Gradient g = new Gradient();
            g.SetKeys(
                new GradientColorKey[] { new GradientColorKey(c, 0.0f), new GradientColorKey(c, 1.0f) },
                new GradientAlphaKey[] { new GradientAlphaKey(c.a, 0.0f), new GradientAlphaKey(c.a, 1.0f) }
            );
            line.colorGradient = g;

            // 2. Vertex Animation
            if (elapsed < growDuration)
            {
                // Rapid Growth: Start fixed, End moves from pStart to pEnd
                float gt = elapsed / growDuration;
                line.SetPosition(0, pStart);
                line.SetPosition(1, Vector3.Lerp(pStart, pEnd, gt));
            }
            else
            {
                // Slow Shrink: Start moves from pStart to pEnd, End fixed at pEnd
                float st = (elapsed - growDuration) / (duration - growDuration);
                line.SetPosition(0, Vector3.Lerp(pStart, pEnd, st));
                line.SetPosition(1, pEnd);
            }
            
            yield return null;
        }
        
        if (line != null) Destroy(line.gameObject);
    }
    }

