using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

/// Anything the player can use with the Interact button (E / gamepad X / right trigger).
public interface IInteractable { string Prompt { get; } void Interact(); }

/// Items the player is carrying (shards, scrolls, keys...). Shown in the inventory screen.
public static class Inventory
{
    public class Item { public string id, name, description; public Sprite icon; }
    public static readonly List<Item> Items = new List<Item>();
    static string selectedId;
    public static event Action<Item> SelectionChanged;
    public static int SelectedIndex
    {
        get
        {
            if (Items.Count == 0) return -1;
            int index = Items.FindIndex(i => i.id == selectedId);
            return index >= 0 ? index : 0;
        }
    }
    public static Item SelectedItem => SelectedIndex < 0 ? null : Items[SelectedIndex];
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void Reset0() { Items.Clear(); selectedId = null; SelectionChanged = null; iconCache.Clear(); }
    public static void Clear() { Items.Clear(); selectedId = null; SelectionChanged?.Invoke(null); }
    public static bool Has(string id) => Items.Exists(i => i.id == id);
    public static void Select(int index)
    {
        if (index < 0 || index >= Items.Count || selectedId == Items[index].id) return;
        selectedId = Items[index].id;
        SelectionChanged?.Invoke(Items[index]);
    }
    public static void Cycle(int direction)
    {
        if (Items.Count == 0) return;
        Select(((SelectedIndex + direction) % Items.Count + Items.Count) % Items.Count);
    }
    static readonly Dictionary<string, Sprite> iconCache = new Dictionary<string, Sprite>();
    public static Sprite IconFor(string id, string name)
    {
        string text = ((id ?? "") + " " + (name ?? "")).ToLowerInvariant();
        string key = "rune";
        foreach (var candidate in new[] { "sword", "bow", "bomb", "torch", "key", "potion" })
            if (text.Contains(candidate)) { key = candidate; break; }
        if (!iconCache.TryGetValue(key, out var sprite) || !sprite)
        {
            sprite = Resources.Load<Sprite>("InventoryIcons/" + key);
            iconCache[key] = sprite;
        }
        return sprite;
    }
    public static void Add(string id, string name, string description, bool announce = true, Sprite icon = null)
    {
        if (string.IsNullOrWhiteSpace(id) || Has(id)) return;
        Items.Add(new Item { id = id, name = string.IsNullOrWhiteSpace(name) ? id : name, description = description ?? "", icon = icon ? icon : IconFor(id, name) });
        if (Items.Count == 1) Select(0);
        if (announce) ShardlightHUD.ShowHint("{" + name + "} added to your satchel", 3f);
    }
}
/// Shardlight menus for VR + desktop: main menu, pause, settings, inventory, NPC dialogue, and the Interact prompt.
/// One per scene. Tick "Is Main Menu" in your title scene. Esc/P or left Menu button = pause. Desktop Tab/I = quick selector; VR = look at left wrist.
[DefaultExecutionOrder(-40)]
public class ShardlightMenus : MonoBehaviour
{
    enum Screen { None, Main, Pause, Settings, Dialogue }

    [Header("Scenes")]
    public bool isMainMenu;
    public string firstScene = "Forest", mainMenuScene = "MainMenu";
    [Header("Look")]
    public TMP_FontAsset font;
    public Color gold = new Color(1f, .82f, .42f), panel = new Color(.08f, .07f, .09f, .94f), textColor = new Color(.97f, .94f, .87f);
    [Header("VR")]
    public float vrDistance = 1.3f, vrScale = .0011f;
    [Header("Inventory testing")]
    [Tooltip("Add example inventory items in Editor Play Mode. Ignored in builds.")]
    public bool exampleInventoryInEditor = true;
    [Header("Interact")]
    public float interactRange = 2.5f;

    public static ShardlightMenus Instance { get; private set; }
    public static bool IsOpen => Instance && Instance.screen != Screen.None;

    Screen screen, back;
    static Material overlay;   // VR: menus draw over walls, like the HUD
    bool vr;
    Canvas canvas;
    RectTransform root, page;
    TextMeshProUGUI prompt, dlgName, dlgText;
    RectTransform dlgChoices;
    InputAction pauseIn, invIn, interactIn;
    Behaviour desktopPlayer;
    WristInventory wristInventory;
    IInteractable focus;
    NPCDialogue npc; int line; float typed;

    // ---------------------------------------------------------------- setup
    void Awake() { if (Instance && Instance != this) { enabled = false; return; } Instance = this; }
    void OnDestroy() { pauseIn?.Dispose(); invIn?.Dispose(); interactIn?.Dispose(); if (Instance == this) { Instance = null; Time.timeScale = 1f; } }

    void Start()
    {
        vr = Project.Forest.PlayerRuntimeMode.VRActive;
        pauseIn = Action("<Keyboard>/escape", "<Keyboard>/p", "<Gamepad>/start", "<XRController>{LeftHand}/menuButton");
        invIn = Action("<Keyboard>/tab", "<Keyboard>/i", "<Gamepad>/select", "<XRController>{LeftHand}/secondaryButton");
        interactIn = Action("<Keyboard>/e", "<Gamepad>/buttonWest", "<XRController>{RightHand}/triggerButton");
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            if (b.GetType().Name == "DesktopForestPlayer") desktopPlayer = b;

        var events = FindFirstObjectByType<EventSystem>();
        if (!events) events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        if (vr)
        {
            foreach (var module in events.GetComponents<InputSystemUIInputModule>()) module.enabled = false;
            if (!events.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>())
                events.gameObject.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule>();
        }
        else if (!events.GetComponent<InputSystemUIInputModule>())
            events.gameObject.AddComponent<InputSystemUIInputModule>();

        var go = new GameObject("Menu Canvas", typeof(RectTransform), typeof(Canvas));
        go.transform.SetParent(transform, false);
        canvas = go.GetComponent<Canvas>();
        root = (RectTransform)go.transform;
        if (vr)
        {
            canvas.renderMode = RenderMode.WorldSpace;
            overlay = new Material(Shader.Find("UI/Default")); overlay.SetFloat("unity_GUIZTestMode", 8f);
            root.sizeDelta = new Vector2(1400, 900); root.localScale = Vector3.one * vrScale * (Camera.main ? Mathf.Abs(Camera.main.transform.lossyScale.x) : 1f);
            var tracked = Type.GetType("UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster, Unity.XR.Interaction.Toolkit");
            if (tracked != null) go.AddComponent(tracked);          // lets controller rays click the buttons
        }
        else
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 600;
            var s = go.AddComponent<CanvasScaler>(); s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; s.referenceResolution = new Vector2(1920, 1080); s.matchWidthOrHeight = .5f;
            go.AddComponent<GraphicRaycaster>();
            prompt = Label(root, "", 22, textColor, TextAlignmentOptions.Center);   // "[E] Talk to Tomas" under the crosshair
            Place(prompt.rectTransform, new Vector2(0, -70), new Vector2(700, 40));
            prompt.outlineWidth = .2f;
        }
        if (!isMainMenu)
        {
            wristInventory = GetComponent<WristInventory>();
            if (!wristInventory) wristInventory = gameObject.AddComponent<WristInventory>();
            wristInventory.exampleItemsInEditor = exampleInventoryInEditor;
            if (!FindFirstObjectByType<Project.HandTracking.LeftTeleportAim>())
            {
                var teleport = gameObject.AddComponent<Project.HandTracking.LeftTeleportAim>();
                teleport.controllerTeleport = false; // Existing XRI controller teleport remains in charge.
            }
        }
        ApplySettings();
        Show(isMainMenu ? Screen.Main : Screen.None);
    }

    static InputAction Action(params string[] bindings)
    {
        var a = new InputAction(type: InputActionType.Button);
        foreach (var b in bindings) a.AddBinding(b);
        a.Enable();
        return a;
    }

    // ---------------------------------------------------------------- per frame
    void Update()
    {
        if (pauseIn.WasPressedThisFrame())
        {
            if (!vr && wristInventory && wristInventory.IsOpen) { wristInventory.ToggleDesktop(); return; }
            if (screen == Screen.None) Show(Screen.Pause);
            else if (screen == Screen.Settings) Show(back);
            else if (screen == Screen.Pause) Show(Screen.None);
        }
        else if (invIn.WasPressedThisFrame() && !vr && wristInventory && screen == Screen.None)
            wristInventory.ToggleDesktop();

        if (screen == Screen.Dialogue) { Dialogue(); return; }
        if (screen != Screen.None) return;

        // what's the player looking at / standing next to?
        var cam = Camera.main ? Camera.main.transform : null;
        IInteractable found = null;
        if (cam)
        {
            if (!vr && Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, interactRange, ~0, QueryTriggerInteraction.Collide))
                found = hit.collider.GetComponentInParent<IInteractable>();
            else if (vr)
            {
                float best = float.MaxValue;
                foreach (var c in Physics.OverlapSphere(cam.position, interactRange, ~0, QueryTriggerInteraction.Collide))
                {
                    var i = c.GetComponentInParent<IInteractable>(); if (i == null) continue;
                    Vector3 to = c.transform.position - cam.position;
                    if (Vector3.Angle(cam.forward, to) < 50f && to.sqrMagnitude < best) { best = to.sqrMagnitude; found = i; }
                }
            }
        }
        if (found != focus && found != null && vr) ShardlightHUD.ShowHint("Pull the {trigger} to " + found.Prompt.ToLower(), 2.5f);
        focus = found;
        if (prompt) prompt.text = focus != null ? "<color=#" + ColorUtility.ToHtmlStringRGB(gold) + ">[E]</color>  " + focus.Prompt : "";
        if (focus != null && interactIn.WasPressedThisFrame()) { ShardlightHUD.PlayChime(7); focus.Interact(); }
    }

    // ---------------------------------------------------------------- screens
    void Show(Screen s)
    {
        if (s == Screen.Settings) back = screen == Screen.Settings ? back : screen;
        screen = s;
        if (page) Destroy(page.gameObject);
        bool open = s != Screen.None;
        Time.timeScale = open ? 0f : 1f;                                       // world pauses behind menus and dialogue
        if (desktopPlayer) desktopPlayer.enabled = !open;                      // frees the mouse for clicking
        if (prompt) prompt.gameObject.SetActive(!open);
        if (!open) return;
        if (vr && Camera.main)                                                 // VR: menu appears in front of you and stays put (comfortable)
        {
            var cam = Camera.main.transform;
            var flat = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (flat.sqrMagnitude < .01f) flat = Vector3.forward;
            float scale = Mathf.Max(.001f, Mathf.Abs(cam.lossyScale.x));
            root.SetPositionAndRotation(cam.position + flat * vrDistance * scale + Vector3.down * (.1f * scale), Quaternion.LookRotation(flat));
        }

        page = Rect("Page", root);
        page.anchorMin = Vector2.zero; page.anchorMax = Vector2.one; page.offsetMin = page.offsetMax = Vector2.zero;
        if (s != Screen.Dialogue && !vr) Img(page, new Color(0, 0, 0, .55f));  // dim the game behind

        switch (s)
        {
            case Screen.Main:
                var m = Panel(new Vector2(0, 0), new Vector2(560, 520), "SHARDLIGHT", "Bring back the light of Veyra");
                Button(m, "Begin", () => { Time.timeScale = 1f; Inventory.Clear(); ShardlightHUD.ResetProgress(); WantedSystem.Clear(); SceneManager.LoadScene(firstScene); });   // fresh game
                Button(m, "Settings", () => Show(Screen.Settings));
                Button(m, "Quit", Quit);
                break;
            case Screen.Pause:
                var p = Panel(Vector2.zero, new Vector2(520, 600), "PAUSED", "The ruins wait for you");
                Button(p, "Resume", () => Show(Screen.None));
                Button(p, "Satchel (Inventory)", () => { Show(Screen.None); if (vr) ShardlightHUD.ShowHint("Hold your {left wrist} steady and look at the watch. Touch it with your right fingertip and slide to browse."); else wristInventory.ToggleDesktop(); });
                Button(p, "Settings", () => Show(Screen.Settings));
                Button(p, "Restart Area", () => { Time.timeScale = 1f; SceneManager.LoadScene(SceneManager.GetActiveScene().name); });
                Button(p, "Main Menu", () => { Time.timeScale = 1f; SceneManager.LoadScene(mainMenuScene); });
                break;
            case Screen.Settings:
                var st = Panel(Vector2.zero, new Vector2(620, 640), "SETTINGS", "Saved automatically");
                Slider(st, "Volume", 0f, 1f, PlayerPrefs.GetFloat("vol", 1f), v => { PlayerPrefs.SetFloat("vol", v); ApplySettings(); });
                Slider(st, "Mouse sensitivity", .03f, .3f, PlayerPrefs.GetFloat("sens", .12f), v => { PlayerPrefs.SetFloat("sens", v); ApplySettings(); });
                Toggle(st, "Movement", "Smooth", "Teleport", "smoothMove", 1);
                Toggle(st, "Turning", "Smooth", "Snap", "smoothTurn", 0);
                Button(st, "Back", () => Show(back));
                break;
            case Screen.Dialogue:
                var d = Rect("Dialogue", page);
                d.anchorMin = d.anchorMax = d.pivot = new Vector2(.5f, 0f); d.anchoredPosition = new Vector2(0, vr ? 140 : 170); d.sizeDelta = new Vector2(1100, 260);
                Img(d, panel); Img(Rect("Edge", d, new Vector2(0, 128), new Vector2(1100, 3)), gold);
                dlgName = Label(d, npc.npcName.ToUpper(), 22, gold, TextAlignmentOptions.TopLeft, FontStyles.Bold); Place(dlgName.rectTransform, new Vector2(0, 100), new Vector2(1040, 36));
                dlgText = Label(d, "", 26, textColor, TextAlignmentOptions.TopLeft); Place(dlgText.rectTransform, new Vector2(0, 20), new Vector2(1040, 110));
                dlgChoices = Rect("Choices", d, new Vector2(0, -85), new Vector2(1040, 70));
                var h = dlgChoices.gameObject.AddComponent<HorizontalLayoutGroup>(); h.spacing = 12; h.childForceExpandWidth = true; h.childControlWidth = h.childControlHeight = true;
                ShowLine(line);
                break;
        }
        ShardlightHUD.PlayChime(7);
    }

    // ---------------------------------------------------------------- dialogue
    public static void StartDialogue(NPCDialogue who)
    {
        var m = Instance; if (!m || who.lines == null || who.lines.Length == 0) return;
        m.npc = who; m.line = 0; m.Show(Screen.Dialogue);
    }

    void ShowLine(int i)
    {
        if (i < 0 || i >= npc.lines.Length) { var done = npc; Show(Screen.None); done.onFinished.Invoke(); return; }
        line = i; typed = 0f;
        var l = npc.lines[i];
        if (!string.IsNullOrEmpty(l.completeObjectiveId)) ShardlightHUD.CompleteObjective(l.completeObjectiveId);
        if (!string.IsNullOrEmpty(l.addObjectiveId)) ShardlightHUD.AddObjective(l.addObjectiveId, l.objectiveText);
        if (!string.IsNullOrEmpty(l.giveItemId)) Inventory.Add(l.giveItemId, l.giveItemName, l.giveItemDescription);
        foreach (Transform c in dlgChoices) Destroy(c.gameObject);
        if (l.choices != null && l.choices.Length > 0)
            for (int c = 0; c < l.choices.Length; c++) { var ch = l.choices[c]; Button(dlgChoices, (c + 1) + ". " + ch.text, () => ShowLine(ch.goTo)); }
        else Button(dlgChoices, vr ? "Continue" : "[E] Continue", Advance);
        ShardlightHUD.PlayChime(8);
    }

    void Advance()
    {
        var l = npc.lines[line];
        if (dlgText.maxVisibleCharacters < l.text.Length) { typed = 9999f; return; }   // first press finishes the line
        if (l.choices == null || l.choices.Length == 0) ShowLine(l.next >= 0 ? l.next : l.next == -1 ? -1 : line + 1);
    }

    void Dialogue()
    {
        var l = npc.lines[line];
        dlgText.text = l.text;
        typed += Time.unscaledDeltaTime * 45f;                                 // typewriter
        dlgText.maxVisibleCharacters = Mathf.Min(l.text.Length, (int)typed);
        bool hasChoices = l.choices != null && l.choices.Length > 0;
        if (!vr && interactIn.WasPressedThisFrame() && (!hasChoices || dlgText.maxVisibleCharacters < l.text.Length)) Advance();
        var kb = Keyboard.current;
        if (hasChoices && kb != null)
            for (int c = 0; c < l.choices.Length && c < 3; c++)
                if (kb[(Key)((int)Key.Digit1 + c)].wasPressedThisFrame) { ShowLine(l.choices[c].goTo); return; }
    }

    // ---------------------------------------------------------------- settings
    void ApplySettings()
    {
        AudioListener.volume = PlayerPrefs.GetFloat("vol", 1f);
        if (desktopPlayer) desktopPlayer.GetType().GetField("mouseSensitivity")?.SetValue(desktopPlayer, PlayerPrefs.GetFloat("sens", .12f));
        foreach (var b in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (b.GetType().Name == "ControllerInputActionManager")              // XRI comfort options
            {
                b.GetType().GetProperty("smoothMotionEnabled")?.SetValue(b, PlayerPrefs.GetInt("smoothMove", 1) == 1);
                b.GetType().GetProperty("smoothTurnEnabled")?.SetValue(b, PlayerPrefs.GetInt("smoothTurn", 0) == 1);
            }
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ---------------------------------------------------------------- UI builders
    RectTransform Panel(Vector2 pos, Vector2 size, string title, string subtitle)
    {
        var p = Rect(title, page, pos, size);
        Img(p, panel); Img(Rect("Edge", p, new Vector2(0, size.y / 2f - 2f), new Vector2(size.x, 3)), gold);
        var v = p.gameObject.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(48, 48, 36, 36); v.spacing = 14; v.childControlWidth = v.childControlHeight = true; v.childForceExpandHeight = false;
        Label(p, title, 44, gold, TextAlignmentOptions.Center, FontStyles.Bold).characterSpacing = 12;
        Label(p, subtitle, 18, textColor * .7f, TextAlignmentOptions.Center, FontStyles.Italic);
        return p;
    }

    Button Button(Transform parent, string text, UnityAction onClick)
    {
        var r = Rect(text, parent, Vector2.zero, new Vector2(300, 60));
        r.gameObject.AddComponent<LayoutElement>().preferredHeight = 60;
        var img = Img(r, Color.white);
        var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = img;
        var cb = b.colors;                                                     // hover/press glow gold = visual feedback
        cb.normalColor = new Color(1, 1, 1, .07f); cb.highlightedColor = cb.selectedColor = new Color(gold.r, gold.g, gold.b, .35f);
        cb.pressedColor = new Color(gold.r, gold.g, gold.b, .65f); cb.fadeDuration = .08f; b.colors = cb;
        b.onClick.AddListener(() => ShardlightHUD.PlayChime(7));               // click sound = feedback
        b.onClick.AddListener(onClick);
        var t = Label(r, text, 24, textColor, TextAlignmentOptions.Center); Stretch(t.rectTransform);
        return b;
    }

    void Slider(Transform parent, string label, float min, float max, float value, UnityAction<float> onChange)
    {
        Label(parent, label, 20, textColor * .85f, TextAlignmentOptions.Left);
        var r = Rect(label + " Slider", parent, Vector2.zero, new Vector2(400, 34));
        r.gameObject.AddComponent<LayoutElement>().preferredHeight = 34;
        var bg = Img(Rect("Track", r, Vector2.zero, new Vector2(0, 8)), new Color(1, 1, 1, .15f)); bg.rectTransform.anchorMin = new Vector2(0, .5f); bg.rectTransform.anchorMax = new Vector2(1, .5f);
        var fillArea = Rect("Fill Area", r); fillArea.anchorMin = new Vector2(0, .5f); fillArea.anchorMax = new Vector2(1, .5f); fillArea.sizeDelta = new Vector2(0, 8);
        var fill = Img(Rect("Fill", fillArea), gold); fill.rectTransform.sizeDelta = Vector2.zero;
        var handleArea = Rect("Handle Area", r); handleArea.anchorMin = Vector2.zero; handleArea.anchorMax = Vector2.one; handleArea.sizeDelta = new Vector2(-28, 0);
        var handle = Img(Rect("Handle", handleArea, Vector2.zero, new Vector2(28, 28)), textColor);
        var s = r.gameObject.AddComponent<Slider>();
        s.fillRect = fill.rectTransform; s.handleRect = handle.rectTransform; s.targetGraphic = handle;
        s.minValue = min; s.maxValue = max; s.value = value;
        s.onValueChanged.AddListener(onChange);
    }

    void Toggle(Transform parent, string label, string on, string off, string key, int def)
    {
        Button b = null;
        b = Button(parent, label + ":  " + (PlayerPrefs.GetInt(key, def) == 1 ? on : off), () =>
        {
            PlayerPrefs.SetInt(key, 1 - PlayerPrefs.GetInt(key, def)); ApplySettings();
            b.GetComponentInChildren<TextMeshProUGUI>().text = label + ":  " + (PlayerPrefs.GetInt(key, def) == 1 ? on : off);
        });
    }

    static RectTransform Rect(string name, Transform parent, Vector2 pos = default, Vector2 size = default)
    {
        var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        r.SetParent(parent, false); r.anchoredPosition = pos; r.sizeDelta = size;
        return r;
    }
    static void Place(RectTransform r, Vector2 pos, Vector2 size) { r.anchorMin = r.anchorMax = new Vector2(.5f, .5f); r.anchoredPosition = pos; r.sizeDelta = size; }
    static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
    static Image Img(Component on, Color c) { var i = on.gameObject.AddComponent<Image>(); i.color = c; if (overlay) i.material = overlay; return i; }
    TextMeshProUGUI Label(Transform parent, string s, float size, Color c, TextAlignmentOptions align, FontStyles style = FontStyles.Normal)
    {
        var t = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
        t.transform.SetParent(parent, false); if (font) t.font = font;
        t.text = s; t.fontSize = size * (vr ? 1.15f : 1f); t.color = c; t.alignment = align; t.fontStyle = style; t.raycastTarget = false;
        if (overlay) t.fontMaterial.SetFloat("unity_GUIZTestMode", 8f);
        return t;
    }
}
