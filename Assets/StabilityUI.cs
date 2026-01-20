using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using Oklab;

public class StabilityUI : MonoBehaviour
{
    public Shapes.Rectangle GoodBarFlipped;
    public Shapes.Rectangle OutlineFlipped;

    public TextMeshProUGUI Status;
    public TextMeshProUGUI StatusFlipped;

    public SquidGameController SquidGameController;

    [ColorUsage(true, true)]
    public Color stableColor;
    [ColorUsage(true, true)]
    public Color unstableColor;

    public Shapes.Line DashFlipped;
    public float Spawntimer = 0;
    public float GameTimer = 120;

    public float score = 0;
    public float CorrectedScore = 0;
    public bool spawningEnabled = true;
    public int sum = 30;

    public OrbitalPresetCycler orbitalPresetCycler;

    // Update is called once per frame
    void Update()
    {
       
        int good = ActualParticlePoolSystem.CurrentGood;
        int bad = ActualParticlePoolSystem.CurrentBad;
        int unk = ActualParticlePoolSystem.CurrentActive - bad - good;

        score = Mathf.Clamp((5 + 1.25f * ((float)good - 0.25f * unk - 1.0f * bad)) / sum, 0.0f, 1.0f);
        float TargetGood = 600f * score;

        if (sum > 0)
        {
            GoodBarFlipped.Width = Mathf.Lerp(GoodBarFlipped.Width, TargetGood, 0.02f);
        }
        else
        {
            GoodBarFlipped.Width = 0;
        }

        if (SquidGameController.CurrentObjective == 1)
        {
            if (Mathf.Lerp(GoodBarFlipped.Width, TargetGood, 0.02f) < 0.70 * 600f && sum != 0)
            {
                DashFlipped.gameObject.SetActive(true);
                Status.text = "unstable";
                StatusFlipped.text = "unstable";
            }
            else
            {
                DashFlipped.gameObject.SetActive(true);
                Status.text = "stable";
                StatusFlipped.text = "stable";
            }
        }
        else
        {
            if (Mathf.Lerp(GoodBarFlipped.Width, TargetGood, 0.02f) < 0.99f * 600f && sum != 0)
            {
                DashFlipped.gameObject.SetActive(false);
                Status.text = "unstable";
                StatusFlipped.text = "unstable";
            }
            else
            {
                DashFlipped.gameObject.SetActive(false);
                Status.text = "stable";
                StatusFlipped.text = "stable";
            }
        }
 


        if (Mathf.Lerp(GoodBarFlipped.Width, TargetGood, 0.02f) < 0.70 * 600f && sum != 0)
        {

            if (TargetGood > 1.02f * GoodBarFlipped.Width)
            {
                if (SquidGameController.CurrentObjective == 1)
                    GoodBarFlipped.Color = Oklab.Oklab.OklabLerp(unstableColor, stableColor, (((GoodBarFlipped.Width + 40) / 0.7f) / 600));
                else
                    GoodBarFlipped.Color = Oklab.Oklab.OklabLerp(unstableColor, stableColor, (((GoodBarFlipped.Width + 40)) / 600));
            }
            else if (TargetGood < 0.98f * GoodBarFlipped.Width)
            {
                if (SquidGameController.CurrentObjective == 1)
                    GoodBarFlipped.Color = Oklab.Oklab.OklabLerp(unstableColor, stableColor, (((GoodBarFlipped.Width - 40) / 0.7f) / 600));
                else
                    GoodBarFlipped.Color = Oklab.Oklab.OklabLerp(unstableColor, stableColor, (((GoodBarFlipped.Width - 40)) / 600));
            }
            else
            {
                if (SquidGameController.CurrentObjective == 1)
                    GoodBarFlipped.Color = Oklab.Oklab.OklabLerp(unstableColor, stableColor, (((GoodBarFlipped.Width) / 0.7f) / 600));
                else
                    GoodBarFlipped.Color = Oklab.Oklab.OklabLerp(unstableColor, stableColor, (((GoodBarFlipped.Width)) / 600));
            }
        }
        else if (sum == 0)
        {
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
            Status.text = "stable";
            StatusFlipped.text = "stable";
            GoodBarFlipped.Color = stableColor;
            DashFlipped.gameObject.SetActive(true);
        }

        if (!spawningEnabled)
            return;

        if (ActualParticlePoolSystem.s_SuppressIdentifySpawns || ActualParticlePoolSystem.s_SuppressCoopSpawns)
            return;


        Spawntimer += Time.deltaTime;
        GameTimer += Time.deltaTime;
        GameTimer = Mathf.Clamp(GameTimer, 0.0f, 120f);
        if (score == 0 || GameTimer == 0)
            CorrectedScore = 0;
        else
        {
            CorrectedScore = score / (((GameTimer * 80) / 100 + 20) / 100);
            orbitalPresetCycler.ApplySpeed(Mathf.Pow(CorrectedScore, 1.5f) * 15);
        }




        if (CorrectedScore > 0.9 && Spawntimer > 5)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore > 0.85 && Spawntimer > 8)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore > 0.8 && Spawntimer > 11)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore > 0.75 && Spawntimer > 14)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore > 0.7 && Spawntimer > 17)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore > 0.65 && Spawntimer > 20)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore > 0.6 && Spawntimer > 23)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
        else if (CorrectedScore <= 0.6f && Spawntimer > 26)
        {
            ActualParticlePoolSystem.RequestImmediateUnknownSpawns(1);
            Spawntimer = 0;
        }
    }

    public void FadeStability(float targetAlpha, float duration)
    {
        StartCoroutine(FadeRect(GoodBarFlipped, targetAlpha, duration));
        StartCoroutine(FadeRect(OutlineFlipped, targetAlpha, duration));
        if(SquidGameController.CurrentObjective == 1)
        {
            StartCoroutine(FadeDash(DashFlipped, targetAlpha, duration));
        }
        else
        {
            StartCoroutine(FadeDash(DashFlipped, 0.0f, duration));
        }

        StartCoroutine(FadeText(StatusFlipped, targetAlpha, duration));
        StartCoroutine(FadeText(Status, targetAlpha, duration));

        if (targetAlpha == 0)
            GameTimer = 0;
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
