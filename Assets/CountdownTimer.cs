using UnityEngine;
using TMPro;
using System.Collections;

public class CountdownTimer : MonoBehaviour
{
    [Header("UI Reference")]
    public TextMeshProUGUI timerText;

    Coroutine timerRoutine;
    public float remaining = 0;

    private void Start()
    {
        timerText.text = "";
    }

    /// <summary>
    /// Starts a countdown timer.
    /// durationSeconds = total time to count down from (in seconds)
    /// </summary>
    public void StartCountdown(float durationSeconds)
    {
        // Prevent multiple timers running
        if (timerRoutine != null)
            StopCoroutine(timerRoutine);

        timerRoutine = StartCoroutine(CountdownRoutine(durationSeconds));
    }

    private IEnumerator CountdownRoutine(float duration)
    {
        remaining = duration;

        while (remaining > 0f)
        {
            remaining -= Time.deltaTime;
            if (remaining < 0f) remaining = 0f;

            // Convert to minutes, seconds, and milliseconds
            int minutes = Mathf.FloorToInt(remaining / 60f);
            int seconds = Mathf.FloorToInt(remaining % 60f);

            if(remaining < 10)
            {
                if (Mathf.FloorToInt((remaining * 4) % 2) == 0)
                    timerText.text = "";
                else
                    timerText.text = $"{minutes:00}:{seconds:00}";
            }
            else
            {
                // Format: MM:SS:msms
                timerText.text = $"{minutes:00}:{seconds:00}";
            }


            yield return null;
        }

        // Optional: timer reached zero
        // timerText.text = "00:00:000";
        timerRoutine = null;
    }
}
