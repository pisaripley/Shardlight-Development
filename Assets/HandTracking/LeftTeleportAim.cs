using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace Project.HandTracking
{
    /// Left aim + fresh index pinch teleports to a clear, walkable surface.
    /// Works in gameplay scenes as well as the original assignment room.
    public sealed class LeftTeleportAim : MonoBehaviour
    {
        public AssignmentHub hub;
        public XROrigin rig;
        public TeleportationProvider teleportation;
        public bool controllerTeleport = true;
        public LayerMask landingLayers = ~0;
        public float maxDistance = 12f;
        public float maxSlope = 40f;
        InputAction position, rotation, tracking, teleport;
        LineRenderer line;
        GameObject marker;
        Material material;
        bool wasPinched, armed;
        float cooldown;
        readonly Vector3[] points = new Vector3[41];
        readonly List<XRHandSubsystem> hands = new List<XRHandSubsystem>();

        void Start()
        {
            if (hub) { rig = hub.rig; teleportation = hub.teleportation; }
            if (!rig && Camera.main) rig = Camera.main.GetComponentInParent<XROrigin>();
            if (!teleportation && rig) teleportation = rig.GetComponentInChildren<TeleportationProvider>(true);
            position = Action("Left teleport position", InputActionType.Value, "<XRController>{LeftHand}/devicePosition");
            rotation = Action("Left teleport rotation", InputActionType.Value, "<XRController>{LeftHand}/deviceRotation");
            tracking = Action("Left teleport tracking", InputActionType.Value, "<XRController>{LeftHand}/trackingState");
            teleport = Action("Left teleport", InputActionType.Button, "<XRController>{LeftHand}/primary2DAxisClick");
            material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.SetColor("_BaseColor", new Color(.3f, .85f, .75f));
            var obj = new GameObject("Left pinch teleport arc"); obj.transform.SetParent(transform, false);
            line = obj.AddComponent<LineRenderer>(); line.sharedMaterial = material; line.useWorldSpace = true;
            line.enabled = false;
            marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder); marker.name = "Teleport landing marker";
            Destroy(marker.GetComponent<Collider>());
            marker.GetComponent<Renderer>().sharedMaterial = material; marker.SetActive(false);
        }
        static InputAction Action(string name, InputActionType type, string binding)
        { var a = new InputAction(name, type, binding); a.Enable(); return a; }

        void Update()
        {
            if (!line) return;
            line.enabled = false; marker.SetActive(false);
            if (!rig || !rig.CameraFloorOffsetObject || !teleportation || !teleportation.isActiveAndEnabled ||
                !Project.Forest.PlayerRuntimeMode.VRActive || ShardlightMenus.IsOpen ||
                (WristInventory.Instance && WristInventory.Instance.IsOpen) ||
                (hub && (!hub.GameplayEnabled || (hub.settings && hub.settings.IsOpen))))
            { armed = wasPinched = false; return; }
            var space = rig.CameraFloorOffsetObject.transform;
            float scale = Mathf.Max(.001f, Mathf.Abs(space.lossyScale.x));
            bool tracked = TryHand(space, scale, out Vector3 start, out Vector3 forward, out bool pinch);
            bool pressed = false;
            if (tracked)
            {
                if (!pinch) armed = true;
                pressed = armed && pinch && !wasPinched;
                wasPinched = pinch;
            }
            else
            {
                armed = wasPinched = false;
                if (!controllerTeleport || (tracking.ReadValue<int>() & 3) != 3) return;
                start = space.TransformPoint(position.ReadValue<Vector3>());
                forward = space.TransformDirection(rotation.ReadValue<Quaternion>() * Vector3.forward);
                pressed = teleport.WasPressedThisFrame();
            }
            if (Vector3.Dot(forward, Vector3.up) > -.1f || Time.unscaledTime < cooldown) return;
            Vector3 velocity = forward * (5f * scale) + Vector3.up * (1.5f * scale);
            points[0] = start;
            int count = 1; bool valid = false; Vector3 landing = default;
            for (int i = 1; i < points.Length; i++)
            {
                float t = i * .06f;
                Vector3 next = start + velocity * t + Vector3.down * (4.9f * scale * t * t);
                Vector3 segment = next - points[count - 1];
                if (Physics.Raycast(points[count - 1], segment.normalized, out var hit, segment.magnitude, landingLayers, QueryTriggerInteraction.Ignore))
                {
                    // Skip colliders belonging to the player, including hand interactors.
                    if (hit.collider.transform.IsChildOf(rig.transform)) { points[count++] = next; continue; }
                    points[count++] = hit.point;
                    landing = hit.point;
                    valid = (!hub || hit.collider == hub.floor) && !hit.rigidbody &&
                        Vector3.Angle(hit.normal, Vector3.up) <= maxSlope &&
                        Vector3.Distance(start, landing) <= maxDistance * scale &&
                        ClearLanding(landing, scale);
                    break;
                }
                points[count++] = next;
            }
            if (!valid) return;
            line.enabled = true; line.widthMultiplier = .005f * scale; line.positionCount = count;
            for (int i = 0; i < count; i++) line.SetPosition(i, points[i]);
            marker.transform.localScale = new Vector3(.38f, .008f, .38f) * scale;
            marker.transform.position = landing + Vector3.up * (.008f * scale); marker.SetActive(true);
            if (!pressed) return;
            if (teleportation.QueueTeleportRequest(new TeleportRequest { destinationPosition = landing, matchOrientation = MatchOrientation.WorldSpaceUp }))
                cooldown = Time.unscaledTime + .6f;
        }
        bool ClearLanding(Vector3 point, float scale)
        {
            foreach (var collider in Physics.OverlapCapsule(point + Vector3.up * (.27f * scale),
                         point + Vector3.up * (1.5f * scale), .22f * scale, landingLayers, QueryTriggerInteraction.Ignore))
                if (!collider.transform.IsChildOf(rig.transform)) return false;
            return true;
        }
        bool TryHand(Transform space, float scale, out Vector3 point, out Vector3 forward, out bool pinch)
        {
            point = forward = default; pinch = false;
            var aim = MetaAimHand.left;
            if (aim != null && aim.added && aim.isTracked.isPressed)
            {
                var flags = (MetaAimFlags)aim.aimFlags.ReadValue();
                if ((flags & (MetaAimFlags.SystemGesture | MetaAimFlags.MenuPressed)) != 0) return false;
                if ((flags & MetaAimFlags.Valid) != 0)
                {
                    point = space.TransformPoint(aim.devicePosition.ReadValue());
                    forward = space.TransformDirection(aim.deviceRotation.ReadValue() * Vector3.forward);
                    pinch = aim.indexPressed.isPressed;
                    return true;
                }
            }
            SubsystemManager.GetSubsystems(hands);
            foreach (var system in hands)
            {
                var hand = system.leftHand;
                if (!system.running || !hand.isTracked ||
                    !hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out var knuckle) ||
                    !hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out var index) ||
                    !hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out var thumb)) continue;
                point = space.TransformPoint(knuckle.position);
                forward = space.TransformDirection((index.position - knuckle.position).normalized);
                float gap = Vector3.Distance(space.TransformPoint(index.position), space.TransformPoint(thumb.position)) / scale;
                pinch = gap < (wasPinched ? .04f : .025f);
                return true;
            }
            return false;
        }
        void OnDisable() { armed = wasPinched = false; if (line) line.enabled = false; if (marker) marker.SetActive(false); }
        void OnDestroy()
        {
            position?.Dispose(); rotation?.Dispose(); tracking?.Dispose(); teleport?.Dispose();
            if (marker) Destroy(marker); if (material) Destroy(material);
        }
    }
}
