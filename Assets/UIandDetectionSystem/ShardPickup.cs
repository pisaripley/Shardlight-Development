using UnityEngine;
using UnityEngine.Events;

/// Put on a shard model (with a collider). Spins, bobs, and is collected when the player touches it
/// (or call Collect() from an XR grab event). Updates the HUD and can tick off / add objectives.
[RequireComponent(typeof(Collider))]
public class ShardPickup : MonoBehaviour
{
    [Tooltip("Sight, Warmth or Echo (matches the HUD's Shard Names).")] public string shardName = "Echo";
    [TextArea(1, 3)] public string description = "A crystal humming with old light.";
    public string completesObjective = "", nextObjectiveId = "";
    [TextArea(1, 2)] public string nextObjectiveText = "";
    public AudioClip collectClip;
    public GameObject collectEffect;
    public UnityEvent onCollected;
    Vector3 start;

    void Awake()
    {
        start = transform.localPosition;
        GetComponent<Collider>().isTrigger = true;
        if (!GetComponent<Rigidbody>()) { var rb = gameObject.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false; }
        if (ShardlightHUD.HasShard(shardName)) gameObject.SetActive(false);
    }

    void Update()
    {
        transform.Rotate(0f, 45f * Time.deltaTime, 0f, Space.World);
        transform.localPosition = start + Vector3.up * Mathf.Sin(Time.time * 1.5f) * .08f;
    }

    void OnTriggerEnter(Collider other) { if (ShardlightHUD.IsPlayer(other)) Collect(); }

    public void Collect()
    {
        ShardlightHUD.CollectShard(shardName, collectClip);
        Inventory.Add("shard_" + shardName, "Shard of " + shardName, description, false);
        if (completesObjective != "") ShardlightHUD.CompleteObjective(completesObjective);
        if (nextObjectiveId != "") ShardlightHUD.AddObjective(nextObjectiveId, nextObjectiveText);
        if (collectEffect) Instantiate(collectEffect, transform.position, Quaternion.identity);
        onCollected.Invoke();
        gameObject.SetActive(false);
    }
}