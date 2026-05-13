using UnityEngine;
using MeshSlicer;
using Unity.Mathematics;
using Plane = Unity.Mathematics.Geometry.Plane;

public class Sliceable : MonoBehaviour
{
    public Material material;
    public float remainingLife = 10f;

    void Update()
    {
        remainingLife -= Time.deltaTime;
        if (remainingLife <= 0)
        {
            Destroy(gameObject);
        }
    }

    public void Slice(Plane worldPlane)
    {
        MeshFilter mf = GetComponentInChildren<MeshFilter>();
        if (mf == null) return;

        Mesh sourceMesh = mf.sharedMesh;
        if (sourceMesh == null) return;

        // Convert world plane to local space
        float3 localNormal = transform.InverseTransformDirection(worldPlane.Normal);
        float3 localPoint = transform.InverseTransformPoint((float3)worldPlane.Normal * -worldPlane.Distance);
        var localPlane = Plane.CreateFromUnitNormalAndPointInPlane(localNormal, localPoint);

        var result = BurstMeshSlicer.Slice(sourceMesh, localPlane);

        if (result.Positive != null && result.Negative != null)
        {
            CreateHalf(result.Positive, "Positive", worldPlane.Normal);
            CreateHalf(result.Negative, "Negative", -worldPlane.Normal);
            Destroy(gameObject);
        }
    }

    private void CreateHalf(Mesh mesh, string name, float3 pushDir)
    {
        GameObject go = new GameObject(gameObject.name + "_" + name);
        go.transform.position = transform.position;
        go.transform.rotation = transform.rotation;
        go.transform.localScale = transform.localScale;

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = material != null ? material : GetComponentInChildren<MeshRenderer>().sharedMaterial;

        var mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
        mc.convex = true;

        var rb = go.AddComponent<Rigidbody>();
        var originalRb = GetComponent<Rigidbody>();
        if (originalRb != null)
        {
            rb.linearVelocity = originalRb.linearVelocity;
            rb.angularVelocity = originalRb.angularVelocity;
        }

        // Apply a small impulse and torque to separate the pieces with a twist
        rb.AddForce((Vector3)pushDir * 3.0f, ForceMode.Impulse);
        
        // Add random torque for "twist"
        Vector3 randomTorque = new Vector3(
            UnityEngine.Random.Range(-1f, 1f),
            UnityEngine.Random.Range(-1f, 1f),
            UnityEngine.Random.Range(-1f, 1f)
        ) * 5.0f;
        rb.AddTorque(randomTorque, ForceMode.Impulse);

        var newSliceable = go.AddComponent<Sliceable>();
        newSliceable.material = mr.sharedMaterial;
        // Inherit the remaining life time
        newSliceable.remainingLife = remainingLife;
    }
}
