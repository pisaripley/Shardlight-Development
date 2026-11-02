using UnityEngine;
using UnityEngine.Events;

/// Invisible trigger box: when the player walks in, completes and/or adds an objective, or shows a hint.
/// Leave a field empty to skip that step. Fire() can also be called from levers, buttons, XR events.
[RequireComponent(typeof(Collider))]
public class ObjectiveTrigger : MonoBehaviour
{
    public string completeId = "";
    public string addId = "";
    [TextArea(1, 3)] public string addText = "";
    [TextArea(1, 3)] public string hint = "";
    public bool onlyOnce = true;
    public UnityEvent onTriggered;
    bool used;

    void Awake()
    {
        GetComponent<Collider>().isTrigger = true;
        if (!GetComponent<Rigidbody>()) { var rb = gameObject.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false; }
    }

    void OnTriggerEnter(Collider other) { if (ShardlightHUD.IsPlayer(other)) Fire(); }

    public void Fire()
    {
        if (used && onlyOnce) return;
        used = true;
        if (completeId != "") ShardlightHUD.CompleteObjective(completeId);
        if (addId != "") ShardlightHUD.AddObjective(addId, addText);
        if (hint != "") ShardlightHUD.ShowHint(hint);
        onTriggered.Invoke();
    }
}