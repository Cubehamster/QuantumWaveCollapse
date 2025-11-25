using Unity.VisualScripting;
using UnityEngine;

public class StabilityUI : MonoBehaviour
{
    public Shapes.Rectangle GoodBar;
    public Shapes.Rectangle BadBar;



    // Update is called once per frame
    void Update()
    {
        int unk = ActualParticlePoolSystem.CurrentUnknown;
        int good = ActualParticlePoolSystem.CurrentGood;
        int bad = ActualParticlePoolSystem.CurrentBad;

        int sum = bad + good + unk;

        if(sum > 0)
        {
            float TargetGood = 9.7f * ((float)good / 50);
            float TargetBad = 9.7f * (((float)sum - (float)unk) / 50);
            GoodBar.Width = Mathf.Lerp(GoodBar.Width, TargetGood, 0.02f);
            BadBar.Width = Mathf.Lerp(BadBar.Width, TargetBad, 0.02f);
        }
        else
        {
            GoodBar.Width = 0;
            BadBar.Width = 0;
        }

    }
}
