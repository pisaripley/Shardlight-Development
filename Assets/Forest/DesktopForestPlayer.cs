using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

namespace Project.Forest
{
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

        void Start()
        {
            if (XRSettings.isDeviceActive) return;
            var origin = GetComponentInChildren<XROrigin>(true);
            if (origin == null || origin.Camera == null) { enabled = false; return; }
            body = GetComponent<CharacterController>();
            view = origin.Camera.transform;
            // Tracking and XR locomotion must not overwrite the keyboard camera pose.
            foreach (var behaviour in GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour != this &&
                    behaviour.GetType().Name != "UniversalAdditionalCameraData")
                    behaviour.enabled = false;
            foreach (var modality in GetComponentsInChildren<XRInputModalityManager>(true))
                foreach (var visual in new[] { modality.leftHand, modality.rightHand,
                             modality.leftController, modality.rightController })
                    if (visual != null) visual.SetActive(false);
            if (origin.CameraFloorOffsetObject != null)
                origin.CameraFloorOffsetObject.transform.localPosition = Vector3.zero;
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

        void Update()
        {
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