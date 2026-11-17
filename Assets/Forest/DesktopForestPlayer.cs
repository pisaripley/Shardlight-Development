using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Unity.XR.CoreUtils;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using UnityEngine.XR.Hands;
using UnityEngine.Playables;
using UnityEngine.Animations;

namespace Project.Forest
{
    // A loader can be initialized before isDeviceActive becomes true (headset wake / Link startup).
    // Share this decision with the HUD, menus and wrist inventory.
    public static class PlayerRuntimeMode
    {
        public static bool VRActive
        {
            get
            {
                if (XRSettings.isDeviceActive) return true;
                var settings = XRGeneralSettings.Instance;
                return settings != null && settings.Manager != null && settings.Manager.activeLoader != null;
            }
        }
    }

    // Desktop fallback for the copied XR player. The forest itself is saved scene content.
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(CharacterController))]
    public sealed class DesktopForestPlayer : MonoBehaviour
    {
        public float walkSpeed = 3f;
        public float sprintSpeed = 5f;
        public float mouseSensitivity = 0.12f;
        public float eyeHeight = 1.7f;
        public bool DesktopActive { get; private set; }
        CharacterController body;
        Transform view;
        float pitch;
        float verticalSpeed;
        public float jumpHeight = 1f;          // standing jump, metres
        public float maxJumpHeight = 1.2f;     // hard cap, even when sprinting
        [Range(0f, 1f)] public float airControl = 0.3f;   // how much you can steer mid-air
        Vector3 airVelocity;                   // run speed carried into the jump

        [Header("Stamina (full bar = 1)")] public float sprintDrain = .2f, staminaRegen = .5f, staminaDelay = .5f, jumpCost = .12f;   // drain/regen per second; regen starts after the delay
        [Tooltip("Degrees the view zooms in while aiming.")] public float aimZoom = 15f;
        [Header("Feel (desktop only)")] [Range(0f, 2f)] public float headBob = 1f;   // 0 = off
        public float sprintFov = 8f; [Range(0f, 1f)] public float sprintVignette = .35f;
        [Range(0f, .9f)] public float lookSmoothing = .5f; [Range(0f, 1f)] public float motionBlur = .3f;   // 0 = off
        Vector2 look; Volume blurVolume; bool savedPost;
        float bob, dip, fov, sprint, aim; bool wasGrounded = true; Camera cam; UnityEngine.UI.Image edges;

        [Header("Climbing")]
        [Tooltip("Desktop: walk into a wall and keep holding W. VR: make a fist (or squeeze grip) on any wall and pull.")]
        public bool climbing = true;
        public float climbSpeed = 1.6f, climbDrain = .12f, leapCost = .2f;   // stamina per second / per leap
        [Tooltip("What can be climbed. Golems, loose props and the player never are.")] public LayerMask climbLayers = ~0;
        [Header("VR comfort")]
        [Tooltip("Darkens the edges of your view while you move, turn, climb or fall (not while walking in your room). 0 = off.")]
        [Range(0f, 1f)] public float vrVignette = .6f;
        bool onWall, climbHinted; float wallPush, leap, mantle;
        [Header("Prayer & spirit")]
        [Tooltip("Optional: a character model (e.g. a Mixamo FBX with skin) shown kneeling in 3rd person while you pray. Empty = a simple stand-in figure.")]
        public GameObject prayerModel;
        [Tooltip("Optional: a kneeling / praying animation for that model.")] public AnimationClip prayerClip;
        [Tooltip("Spirit gained per second of prayer, and lost per second otherwise.")] public float spiritFill = .2f, spiritFade = .004f;
        [Tooltip("VR: hold this controller button to pray (Input System path). Default = B on the right controller. Palms/controllers together in front of your chest also works.")]
        public string vrPrayButton = "<XRController>{RightHand}/secondaryButton";
        InputAction prayIn; bool prayByButton;
        bool praying, prayHinted; float prayBlend, prayOrbit, prayHold; Vector3 prayForward; GameObject prayFigure; PlayableGraph prayGraph;

        readonly List<Behaviour> disabledXR = new List<Behaviour>();
        readonly Dictionary<GameObject, bool> visuals = new Dictionary<GameObject, bool>();
        XROrigin rig;
        Vector3 savedCameraPosition, savedFloorOffset, savedCenter;
        Quaternion savedCameraRotation;
        float savedHeight, savedRadius, savedStepOffset, savedSkinWidth, savedMinMove;
        bool savedBodyEnabled;

        void Start()
        {
            rig = GetComponentInChildren<XROrigin>(true);
            if (rig == null || rig.Camera == null) { enabled = false; return; }
            body = GetComponent<CharacterController>();
            view = rig.Camera.transform;
            if (!PlayerRuntimeMode.VRActive) EnterDesktopMode();
        }

        void EnterDesktopMode()
        {
            savedCameraPosition = view.localPosition;
            savedCameraRotation = view.localRotation;
            if (rig.CameraFloorOffsetObject)
                savedFloorOffset = rig.CameraFloorOffsetObject.transform.localPosition;
            savedBodyEnabled = body.enabled;
            savedHeight = body.height;
            savedRadius = body.radius;
            savedCenter = body.center;
            savedStepOffset = body.stepOffset;
            savedSkinWidth = body.skinWidth;
            savedMinMove = body.minMoveDistance;
            // Only suspend XR components; keep gameplay scripts and the mode detector alive.
            foreach (var behaviour in GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!behaviour || !behaviour.enabled || behaviour == this) continue;
                string ns = behaviour.GetType().Namespace ?? "";
                if (ns.StartsWith("UnityEngine.XR.Interaction.Toolkit") ||
                    ns.StartsWith("UnityEngine.XR.Hands") ||
                    ns.StartsWith("UnityEngine.InputSystem.XR") ||
                    ns.StartsWith("Unity.XR.CoreUtils") ||
                    ns == "Project.HandTracking")
                {
                    disabledXR.Add(behaviour);
                    behaviour.enabled = false;
                }
            }
            foreach (var modality in GetComponentsInChildren<XRInputModalityManager>(true))
                foreach (var visual in new[] { modality.leftHand, modality.rightHand,
                             modality.leftController, modality.rightController })
                    if (visual && !visuals.ContainsKey(visual))
                    {
                        visuals.Add(visual, visual.activeSelf);
                        visual.SetActive(false);
                    }
            if (rig.CameraFloorOffsetObject)
                rig.CameraFloorOffsetObject.transform.localPosition = Vector3.zero;
            view.localPosition = Vector3.up * eyeHeight;
            view.localRotation = Quaternion.identity;
            body.enabled = false;
            body.height = 1.8f;
            body.radius = 0.25f;
            body.center = Vector3.up * 0.9f;
            body.stepOffset = 0.3f;
            body.skinWidth = 0.025f;
            body.minMoveDistance = 0f;
            body.enabled = true;
            cam = view.GetComponent<Camera>(); if (cam) fov = cam.fieldOfView;
            Edges();
            if (motionBlur > 0f && cam)   // URP camera blur: only smears while the view moves
            {
                var data = cam.GetUniversalAdditionalCameraData(); savedPost = data.renderPostProcessing; data.renderPostProcessing = true;
                blurVolume = new GameObject("Look Blur").AddComponent<Volume>(); blurVolume.isGlobal = true; blurVolume.priority = 50;
                blurVolume.profile.Add<MotionBlur>(true).intensity.value = motionBlur;
            }
            DesktopActive = true;
        }

        void RestoreXRMode()
        {
            DesktopActive = false;
            ReleaseCursor();
            body.enabled = false;
            body.height = savedHeight;
            body.radius = savedRadius;
            body.center = savedCenter;
            body.stepOffset = savedStepOffset;
            body.skinWidth = savedSkinWidth;
            body.minMoveDistance = savedMinMove;
            body.enabled = savedBodyEnabled;
            if (rig.CameraFloorOffsetObject)
                rig.CameraFloorOffsetObject.transform.localPosition = savedFloorOffset;
            view.localPosition = savedCameraPosition;
            view.localRotation = savedCameraRotation;
            if (cam) cam.fieldOfView = fov;
            if (edges) Destroy(edges.canvas.gameObject);
            if (blurVolume) { Destroy(blurVolume.gameObject); cam.GetUniversalAdditionalCameraData().renderPostProcessing = savedPost; }   // never blur in VR
            foreach (var pair in visuals)
                if (pair.Key) pair.Key.SetActive(pair.Value);
            visuals.Clear();
            foreach (var behaviour in disabledXR)
                if (behaviour) behaviour.enabled = true;
            disabledXR.Clear();
            Debug.Log("[Player] XR became available; restored hand/controller tracking and locomotion.", this);
        }
        void Update()
        {
            if (DesktopActive && PlayerRuntimeMode.VRActive) RestoreXRMode();
            if (!DesktopActive) { if (PlayerRuntimeMode.VRActive) VRUpdate(); return; }
            if (!Application.isFocused) return;
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            { ReleaseCursor(); return; }
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            Vector2 movement = Vector2.zero;
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                if (mouse != null)
                {
                    look = Vector2.Lerp(look, mouse.delta.ReadValue() * mouseSensitivity, 1f - Mathf.Pow(lookSmoothing, Time.deltaTime * 60f));   // a little weight to the head
                    transform.Rotate(0f, look.x, 0f, Space.World);
                    pitch = Mathf.Clamp(pitch - look.y, -80f, 80f);
                    view.localRotation = Quaternion.Euler(pitch, 0f, 0f);
                }
                if (keyboard != null)
                {
                    movement.x = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
                    movement.y = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
                }
            }
            movement = Vector2.ClampMagnitude(movement, 1f);
            if (keyboard != null && Cursor.lockState == CursorLockMode.Locked && keyboard.rKey.wasPressedThisFrame && body.isGrounded && !onWall) SetPraying(!praying);   // R: kneel and pray
            if (praying && (movement.sqrMagnitude > .01f || (keyboard != null && keyboard.spaceKey.wasPressedThisFrame))) SetPraying(false);   // move to stand
            SpiritTick(Time.deltaTime);
            if (praying || prayBlend > 0f) { PrayCam(); return; }
            bool sprint = keyboard != null && keyboard.leftShiftKey.isPressed && movement.sqrMagnitude > .01f && !ShardlightHUD.Exhausted && !ShardlightHUD.Aiming;
            float speed = (sprint ? sprintSpeed : walkSpeed) * (ShardlightHUD.Aiming ? .55f : 1f);   // slower while aiming
            var velocity = (transform.right * movement.x + transform.forward * movement.y) * speed;
            bool grounded = body.isGrounded;
            if (grounded && verticalSpeed < 0f) verticalSpeed = -2f;
            if (sprint && grounded) ShardlightHUD.UseStamina(sprintDrain * Time.deltaTime);
            else if (Time.time - ShardlightHUD.StaminaUsedAt > staminaDelay) ShardlightHUD.Stamina = Mathf.Min(1f, ShardlightHUD.Stamina + staminaRegen * Mathf.Lerp(.6f, 1.4f, ShardlightHUD.Spirit) * Time.deltaTime);   // more spirit, faster recovery
            if (ShardlightHUD.Stamina > .3f) ShardlightHUD.Exhausted = false;
            if (Climb(movement, keyboard, ref velocity)) { body.Move(velocity * Time.deltaTime); verticalSpeed = 0f; wasGrounded = false; Feel(0f, false, false); return; }

            if (grounded) airVelocity = velocity;                                  // remember how fast you're running
            else velocity = Vector3.Lerp(airVelocity, velocity, airControl);       // in the air: momentum + a little steering

            if (grounded && Cursor.lockState == CursorLockMode.Locked && keyboard != null && keyboard.spaceKey.wasPressedThisFrame && ShardlightHUD.UseStamina(jumpCost))
            {
                float h = Mathf.Min(jumpHeight + airVelocity.magnitude * 0.04f, maxJumpHeight);   // sprinting adds a bit, capped
                verticalSpeed = Mathf.Sqrt(2f * -Physics.gravity.y * h);
            }

            verticalSpeed += Physics.gravity.y * Time.deltaTime;
            var hit = body.Move((velocity + Vector3.up * verticalSpeed) * Time.deltaTime);
            if ((hit & CollisionFlags.Above) != 0 && verticalSpeed > 0f) verticalSpeed = 0f;     // bonk on ceilings
            if (body.isGrounded && !wasGrounded) dip = .05f;       // knees bend on landing
            wasGrounded = body.isGrounded;
            Feel(new Vector2(velocity.x, velocity.z).magnitude, body.isGrounded, speed > walkSpeed);
        }

        // ---------------------------------------------------------------- climbing (desktop)
        // Push into a steep wall for a moment to grab on. W/S climb, A/D shuffle, Space leaps up, S at the bottom lets go.
        // Reaching the top pulls you over the edge. Uses stamina; run out and you fall.
        bool Climb(Vector2 move, Keyboard kb, ref Vector3 velocity)
        {
            if (!climbing) return false;
            float dt = Time.deltaTime;
            if (mantle > 0f) { mantle -= dt; velocity = Vector3.up * 3.5f + transform.forward * 2f; return true; }   // pull yourself over the top
            bool wall = Physics.Raycast(transform.position + Vector3.up * 1.1f, transform.forward, out var hit, body.radius + .4f, climbLayers, QueryTriggerInteraction.Ignore)
                        && hit.normal.y < .4f && Climbable(hit.collider);
            if (!onWall)
            {
                wallPush = wall && move.y > .5f && !ShardlightHUD.Exhausted ? wallPush + dt : 0f;
                if (wallPush < .25f) return false;
                onWall = true; leap = 0f;
                if (!climbHinted) { climbHinted = true; ShardlightHUD.ShowHint("Climbing uses {stamina}. {Space} leaps up, {S} climbs down.", 4f); }
            }
            if (!wall || (move.y < -.5f && body.isGrounded)) { onWall = false; return false; }
            bool top = !Physics.Raycast(transform.position + Vector3.up * (eyeHeight + .1f), transform.forward, body.radius + .6f, climbLayers, QueryTriggerInteraction.Ignore);
            if (top && move.y > 0f) { onWall = false; mantle = .45f; return true; }
            if (kb != null && kb.spaceKey.wasPressedThisFrame && ShardlightHUD.UseStamina(leapCost)) leap = .3f;
            leap -= dt;
            if (!ShardlightHUD.UseStamina(climbDrain * dt * (move.sqrMagnitude > .01f ? 1f : .4f)))   // hanging still costs less
            { onWall = false; ShardlightHUD.ShowHint("Too tired to hold on!", 2f); return false; }
            var side = Vector3.ProjectOnPlane(transform.right, hit.normal).normalized;
            velocity = Vector3.up * (leap > 0f ? 5f : move.y * climbSpeed) + side * move.x * climbSpeed * .7f - hit.normal * .5f;
            return true;
        }
        static bool Climbable(Collider c) => c && !c.isTrigger && !c.attachedRigidbody && !ShardlightHUD.IsPlayer(c) && !c.GetComponentInParent<GolemGuardAI>();

        // ---------------------------------------------------------------- VR: climbing + comfort vignette
        // Make a fist (hand tracking) or squeeze grip (controllers) with a hand touching a wall, then pull: you move, the wall doesn't.
        // Gravity is paused while you hang on. Edges of the view darken during artificial motion only.
        readonly List<XRHandSubsystem> handSystems = new List<XRHandSubsystem>();
        readonly bool[] wasGrab = new bool[2]; readonly Vector3[] handPos = new Vector3[2];
        int climbHand = -1; Vector3 anchor, lastRigPos; float lastYaw, vrEdge; bool rigInit;
        InputAction gripL, gripR; UnityEngine.UI.Image vrEdges; readonly List<Behaviour> gravity = new List<Behaviour>();

        void VRUpdate()
        {
            float dt = Time.deltaTime;
            if (climbing)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (!Hand(i == 0, out var p, out bool g)) { if (climbHand == i) climbHand = -1; wasGrab[i] = false; continue; }
                    if (g && !wasGrab[i] && Touching(p))
                    {
                        climbHand = i; anchor = p;
                        if (!climbHinted) { climbHinted = true; ShardlightHUD.ShowHint("Pull yourself up. Climbing uses {stamina}.", 4f); }
                    }
                    if (!g && climbHand == i) climbHand = -1;
                    wasGrab[i] = g; handPos[i] = p;
                }
                if (climbHand >= 0 && !ShardlightHUD.UseStamina(climbDrain * dt)) { climbHand = -1; ShardlightHUD.ShowHint("Too tired to hold on!", 2f); }
                if (climbHand >= 0)
                {
                    Vector3 shift = anchor - handPos[climbHand];   // keep the gripping hand where it grabbed
                    var mover = rig.GetComponent<CharacterController>(); if (!mover) mover = body;
                    if (mover && mover.enabled) mover.Move(shift); else rig.transform.position += shift;
                }
                Gravity(climbHand < 0);
            }
            if (climbHand < 0 && Time.time - ShardlightHUD.StaminaUsedAt > staminaDelay) ShardlightHUD.Stamina = Mathf.Min(1f, ShardlightHUD.Stamina + staminaRegen * Mathf.Lerp(.6f, 1.4f, ShardlightHUD.Spirit) * dt);
            if (ShardlightHUD.Stamina > .3f) ShardlightHUD.Exhausted = false;
            // VR prayer: press your palms together in front of your chest for a second. You stay in first person
            // (pulling the camera out of your head in VR causes motion sickness); the view fills with soft gold instead.
            bool hl = Hand(true, out var lp, out _), hr = Hand(false, out var rp, out _);
            Vector3 head = view.position;
            bool palms = hl && hr && Vector3.Distance(lp, rp) < .12f && lp.y < head.y - .1f && lp.y > head.y - .7f && Vector3.Dot(lp - head, view.forward) > .1f;
            if (prayIn == null && !string.IsNullOrEmpty(vrPrayButton)) { prayIn = new InputAction("Pray", InputActionType.Button, vrPrayButton); prayIn.Enable(); }
            bool button = prayIn != null && prayIn.IsPressed();
            prayHold = palms || button ? prayHold + dt : 0f;
            if (!praying && prayHold > (button ? .5f : 1f)) { SetPraying(true); prayByButton = button && !palms; }
            if (praying && (prayByButton ? !button : !hl || !hr || Vector3.Distance(lp, rp) > .25f)) SetPraying(false);   // let go of the button / part your hands
            SpiritTick(dt);
            ComfortVignette(dt);
        }

        // ---------------------------------------------------------------- prayer
        void SpiritTick(float dt)
        {
            ShardlightHUD.Spirit = Mathf.Clamp01(ShardlightHUD.Spirit + (praying ? spiritFill : -spiritFade) * dt);
            if (praying) { ShardlightHUD.Heal(.03f * dt); ShardlightHUD.Stamina = Mathf.Min(1f, ShardlightHUD.Stamina + staminaRegen * dt); }   // rest: a little healing, stamina back
        }
        void SetPraying(bool on)
        {
            if (on == praying) return;
            praying = on; ShardlightHUD.Praying = on;
            if (!on) return;
            if (!prayHinted) { prayHinted = true; ShardlightHUD.ShowHint(DesktopActive ? "You kneel in prayer. Your {spirit} is restored. Move to stand." : "You pray. Your {spirit} is restored. Let go to stop.", 4f); }
            if (!DesktopActive) return;
            prayForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized; prayOrbit = 0f;
            if (!prayFigure) prayFigure = Kneeler();
            prayFigure.transform.SetPositionAndRotation(transform.position, Quaternion.LookRotation(prayForward));
            prayFigure.SetActive(false);
        }
        // Desktop: the view eases out of your head to a slow orbit around you, kneeling; then eases back when you stand.
        void PrayCam()
        {
            float dt = Time.deltaTime;
            prayBlend = Mathf.MoveTowards(prayBlend, praying ? 1f : 0f, dt * 1.25f);
            float b = Mathf.SmoothStep(0f, 1f, prayBlend);
            prayOrbit += dt * 6f;
            Vector3 focus = transform.position + Vector3.up * .8f, eye = transform.position + Vector3.up * eyeHeight;
            Vector3 want = focus + Quaternion.Euler(0f, 145f + prayOrbit, 0f) * prayForward * 2.6f + Vector3.up * .9f;   // in front and to the side
            if (Physics.Linecast(focus, want, out var hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) want = hit.point + (focus - want).normalized * .2f;   // don't go through walls
            view.position = Vector3.Lerp(eye, want, b);
            view.rotation = Quaternion.Slerp(transform.rotation * Quaternion.Euler(pitch, 0f, 0f), Quaternion.LookRotation(focus - view.position), b);
            if (cam) cam.fieldOfView = fov;
            if (edges) edges.color = Color.clear;
            if (prayFigure) prayFigure.SetActive(b > .35f);
            if (!praying && prayBlend <= 0f)   // back in your head
            {
                if (prayFigure) Destroy(prayFigure);
                if (prayGraph.IsValid()) prayGraph.Destroy();
                view.localPosition = Vector3.up * eyeHeight; view.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
        }
        GameObject Kneeler()
        {
            if (prayerModel)
            {
                var g = Instantiate(prayerModel);
                foreach (var c in g.GetComponentsInChildren<Collider>()) Destroy(c);
                var anim = g.GetComponentInChildren<Animator>();
                if (anim && prayerClip) { anim.applyRootMotion = false; AnimationPlayableUtilities.PlayClip(anim, prayerClip, out prayGraph); }
                return g;
            }
            var root = new GameObject("Praying (stand-in)");   // hooded figure on both knees, head bowed, hands together
            Color cloak = new Color(.16f, .2f, .3f), skin = new Color(.85f, .68f, .55f);
            Limb(root, PrimitiveType.Capsule, new Vector3(0f, .86f, 0f), new Vector3(.42f, .34f, .3f), new Vector3(8f, 0f, 0f), cloak);       // body
            Limb(root, PrimitiveType.Sphere, new Vector3(0f, 1.27f, .07f), Vector3.one * .23f, Vector3.zero, skin);                             // bowed head
            Limb(root, PrimitiveType.Sphere, new Vector3(0f, 1.31f, .02f), Vector3.one * .27f, Vector3.zero, cloak);                            // hood
            foreach (float x in new[] { -.11f, .11f })
            {
                Limb(root, PrimitiveType.Capsule, new Vector3(x, .33f, 0f), new Vector3(.16f, .2f, .16f), Vector3.zero, cloak);                // thigh, upright
                Limb(root, PrimitiveType.Capsule, new Vector3(x, .08f, -.22f), new Vector3(.14f, .2f, .14f), new Vector3(90f, 0f, 0f), cloak); // shin on the ground
                Limb(root, PrimitiveType.Capsule, new Vector3(x * .8f, .98f, .17f), new Vector3(.09f, .13f, .09f), new Vector3(-60f, 0f, x > 0 ? 25f : -25f), cloak);   // forearm
            }
            Limb(root, PrimitiveType.Sphere, new Vector3(0f, 1.06f, .25f), new Vector3(.1f, .13f, .08f), Vector3.zero, skin);                  // hands together
            return root;
        }
        static void Limb(GameObject root, PrimitiveType type, Vector3 pos, Vector3 scale, Vector3 euler, Color color)
        {
            var t = GameObject.CreatePrimitive(type).transform;
            Destroy(t.GetComponent<Collider>());
            t.SetParent(root.transform, false); t.localPosition = pos; t.localScale = scale; t.localRotation = Quaternion.Euler(euler);
            t.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard")) { color = color };
        }

        /// Where a hand/controller is (world) and whether it's a fist (or grip squeezed). Also used by the HUD's safety pose.
        public bool Hand(bool left, out Vector3 pos, out bool grab)
        {
            pos = default; grab = false;
            if (!rig) return false;
            SubsystemManager.GetSubsystems(handSystems);
            foreach (var s in handSystems)   // hand tracking: a fist grabs (a pinch is left for the watch and bow)
            {
                var h = left ? s.leftHand : s.rightHand;
                if (!s.running || !h.isTracked || !h.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm)) continue;
                var space = rig.CameraFloorOffsetObject ? rig.CameraFloorOffsetObject.transform : rig.transform;
                pos = space.TransformPoint(palm.position);
                float curl = 0f; int n = 0;
                foreach (var id in new[] { XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.RingTip })
                    if (h.GetJoint(id).TryGetPose(out var tip)) { curl += Vector3.Distance(tip.position, palm.position); n++; }
                grab = n > 0 && curl / n < .065f;
                return true;
            }
            var m = rig.GetComponentInChildren<XRInputModalityManager>();   // controllers: grip button
            var ctrl = m ? (left ? m.leftController : m.rightController) : null;
            if (!ctrl || !ctrl.activeInHierarchy) return false;
            if (gripL == null)
            {
                gripL = new InputAction("Climb L", InputActionType.Button, "<XRController>{LeftHand}/gripPressed"); gripL.AddBinding("<XRController>{LeftHand}/{GripButton}");
                gripR = new InputAction("Climb R", InputActionType.Button, "<XRController>{RightHand}/gripPressed"); gripR.AddBinding("<XRController>{RightHand}/{GripButton}");
                gripL.Enable(); gripR.Enable();
            }
            pos = ctrl.transform.position; grab = (left ? gripL : gripR).IsPressed();
            return true;
        }
        bool Touching(Vector3 p)
        {
            foreach (var c in Physics.OverlapSphere(p, .08f, climbLayers, QueryTriggerInteraction.Ignore))
                if (Climbable(c) && !(c is CharacterController) && !c.transform.IsChildOf(transform) && !c.transform.IsChildOf(rig.transform)) return true;   // never your own body
            return false;
        }
        void Gravity(bool on)   // XRI gravity would pull you off the wall
        {
            if (gravity.Count == 0) foreach (var b in rig.GetComponentsInChildren<Behaviour>(true)) if (b.GetType().Name == "GravityProvider") gravity.Add(b);
            foreach (var b in gravity) if (b) b.enabled = on;
        }

        void ComfortVignette(float dt)
        {
            if (!vrEdges)
            {
                var cv = new GameObject("Comfort Vignette", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
                cv.renderMode = RenderMode.WorldSpace; cv.sortingOrder = 30000;
                var rt = (RectTransform)cv.transform; rt.SetParent(view, false);
                rt.localPosition = Vector3.forward * .3f; rt.localRotation = Quaternion.identity; rt.sizeDelta = Vector2.one * 100f; rt.localScale = Vector3.one * .008f;   // 0.8 m square, 0.3 m from the eyes
                vrEdges = new GameObject("Edges", typeof(RectTransform)).AddComponent<UnityEngine.UI.Image>();
                vrEdges.transform.SetParent(rt, false);
                vrEdges.rectTransform.anchorMin = Vector2.zero; vrEdges.rectTransform.anchorMax = Vector2.one; vrEdges.rectTransform.sizeDelta = Vector2.zero;
                vrEdges.sprite = Ring(); vrEdges.raycastTarget = false; vrEdges.color = Color.clear;
                var mat = new Material(Shader.Find("UI/Default")); mat.SetFloat("unity_GUIZTestMode", 8f); vrEdges.material = mat;   // always on top
            }
            Vector3 pos = rig.transform.position; float yaw = rig.transform.eulerAngles.y;
            if (!rigInit) { rigInit = true; lastRigPos = pos; lastYaw = yaw; }
            float move = (pos - lastRigPos).magnitude, turn = Mathf.Abs(Mathf.DeltaAngle(lastYaw, yaw));
            lastRigPos = pos; lastYaw = yaw;
            if (move > 1.5f) move = 0f;   // teleports and snap turns are instant, so they don't need it
            if (turn > 20f) turn = 0f;
            float target = dt > 0f ? vrVignette * Mathf.Clamp01(Mathf.Max(move / dt / 3f, turn / dt / 90f)) : 0f;
            vrEdge = Mathf.MoveTowards(vrEdge, target, dt * (target > vrEdge ? 6f : 2f));   // closes in fast, opens slowly
            vrEdges.color = new Color(0f, 0f, 0f, vrEdge);
        }
        static Sprite Ring()
        {
            var t = new Texture2D(32, 32) { wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < 1024; i++)
                t.SetPixel(i % 32, i / 32, new Color(0f, 0f, 0f, Mathf.Clamp01(new Vector2(i % 32 - 15.5f, i / 32 - 15.5f).magnitude / 16f - .5f) * 2f));
            t.Apply();
            return Sprite.Create(t, new Rect(0, 0, 32, 32), Vector2.one * .5f);
        }

        // Footstep bump, side sway, small roll; sprinting widens the view and darkens the edges.
        void Feel(float spd, bool grounded, bool sprinting)
        {
            aim = Mathf.MoveTowards(aim, ShardlightHUD.Aiming ? 1f : 0f, Time.deltaTime * 5f);
            float dt = Time.deltaTime, amp = (grounded ? headBob * Mathf.Clamp01(spd / walkSpeed) : 0f) * (1f - .7f * aim);   // steadier while aiming
            sprint = Mathf.MoveTowards(sprint, sprinting && grounded && spd > .1f ? 1f : 0f, dt * 3f);
            bob += spd * dt * 3f; dip = Mathf.MoveTowards(dip, 0f, dt * .3f);
            var offset = new Vector3(Mathf.Sin(bob) * .015f, Mathf.Abs(Mathf.Cos(bob)) * .025f - dip * headBob, 0f) * (1f + sprint * .5f);
            view.localPosition = Vector3.Lerp(view.localPosition, Vector3.up * eyeHeight + offset * amp, dt * 12f);
            view.localRotation = Quaternion.Euler(pitch, 0f, -Mathf.Sin(bob) * .5f * amp);
            if (cam) cam.fieldOfView = fov + sprintFov * sprint - aimZoom * Mathf.SmoothStep(0f, 1f, aim);
            if (edges) edges.color = new Color(0f, 0f, 0f, sprintVignette * sprint);
        }

        void Edges()   // soft black ring, drawn under the HUD
        {
            if (edges) return;
            var c = new GameObject("Sprint Vignette").AddComponent<Canvas>();
            c.transform.SetParent(transform, false); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = -10;
            edges = new GameObject("Edges").AddComponent<UnityEngine.UI.Image>();
            edges.transform.SetParent(c.transform, false);
            edges.rectTransform.anchorMin = Vector2.zero; edges.rectTransform.anchorMax = Vector2.one; edges.rectTransform.sizeDelta = Vector2.zero;
            edges.sprite = Ring(); edges.raycastTarget = false; edges.color = Color.clear;
        }

        void OnApplicationFocus(bool focused) { if (!focused && DesktopActive) ReleaseCursor(); }
        void OnDisable()   // menus pause the player: let go of walls, restore gravity, clear the vignette
        {
            if (DesktopActive) ReleaseCursor();
            gripL?.Disable(); gripR?.Disable(); prayIn?.Disable();
            if (climbHand >= 0) { climbHand = -1; Gravity(true); }
            if (vrEdges) { vrEdge = 0f; vrEdges.color = Color.clear; }
        }
        void OnEnable() { gripL?.Enable(); gripR?.Enable(); prayIn?.Enable(); rigInit = false; }
        static void ReleaseCursor() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        void OnGUI()
        {
            if (DesktopActive && Cursor.lockState != CursorLockMode.Locked)
                GUI.Box(new Rect(12, 12, 500, 28), "Click to explore | WASD move | Mouse look | Shift sprint | R pray | Esc cursor");
        }
    }
}