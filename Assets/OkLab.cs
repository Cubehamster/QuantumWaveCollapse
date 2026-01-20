using UnityEngine;

/*
 *  Oklab Unity implementation by Ferran Bertomeu Castells @fonserbc
 *  
 *  Oklab by Björn Ottosson https://bottosson.github.io/posts/oklab/
 *  under Public Domain
 * 
 *  For the conversions, I understand unity's Color as a gamma-space color
 */

namespace Oklab
{
    public static class Oklab
    {
        public struct Lab { public float L; public float a; public float b; };

        public static Lab ToOklab(this Color _color)
        {
            Color c = _color.linear;

            float l = 0.4122214708f * c.r + 0.5363325363f * c.g + 0.0514459929f * c.b;
            float m = 0.2119034982f * c.r + 0.6806995451f * c.g + 0.1073969566f * c.b;
            float s = 0.0883024619f * c.r + 0.2817188376f * c.g + 0.6299787005f * c.b;

            float l_ = cbrtf(l);
            float m_ = cbrtf(m);
            float s_ = cbrtf(s);

            return new Lab
            {
                L = 0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
                a = 1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
                b = 0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_
            };
        }

        public static Color toColor(this Lab _c)
        {
            float l_ = _c.L + 0.3963377774f * _c.a + 0.2158037573f * _c.b;
            float m_ = _c.L - 0.1055613458f * _c.a - 0.0638541728f * _c.b;
            float s_ = _c.L - 0.0894841775f * _c.a - 1.2914855480f * _c.b;

            float l = l_ * l_ * l_;
            float m = m_ * m_ * m_;
            float s = s_ * s_ * s_;

            return new Color(
                +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
        -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
        -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s
            ).gamma;
        }

        public static Lab Lerp(Lab _a, Lab _b, float _f)
        {
            return new Lab
            {
                L = Mathf.Lerp(_a.L, _b.L, _f),
                a = Mathf.Lerp(_a.a, _b.a, _f),
                b = Mathf.Lerp(_a.b, _b.b, _f)
            };
        }

        public static Color OklabLerp(Color _a, Color _b, float _f)
        {
            return Lerp(_a.ToOklab(), _b.ToOklab(), _f).toColor();
        }

        public static Color EvaluateOklab(this Gradient _g, float _time)
        {
            int it = 0;
            while (it < _g.colorKeys.Length - 1 && _time >= _g.colorKeys[it + 1].time)
                it++;

            Color c;
            if (it >= _g.colorKeys.Length - 1)
                c = _g.colorKeys[_g.colorKeys.Length - 1].color;
            else
            {
                float lerpAmount = (_time - _g.colorKeys[it].time) / (_g.colorKeys[it + 1].time - _g.colorKeys[it].time);
                c = OklabLerp(_g.colorKeys[it].color, _g.colorKeys[it + 1].color, lerpAmount);
            }

            // alpha
            it = 0;
            while (it < _g.alphaKeys.Length - 1 && _time >= _g.alphaKeys[it + 1].time)
                it++;

            if (it >= _g.alphaKeys.Length - 1)
                c.a = _g.alphaKeys[_g.alphaKeys.Length - 1].alpha;
            else
            {
                float lerpAmount = (_time - _g.alphaKeys[it].time) / (_g.alphaKeys[it + 1].time - _g.alphaKeys[it].time);
                c.a = Mathf.Lerp(_g.alphaKeys[it].alpha, _g.alphaKeys[it + 1].alpha, lerpAmount);
            }

            return c;
        }

        // If using .NET core, please use System.Math.Cbrt for cuberoot
        static float cbrtf(float _v)
        {
            return Mathf.Sign(_v) * Mathf.Pow(Mathf.Abs(_v), 1f / 3f);
        }
    }
}