using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public enum StealthLevel { Hidden, Suspicious, Detected }

/// Shardlight HUD (VR + desktop). Top-left health + stamina with hints/shards/alert under, bottom objectives,
/// bottom-left radar, top-right DISCOVERED. Health: ShardlightHUD.Damage(.25f) / Heal(.25f).
/// Also watches the golems (stealth state + sounds). One per scene.
/// Code: ShardlightHUD.AddObjective("gate", "Sneak past the [Golem] to the {Castle Gate}"); CompleteObjective("gate");
///       ShowHint("..."); CollectShard("Echo");   {gold}  [red]
[DefaultExecutionOrder(-50)]
public class ShardlightHUD : MonoBehaviour
{
    [Serializable] public class Objective { public string id = "find_shard"; [TextArea(1, 3)] public string text = "Find the {Shard of Echo} hidden in the ruins"; public bool optional; [NonSerialized] public bool done; }

    [Header("Objectives - {gold}  [red]")]
    public List<Objective> objectives = new List<Objective> { new Objective() };
    public Color keyColor = new Color(1f, .78f, .27f), dangerColor = new Color(1f, .42f, .32f);
    [Header("Hints")]
    [TextArea(1, 3)] public string startHint = "Stay out of the golems' sight. Their vision cones show on your {radar}.";
    [Header("Shards")]
    public string[] shardNames = { "Sight", "Warmth", "Echo" };
    public Color[] shardColors = { new Color(1f, .93f, .62f), new Color(1f, .58f, .24f), new Color(.74f, .64f, 1f) };
    [Header("Discovered / Seeking")]
    [Tooltip("dimTime: seconds for SEEKING to fade red -> white -> grey while they search. fadeOutTime: vanish once they stop.")]
    public float blinkRate = 2f, dimTime = 15f, fadeOutTime = 1.5f;
    public bool showSnapshot = true;
    [Header("Radar")]
    public float radarRange = 30f, radarRangeDiscovered = 55f, radarSize = 240f, pursuitRadius = 7f, searchRadius = 11f;
    [Header("Health & Stamina")]
    public Color healthColor = new Color(.92f, .27f, .32f), staminaColor = new Color(1f, .83f, .3f);
    /// 0-1. Stamina is driven by the player's sprinting (DesktopForestPlayer).
    public static float Health = 1f, Stamina = 1f;
    public static bool Exhausted;
    /// What the player is holding ("bow", "bomb"...), set by WristInventory. Opens the aim ring.
    public static string HeldItem;
    /// Ammo for the held weapon ("bow"/"bomb", or null to hide), set by WristInventory. Refill 0-1 shows a recharge bar, -1 hides it.
    public static string AmmoKind; public static int AmmoCount, AmmoMax; public static float AmmoRefill = -1f;                      // ran out: no sprint until it refills a bit
    public static void Damage(float amount) { Health = Mathf.Clamp01(Health - amount); if (Instance && amount > 0f) Instance.hurt = 1f; }   // also flashes the screen red
    /// Spend stamina (sprint, jump, aim, swing). False = not enough, so don't do the action. Refills shortly after you stop.
    public static bool UseStamina(float amount)
    {
        if (Exhausted || Stamina <= 0f) return false;
        Stamina = Mathf.Max(0f, Stamina - amount); StaminaUsedAt = Time.time;
        if (Stamina <= 0f) Exhausted = true;
        return true;
    }
    public static float StaminaUsedAt = -9f;
    /// True while the player aims a bow or bomb (desktop): zooms the view, slows movement, tightens the reticle.
    public static bool Aiming;
    public static void Heal(float amount) => Health = Mathf.Clamp01(Health + amount);
    [Header("Layout")]
    public bool forceVR;
    public TMP_FontAsset font;
    public float vrDistance = 1.6f, vrScale = .0013f, vrDeadzone = 15f;
    [Header("Colours")]
    public Color gold = new Color(1f, .82f, .42f), panel = new Color(.08f, .07f, .09f, .82f), textColor = new Color(.97f, .94f, .87f);
    public Color calm = new Color(.62f, .76f, .92f), suspicious = new Color(1f, .8f, .25f), detected = new Color(1f, .3f, .2f);
    [Header("Sounds (optional - chimes are generated if empty)")]
    public AudioClip objectiveClip, shardClip, suspiciousClip, detectedClip, caughtClip;
    [Range(0, 1)] public float volume = .7f;

    public static ShardlightHUD Instance { get; private set; }
    static readonly List<string> shards = new List<string>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset0() { Instance = null; shards.Clear(); Health = Stamina = 1f; Exhausted = false; }

    bool built, vr, engaged, wasDetected, wasSus, wasCaught, searching;
    StealthLevel level;
    Image hurtImg; float hurt;
    CanvasGroup ammoGroup; Image ammoIcon, ammoBar; TextMeshProUGUI ammoText; Sprite arrowIcon, bombIcon; int lastAmmo = -1; float ammoPop;
    Image reticle, hitX; float hitFlash; bool hitKill;
    readonly Dictionary<string, Image> aims = new Dictionary<string, Image>();
    /// Flashes the hit marker on the reticle (red X when something breaks).
    public static void HitMarker(bool kill = false) { var h = Instance; if (h) { h.hitFlash = 1f; h.hitKill = kill; } }
    Image hpFill, stFill; float hpShown = 1f, stShown = 1f; const float BarW = 220f;
    float seek, fade, alpha, pop, yaw, range, golemScan, hintUntil;
    Vector3 seenFrom;                                   // eyes of the golem that spotted you (for the photo)
    RawImage photo; Image photoFrame;
    int lastWanted;
    Transform cam;
    RectTransform canvasRT, world, northRT;
    TextMeshProUGUI hint, objMain, objExtra, disc, shardCount;
    GameObject alertRow;
    Image[] shardPips, alertPips;
    Image rim;
    AudioSource audioSrc;
    Sprite circle, ring, rounded;
    Material overlay;
    GolemGuardAI[] golems = new GolemGuardAI[0];
    ShardPickup[] pickups = new ShardPickup[0];
    readonly List<Blip> blips = new List<Blip>();
    readonly List<Image> shardMarks = new List<Image>();
    class Blip { public Image zone, dot; public RadarCone cone; }
    readonly List<Vector3> used = new List<Vector3>();   // search zones already drawn this frame

    // ---------------------------------------------------------------- public API
    public static void AddObjective(string id, string text, bool optional = false)
    {
        var h = Instance; if (!h) return;
        var o = h.Find(id);
        if (o == null) { h.objectives.Add(new Objective { id = id, text = text, optional = optional }); h.Play(h.objectiveClip, 0); }
        else { o.text = text; o.done = false; }
        h.RefreshObjectives();
    }
    public static void CompleteObjective(string id)
    {
        var h = Instance; var o = h ? h.Find(id) : null; if (o == null || o.done) return;
        o.done = true; h.Play(h.objectiveClip, 1); h.RefreshObjectives(); h.StartCoroutine(h.RemoveLater(o));
    }
    public static void SetObjectiveText(string id, string text) => AddObjective(id, text);
    public static void RemoveObjective(string id) { var h = Instance; if (h) { h.objectives.RemoveAll(o => o.id == id); h.RefreshObjectives(); } }
    public static void ShowHint(string text, float seconds = 5f) { var h = Instance; if (!h || !h.built) return; h.hint.text = h.Markup(text); h.hintUntil = Time.time + seconds; }
    public static void ShowMessage(string text, float seconds = 3f) => ShowHint(text, seconds);
    public static bool HasShard(string name) => shards.Contains(name);
    /// New game: forget collected shards.
    public static void ResetProgress() { shards.Clear(); Health = Stamina = 1f; Exhausted = false; }
    public static int ShardsCollected => shards.Count;
    public static void CollectShard(string name, AudioClip clip = null)
    {
        if (!shards.Contains(name)) shards.Add(name);
        var h = Instance; if (!h) return;
        h.RefreshShards(); h.Play(clip ? clip : h.shardClip, 2);
        ShowHint("{Shard of " + name + "} recovered", 3.5f);
    }
    /// Short status for the VR wrist watch (WristInventory): current objective, shards, alert. Plain text, no markup.
    public static string WristSummary
    {
        get
        {
            var h = Instance; if (!h) return "";
            var o = h.objectives.Find(x => !x.done && !x.optional) ?? h.objectives.Find(x => !x.done);
            string goal = o == null ? "No current objective" : Regex.Replace(o.text ?? "", @"[\{\}\[\]]", "");
            return goal + "\nShards " + shards.Count + "/" + h.shardNames.Length + (WantedSystem.Level > 0 ? "   Alert " + WantedSystem.Level : "");
        }
    }
    public static bool IsPlayer(Component c) { for (var t = c.transform; t; t = t.parent) if (t.CompareTag("Player")) return true; return false; }

    // ---------------------------------------------------------------- setup
    void Awake() { if (Instance && Instance != this) { enabled = false; return; } Instance = this; audioSrc = gameObject.AddComponent<AudioSource>(); range = radarRange; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    IEnumerator Start()
    {
        yield return null;                                   // let XR start first
        vr = forceVR || XRSettings.isDeviceActive;
        cam = Camera.main ? Camera.main.transform : null;
        circle = Sdf(64, p => p.magnitude - 30f, 0);
        ring = Sdf(128, p => Mathf.Abs(p.magnitude - 60f) - 1.5f, 0);
        var heart = Sdf(64, p => { p.y += 4f; var q = new Vector2(p.x + p.y, p.y - p.x) * .7071f;   // square turned 45deg + two round lobes
            var d = new Vector2(Mathf.Max(Mathf.Abs(q.x) - 12f, 0), Mathf.Max(Mathf.Abs(q.y) - 12f, 0)).magnitude + Mathf.Min(Mathf.Max(Mathf.Abs(q.x) - 12f, Mathf.Abs(q.y) - 12f), 0);
            return Mathf.Min(d, Mathf.Min((p - new Vector2(-8.5f, 8.5f)).magnitude, (p - new Vector2(8.5f, 8.5f)).magnitude) - 12f); }, 0);
        var bolt = Sdf(64, p => Poly(p, new[] { new Vector2(6, 28), new Vector2(-15, -1), new Vector2(-2, -1), new Vector2(-6, -28), new Vector2(15, 5), new Vector2(2, 5) }), 0);
        rounded = Sdf(64, p => { var q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - new Vector2(18f, 18f); return new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude - 12f; }, 20);

        var go = new GameObject("HUD Canvas", typeof(RectTransform), typeof(Canvas));
        go.transform.SetParent(transform, false);
        canvasRT = (RectTransform)go.transform;
        var canvas = go.GetComponent<Canvas>();
        if (vr) { overlay = new Material(Shader.Find("UI/Default")); overlay.SetFloat("unity_GUIZTestMode", 8f); }   // VR: HUD never clips into walls
        if (vr) { canvas.renderMode = RenderMode.WorldSpace; canvasRT.sizeDelta = new Vector2(1600, 900); canvasRT.localScale = Vector3.one * vrScale; }
        else
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var s = go.AddComponent<CanvasScaler>(); s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; s.referenceResolution = new Vector2(1920, 1080); s.matchWidthOrHeight = .5f;
        }
        float m = vr ? 40f : 36f, k = vr ? 1.25f : 1f;       // margin, VR text boost

        // whole screen: red shade when you take damage (drawn first, so everything else sits on top)
        var hr = Rect("Hurt", canvasRT, Vector2.zero, Vector2.zero, Vector2.zero); hr.anchorMax = Vector2.one;
        hurtImg = Img(hr, Sdf(128, p => .2f - .6f * p.magnitude / 64f, 0), Color.clear);   // light tint in the middle, stronger at the edges

        // top left: health (heart) + stamina (bolt) bars
        var vt = Rect("Vitals", canvasRT, new Vector2(0, 1), new Vector2(m, -m), new Vector2(BarW + 70f, 78f)); vt.localScale = Vector3.one * k;
        var vbg = Img(vt, rounded, panel); vbg.type = Image.Type.Sliced;
        hpFill = Bar(vt, heart, healthColor, 0); stFill = Bar(vt, bolt, staminaColor, 1);

        // right under it: hint + shards + alert
        var tl = Rect("Hints", canvasRT, new Vector2(0, 1), new Vector2(m, -m - 88f * k), new Vector2(400, 10));
        var bg = Img(tl, rounded, panel); bg.type = Image.Type.Sliced;
        var v = tl.gameObject.AddComponent<VerticalLayoutGroup>(); v.padding = new RectOffset(20, 18, 14, 14); v.spacing = 8; v.childControlHeight = v.childControlWidth = true; v.childForceExpandHeight = false;
        tl.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        hint = Text(tl, "", 19 * k, textColor);
        shardCount = Text(tl, "", 15 * k, gold);
        shardPips = Pips(shardCount.transform, shardNames.Length, 90f * k);
        var a = Text(tl, "ALERT", 15 * k, detected); alertRow = a.gameObject;
        alertPips = Pips(a.transform, WantedSystem.MaxLevel, 84f * k);

        // bottom: objectives
        var ob = Rect("Objectives", canvasRT, new Vector2(.5f, 0), new Vector2(0, m), new Vector2(vr ? 760 : 940, 130));
        objMain = Text(ob, "", 30 * k, textColor); objMain.alignment = TextAlignmentOptions.Bottom; Fill(objMain.rectTransform, 0, 76);
        objExtra = Text(ob, "", 20 * k, textColor * .8f); objExtra.alignment = TextAlignmentOptions.Top; Fill(objExtra.rectTransform, 80, 50);
        objMain.outlineWidth = objExtra.outlineWidth = .2f;

        // top right: DISCOVERED
        var tr = Rect("Discovered", canvasRT, new Vector2(1, 1), new Vector2(-m, -m), new Vector2(560, 200));
        disc = Text(tr, "DISCOVERED", 50 * k, detected, FontStyles.Bold); disc.alignment = TextAlignmentOptions.TopRight; disc.characterSpacing = 14; disc.outlineWidth = .15f; Fill(disc.rectTransform, 0, 64); disc.rectTransform.pivot = Vector2.one;
        photoFrame = Img(Rect("Photo", tr, new Vector2(1, 1), new Vector2(0, -72), Vector2.one * 112 * k), null, Color.clear);   // snapshot of you, under the word
        photo = Rect("Image", photoFrame.transform, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 104 * k).gameObject.AddComponent<RawImage>();
        photo.raycastTarget = false; if (overlay) photo.material = overlay;
        disc.alpha = 0; photo.color = Color.clear;

        // top right, under DISCOVERED + photo: ammo for the held bow / bombs
        arrowIcon = Sdf(64, p => Mathf.Min(Mathf.Min(Seg(p, new Vector2(-20, -20), new Vector2(14, 14)) - 2.5f,
                                                     Poly(p, new[] { new Vector2(24, 24), new Vector2(8, 18), new Vector2(18, 8) })),
                                           Mathf.Min(Seg(p, new Vector2(-20, -20), new Vector2(-24, -10)), Seg(p, new Vector2(-20, -20), new Vector2(-10, -24))) - 2.5f), 0);
        bombIcon = Sdf(64, p => Mathf.Min(Mathf.Min((p - new Vector2(-3, -4)).magnitude - 18f, Seg(p, new Vector2(8, 10), new Vector2(17, 21)) - 2.5f), (p - new Vector2(21, 25)).magnitude - 4f), 0);
        var am = Rect("Ammo", canvasRT, Vector2.one, new Vector2(-m, -m - 196f * k), new Vector2(180f, 60f)); am.localScale = Vector3.one * k;
        ammoGroup = am.gameObject.AddComponent<CanvasGroup>(); ammoGroup.alpha = 0f;
        var abg = Img(am, rounded, panel); abg.type = Image.Type.Sliced;
        ammoIcon = Img(Rect("Icon", am, new Vector2(0, .5f), new Vector2(14f, 0f), Vector2.one * 36f), arrowIcon, gold);
        ammoText = Text(am, "", 32, textColor, FontStyles.Bold); ammoText.alignment = TextAlignmentOptions.Right;
        var at = ammoText.rectTransform; at.anchorMin = Vector2.zero; at.anchorMax = Vector2.one; at.offsetMin = new Vector2(58f, 6f); at.offsetMax = new Vector2(-16f, -4f);
        ammoBar = Img(Rect("Recharge", am, Vector2.zero, new Vector2(58f, 8f), new Vector2(106f, 4f)), null, gold);

        // bottom left: radar (disk masks cones/zones to a circle)
        var frame = Rect("Radar", canvasRT, Vector2.zero, new Vector2(m, m), Vector2.one * radarSize);
        var disk = Rect("Disk", frame, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * radarSize); disk.pivot = new Vector2(.5f, .5f);
        Img(disk, circle, panel); disk.gameObject.AddComponent<Mask>();
        world = Rect("World", disk, new Vector2(.5f, .5f), Vector2.zero, Vector2.zero);
        var you = Cone(disk, 32f, 30f); you.color = new Color(gold.r, gold.g, gold.b, .45f);
        Img(Rect("You", disk, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 10), circle, gold).rectTransform.pivot = new Vector2(.5f, .5f);
        rim = Img(frame, ring, gold);
        var n = Text(frame, "N", 16 * k, gold, FontStyles.Bold); n.alignment = TextAlignmentOptions.Center; northRT = n.rectTransform; northRT.anchorMin = northRT.anchorMax = new Vector2(.5f, .5f); northRT.sizeDelta = new Vector2(24, 24);

        // centre: reticle (desktop only; in VR you aim with your hands). A dot, plus a shape for the weapon you hold.
        if (!vr)
        {
            reticle = Img(Rect("Reticle", canvasRT, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 6f), circle, new Color(1, 1, 1, .85f));
            var shapes = new Dictionary<string, Sprite>
            {
                ["sword"] = Sdf(64, p => Mathf.Max(Mathf.Abs(p.magnitude - 18f) - 2f, 10f - Mathf.Abs(p.x)), 0),                     // ( )  melee brackets
                ["bow"] = Sdf(64, p => { p = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)); return Mathf.Min(Seg(p, new Vector2(0, 9), new Vector2(0, 22)), Seg(p, new Vector2(9, 0), new Vector2(22, 0))) - 2f; }, 0),   // + crosshair
                ["bomb"] = Sdf(64, p => Mathf.Min(Mathf.Abs(p.magnitude - 20f) - 2f, (p - new Vector2(0, -27)).magnitude - 3.5f), 0),   // ring + landing dot
                ["potion"] = Sdf(64, p => Mathf.Abs((p - new Vector2(0, -4)).magnitude - 12f) - 2f, 0),                                  // small soft circle
            };
            shapes["torch"] = shapes["sword"];
            foreach (var kv in shapes) aims[kv.Key] = Img(Rect(kv.Key, reticle.transform, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 44f), kv.Value, Color.clear);
            hitX = Img(Rect("Hit marker", reticle.transform, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 36f), Sdf(64, p => Seg(new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)), new Vector2(9, 9), new Vector2(19, 19)) - 2.5f, 0), Color.clear);
        }

        built = true;
        if (showSnapshot) PlayerSnapshot.Get();            // start tracking your movement now, so the photo catches you mid-action
        RefreshShards(); RefreshObjectives(); ShowHint(startHint, 8f);
    }

    // ---------------------------------------------------------------- per frame
    void Update()
    {
        if (!built) return;
        if (!cam && Camera.main) cam = Camera.main.transform;
        if ((golemScan -= Time.deltaTime) <= 0f) { golemScan = 1f; golems = FindObjectsByType<GolemGuardAI>(FindObjectsSortMode.None); pickups = FindObjectsByType<ShardPickup>(FindObjectsSortMode.None); }
        Watch();
        hint.gameObject.SetActive(Time.time < hintUntil);
        int w = WantedSystem.Level;
        alertRow.SetActive(w > 0);
        for (int i = 0; i < alertPips.Length; i++) alertPips[i].color = i < w ? detected : new Color(1, 1, 1, .15f);
        Discovered();
        Vitals();
        hurt = Mathf.MoveTowards(hurt, 0f, Time.deltaTime * 1.8f); hurtImg.color = new Color(.85f, .05f, .05f, .85f * hurt);
        Ammo();
        if (reticle)
        {
            reticle.gameObject.SetActive(Cursor.lockState == CursorLockMode.Locked && !ShardlightMenus.IsOpen);
            float dt = Time.deltaTime;
            foreach (var kv in aims)   // the held weapon's shape fades and closes in
            {
                float a = Mathf.MoveTowards(kv.Value.color.a, kv.Key == HeldItem ? .9f : 0f, dt * 6f);
                kv.Value.color = new Color(gold.r, gold.g, gold.b, a);
                kv.Value.rectTransform.localScale = Vector3.one * (1.5f - .5f * a / .9f) * (Aiming ? .7f : 1f);   // tighter while aiming
            }
            hitFlash = Mathf.MoveTowards(hitFlash, 0f, dt * 4f);
            var hc = hitKill ? detected : Color.white; hc.a = hitFlash; hitX.color = hc;
            hitX.rectTransform.localScale = Vector3.one * (1f + .3f * hitFlash);
        }
        if (cam) Radar();
    }

    // stealth state from all golems, plus sounds and hints when it changes
    void Watch()
    {
        var l = StealthLevel.Hidden; bool caught = false; searching = WantedSystem.IsActive;
        foreach (var g in golems)
        {
            if (!g || !g.isActiveAndEnabled) continue;
            var s = g.CurrentState;
            var gl = s == GolemGuardAI.State.Chase || s == GolemGuardAI.State.Escort ? StealthLevel.Detected
                   : s == GolemGuardAI.State.Suspicious || g.Suspicion > .15f ? StealthLevel.Suspicious : StealthLevel.Hidden;
            if (gl > l) { l = gl; seenFrom = g.transform.position + Vector3.up * g.eyeHeight; }
            caught |= s == GolemGuardAI.State.Escort;
            searching |= g.IsHunting || s == GolemGuardAI.State.Investigate || s == GolemGuardAI.State.Search;
        }
        if (caught && !wasCaught) { Play(caughtClip, 6); ShowHint("The golem lifts you up and carries you out...", 3.5f); }
        else if (l > level) Play(l == StealthLevel.Detected ? detectedClip : suspiciousClip, l == StealthLevel.Detected ? 4 : 3);
        int w = WantedSystem.Level;
        if (w > lastWanted) ShowHint("The ruins are on {alert}. The golems will search for you.", 4f);
        else if (w == 0 && lastWanted > 0) ShowHint("The ruins have settled. You're safe... for now.", 3.5f);
        wasCaught = caught; level = l; lastWanted = w;
    }

    // SEEKING (yellow, pulsing) while suspected; DISCOVERED (red) when spotted; then SEEKING again, fading
    // red -> white -> grey while they search, and gone once they stop. A photo of you sits underneath.
    void Discovered()
    {
        float dt = Time.deltaTime; bool det = level == StealthLevel.Detected;
        if (det) { if (!engaged || !wasDetected) { pop = 1f; Snap(); } engaged = true; seek = 0f; fade = 1f; }
        else if (engaged)
        {
            if (searching) seek = Mathf.MoveTowards(seek, 1f, dt / dimTime);
            else if ((fade -= dt / fadeOutTime) <= 0f) engaged = false;
        }
        bool sus = !engaged && level == StealthLevel.Suspicious;
        if (sus && !wasSus) Snap();
        wasDetected = det; wasSus = sus;
        pop = Mathf.MoveTowards(pop, 0f, dt * 2.5f);

        float wave = .5f + .5f * Mathf.Sin(Time.time * (vr ? Mathf.Min(blinkRate, 1.2f) : blinkRate) * Mathf.PI * 2f);   // soft pulse, gentler in VR
        Color grey = new Color(.55f, .55f, .55f);
        Color c = det ? Color.Lerp(detected, Color.white, pop * .6f)
                : engaged ? (seek < .5f ? Color.Lerp(detected, Color.white, seek * 2f) : Color.Lerp(Color.white, grey, seek * 2f - 1f))
                : suspicious;
        alpha = det ? 1f : Mathf.MoveTowards(alpha, engaged ? fade : sus ? Mathf.Lerp(.35f, 1f, wave) : 0f, dt * 6f);
        string label = det ? "DISCOVERED" : engaged || sus ? "SEEKING" : disc.text;
        if (disc.text != label) disc.text = label;
        c.a = alpha; disc.color = c;
        disc.rectTransform.localScale = Vector3.one * (1f + .35f * pop * pop);

        bool pic = showSnapshot && photo.texture;
        photoFrame.color = pic ? c : Color.clear;                                    // frame matches the word's colour
        photo.color = pic ? new Color(Mathf.Lerp(1f, .6f, seek), Mathf.Lerp(1f, .6f, seek), Mathf.Lerp(1f, .6f, seek), alpha) : Color.clear;   // greys out with it
    }

    void Ammo()
    {
        float dt = Time.deltaTime;
        ammoGroup.alpha = Mathf.MoveTowards(ammoGroup.alpha, AmmoKind != null ? 1f : 0f, dt * 6f);
        if (AmmoKind == null) return;
        if (AmmoCount != lastAmmo) { if (lastAmmo >= 0) ammoPop = 1f; lastAmmo = AmmoCount; }
        ammoPop = Mathf.MoveTowards(ammoPop, 0f, dt * 5f);
        ammoIcon.sprite = AmmoKind == "bomb" ? bombIcon : arrowIcon;
        ammoText.text = AmmoCount + "<size=55%><alpha=#99> / " + AmmoMax;
        ammoText.color = AmmoCount == 0 ? detected : textColor;   // red when empty
        ammoText.rectTransform.localScale = Vector3.one * (1f + .15f * ammoPop);
        ammoBar.gameObject.SetActive(AmmoRefill >= 0f);
        ammoBar.rectTransform.sizeDelta = new Vector2(106f * Mathf.Clamp01(AmmoRefill), 4f);
    }

    void Vitals()
    {
        float dt = Time.deltaTime, low = .5f + .5f * Mathf.Sin(Time.time * 7f);
        hpShown = Mathf.MoveTowards(hpShown, Health, dt * 1.5f); stShown = Mathf.MoveTowards(stShown, Stamina, dt * 3f);
        SetBar(hpFill, hpShown, Health < .25f ? Color.Lerp(healthColor, Color.white, low * .5f) : healthColor);   // pulses when low
        SetBar(stFill, stShown, Exhausted ? Color.Lerp(staminaColor, Color.grey, .6f) : staminaColor);
    }
    static void SetBar(Image f, float v, Color c) { f.enabled = v > .02f; f.rectTransform.sizeDelta = new Vector2(BarW * v, 12f); f.color = c; }

    Image Bar(RectTransform p, Sprite icon, Color c, int row)
    {
        float cy = -(23f + row * 32f);
        Img(Rect("Icon", p, new Vector2(0, 1), new Vector2(14f, cy + 12f), Vector2.one * 24f), icon, c);
        var back = Img(Rect("Bar", p, new Vector2(0, 1), new Vector2(50f, cy + 6f), new Vector2(BarW, 12f)), rounded, new Color(1, 1, 1, .12f));
        var fill = Img(Rect("Fill", back.transform, new Vector2(0, 1), Vector2.zero, new Vector2(BarW, 12f)), rounded, c);
        back.type = fill.type = Image.Type.Sliced; back.pixelsPerUnitMultiplier = fill.pixelsPerUnitMultiplier = 3.4f;
        return fill;
    }
    static float Seg(Vector2 p, Vector2 a, Vector2 b) { Vector2 e = b - a, w = p - a; return (w - e * Mathf.Clamp01(Vector2.Dot(w, e) / e.sqrMagnitude)).magnitude; }
    // signed distance to a polygon (for the lightning bolt icon)
    static float Poly(Vector2 p, Vector2[] v)
    {
        float d = float.MaxValue; bool inside = false;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
        {
            Vector2 e = v[i] - v[j], w = p - v[j];
            d = Mathf.Min(d, (w - e * Mathf.Clamp01(Vector2.Dot(w, e) / e.sqrMagnitude)).magnitude);
            if ((v[i].y > p.y) != (v[j].y > p.y) && p.x < e.x * (p.y - v[j].y) / e.y + v[j].x) inside = !inside;
        }
        return inside ? -d : d;
    }

    void Snap()
    {
        if (!showSnapshot || !cam) return;
        if (vr) SetLayer(canvasRT, 5);                     // keep the floating VR HUD out of the photo
        photo.texture = PlayerSnapshot.Get().Take(seenFrom == Vector3.zero ? cam.position + cam.forward * 3f : seenFrom);
    }

    static void SetLayer(Transform t, int layer) { t.gameObject.layer = layer; foreach (Transform c in t) SetLayer(c, layer); }

    void Radar()
    {
        bool alarmed = level == StealthLevel.Detected || engaged;
        range = Mathf.Lerp(range, alarmed ? radarRangeDiscovered : radarRange, 1f - Mathf.Exp(-2.5f * Time.deltaTime));   // map zooms out, markers keep size
        rim.color = Color.Lerp(gold, detected, Mathf.InverseLerp(radarRange, radarRangeDiscovered, range));
        float r = radarSize * .5f, ppm = (r - 4f) / range, edge = r - 10f, y = cam.eulerAngles.y;
        world.localRotation = Quaternion.Euler(0, 0, y);   // forward is always up
        northRT.anchoredPosition = new Vector2(-Mathf.Sin(y * Mathf.Deg2Rad), Mathf.Cos(y * Mathf.Deg2Rad)) * (r - 14f);

        while (blips.Count < golems.Length) blips.Add(new Blip
        {
            zone = Img(Rect("Zone", world, new Vector2(.5f, .5f), Vector2.zero, Vector2.zero), circle, Color.clear),
            cone = Cone(world, 50f, 50f),
            dot = Img(Rect("Golem", world, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 12), circle, Color.white),
        });
        used.Clear();
        for (int i = 0; i < blips.Count; i++)
        {
            var b = blips[i]; var g = i < golems.Length ? golems[i] : null;
            bool on = g && g.isActiveAndEnabled;
            b.zone.gameObject.SetActive(on); b.cone.gameObject.SetActive(on); b.dot.gameObject.SetActive(on);
            if (!on) continue;
            var s = g.CurrentState;
            bool chase = s == GolemGuardAI.State.Chase || s == GolemGuardAI.State.Escort;
            bool hunt = g.IsHunting || s == GolemGuardAI.State.Investigate || s == GolemGuardAI.State.Search;
            Color col = chase ? detected : hunt || s == GolemGuardAI.State.Suspicious ? suspicious : calm;

            // search zone: red around you in a chase, yellow around your last known spot while searching
            Vector3 zc = chase ? cam.position : g.LastKnownPosition;
            bool zone = (chase || hunt) && !used.Exists(u => (u - zc).sqrMagnitude < 9f);
            b.zone.gameObject.SetActive(zone);
            if (zone)
            {
                used.Add(zc);
                float f = .5f + .5f * Mathf.Sin(Time.time * (chase ? 6f : 2.5f));
                b.zone.rectTransform.anchoredPosition = Map(zc, ppm);
                b.zone.rectTransform.sizeDelta = Vector2.one * (chase ? pursuitRadius : searchRadius) * ppm * 2f;
                b.zone.color = new Color(col.r, col.g, col.b, .18f + .14f * f);
            }

            Vector2 p = Map(g.transform.position, ppm); bool far = p.magnitude > edge;
            if (far) p = p.normalized * edge;
            b.cone.gameObject.SetActive(!far);
            b.cone.rectTransform.anchoredPosition = b.dot.rectTransform.anchoredPosition = p;
            b.cone.Set(g.viewAngle, g.viewDistance * ppm);
            b.cone.rectTransform.localRotation = Quaternion.Euler(0, 0, -g.transform.eulerAngles.y);
            b.cone.color = new Color(col.r, col.g, col.b, chase ? .4f : .25f);
            b.dot.color = far ? col * .7f : col;
            b.dot.rectTransform.localScale = Vector3.one * (chase ? 1f + .3f * Mathf.Sin(Time.time * 10f) : 1f);
        }

        while (shardMarks.Count < pickups.Length) { var d = Img(Rect("Shard", world, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 9), null, gold); d.rectTransform.localRotation = Quaternion.Euler(0, 0, 45); shardMarks.Add(d); }
        for (int i = 0; i < shardMarks.Count; i++)
        {
            var sp = i < pickups.Length ? pickups[i] : null; bool on = sp && sp.isActiveAndEnabled;
            shardMarks[i].gameObject.SetActive(on); if (!on) continue;
            Vector2 p = Map(sp.transform.position, ppm); if (p.magnitude > edge) p = p.normalized * edge;
            shardMarks[i].rectTransform.anchoredPosition = p;
            shardMarks[i].color = ShardColor(Array.IndexOf(shardNames, sp.shardName));
        }
    }

    // VR: HUD floats in front, level with the horizon, and only turns once you look away past the deadzone
    void LateUpdate()
    {
        if (!built || !vr || !cam) return;
        float diff = Mathf.DeltaAngle(yaw, cam.eulerAngles.y);
        if (Mathf.Abs(diff) > vrDeadzone || Time.frameCount < 5) yaw = Mathf.LerpAngle(yaw, cam.eulerAngles.y, Time.frameCount < 5 ? 1f : Time.deltaTime * 4f);
        var rot = Quaternion.Euler(0, yaw, 0);
        canvasRT.SetPositionAndRotation(cam.position + rot * Vector3.forward * vrDistance, rot);
    }

    // ---------------------------------------------------------------- helpers
    Objective Find(string id) => objectives.Find(o => o.id == id);
    IEnumerator RemoveLater(Objective o) { yield return new WaitForSeconds(2f); objectives.Remove(o); RefreshObjectives(); }

    void RefreshObjectives()
    {
        if (!built) return;
        var main = objectives.Find(o => !o.optional) ?? (objectives.Count > 0 ? objectives[0] : null);
        objMain.text = main == null ? "" : Format(main);
        var extra = new List<string>();
        foreach (var o in objectives) if (o != main && extra.Count < 2) extra.Add(Format(o));
        objExtra.text = string.Join("\n", extra);
    }
    string Format(Objective o) { var t = (o.optional ? "<i>Optional:</i> " : "") + Markup(o.text); return o.done ? "<alpha=#88><s>" + t + "</s>" : t; }
    string Markup(string s) => Regex.Replace(Regex.Replace(s ?? "", @"\{([^}]*)\}", "<color=#" + ColorUtility.ToHtmlStringRGB(keyColor) + ">$1</color>"),
                                             @"\[([^\]]*)\]", "<color=#" + ColorUtility.ToHtmlStringRGB(dangerColor) + ">$1</color>");

    void RefreshShards()
    {
        if (!built) return;
        for (int i = 0; i < shardPips.Length; i++) shardPips[i].color = shards.Contains(shardNames[i]) ? ShardColor(i) : new Color(1, 1, 1, .15f);
        shardCount.text = "SHARDS<pos=50%>" + shards.Count + " / " + shardNames.Length;
    }
    Color ShardColor(int i) => i >= 0 && i < shardColors.Length ? shardColors[i] : gold;
    Vector2 Map(Vector3 w, float ppm) => new Vector2(w.x - cam.position.x, w.z - cam.position.z) * ppm;

    void Play(AudioClip clip, int chime) { var c = clip ? clip : Chime(chime); if (c) audioSrc.PlayOneShot(c, volume); }
    /// UI sounds for other scripts: 0 new objective, 1 done, 2 shard, 7 click, 8 dialogue blip.
    public static void PlayChime(int i) { var h = Instance; if (h && h.audioSrc) h.Play(null, i); }

    static readonly float[][] Chimes = { new[] { 523f, 659f }, new[] { 659f, 784f, 988f }, new[] { 523f, 659f, 784f, 1047f }, new[] { 392f, 466f }, new[] { 880f, 880f, 1047f }, null, new[] { 587f, 494f, 392f }, new[] { 1319f }, new[] { 784f } };
    static readonly Dictionary<int, AudioClip> chimeCache = new Dictionary<int, AudioClip>();
    static AudioClip Chime(int i)   // soft bell notes, so there's sound before you add real clips
    {
        if (chimeCache.TryGetValue(i, out var c) && c) return c;
        var f = Chimes[i]; if (f == null) return null;
        const int rate = 44100; int per = rate / 8, total = per * f.Length + rate / 2;
        var data = new float[total];
        for (int n = 0; n < f.Length; n++)
            for (int s = 0; n * per + s < total; s++) { float t = s / (float)rate; data[n * per + s] += Mathf.Sin(2 * Mathf.PI * f[n] * t) * Mathf.Min(1f, t * 300f) * Mathf.Exp(-t * 6f) * .25f; }
        c = AudioClip.Create("chime" + i, total, 1, rate, false); c.SetData(data, 0);
        return chimeCache[i] = c;
    }

    static RectTransform Rect(string name, Transform parent, Vector2 corner, Vector2 pos, Vector2 size)
    {
        var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rt.SetParent(parent, false); rt.anchorMin = rt.anchorMax = rt.pivot = corner; rt.anchoredPosition = pos; rt.sizeDelta = size;
        return rt;
    }
    static void Fill(RectTransform rt, float top, float h) { rt.anchorMin = new Vector2(0, 1); rt.anchorMax = Vector2.one; rt.pivot = new Vector2(.5f, 1); rt.anchoredPosition = new Vector2(0, -top); rt.sizeDelta = new Vector2(0, h); }
    Image Img(Component on, Sprite s, Color c) { var i = on.gameObject.AddComponent<Image>(); i.sprite = s; i.color = c; i.raycastTarget = false; if (overlay) i.material = overlay; return i; }
    TextMeshProUGUI Text(Transform parent, string s, float size, Color c, FontStyles style = FontStyles.Normal)
    {
        var t = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        t.transform.SetParent(parent, false); if (font) t.font = font;
        t.text = s; t.fontSize = size; t.color = c; t.fontStyle = style; t.raycastTarget = false;
        if (overlay) t.fontMaterial.SetFloat("unity_GUIZTestMode", 8f);   // VR: draw over walls
        return t;
    }
    Image[] Pips(Transform parent, int count, float x)
    {
        var a = new Image[count];
        for (int i = 0; i < count; i++) { var r = Rect("Pip", parent, new Vector2(0, .5f), new Vector2(x + i * 22f, 0), Vector2.one * 11f); r.pivot = new Vector2(.5f, .5f); r.localRotation = Quaternion.Euler(0, 0, 45); a[i] = Img(r, null, Color.white); }
        return a;
    }
    RadarCone Cone(Transform parent, float half, float radius)
    {
        var go = new GameObject("Cone", typeof(RectTransform)); go.transform.SetParent(parent, false);
        var c = go.AddComponent<RadarCone>(); c.raycastTarget = false; c.Set(half, radius); if (overlay) c.material = overlay;
        return c;
    }
    static Sprite Sdf(int size, Func<Vector2, float> f, int border)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            px[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(.5f - f(new Vector2(x + .5f - size / 2f, y + .5f - size / 2f))));
        tex.SetPixels32(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100f, 0, SpriteMeshType.FullRect, Vector4.one * border);
    }
}