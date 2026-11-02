using UnityEngine;
using UnityEngine.Events;

/// A lever (or switch, wheel...) that opens a door or gate. Press Interact while looking at it.
/// The handle swings, the door slides/rotates open, and it can tick off an objective.
public class Lever : MonoBehaviour, IInteractable
{
    public Transform handle;
    public Vector3 handleAngle = new Vector3(0f, 0f, -70f);
    public Transform door;
    [Tooltip("How far the door moves when open (local space). Gates usually slide up.")] public Vector3 doorMove = new Vector3(0f, 3f, 0f);
    public Vector3 doorRotate;
    public float speed = 1.5f;
    public bool oneWay = true;
    public string completesObjective = "";
    [TextArea(1, 2)] public string hint = "Somewhere, a gate grinds open...";
    public AudioClip sound;
    public UnityEvent onPulled;

    public string Prompt => on ? "Push the lever back" : "Pull the lever";

    bool on; float t;
    Quaternion handle0, door0; Vector3 doorPos0;

    void Awake()
    {
        if (handle) handle0 = handle.localRotation;
        if (door) { door0 = door.localRotation; doorPos0 = door.localPosition; }
    }

    public void Interact()
    {
        if (oneWay && on) return;
        on = !on;
        if (sound) AudioSource.PlayClipAtPoint(sound, transform.position);
        if (on && completesObjective != "") ShardlightHUD.CompleteObjective(completesObjective);
        if (on && hint != "") ShardlightHUD.ShowHint(hint, 3f);
        onPulled.Invoke();
    }

    void Update()
    {
        t = Mathf.MoveTowards(t, on ? 1f : 0f, Time.deltaTime * speed);
        float e = Mathf.SmoothStep(0f, 1f, t);
        if (handle) handle.localRotation = handle0 * Quaternion.Euler(handleAngle * Mathf.Clamp01(t * 3f));   // handle snaps, door follows slowly
        if (door) { door.localPosition = doorPos0 + doorMove * e; door.localRotation = door0 * Quaternion.Euler(doorRotate * e); }
    }
}
