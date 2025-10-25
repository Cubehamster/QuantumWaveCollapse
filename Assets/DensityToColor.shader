Shader "Hidden/DensityToColor"
{
    Properties
    {
        _Density ("Density", 2D) = "black" {}
        _LUT     ("LUT", 2D)     = "white" {}
        _Bg      ("Background", Color) = (0.02,0.03,0.08,1)
        _K       ("ExposureK", Float) = 0.01
        _Gamma   ("Gamma", Float) = 0.9
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Density;
            sampler2D _LUT;
            float4 _Bg;
            float  _K;
            float  _Gamma;

            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f     { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float d = tex2D(_Density, i.uv).r;
                // Tone-map density → [0..1]
                float t = 1.0 - exp(-_K * 0.5 * max(0, d)); // lower contrast
                t = pow(saturate(t), 1.0 / max(1e-3, _Gamma + 0.2)); // boost low intensities
                // Gradient lookup (assumes 1D LUT in X)
                float4 c = tex2D(_LUT, float2(t, 0.5));
                // Over background
                return lerp(_Bg, c, saturate(t));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
