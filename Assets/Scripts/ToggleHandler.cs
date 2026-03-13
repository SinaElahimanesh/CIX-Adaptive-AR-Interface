using Meta.WitAi.Json;
using Oculus.Voice;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VoiceToggle : MonoBehaviour
{
    [Header("Button (optional – disabled during Processing...)")]
    [SerializeField] private Button _button;

    [Header("Button Label (optional)")]
    [SerializeField] private TextMeshProUGUI _buttonLabel;

    [Header("State Text")]
    [SerializeField] private string _startText = "Start";
    [SerializeField] private string _stopText = "Stop";
    [SerializeField] private string _processingText = "Processing...";

    private AppVoiceExperience _voice;

    void Start()
    {
        if (_button == null) _button = GetComponent<Button>();
        _voice = GetComponent<AppVoiceExperience>();
        if (_voice != null)
        {
            _voice.VoiceEvents.OnStartListening.AddListener(OnStartListening);
            _voice.VoiceEvents.OnStoppedListening.AddListener(OnStoppedListening);
            _voice.VoiceEvents.OnRequestCompleted.AddListener(OnIdle);
            _voice.VoiceEvents.OnResponse.AddListener(OnResponseReceived);
            _voice.VoiceEvents.OnError.AddListener(OnErrorReceived);
        }
        SetButtonText(_startText);
        SetButtonInteractable(true);
    }

    void OnDestroy()
    {
        if (_voice != null)
        {
            _voice.VoiceEvents.OnStartListening.RemoveListener(OnStartListening);
            _voice.VoiceEvents.OnStoppedListening.RemoveListener(OnStoppedListening);
            _voice.VoiceEvents.OnRequestCompleted.RemoveListener(OnIdle);
            _voice.VoiceEvents.OnResponse.RemoveListener(OnResponseReceived);
            _voice.VoiceEvents.OnError.RemoveListener(OnErrorReceived);
        }
    }

    private void OnStartListening()
    {
        SetButtonText(_stopText);
        SetButtonInteractable(true);
    }

    private void OnStoppedListening()
    {
        SetButtonText(_processingText);
        SetButtonInteractable(false);
    }

    private void OnIdle()
    {
        SetButtonText(_startText);
        SetButtonInteractable(true);
    }

    private void OnResponseReceived(WitResponseNode response)
    {
        SetButtonText(_startText);
        SetButtonInteractable(true);
    }

    private void OnErrorReceived(string message, string stack)
    {
        SetButtonText(_startText);
        SetButtonInteractable(true);
    }

    private void SetButtonText(string text)
    {
        if (_buttonLabel != null)
            _buttonLabel.text = text;
    }

    private void SetButtonInteractable(bool interactable)
    {
        if (_button != null)
            _button.interactable = interactable;
    }

    public void Toggle()
    {
        if (_voice == null) return;
        if (_voice.Active) _voice.Deactivate();
        else _voice.Activate();
    }
}