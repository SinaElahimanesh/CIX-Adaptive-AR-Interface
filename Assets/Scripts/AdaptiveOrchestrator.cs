using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Meta.WitAi.TTS.Utilities;
#if ADAPTIVE_ORCHESTRATOR_MRUK
using Meta.XR.MRUtilityKit;
#endif
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class AdaptiveCommandOrchestrator : MonoBehaviour
{
    // ============================================================
    // 0) CONFIG
    // ============================================================

    // [Header("OpenAI (Testing Only - Do NOT ship keys in Quest APK)")]
    [Tooltip("DO NOT commit or ship real keys. Use server proxy or secure config.")]
    public string OPENAI_API_KEY = ""; 
    [Tooltip("Use a model that supports the Responses API and structured outputs, e.g. gpt-4o, gpt-4o-mini. gpt-5.2 is not a valid model name and will cause request failures.")]
    public string model = "gpt-5.2";
    [Range(0f, 1.5f)] public float temperature = 0.2f;

    // ============================================================
    // 1) UI INPUT/OUTPUT
    // ============================================================

    [Header("UI")]
    public TMP_InputField commandInput;
    public Button executeButton;
    public TMP_Text debugText;          // shows status / last JSON (optional)
    public TMP_Text supportedText;      // shows supported commands list (optional)
    [Tooltip("Shows LLM response: success message, error, or clarification question when confidence is low.")]
    public TMP_Text responseText;

    [Header("Clarification options (when the LLM asks follow-up with choices)")]
    [Tooltip("Parent transform for option buttons (e.g. a Panel with VerticalLayoutGroup). If null, only response text is shown.")]
    public RectTransform clarifyOptionsContainer;
    [Tooltip("Optional prefab for each option button (must have Button and a TMP_Text on self or child for the label). If null, simple buttons are created at runtime.")]
    public Button clarifyOptionButtonPrefab;
    [Tooltip("Vertical offset (in local units) from the bottom of the container where clarify buttons start (use this to align them under the loading icon/text).")]
    public float clarifyOptionsBottomOffset = 40f;
    [Tooltip("Optional sprite with rounded corners for clarify buttons (9-sliced recommended). If assigned, buttons will use this for nicer corner radius.")]
    public Sprite clarifyOptionSprite;

    [Header("Text-to-Speech (Meta Voice SDK / Wit)")]
    [Tooltip("Assign a TTSSpeaker from your scene (e.g. from Voice SDK TTS setup). Same stack as speech recognition.")]
    public TTSSpeaker ttsSpeaker;
    [Tooltip("When true, the response text is spoken aloud via the TTSSpeaker above.")]
    public bool speakResponseWithTts = true;
    [Tooltip("Max characters to speak; longer messages are truncated to avoid very long TTS.")]
    public int ttsMaxLength = 200;

    [Header("Loading UI (optional)")]
    [Tooltip("Panel or GameObject to show while API call and apply are in progress. Leave empty to skip.")]
    public GameObject loadingPanel;
    [Tooltip("Optional text to show status: 'Calling API...', 'Applying...', etc.")]
    public TMP_Text loadingText;
    [Tooltip("Optional Image (or child GameObject) to use as a spinner. It will rotate while loading. Assign the Image's Transform.")]
    public Transform loadingSpinner;
    [Tooltip("Spinner rotation speed in degrees per second (positive = clockwise).")]
    public float loadingSpinnerSpeedDegPerSec = 360f;

    // ============================================================
    // 2) WORLD OBJECT TARGET (example: cube)
    // ============================================================

    [Header("World Object Target (Cube)")]
    public Transform cube;

    [Tooltip("Used for left/right/forward/back/away. If null uses Camera.main.")]
    public Transform directionReference;

    [Header("Anchor Targets (optional)")]
    [Tooltip("World = fixed in space. Assign empty transform or leave null to unparent.")]
    public Transform worldAnchor;
    [Tooltip("e.g. OVR hand or controller transform so object follows hand.")]
    public Transform handAnchor;
    [Tooltip("e.g. CameraRig torso/chest or center eye anchor.")]
    public Transform torsoAnchor;
    [Tooltip("e.g. CenterEyeAnchor or main camera so object follows head.")]
    public Transform headAnchor;

    // ============================================================
    // 3) AUIT TARGETS
    // ============================================================

    [Header("AUIT Integration")]
    [Tooltip("Drag UI GameObjects that are controlled by AUIT (have AUIT components attached).")]
    public List<GameObject> auitUiElements = new List<GameObject>();

    [Tooltip("If true, AUIT wins conflicts whenever AUIT and others want the same effect.")]
    public bool preferAUIT = true;

    [Header("Hard Constraint Defaults (FOR SURE)")]
    [Tooltip("Default minimum distance of UI to camera to prevent UI going into eyes.")]
    public float defaultMinDistanceMeters = 0.6f;

    [Tooltip("Default max gaze cone when 'keep in view' is requested.")]
    public float defaultKeepInViewAngleDeg = 10f;

    [Header("Environment for spatial references")]
    [Tooltip("Optional: drag more GameObjects here so the model can resolve 'to the right of X', 'next to Y'. Cube and AUIT UI are included automatically. Add any new scene objects here when you add them to the scene.")]
    public List<GameObject> additionalEnvironmentObjects = new List<GameObject>();

    // ============================================================
    // INTERNAL
    // ============================================================

    private const string ENDPOINT = "https://api.openai.com/v1/responses";
    private const string LOGP = "[AdaptiveOrchestrator] ";

    private IAdaptationProvider _auitProvider;
    private IAdaptationProvider _cubeProvider;
    private AdaptationRouter _router;
    private bool _isLoading;

    // ============================================================
    // Persistent UI command memory (history) + hard constraints
    // ============================================================

    [Serializable]
    public class UiCommandState
    {
        // Pose lock (world anchor)
        public bool lockWorldPose = false;
        public Vector3 lockedWorldPos;
        public Quaternion lockedWorldRot;

        // Distance clamp from camera
        public bool clampMinDistance = false;
        public float minDistanceMeters = 0.6f;

        public bool clampMaxDistance = false;
        public float maxDistanceMeters = 2.0f;

        // Visibility
        public bool forceVisible = false;
        public bool forceHidden = false;

        // Keep in view cone (pull UI into center — use for "keep visible", NOT for "don't block")
        public bool keepInView = false;
        public float maxGazeAngleDeg = 10f;

        // Avoid occlusion: push UI out of center so it doesn't block the view (e.g. to the side)
        public bool avoidOcclusion = false;
        public float minGazeAngleDeg = 22f; // stay at least this many degrees off center

        // Optional: extra user-driven relative nudges (accumulates)
        public Vector3 pendingWorldDelta = Vector3.zero; // apply once in LateUpdate

        public UiCommandState Clone()
        {
            return new UiCommandState
            {
                lockWorldPose = lockWorldPose,
                lockedWorldPos = lockedWorldPos,
                lockedWorldRot = lockedWorldRot,
                clampMinDistance = clampMinDistance,
                minDistanceMeters = minDistanceMeters,
                clampMaxDistance = clampMaxDistance,
                maxDistanceMeters = maxDistanceMeters,
                forceVisible = forceVisible,
                forceHidden = forceHidden,
                keepInView = keepInView,
                maxGazeAngleDeg = maxGazeAngleDeg,
                avoidOcclusion = avoidOcclusion,
                minGazeAngleDeg = minGazeAngleDeg,
                pendingWorldDelta = pendingWorldDelta
            };
        }
    }

    private readonly Dictionary<int, UiCommandState> _uiStateByInstanceId = new Dictionary<int, UiCommandState>();

    // Command memory: last N commands for context and refinement ("move it upper" = same target as last)
    private const int MaxCommandHistory = 8;
    private readonly List<CommandHistoryEntry> _commandHistory = new List<CommandHistoryEntry>();

    private struct CommandHistoryEntry
    {
        public string Raw;
        public string Domain;
        public string Target;
        public string ActionSummary;
    }

    // Undo: state snapshot before last apply (so "undo"/"revert" restores it)
    private UndoSnapshot _undoSnapshot;

    // Conversation sequence: last N turns (user message + app response) so the API can interpret the current message in context
    private const int MaxConversationTurns = 6;
    private readonly List<ConversationTurn> _conversationTurns = new List<ConversationTurn>();

    private struct ConversationTurn
    {
        public string UserMessage;
        public string AppResponseText;
        public string SuggestedCommandSummary; // when app_action was clarify, so next turn the model can output it if user confirms
    }

    private class UndoSnapshot
    {
        public bool HasCube;
        public Vector3 CubePosition;
        public Quaternion CubeRotation;
        public Transform CubeParent;

        public List<UIStateEntry> UIStates = new List<UIStateEntry>();
    }

    private class UIStateEntry
    {
        public int InstanceId;
        public Vector3 Position;
        public Quaternion Rotation;
        public Transform Parent;
        public bool Active;
        public UiCommandState State;
    }

    public UiCommandState GetOrCreateUiState(GameObject go)
    {
        if (go == null) return null;
        int id = go.GetInstanceID();
        if (!_uiStateByInstanceId.TryGetValue(id, out var st))
        {
            st = new UiCommandState
            {
                clampMinDistance = true,
                minDistanceMeters = defaultMinDistanceMeters,
                keepInView = false,
                maxGazeAngleDeg = defaultKeepInViewAngleDeg
            };
            _uiStateByInstanceId[id] = st;
        }
        return st;
    }

    private void Awake()
    {
        Log("Awake().");

        if (executeButton != null)
        {
            executeButton.onClick.RemoveListener(OnExecuteClicked);
            executeButton.onClick.AddListener(OnExecuteClicked);
            Log("Button listener attached.");
        }
        else
        {
            Log("WARNING: executeButton not assigned.");
        }

        _auitProvider = new AUITProvider(auitUiElements, this);
        _cubeProvider = new CubeProvider(cube, directionReference, worldAnchor, handAnchor, torsoAnchor, headAnchor);

        _router = new AdaptationRouter(preferAUIT ? ConflictPolicy.PreferHigherPriority : ConflictPolicy.MergeNoPriority);
        _router.Register(_auitProvider);
        _router.Register(_cubeProvider);
    }

    private void Start()
    {
        RefreshSupportedCommandsUI();
        HideLoading(); // ensure loading UI is hidden at start (icon + text only show when API runs)
        SetResponseText("");
    }

    private void LateUpdate()
    {
        // Enforce after AUIT / other systems wrote transforms.
        if (auitUiElements == null || auitUiElements.Count == 0) return;

        foreach (var go in auitUiElements)
        {
            if (go == null) continue;

            int id = go.GetInstanceID();
            if (!_uiStateByInstanceId.TryGetValue(id, out var st) || st == null) continue;

            ApplyHardConstraints(go.transform, st);
        }
    }

    private void ApplyHardConstraints(Transform t, UiCommandState st)
    {
        if (t == null || st == null) return;

        // Visibility
        if (st.forceHidden) t.gameObject.SetActive(false);
        else if (st.forceVisible) t.gameObject.SetActive(true);

        // Pose lock (strongest)
        if (st.lockWorldPose)
        {
            t.position = st.lockedWorldPos;
            t.rotation = st.lockedWorldRot;
            return; // locked means "FOR SURE"
        }

        // Apply ONE-SHOT delta (very important!)
        if (st.pendingWorldDelta != Vector3.zero)
        {
            t.position += st.pendingWorldDelta;
            st.pendingWorldDelta = Vector3.zero;
        }

        // Distance clamp
        var cam = Camera.main != null ? Camera.main.transform : null;
        if (cam != null && (st.clampMinDistance || st.clampMaxDistance))
        {
            Vector3 to = t.position - cam.position;
            float d = to.magnitude;
            if (d > 1e-5f)
            {
                float minD = st.clampMinDistance ? st.minDistanceMeters : 0f;
                float maxD = st.clampMaxDistance ? st.maxDistanceMeters : float.PositiveInfinity;

                float clamped = Mathf.Clamp(d, minD, maxD);
                if (Mathf.Abs(clamped - d) > 1e-4f)
                    t.position = cam.position + to.normalized * clamped;
            }
        }

        // Keep in view and/or avoid occlusion (single objective or multi-objective band)
        if (cam != null && (st.keepInView || st.avoidOcclusion))
        {
            Vector3 to = (t.position - cam.position);
            float d = to.magnitude;
            if (d > 1e-4f)
            {
                float ang = Vector3.Angle(cam.forward, to.normalized);
                float minAng = st.avoidOcclusion ? st.minGazeAngleDeg : 0f;
                float maxAng = st.keepInView ? st.maxGazeAngleDeg : 180f;

                if (st.keepInView && st.avoidOcclusion)
                {
                    float bandMax = maxAng > minAng ? maxAng : Mathf.Max(45f, minAng + 10f);
                    if (ang < minAng)
                    {
                        float minRad = minAng * Mathf.Deg2Rad;
                        Vector3 newDir = (cam.forward * Mathf.Cos(minRad) + cam.right * Mathf.Sin(minRad)).normalized;
                        t.position = cam.position + newDir * d;
                    }
                    else if (ang > bandMax)
                    {
                        float maxRad = bandMax * Mathf.Deg2Rad;
                        Vector3 lateral = Vector3.ProjectOnPlane(to, cam.forward).normalized;
                        if (lateral.sqrMagnitude < 1e-6f) lateral = cam.right;
                        Vector3 newDir = (cam.forward * Mathf.Cos(maxRad) + lateral * Mathf.Sin(maxRad)).normalized;
                        t.position = cam.position + newDir * d;
                    }
                }
                else if (st.keepInView && ang > maxAng)
                    t.position = cam.position + cam.forward * d;
                else if (st.avoidOcclusion && minAng > 0 && ang < minAng)
                {
                    float minRad = minAng * Mathf.Deg2Rad;
                    Vector3 newDir = (cam.forward * Mathf.Cos(minRad) + cam.right * Mathf.Sin(minRad)).normalized;
                    t.position = cam.position + newDir * d;
                }
            }
        }
    }

    /// <summary>
    /// Builds a description of the current scene (objects and world positions) so the model can resolve spatial references like ""put UI to the right of cube"".
    /// Includes cube, AUIT UI elements, and additionalEnvironmentObjects. Called at request time so positions are current.
    /// </summary>
    private string BuildEnvironmentDescription()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Current environment (world position in meters, X=right Y=up Z=forward):");

        if (cube != null)
        {
            var p = cube.position;
            sb.AppendLine($"- cube: ({p.x:F2}, {p.y:F2}, {p.z:F2})");
        }

        if (auitUiElements != null)
        {
            foreach (var go in auitUiElements)
            {
                if (go == null) continue;
                var p = go.transform.position;
                sb.AppendLine($"- {go.name}: ({p.x:F2}, {p.y:F2}, {p.z:F2})");
            }
        }

        if (additionalEnvironmentObjects != null)
        {
            foreach (var go in additionalEnvironmentObjects)
            {
                if (go == null) continue;
                var p = go.transform.position;
                sb.AppendLine($"- {go.name}: ({p.x:F2}, {p.y:F2}, {p.z:F2})");
            }
        }

        var cam = Camera.main;
        if (cam != null)
        {
            var p = cam.transform.position;
            sb.AppendLine($"- camera (user): ({p.x:F2}, {p.y:F2}, {p.z:F2})");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a description of the REAL-WORLD environment from the headset (MRUK/Scene API when available).
    /// Runs without additional setup: if MRUK is missing or no scene is loaded, returns a short fallback message.
    /// Define ADAPTIVE_ORCHESTRATOR_MRUK in Player Settings to enable MRUK (requires Meta XR MR Utility Kit package).
    /// </summary>
    private string BuildRealEnvironmentDescription()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Real-world environment (from headset scene understanding; world position in meters, X=right Y=up Z=forward):");

#if ADAPTIVE_ORCHESTRATOR_MRUK
        if (MRUK.Instance == null || MRUK.Instance.Rooms == null || MRUK.Instance.Rooms.Count == 0)
        {
            sb.AppendLine("(No real-world scene loaded. Optional: add MRUK prefab and run Space Setup on the headset for 'put on table' etc.)");
            return sb.ToString();
        }

        var labelCounts = new Dictionary<string, int>();

        foreach (var room in MRUK.Instance.Rooms)
        {
            if (room?.Anchors == null) continue;

            foreach (var anchor in room.Anchors)
            {
                if (anchor == null) continue;

                string labelName = GetRealEnvironmentLabelName(anchor.Label);
                if (string.IsNullOrEmpty(labelName)) labelName = "object";

                string key = labelName;
                if (!labelCounts.TryGetValue(key, out int count)) count = 0;
                count++;
                labelCounts[key] = count;
                string displayName = count > 1 ? $"{labelName}_{count}" : labelName;

                Vector3 center = anchor.GetAnchorCenter();
                sb.Append($"- {displayName} (semantic: {anchor.Label}): center=({center.x:F2}, {center.y:F2}, {center.z:F2})");

                if (anchor.PlaneRect.HasValue)
                {
                    var r = anchor.PlaneRect.Value;
                    sb.Append($", planeSize={r.size.x:F2}x{r.size.y:F2}m");
                }
                else if (anchor.VolumeBounds.HasValue)
                {
                    var b = anchor.VolumeBounds.Value;
                    sb.Append($", volumeSize=({b.size.x:F2}, {b.size.y:F2}, {b.size.z:F2})m");
                }

                sb.AppendLine();
            }
        }

        if (labelCounts.Count == 0)
            sb.AppendLine("(No anchors in scene. Run Space Setup on the headset to detect walls, tables, etc.)");
#else
        sb.AppendLine("(Real-world scene not available. For 'put on table' support: add Meta XR MR Utility Kit, add MRUK prefab, then add Scripting Define Symbol ADAPTIVE_ORCHESTRATOR_MRUK in Project Settings > Player.)");
#endif
        return sb.ToString();
    }

#if ADAPTIVE_ORCHESTRATOR_MRUK
    /// <summary>Maps MRUK SceneLabels to short, model-friendly names for prompts. Includes all labels so small/misc objects (bottle, whiteboard, etc.) are not dropped.</summary>
    private static string GetRealEnvironmentLabelName(MRUKAnchor.SceneLabels label)
    {
        var names = new List<string>();
        if ((label & MRUKAnchor.SceneLabels.FLOOR) != 0) names.Add("floor");
        if ((label & MRUKAnchor.SceneLabels.CEILING) != 0) names.Add("ceiling");
        if ((label & MRUKAnchor.SceneLabels.WALL_FACE) != 0) names.Add("wall");
        if ((label & MRUKAnchor.SceneLabels.INNER_WALL_FACE) != 0) names.Add("wall_inner");
        if ((label & MRUKAnchor.SceneLabels.INVISIBLE_WALL_FACE) != 0) names.Add("wall_invisible");
        if ((label & MRUKAnchor.SceneLabels.TABLE) != 0) names.Add("table");
        if ((label & MRUKAnchor.SceneLabels.COUCH) != 0) names.Add("couch");
        if ((label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0) names.Add("door");
        if ((label & MRUKAnchor.SceneLabels.WINDOW_FRAME) != 0) names.Add("window");
        if ((label & MRUKAnchor.SceneLabels.STORAGE) != 0) names.Add("storage");
        if ((label & MRUKAnchor.SceneLabels.BED) != 0) names.Add("bed");
        if ((label & MRUKAnchor.SceneLabels.SCREEN) != 0) names.Add("monitor");
        if ((label & MRUKAnchor.SceneLabels.LAMP) != 0) names.Add("lamp");
        if ((label & MRUKAnchor.SceneLabels.PLANT) != 0) names.Add("plant");
        if ((label & MRUKAnchor.SceneLabels.WALL_ART) != 0) names.Add("wall_art");  // can be whiteboard, painting, etc.
        if ((label & MRUKAnchor.SceneLabels.OTHER) != 0) names.Add("other");       // misc/small objects (bottle, etc.)
        if ((label & MRUKAnchor.SceneLabels.UNKNOWN) != 0) names.Add("object");
        if ((label & MRUKAnchor.SceneLabels.GLOBAL_MESH) != 0) names.Add("scene_surface");
        return names.Count > 0 ? string.Join("_", names) : "object";
    }
#endif

    private void RefreshSupportedCommandsUI()
    {
        string s = BuildSupportedCommandsText();
        if (supportedText != null) supportedText.text = s;
        Log("Supported commands:\n" + s);
    }

    /// <summary>Snapshot current state so "undo" can restore to this. Call before each apply.</summary>
    public void SnapshotForUndo()
    {
        var snap = new UndoSnapshot();

        if (cube != null)
        {
            snap.HasCube = true;
            snap.CubePosition = cube.position;
            snap.CubeRotation = cube.rotation;
            snap.CubeParent = cube.parent;
        }

        if (auitUiElements != null)
        {
            foreach (var go in auitUiElements)
            {
                if (go == null) continue;
                var t = go.transform;
                var st = GetOrCreateUiState(go);
                snap.UIStates.Add(new UIStateEntry
                {
                    InstanceId = go.GetInstanceID(),
                    Position = t.position,
                    Rotation = t.rotation,
                    Parent = t.parent,
                    Active = go.activeSelf,
                    State = st != null ? st.Clone() : new UiCommandState()
                });
            }
        }

        _undoSnapshot = snap;
    }

    /// <summary>Restore state from last snapshot (before the last applied command). Call when user says "undo"/"revert".</summary>
    public void RestoreFromUndo()
    {
        if (_undoSnapshot == null)
        {
            Log("Nothing to undo.");
            return;
        }

        if (_undoSnapshot.HasCube && cube != null)
        {
            cube.position = _undoSnapshot.CubePosition;
            cube.rotation = _undoSnapshot.CubeRotation;
            cube.SetParent(_undoSnapshot.CubeParent, true);
        }

        if (auitUiElements != null)
        {
            foreach (var go in auitUiElements)
            {
                if (go == null) continue;
                int id = go.GetInstanceID();
                UIStateEntry entry = null;
                foreach (var e in _undoSnapshot.UIStates)
                {
                    if (e.InstanceId == id) { entry = e; break; }
                }
                if (entry == null) continue;

                go.transform.position = entry.Position;
                go.transform.rotation = entry.Rotation;
                go.transform.SetParent(entry.Parent, true);
                go.SetActive(entry.Active);

                var st = GetOrCreateUiState(go);
                if (st != null && entry.State != null)
                {
                    var s = entry.State;
                    st.lockWorldPose = s.lockWorldPose;
                    st.lockedWorldPos = s.lockedWorldPos;
                    st.lockedWorldRot = s.lockedWorldRot;
                    st.clampMinDistance = s.clampMinDistance;
                    st.minDistanceMeters = s.minDistanceMeters;
                    st.clampMaxDistance = s.clampMaxDistance;
                    st.maxDistanceMeters = s.maxDistanceMeters;
                    st.forceVisible = s.forceVisible;
                    st.forceHidden = s.forceHidden;
                    st.keepInView = s.keepInView;
                    st.maxGazeAngleDeg = s.maxGazeAngleDeg;
                    st.avoidOcclusion = s.avoidOcclusion;
                    st.minGazeAngleDeg = s.minGazeAngleDeg;
                    st.pendingWorldDelta = s.pendingWorldDelta;
                }
            }
        }

        Log("Undo: restored previous state.");
    }

    private static string BuildActionSummary(StructuredCommand cmd)
    {
        if (cmd?.@params == null) return "command";
        var p = cmd.@params;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(p.ui_intent)) parts.Add("ui_intent=" + p.ui_intent);
        if (!string.IsNullOrEmpty(p.anchor_target) && p.anchor_target != "none") parts.Add("anchor=" + p.anchor_target);
        if (p.move != null && p.move.mode != "none") parts.Add("move " + p.move.mode);
        if (!string.IsNullOrEmpty(p.visibility)) parts.Add("visibility=" + p.visibility);
        return parts.Count > 0 ? string.Join(", ", parts) : "apply";
    }

    private void PushCommandHistory(string raw, StructuredCommand cmd)
    {
        if (cmd == null) return;
        _commandHistory.Add(new CommandHistoryEntry
        {
            Raw = raw ?? "",
            Domain = cmd.domain ?? "",
            Target = cmd.target ?? "",
            ActionSummary = BuildActionSummary(cmd)
        });
        while (_commandHistory.Count > MaxCommandHistory)
            _commandHistory.RemoveAt(0);
    }

    private string BuildCommandHistoryForPrompt()
    {
        if (_commandHistory == null || _commandHistory.Count == 0)
            return "No previous commands in this session. User has not given any command yet.";
        var sb = new StringBuilder();
        sb.AppendLine("Recent command history (use for refinement: \"it\", \"that\", \"move it upper\", \"a bit more\"; and for undo context):");
        for (int i = _commandHistory.Count - 1; i >= 0; i--)
        {
            var e = _commandHistory[i];
            int rev = _commandHistory.Count - 1 - i;
            string label = rev == 0 ? "Last" : ("Previous " + (rev + 1));
            sb.AppendLine($"- {label}: \"{e.Raw}\" -> domain={e.Domain}, target={e.Target}, action={e.ActionSummary}");
        }
        return sb.ToString();
    }

    /// <summary>Build prompt block with the conversation sequence so the API can interpret the current message in context (e.g. "yes" after a clarification).</summary>
    private string BuildConversationSequenceBlock(string currentUserMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Conversation sequence (use this to interpret the CURRENT message in context):");
        if (_conversationTurns == null || _conversationTurns.Count == 0)
        {
            sb.AppendLine("No previous turns. This is the first message in the conversation.");
        }
        else
        {
            int start = Mathf.Max(0, _conversationTurns.Count - MaxConversationTurns);
            for (int i = start; i < _conversationTurns.Count; i++)
            {
                var t = _conversationTurns[i];
                int turnNum = i - start + 1;
                sb.AppendLine($"Turn {turnNum} - User: \"{EscapeForPrompt(t.UserMessage)}\"");
                sb.AppendLine($"         App: \"{EscapeForPrompt(t.AppResponseText)}\"");
                if (!string.IsNullOrEmpty(t.SuggestedCommandSummary))
                    sb.AppendLine($"         (Your suggested interpretation, not applied: {t.SuggestedCommandSummary})");
            }
        }
        sb.AppendLine($"Current user message (interpret this in the sequence above): \"{EscapeForPrompt(currentUserMessage ?? "")}\"");
        sb.AppendLine("If the sequence shows you just asked for clarification and the user is now confirming (e.g. yes, that one, do it), output that suggested interpretation with app_action=\"apply\". If this is a new or different command, interpret it normally. For a simple first message with no prior turns, interpret the message directly.");
        return sb.ToString();
    }

    private static string EscapeForPrompt(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private void PushConversationTurn(string userMessage, string appResponseText, string suggestedCommandSummary = null)
    {
        _conversationTurns.Add(new ConversationTurn
        {
            UserMessage = userMessage ?? "",
            AppResponseText = appResponseText ?? "",
            SuggestedCommandSummary = suggestedCommandSummary
        });
        while (_conversationTurns.Count > MaxConversationTurns)
            _conversationTurns.RemoveAt(0);
    }

    private static string BuildSuggestedCommandSummary(StructuredCommand cmd)
    {
        if (cmd?.@params == null) return null;
        string summary = BuildActionSummary(cmd);
        var p = cmd.@params;
        string detail = $"domain={cmd.domain}, target={cmd.target}, {summary}";
        if (p.move != null && !string.IsNullOrEmpty(p.move.mode) && p.move.mode != "none")
        {
            if (p.move.mode.Equals("relative", StringComparison.OrdinalIgnoreCase) && p.move.delta_meters != null)
                detail += $", move relative (x={p.move.delta_meters.x}, y={p.move.delta_meters.y}, z={p.move.delta_meters.z})";
            else if (p.move.mode.Equals("absolute", StringComparison.OrdinalIgnoreCase) && p.move.world_position != null)
                detail += $", move absolute ({p.move.world_position.x}, {p.move.world_position.y}, {p.move.world_position.z})";
        }
        return detail;
    }

    private string BuildSupportedCommandsText()
    {
        var sb = new StringBuilder();

        sb.AppendLine("Supported Commands (runtime):");
        sb.AppendLine();

        sb.AppendLine("AUIT UI (natural language supported):");
        sb.AppendLine("- \"bring the menu closer\" / \"move UI left/right/up/down\"");
        sb.AppendLine("- \"make the UI easier to reach\"");
        sb.AppendLine("- \"keep the panel in view\"");
        sb.AppendLine("- \"show the interface\" / \"hide the interface\"");
        sb.AppendLine("- \"adapt the UI\" / \"optimize the UI layout\"");
        sb.AppendLine("- \"anchor UI to world\" (locks pose FOR SURE)");
        sb.AppendLine("- \"resume auto\" (unlocks pose and allows optimization)");
        sb.AppendLine();

        sb.AppendLine("AUIT adaptation objectives (intents the system can handle):");
        sb.AppendLine("- AnchorToTarget, AvoidAdaptWhileMoving, Collision, ConstantViewSize, DistanceInterval,");
        sb.AppendLine("  FieldOfView, LookTowards, Occlusion, SpatialCoherence, SurfaceMagnetism.");
        sb.AppendLine("(Reachability, visibility, consistency map to make_reachable, make_visible, adapt/optimize.)");
        sb.AppendLine();

        sb.AppendLine("Cube (world object):");
        sb.AppendLine("- \"hide the cube\" / \"show the cube\"");
        sb.AppendLine("- \"move cube to x 1 y 2 z 3\" (absolute)");
        sb.AppendLine("- \"move it 1 meter left/right/forward/back/up/down\" (relative to camera/reference)");
        sb.AppendLine("- \"anchor cube to world/hand/torso/head\" (if anchors assigned)");
        sb.AppendLine("- \"put cube on the table\" / \"place it on the desk\" / \"on the floor\" (uses real-world scene from headset)");
        sb.AppendLine();

        sb.AppendLine("Detected capabilities:");
        sb.AppendLine(_router.GetCapabilitiesSummary());

        return sb.ToString();
    }

    public void OnExecuteClicked()
    {
        Debug.Log(LOGP + "Internet reachability: " + Application.internetReachability);

        if (commandInput == null)
        {
            Log("ERROR: commandInput is null.");
            return;
        }

        string text = commandInput.text;
        if (string.IsNullOrWhiteSpace(text))
        {
            Log("Type a command first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OPENAI_API_KEY))
        {
            Log("ERROR: OPENAI_API_KEY missing (testing only).");
            return;
        }

        StartCoroutine(CallLLMAndApply(text.Trim()));
    }

    // ============================================================
    // LLM CALL
    // ============================================================

    private IEnumerator CallLLMAndApply(string userCommand)
    {
        Log("Starting LLM call. User command: " + userCommand);
        SetResponseText("");
        HideClarifyOptions();
        ShowLoading("Calling API...");

        string capabilities = _router.GetCapabilitiesForPrompt();
        string environmentDescription = BuildEnvironmentDescription();
        string realEnvironmentDescription = BuildRealEnvironmentDescription();
        string commandHistoryBlock = BuildCommandHistoryForPrompt();
        string conversationSequenceBlock = BuildConversationSequenceBlock(userCommand);

        string systemPrompt =
$@"You are an intent extractor for a Unity XR app. Your job is to infer the user's goal from natural language and output a single JSON object that matches the schema. The app applies ONLY what you output; it does not parse the raw command.

You receive the CONVERSATION SEQUENCE (previous turns: user message, app response, and when you had asked for clarification, your suggested interpretation). Use this sequence to interpret the CURRENT user message: in a multi-turn flow (e.g. you asked ""Did you mean move to the right?"" and the user now says ""yes""), output the suggested command with app_action=""apply"". When there are no prior turns or the message is a new command, interpret the current message directly. So one API call handles both simple single-message commands and sequential conversation.

Input is from SPEECH RECOGNITION: words may be misheard, misspelled, or imperfect. If the intended meaning is clear from context or the conversation sequence, interpret and correct. Put the user's exact words as heard in ""raw""; your interpretation drives the rest of the JSON.

Rules:
- Put the user's exact words (as heard) in ""raw"".
- Return only valid JSON that matches the schema. No markdown, no explanation.

app_action (required) — the app does EXACTLY what you set; no other logic runs. Set one of:
- ""apply"": Execute the command. Fill domain, target, params (move, visibility, anchor_target, ui_intent, etc.) and set ""response_text"" to a short confirmation (e.g. ""Done. Menu moved closer."", ""Cube is hidden.""). Use when the user's intent is clear and within the app's capabilities.
- ""undo"": The user said undo/revert/take that back. Set ""response_text"" to e.g. ""Undo applied."" The app will restore the previous state. Fill params minimally (ui_intent='undo' is enough).
- ""clarify"": Do NOT execute. Only show ""response_text"" to the user. Use when: (1) ambiguous and you need to ask (e.g. ""Do you mean the menu or the cube?"", ""Did you mean move to the right?""), (2) not applicable (e.g. ""That's not something I can do. I can only move the UI and the cube.""), or (3) couldn't understand (e.g. ""I didn't catch that. Could you try again?""). The app will remember your suggested interpretation so if the user says ""yes"" next, you can output the same command with app_action=""apply"". When your clarification question explicitly lists multiple options (e.g. ""near the table, the door, or the window?""), set ""clarify_options"" to EXACTLY those options as short labels: [""table"", ""door"", ""window""]; do NOT invent other labels like ""menu"" unless they appear in your question. If there is only one option, you may output a single-element array; if there are no explicit options, use [].

clarify_options (required) — always output this array. When app_action is ""clarify"" and the question has a small set of choices, list them as short labels taken from your own question (e.g. [""Yes"", ""No""], [""menu"", ""cube""], [""table"", ""door"", ""window""]). When ANY follow-up question has clear options, prefer to expose them as buttons via clarify_options so the user can answer with a tap instead of speech. For apply/undo or when there are no explicit choices, use [].

Feedback (response_text) — required. The app always shows this to the user. Set it to the right message for the situation. Confidence (0-1) is optional; the app only follows app_action.

Domain and target (critical — wrong domain causes the CUBE command to change the UI or vice versa; only ONE domain per command):
- Cube / the cube / the object / move it (when ""it"" is the cube) / place the cube / put the cube / hide the cube / show the cube -> domain='world_object', target='cube'. Nothing in the command should affect the UI.
- Menu / panel / UI / interface / HUD / visibility / placement / reach / view / bring closer (UI) / place the menu / put the UI -> domain='auit'. Nothing in the command should affect the cube.
- If the user is clearly talking about UI (menus, panels, HUD, interface, comfort, reach, view), set domain='auit'. If they are talking about the cube or a 3D object, set domain='world_object'.
- For domain='auit', if they name a specific UI element set target to that name; otherwise set target='auto'. For domain='world_object' use target='cube'.

Multi-objective commands (combine several objectives in one command):
- When the user expresses MORE THAN ONE goal in one sentence, set ui_intents to an ARRAY of all applicable intents. E.g. ""move UI to my FOV but avoid occlusion"" -> ui_intents=[""field_of_view"", ""occlusion""]. ""Keep it visible and reachable"" -> ui_intents=[""make_visible"", ""make_reachable""]. ""In view but don't block"" -> ui_intents=[""field_of_view"", ""occlusion""]. The app and AUIT will apply ALL objectives together (multi-object optimization). Still set ui_intent to the first or primary one. Set params (keep_in_view, visibility, etc.) to satisfy all when possible (e.g. field_of_view + occlusion: keep_in_view=""true"", and the app keeps UI in a band: in FOV but not in center).

AUIT (Adaptive User Interfaces Toolkit) context — use for intent mapping only; output schema unchanged:
- AUIT goals: support adaptive UIs in XR, multi-object optimization for 3D UI adaptation, combine preferences (reachability, visibility, consistency), real-time adaptation.
- AUIT adaptation objective types (map user phrases to ui_intent/params when relevant): AnchorToTarget (pin/follow target) -> anchor_target; AvoidAdaptWhileMoving -> unlock_pose or adapt; Collision (avoid overlap) -> adapt; ConstantViewSize (stable size on screen) -> keep_in_view; DistanceInterval (prefer distance range) -> min_distance_meters; FieldOfView (keep in FOV) -> keep_in_view true; LookTowards (face user) -> keep_in_view or adapt; Occlusion (don't block my view / get UI out of the way) -> visibility=""visible"", keep_in_view=""false"" (app pushes UI to the side); SpatialCoherence (layout consistency) -> adapt or optimize; SurfaceMagnetism (stick to surface) -> adapt. Common XR concerns: element reachability -> make_reachable; visibility -> make_visible, keep_in_view; consistency -> adapt, optimize.

Extract intent from meaning, not keywords. Use paraphrases and context. Examples of patterns:

Visibility and in-view:
- Any request to keep UI visible, in view, in FOV, or to see the UI -> set keep_in_view=""true"", visibility=""visible"", ui_intent='make_visible'. Optionally set keep_in_view_angle_deg (e.g. 10–15) for view cone.
- ""Don't block my view"" / ""get the UI out of the way"" / ""it's blocking"" -> ui_intent='occlusion', keep_in_view=""false"", visibility=""visible"". (The app will push the UI to the side so it does not block the center.)
- Show / make visible / display the UI -> visibility=""visible"", ui_intent='make_visible'.
- Hide / get rid of / close the UI -> visibility=""hidden"", ui_intent (optional).

Reach and distance:
- Bring closer / easier to reach / too far / bring it near / in reach -> ui_intent='make_reachable', set max_distance_meters (e.g. 0.8 or 1.0) so the UI is PULLED IN. Do NOT set min_distance_meters for ""bring closer"".
- Too close / in my face / move it back / push it away -> ui_intent='make_reachable', set min_distance_meters to a larger value (e.g. 1.0 or 1.2) so the UI is pushed back. Optionally set max_distance_meters to 0 to leave it unchanged.

Place, put, move, anchor — disambiguate by intent (language is ambiguous; infer from context):
- MOVE = change position only. ""Move X left/right/up/down"" / ""move it to the right"" / ""move the cube somewhere"" -> move.mode=""relative"" (or ""absolute"" if coordinates given). Do NOT set anchor_target unless they also say attach/pin/follow.
- PLACE / PUT = can mean (1) set position somewhere, or (2) fix here/there (lock). Use context: ""Place the cube at x 1 y 2"" / ""put it over there"" (position) -> move.mode=""absolute"" and world_position; ""place it here"" / ""put it right here"" / ""leave it there"" (fix in place) -> anchor_target=""world"", lock_world_pose=""true"". ""Put the menu in front of me"" -> domain='auit', move or keep_in_view; ""put the cube on my hand"" -> anchor_target=""hand"".
- ANCHOR / ATTACH / PIN / FOLLOW = stick to something. ""Anchor to world/hand/head/torso"" / ""attach to my hand"" / ""pin here"" / ""follow my head"" -> anchor_target=world|hand|head|torso. ""Unpin"" / ""release"" / ""stop following"" -> anchor_target=""none"", unlock_pose=""true"".
- When phrasing is ambiguous (e.g. ""put it there"", ""move it somewhere""), prefer: if ""here""/""there""/""in place"" suggests fixing -> anchor; if direction or ""to the right""/""closer"" suggests repositioning -> move. For ""somewhere"" with no anchor words -> use move (relative nudge or unchanged if no direction).

Lock and unlock (anchor_target for cube/UI):
- Pin here / anchor to world / lock in place -> anchor_target=""world"" (and for UI: lock_world_pose=""true"").
- Follow hand / attach to hand -> anchor_target=""hand"".
- Follow torso / attach to body/chest -> anchor_target=""torso"".
- Follow head / attach to head -> anchor_target=""head"".
- Resume / unlock / auto mode -> unlock_pose=""true"", lock_world_pose=""false"", or anchor_target=""none"".

Reset vs Undo:
- Reset UI / back to default (clear constraints to defaults) -> ui_intent='reset'. Omit or set lock_world_pose=""false"", unlock_pose=""true"", keep_in_view=""false"" as appropriate.
- Undo / revert / take that back = revert the LAST change only -> ui_intent='undo'. Do not use 'reset' for undo.

Movement (see Place/put/move/anchor disambiguation above for ambiguous cases):
- ""Move X left/right/up/down/forward/back"" / ""move it to the right"" / ""move the cube somewhere"" (with direction) -> move.mode=""relative"", delta_meters (x=right, y=up, z=forward in camera space). Cube -> domain='world_object', target='cube'. Use small values (e.g. 0.1–0.3 m).
- ""Place X at ..."" / ""Put X there"" (position, not lock) -> move.mode=""absolute"", world_position. ""Place here"" / ""put it right here"" (fix in place) -> use anchor_target=""world"" instead, move.mode=""none"".
- Small nudge for UI (closer, left, right): domain='auit', move.mode=""relative"", delta_meters as above.
- Absolute position when coordinates or clear location: move.mode=""absolute"" and world_position.

Set keep_in_view, lock_world_pose, unlock_pose to ""unchanged"" when the user's command does not imply that constraint. Always fill visibility, move, ui_intent, anchor_target. Anchor_target values: world | hand | torso | head | none. For domain='world_object', use visibility, move, anchor_target (world/hand/torso/head/none) for the cube.

ui_intent values (use exactly): make_visible | make_reachable | adapt | optimize | reset | undo | field_of_view | constant_view_size | look_towards | occlusion | distance_interval | spatial_coherence | collision | surface_magnetism | avoid_adapt_while_moving. For multiple objectives in one command use ui_intents (array of these strings). For pin/follow use anchor_target only. For undo set ui_intent='undo' only. When no movement, set move.mode=""none"" and leave world_position/delta_meters zero.

Environment understanding (use the list below for spatial references):
- You will receive the current virtual environment: every known object and its world position (x, y, z in meters). Use this to resolve references like ""put UI to the right of the cube"", ""place the menu left of the table"", ""move the cube in front of me"".
- Object names in the environment list are the exact target names (e.g. cube, or the UI element name like MenuPanel). When the user says ""the cube"" or ""cube"", use the position of ""cube""; when they say ""the UI"" or ""menu"", use the UI element name that appears in the list.
- Spatial relations: ""to the right of X"" = X's position + (0.3 to 0.5, 0, 0) in world (or more if ""far right""). ""Left of X"" = X + (-0.3 to -0.5, 0, 0). ""In front of X"" = X + (0, 0, 0.3 to 0.5). ""Behind X"" = X + (0, 0, -0.3 to -0.5). ""Above X"" = X + (0, 0.3 to 0.5, 0). ""Next to X"" = same as right or left (use 0.3–0.5 m offset). ""In front of me"" / ""in front of camera"" = use camera position + (0, 0, 0.5 to 1.0) for in-front.
- Output: use move.mode=""absolute"" and set world_position to the computed (x, y, z). Set domain and target so the correct object moves (UI -> domain='auit' and target=that UI's name; cube -> domain='world_object', target='cube').
- New objects may appear in the environment list as the user adds them; always use whatever objects are listed. If the user refers to an object not in the list, infer the closest match (e.g. ""the box"" might be ""cube"").

Real-world environment (physical space from the headset — walls, floor, ceiling, table, desk, door, window, monitor, etc.):
- The app provides a list of DETECTED REAL-WORLD surfaces and objects from the headset's scene understanding (MRUK/Scene API). The list includes large surfaces (walls, floor, ceiling, table, door, window, monitor, couch) AND smaller or miscellaneous items: ""other"" / ""object"" (e.g. bottle, small furniture, uncategorized), ""wall_art"" (e.g. whiteboard, painting, poster), ""scene_surface"". Use this for any placement command: ""put cube on the table"", ""on the whiteboard"", ""on the bottle"", ""next to the door"", ""on the wall"".
- When the user says ""on the table"" / ""on the desk"" / ""on the floor"" / ""on the monitor"" / ""on the couch"" / ""on the whiteboard"" / ""on the bottle"" / ""on the [object]"", find the best-matching entry in the real-world list (table, floor, monitor, couch, wall_art, other, object, etc.) and use its center position. Output domain='world_object', target='cube', move.mode=""absolute"", world_position = (x, y, z) of that surface's center. For ""on"" a horizontal surface (table, floor, desk), the given center is the placement point; for vertical (wall, whiteboard) use the center as the placement point.
- When the user says ""next to the door"" / ""by the window"" / ""on the wall"", use the real-world list to get that object's position and add a small offset (e.g. 0.3–0.5 m) for ""next to"" or ""by""; use the surface center for ""on the wall"" (vertical surface).
- If the real-world list says ""(No real-world scene loaded)"", you cannot fulfill placement on physical surfaces; reply with clarify and response_text explaining that scene setup is needed, or use camera-relative placement (e.g. ""in front of me"").

{environmentDescription}

{realEnvironmentDescription}

Command memory and refinement:
{commandHistoryBlock}
- Use the history above to resolve ""it"", ""that"", ""the menu"", ""the cube"" when the user does not name the object: the LAST command's domain and target are the default. E.g. last was ""move menu right"" target=MenuPanel -> ""move it upper"" means domain=auit, target=MenuPanel, move relative (0, 0.2, 0). ""A bit more"" / ""again"" = same action as last, same target.
- Undo / revert: when the user says ""undo"", ""revert"", ""revert it"", ""undo that"", ""take that back"", ""go back"", set ui_intent='undo'. The app will restore the state from before the last command. Do not fill move or anchor for undo.

{conversationSequenceBlock}

Runtime capabilities (use for target names and available actions):
{capabilities}";

        string requestJson = BuildResponsesApiRequestJson(systemPrompt, userCommand);

        using (var req = new UnityWebRequest(ENDPOINT, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(requestJson));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + OPENAI_API_KEY);
            req.chunkedTransfer = false;

            Log("Sending request...");
            yield return req.SendWebRequest();

            Log($"HTTP done. result={req.result}, code={req.responseCode}");

            string raw = req.downloadHandler != null ? req.downloadHandler.text : "(no body)";

            if (req.result != UnityWebRequest.Result.Success)
            {
                LogMultiline("OpenAI error response", raw);
                string userMessage = TryParseOpenAIError(raw);
                if (string.IsNullOrEmpty(userMessage))
                    userMessage = "Request failed. Check connection and API key.";
                else
                    userMessage = "Request failed: " + userMessage;
                SetResponseText(userMessage);
                HideLoading();
                yield break;
            }

            ResponsesApiEnvelope env;
            try
            {
                env = JsonUtility.FromJson<ResponsesApiEnvelope>(raw);
            }
            catch (Exception e)
            {
                Log("ERROR: Failed to parse Responses envelope: " + e.Message);
                LogMultiline("Raw", raw);
                SetResponseText("Failed to read API response.");
                HideLoading();
                yield break;
            }

            string json = ExtractAssistantText(env);
            if (string.IsNullOrWhiteSpace(json))
            {
                Log("ERROR: No output_text found.");
                LogMultiline("Raw", raw);
                SetResponseText("No response from model.");
                HideLoading();
                yield break;
            }

            LogMultiline("Model JSON", json);
            if (debugText != null) debugText.text = json;

            ShowLoading("Applying...");

            StructuredCommand cmd;
            try
            {
                cmd = JsonUtility.FromJson<StructuredCommand>(json);
            }
            catch (Exception e)
            {
                Log("ERROR: Failed parsing command JSON into StructuredCommand: " + e.Message);
                SetResponseText("Could not understand the response. Try rephrasing.");
                HideLoading();
                yield break;
            }

            // Show LLM response in UI (success, failure, or clarification)
            string responseMessage = !string.IsNullOrEmpty(cmd.response_text) ? cmd.response_text.Trim() : "Done.";
            SetResponseText(responseMessage);

            // API controls all behavior via app_action; app only follows. Conversation sequence is in the prompt for next turn.
            string appAction = (cmd.app_action ?? "apply").Trim().ToLowerInvariant();

            if (appAction == "undo")
            {
                HideClarifyOptions();
                RestoreFromUndo();
                Log("Undo applied.");
                PushConversationTurn(userCommand, responseMessage, null);
                HideLoading();
                yield break;
            }

            if (appAction == "clarify")
            {
                PushConversationTurn(userCommand, responseMessage, BuildSuggestedCommandSummary(cmd));
                Log("Clarification shown; conversation turn stored for sequence.");
                if (clarifyOptionsContainer != null && cmd.clarify_options != null && cmd.clarify_options.Length > 0)
                    ShowClarifyOptions(cmd.clarify_options);
                HideLoading();
                yield break;
            }

            // apply (default)
            HideClarifyOptions();
            PushConversationTurn(userCommand, responseMessage, null);
            SnapshotForUndo();
            _router.RouteAndApply(cmd);
            PushCommandHistory(userCommand, cmd);
            Log("Done applying command.");
            HideLoading();
        }
    }

    private void SetResponseText(string text)
    {
        if (responseText != null) responseText.text = text ?? "";
        if (speakResponseWithTts && !string.IsNullOrWhiteSpace(text))
            SpeakResponse(text.Trim());
    }

    private void HideClarifyOptions()
    {
        if (clarifyOptionsContainer == null) return;
        for (int i = clarifyOptionsContainer.childCount - 1; i >= 0; i--)
        {
            var child = clarifyOptionsContainer.GetChild(i);
            if (Application.isPlaying)
                Destroy(child.gameObject);
            else
                DestroyImmediate(child.gameObject);
        }
        // Do NOT SetActive(false) on the container — it may be your controller canvas; we only clear the option buttons.
    }

    private void ShowClarifyOptions(string[] options)
    {
        if (clarifyOptionsContainer == null || options == null || options.Length == 0) return;
        HideClarifyOptions();
        float buttonWidth = 30f;
        float buttonHeight = 14f;
        float horizontalSpacing = 4f;
        float verticalSpacing = 4f;
        int columns = 2;
        int index = 0;
        foreach (string option in options)
        {
            if (string.IsNullOrWhiteSpace(option)) continue;
            string label = option.Trim();
            Button btn = CreateClarifyOptionButton(label);
            if (btn != null)
            {
                btn.transform.SetParent(clarifyOptionsContainer, false);
                var rect = btn.GetComponent<RectTransform>();
                if (rect != null)
                {
                    // Anchor to bottom-left of the container so the grid sits at the bottom of the controller canvas.
                    rect.anchorMin = new Vector2(0f, 0f);
                    rect.anchorMax = new Vector2(0f, 0f);
                    rect.pivot = new Vector2(0f, 0f);

                    int col = index % columns;
                    int row = index / columns;
                    float x = col * (buttonWidth + horizontalSpacing);
                    // Start row 0 at clarifyOptionsBottomOffset above the bottom of the container.
                    float y = clarifyOptionsBottomOffset + row * (buttonHeight + verticalSpacing);
                    rect.anchoredPosition = new Vector2(x, y);
                    rect.sizeDelta = new Vector2(buttonWidth, buttonHeight);
                }
                btn.onClick.AddListener(() => OnClarifyOptionClicked(label));
                index++;
            }
        }
    }

    private Button CreateClarifyOptionButton(string label)
    {
        if (clarifyOptionButtonPrefab != null)
        {
            var btn = Instantiate(clarifyOptionButtonPrefab);
            var tmp = btn.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null) tmp.text = label;
            return btn;
        }
        var go = new GameObject("ClarifyOption_" + label);
        var rect = go.AddComponent<RectTransform>();
        // Very small default button; exact size is overridden in ShowClarifyOptions.
        rect.sizeDelta = new Vector2(30f, 14f);
        var image = go.AddComponent<Image>();
        image.color = new Color(0.85f, 0.85f, 0.85f); // light grey
        if (clarifyOptionSprite != null)
        {
            image.sprite = clarifyOptionSprite;
            image.type = Image.Type.Sliced;
        }
        var button = go.AddComponent<Button>();
        var colors = button.colors;
        colors.highlightedColor = new Color(0.4f, 0.5f, 0.65f);
        button.colors = colors;
        var textGo = new GameObject("Text");
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = textRect.offsetMax = Vector2.zero;
        textGo.transform.SetParent(go.transform, false);
        var labelText = textGo.AddComponent<TextMeshProUGUI>();
        labelText.text = label;
        labelText.alignment = TextAlignmentOptions.Center;
        labelText.fontSize = 5f; // ~50% of previous 10f
        labelText.color = Color.black;
        return button;
    }

    private void OnClarifyOptionClicked(string option)
    {
        HideClarifyOptions();
        if (commandInput != null) commandInput.text = option;
        StartCoroutine(CallLLMAndApply(option));
    }

    /// <summary>Speak the given text via Meta Voice SDK TTS (Wit). Uses TTSSpeaker if assigned.</summary>
    private void SpeakResponse(string text)
    {
        if (string.IsNullOrEmpty(text) || !speakResponseWithTts || ttsSpeaker == null) return;
        if (text.Length > ttsMaxLength) text = text.Substring(0, ttsMaxLength) + "…";
        ttsSpeaker.Speak(text);
    }

    private void ShowLoading(string message)
    {
        _isLoading = true;
        if (loadingPanel != null) loadingPanel.SetActive(true);
        if (loadingText != null) loadingText.text = message;
        if (loadingSpinner != null) loadingSpinner.gameObject.SetActive(true);
        if (executeButton != null) executeButton.interactable = false;
    }

    private void HideLoading()
    {
        _isLoading = false;
        if (loadingPanel != null) loadingPanel.SetActive(false);
        if (loadingText != null) loadingText.text = "";
        if (loadingSpinner != null) loadingSpinner.gameObject.SetActive(false);
        if (executeButton != null) executeButton.interactable = true;
    }

    private void Update()
    {
        if (_isLoading && loadingSpinner != null)
            loadingSpinner.Rotate(0f, 0f, -loadingSpinnerSpeedDegPerSec * Time.deltaTime);
    }

    // ============================================================
    // REQUEST JSON (Responses API) - IMPORTANT: input_text!
    // ============================================================

    private string BuildResponsesApiRequestJson(string systemPrompt, string userCommand)
    {
        // Schema: all behavior is driven by model output. No keyword parsing in app.
        string schema =
@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""raw"", ""domain"", ""target"", ""action"", ""priority"", ""params"", ""response_text"", ""confidence"", ""app_action"", ""clarify_options""],
  ""properties"": {
    ""raw"": { ""type"": ""string"" },
    ""domain"": { ""type"": ""string"" },
    ""target"": { ""type"": ""string"" },
    ""action"": { ""type"": ""string"" },
    ""priority"": { ""type"": ""string"" },
    ""response_text"": { ""type"": ""string"" },
    ""confidence"": { ""type"": ""number"" },
    ""app_action"": { ""type"": ""string"", ""enum"": [""apply"", ""undo"", ""clarify""] },
    ""clarify_options"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""When app_action is clarify and there are choices, list button labels e.g. [Yes, No]; otherwise use []"" },
    ""params"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""required"": [""ui_intent"", ""ui_intents"", ""anchor_target"", ""visibility"", ""keep_in_view"", ""lock_world_pose"", ""unlock_pose"", ""min_distance_meters"", ""max_distance_meters"", ""keep_in_view_angle_deg"", ""move""],
      ""properties"": {
        ""ui_intent"": { ""type"": ""string"" },
        ""ui_intents"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
        ""anchor_target"": { ""type"": ""string"" },
        ""visibility"": { ""type"": ""string"" },
        ""keep_in_view"": { ""type"": ""string"", ""enum"": [""true"", ""false"", ""unchanged""] },
        ""lock_world_pose"": { ""type"": ""string"", ""enum"": [""true"", ""false"", ""unchanged""] },
        ""unlock_pose"": { ""type"": ""string"", ""enum"": [""true"", ""false"", ""unchanged""] },
        ""min_distance_meters"": { ""type"": ""number"" },
        ""max_distance_meters"": { ""type"": ""number"" },
        ""keep_in_view_angle_deg"": { ""type"": ""number"" },
        ""move"": {
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""required"": [""mode"", ""world_position"", ""delta_meters""],
          ""properties"": {
            ""mode"": { ""type"": ""string"" },
            ""world_position"": {
              ""type"": ""object"",
              ""additionalProperties"": false,
              ""required"": [""x"", ""y"", ""z""],
              ""properties"": {
                ""x"": { ""type"": ""number"" },
                ""y"": { ""type"": ""number"" },
                ""z"": { ""type"": ""number"" }
              }
            },
            ""delta_meters"": {
              ""type"": ""object"",
              ""additionalProperties"": false,
              ""required"": [""x"", ""y"", ""z""],
              ""properties"": {
                ""x"": { ""type"": ""number"" },
                ""y"": { ""type"": ""number"" },
                ""z"": { ""type"": ""number"" }
              }
            }
          }
        }
      }
    }
  }
}";

        string sysEsc = JsonEscape(systemPrompt);
        string userEsc = JsonEscape(userCommand);

        return $@"{{
  ""model"": ""{model}"",
  ""temperature"": {temperature.ToString(CultureInfo.InvariantCulture)},
  ""input"": [
    {{
      ""role"": ""system"",
      ""content"": [ {{ ""type"": ""input_text"", ""text"": ""{sysEsc}"" }} ]
    }},
    {{
      ""role"": ""user"",
      ""content"": [ {{ ""type"": ""input_text"", ""text"": ""{userEsc}"" }} ]
    }}
  ],
  ""text"": {{
    ""format"": {{
      ""type"": ""json_schema"",
      ""name"": ""adaptive_command"",
      ""strict"": true,
      ""schema"": {schema}
    }}
  }}
}}";
    }

    // ============================================================
    // Responses envelope parsing
    // ============================================================

    [Serializable] private class ResponsesApiEnvelope { public OutputItem[] output; }
    [Serializable] private class OutputItem { public string type; public string role; public ContentItem[] content; }
    [Serializable] private class ContentItem { public string type; public string text; }

    [Serializable] private class OpenAIErrorEnvelope { public OpenAIErrorInner error; }
    [Serializable] private class OpenAIErrorInner { public string message; public string code; }

    private static string TryParseOpenAIError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var env = JsonUtility.FromJson<OpenAIErrorEnvelope>(raw);
            if (env?.error != null && !string.IsNullOrEmpty(env.error.message))
                return env.error.message.Trim();
        }
        catch { /* ignore parse errors */ }
        return null;
    }

    private static string ExtractAssistantText(ResponsesApiEnvelope env)
    {
        if (env?.output == null) return null;

        foreach (var item in env.output)
        {
            if (item == null) continue;
            if (item.type != "message") continue;
            if (item.content == null) continue;

            foreach (var c in item.content)
            {
                if (c != null && c.type == "output_text")
                    return c.text;
            }
        }
        return null;
    }

    // ============================================================
    // Command DTO
    // ============================================================

    [Serializable]
    public class StructuredCommand
    {
        public string raw;     // original user command (for AUIT auto-target selection)
        public string domain;  // "auit" | "world_object"
        public string target;  // UI element name or "auto" or "cube"
        public string action;  // free string; used mostly for debugging
        public string priority;
        /// <summary>Short user-facing message: success, failure, or clarification question.</summary>
        public string response_text;
        /// <summary>Confidence in interpreting the command (0-1). Informational; app behavior is controlled by app_action.</summary>
        public float confidence;
        /// <summary>What the app must do: "apply" = execute command, "undo" = restore previous state, "clarify" = only show response_text (no execution).</summary>
        public string app_action;
        /// <summary>When app_action is "clarify", optional list of button labels (e.g. ["Yes", "No"], ["menu", "cube"]) so the user can pick by clicking.</summary>
        public string[] clarify_options;
        public Params @params;

        [Serializable]
        public class Params
        {
            public string ui_intent;
            /// <summary>Multiple objectives in one command (e.g. ["field_of_view", "occlusion"]). When set, app merges state for all.</summary>
            public string[] ui_intents;
            public string anchor_target;
            public string visibility;
            // Explicit constraints from model (app applies these only; no raw parsing)
            public string keep_in_view;       // "true" | "false"
            public string lock_world_pose;    // "true" | "false"
            public string unlock_pose;        // "true" | "false"
            public float min_distance_meters; // >0 to set min distance (e.g. "too close" -> push back)
            public float max_distance_meters;  // >0 to set max distance ("bring closer" -> pull in)
            public float keep_in_view_angle_deg; // >0 to set view cone
            public Move move;

            [Serializable]
            public class Move
            {
                // "absolute" | "relative" | "none"
                public string mode;
                public Vec3 world_position;
                public Vec3 delta_meters;
            }

            [Serializable]
            public class Vec3 { public float x, y, z; }
        }
    }

    // ============================================================
    // Router + providers
    // ============================================================

    private enum ConflictPolicy { PreferHigherPriority, MergeNoPriority }

    private class AdaptationRouter
    {
        private readonly List<IAdaptationProvider> _providers = new List<IAdaptationProvider>();
        private readonly ConflictPolicy _policy;

        public AdaptationRouter(ConflictPolicy policy) { _policy = policy; }

        public void Register(IAdaptationProvider provider)
        {
            if (provider == null) return;
            _providers.Add(provider);
            _providers.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public string GetCapabilitiesSummary()
        {
            var sb = new StringBuilder();
            foreach (var p in _providers)
                sb.AppendLine($"- {p.Name} (priority {p.Priority}): {p.GetCapabilitiesSummary()}");
            return sb.ToString();
        }

        public string GetCapabilitiesForPrompt()
        {
            var sb = new StringBuilder();
            foreach (var p in _providers)
                sb.AppendLine($"{p.Name}: {p.GetCapabilitiesForPrompt()}");
            return sb.ToString();
        }

        public void RouteAndApply(StructuredCommand cmd)
        {
            if (cmd == null)
            {
                Debug.LogError(LOGP + "RouteAndApply: cmd is null.");
                return;
            }

            var candidates = new List<IAdaptationProvider>();
            foreach (var p in _providers)
                if (p.CanHandle(cmd)) candidates.Add(p);

            if (candidates.Count == 0)
            {
                Debug.LogWarning(LOGP + "No provider can handle this command.");
                return;
            }

            var plans = new List<AdaptationPlan>();
            foreach (var p in candidates)
            {
                var plan = p.BuildPlan(cmd);
                if (plan == null) continue;
                plan.SourceProvider = p.Name;
                plan.SourcePriority = p.Priority;
                plans.Add(plan);
            }

            var merged = ConflictResolver.Merge(plans, _policy);

            // Apply in provider priority order
            foreach (var p in _providers)
            {
                if (merged.PlansByProvider.TryGetValue(p.Name, out var providerPlan))
                    p.Apply(providerPlan);
            }
        }
    }

    private interface IAdaptationProvider
    {
        string Name { get; }
        int Priority { get; }
        bool CanHandle(StructuredCommand cmd);
        AdaptationPlan BuildPlan(StructuredCommand cmd);
        void Apply(AdaptationPlan plan);
        string GetCapabilitiesSummary();
        string GetCapabilitiesForPrompt();
    }

    private class AdaptationPlan
    {
        public string SourceProvider;
        public int SourcePriority;

        public bool WantsUIAdaptation;
        public bool WantsMove;
        public bool WantsVisibility;
        public bool WantsAnchor;

        public StructuredCommand Cmd;

        public Dictionary<string, AdaptationPlan> PlansByProvider = new Dictionary<string, AdaptationPlan>();
    }

    private static class ConflictResolver
    {
        public static AdaptationPlan Merge(List<AdaptationPlan> plans, ConflictPolicy policy)
        {
            var merged = new AdaptationPlan();

            if (plans == null || plans.Count == 0)
                return merged;

            if (plans.Count == 1)
            {
                merged.PlansByProvider[plans[0].SourceProvider] = plans[0];
                return merged;
            }

            if (policy == ConflictPolicy.MergeNoPriority)
            {
                foreach (var p in plans)
                    merged.PlansByProvider[p.SourceProvider] = p;
                return merged;
            }

            // PreferHigherPriority:
            AdaptationPlan bestUI = Best(plans, p => p.WantsUIAdaptation);
            AdaptationPlan bestMove = Best(plans, p => p.WantsMove);
            AdaptationPlan bestVis = Best(plans, p => p.WantsVisibility);
            AdaptationPlan bestAnchor = Best(plans, p => p.WantsAnchor);

            AddIfNotNull(merged, bestUI);
            AddIfNotNull(merged, bestMove);
            AddIfNotNull(merged, bestVis);
            AddIfNotNull(merged, bestAnchor);

            return merged;
        }

        private static AdaptationPlan Best(List<AdaptationPlan> plans, Func<AdaptationPlan, bool> wants)
        {
            AdaptationPlan best = null;
            foreach (var p in plans)
            {
                if (!wants(p)) continue;
                if (best == null || p.SourcePriority > best.SourcePriority)
                    best = p;
            }
            return best;
        }

        private static void AddIfNotNull(AdaptationPlan merged, AdaptationPlan plan)
        {
            if (plan == null) return;
            merged.PlansByProvider[plan.SourceProvider] = plan;
        }
    }

    // ============================================================
    // AUIT provider: "FOR SURE" constraint memory + optional trigger
    // ============================================================

    private class AUITProvider : IAdaptationProvider
    {
        public string Name => "AUIT";
        public int Priority => 100;

        private readonly List<GameObject> _uiElements;
        private readonly AdaptiveCommandOrchestrator _orch;

        public AUITProvider(List<GameObject> uiElements, AdaptiveCommandOrchestrator orch)
        {
            _uiElements = uiElements ?? new List<GameObject>();
            _orch = orch;
        }

        public bool CanHandle(StructuredCommand cmd)
        {
            if (cmd == null) return false;

            // Critical: cube/world_object commands must NEVER affect the UI. Domain wins.
            if (!string.IsNullOrWhiteSpace(cmd.domain) &&
                cmd.domain.Equals("world_object", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(cmd.domain) &&
                cmd.domain.Equals("auit", StringComparison.OrdinalIgnoreCase))
                return true;

            if (cmd.@params != null && !string.IsNullOrWhiteSpace(cmd.@params.ui_intent))
                return true;
            if (cmd.@params != null && cmd.@params.ui_intents != null && cmd.@params.ui_intents.Length > 0)
                return true;

            // also accept if raw clearly refers to UI (not the cube)
            var raw = (cmd.raw ?? "").ToLowerInvariant();
            if (raw.Contains("ui") || raw.Contains("panel") || raw.Contains("menu") || raw.Contains("hud") || raw.Contains("interface"))
                return true;

            return false;
        }

        public AdaptationPlan BuildPlan(StructuredCommand cmd)
        {
            // WantsUIAdaptation is true, but commands will be enforced in LateUpdate via state.
            return new AdaptationPlan { Cmd = cmd, WantsUIAdaptation = true };
        }

        public void Apply(AdaptationPlan plan)
        {
            if (plan?.Cmd == null) return;

            var cmd = plan.Cmd;
            string intent = cmd.@params != null ? cmd.@params.ui_intent : null;
            if (string.IsNullOrWhiteSpace(intent)) intent = "adapt";

            var target = FindTargetUI(cmd.target, cmd.raw);
            if (target == null)
            {
                Debug.LogWarning(LOGP + "AUIT: No UI element found (list is empty or all null).");
                return;
            }

            // 1) ALWAYS update persistent state first (FOR SURE)
            var st = _orch != null ? _orch.GetOrCreateUiState(target) : null;
            if (st != null) ApplyCommandToState(cmd, target.transform, st);

            Debug.Log(LOGP + $"AUIT Apply: target={target.name}, intent={intent} (constraints updated)");

            // 2) Optionally invoke AUIT triggers, but never "eat" the command.
            // If continuous trigger is active, do not manually invoke. LateUpdate will enforce constraints anyway.
            var continuous = target.GetComponentInChildren<AUIT.AdaptationTriggers.ContinuousOptimizationTrigger>(true);
            if (continuous != null && continuous.isActiveAndEnabled)
            {
                Debug.Log(LOGP + "AUIT: ContinuousOptimizationTrigger is active; not manually invoking (constraints enforced in LateUpdate).");
                return;
            }

            // Otherwise: try manual trigger to let AUIT respond to the new intent (optional).
            bool invoked = TryInvokeAUIT(target);
            if (!invoked)
                Debug.LogWarning(LOGP + "AUIT: Could not trigger AUIT on this object. Check that AdaptationTrigger exists or use ContinuousOptimizationTrigger.");
        }

        private void ApplyCommandToState(StructuredCommand cmd, Transform t, UiCommandState st)
        {
            if (cmd.@params == null) return;
            var p = cmd.@params;
            float defaultMin = _orch != null ? _orch.defaultMinDistanceMeters : 0.6f;
            float defaultAngle = _orch != null ? _orch.defaultKeepInViewAngleDeg : 10f;

            // Visibility: only from params
            if (!string.IsNullOrWhiteSpace(p.visibility))
            {
                if (string.Equals(p.visibility, "visible", StringComparison.OrdinalIgnoreCase))
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                }
                else if (string.Equals(p.visibility, "hidden", StringComparison.OrdinalIgnoreCase))
                {
                    st.forceHidden = true;
                    st.forceVisible = false;
                }
            }

            // Explicit keep_in_view from model ("unchanged" = do not modify)
            if (!string.IsNullOrWhiteSpace(p.keep_in_view) && !string.Equals(p.keep_in_view, "unchanged", StringComparison.OrdinalIgnoreCase))
            {
                st.keepInView = string.Equals(p.keep_in_view, "true", StringComparison.OrdinalIgnoreCase);
                st.maxGazeAngleDeg = p.keep_in_view_angle_deg > 0 ? p.keep_in_view_angle_deg : defaultAngle;
            }

            // Lock / unlock from model
            if (!string.IsNullOrWhiteSpace(p.unlock_pose) && string.Equals(p.unlock_pose, "true", StringComparison.OrdinalIgnoreCase))
                st.lockWorldPose = false;
            if (!string.IsNullOrWhiteSpace(p.lock_world_pose) && !string.Equals(p.lock_world_pose, "unchanged", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(p.lock_world_pose, "true", StringComparison.OrdinalIgnoreCase))
                {
                    st.lockWorldPose = true;
                    st.lockedWorldPos = t.position;
                    st.lockedWorldRot = t.rotation;
                }
                else
                    st.lockWorldPose = false;
            }
            // Anchor: world = lock pose; hand/torso/head = parent UI to that transform; none = unparent
            if (!string.IsNullOrWhiteSpace(p.anchor_target))
            {
                if (string.Equals(p.anchor_target, "world", StringComparison.OrdinalIgnoreCase))
                {
                    st.lockWorldPose = true;
                    st.lockedWorldPos = t.position;
                    st.lockedWorldRot = t.rotation;
                }
                else if (string.Equals(p.anchor_target, "none", StringComparison.OrdinalIgnoreCase))
                {
                    st.lockWorldPose = false;
                    t.SetParent(null, true);
                }
                else if (_orch != null)
                {
                    Transform anchor = null;
                    if (string.Equals(p.anchor_target, "hand", StringComparison.OrdinalIgnoreCase)) anchor = _orch.handAnchor;
                    else if (string.Equals(p.anchor_target, "torso", StringComparison.OrdinalIgnoreCase)) anchor = _orch.torsoAnchor;
                    else if (string.Equals(p.anchor_target, "head", StringComparison.OrdinalIgnoreCase)) anchor = _orch.headAnchor;
                    if (anchor != null)
                    {
                        st.lockWorldPose = false;
                        t.SetParent(anchor, true);
                    }
                }
            }

            // Min distance from model
            if (p.min_distance_meters > 0)
            {
                st.clampMinDistance = true;
                st.minDistanceMeters = p.min_distance_meters;
            }

            // Multi-objective: apply ALL intents in ui_intents (merge state), then fall back to single ui_intent if no array
            bool isMultiObjective = p.ui_intents != null && p.ui_intents.Length > 0;
            if (isMultiObjective)
            {
                foreach (var one in p.ui_intents)
                {
                    if (string.IsNullOrWhiteSpace(one)) continue;
                    ApplyOneIntentToState(one.Trim(), p, st, defaultMin, defaultAngle, true);
                }
                if (st.keepInView && st.avoidOcclusion && st.maxGazeAngleDeg < st.minGazeAngleDeg)
                    st.maxGazeAngleDeg = Mathf.Max(45f, st.minGazeAngleDeg + 10f);
            }
            else
            {
                string intent = (p.ui_intent ?? "").Trim();
                if (!string.IsNullOrEmpty(intent))
                    ApplyOneIntentToState(intent, p, st, defaultMin, defaultAngle, false);
            }

            // Move: only from params.move (relative nudge or absolute world position, e.g. from "put UI to the right of cube")
            var mv = p.move;
            if (mv != null && !string.IsNullOrWhiteSpace(mv.mode))
            {
                if (mv.mode.Equals("absolute", StringComparison.OrdinalIgnoreCase) && mv.world_position != null)
                {
                    Vector3 pos = new Vector3(mv.world_position.x, mv.world_position.y, mv.world_position.z);
                    t.position = pos;
                    if (st.lockWorldPose) { st.lockedWorldPos = pos; st.lockedWorldRot = t.rotation; }
                }
                else if (mv.mode.Equals("relative", StringComparison.OrdinalIgnoreCase) && mv.delta_meters != null)
                {
                    Transform refTf = _orch != null && _orch.directionReference != null
                        ? _orch.directionReference
                        : (Camera.main != null ? Camera.main.transform : null);
                    Vector3 right = refTf != null ? refTf.right : Vector3.right;
                    Vector3 up = refTf != null ? refTf.up : Vector3.up;
                    Vector3 fwd = refTf != null ? refTf.forward : Vector3.forward;
                    Vector3 delta = right * mv.delta_meters.x + up * mv.delta_meters.y + fwd * mv.delta_meters.z;
                    if (st.lockWorldPose)
                        st.lockedWorldPos += delta;
                    else
                        st.pendingWorldDelta += delta;
                }
            }
        }

        private void ApplyOneIntentToState(string intent, StructuredCommand.Params p, UiCommandState st, float defaultMin, float defaultAngle, bool isMultiObjective)
        {
            if (string.IsNullOrEmpty(intent)) return;
            string inorm = intent.Replace("_", "").Replace(" ", "").ToLowerInvariant();

            if (string.Equals(intent, "make_visible", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(p.keep_in_view)) st.keepInView = true;
                    if (string.IsNullOrWhiteSpace(p.visibility)) { st.forceVisible = true; st.forceHidden = false; }
                    if (st.keepInView && p.keep_in_view_angle_deg <= 0) st.maxGazeAngleDeg = defaultAngle;
                }
                else if (string.Equals(intent, "make_reachable", StringComparison.OrdinalIgnoreCase))
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                    // "Bring closer" = pull UI in when far (max distance). Do NOT set min here or we push UI away when it's close.
                    float maxReach = p.max_distance_meters > 0 ? p.max_distance_meters : 1.0f;
                    st.clampMaxDistance = true;
                    st.maxDistanceMeters = maxReach;
                    // Only set min if model explicitly asked to push back (e.g. "too close"); otherwise leave as-is
                    if (p.min_distance_meters > 0) { st.clampMinDistance = true; st.minDistanceMeters = p.min_distance_meters; }
                }
                else if (string.Equals(intent, "adapt", StringComparison.OrdinalIgnoreCase) || string.Equals(intent, "optimize", StringComparison.OrdinalIgnoreCase))
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                }
                else if (string.Equals(intent, "reset", StringComparison.OrdinalIgnoreCase))
                {
                    st.lockWorldPose = false;
                    st.forceHidden = false;
                    st.forceVisible = false;
                    st.keepInView = false;
                    st.avoidOcclusion = false;
                    st.pendingWorldDelta = Vector3.zero;
                    st.clampMinDistance = true;
                    st.minDistanceMeters = defaultMin;
                    st.clampMaxDistance = false;
                }
                // AUIT adaptation objectives (accept snake_case or PascalCase: FieldOfView, field_of_view, etc.)
                else if (inorm == "fieldofview" || inorm == "constantviewsize" || inorm == "looktowards")
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                    st.keepInView = true;
                    if (p.keep_in_view_angle_deg <= 0) st.maxGazeAngleDeg = defaultAngle;
                }
                else if (inorm == "occlusion")
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                    st.avoidOcclusion = true;
                    st.minGazeAngleDeg = p.keep_in_view_angle_deg > 0 ? Mathf.Max(p.keep_in_view_angle_deg, 15f) : 22f;
                    if (!isMultiObjective) st.keepInView = false;
                }
                else if (inorm == "distanceinterval")
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                    if (!st.clampMinDistance) { st.clampMinDistance = true; st.minDistanceMeters = p.min_distance_meters > 0 ? p.min_distance_meters : defaultMin; }
                }
                else if (inorm == "spatialcoherence" || inorm == "collision" || inorm == "surfacemagnetism" || inorm == "avoidadaptwhilemoving")
                {
                    st.forceVisible = true;
                    st.forceHidden = false;
                }
                // AnchorToTarget: handled only via anchor_target param (lock/follow), not ui_intent
        }

        private GameObject FindTargetUI(string targetName, string rawCommand)
        {
            if (!string.IsNullOrWhiteSpace(targetName) &&
                !targetName.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var go in _uiElements)
                {
                    if (go == null) continue;
                    if (string.Equals(go.name, targetName, StringComparison.OrdinalIgnoreCase))
                        return go;
                }
            }

            if (_uiElements.Count == 0)
                return null;

            if (string.IsNullOrWhiteSpace(rawCommand))
            {
                foreach (var go in _uiElements)
                    if (go != null) return go;
                return null;
            }

            string cmd = Normalize(rawCommand);

            int bestScore = int.MinValue;
            GameObject best = null;

            foreach (var go in _uiElements)
            {
                if (go == null) continue;

                int score = 0;
                string n = Normalize(go.name);

                if (cmd.Contains(n)) score += 50;

                var tokens = n.Split(' ');
                foreach (var t in tokens)
                {
                    if (t.Length < 3) continue;
                    if (cmd.Contains(t)) score += 10;
                }

                if (n.Contains("menu") && cmd.Contains("menu")) score += 15;
                if (n.Contains("settings") && cmd.Contains("settings")) score += 15;
                if (n.Contains("hud") && (cmd.Contains("hud") || cmd.Contains("ui"))) score += 10;
                if (n.Contains("panel") && cmd.Contains("panel")) score += 10;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = go;
                }
            }

            if (best != null) return best;

            foreach (var go in _uiElements)
                if (go != null) return go;

            return null;
        }

        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.ToLowerInvariant();
            s = s.Replace("_", " ").Replace("-", " ");
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return s.Trim();
        }

        private bool TryInvokeAUIT(GameObject target)
        {
            try
            {
                // 1) If AdaptationTrigger exists, call ApplyStrategy()
                var trig = target.GetComponentInChildren<AUIT.AdaptationTriggers.AdaptationTrigger>(true);
                if (trig != null && trig.isActiveAndEnabled)
                {
                    trig.ApplyStrategy();
                    Debug.Log(LOGP + "AUIT: Called AdaptationTrigger.ApplyStrategy()");
                    return true;
                }

                // 2) If IntervalOptimizationTrigger exists, call ApplyStrategy()
                var interval = target.GetComponentInChildren<AUIT.AdaptationTriggers.IntervalOptimizationTrigger>(true);
                if (interval != null && interval.isActiveAndEnabled)
                {
                    interval.ApplyStrategy();
                    Debug.Log(LOGP + "AUIT: Called IntervalOptimizationTrigger.ApplyStrategy()");
                    return true;
                }

                // 3) Reflection fallback
                var comps = new List<Component>();
                comps.AddRange(target.GetComponentsInChildren<Component>(true));
                comps.AddRange(target.GetComponentsInParent<Component>(true));

                string[] methods = { "ApplyStrategy", "Trigger", "Adapt", "Solve", "Optimize" };

                foreach (var c in comps)
                {
                    if (c == null) continue;
                    var t = c.GetType();
                    string tn = t.FullName ?? t.Name;

                    if (tn.IndexOf("AUIT", StringComparison.OrdinalIgnoreCase) < 0 &&
                        tn.IndexOf("Adaptation", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    foreach (var mn in methods)
                    {
                        // AmbiguousMatchException-safe: enumerate all overloads and pick a 0-arg non-generic
                        MethodInfo best = null;
                        var all = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        foreach (var mi in all)
                        {
                            if (!string.Equals(mi.Name, mn, StringComparison.Ordinal)) continue;
                            if (mi.IsGenericMethodDefinition) continue;
                            if (mi.GetParameters().Length != 0) continue;

                            // Prefer declared-on-type method (more specific) if multiple
                            if (best == null) best = mi;
                            else if (mi.DeclaringType == t) best = mi;
                        }

                        if (best == null) continue;

                        try
                        {
                            best.Invoke(c, null);
                            Debug.Log(LOGP + $"AUIT: Invoked {tn}.{best.Name}()");
                            return true;
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning(LOGP + $"AUIT: invoke failed {tn}.{best.Name}: {e.Message}");
                        }
                    }
                }

                return false;
            }
            catch (Exception e)
            {
                Debug.LogError(LOGP + "AUIT: TryInvokeAUIT crashed: " + e);
                return false;
            }
        }

        public string GetCapabilitiesSummary()
        {
            int n = 0;
            foreach (var go in _uiElements) if (go != null) n++;
            return n > 0 ? $"UI elements registered={n}" : "No UI elements registered";
        }

        public string GetCapabilitiesForPrompt()
        {
            var sb = new StringBuilder();
            sb.Append("UI targets: ");
            if (_uiElements.Count == 0) sb.Append("none");
            else
            {
                bool first = true;
                foreach (var go in _uiElements)
                {
                    if (go == null) continue;
                    if (!first) sb.Append(", ");
                    sb.Append(go.name);
                    first = false;
                }
            }
            sb.Append(". Natural language UI commands map to intents: make_reachable | make_visible | adapt | optimize | reset.");
            sb.Append(" AUIT adaptation objectives (map to intents/params): AnchorToTarget, AvoidAdaptWhileMoving, Collision, ConstantViewSize, DistanceInterval, FieldOfView, LookTowards, Occlusion, SpatialCoherence, SurfaceMagnetism.");
            sb.Append(" If user doesn't name target, target='auto'. Anchor_target for UI: world/hand/torso/head/none.");
            sb.Append(" Commands are enforced FOR SURE by hard constraints in LateUpdate.");
            return sb.ToString();
        }
    }

    // ============================================================
    // Cube provider
    // ============================================================

    private class CubeProvider : IAdaptationProvider
    {
        public string Name => "Cube";
        public int Priority => 10;

        private readonly Transform _cube;
        private readonly Transform _directionReference;
        private readonly Transform _worldAnchor;
        private readonly Transform _handAnchor;
        private readonly Transform _torsoAnchor;
        private readonly Transform _headAnchor;

        public CubeProvider(Transform cube, Transform directionRef, Transform worldAnchor, Transform handAnchor, Transform torsoAnchor, Transform headAnchor)
        {
            _cube = cube;
            _directionReference = directionRef;
            _worldAnchor = worldAnchor;
            _handAnchor = handAnchor;
            _torsoAnchor = torsoAnchor;
            _headAnchor = headAnchor;
        }

        public bool CanHandle(StructuredCommand cmd)
        {
            if (cmd == null) return false;

            if (!string.IsNullOrWhiteSpace(cmd.domain) &&
                !cmd.domain.Equals("world_object", StringComparison.OrdinalIgnoreCase))
                return false;

            if (_cube == null) return false;

            if (string.IsNullOrWhiteSpace(cmd.target)) return true;
            return cmd.target.IndexOf("cube", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public AdaptationPlan BuildPlan(StructuredCommand cmd)
        {
            var plan = new AdaptationPlan { Cmd = cmd };

            var p = cmd.@params;
            if (p != null)
            {
                plan.WantsVisibility = !string.IsNullOrWhiteSpace(p.visibility) && p.visibility != "unchanged";
                plan.WantsAnchor = !string.IsNullOrWhiteSpace(p.anchor_target) && p.anchor_target != "none";
                plan.WantsMove = p.move != null && !string.IsNullOrWhiteSpace(p.move.mode) && p.move.mode != "none";
            }

            return plan;
        }

        public void Apply(AdaptationPlan plan)
        {
            if (_cube == null)
            {
                Debug.LogWarning(LOGP + "CubeProvider: cube is null.");
                return;
            }

            var cmd = plan?.Cmd;
            if (cmd?.@params == null) return;

            var p = cmd.@params;

            // visibility
            if (!string.IsNullOrWhiteSpace(p.visibility))
            {
                if (p.visibility == "hidden") _cube.gameObject.SetActive(false);
                if (p.visibility == "visible") _cube.gameObject.SetActive(true);
            }

            // anchor
            if (!string.IsNullOrWhiteSpace(p.anchor_target))
            {
                switch (p.anchor_target.ToLowerInvariant())
                {
                    case "world":
                        _cube.SetParent(_worldAnchor != null ? _worldAnchor : null, true);
                        break;
                    case "hand":
                        if (_handAnchor != null) _cube.SetParent(_handAnchor, true);
                        break;
                    case "torso":
                        if (_torsoAnchor != null) _cube.SetParent(_torsoAnchor, true);
                        break;
                    case "head":
                        if (_headAnchor != null) _cube.SetParent(_headAnchor, true);
                        break;
                    case "none":
                        _cube.SetParent(null, true);
                        break;
                }
            }

            // movement
            if (p.move != null && !string.IsNullOrWhiteSpace(p.move.mode))
            {
                if (p.move.mode == "absolute" && p.move.world_position != null)
                {
                    _cube.position = new Vector3(p.move.world_position.x, p.move.world_position.y, p.move.world_position.z);
                }
                else if (p.move.mode == "relative" && p.move.delta_meters != null)
                {
                    Transform refTf = _directionReference != null ? _directionReference : (Camera.main != null ? Camera.main.transform : null);

                    Vector3 right = refTf != null ? refTf.right : Vector3.right;
                    Vector3 up = refTf != null ? refTf.up : Vector3.up;
                    Vector3 fwd = refTf != null ? refTf.forward : Vector3.forward;

                    var d = p.move.delta_meters;
                    Vector3 delta = right * d.x + up * d.y + fwd * d.z;

                    _cube.position += delta;
                }
            }
        }

        public string GetCapabilitiesSummary()
        {
            return _cube != null ? "move|hide|show|anchor" : "cube not assigned";
        }

        public string GetCapabilitiesForPrompt()
        {
            return _cube != null
                ? "Targets: cube. Visibility: visible/hidden/unchanged. Move: absolute(world_position) or relative(delta_meters). Anchor: world/hand/torso/head/none."
                : "No cube available.";
        }
    }

    // ============================================================
    // Logging helpers
    // ============================================================

    private void Log(string msg)
    {
        Debug.Log(LOGP + msg);
        if (debugText != null) debugText.text = msg;
    }

    private void LogMultiline(string title, string text)
    {
        if (text == null) text = "(null)";
        Debug.Log(LOGP + title + " (len=" + text.Length + ")");
        var lines = text.Split('\n');
        int maxLines = Mathf.Min(lines.Length, 140);
        for (int i = 0; i < maxLines; i++) Debug.Log(LOGP + lines[i]);
        if (lines.Length > maxLines) Debug.Log(LOGP + "...(truncated, total lines=" + lines.Length + ")");
    }

    private static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");
    }
}
