using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Unity.XR.CoreUtils;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

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

        [Header("Stamina (full bar = 1)")] public float sprintDrain = .2f, staminaRegen = .25f, jumpCost = .08f;   // per second
        [Header("Feel (desktop only)")] [Range(0f, 2f)] public float headBob = 1f;   // 0 = off
        public float sprintFov = 8f; [Range(0f, 1f)] public float sprintVignette = .35f;
        [Range(0f, .9f)] public float lookSmoothing = .5f; [Range(0f, 1f)] public float motionBlur = .3f;   // 0 = off
        Vector2 look; Volume blurVolume; bool savedPost;
        float bob, dip, fov, sprint; bool wasGrounded = true; Camera cam; UnityEngine.UI.Image edges;

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
            if (!DesktopActive || !Application.isFocused) return;
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
            bool sprint = keyboard != null && keyboard.leftShiftKey.isPressed && movement.sqrMagnitude > .01f && !ShardlightHUD.Exhausted;
            float speed = sprint ? sprintSpeed : walkSpeed;
            var velocity = (transform.right * movement.x + transform.forward * movement.y) * speed;
            bool grounded = body.isGrounded;
            if (grounded && verticalSpeed < 0f) verticalSpeed = -2f;
            ShardlightHUD.Stamina = Mathf.Clamp01(ShardlightHUD.Stamina + (sprint && grounded ? -sprintDrain : staminaRegen) * Time.deltaTime);
            if (ShardlightHUD.Stamina <= 0f) ShardlightHUD.Exhausted = true; else if (ShardlightHUD.Stamina > .3f) ShardlightHUD.Exhausted = false;

            if (grounded) airVelocity = velocity;                                  // remember how fast you're running
            else velocity = Vector3.Lerp(airVelocity, velocity, airControl);       // in the air: momentum + a little steering

            if (grounded && Cursor.lockState == CursorLockMode.Locked && keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            {
                float h = Mathf.Min(jumpHeight + airVelocity.magnitude * 0.04f, maxJumpHeight);   // sprinting adds a bit, capped
                verticalSpeed = Mathf.Sqrt(2f * -Physics.gravity.y * h);
                ShardlightHUD.Stamina = Mathf.Max(0f, ShardlightHUD.Stamina - jumpCost);
            }

            verticalSpeed += Physics.gravity.y * Time.deltaTime;
            var hit = body.Move((velocity + Vector3.up * verticalSpeed) * Time.deltaTime);
            if ((hit & CollisionFlags.Above) != 0 && verticalSpeed > 0f) verticalSpeed = 0f;     // bonk on ceilings
            if (body.isGrounded && !wasGrounded) dip = .05f;       // knees bend on landing
            wasGrounded = body.isGrounded;
            Feel(new Vector2(velocity.x, velocity.z).magnitude, body.isGrounded, speed > walkSpeed);
        }

        // Footstep bump, side sway, small roll; sprinting widens the view and darkens the edges.
        void Feel(float spd, bool grounded, bool sprinting)
        {
            float dt = Time.deltaTime, amp = grounded ? headBob * Mathf.Clamp01(spd / walkSpeed) : 0f;
            sprint = Mathf.MoveTowards(sprint, sprinting && grounded && spd > .1f ? 1f : 0f, dt * 3f);
            bob += spd * dt * 3f; dip = Mathf.MoveTowards(dip, 0f, dt * .3f);
            var offset = new Vector3(Mathf.Sin(bob) * .015f, Mathf.Abs(Mathf.Cos(bob)) * .025f - dip * headBob, 0f) * (1f + sprint * .5f);
            view.localPosition = Vector3.Lerp(view.localPosition, Vector3.up * eyeHeight + offset * amp, dt * 12f);
            view.localRotation = Quaternion.Euler(pitch, 0f, -Mathf.Sin(bob) * .5f * amp);
            if (cam) cam.fieldOfView = fov + sprintFov * sprint;
            if (edges) edges.color = new Color(0f, 0f, 0f, sprintVignette * sprint);
        }

        void Edges()   // soft black ring, drawn under the HUD
        {
            if (edges) return;
            var c = new GameObject("Sprint Vignette").AddComponent<Canvas>();
            c.transform.SetParent(transform, false); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = -10;
            var t = new Texture2D(32, 32) { wrapMode = TextureWrapMode.Clamp };
            for (int i = 0; i < 1024; i++)
                t.SetPixel(i % 32, i / 32, new Color(0f, 0f, 0f, Mathf.Clamp01(new Vector2(i % 32 - 15.5f, i / 32 - 15.5f).magnitude / 16f - .5f) * 2f));
            t.Apply();
            edges = new GameObject("Edges").AddComponent<UnityEngine.UI.Image>();
            edges.transform.SetParent(c.transform, false);
            edges.rectTransform.anchorMin = Vector2.zero; edges.rectTransform.anchorMax = Vector2.one; edges.rectTransform.sizeDelta = Vector2.zero;
            edges.sprite = Sprite.Create(t, new Rect(0, 0, 32, 32), Vector2.one * .5f); edges.raycastTarget = false; edges.color = Color.clear;
        }

        void OnApplicationFocus(bool focused) { if (!focused && DesktopActive) ReleaseCursor(); }
        void OnDisable() { if (DesktopActive) ReleaseCursor(); }
        static void ReleaseCursor() { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        void OnGUI()
        {
            if (DesktopActive && Cursor.lockState != CursorLockMode.Locked)
                GUI.Box(new Rect(12, 12, 440, 28), "Click to explore | WASD move | Mouse look | Shift sprint | Esc cursor");
        }
    }
}