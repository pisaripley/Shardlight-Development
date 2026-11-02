using UnityEngine;

/// Alert level for the ruins. +1 each time a golem throws you out (max 3). While it's up, golems keep
/// hunting and notice you faster. Drops by 1 after calmDownTime seconds unseen. Creates itself when needed.
public class WantedSystem : MonoBehaviour
{
    [Range(1, 5)] public int maxLevel = 3;
    public float calmDownTime = 25f, suspicionBoostPerLevel = .35f, huntSpeedBoost = 1.25f;

    static WantedSystem inst;
    int level; float timer;

    public static int Level => inst ? inst.level : 0;
    public static int MaxLevel => inst ? inst.maxLevel : 3;
    public static bool IsActive => Level > 0;
    public static float SuspicionMultiplier => 1f + Level * (inst ? inst.suspicionBoostPerLevel : 0f);
    public static float HuntSpeed => inst ? inst.huntSpeedBoost : 1f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset0() => inst = null;

    public static void ReportCaught() { if (!inst) new GameObject("WantedSystem").AddComponent<WantedSystem>(); inst.Set(inst.level + 1); }
    public static void ReportSeen() { if (inst) inst.timer = 0f; }
    public static void Clear() { if (inst) inst.Set(0); }

    void Awake() { if (inst && inst != this) Destroy(this); else inst = this; }
    void Update() { if (level > 0 && (timer += Time.deltaTime) >= calmDownTime) Set(level - 1); }
    void Set(int l) { level = Mathf.Clamp(l, 0, maxLevel); timer = 0f; }
}