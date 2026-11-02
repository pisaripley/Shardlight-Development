#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// One-click builder for the golem's Animator Controller.
/// Menu: Tools > Golem > Build Golem Controller
/// Select the golem in the Hierarchy first and it will be assigned automatically.
public static class GolemControllerBuilder
{
    const string Folder = "Assets/Animations";
    const string SavePath = Folder + "/GolemController.controller";

    static readonly string[] LocoAvoid =
        { "turn", "left", "right", "back", "strafe", "start", "stop", "crouch", "injured",
          "jump", "attack", "death", "die", "hit", "combat", "battle", "damage" };
    static readonly string[] ActionAvoid = { "hit", "damage", "death", "die", "attack" };

    [MenuItem("Tools/Golem/Build Golem Controller")]
    static void Build()
    {
        GatherClips(out var giantClips, out var allClips);
        if (allClips.Count == 0)
        {
            EditorUtility.DisplayDialog("Golem Controller", "No animation clips found in the project.", "OK");
            return;
        }

        AnimationClip Pick(string[] keys, string[] avoid) =>
            Find(giantClips, keys, avoid) ?? Find(allClips, keys, avoid);

        var idle = Pick(new[] { "idle" }, Concat(LocoAvoid, "warrior"));
        var walk = Pick(new[] { "walk" }, LocoAvoid);
        var run  = Pick(new[] { "run", "sprint", "jog" }, LocoAvoid);
        var look = Pick(new[] { "lookaround", "look_around", "look around", "search", "look", "scan", "inspect" }, ActionAvoid);
        var peek = Pick(new[] { "alert", "suspicious", "cautious", "warrior idle", "warrior_idle", "combatidle", "combat_idle", "battleidle" }, ActionAvoid);
        if (!peek) peek = idle;
        var grab = Pick(new[] { "grab", "pickup", "pick_up", "pick up", "pick", "reach", "interact", "take" }, ActionAvoid);
        var shoo = Pick(new[] { "shoo", "dismiss", "shaking head", "head shake", "shake", "wave", "gesture", "taunt", "roar", "shout", "yell" }, ActionAvoid);

        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "Animations");
        if (AssetDatabase.LoadAssetAtPath<AnimatorController>(SavePath))
        {
            if (!EditorUtility.DisplayDialog("Golem Controller",
                "GolemController already exists. Replace it?", "Replace", "Cancel")) return;
            AssetDatabase.DeleteAsset(SavePath);
        }

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(SavePath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Suspicious", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("LookAround", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Grab", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Shoo", AnimatorControllerParameterType.Trigger);

        // IK Pass lets the script turn his head and lean his body (the "peek")
        var layers = ctrl.layers;
        layers[0].iKPass = true;
        ctrl.layers = layers;
        var sm = ctrl.layers[0].stateMachine;

        // Locomotion blend tree driven by Speed
        var loco = ctrl.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        if (idle) tree.AddChild(idle, 0f);
        if (walk) tree.AddChild(walk, 2.5f);
        if (run)  tree.AddChild(run, 6f);
        sm.defaultState = loco;

        // Peek: held while Suspicious is true
        if (peek)
        {
            var p = sm.AddState("Peek", new Vector3(300, 0, 0));
            p.motion = peek;
            var to = loco.AddTransition(p);
            to.AddCondition(AnimatorConditionMode.If, 0, "Suspicious");
            to.hasExitTime = false; to.duration = 0.3f;
            var back = p.AddTransition(loco);
            back.AddCondition(AnimatorConditionMode.IfNot, 0, "Suspicious");
            back.hasExitTime = false; back.duration = 0.3f;
        }

        // One-shot actions fired by triggers, returning to Locomotion
        AddAction(sm, loco, "LookAround", look, "LookAround", new Vector3(300, 80, 0));
        AddAction(sm, loco, "Grab", grab, "Grab", new Vector3(300, 160, 0));
        AddAction(sm, loco, "Shoo", shoo, "Shoo", new Vector3(300, 240, 0));

        AssetDatabase.SaveAssets();

        // Assign to the selected golem
        string assigned = "Nothing selected, so drag GolemController into the golem's Animator > Controller slot.";
        var go = Selection.activeGameObject;
        if (go)
        {
            var anim = go.GetComponent<Animator>();
            if (!anim) anim = go.GetComponentInParent<Animator>();
            if (!anim) anim = go.GetComponentInChildren<Animator>();
            if (anim)
            {
                Undo.RecordObject(anim, "Assign Golem Controller");
                anim.runtimeAnimatorController = ctrl;
                anim.applyRootMotion = false;
                EditorUtility.SetDirty(anim);

                var ai = anim.GetComponent("GolemGuardAI");
                if (ai)
                {
                    var so = new SerializedObject(ai);
                    var prop = so.FindProperty("animator");
                    if (prop != null && !prop.objectReferenceValue) { prop.objectReferenceValue = anim; so.ApplyModifiedProperties(); }
                }
                assigned = $"Assigned to '{anim.name}' (Apply Root Motion turned off).";
            }
        }

        var sb = new StringBuilder("GolemController built at " + SavePath + "\n\n");
        Line(sb, "Idle", idle); Line(sb, "Walk", walk); Line(sb, "Run", run);
        Line(sb, "Peek", peek); Line(sb, "LookAround", look); Line(sb, "Grab", grab); Line(sb, "Shoo", shoo);
        sb.Append("\n").Append(assigned);
        sb.Append("\n\nWrong clip picked? Open the controller, click the state, and drag a different clip into Motion.");
        Debug.Log(sb.ToString(), ctrl);
        EditorUtility.DisplayDialog("Golem Controller", sb.ToString(), "OK");
        Selection.activeObject = ctrl;
    }

    static void AddAction(AnimatorStateMachine sm, AnimatorState loco, string name, AnimationClip clip, string trigger, Vector3 pos)
    {
        if (!clip) return;
        var s = sm.AddState(name, pos);
        s.motion = clip;
        var t = sm.AddAnyStateTransition(s);
        t.AddCondition(AnimatorConditionMode.If, 0, trigger);
        t.canTransitionToSelf = false;
        t.hasExitTime = false;
        t.duration = 0.2f;
        var back = s.AddTransition(loco);
        back.hasExitTime = true;
        back.exitTime = 0.9f;
        back.duration = 0.25f;
    }

    static void GatherClips(out List<AnimationClip> giant, out List<AnimationClip> all)
    {
        giant = new List<AnimationClip>();
        all = new List<AnimationClip>();
        Labels.Clear();
        foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.StartsWith("Assets/")) continue;
            bool isGiant = path.ToLowerInvariant().Contains("giant");
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!(o is AnimationClip c) || c.name.StartsWith("__preview__") || all.Contains(c)) continue;
                all.Add(c);
                if (isGiant) giant.Add(c);
                // Search text = clip name + file name (Mixamo clips are all called "mixamo.com")
                Labels[c] = (c.name + " " + System.IO.Path.GetFileNameWithoutExtension(path)).ToLowerInvariant();
            }
        }
    }

    static readonly Dictionary<AnimationClip, string> Labels = new Dictionary<AnimationClip, string>();

    // Lower score wins: earlier keyword, no "avoid" words, shorter name
    static AnimationClip Find(List<AnimationClip> clips, string[] keys, string[] avoid)
    {
        AnimationClip best = null;
        float bestScore = float.MaxValue;
        foreach (var c in clips)
        {
            string n = Labels.TryGetValue(c, out var label) ? label : c.name.ToLowerInvariant();
            int k = System.Array.FindIndex(keys, key => n.Contains(key));
            if (k < 0) continue;
            float score = k * 10f + n.Length * 0.01f;
            foreach (var a in avoid) if (n.Contains(a)) score += 5f;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    static string[] Concat(string[] a, params string[] b)
    {
        var r = new string[a.Length + b.Length];
        a.CopyTo(r, 0); b.CopyTo(r, a.Length);
        return r;
    }

    static void Line(StringBuilder sb, string label, AnimationClip c) =>
        sb.Append(label).Append(": ").Append(c ? (Labels.TryGetValue(c, out var l) ? l : c.name) : "(none found - add one later)").Append("\n");
}
#endif