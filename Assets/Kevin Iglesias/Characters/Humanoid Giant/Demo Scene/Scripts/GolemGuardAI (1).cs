using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;
using UnityEngine.UI;

/// Castle-guard golem. Patrols guard points and looks around, peeks when suspicious, investigates and
/// searches, chases, then gently carries the player outside (fade, shoo, no damage). Keeps hunting while
/// the ruins are on alert, then walks home. Learns which routes the player uses (saved between sessions).
[RequireComponent(typeof(NavMeshAgent))]
public class GolemGuardAI : MonoBehaviour
{
    public enum State { Patrol, LookAround, Suspicious, Investigate, Search, Chase, Escort, ReturnHome }

    [Header("References")]
    public Transform player, playerHead, ejectPoint, castleGoal, shooSpot, homePoint;
    public Transform[] guardPoints = new Transform[0];
    public Animator animator;
    [Tooltip("XR move/turn providers to switch off while you're being carried out.")] public Behaviour[] disableWhileCaught;
    [Header("Senses")]
    public float viewDistance = 13f;
    [Range(10, 180)] public float viewAngle = 60f;
    public float hearingRadius = 7f, eyeHeight = 3f;
    public LayerMask sightBlockers = ~0;
    [Header("Behaviour")]
    public float patrolSpeed = 2.5f, investigateSpeed = 1.5f, chaseSpeed = 6f, grabRange = 2.5f, breachRadius = 4f;
    public float suspicionRate = 1.5f, suspicionDecay = .4f, giveUpTime = 6f, lookAroundTime = 3f, peekTime = 2.5f, searchTime = 5f, turnSpeed = 120f;
    [Range(0, 1)] public float suspiciousAt = .3f, investigateAt = .6f;
    [Header("Catch")]
    public float grabAnimTime = 1.5f, fadeTime = .6f, shooTime = 2.5f;
    public bool huntWhileWanted = true, returnHomeAfterCatch = true;
    [Header("Voice (optional 3D sounds)")]
    public AudioClip noticeClip, alarmClip, searchClip;
    [Header("Learning")]
    [Range(0, 1)] public float learnRate = .3f;
    public float exploreTemp = .6f;
    public UnityEvent onPlayerCaught, onPlayerEjected;

    public State CurrentState => state;
    public float Suspicion => suspicion;
    public bool IsHunting => hunting;
    public Vector3 Velocity => agent ? agent.velocity : Vector3.zero;
    public Vector3 LastKnownPosition => lastKnown;

    NavMeshAgent agent; AudioSource voice; CanvasGroup fader;
    State state; bool hunting, atHome, breached;
    float suspicion, lost, timer, duration, sample;
    Vector3 lastKnown, lastPos, vel, homePos, catchPos, lookPos;
    Quaternion homeRot;
    float[] weights;
    readonly Vector3[] route = new Vector3[12]; int routeN, routeHead, target = -1;
    float lookW;
    const string SaveKey = "GolemGuardWeights";

    Vector3 PlayerPos => playerHead ? new Vector3(playerHead.position.x, player.position.y, playerHead.position.z) : player.position;
    Vector3 PlayerEyes => playerHead ? playerHead.position : player.position + Vector3.up * 1.2f;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        if (!animator) animator = GetComponent<Animator>();
        if (animator) animator.applyRootMotion = false;
        if (!player) { var p = GameObject.FindWithTag("Player"); if (p) player = p.transform; }
        if (!playerHead && player && Camera.main && Camera.main.transform.IsChildOf(player)) playerHead = Camera.main.transform;
        voice = gameObject.AddComponent<AudioSource>(); voice.spatialBlend = 1f; voice.maxDistance = 35f; voice.rolloffMode = AudioRolloffMode.Linear;
        weights = new float[guardPoints.Length];
        var saved = PlayerPrefs.GetString(SaveKey, "").Split(',');
        if (saved.Length == weights.Length) for (int i = 0; i < saved.Length; i++) float.TryParse(saved[i], NumberStyles.Float, CultureInfo.InvariantCulture, out weights[i]);
        homePos = homePoint ? homePoint.position : transform.position;
        homeRot = homePoint ? homePoint.rotation : transform.rotation;
        if (player) lastPos = PlayerPos;
    }

    void Start() => Enter(State.Patrol);

    void Update()
    {
        timer += Time.deltaTime;
        if (!player || state == State.Escort) { Animate(); return; }

        // senses
        Vector3 pp = PlayerPos;
        vel = Vector3.Lerp(vel, (pp - lastPos) / Mathf.Max(Time.deltaTime, 1e-4f), .2f); lastPos = pp;
        if ((sample += Time.deltaTime) >= 1f) { sample = 0f; route[routeHead] = pp; routeHead = (routeHead + 1) % route.Length; routeN = Mathf.Min(routeN + 1, route.Length); }
        float dist = Vector3.Distance(transform.position, pp);
        bool seen = CanSee(), heard = dist <= hearingRadius * Mathf.Clamp(new Vector2(vel.x, vel.z).magnitude / 2.5f, 0f, 1.5f);   // creeping is quiet
        if (seen || heard)
        {
            suspicion += suspicionRate * WantedSystem.SuspicionMultiplier * (seen ? 1f : .5f) * Mathf.Lerp(2.5f, .6f, dist / viewDistance) * Time.deltaTime;
            if (seen) WantedSystem.ReportSeen();
            lastKnown = pp; lost = 0f;
        }
        else { lost += Time.deltaTime; if (state != State.Chase) suspicion -= suspicionDecay * Time.deltaTime; }
        suspicion = Mathf.Clamp01(suspicion);

        // transitions
        if (state == State.Chase) { if (lost > giveUpTime) { suspicion = investigateAt; Enter(State.Search); } }
        else if (suspicion >= 1f) Enter(State.Chase);
        else if (suspicion >= investigateAt && state != State.Investigate && state != State.Search) Enter(State.Investigate);
        else if (suspicion >= suspiciousAt && (state == State.Patrol || state == State.LookAround || state == State.ReturnHome)) Enter(State.Suspicious);
        if (hunting && !WantedSystem.IsActive && suspicion < suspiciousAt && state != State.Chase && state != State.Suspicious)
        { hunting = false; if (returnHomeAfterCatch) Enter(State.ReturnHome); }

        switch (state)
        {
            case State.Patrol: if (Arrived()) Enter(hunting ? State.Search : State.LookAround); break;
            case State.LookAround: if (timer >= duration) Enter(State.Patrol); break;
            case State.Suspicious: Face(lastKnown); if (timer >= peekTime) Enter(suspicion >= suspiciousAt ? State.Investigate : State.Patrol); break;
            case State.Investigate: if (seen || heard) Go(lastKnown); if (Arrived()) Enter(State.Search); break;
            case State.Search: if (timer >= searchTime) Enter(State.Patrol); break;
            case State.Chase:
                Vector3 guess = pp + vel * Mathf.Clamp(dist / chaseSpeed, 0f, 1.5f);   // cut you off
                Go(seen || heard ? (NavMesh.SamplePosition(guess, out NavMeshHit h, 3f, NavMesh.AllAreas) ? h.position : pp) : lastKnown);
                if (dist <= grabRange) StartCoroutine(Escort());
                break;
            case State.ReturnHome:
                if (!atHome && Arrived()) { atHome = true; Stop(); }
                if (atHome)
                {
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, homeRot, turnSpeed * Time.deltaTime);
                    if (Quaternion.Angle(transform.rotation, homeRot) < 2f) { target = -1; Enter(State.LookAround); }
                }
                break;
        }
        // you reached the castle goal unseen: learn your route and get a little sharper (capped)
        bool inGoal = castleGoal && Vector3.Distance(pp, castleGoal.position) <= breachRadius;
        if (inGoal && !breached && state != State.Chase) { Learn(); viewDistance = Mathf.Min(viewDistance * 1.08f, 30f); suspicionRate = Mathf.Min(suspicionRate * 1.08f, 2.5f); }
        breached = inGoal;
        Animate();
    }

    void Enter(State s)
    {
        state = s; timer = 0f;
        switch (s)
        {
            case State.Patrol:
                agent.speed = patrolSpeed * (hunting ? WantedSystem.HuntSpeed : 1f);
                target = PickPoint();
                if (target >= 0) Go(guardPoints[target].position); else { state = State.LookAround; Stop(); duration = lookAroundTime; }
                break;
            case State.LookAround: Stop(); duration = lookAroundTime * (1f + (target >= 0 ? weights[target] : 0f)); Trigger("LookAround"); break;   // lingers at hotspots
            case State.Suspicious: Stop(); if (noticeClip) voice.PlayOneShot(noticeClip); break;
            case State.Investigate: agent.speed = investigateSpeed; Go(lastKnown); break;
            case State.Search: Stop(); Trigger("LookAround"); if (searchClip) voice.PlayOneShot(searchClip); break;
            case State.Chase: agent.speed = chaseSpeed; if (alarmClip) voice.PlayOneShot(alarmClip); break;
            case State.Escort: Stop(); break;
            case State.ReturnHome: atHome = false; agent.speed = patrolSpeed; Go(homePos); break;
        }
        if (animator && animator.runtimeAnimatorController) animator.SetBool("Suspicious", state == State.Suspicious);
    }

    // gentle "throw out": scoop up, fade, set down outside facing him, he shoos you away
    IEnumerator Escort()
    {
        Enter(State.Escort); catchPos = transform.position;
        onPlayerCaught.Invoke(); Learn(); Lock(true); Trigger("Grab");
        for (float t = 0; t < grabAnimTime; t += Time.deltaTime) { Face(PlayerPos); yield return null; }
        yield return Fade(1f);
        if (shooSpot && NavMesh.SamplePosition(shooSpot.position, out NavMeshHit h, 3f, NavMesh.AllAreas)) agent.Warp(h.position);
        if (ejectPoint) MovePlayer(ejectPoint);
        Face(PlayerPos, 9999f);
        yield return new WaitForSeconds(.3f);
        yield return Fade(0f);
        Trigger("Shoo"); onPlayerEjected.Invoke();
        for (float t = 0; t < shooTime; t += Time.deltaTime) { Face(PlayerPos); yield return null; }
        Lock(false); suspicion = 0f; lost = 0f;
        WantedSystem.ReportCaught();
        if (huntWhileWanted && WantedSystem.IsActive) { hunting = true; lastKnown = catchPos; Enter(State.Investigate); }
        else Enter(returnHomeAfterCatch ? State.ReturnHome : State.Patrol);
    }

    public void ReturnHome() { if (state == State.Escort) return; hunting = false; suspicion = 0f; Enter(State.ReturnHome); }
    public void ResetToHome()
    {
        StopAllCoroutines(); Lock(false); hunting = false; suspicion = 0f;
        if (fader) fader.alpha = 0f;
        agent.Warp(homePos); transform.rotation = homeRot; target = -1; Enter(State.LookAround);
    }

    void MovePlayer(Transform to)
    {
        var cc = player.GetComponent<CharacterController>(); if (cc) cc.enabled = false;
        Vector3 toGolem = Vector3.ProjectOnPlane(transform.position - to.position, Vector3.up);
        float headOffset = playerHead ? playerHead.eulerAngles.y - player.eulerAngles.y : 0f;   // face him, whatever way your head is turned
        player.rotation = Quaternion.Euler(0f, Quaternion.LookRotation(toGolem.sqrMagnitude > .01f ? toGolem : to.forward).eulerAngles.y - headOffset, 0f);
        Vector3 off = PlayerPos - player.position; off.y = 0f;
        player.position = to.position - off;                                                   // put your head, not the rig origin, on the spot
        if (cc) cc.enabled = true;
        lastPos = PlayerPos; vel = Vector3.zero; routeN = 0;
    }

    void Lock(bool on) { if (disableWhileCaught != null) foreach (var b in disableWhileCaught) if (b) b.enabled = !on; }

    IEnumerator Fade(float to)
    {
        if (!fader && Camera.main)   // black screen in front of the camera (works in the headset)
        {
            var go = new GameObject("GolemFade", typeof(Canvas), typeof(CanvasGroup));
            var c = go.GetComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceCamera; c.worldCamera = Camera.main; c.planeDistance = Camera.main.nearClipPlane + .02f; c.sortingOrder = 32767;
            fader = go.GetComponent<CanvasGroup>(); fader.alpha = 0f; fader.blocksRaycasts = false;
            var img = new GameObject("Black", typeof(RectTransform), typeof(Image)); img.transform.SetParent(go.transform, false);
            var rt = (RectTransform)img.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            img.GetComponent<Image>().color = Color.black;
        }
        if (!fader) yield break;
        for (float from = fader.alpha, t = 0; t < fadeTime; t += Time.deltaTime) { fader.alpha = Mathf.Lerp(from, to, t / fadeTime); yield return null; }
        fader.alpha = to;
    }

    bool CanSee()
    {
        Vector3 eye = transform.position + Vector3.up * eyeHeight, to = PlayerEyes - eye;
        if (to.magnitude > viewDistance || Vector3.Angle(transform.forward, Vector3.ProjectOnPlane(to, Vector3.up)) > viewAngle) return false;
        foreach (var h in Physics.RaycastAll(eye, to.normalized, to.magnitude, sightBlockers, QueryTriggerInteraction.Ignore))
            if (!h.transform.IsChildOf(transform) && !h.transform.IsChildOf(player)) return false;
        return true;
    }

    // ---- learning: guard points near the routes you use get more weight; patrol picks them more often
    int PickPoint()
    {
        float total = 0f, temp = Mathf.Max(.05f, hunting ? exploreTemp * .4f : exploreTemp);
        var p = new float[weights.Length];
        for (int i = 0; i < p.Length; i++) if (guardPoints[i] && (i != target || p.Length == 1)) total += p[i] = Mathf.Exp(weights[i] / temp);
        float r = Random.value * total;
        for (int i = 0; i < p.Length; i++) if (p[i] > 0f && (r -= p[i]) <= 0f) return i;
        return -1;
    }

    void Learn()
    {
        if (routeN == 0) return;
        for (int i = 0; i < weights.Length; i++)
        {
            if (!guardPoints[i]) continue;
            int near = 0;
            for (int k = 0; k < routeN; k++) if (Vector3.Distance(route[k], guardPoints[i].position) < 6f) near++;
            weights[i] = Mathf.Clamp(weights[i] * (1f - learnRate * .2f) + learnRate * 3f * near / routeN, 0f, 5f);
        }
        PlayerPrefs.SetString(SaveKey, string.Join(",", System.Array.ConvertAll(weights, w => w.ToString(CultureInfo.InvariantCulture))));
    }

    [ContextMenu("Reset Learning")] void ResetLearning() { PlayerPrefs.DeleteKey(SaveKey); System.Array.Clear(weights, 0, weights.Length); }

    // ---- animation + head look (needs IK Pass on the Animator layer)
    void Animate()
    {
        if (animator && animator.runtimeAnimatorController) animator.SetFloat("Speed", agent.velocity.magnitude, .1f, Time.deltaTime);
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        bool sweep = state == State.LookAround || state == State.Search;
        Vector3 want = state == State.Suspicious || state == State.Investigate ? lastKnown + Vector3.up * 1.2f
                     : (state == State.Chase || state == State.Escort) && player ? PlayerEyes
                     : eye + Quaternion.Euler(0f, Mathf.Sin(timer * (sweep ? 1.3f : .5f)) * (sweep ? 70f : 25f), 0f) * transform.forward * 8f;
        lookPos = Vector3.Lerp(lookPos, want, Time.deltaTime * 3f);
        lookW = Mathf.MoveTowards(lookW, 1f, Time.deltaTime * 2f);
    }

    void OnAnimatorIK(int layer)
    {
        if (!animator) return;
        animator.SetLookAtWeight(lookW, state == State.Suspicious ? .45f : .2f, 1f, 0f, .6f);   // leaning in = the peek
        animator.SetLookAtPosition(lookPos);
    }

    void Trigger(string t) { if (animator && animator.runtimeAnimatorController) foreach (var p in animator.parameters) if (p.name == t) { animator.SetTrigger(t); return; } }
    void Go(Vector3 p) { if (agent.isOnNavMesh) { agent.isStopped = false; agent.SetDestination(p); } }
    void Stop() { if (agent.isOnNavMesh) { agent.isStopped = true; agent.ResetPath(); } }
    bool Arrived() => agent.isOnNavMesh && !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + .3f;
    void Face(Vector3 p, float speed = -1f)
    {
        Vector3 d = Vector3.ProjectOnPlane(p - transform.position, Vector3.up);
        if (d.sqrMagnitude > .01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), (speed > 0 ? speed : turnSpeed) * Time.deltaTime);
    }

    void OnDrawGizmosSelected()
    {
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Gizmos.color = Color.cyan;
        Gizmos.DrawRay(eye, Quaternion.Euler(0, viewAngle, 0) * transform.forward * viewDistance);
        Gizmos.DrawRay(eye, Quaternion.Euler(0, -viewAngle, 0) * transform.forward * viewDistance);
        Gizmos.color = new Color(1, 1, 0, .4f); Gizmos.DrawWireSphere(transform.position, hearingRadius);
        for (int i = 0; i < guardPoints.Length; i++)
            if (guardPoints[i]) { Gizmos.color = Color.Lerp(Color.green, Color.red, weights != null && i < weights.Length ? weights[i] / 3f : 0f); Gizmos.DrawSphere(guardPoints[i].position, .4f); }
    }
}