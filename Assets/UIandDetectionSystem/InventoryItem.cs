using UnityEngine;
using UnityEngine.Events;

/// A collectable that goes into the satchel (lore scroll, key, torch oil...). Press Interact, or touch it in VR.
[RequireComponent(typeof(Collider))]
public class InventoryItem : MonoBehaviour, IInteractable
{
    public string id = "scroll_1", itemName = "Lore Scroll";
    [TextArea(2, 4)] public string description = "A brittle scroll about the last Keeper.";
    public bool collectOnTouch = true;
    public string completesObjective = "";
    public UnityEvent onCollected;

    public string Prompt => "Pick up " + itemName;

    void Awake() { if (Inventory.Has(id)) gameObject.SetActive(false); }

    void OnTriggerEnter(Collider other) { if (collectOnTouch && ShardlightHUD.IsPlayer(other)) Interact(); }

    public void Interact()
    {
        Inventory.Add(id, itemName, description);
        ShardlightHUD.PlayChime(2);
        if (completesObjective != "") ShardlightHUD.CompleteObjective(completesObjective);
        onCollected.Invoke();
        gameObject.SetActive(false);
    }
}
