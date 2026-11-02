using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Unity.XR.CoreUtils;
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
                    var look = mouse.delta.ReadValue() * mouseSensitivity;
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
            float speed = keyboard != null && keyboard.leftShiftKey.isPressed ? sprintSpeed : walkSpeed;
            var velocity = (transform.right * movement.x + transform.forward * movement.y) * speed;
            bool grounded = body.isGrounded;
            if (grounded && verticalSpeed < 0f) verticalSpeed = -2f;

            if (grounded) airVelocity = velocity;                                  // remember how fast you're running
            else velocity = Vector3.Lerp(airVelocity, velocity, airControl);       // in the air: momentum + a little steering

            if (grounded && Cursor.lockState == CursorLockMode.Locked && keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
            {
                float h = Mathf.Min(jumpHeight + airVelocity.magnitude * 0.04f, maxJumpHeight);   // sprinting adds a bit, capped
                verticalSpeed = Mathf.Sqrt(2f * -Physics.gravity.y * h);
            }

            verticalSpeed += Physics.gravity.y * Time.deltaTime;
            var hit = body.Move((velocity + Vector3.up * verticalSpeed) * Time.deltaTime);
            if ((hit & CollisionFlags.Above) != 0 && verticalSpeed > 0f) verticalSpeed = 0f;     // bonk on ceilings
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
