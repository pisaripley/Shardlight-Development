using System.Collections;
using UnityEngine;

/// Takes a small photo of the player (e.g. "what the golem saw") into a texture any UI can show.
/// Creates itself when first used. Until the player has a body model, it adds a stand-in figure (hooded lamplighter
/// with a lantern) that ONLY the photo camera can see, which follows you and leans/jumps with your movement.
/// When your real model exists: add this component to any object and drag the model into Body Model.
public class PlayerSnapshot : MonoBehaviour
{
    [Tooltip("The player rig. Empty = the object tagged Player.")] public Transform target;
    [Tooltip("Your real player model. Empty = use the stand-in figure.")] public Transform bodyModel;
    [Tooltip("Aim this high above the floor (chest/face).")] public float targetHeight = 1.2f;
    [Tooltip("How far in front of the player the photo is taken. It moves closer if a golem or wall is in the way.")] public float distance = 2.2f;
    [Tooltip("Degrees to the side of straight-on, for a more dynamic three-quarter shot.")] public float sideAngle = 35f;
    [Range(10, 90)] public float fieldOfView = 40f;
    public int size = 128;
    [Tooltip("A layer only the photo camera sees (the stand-in lives here). Pick an unused one.")] [Range(8, 31)] public int photoOnlyLayer = 31;

    public static PlayerSnapshot Instance { get; private set; }
    public RenderTexture Photo { get; private set; }
    Camera cam;
    Transform standIn, lean;
    Vector3 lastPos, vel;

    public static PlayerSnapshot Get() => Instance ? Instance : new GameObject("PlayerSnapshot").AddComponent<PlayerSnapshot>();

    void Awake()
    {
        if (Instance && Instance != this) { Destroy(this); return; }
        Instance = this;
        Photo = new RenderTexture(size, size, 16);
        cam = new GameObject("Snapshot Camera").AddComponent<Camera>();
        cam.transform.SetParent(transform, false);
        cam.enabled = false;                                   // only renders for the one frame of a photo
        cam.targetTexture = Photo;
        cam.stereoTargetEye = StereoTargetEyeMask.None;        // never draws into the VR headset
        cam.fieldOfView = fieldOfView;
        cam.nearClipPlane = .05f;
        cam.cullingMask = ~(1 << 5);                           // everything except UI (stand-in layer included)
    }

    /// Photograph the player, front three-quarter view. 'from' (e.g. the golem's eyes) picks which side the shot is taken from.
    public Texture Take(Vector3 from)
    {
        if (!Find()) return Photo;
        Vector3 at = Feet() + Vector3.up * targetHeight;
        Transform body = bodyModel ? bodyModel : standIn ? standIn : target;
        Vector3 fwd = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
        if (fwd.sqrMagnitude < .01f) fwd = Vector3.forward;
        float side = Vector3.Dot(from - at, Vector3.Cross(Vector3.up, fwd)) >= 0f ? sideAngle : -sideAngle;   // lean toward the spotter's side
        Vector3 to = (Quaternion.Euler(0f, side, 0f) * fwd * distance + Vector3.up * .35f);
        // golem, wall or pillar in the way? slide in closer so you're never hidden behind it
        if (Physics.Raycast(at, to.normalized, out RaycastHit hit, to.magnitude, ~(1 << photoOnlyLayer), QueryTriggerInteraction.Ignore))
            to = to.normalized * Mathf.Max(.7f, hit.distance - .15f);
        cam.transform.position = at + to;
        cam.transform.LookAt(at);
        StopAllCoroutines();
        StartCoroutine(Shoot());
        return Photo;
    }

    IEnumerator Shoot() { cam.enabled = true; yield return null; cam.enabled = false; }

    bool Find()
    {
        if (!target) { var p = GameObject.FindWithTag("Player"); if (p) { target = p.transform; lastPos = Feet(); } }
        return target;
    }

    // In VR you walk around inside the rig, so the feet are under the headset, not at the rig's origin.
    Vector3 Feet()
    {
        var c = Camera.main;
        return c && c.transform.IsChildOf(target) ? new Vector3(c.transform.position.x, target.position.y, c.transform.position.z) : target.position;
    }

    void LateUpdate()
    {
        if (!Find() || Time.deltaTime <= 0f) return;
        if (Camera.main) Camera.main.cullingMask &= ~(1 << photoOnlyLayer);   // you never see the stand-in yourself
        Vector3 feet = Feet();
        vel = Vector3.Lerp(vel, (feet - lastPos) / Time.deltaTime, .2f); lastPos = feet;
        if (bodyModel) { if (standIn) standIn.gameObject.SetActive(false); return; }
        if (!standIn) BuildStandIn();
        standIn.gameObject.SetActive(true);

        // follow you, face where you're going (or looking), lean into speed, tuck when airborne
        Vector3 flat = new Vector3(vel.x, 0f, vel.z);
        Vector3 face = flat.sqrMagnitude > .2f ? flat : Camera.main ? Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up) : standIn.forward;
        if (face.sqrMagnitude > .001f) standIn.rotation = Quaternion.Slerp(standIn.rotation, Quaternion.LookRotation(face), Time.deltaTime * 10f);
        standIn.position = feet;
        float run = Mathf.Clamp01(flat.magnitude / 5f), air = Mathf.Clamp(vel.y / 5f, -1f, 1f);
        lean.localRotation = Quaternion.Euler(run * 18f - air * 8f, 0f, Mathf.Sin(Time.time * 9f) * 4f * run);
    }

    void BuildStandIn()
    {
        standIn = new GameObject("Player Stand-in (photo only)").transform;
        lean = new GameObject("Lean").transform; lean.SetParent(standIn, false);
        Color cloak = new Color(.16f, .2f, .3f), skin = new Color(.85f, .68f, .55f), lantern = new Color(1f, .82f, .42f);
        Part(PrimitiveType.Capsule, new Vector3(0f, .8f, 0f), new Vector3(.5f, .8f, .4f), cloak);          // body + cloak
        Part(PrimitiveType.Sphere, new Vector3(0f, 1.55f, 0f), Vector3.one * .3f, skin);                    // head
        Part(PrimitiveType.Sphere, new Vector3(0f, 1.6f, -.04f), new Vector3(.36f, .36f, .36f), cloak);     // hood
        Part(PrimitiveType.Capsule, new Vector3(.3f, 1f, .1f), new Vector3(.14f, .32f, .14f), cloak).localRotation = Quaternion.Euler(-30f, 0f, 0f);   // lantern arm
        Part(PrimitiveType.Sphere, new Vector3(.34f, .68f, .3f), Vector3.one * .16f, lantern, true);       // the lamplighter's glowing lantern
    }

    Transform Part(PrimitiveType type, Vector3 pos, Vector3 scale, Color color, bool glow = false)
    {
        var go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());                  // invisible to golem sight and physics
        go.layer = photoOnlyLayer;
        go.transform.SetParent(lean, false);
        go.transform.localPosition = pos; go.transform.localScale = scale;
        var sh = Shader.Find(glow ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit") ?? Shader.Find(glow ? "Unlit/Color" : "Standard");
        var mat = new Material(sh) { color = color };
        go.GetComponent<Renderer>().sharedMaterial = mat;
        return go.transform;
    }

    void OnDestroy() { if (Photo) Photo.Release(); if (standIn) Destroy(standIn.gameObject); if (Instance == this) Instance = null; }
}