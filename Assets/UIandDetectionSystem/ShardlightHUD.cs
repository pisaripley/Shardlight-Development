using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

public enum StealthLevel { Hidden, Suspicious, Detected }

/// Shardlight HUD (VR + desktop). Top-left hints/shards/alert, bottom objectives, bottom-left radar,
/// top-right DISCOVERED. Also watches the golems (stealth state + sounds). One per scene.
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
    [Header("Discovered")]
    public float blinkRate = 2f, dimTime = 15f, fadeOutTime = 1.5f;
    [Range(0, 1)] public float dimFloor = .2f;
    [Header("Radar")]
    public float radarRange = 30f, radarRangeDiscovered = 55f, radarSize = 240f, pursuitRadius = 7f, searchRadius = 11f;
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
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset0() { Instance = null; shards.Clear(); }

    bool built, vr, engaged, wasDetected, wasCaught, searching;
    StealthLevel level;
    float intensity, alpha, pop, yaw, range, golemScan, hintUntil;
    int lastWanted;
    Transform cam;
    RectTransform canvasRT, world, northRT;
    TextMeshProUGUI hint, objMain, objExtra, disc, discSub, shardCount;
    GameObject alertRow;
    Image[] shardPips, alertPips;
    Image rim;
    AudioSource audioSrc;
    Sprite circle, ring, rounded;
    Material overlay;
    RectTransform vrNotice;
    CanvasGroup noticeFade;
    TextMeshProUGUI noticeText;
    float noticeUntil;
    TextMeshProUGUI vrStatusText, vrWatchHelp;
    RectTransform vrStatus;
    public static string WristSummary
    {
        get
        {
            var h = Instance;
            string state = WantedSystem.Level > 0 ? "ALERT " + WantedSystem.Level : "CLEAR";
            string summary = "SHARDS " + shards.Count + " / " + (h ? h.shardNames.Length : 3) + "   |   " + state;
            var objective = h ? h.objectives.Find(o => !o.done && !o.optional) : null;
            return objective == null ? summary : summary + "\n" + Regex.Replace(objective.text ?? "", @"[{}\[\]]", "");
        }
    }
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
    public static void ShowHint(string text, float seconds = 5f) { var h = Instance; if (!h || !h.built) return; h.hint.text = h.Markup(text); h.hintUntil = Time.time + seconds; if (h.vr) h.ShowVRNotice(text, seconds); }
    public static void ShowMessage(string text, float seconds = 3f) => ShowHint(text, seconds);
    public static bool HasShard(string name) => shards.Contains(name);
    /// New game: forget collected shards.
    public static void ResetProgress() => shards.Clear();
    public static int ShardsCollected => shards.Count;
    public static void CollectShard(string name, AudioClip clip = null)
    {
        if (!shards.Contains(name)) shards.Add(name);
        var h = Instance; if (!h) return;
        h.RefreshShards(); h.Play(clip ? clip : h.shardClip, 2);
        ShowHint("{Shard of " + name + "} recovered", 3.5f);
    }
    public static bool IsPlayer(Component c) { for (var t = c.transform; t; t = t.parent) if (t.CompareTag("Player")) return true; return false; }

    // ---------------------------------------------------------------- setup
    void Awake() { if (Instance && Instance != this) { enabled = false; return; } Instance = this; audioSrc = gameObject.AddComponent<AudioSource>(); range = radarRange; }
    void OnDestroy() { if (Instance == this) Instance = null; }

    IEnumerator Start()
    {
        yield return null;                                   // let XR start first
        vr = forceVR || Project.Forest.PlayerRuntimeMode.VRActive;
        cam = Camera.main ? Camera.main.transform : null;
        circle = Sdf(64, p => p.magnitude - 30f, 0);
        ring = Sdf(128, p => Mathf.Abs(p.magnitude - 60f) - 1.5f, 0);
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

        // top left: hint + shards + alert
        var tl = Rect("Hints", canvasRT, new Vector2(0, 1), new Vector2(m, -m), new Vector2(400, 10));
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
        var tr = Rect("Discovered", canvasRT, new Vector2(1, 1), new Vector2(-m, -m), new Vector2(560, 100));
        disc = Text(tr, "DISCOVERED", 50 * k, detected, FontStyles.Bold); disc.alignment = TextAlignmentOptions.TopRight; disc.characterSpacing = 14; disc.outlineWidth = .15f; Fill(disc.rectTransform, 0, 64); disc.rectTransform.pivot = Vector2.one;
        discSub = Text(tr, "", 18 * k, textColor * .85f, FontStyles.Italic); discSub.alignment = TextAlignmentOptions.TopRight; Fill(discSub.rectTransform, 66, 30);
        disc.alpha = discSub.alpha = 0;

        // bottom left: radar (disk masks cones/zones to a circle)
        var frame = Rect("Radar", canvasRT, Vector2.zero, new Vector2(m, m), Vector2.one * radarSize);
        var disk = Rect("Disk", frame, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * radarSize); disk.pivot = new Vector2(.5f, .5f);
        Img(disk, circle, panel); disk.gameObject.AddComponent<Mask>();
        world = Rect("World", disk, new Vector2(.5f, .5f), Vector2.zero, Vector2.zero);
        var you = Cone(disk, 32f, 30f); you.color = new Color(gold.r, gold.g, gold.b, .45f);
        Img(Rect("You", disk, new Vector2(.5f, .5f), Vector2.zero, Vector2.one * 10), circle, gold).rectTransform.pivot = new Vector2(.5f, .5f);
        rim = Img(frame, ring, gold);
        var n = Text(frame, "N", 16 * k, gold, FontStyles.Bold); n.alignment = TextAlignmentOptions.Center; northRT = n.rectTransform; northRT.anchorMin = northRT.anchorMax = new Vector2(.5f, .5f); northRT.sizeDelta = new Vector2(24, 24);

        built = true;
        if (vr)
        {
            // Persistent information lives on the wrist. Do not render the desktop HUD in the headset.
            canvasRT.gameObject.SetActive(false);
            var notice = new GameObject("VR brief notification", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
            notice.transform.SetParent(transform, false);
            vrNotice = (RectTransform)notice.transform;
            vrNotice.sizeDelta = new Vector2(600, 90);
            notice.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            notice.GetComponent<Canvas>().worldCamera = Camera.main;
            noticeFade = notice.GetComponent<CanvasGroup>(); noticeFade.alpha = 0;
            // Physical world text uses normal depth testing instead of drawing through the world.
            var previousOverlay = overlay; overlay = null;
            noticeText = Text(vrNotice, "", 23, textColor);
            overlay = previousOverlay;
            noticeText.alignment = TextAlignmentOptions.Center;
            noticeText.enableAutoSizing = true; noticeText.fontSizeMin = 16;
            noticeText.rectTransform.anchorMin = Vector2.zero; noticeText.rectTransform.anchorMax = Vector2.one;
            noticeText.rectTransform.offsetMin = noticeText.rectTransform.offsetMax = Vector2.zero;
            var status = new GameObject("VR status and objective", typeof(RectTransform), typeof(Canvas));
            status.transform.SetParent(cam, false);
            vrStatus = (RectTransform)status.transform;
            vrStatus.sizeDelta = new Vector2(620, 140);
            vrStatus.localScale = Vector3.one * .00065f;
            vrStatus.localPosition = new Vector3(0, -.24f, .8f);
            vrStatus.localRotation = Quaternion.identity;
            status.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            status.GetComponent<Canvas>().worldCamera = Camera.main;
            vrStatusText = Text(vrStatus, "", 19, textColor);
            Fill(vrStatusText.rectTransform, 0, 84);
            vrStatusText.alignment = TextAlignmentOptions.Center;
            vrStatusText.enableAutoSizing = true; vrStatusText.fontSizeMin = 15;
            vrWatchHelp = Text(vrStatus, "Raise your left hand to view the watch", 15, gold);
            Fill(vrWatchHelp.rectTransform, 95, 36);
            vrWatchHelp.alignment = TextAlignmentOptions.Center;
        }
        RefreshShards(); RefreshObjectives(); ShowHint(startHint, 8f);
    }

    // ---------------------------------------------------------------- per frame
    void Update()
    {
        if (!built) return;
        if (!cam && Camera.main) cam = Camera.main.transform;
        if ((golemScan -= Time.deltaTime) <= 0f) { golemScan = 1f; golems = FindObjectsByType<GolemGuardAI>(FindObjectsSortMode.None); pickups = FindObjectsByType<ShardPickup>(FindObjectsSortMode.None); }
        Watch();
        if (vr)
        {
            if (vrStatus)
            {
                vrStatus.gameObject.SetActive(!ShardlightMenus.IsOpen);
                vrStatusText.text = WristSummary;
                vrWatchHelp.text = WristInventory.Instance ? WristInventory.Instance.TrackingStatus : "Watch starting...";
            }
            if (noticeFade)
            {
                float remaining = noticeUntil - Time.unscaledTime;
                noticeFade.alpha = ShardlightMenus.IsOpen ? 0 : Mathf.Clamp01(remaining / .4f);
            }
            return;
        }
        hint.gameObject.SetActive(Time.time < hintUntil);
        int w = WantedSystem.Level;
        alertRow.SetActive(w > 0);
        for (int i = 0; i < alertPips.Length; i++) alertPips[i].color = i < w ? detected : new Color(1, 1, 1, .15f);
        Discovered();
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
            if (s == GolemGuardAI.State.Chase || s == GolemGuardAI.State.Escort) l = StealthLevel.Detected;
            else if ((s == GolemGuardAI.State.Suspicious || g.Suspicion > .15f) && l == StealthLevel.Hidden) l = StealthLevel.Suspicious;
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

    // blinks while suspected, pops in bright when spotted, dims while they search, fades when they stop
    void Discovered()
    {
        float dt = Time.deltaTime; bool det = level == StealthLevel.Detected;
        if (det) { if (!engaged || !wasDetected) pop = 1f; engaged = true; intensity = 1f; }
        else if (engaged)
        {
            intensity = searching ? Mathf.MoveTowards(intensity, dimFloor, dt * (1 - dimFloor) / dimTime) : intensity - dt / fadeOutTime;
            if (intensity <= 0f) { engaged = false; intensity = 0f; }
        }
        wasDetected = det;
        pop = Mathf.MoveTowards(pop, 0f, dt * 2.5f);
        float wave = .5f + .5f * Mathf.Sin(Time.time * (vr ? Mathf.Min(blinkRate, 1.2f) : blinkRate) * Mathf.PI * 2f);   // soft pulse, gentler in VR
        float target = det ? 1f : engaged ? intensity : level == StealthLevel.Suspicious ? Mathf.Lerp(.15f, 1f, wave) : 0f;
        Color c = det ? Color.Lerp(detected, Color.white, pop * .6f) : engaged ? Color.Lerp(detected * .45f, detected, intensity) : suspicious;
        alpha = det ? 1f : Mathf.MoveTowards(alpha, target, dt * 6f);
        c.a = alpha; disc.color = c;
        disc.rectTransform.localScale = Vector3.one * (1f + .35f * pop * pop);
        string sub = det ? "" : engaged ? "They're searching for you" : level == StealthLevel.Suspicious ? "Something sensed you..." : discSub.text;
        if (discSub.text != sub) discSub.text = sub;
        discSub.alpha = alpha * .85f;
    }

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

    // Notices stay at the world position where they appeared, then fade; they do not chase head turns.
    void ShowVRNotice(string text, float seconds)
    {
        if (!vrNotice || !cam) return;
        float scale = Mathf.Max(.001f, Mathf.Abs(cam.lossyScale.x));
        vrNotice.localScale = Vector3.one * (.00065f * scale);
        vrNotice.SetPositionAndRotation(cam.position + cam.forward * (.8f * scale) - cam.up * (.2f * scale), cam.rotation);
        noticeText.text = Markup(text);
        noticeUntil = Time.unscaledTime + Mathf.Min(seconds, 3.5f);
        noticeFade.alpha = 1;
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
