#if UNITY_EDITOR
using System;
using System.Linq;
using Project.HandTracking;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

public static class VRGameplaySetup
{
    public static int RepairHandVisuals(GameObject root)
    {
        const string source = "Assets/Samples/XR Interaction Toolkit/3.5.1/Hands Interaction Demo/Prefabs/";
        foreach (var visualizer in root.GetComponentsInChildren<UnityEngine.XR.Hands.Samples.VisualizerSample.HandVisualizer>(true))
        {
            var serialized = new SerializedObject(visualizer);
            foreach (var entry in new[] {
                new[] { "m_MetaQuestLeftHandMesh", "LeftHandQuestVisual.prefab" },
                new[] { "m_MetaQuestRightHandMesh", "RightHandQuestVisual.prefab" },
                new[] { "m_AndroidXRLeftHandMesh", "LeftHandAndroidXRVisual.prefab" },
                new[] { "m_AndroidXRRightHandMesh", "RightHandAndroidXRVisual.prefab" } })
            {
                var property = serialized.FindProperty(entry[0]);
                var old = property.objectReferenceValue as GameObject;
                if (!old) continue;
                var oldDriver = old.GetComponent<UnityEngine.XR.Hands.XRHandSkeletonDriver>();
                var oldMesh = old.GetComponent<UnityEngine.XR.Hands.XRHandMeshController>();
                if (oldDriver && oldDriver.rootTransform && oldMesh && oldMesh.handMeshRenderer) continue;
                var template = AssetDatabase.LoadAssetAtPath<GameObject>(source + entry[1]);
                if (!template) throw new InvalidOperationException("Original hand prefab missing: " + entry[1]);
                var restored = (GameObject)PrefabUtility.InstantiatePrefab(template, old.transform.parent);
                restored.name = old.name + " Restored";
                restored.transform.localPosition = old.transform.localPosition;
                restored.transform.localRotation = old.transform.localRotation;
                restored.transform.localScale = old.transform.localScale;
                var driver = restored.GetComponent<UnityEngine.XR.Hands.XRHandSkeletonDriver>();
                var mesh = restored.GetComponent<UnityEngine.XR.Hands.XRHandMeshController>();
                if (!driver || !driver.rootTransform || driver.jointTransformReferences.Count < 20 ||
                    driver.jointTransformReferences.Any(j => !j.jointTransform) || !mesh || !mesh.handMeshRenderer)
                    throw new InvalidOperationException("Original hand visual is incomplete: " + entry[1]);
                // Preserve the scene's poke-interactor hookup while retaining the source's bone/mesh links.
                foreach (var oldComponent in old.GetComponents<MonoBehaviour>())
                {
                    if (!oldComponent) continue;
                    var oldData = new SerializedObject(oldComponent);
                    var poke = oldData.FindProperty("m_PokeInteractorObject");
                    if (poke == null) continue;
                    var replacement = restored.GetComponent(oldComponent.GetType());
                    if (!replacement) continue;
                    var newData = new SerializedObject(replacement);
                    var newPoke = newData.FindProperty("m_PokeInteractorObject");
                    if (newPoke != null) { newPoke.objectReferenceValue = poke.objectReferenceValue; newData.ApplyModifiedPropertiesWithoutUndo(); }
                }
                old.SetActive(false);
                restored.SetActive(true);
                property.objectReferenceValue = restored;
                Debug.Log("[Hands] Restored " + entry[1] + " with " + driver.jointTransformReferences.Count + " valid joints and the original hand mesh.");
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.RecordPrefabInstancePropertyModifications(visualizer);
        }
        // Start all alternatives hidden; HandVisualizer activates exactly one platform pair.
        foreach (var visualizer in root.GetComponentsInChildren<UnityEngine.XR.Hands.Samples.VisualizerSample.HandVisualizer>(true))
        {
            var data = new SerializedObject(visualizer);
            foreach (string field in new[] { "m_MetaQuestLeftHandMesh", "m_MetaQuestRightHandMesh", "m_AndroidXRLeftHandMesh", "m_AndroidXRRightHandMesh" })
            {
                var visual = data.FindProperty(field).objectReferenceValue as GameObject;
                if (visual) { visual.SetActive(false); PrefabUtility.RecordPrefabInstancePropertyModifications(visual); }
            }
        }
        // These meshes have no blend shapes; the legacy feedback receiver otherwise logs every frame.
        foreach (var receiver in root.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.AffordanceSystem.Receiver.Rendering.BlendShapeAffordanceReceiver>(true))
        {
            var mesh = receiver.skinnedMeshRenderer ? receiver.skinnedMeshRenderer.sharedMesh : null;
            if (!mesh || receiver.blendShapeIndex < 0 || receiver.blendShapeIndex >= mesh.blendShapeCount)
            { receiver.enabled = false; EditorUtility.SetDirty(receiver); PrefabUtility.RecordPrefabInstancePropertyModifications(receiver); }
        }
        int removed = 0;
        foreach (var driver in root.GetComponentsInChildren<UnityEngine.XR.Hands.XRHandSkeletonDriver>(true))
        {
            var valid = driver.jointTransformReferences.Where(j => j.jointTransform != null).ToList();
            removed += driver.jointTransformReferences.Count - valid.Count;
            if (valid.Count != driver.jointTransformReferences.Count)
            {
                driver.jointTransformReferences = valid;
                EditorUtility.SetDirty(driver);
                PrefabUtility.RecordPrefabInstancePropertyModifications(driver);
            }
        }
        foreach (var visualizer in root.GetComponentsInChildren<UnityEngine.XR.Hands.Samples.VisualizerSample.HandVisualizer>(true))
        {
            visualizer.enabled = true;
            visualizer.drawMeshes = true;
            EditorUtility.SetDirty(visualizer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(visualizer);
        }
        Debug.Log("[Hands] Removed " + removed + " missing bone references; original hand mesh rendering enabled.");
        return removed;
    }
    [MenuItem("Tools/Shardlight/Apply and save VR gameplay setup")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("VR setup must be applied in Edit Mode.");
        var prefabRoot = PrefabUtility.LoadPrefabContents("Assets/Forest/Explorer Player.prefab");
        try { RepairHandVisuals(prefabRoot); PrefabUtility.SaveAsPrefabAsset(prefabRoot, "Assets/Forest/Explorer Player.prefab"); }
        finally { PrefabUtility.UnloadPrefabContents(prefabRoot); }
        var active = SceneManager.GetActiveScene();
        int saved = 0;
        foreach (var buildScene in EditorBuildSettings.scenes.Where(s => s.enabled && s.path.StartsWith("Assets/GAME SCENES/") && !s.path.EndsWith("SampleScene.unity")))
        {
            var scene = SceneManager.GetSceneByPath(buildScene.path);
            bool wasLoaded = scene.IsValid() && scene.isLoaded;
            if (!wasLoaded) scene = EditorSceneManager.OpenScene(buildScene.path, OpenSceneMode.Additive);
            try
            {
                var roots = scene.GetRootGameObjects();
                var rig = roots.SelectMany(r => r.GetComponentsInChildren<XROrigin>(true)).FirstOrDefault(r => r.Camera);
                if (!rig) throw new InvalidOperationException("XR rig not found: " + scene.path);
                RepairHandVisuals(rig.gameObject);
                var provider = rig.GetComponentInChildren<TeleportationProvider>(true);
                if (!provider) throw new InvalidOperationException("Teleportation provider not found: " + scene.path);
                var menus = roots.SelectMany(r => r.GetComponentsInChildren<ShardlightMenus>(true)).FirstOrDefault(m => !m.isMainMenu);
                if (!menus) throw new InvalidOperationException("Gameplay menus not found: " + scene.path);
                var watch = menus.GetComponent<WristInventory>() ?? menus.gameObject.AddComponent<WristInventory>();
                watch.enabled = true;
                watch.wristLift = .012f; watch.wristScale = .00045f;
                watch.gazeAngle = 16; watch.dwellSeconds = .65f; watch.hideDelay = .45f;
                watch.pinchStart = .025f; watch.pinchRelease = .04f;
                watch.dragStep = .065f; watch.vrInventoryRotation = 90;
                watch.exampleItemsInEditor = menus.exampleInventoryInEditor;
                var teleport = menus.GetComponent<LeftTeleportAim>() ?? menus.gameObject.AddComponent<LeftTeleportAim>();
                teleport.enabled = true; teleport.rig = rig; teleport.teleportation = provider; teleport.controllerTeleport = false;
                EditorUtility.SetDirty(watch); EditorUtility.SetDirty(teleport);
                PrefabUtility.RecordPrefabInstancePropertyModifications(watch);
                PrefabUtility.RecordPrefabInstancePropertyModifications(teleport);
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Failed to save " + scene.path);
                saved++;
                Debug.Log("[VR setup] Saved sideways watch/pinch controls and left pinch teleport: " + scene.path);
            }
            finally { if (!wasLoaded) EditorSceneManager.CloseScene(scene, true); }
        }
        if (active.IsValid() && active.isLoaded) SceneManager.SetActiveScene(active);
        AssetDatabase.SaveAssets();
        Debug.Log("[VR setup] PASS: saved " + saved + " gameplay scenes in Edit Mode.");
    }
}
#endif
