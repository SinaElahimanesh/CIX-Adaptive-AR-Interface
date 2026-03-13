using TMPro;
using UnityEngine;

public class TimerDisplay : MonoBehaviour
{
    [Header("Assign this in Inspector")]
    public TimerController timer;

    private TextMeshProUGUI _uiTextPro;

    private void Awake()
    {
        // Grab the UI text component from THIS object.
        _uiTextPro = GetComponent<TextMeshProUGUI>();

        if (_uiTextPro == null)
            Debug.LogError("[TimerDisplay] Missing TextMeshProUGUI on this GameObject. Add a TMP Text component or move this script onto the TMP Text object.");
    }

    private void Start()
    {
        if (timer == null)
            Debug.LogError("[TimerDisplay] 'timer' reference is not assigned. Drag your TimerController into the TimerDisplay.timer field in the Inspector.");
    }

    private void Update()
    {
        // If references are missing, do nothing (prevents spam/crashes).
        if (_uiTextPro == null || timer == null) return;

        _uiTextPro.text = timer.GetFormattedTimeFromSeconds();
    }
}
