using System;
using UnityEngine;
using UnityEngine.Events;

/// Put on an NPC (with a collider). Walk up and press Interact to talk. Lines play in order; a line with
/// choices branches (Go To = line number, -1 = end). Lines can add/complete objectives and give items.
public class NPCDialogue : MonoBehaviour, IInteractable
{
    [Serializable] public class Choice { public string text = "Tell me more."; public int goTo = -1; }

    [Serializable] public class Line
    {
        [TextArea(2, 4)] public string text;
        public Choice[] choices;
        [Tooltip("-2 = next line, -1 = end, 0+ = jump to that line.")] public int next = -2;
        public string completeObjectiveId, addObjectiveId, objectiveText;
        public string giveItemId, giveItemName;
        [TextArea(1, 3)] public string giveItemDescription;
    }

    public string npcName = "Tomas";
    public Line[] lines;
    [Tooltip("Turn to face the player while talking.")] public bool facePlayer = true;
    public UnityEvent onFinished;

    public string Prompt => "Talk to " + npcName;

    public void Interact()
    {
        if (facePlayer && Camera.main)
        {
            var to = Vector3.ProjectOnPlane(Camera.main.transform.position - transform.position, Vector3.up);
            if (to.sqrMagnitude > .01f) transform.rotation = Quaternion.LookRotation(to);
        }
        ShardlightMenus.StartDialogue(this);
    }

    void Reset()   // example conversation for Tomas the scholar in the Ancient Ruins
    {
        lines = new[]
        {
            new Line { text = "Careful, traveller. The golem has walked these halls since before the Keepers fell." },
            new Line { text = "It will not harm you. It only puts things back where they belong... including you.",
                choices = new[] { new Choice { text = "How do I get past it?", goTo = 2 }, new Choice { text = "Why is it here?", goTo = 3 } } },
            new Line { text = "Stay out of its sight and keep low near the pillars. Your radar shows where it looks.", next = 4 },
            new Line { text = "Vane bound it here so nothing in the castle could ever change. Not even the dust.", next = 4 },
            new Line { text = "The Shard of Echo rests in the keep. Take this map of the old halls.",
                addObjectiveId = "keep", objectiveText = "Sneak past the [Golem] into the {Keep}",
                giveItemId = "map", giveItemName = "Keeper's Map", giveItemDescription = "A faded map of the castle. The keep is marked in gold ink." },
        };
    }
}
