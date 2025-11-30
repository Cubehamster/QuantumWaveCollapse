using System.Collections;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(CanvasGroup))]
public class IntroTextFader : MonoBehaviour
{
    [SerializeField] private TMP_Text label;
    [SerializeField] private float fadeDuration = 0.4f;

    CanvasGroup _group;
    Coroutine _fadeRoutine;

    void Awake()
    {
        _group = GetComponent<CanvasGroup>();
        if (label == null)
            label = GetComponentInChildren<TMP_Text>();

        // start hidden
        _group.alpha = 0f;
        gameObject.SetActive(false);
    }

    public void SetText(string text)
    {
        if (label != null)
            label.text = text;
    }

    public void FadeIn()
    {
        gameObject.SetActive(true);
        StartFade(1f);
    }

    public void FadeOut()
    {
        StartFade(0f);
    }

    void StartFade(float target)
    {
        if (_fadeRoutine != null)
            StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeRoutine(target));
    }

    IEnumerator FadeRoutine(float target)
    {
        float start = _group.alpha;
        float t = 0f;

        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / fadeDuration);
            _group.alpha = Mathf.Lerp(start, target, k);
            yield return null;
        }

        _group.alpha = target;

        // Auto-disable when fully transparent
        if (Mathf.Approximately(target, 0f))
            gameObject.SetActive(false);

        _fadeRoutine = null;
    }
}
