using Meta.WitAi;
using Meta.WitAi.Json;
using TMPro;
using UnityEngine;

public class VoiceResponseDisplay : MonoBehaviour
{
    [SerializeField] private TMP_InputField _resultInputField;

    public void OnResponse(WitResponseNode response)
    {
        if (_resultInputField == null) return;
        
        string transcription = response.GetTranscription();
        _resultInputField.text = string.IsNullOrEmpty(transcription) 
            ? "(No text recognized)" 
            : transcription;
    }
}