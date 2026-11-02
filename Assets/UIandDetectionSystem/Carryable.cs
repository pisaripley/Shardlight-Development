using UnityEngine;
using UnityEngine.XR;

/// A physics object you can pick up, carry and drop/throw on desktop (press Interact; press again to drop).
/// In VR, add an XR Grab Interactable instead - this script then stays out of the way.
[RequireComponent(typeof(Rigidbody))]
public class Carryable : MonoBehaviour, IInteractable
{
    public float holdDistance = 1.2f, followStrength = 15f, throwSpeed = 3f;

    public string Prompt => held ? "Drop" : "Pick up " + name;

    Rigidbody rb; bool held;

    void Awake() => rb = GetComponent<Rigidbody>();

    public void Interact()
    {
        if (XRSettings.isDeviceActive && GetComponent("XRGrabInteractable")) return;   // VR hands handle it
        held = !held;
        rb.useGravity = !held;
        if (!held && Camera.main) rb.linearVelocity = Camera.main.transform.forward * throwSpeed;   // a gentle toss
    }

    void FixedUpdate()
    {
        if (!held || !Camera.main) return;
        var cam = Camera.main.transform;
        Vector3 target = cam.position + cam.forward * holdDistance;
        rb.linearVelocity = (target - rb.position) * followStrength;            // physics follow: bumps into walls, feels weighty
        rb.angularVelocity *= .9f;
        if ((target - rb.position).sqrMagnitude > 9f) { held = false; rb.useGravity = true; }   // snagged on something: let go
    }
}
