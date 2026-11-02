using System;
using System.Collections.Generic;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

/// Horizontal quick selector. Added by ShardlightMenus in gameplay scenes.
/// VR: look at the back of the left wrist; look at the sideways watch, then right pinch and move to scroll.
/// Desktop: Tab toggles; mouse wheel selects. Selection does not consume/use items.
public sealed class WristInventory : MonoBehaviour
{
    [Header("Wrist placement (metres)")]
    public float wristLift = .012f;
    public float wristScale = .00045f;
    public float gazeAngle = 16f;
    public float dwellSeconds = .65f;
    public float hideDelay = .45f;
    [Header("Watch interaction")]
    public float pinchStart = .025f;
    public float pinchRelease = .04f;
    public float dragStep = .065f;
    public float vrInventoryRotation = 90f;
    [Header("Carousel")]
    [Min(.05f)] public float slideSeconds = .16f;

    [Tooltip("Editor Play Mode only; controlled by ShardlightMenus.")]
    public bool exampleItemsInEditor = true;

    public static WristInventory Instance { get; private set; }
    public bool IsOpen => root && root.gameObject.activeSelf && selector && selector.gameObject.activeSelf;
    float TrackingScale => origin && origin.CameraFloorOffsetObject ? Mathf.Max(.001f, Mathf.Abs(origin.CameraFloorOffsetObject.transform.lossyScale.x)) : 1f;
    RectTransform selector, watchFace;
    CanvasGroup selectorFade;
    Image watchIcon;
    TextMeshProUGUI watchStatus;
    public bool IsDragging => dragging;
    bool desktopOpen, dragging, contactHeld, armed;
    float gazeTime, lastLook, dragOrigin, nextDragStep;
    Vector3 dragAxis;
    Material watchOverlay;
    public string TrackingStatus { get; private set; } = "Raise your left hand to view the watch";
    int preview, lastCount = -1;
    string lastId;
    InputAction wheel;
    int queuedScroll, drawnIndex = -1, slideDirection;
    float slideTime, slideOffset, feedbackUntil;
    string feedback;
    bool sliding;
    const float SlotSpacing = 122f;
    Camera view;
    XROrigin origin;
    RectTransform root;
    Canvas canvas;
    TextMeshProUGUI title, detail, hint;
    readonly List<XRHandSubsystem> hands = new List<XRHandSubsystem>();
    readonly Image[] slots = new Image[7];
    readonly Image[] icons = new Image[7];
    readonly TextMeshProUGUI[] names = new TextMeshProUGUI[7];
    readonly Color gold = new Color(1f, .82f, .42f);
    readonly Color dark = new Color(.055f, .075f, .105f, .96f);
    bool vr;

    void Start()
    {
        Instance = this;
#if UNITY_EDITOR
        if (exampleItemsInEditor) AddExampleItems();
#endif
        view = Camera.main;
        origin = view ? view.GetComponentInParent<XROrigin>() : null;
        vr = Project.Forest.PlayerRuntimeMode.VRActive;
        Build();
        Hide();
        wheel = new InputAction("Inventory wheel", InputActionType.PassThrough, "<Mouse>/scroll/y");
        wheel.performed += OnWheel;
        wheel.Enable();
    }

#if UNITY_EDITOR
    static void AddExampleItems()
    {
        Inventory.Add("example_sword", "Sword", "Example weapon. Test selection only; no attack behavior.", false);
        Inventory.Add("example_bow", "Bow", "Example bow. Scroll to test selecting a ranged weapon.", false);
        Inventory.Add("example_bomb", "Bomb", "Example bomb. Safe test item; does not explode.", false);
        Inventory.Add("example_torch", "Torch", "Example torch. Test switching to a utility item.", false);
        Inventory.Add("example_key", "Ancient Key", "Example key. Does not unlock any real doors.", false);
        Inventory.Add("example_potion", "Health Potion", "Example consumable. Selection does not consume it.", false);
        Inventory.Add("example_rune", "Stasis Rune", "Example power. Test scrolling past five items and wrapping around.", false);
    }
#endif
    void OnWheel(InputAction.CallbackContext context)
    {
        float delta = context.ReadValue<float>();
        if (!vr && desktopOpen && !ShardlightMenus.IsOpen && Mathf.Abs(delta) > .01f)
            queuedScroll = Mathf.Clamp(queuedScroll + (delta > 0 ? -1 : 1), -8, 8);
    }

    public void ToggleDesktop()
    {
        if (Project.Forest.PlayerRuntimeMode.VRActive || ShardlightMenus.IsOpen) return;
        desktopOpen = !desktopOpen;
    }

    void Update()
    {
        if (!root) return;
        if (!view || !view.isActiveAndEnabled)
        {
            view = Camera.main;
            origin = view ? view.GetComponentInParent<XROrigin>() : null;
        }
        if (vr != Project.Forest.PlayerRuntimeMode.VRActive)
        {
            Hide();
            Destroy(root.gameObject);
            vr = Project.Forest.PlayerRuntimeMode.VRActive;
            desktopOpen = false;
            Build();
            Hide();
            return;
        }
        if (ShardlightMenus.IsOpen || !view)
        {
            desktopOpen = false;
            Hide();
            return;
        }
        if (vr) UpdateWrist();
        else
        {
            if (desktopOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                desktopOpen = false;
            if (!desktopOpen) { Hide(); return; }
            root.gameObject.SetActive(true);
            selector.gameObject.SetActive(true);
            if (!sliding && queuedScroll != 0)
            {
                int direction = Math.Sign(queuedScroll);
                queuedScroll -= direction;
                if (Inventory.Items.Count > 1)
                {
                    Inventory.Cycle(direction);
                    BeginSlide(direction);
                }
                else
                {
                    feedback = Inventory.Items.Count == 0 ? "NO ITEMS COLLECTED YET" : "ONLY ONE ITEM COLLECTED";
                    feedbackUntil = Time.unscaledTime + 1.5f;
                    queuedScroll = 0;
                }
            }
            preview = Inventory.SelectedIndex;
        }
        if (IsOpen)
        {
            if (selectorFade) selectorFade.alpha = Mathf.MoveTowards(selectorFade.alpha, 1, Time.unscaledDeltaTime * 8);
            if (vr) selector.localScale = Vector3.one * Mathf.Lerp(.3f, 1f, 1 - Mathf.Pow(1 - selectorFade.alpha, 3));
            if (sliding)
            {
                slideTime += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(slideTime / Mathf.Max(.05f, slideSeconds));
                // Cubic ease-out: fast response followed by a soft settle.
                slideOffset = slideDirection * SlotSpacing * Mathf.Pow(1 - t, 3);
                if (t >= 1) { sliding = false; slideOffset = 0; }
            }
            Refresh();
        }
    }

    void LateUpdate()
    {
        // Joint poses and wrist contact are read in Update; UI stays attached to the wrist.
        if (vr && root && root.gameObject.activeSelf && TryLeft(out var wrist, out var along, out var normal))
            PlaceWrist(wrist, along, normal);
    }

    void UpdateWrist()
    {
        if (!TryLeft(out var wrist, out var along, out var normal)) { TrackingStatus = "Left hand tracking unavailable"; Hide(); return; }
        PlaceWrist(wrist, along, normal);
        root.gameObject.SetActive(true);
        var selectedItem = Inventory.SelectedItem;
        watchIcon.sprite = selectedItem == null ? Inventory.IconFor("rune", "") : selectedItem.icon ? selectedItem.icon : Inventory.IconFor(selectedItem.id, selectedItem.name);
        watchStatus.text = "SHARDS " + ShardlightHUD.ShardsCollected + "  |  " + (WantedSystem.Level > 0 ? "ALERT " + WantedSystem.Level : "CLEAR");
        watchStatus.color = WantedSystem.Level > 0 ? new Color(1f, .45f, .3f) : gold;
        Vector3 toWrist = root.position - view.transform.position;
        float distance = toWrist.magnitude / TrackingScale;
        bool looking = distance < 1f && distance > .1f &&
            Vector3.Angle(view.transform.forward, toWrist) < gazeAngle &&
            Vector3.Dot(normal, -toWrist.normalized) > .25f;
        // The gaze dwell is deliberate; frame-to-frame joint jitter must not reset it.

        bool shouldOpen = AdvanceOpenDwell(looking, Time.unscaledDeltaTime);
        TrackingStatus = IsOpen ? "Right pinch + move to scroll; release to select" : "Look at the left watch to open inventory";
        watchStatus.text = gazeTime > 0 && !IsOpen ? "OPEN " + Mathf.Min(100, Mathf.RoundToInt(gazeTime / dwellSeconds * 100)) + "%" : "INVENTORY";
        if (looking) lastLook = Time.unscaledTime;
        if (!IsOpen)
        {
            if (!shouldOpen) return;
            selector.gameObject.SetActive(true);
            selectorFade.alpha = 0;
            preview = Inventory.SelectedIndex;
            armed = false;
        }
        watchIcon.enabled = !IsOpen;
        watchStatus.gameObject.SetActive(!IsOpen);
        if (!looking && !dragging && Time.unscaledTime - lastLook > .9f) { CloseWrist(); return; }
        if (!TryRightPinch(out var point, out bool pressed))
        { CancelDrag(); armed = false; return; }
        if (!pressed) armed = true;
        if (!dragging && armed && pressed && Vector3.Distance(point, root.position) / TrackingScale < .65f)
        {
            dragging = contactHeld = true;
            // Freeze the axis for this gesture and measure relative to the wrist.
            // Dot(worldPosition, a rotating axis) produces enormous phantom deltas.
            dragAxis = selector.right;
            dragOrigin = DragPosition(point);
            nextDragStep = Time.unscaledTime;
            preview = Inventory.SelectedIndex;
        }
        if (dragging && pressed)
        {
            float swipe = DragPosition(point);
            float delta = swipe - dragOrigin;
            float threshold = Mathf.Max(.065f, dragStep);
            if (Mathf.Abs(delta) >= threshold && Time.unscaledTime >= nextDragStep)
            {
                int step = delta > 0 ? 1 : -1;
                preview = Wrap(preview - step, Inventory.Items.Count);
                if (Inventory.Items.Count > 1) BeginSlide(-step);
                // Consume this movement once, without queuing extra steps from a tracking spike.
                dragOrigin = swipe;
                nextDragStep = Time.unscaledTime + .2f;
            }
        }
        if (dragging && contactHeld && !pressed)
        {
            Inventory.Select(preview);
            dragging = contactHeld = false;
            armed = false;
            lastLook = Time.unscaledTime;
        }
    }
    float DragPosition(Vector3 point) => Vector3.Dot(point - root.position, dragAxis) / TrackingScale;

    bool AdvanceOpenDwell(bool looking, float dt)
    {
        gazeTime = looking ? gazeTime + dt : Mathf.Max(0, gazeTime - dt * 2);
        return gazeTime >= dwellSeconds;
    }
    bool TryRightPinch(out Vector3 point, out bool pressed)
    {
        point = default; pressed = false;
        foreach (var system in hands)
        {
            var hand = system.rightHand;
            if (!system.running || !hand.isTracked ||
                !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index) ||
                !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb)) continue;
            var space = origin.CameraFloorOffsetObject.transform;
            point = space.TransformPoint((index.position + thumb.position) * .5f);
            float gap = Vector3.Distance(index.position, thumb.position);
            pressed = gap < (dragging ? pinchRelease : pinchStart);
            return true;
        }
        return false;
    }
    void PlaceWrist(Vector3 wrist, Vector3 along, Vector3 normal)
    {
        float scale = TrackingScale;
        float size = wristScale * scale;
        Vector3 parentScale = root.parent ? root.parent.lossyScale : Vector3.one;
        root.localScale = new Vector3(size / Mathf.Max(.001f, Mathf.Abs(parentScale.x)),
            size / Mathf.Max(.001f, Mathf.Abs(parentScale.y)), size / Mathf.Max(.001f, Mathf.Abs(parentScale.z)));
        root.position = wrist - along * (.045f * scale) + normal * (wristLift * scale);
        // Rigid watch orientation comes from the hand, never from the viewer's camera.
        root.rotation = Quaternion.LookRotation(-normal, along);
    }
    bool TryLeft(out Vector3 wrist, out Vector3 along, out Vector3 normal)
    {
        wrist = along = normal = default;
        if (!origin || !origin.CameraFloorOffsetObject) return false;
        SubsystemManager.GetSubsystems(hands);
        var modality = origin.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Inputs.XRInputModalityManager>(true);
        if (modality && modality.leftHand)
        {
            foreach (var driver in modality.leftHand.GetComponentsInChildren<XRHandSkeletonDriver>(true))
            {
                if (!driver.isActiveAndEnabled || !driver.handTrackingEvents || !driver.handTrackingEvents.handIsTracked) continue;
                Transform w = null, m = null, i = null, l = null;
                foreach (var joint in driver.jointTransformReferences)
                {
                    if (joint.xrHandJointID == XRHandJointID.Wrist) w = joint.jointTransform;
                    if (joint.xrHandJointID == XRHandJointID.MiddleProximal) m = joint.jointTransform;
                    if (joint.xrHandJointID == XRHandJointID.IndexProximal) i = joint.jointTransform;
                    if (joint.xrHandJointID == XRHandJointID.LittleProximal) l = joint.jointTransform;
                }
                if (!w || !m || !i || !l) continue;
                wrist = w.position;
                along = (m.position - w.position).normalized;
                normal = -Vector3.Cross(i.position - l.position, along).normalized;
                return normal.sqrMagnitude > .5f;
            }
        }
        foreach (var system in hands)
        {
            var hand = system.leftHand;
            if (!system.running || !hand.isTracked) continue;
            if (!hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out var w) ||
                !hand.GetJoint(XRHandJointID.MiddleProximal).TryGetPose(out var m) ||
                !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var i) ||
                !hand.GetJoint(XRHandJointID.LittleProximal).TryGetPose(out var l)) continue;
            var space = origin.CameraFloorOffsetObject.transform;
            wrist = space.TransformPoint(w.position);
            along = space.TransformDirection((m.position - w.position).normalized);
            // Opposite the palm: a watch face on the back of the left wrist.
            normal = -space.TransformDirection(Vector3.Cross(i.position - l.position,
                m.position - w.position).normalized);
            return along.sqrMagnitude > .5f && normal.sqrMagnitude > .5f;
        }
        return false;
    }

    void BeginSlide(int direction)
    {
        sliding = true;
        slideDirection = direction;
        slideTime = 0;
        slideOffset = direction * SlotSpacing;
    }

    void CancelDrag() { dragging = contactHeld = false; preview = Inventory.SelectedIndex; }
    void CloseWrist()
    {
        if (selector) selector.gameObject.SetActive(false);
        if (watchIcon) watchIcon.enabled = true;
        if (watchStatus) watchStatus.gameObject.SetActive(true);
        gazeTime = 0;
        armed = false;
        sliding = false;
        slideOffset = 0;
        drawnIndex = -1;
        CancelDrag();
    }
    void Hide()
    {
        CloseWrist();
        if (root) root.gameObject.SetActive(false);
        gazeTime = 0;
        queuedScroll = 0;
        sliding = false;
        slideOffset = 0;
        drawnIndex = -1;
        feedbackUntil = 0;
        armed = false;
        CancelDrag();
    }
    void OnEnable() { wheel?.Enable(); }
    void OnDisable() { wheel?.Disable(); desktopOpen = false; Hide(); }
    void OnDestroy() { wheel?.Dispose(); if (watchOverlay) Destroy(watchOverlay); if (Instance == this) Instance = null; }
    static int Wrap(int index, int count) => count == 0 ? -1 : (index % count + count) % count;

    void Refresh()
    {
        int count = Inventory.Items.Count;
        if (!dragging) preview = Inventory.SelectedIndex;
        preview = Wrap(preview, count);
        var selected = preview < 0 ? null : Inventory.Items[preview];
        if (selected != null && lastId != selected.id && lastCount >= 0) ShardlightHUD.PlayChime(7);
        lastId = selected?.id;
        lastCount = count;
        title.text = selected != null ? selected.name.ToUpperInvariant() : "SATCHEL EMPTY";
        detail.text = vr ? ShardlightHUD.WristSummary : selected != null ? selected.description : "Collect items to add them to your wrist inventory.";
        hint.text = Time.unscaledTime < feedbackUntil ? feedback : count < 2 ?
            (count == 0 ? "COLLECT ITEMS TO FILL YOUR SATCHEL" : "1 ITEM  -  COLLECT MORE TO SCROLL") :
            vr ? (dragging ? "RELEASE TO SELECT" : "RIGHT PINCH + MOVE TO SCROLL") : "TAB  CLOSE     SCROLL WHEEL  SELECT";
        if (drawnIndex != preview && !sliding && drawnIndex >= 0 && count > 1)
        {
            int direction = Wrap(preview - drawnIndex, count) == 1 ? 1 : -1;
            BeginSlide(direction);
        }
        drawnIndex = preview;
        for (int s = 0; s < slots.Length; s++)
        {
            int offset = s - 3;
            int index = Wrap(preview + offset, count);
            int min = -(count / 2), max = (count - 1) / 2;
            // Include the outgoing edge while wrapping a short inventory.
            if (sliding && count > 1)
            {
                min -= Mathf.Max(slideDirection, 0);
                max -= Mathf.Min(slideDirection, 0);
            }
            bool filled = count > 0 && offset >= min && offset <= max;
            var item = filled ? Inventory.Items[index] : null;
            float x = offset * SlotSpacing + slideOffset;
            float emphasis = 1 - Mathf.Clamp01(Mathf.Abs(x) / SlotSpacing);
            slots[s].gameObject.SetActive(!vr || Mathf.Abs(x) < SlotSpacing * 2.5f);
            slots[s].rectTransform.anchoredPosition = new Vector2(x, 0);
            slots[s].color = Color.Lerp(new Color(.2f, .27f, .34f, filled ? .95f : .25f), gold, emphasis);
            float pulse = Time.unscaledTime < feedbackUntil ? .04f * Mathf.Sin(Time.unscaledTime * 18) : 0;
            slots[s].rectTransform.localScale = Vector3.one * Mathf.Lerp(.92f, 1.12f + pulse, emphasis);
            names[s].text = item?.name ?? "";
            names[s].color = Color.Lerp(Color.white, dark, emphasis);
            icons[s].sprite = item == null ? null : item.icon ? item.icon : Inventory.IconFor(item.id, item.name);
            icons[s].enabled = item != null && icons[s].sprite;
        }
    }

    void Build()
    {
        root = Rect("Wrist Inventory", transform, Vector2.zero, new Vector2(660, 270));
        canvas = root.gameObject.AddComponent<Canvas>();
        canvas.sortingOrder = 650;
        if (vr)
        {
            if (!watchOverlay) { watchOverlay = new Material(Shader.Find("UI/Default")); watchOverlay.SetFloat("unity_GUIZTestMode", 8f); }
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = view;
            root.localScale = Vector3.one * wristScale * TrackingScale;
        }
        else
        {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = root.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;
            // Overlay canvases span the screen; place the compact panel below centre.
        }
        if (vr)
        {
            watchFace = Rect("Watch body", root, Vector2.zero, new Vector2(178, 130));
            watchFace.localRotation = Quaternion.Euler(0, 0, vrInventoryRotation);
            Image(watchFace, gold);
            Image(Rect("Watch screen", watchFace, Vector2.zero, new Vector2(168, 120)), dark);
            Image(Rect("Watch strap upper", watchFace, new Vector2(0, 79), new Vector2(60, 38)), new Color(.09f, .12f, .14f));
            Image(Rect("Watch strap lower", watchFace, new Vector2(0, -79), new Vector2(60, 38)), new Color(.09f, .12f, .14f));
            watchIcon = Image(Rect("Selected item", watchFace, new Vector2(0, 8), new Vector2(72, 72)), Color.white);
            watchIcon.preserveAspect = true;
            watchStatus = Text(watchFace, new Vector2(0, -43), new Vector2(102, 20), 10, gold);
        }
        var panel = Rect("Selector", root, vr ? Vector2.zero : new Vector2(0, -180), new Vector2(660, 270));
        selector = panel;
        if (vr) { selector.localRotation = Quaternion.Euler(0, 0, vrInventoryRotation); selector.localScale = Vector3.one; selector.localPosition = new Vector3(0, 0, -180); }
        selectorFade = panel.gameObject.AddComponent<CanvasGroup>();
        Image(panel, dark);
        Image(Rect("Gold line", panel, new Vector2(0, 134), new Vector2(660, 2)), gold);
        title = Text(panel, new Vector2(0, 103), new Vector2(620, 32), 24, gold);
        var viewport = Rect("Carousel viewport", panel, new Vector2(0, 23), new Vector2(614, 138));
        if (!vr) viewport.gameObject.AddComponent<RectMask2D>();
        for (int s = 0; s < slots.Length; s++)
        {
            var cell = Rect("Slot " + s, viewport, new Vector2((s - 3) * SlotSpacing, 0), new Vector2(108, 108));
            slots[s] = Image(cell, dark);
            icons[s] = Image(Rect("Icon", cell, new Vector2(0, 9), new Vector2(76, 76)), Color.white);
            icons[s].preserveAspect = true;
            names[s] = Text(cell, new Vector2(0, -40), new Vector2(102, 24), 14, Color.white);
            names[s].enableAutoSizing = true;
            names[s].fontSizeMin = 10;
        }
        detail = Text(panel, new Vector2(0, -64), new Vector2(604, 42), 17, Color.white);
        detail.enableAutoSizing = true;
        detail.fontSizeMin = 12;
        if (vr) detail.gameObject.SetActive(false);
        hint = Text(panel, new Vector2(0, -110), new Vector2(620, 26), 14, gold);
        // Keep the scrolling instruction readable on the expanded panel.
    }

    static RectTransform Rect(string name, Transform parent, Vector2 pos, Vector2 size)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
        rect.anchoredPosition = pos;
        rect.sizeDelta = size;
        return rect;
    }
    Image Image(RectTransform rect, Color color)
    {
        var image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        if (vr && watchOverlay) image.material = watchOverlay;
        image.raycastTarget = false;
        return image;
    }
    TextMeshProUGUI Text(Transform parent, Vector2 pos, Vector2 size, float fontSize, Color color)
    {
        var label = Rect("Label", parent, pos, size).gameObject.AddComponent<TextMeshProUGUI>();
        if (ShardlightMenus.Instance && ShardlightMenus.Instance.font) label.font = ShardlightMenus.Instance.font;
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        label.richText = false;
        if (vr) label.fontMaterial.SetFloat("unity_GUIZTestMode", 8f);
        return label;
    }
}
