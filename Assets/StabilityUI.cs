using TMPro;
using Unity.VisualScripting;
using UnityEngine;

public class StabilityUI : MonoBehaviour
{
    public Shapes.Rectangle GoodBar;
    public Shapes.Rectangle GoodBarFlipped;
    public Shapes.Rectangle Outline;
    public Shapes.Rectangle OutlineFlipped;
    public TextMeshProUGUI Status;
    public TextMeshProUGUI StatusFlipped;

    public SquidGameController SquidGameController;

    [ColorUsage(true, true)]
    public Color stableColor;
    [ColorUsage(true, true)]
    public Color unstableColor;

    public Shapes.Line Dash;
    public Shapes.Line DashFlipped;
    private float Spawntimer = 0;

    public float score;
    public bool spawningEnabled = true;

    // Update is called once per frame
    void Update()
    {
       
        int good = ActualParticlePoolSystem.CurrentGood;
        int bad = ActualParticlePoolSystem.CurrentBad;
        int unk = ActualParticlePoolSystem.CurrentActive - bad - good;

        int sum = bad + good + unk;
        score = ((float)good + 0.5f * unk + 0.1f * bad) / sum;
        float TargetGood = 400f * score;

        if (sum > 0)
        {
            
            GoodBar.Width = Mathf.Lerp(GoodBar.Width, TargetGood, 0.02f);
            GoodBarFlipped.Width = Mathf.Lerp(GoodBarFlipped.Width, TargetGood, 0.02f);
        }
        else
        {
            GoodBar.Width = 0;
            GoodBarFlipped.Width = 0;
        }

        if (Mathf.Lerp(GoodBarFlipped.Width, TargetGood, 0.02f) < 0.75 * 400f && sum != 0)
        {
            Status.text = "status: unstable";
            StatusFlipped.text = "status: unstable";
            GoodBar.Color = unstableColor;
            GoodBarFlipped.Color = unstableColor;
            Dash.gameObject.SetActive(true);
            DashFlipped.gameObject.SetActive(true);
        }
        else if (sum == 0)
        {
            Dash.gameObject.SetActive(false);
            DashFlipped.gameObject.SetActive(false);

            if (Mathf.FloorToInt((Time.time * 2) % 4) == 0)
            {
                if (!SquidGameController._introP1Clicked || !SquidGameController._introP2Clicked)
                {
                    Status.text = "waiting input";
                    StatusFlipped.text = "waiting input";
                }
                else
                {
                    Status.text = "checking status";
                    StatusFlipped.text = "checking status";
                }
            }
            else if (Mathf.FloorToInt((Time.time * 2) % 4) == 1)
            {
                if (!SquidGameController._introP1Clicked || !SquidGameController._introP2Clicked)
                {
                    Status.text = "waiting input.";
                    StatusFlipped.text = "waiting input.";
                }
                else
                {
                    Status.text = "checking status.";
                    StatusFlipped.text = "checking status.";
                }
            }
            else if (Mathf.FloorToInt((Time.time * 2) % 4) == 2)
            {
                if (!SquidGameController._introP1Clicked || !SquidGameController._introP2Clicked)
                {
                    Status.text = "waiting input..";
                    StatusFlipped.text = "waiting input..";
                }
                else
                {
                    Status.text = "checking status..";
                    StatusFlipped.text = "checking status..";
                }
            }
            else
            {
                if (!SquidGameController._introP1Clicked || !SquidGameController._introP2Clicked)
                {
                    Status.text = "waiting input...";
                    StatusFlipped.text = "waiting input...";
                }
                else
                {
                    Status.text = "checking status...";
                    StatusFlipped.text = "checking status...";
                }
            }
        }
        else
        {
            Status.text = "status: stable";
            StatusFlipped.text = "status: stable";
            GoodBar.Color = stableColor;
            GoodBarFlipped.Color = stableColor;
            Dash.gameObject.SetActive(true);
            DashFlipped.gameObject.SetActive(true);

            if (!spawningEnabled)
                return;

            if (ActualParticlePoolSystem.s_SuppressIdentifySpawns || ActualParticlePoolSystem.s_SuppressCoopSpawns)
                return;


            Spawntimer += Time.deltaTime;

            if (score > 0.9 && Spawntimer > 5)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score > 0.85 && Spawntimer > 8)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score > 0.8 && Spawntimer > 11)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score > 0.75 && Spawntimer > 14)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score > 0.7 && Spawntimer > 17)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score > 0.65 && Spawntimer > 20)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score > 0.6 && Spawntimer > 23)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
            else if (score <= 0.6f && Spawntimer > 26)
            {
                ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
                Spawntimer = 0;
            }
        }
    }

    public void FadeStability(float targetAlpha, float duration)
    {
        StartCoroutine(FadeRect(GoodBar, targetAlpha, duration));
        StartCoroutine(FadeRect(Outline, targetAlpha, duration));
        StartCoroutine(FadeRect(GoodBarFlipped, targetAlpha, duration));
        StartCoroutine(FadeRect(OutlineFlipped, targetAlpha, duration));
        StartCoroutine(FadeDash(Dash, targetAlpha, duration));
        StartCoroutine(FadeDash(DashFlipped, targetAlpha, duration));
        StartCoroutine(FadeText(Status, targetAlpha, duration));
        StartCoroutine(FadeText(StatusFlipped, targetAlpha, duration));
    }

    private System.Collections.IEnumerator FadeRect(Shapes.Rectangle rect, float targetAlpha, float duration)
    {
        if (rect == null)
            yield break;

        rect.gameObject.SetActive(true);
        Color startColor= rect.Color;
        float t = 0f;

        if (duration <= 0f)
        {
            rect.Color = new Color (rect.Color.r, rect.Color.g, rect.Color.b, targetAlpha);
        }
        else
        {
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                rect.Color = Vector4.Lerp(startColor, new Color(rect.Color.r, rect.Color.g, rect.Color.b, targetAlpha), u);
                yield return null;
            }

            rect.Color = new Color(rect.Color.r, rect.Color.g, rect.Color.b, targetAlpha);
        }

        if (Mathf.Approximately(targetAlpha, 0f))
            rect.gameObject.SetActive(false);
    }

    private System.Collections.IEnumerator FadeDash(Shapes.Line rect, float targetAlpha, float duration)
    {
        if (rect == null)
            yield break;

        rect.gameObject.SetActive(true);
        Color startColor = rect.Color;
        float t = 0f;

        if (duration <= 0f)
        {
            rect.Color = new Color(rect.Color.r, rect.Color.g, rect.Color.b, targetAlpha);
        }
        else
        {
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                rect.Color = Vector4.Lerp(startColor, new Color(rect.Color.r, rect.Color.g, rect.Color.b, targetAlpha), u);
                yield return null;
            }

            rect.Color = new Color(rect.Color.r, rect.Color.g, rect.Color.b, targetAlpha);
        }

        if (Mathf.Approximately(targetAlpha, 0f))
            rect.gameObject.SetActive(false);
    }

    private System.Collections.IEnumerator FadeText(TextMeshProUGUI rect, float targetAlpha, float duration)
    {
        if (rect == null)
            yield break;

        rect.gameObject.SetActive(true);
        Color startColor = rect.faceColor;
        float t = 0f;

        if (duration <= 0f)
        {
            rect.faceColor = new Color(rect.faceColor.r, rect.faceColor.g, rect.faceColor.b, targetAlpha);
        }
        else
        {
            while (t < duration)
            {
                t += Time.deltaTime;
                float u = Mathf.Clamp01(t / duration);
                rect.faceColor = Color.Lerp(rect.faceColor, new Color(rect.faceColor.r, rect.faceColor.g, rect.faceColor.b, targetAlpha), u);
                yield return null;
            }

            rect.faceColor = new Color(rect.faceColor.r, rect.faceColor.g, rect.faceColor.b, targetAlpha);
        }

        if (Mathf.Approximately(targetAlpha, 0f))
            rect.gameObject.SetActive(false);
    }
}
