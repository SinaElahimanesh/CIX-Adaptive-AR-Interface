using Oculus.Interaction;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bridges Meta ISDK Ray Canvas pinch to your Voice Button.
/// When the user pinches on the button, this fires the toggle via PointableCanvasModule.
/// Add to the same GameObject as your Button.
/// Requires PointableCanvasModule in the scene (comes with Meta Interaction SDK).
/// </summary>
[RequireComponent(typeof(Button))]
public class VoiceButtonPointableBridge : MonoBehaviour
{
    [Tooltip("Optional: if null, uses VoiceToggle from scene")]
    [SerializeField] private VoiceToggle _voiceToggle;

    [Tooltip("If true, log when pinch is detected (helps debug)")]
    [SerializeField] private bool _debugLog;

    private Button _button;
    private VoiceToggle _resolvedToggle;

    void Start()
    {
        _button = GetComponent<Button>();
        _resolvedToggle = _voiceToggle != null ? _voiceToggle : FindFirstObjectByType<VoiceToggle>();
        if (_resolvedToggle == null)
        {
            Debug.LogWarning("[VoiceButtonPointableBridge] No VoiceToggle found in scene.");
            return;
        }

        // Fire on release (Unselect) – matches standard "click on release" behavior
        PointableCanvasModule.WhenUnselected += OnPointableUnselected;
    }

    void OnDestroy()
    {
        PointableCanvasModule.WhenUnselected -= OnPointableUnselected;
    }

    private void OnPointableUnselected(PointableCanvasEventArgs args)
    {
        if (args.Hovered == null) return;

        // args.Hovered is the Selectable (Button) that received the click
        if (args.Hovered != gameObject && !args.Hovered.transform.IsChildOf(transform))
            return;

        if (_button != null && !_button.interactable)
            return;

        if (_debugLog) Debug.Log("[VoiceButtonPointableBridge] Pinch detected on button, toggling voice.");

        if (_resolvedToggle != null)
            _resolvedToggle.Toggle();
    }
}
