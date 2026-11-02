#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using Project.Forest;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem.XR;

/// Runs on an isolated prefab instance; never edits or saves the open scene.
public static class PlayerModeChecks
{
    [MenuItem("Tools/Shardlight/Validate XR fallback")]
    public static void Run()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before running this check.");
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Forest/Explorer Player.prefab");
            var clone = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var player = clone.GetComponent<DesktopForestPlayer>();
            var rig = clone.GetComponent<XROrigin>();
            var pose = rig.Camera.GetComponent<TrackedPoseDriver>();
            Require(pose && pose.enabled, "Head camera pose driver must be enabled in the original prefab.");
            var gameplay = new GameObject("Gameplay probe");
            gameplay.transform.SetParent(clone.transform, false);
            var inventory = gameplay.AddComponent<WristInventory>();
            var states = new Dictionary<Behaviour, bool>();
            foreach (var component in clone.GetComponentsInChildren<MonoBehaviour>(true))
                if (component) states.Add(component, component.enabled);
            var objects = new Dictionary<GameObject, bool>();
            foreach (var child in clone.GetComponentsInChildren<Transform>(true)) objects.Add(child.gameObject, child.gameObject.activeSelf);
            var cameraPosition = rig.Camera.transform.localPosition;
            var cameraRotation = rig.Camera.transform.localRotation;
            var offset = rig.CameraFloorOffsetObject.transform.localPosition;
            var rootRotation = clone.transform.rotation;
            Invoke(player, "Start");
            if (!player.DesktopActive) Invoke(player, "EnterDesktopMode");
            Require(player.DesktopActive && !pose.enabled, "Desktop must suspend the head pose driver.");
            Require(inventory.enabled, "Desktop must keep gameplay components enabled.");
            Invoke(player, "RestoreXRMode");
            Require(!player.DesktopActive && pose.enabled, "XR must restore the head pose driver.");
            foreach (var pair in states) Require(pair.Key.enabled == pair.Value, "Component state changed: " + pair.Key.GetType().Name);
            foreach (var pair in objects) Require(pair.Key.activeSelf == pair.Value, "Object state changed: " + pair.Key.name);
            Require(Vector3.Distance(rig.Camera.transform.localPosition, cameraPosition) < .0001f, "Camera position was not restored.");
            Require(Quaternion.Angle(rig.Camera.transform.localRotation, cameraRotation) < .01f, "Camera rotation was not restored.");
            Require(Vector3.Distance(rig.CameraFloorOffsetObject.transform.localPosition, offset) < .0001f, "Tracking origin offset was not restored.");
            Require(Quaternion.Angle(clone.transform.rotation, rootRotation) < .01f, "Mode switch changed player facing.");
            // Render-space and interaction distances must be physical metres even with a quarter-size rig.
            Set(inventory, "view", rig.Camera);
            Set(inventory, "origin", rig);
            Set(inventory, "vr", true);
            Call(inventory, "Build");
            Call(inventory, "Hide");
            var watchRoot = (RectTransform)Get(inventory, "root");
            var selector = (RectTransform)Get(inventory, "selector");
            var face = (RectTransform)Get(inventory, "watchFace");
            Require(!inventory.IsOpen && face, "Watch must have a persistent face with the selector initially closed.");
            Require(Mathf.Abs(Mathf.DeltaAngle(selector.localEulerAngles.z, 90)) < .01f, "Inventory must open sideways.");
            foreach (float scale in new[] { 1f, .25f })
            {
                clone.transform.localScale = Vector3.one * scale;
                Vector3 wrist = rig.CameraFloorOffsetObject.transform.TransformPoint(new Vector3(-.2f, 1f, .4f));
                Call(inventory, "PlaceWrist", wrist, Vector3.forward, Vector3.up);
                Require(Mathf.Abs(watchRoot.lossyScale.x / scale - inventory.wristScale) < .00001f, "Watch size must compensate for XR origin scale.");
                float height = Vector3.Dot(watchRoot.position - wrist, Vector3.up) / scale;
                Require(Mathf.Abs(height - inventory.wristLift) < .0001f, "Watch offset must compensate for XR origin scale.");
                Require(Vector3.Dot(watchRoot.up, Vector3.forward) > .99f, "Watch orientation must follow the forearm.");
            }
            Set(inventory, "dragAxis", Vector3.right);
            var stationaryPinch = watchRoot.position + Vector3.right * .02f;
            float initialDrag = (float)Result(inventory, "DragPosition", stationaryPinch);
            selector.localRotation = Quaternion.Euler(0, 0, 94);
            Require(Mathf.Abs((float)Result(inventory, "DragPosition", stationaryPinch) - initialDrag) < .00001f, "Wrist rotation must not generate phantom scrolling.");
            Vector3 shift = new Vector3(10, 0, 3);
            watchRoot.position += shift;
            Require(Mathf.Abs((float)Result(inventory, "DragPosition", stationaryPinch + shift) - initialDrag) < .0001f, "Moving both hands together must not scroll.");
            Require(inventory.dragStep >= .065f, "Scroll threshold must reject small hand jitter.");
            Set(inventory, "gazeTime", 0f);
            for (int frame = 0; frame < 6; frame++)
                Require(!(bool)Result(inventory, "AdvanceOpenDwell", true, .1f), "Watch must not open before the deliberate dwell.");
            Require((bool)Result(inventory, "AdvanceOpenDwell", true, .1f), "A sustained gaze must open the watch.");
            Set(inventory, "gazeTime", 0f);
            Require(!(bool)Result(inventory, "AdvanceOpenDwell", false, .1f), "No gaze must leave the watch closed.");
            Require(selector.localPosition.z < -100, "Inventory must pop out above the wrist and hand mesh.");
            Require(!selector.GetComponentInChildren<UnityEngine.UI.RectMask2D>(true), "Rotated VR carousel must not clip its icons with RectMask2D.");
            Require(Mathf.Abs(selector.rect.width * selector.localScale.x * inventory.wristScale - .297f) < .0001f, "Expanded inventory must be 30cm wide.");
            foreach (var driver in clone.GetComponentsInChildren<UnityEngine.XR.Hands.XRHandSkeletonDriver>(true).Where(d => d.gameObject.activeInHierarchy))
                foreach (var joint in driver.jointTransformReferences)
                    Require(joint.jointTransform, "Missing bone would crash the hand visualizer: " + joint.xrHandJointID);
            var visualizer = clone.GetComponentInChildren<UnityEngine.XR.Hands.Samples.VisualizerSample.HandVisualizer>(true);
            var visualData = new SerializedObject(visualizer);
            foreach (string field in new[] { "m_MetaQuestLeftHandMesh", "m_MetaQuestRightHandMesh", "m_AndroidXRLeftHandMesh", "m_AndroidXRRightHandMesh" })
            {
                var visual = (GameObject)visualData.FindProperty(field).objectReferenceValue;
                var skeleton = visual.GetComponent<UnityEngine.XR.Hands.XRHandSkeletonDriver>();
                Require(!visual.activeSelf, "Platform hand alternatives must start hidden: " + field);
                Require(skeleton.rootTransform && skeleton.jointTransformReferences.Count >= 20, "Hand skeleton missing: " + field);
                Require(visual.GetComponent<UnityEngine.XR.Hands.XRHandMeshController>().handMeshRenderer, "Hand mesh missing: " + field);
            }
            foreach (string item in new[] { "sword", "bow", "bomb", "torch", "key", "potion", "rune" })
                Require(Inventory.IconFor(item, item), "Missing imported sprite: " + item);
            Debug.Log("[VR interaction regression] PASS: sideways watch, gaze dwell, physical watch size at 1x and 0.25x scale, and all seven imported icons.");
            Debug.Log("[XR regression] PASS: head pose, hand/controller components, visuals, camera pose and gameplay survive desktop -> VR restoration.");
        }
        finally { EditorSceneManager.ClosePreviewScene(scene); }
    }
    static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    static object Get(object target, string field) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
    static object Result(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    static void Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
    static void Invoke(DesktopForestPlayer player, string name) => typeof(DesktopForestPlayer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(player, null);
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("[XR regression] " + message); }
}
#endif
