using System;
using System.Collections;
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

    [Header("Pull items out")]
    [Tooltip("Optional real models (item id -> prefab). Items without one get a simple placeholder.")]
    public List<ItemModel> itemModels = new List<ItemModel>();
    [Tooltip("VR: pinch the carousel and pull this far away from the watch (metres) to take the item out.")]
    public float pullDistance = .12f;
    [Serializable] public class ItemModel { public string id; public GameObject prefab; }
    [Header("Ammo")]
    public int maxArrows = 12, maxBombs = 3;
    [Tooltip("Seconds to get one bomb back.")] public float bombRecharge = 20f;
    static int arrows = -1, bombs = -1; static float bombTimer;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void ResetAmmo() { arrows = bombs = -1; bombTimer = 0f; }
    /// Picked an arrow back up (or any arrow pickup you make).
    public static void AddArrows(int n) => arrows = Mathf.Min(arrows + n, Instance ? Instance.maxArrows : 99);
    static readonly Queue<GameObject> fired = new Queue<GameObject>();
    GameObject held; string heldId, heldKind; bool holding, busy, wasLocked, aiming; float aimBlend;
    [Header("Aiming (desktop: hold right mouse with the bow or bombs)")] public float aimDrain = .2f;   // stamina per second
    float pullOrigin; Vector3 pullNormal;

    [Tooltip("Editor Play Mode only; controlled by ShardlightMenus.")]
    public bool exampleItemsInEditor = true;

    public static WristInventory Instance { get; private set; }
    /// What you're holding right now ("sword", "bow", "bomb", "potion"...), or null. Used by the HUD reticle.
    public static string HeldKind => Instance && Instance.held ? Instance.heldKind : null;
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
        if (arrows < 0) arrows = maxArrows;
        if (bombs < 0) bombs = maxBombs;
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
        if (bombs < maxBombs) { if ((bombTimer += Time.deltaTime) >= bombRecharge) { bombTimer = 0f; bombs++; } } else bombTimer = 0f;   // bombs come back over time
        if (!view || !view.isActiveAndEnabled)
        {
            view = Camera.main;
            origin = view ? view.GetComponentInParent<XROrigin>() : null;
        }
        if (vr != Project.Forest.PlayerRuntimeMode.VRActive)
        {
            Hide();
            PutAway();
            Destroy(root.gameObject);
            vr = Project.Forest.PlayerRuntimeMode.VRActive;
            desktopOpen = false;
            Build();
            Hide();
            return;
        }
        if (ShardlightMenus.IsOpen || !view)
        {
            ShardlightHUD.Aiming = false;
            desktopOpen = false;
            Hide();
            return;
        }
        if (!vr && Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame) { if (held) PutAway(); else TakeOut(); }
        if (!vr && held && heldId != Inventory.SelectedItem?.id) TakeOut();   // scrolled to another item: swap it
        bool locked = Cursor.lockState == CursorLockMode.Locked;               // (the click that locks the cursor isn't a use)
        if (!vr && locked && wasLocked && Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) Use();
        bool wantAim = !vr && locked && held && !busy && (heldKind == "bow" || heldKind == "bomb") && Mouse.current != null && Mouse.current.rightButton.isPressed;
        aiming = wantAim && ShardlightHUD.UseStamina(aimDrain * Time.deltaTime);   // drops when stamina runs out
        ShardlightHUD.Aiming = aiming;
        if (!vr && held && !busy) Pose();
        wasLocked = locked;
        ShardlightHUD.HeldItem = held ? heldKind : null;
        string ammo = held && (heldKind == "bow" || heldKind == "bomb") ? heldKind : null;   // ammo counter, top right
        ShardlightHUD.AmmoKind = ammo;
        ShardlightHUD.AmmoCount = ammo == "bow" ? arrows : bombs;
        ShardlightHUD.AmmoMax = ammo == "bow" ? maxArrows : maxBombs;
        ShardlightHUD.AmmoRefill = ammo == "bomb" && bombs < maxBombs ? bombTimer / bombRecharge : -1f;
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
        if (holding && held)   // VR: item follows your pinch; let go and it drops (grab it again like any object)
        {
            if (TryRightPinch(out var p, out bool pinched) && pinched)
                held.transform.SetPositionAndRotation(p, Quaternion.LookRotation(p - view.transform.position) * Quaternion.Euler(90f, 0f, 0f));
            else { holding = false; held.GetComponent<Rigidbody>().isKinematic = false; if (heldKind == "bomb") LightFuse(); }
        }
        if (vr && held && heldKind == "potion" && Vector3.Distance(held.transform.position, view.transform.position) < .18f) DrinkNow();   // raise it to your mouth
        if (vr) BowHand();
    }

    // ---- VR bow (hand tracking): it sits in your left hand. Pinch near it with your right hand, pull back
    // toward your face, and let go to fire. Pull further = faster, further arrow. A short tug doesn't fire.
    bool bowHand, drawing, lastPinch; float draw; Vector3 drawPoint; Transform bowString, drawRig, strA, strB, nocked;
    void BowHand()
    {
        if (!bowHand || !held) { EndDraw(); return; }
        if (TryLeft(out var wrist, out var along, out var normal))
        {
            var up = Vector3.ProjectOnPlane(Vector3.up, along).normalized;   // bow stays upright
            held.transform.SetPositionAndRotation(wrist + along * (.08f * TrackingScale) - normal * (.03f * TrackingScale), Quaternion.LookRotation(Vector3.Cross(along, up), up));
        }
        Vector3 grip = held.transform.position;
        bool pinch = TryRightPinch(out var p, out bool pressed) && pressed;
        if (!drawing && pinch && !lastPinch && Vector3.Distance(p, grip) < .2f * TrackingScale)   // grab the string
        {
            if (arrows <= 0) Empty();
            else
            {
                drawing = true;
                drawRig = new GameObject("Bow draw").transform;
                strA = Bit(new Color(.9f, .9f, .85f)); strB = Bit(new Color(.9f, .9f, .85f)); nocked = Bit(new Color(.42f, .28f, .16f));
                if (bowString) bowString.gameObject.SetActive(false);
            }
        }
        lastPinch = pinch;
        if (!drawing) return;
        if (pinch)   // pulling: string bends to your fingers, arrow rests on the bow
        {
            drawPoint = p;
            draw = Mathf.Clamp01((Vector3.Distance(p, grip) / TrackingScale - .1f) / .5f);
            Vector3 up = held.transform.up * .33f;
            Stretch(strA, grip + up, p, .004f); Stretch(strB, grip - up, p, .004f);
            Stretch(nocked, p, p + (grip - p).normalized * .65f, .015f);
            return;
        }
        if (draw > .15f) { var dir = (grip - drawPoint).normalized; Shoot(grip + dir * .35f, dir, 12f + 30f * draw); }   // let go: fire
        EndDraw();
    }
    Transform Bit(Color c) { var t = Part(drawRig.gameObject, PrimitiveType.Cube, Vector3.zero, Vector3.one * .01f, c); Destroy(t.GetComponent<Collider>()); return t; }
    static void Stretch(Transform t, Vector3 a, Vector3 b, float thick)
    {
        t.SetPositionAndRotation((a + b) * .5f, Quaternion.LookRotation(b - a + Vector3.forward * 1e-5f));
        t.localScale = new Vector3(thick, thick, Vector3.Distance(a, b));
    }
    void EndDraw()
    {
        if (drawRig) Destroy(drawRig.gameObject);
        if (bowString) bowString.gameObject.SetActive(true);
        drawing = false; draw = 0f;
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
        if (!dragging && !drawing && armed && pressed && Vector3.Distance(point, root.position) / TrackingScale < .65f)
        {
            dragging = contactHeld = true;
            // Freeze the axis for this gesture and measure relative to the wrist.
            // Dot(worldPosition, a rotating axis) produces enormous phantom deltas.
            dragAxis = selector.right;
            dragOrigin = DragPosition(point);
            pullNormal = -root.forward;   // straight out from the watch
            pullOrigin = Vector3.Dot(point - root.position, pullNormal) / TrackingScale;
            nextDragStep = Time.unscaledTime;
            preview = Inventory.SelectedIndex;
        }
        if (dragging && pressed && preview >= 0 && Vector3.Dot(point - root.position, pullNormal) / TrackingScale - pullOrigin > pullDistance)
        {   // pulled away from the watch: take the item out into your hand
            Inventory.Select(preview);
            TakeOut();
            holding = held && !bowHand;
            CancelDrag(); armed = false;
            return;
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
            pressed = gap < (dragging || holding || drawing ? pinchRelease : pinchStart);
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
    void OnDestroy() { PutAway(); wheel?.Dispose(); if (watchOverlay) Destroy(watchOverlay); if (Instance == this) Instance = null; }
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
            vr ? (dragging ? "RELEASE TO SELECT  -  PULL OUT TO TAKE" : "RIGHT PINCH + MOVE TO SCROLL") : "TAB  CLOSE     SCROLL  SELECT     F  HOLD     CLICK  USE";
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

    // One item out at a time. Desktop: held in view. VR: a physics object you can grab.
    void TakeOut()
    {
        PutAway();
        var item = Inventory.SelectedItem;
        if (item == null || !view) return;
        GiveGolemsBodies();
        if (vr && KindOf(item.id, item.name) == "bomb" && bombs <= 0) { Empty(); return; }   // VR: no bomb to pull out yet
        held = Model(item.id, item.name); heldId = item.id; heldKind = KindOf(item.id, item.name);
        bowHand = vr && heldKind == "bow" && TryLeft(out _, out _, out _);   // hand tracking: the bow sits in your left hand
        if (bowHand)
        {
            foreach (var c in held.GetComponentsInChildren<Collider>()) Destroy(c);
            bowString = held.transform.Find("String");
        }
        else if (vr)
        {
            held.AddComponent<Rigidbody>().isKinematic = true;
            var grab = held.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRGrabInteractable>();
            if (heldKind != "bow" && heldKind != "potion") { var h = held.AddComponent<ItemHit>(); h.damage = DamageOf(heldKind); }   // swing it into things
            if (heldKind == "bow") grab.activated.AddListener(_ => Shoot(held.transform.position + view.transform.forward * .3f, view.transform.forward));   // trigger fires
        }
        else
        {
            foreach (var c in held.GetComponentsInChildren<Collider>()) Destroy(c);
            held.transform.SetParent(view.transform, false);
            held.transform.localPosition = new Vector3(.28f, -.3f, .55f);
            held.transform.localRotation = Quaternion.Euler(60f, -15f, 0f);
        }
        ShardlightHUD.PlayChime(7);
    }
    void PutAway() { EndDraw(); bowHand = false; if (held) Destroy(held); held = null; heldId = null; holding = busy = false; }

    // ---- Using items. Desktop: left click. VR: swing/throw it for real, drink by raising it to your mouth, trigger fires the bow.
    // Anything hit gets OnItemHit("sword"/"bomb"/...) so crates, levers etc. can react.
    void Use()
    {
        if (!held || busy) return;
        if (heldKind == "potion") StartCoroutine(Drink());
        else if (heldKind == "bomb")   // throw a copy; you keep the bomb bag
        {
            if (bombs <= 0) { Empty(); return; }
            bombs--;
            var b = Model(heldId, "bomb"); b.transform.SetPositionAndRotation(held.transform.position, held.transform.rotation);
            foreach (var c in b.GetComponentsInChildren<Collider>()) IgnorePlayer(c);
            b.AddComponent<Rigidbody>().linearVelocity = view.transform.forward * (aiming ? 16f : 12f) + Vector3.up * 3f;   // aimed throws go further
            StartCoroutine(Fuse(b));
        }
        else if (heldKind == "bow") Shoot(view.transform.position + view.transform.forward * .5f, Quaternion.Euler(UnityEngine.Random.insideUnitCircle * (aiming ? 0f : 4f)) * view.transform.forward);   // aimed = dead on, hip-fire = a little spread
        else if (ShardlightHUD.UseStamina(.12f)) StartCoroutine(Swing());   // swings cost stamina
    }
    IEnumerator Swing()
    {
        busy = true; var t = held.transform; var r0 = t.localRotation; bool hit = false;
        for (float u = 0; u < 1f && t; u += Time.deltaTime / .3f)
        {
            float s = Mathf.Sin(u * Mathf.PI);
            t.localRotation = r0 * Quaternion.Euler(50f * s, 0f, 70f * s);
            if (u > .4f && !hit) { hit = true; Blast(view.transform.position, view.transform.position + view.transform.forward * 1.2f, .8f, 3f, heldKind); }
            yield return null;
        }
        if (t) t.localRotation = r0;
        busy = false;
    }
    IEnumerator Drink()
    {
        busy = true; var t = held.transform; Vector3 p0 = t.localPosition; Quaternion r0 = t.localRotation;
        for (float u = 0; u < 1f && t; u += Time.deltaTime / .8f)
        {
            float s = Mathf.SmoothStep(0f, 1f, Mathf.PingPong(u * 2f, 1f));
            t.localPosition = Vector3.Lerp(p0, new Vector3(0f, -.1f, .22f), s); t.localRotation = Quaternion.Slerp(r0, Quaternion.Euler(150f, 0f, 0f), s);
            yield return null;
        }
        if (t) DrinkNow();
        busy = false;
    }
    void DrinkNow() { string id = heldId; PutAway(); Consume(id); ShardlightHUD.Heal(.4f); ShardlightHUD.ShowHint("You feel {restored}.", 2f); }
    // VR: let go of a bomb and its fuse lights. Uses one bomb; pull another from the watch.
    void LightFuse() { var b = held; held = null; heldId = null; holding = false; bombs = Mathf.Max(0, bombs - 1); StartCoroutine(Fuse(b)); }
    void Shoot(Vector3 pos, Vector3 dir, float speed = 35f) { if (arrows <= 0) { Empty(); return; } arrows--; Arrow(pos, dir, speed); }
    void Empty()
    {
        ShardlightHUD.ShowHint(heldKind == "bow" ? "Out of {arrows}. Walk over your arrows to pick them back up."
            : "No {bombs} left. One comes back every " + Mathf.RoundToInt(bombRecharge) + "s.", 2.5f);
    }
    IEnumerator Fuse(GameObject b)
    {
        yield return new WaitForSeconds(2.5f);
        if (!b) yield break;
        Vector3 pos = b.transform.position; Destroy(b);
        Blast(pos, pos, 4f, 10f, "bomb");
        if (view)   // too close? it hurts you too
        {
            float d = Vector3.Distance(view.transform.position - Vector3.up * .8f, pos);
            if (d < 4f) ShardlightHUD.Damage(.4f * (1f - d / 4f * .7f));
        }
        AudioSource.PlayClipAtPoint(Boom(), pos);
        var fx = Part(new GameObject("Explosion"), PrimitiveType.Sphere, Vector3.zero, Vector3.zero, new Color(1f, .6f, .2f), true);
        fx.parent.position = pos; Destroy(fx.GetComponent<Collider>()); fx.gameObject.AddComponent<Light>().color = new Color(1f, .6f, .2f);
        for (float u = 0; u < 1f; u += Time.deltaTime / .35f) { fx.localScale = Vector3.one * 4f * u; yield return null; }
        Destroy(fx.parent.gameObject);
    }
    // Desktop held pose: at the hip, or raised to aim (bow up in front of you, bomb pulled back by the shoulder).
    void Pose()
    {
        aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, Time.deltaTime * 6f);
        float a = Mathf.SmoothStep(0f, 1f, aimBlend), t = Time.time;
        Vector3 hipPos = new Vector3(.28f, -.3f, .55f), aimPos = heldKind == "bow" ? new Vector3(.02f, -.1f, .45f) : new Vector3(.24f, -.06f, .32f);
        Quaternion hipRot = Quaternion.Euler(60f, -15f, 0f), aimRot = heldKind == "bow" ? Quaternion.Euler(0f, -90f, -8f) : Quaternion.Euler(20f, 0f, 0f);
        var sway = new Vector3(Mathf.Sin(t * 1.3f) * .004f, Mathf.Sin(t * 2.1f) * .003f, 0f) * a;   // breathing while you hold the aim
        held.transform.localPosition = Vector3.Lerp(hipPos, aimPos, a) + sway;
        held.transform.localRotation = Quaternion.Slerp(hipRot, aimRot, a);
    }

    void Arrow(Vector3 pos, Vector3 dir, float speed = 35f)
    {
        var a = Part(new GameObject("Arrow"), PrimitiveType.Cube, Vector3.zero, new Vector3(.02f, .02f, .6f), new Color(.42f, .28f, .16f)).parent;
        a.SetPositionAndRotation(pos, Quaternion.LookRotation(dir)); IgnorePlayer(a.GetComponentInChildren<Collider>());
        var rb = a.gameObject.AddComponent<Rigidbody>(); rb.mass = .1f; rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; rb.linearVelocity = dir * speed;
        var hit = a.gameObject.AddComponent<ItemHit>(); hit.damage = DamageOf("bow"); hit.minSpeed = 0f; hit.arrow = true;
        fired.Enqueue(a.gameObject); while (fired.Count > 30) { var old = fired.Dequeue(); if (old) Destroy(old); }   // keep at most 30 lying around
        ShardlightHUD.PlayChime(7);
    }
    void Blast(Vector3 from, Vector3 at, float radius, float force, string kind)   // push loose objects away and tell them they were hit
    {
        var seen = new HashSet<GameObject>();
        foreach (var c in Physics.OverlapSphere(at, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            if (ShardlightHUD.IsPlayer(c) || !seen.Add(Damageable.Root(c))) continue;
            if (c.attachedRigidbody && !c.attachedRigidbody.isKinematic) c.attachedRigidbody.AddExplosionForce(force, from, radius + 2f, .3f, ForceMode.Impulse);
            float falloff = kind == "bomb" ? 1f - Mathf.Clamp01(Vector3.Distance(c.ClosestPoint(at), at) / radius) * .7f : 1f;
            Damageable.Hit(c, DamageOf(kind) * falloff);
            c.SendMessageUpwards("OnItemHit", kind, SendMessageOptions.DontRequireReceiver);
        }
        foreach (var g in FindObjectsByType<GolemGuardAI>(FindObjectsSortMode.None))   // golems: by body shape, collider or not
        {
            if (!g.isActiveAndEnabled || seen.Contains(g.gameObject)) continue;
            var b = Damageable.Body(g.gameObject);
            float d = Vector3.Distance(b.ClosestPoint(at), at);
            if (d <= radius) Damageable.HitObject(g.gameObject, DamageOf(kind) * (kind == "bomb" ? 1f - d / radius * .7f : 1f));
        }
    }
    // Golems often have no collider (the NavMeshAgent isn't one), so arrows and swings went straight through.
    // Give each one a capsule around its model; the player still walks through it.
    void GiveGolemsBodies()
    {
        var cc = view ? view.GetComponentInParent<CharacterController>() : null;
        foreach (var g in FindObjectsByType<GolemGuardAI>(FindObjectsSortMode.None))
        {
            bool solid = false; foreach (var c in g.GetComponentsInChildren<Collider>()) solid |= !c.isTrigger;   // trigger zones don't stop arrows
            if (solid) continue;
            var b = new Bounds(g.transform.position + Vector3.up, Vector3.one);
            foreach (var r in g.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
            var cap = g.gameObject.AddComponent<CapsuleCollider>(); var s = g.transform.lossyScale;
            cap.center = g.transform.InverseTransformPoint(b.center);
            cap.height = b.size.y / Mathf.Max(.01f, s.y);
            cap.radius = Mathf.Min(b.extents.x, b.extents.z) * .7f / Mathf.Max(.01f, s.x);
            if (cc) Physics.IgnoreCollision(cap, cc);
        }
    }
    void IgnorePlayer(Collider c) { var cc = view.GetComponentInParent<CharacterController>(); if (c && cc) Physics.IgnoreCollision(c, cc); }
    static void Consume(string id)   // used up: remove from the satchel
    {
        int i = Inventory.SelectedIndex;
        Inventory.Items.RemoveAll(x => x.id == id);
        if (Inventory.Items.Count > 0) Inventory.Select(Mathf.Clamp(i, 0, Inventory.Items.Count - 1));
    }
    static AudioClip boom;
    static AudioClip Boom()   // short generated explosion thud
    {
        if (boom) return boom;
        const int rate = 22050; var d = new float[rate]; float y = 0f; var r = new System.Random(1);
        for (int i = 0; i < d.Length; i++) { y += ((float)r.NextDouble() * 2f - 1f - y) * .06f; d[i] = y * 3f * Mathf.Exp(-i / (rate * .18f)); }
        boom = AudioClip.Create("boom", d.Length, 1, rate, false); boom.SetData(d, 0); return boom;
    }
    static float DamageOf(string kind) => kind == "sword" ? 25f : kind == "bow" ? 35f : kind == "bomb" ? 60f : kind == "torch" ? 15f : 10f;
    static string KindOf(string id, string name)
    {
        string k = (id + " " + name).ToLowerInvariant();
        foreach (var w in new[] { "sword", "bow", "bomb", "torch", "key", "potion" }) if (k.Contains(w)) return w;
        return "rune";
    }

    // Real prefab if you assigned one, else a quick placeholder built from shapes (long axis = up, grip at 0).
    GameObject Model(string id, string name)
    {
        foreach (var m in itemModels) if (m.prefab && m.id == id) return Instantiate(m.prefab);
        var g = new GameObject(name);
        string k = KindOf(id, name);
        Color metal = new Color(.78f, .8f, .85f), wood = new Color(.42f, .28f, .16f), glow = new Color(.45f, .85f, 1f);
        if (k == "sword") { Part(g, PrimitiveType.Cube, new Vector3(0, .42f, 0), new Vector3(.05f, .66f, .012f), metal); Part(g, PrimitiveType.Cube, new Vector3(0, .08f, 0), new Vector3(.2f, .03f, .04f), gold); Part(g, PrimitiveType.Cylinder, Vector3.zero, new Vector3(.035f, .07f, .035f), wood); }
        else if (k == "bow") { for (int i = -4; i <= 4; i++) { float y = i * .08f; Part(g, PrimitiveType.Cube, new Vector3(.12f * (1 - y * y / .1f), y, 0), new Vector3(.025f, .09f, .025f), wood).localRotation = Quaternion.Euler(0, 0, y * 120f); } Part(g, PrimitiveType.Cube, Vector3.zero, new Vector3(.004f, .66f, .004f), Color.white).name = "String"; }
        else if (k == "bomb") { Part(g, PrimitiveType.Sphere, Vector3.zero, Vector3.one * .16f, new Color(.12f, .12f, .14f)); Part(g, PrimitiveType.Cylinder, new Vector3(0, .1f, 0), new Vector3(.015f, .03f, .015f), wood); }
        else if (k == "torch") { Part(g, PrimitiveType.Cylinder, new Vector3(0, .1f, 0), new Vector3(.04f, .22f, .04f), wood); Part(g, PrimitiveType.Sphere, new Vector3(0, .36f, 0), Vector3.one * .09f, new Color(1f, .6f, .2f), true); g.transform.GetChild(1).gameObject.AddComponent<Light>().range = 4f; }
        else if (k == "key") { Part(g, PrimitiveType.Cube, new Vector3(0, .08f, 0), new Vector3(.015f, .16f, .015f), gold); Part(g, PrimitiveType.Cylinder, Vector3.zero, new Vector3(.06f, .005f, .06f), gold).localRotation = Quaternion.Euler(90, 0, 0); Part(g, PrimitiveType.Cube, new Vector3(.02f, .15f, 0), new Vector3(.03f, .02f, .01f), gold); }
        else if (k == "potion") { Part(g, PrimitiveType.Sphere, Vector3.zero, Vector3.one * .1f, new Color(.85f, .15f, .2f), true); Part(g, PrimitiveType.Cylinder, new Vector3(0, .07f, 0), new Vector3(.03f, .025f, .03f), new Color(.8f, .9f, 1f)); }
        else Part(g, PrimitiveType.Cube, Vector3.zero, Vector3.one * .1f, glow, true).localRotation = Quaternion.Euler(45, 0, 45);   // rune / shard / anything else
        return g;
    }
    static Transform Part(GameObject parent, PrimitiveType type, Vector3 pos, Vector3 scale, Color color, bool glow = false)
    {
        var t = GameObject.CreatePrimitive(type).transform;
        t.SetParent(parent.transform, false); t.localPosition = pos; t.localScale = scale;
        var shader = Shader.Find(glow ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        t.GetComponent<Renderer>().sharedMaterial = new Material(shader) { color = color };
        return t;
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

/// Damage from items. Hits a golem or any loose physics prop (anything with a Rigidbody); this is added to it automatically.
/// Props break into rubble. Golems crumble, then reform at home after reformTime. Static scenery is never damaged.
public class Damageable : MonoBehaviour
{
    public float maxHealth = -1f, reformTime = 30f;
    float health, flash, top, ghost = 1f, ghostWait; GolemGuardAI golem; Renderer[] rends; MaterialPropertyBlock mpb;
    Transform bar; RectTransform fill, ghostFill; CanvasGroup barFade;

    public static GameObject Root(Collider c)
    {
        var g = c.GetComponentInParent<GolemGuardAI>(); if (g) return g.gameObject;
        var d = c.GetComponentInParent<Damageable>(); if (d) return d.gameObject;
        return c.attachedRigidbody ? c.attachedRigidbody.gameObject : c.gameObject;
    }
    public static void Hit(Collider c, float amount)
    {
        var root = Root(c);
        if (root.GetComponent<GolemGuardAI>() || root.GetComponent<Damageable>() || (c.attachedRigidbody && !c.attachedRigidbody.isKinematic)) HitObject(root, amount);
    }
    public static void HitObject(GameObject root, float amount)
    {
        if (!root || amount <= 0f || root.name == "Rubble" || root.GetComponent<ItemHit>()) return;   // not rubble, arrows or held items
        var d = root.GetComponent<Damageable>(); if (!d) d = root.AddComponent<Damageable>();
        d.Take(amount);
    }
    /// The space a model takes up (from its visible meshes).
    public static Bounds Body(GameObject g)
    {
        var b = new Bounds(g.transform.position + Vector3.up, Vector3.one * .5f);
        foreach (var r in g.GetComponentsInChildren<Renderer>()) if (r.enabled && !(r is ParticleSystemRenderer)) b.Encapsulate(r.bounds);
        return b;
    }
    void Awake()
    {
        golem = GetComponent<GolemGuardAI>(); rends = GetComponentsInChildren<Renderer>(); mpb = new MaterialPropertyBlock();
        var rb = GetComponent<Rigidbody>();
        if (maxHealth <= 0f) maxHealth = golem ? 100f : Mathf.Clamp(40f * (rb ? rb.mass : 1f), 20f, 150f);   // heavier props take more
        health = maxHealth;
        var b = new Bounds(transform.position, Vector3.zero);
        foreach (var r in rends) if (r) b.Encapsulate(r.bounds);
        top = b.max.y - transform.position.y + (golem ? .4f : .25f);
        BuildBar();
    }

    // Health bar floating over it: shows once damaged, red drains instantly, a pale trail catches up behind it.
    void BuildBar()
    {
        var go = new GameObject(name + " Health Bar", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup));
        go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        bar = go.transform; ((RectTransform)bar).sizeDelta = new Vector2(100f, 12f); bar.localScale = Vector3.one * (golem ? .012f : .006f);
        barFade = go.GetComponent<CanvasGroup>(); barFade.alpha = 0f;
        Box(new Color(0f, 0f, 0f, .65f), -2f, 104f, 16f);
        ghostFill = Box(new Color(1f, .9f, .75f, .85f), 0f, 100f, 10f);
        fill = Box(new Color(.9f, .22f, .2f), 0f, 100f, 10f);
    }
    RectTransform Box(Color c, float x, float w, float h)
    {
        var r = new GameObject("Bar", typeof(RectTransform), typeof(UnityEngine.UI.Image)).GetComponent<RectTransform>();
        r.SetParent(bar, false); r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, .5f);
        r.anchoredPosition = new Vector2(x - 50f, 0f); r.sizeDelta = new Vector2(w, h);
        var i = r.GetComponent<UnityEngine.UI.Image>(); i.color = c; i.raycastTarget = false;
        return r;
    }
    void LateUpdate()
    {
        if (!bar) return;
        float dt = Time.deltaTime, v = Mathf.Clamp01(health / maxHealth);
        bar.position = transform.position + Vector3.up * top;
        if (Camera.main) bar.rotation = Quaternion.LookRotation(bar.position - Camera.main.transform.position);   // always faces you
        ghost = v > ghost ? v : Time.time > ghostWait ? Mathf.MoveTowards(ghost, v, dt * .6f) : ghost;
        fill.sizeDelta = new Vector2(100f * v, 10f); ghostFill.sizeDelta = new Vector2(100f * ghost, 10f);
        barFade.alpha = Mathf.MoveTowards(barFade.alpha, health > 0f && health < maxHealth ? 1f : 0f, dt * 4f);
    }
    void OnDestroy() { if (bar) Destroy(bar.gameObject); }
    public void Take(float amount)
    {
        if (health <= 0f) return;
        health -= amount; flash = 1f; ghostWait = Time.time + .4f;
        Debug.Log($"[Damage] {name} took {amount:0} ({Mathf.Max(0, health):0}/{maxHealth:0} left)", this);
        ShardlightHUD.HitMarker(health <= 0f);
        if (golem) golem.SendMessage("BlockGrab", 6f, SendMessageOptions.DontRequireReceiver);   // hurt golems can't grab you for a while
        if (health <= 0f) StartCoroutine(Break());
        else if (golem) StartCoroutine(Flinch());
    }
    IEnumerator Flinch()   // golem plays its "Hit" animation (if the controller has one) and stops for a moment
    {
        var anim = golem.GetComponentInChildren<Animator>();
        bool has = false;
        if (anim && anim.runtimeAnimatorController) foreach (var p in anim.parameters) has |= p.name == "Hit";
        if (has) anim.SetTrigger("Hit");
        else Debug.LogWarning($"[Damage] {name}: no 'Hit' trigger on its Animator ({(anim && anim.runtimeAnimatorController ? anim.runtimeAnimatorController.name : "no controller")}). Rebuild with Tools > Golem > Build Golem Controller.", this);
        var agent = GetComponent<UnityEngine.AI.NavMeshAgent>();
        if (!agent || !agent.enabled || agent.speed <= 0f) yield break;
        float speed = agent.speed; agent.speed = 0f; agent.velocity = Vector3.zero;
        yield return new WaitForSeconds(.3f);   // stays put for as long as the Hit animation plays (max 6 s)
        for (float t = 0f; t < 6f && anim && (anim.GetCurrentAnimatorStateInfo(0).IsName("Hit") || anim.GetNextAnimatorStateInfo(0).IsName("Hit")); t += Time.deltaTime) yield return null;
        if (agent && agent.speed == 0f) agent.speed = speed;
    }
    void Update()   // quick red flash when hit
    {
        if (flash <= 0f) return;
        flash = Mathf.MoveTowards(flash, 0f, Time.deltaTime * 6f);
        foreach (var r in rends)
        {
            if (!r) continue;
            if (flash <= 0f) { r.SetPropertyBlock(null); continue; }
            var c = Color.Lerp(Color.white, new Color(1f, .25f, .2f), flash);
            mpb.SetColor("_BaseColor", c); mpb.SetColor("_Color", c); r.SetPropertyBlock(mpb);
        }
    }
    IEnumerator Break()
    {
        var b = new Bounds(transform.position, Vector3.one * .5f);
        foreach (var r in rends) if (r && r.enabled) b.Encapsulate(r.bounds);
        var m = rends.Length > 0 && rends[0] ? rends[0].sharedMaterial : null;
        Color col = m && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m && m.HasProperty("_Color") ? m.color : Color.grey;
        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { color = col };
        float size = Mathf.Clamp(b.size.magnitude * .12f, .05f, .5f);
        for (int i = 0; i < 10; i++)   // rubble
        {
            var t = GameObject.CreatePrimitive(PrimitiveType.Cube).transform; t.name = "Rubble";
            t.position = b.center + Vector3.Scale(UnityEngine.Random.insideUnitSphere, b.extents * .8f); t.rotation = UnityEngine.Random.rotation; t.localScale = Vector3.one * size * UnityEngine.Random.Range(.6f, 1.3f);
            t.GetComponent<Renderer>().sharedMaterial = mat;
            t.gameObject.AddComponent<Rigidbody>().linearVelocity = UnityEngine.Random.insideUnitSphere * 3f + Vector3.up * 2f;
            Destroy(t.gameObject, 5f);
        }
        if (!golem) { Destroy(gameObject); yield break; }
        // golem crumbles, then pulls itself back together at home
        var agent = GetComponent<UnityEngine.AI.NavMeshAgent>(); var cols = GetComponentsInChildren<Collider>();
        foreach (var a in GetComponentsInChildren<ItemHit>()) { a.transform.SetParent(null, true); var rb = a.GetComponent<Rigidbody>(); if (rb) rb.isKinematic = false; }   // arrows drop out
        golem.enabled = false; if (agent) agent.enabled = false;
        foreach (var r in rends) if (r) r.enabled = false;
        foreach (var c in cols) if (c) c.enabled = false;
        yield return new WaitForSeconds(reformTime);
        if (agent) agent.enabled = true;
        foreach (var r in rends) if (r) r.enabled = true;
        foreach (var c in cols) if (c) c.enabled = true;
        golem.enabled = true; golem.ResetToHome(); health = maxHealth;
    }
}

/// Makes an arrow, or a held item swung in VR, deal damage when it hits something.
public class ItemHit : MonoBehaviour
{
    public float damage = 25f, minSpeed = 2.5f; public bool arrow;
    float next, speed, born = -1f; Vector3 last; bool started;
    // Checked every frame against golem bodies, so it works without colliders and while a VR hand holds it
    // (held items move kinematically, which gives no collision events or speed).
    void Update()
    {
        if (arrow)   // walk over a spent arrow to take it back
        {
            if (born < 0f) born = Time.time;
            var cam = Camera.main;
            Vector3 d = cam ? transform.position - cam.transform.position : Vector3.one * 99f; d.y *= .5f;
            if (transform.position.y < -50f || (Time.time - born > 1f && !transform.parent && d.magnitude < 1.6f))
            {
                WristInventory.AddArrows(1); if (transform.position.y >= -50f) ShardlightHUD.PlayChime(7);
                Destroy(gameObject); return;
            }
        }
        if (!started) { started = true; last = transform.position; return; }
        Vector3 now = transform.position;
        speed = Time.deltaTime > 0f ? Vector3.Distance(now, last) / Time.deltaTime : 0f;
        if (next > Time.time || (!arrow && speed < minSpeed)) { last = now; return; }
        foreach (var g in FindObjectsByType<GolemGuardAI>(FindObjectsSortMode.None))
        {
            if (!g.isActiveAndEnabled) continue;
            var b = Damageable.Body(g.gameObject); b.Expand(.1f);
            for (int k = 0; k <= 4; k++)
            {
                if (!b.Contains(Vector3.Lerp(last, now, k / 4f))) continue;
                if (arrow)   // hit once, stick in place
                {
                    next = float.MaxValue; transform.position = Vector3.Lerp(last, now, k / 4f); transform.SetParent(g.transform, true);   // rides along with the golem
                    var rb = GetComponent<Rigidbody>(); if (rb) rb.isKinematic = true;
                }
                else next = Time.time + .5f;   // VR swing: one hit per swing
                last = now;
                Damageable.HitObject(g.gameObject, damage);
                Debug.Log((arrow ? "[Arrow] hit " : "[Swing] hit ") + g.name, g);
                return;
            }
        }
        last = now;
    }
    void OnCollisionEnter(Collision c)
    {
        if (arrow && Time.time >= next) Debug.Log($"[Arrow] hit {c.collider.name}", c.collider);
        if (Time.time < next || Mathf.Max(c.relativeVelocity.magnitude, speed) < minSpeed || ShardlightHUD.IsPlayer(c.collider) || c.collider.GetComponentInParent<GolemGuardAI>()) return;   // golems are handled in Update
        next = arrow ? float.MaxValue : Time.time + .3f;   // arrows hit once, then stick
        Damageable.Hit(c.collider, damage);
        c.collider.SendMessageUpwards("OnItemHit", arrow ? "bow" : "melee", SendMessageOptions.DontRequireReceiver);
        if (arrow) GetComponent<Rigidbody>().isKinematic = true;
    }
}