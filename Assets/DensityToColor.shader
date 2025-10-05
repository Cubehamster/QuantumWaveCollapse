// DensityToColor.shader
Shader "Hidden/DensityToColor"
{
    Properties{
        _Density ("Density (RFloat)", 2D) = "black" {}
        _LUT     ("Gradient 1D", 2D) = "white" {}
        _K       ("Exposure K", Float) = 0.01
        _Gamma   ("Gamma", Float) = 0.9
        _Bg      ("Background", Color) = (0.02,0.03,0.08,1)
    }
    SubShader{
        Tags{ "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off Blend SrcAlpha OneMinusSrcAlpha Cull Off

        Pass{
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Density;
            sampler2D _LUT;
            float4 _Bg;
            float _K;
            float _Gamma;

            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };

            v2f vert(appdata v){ v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }

            fixed4 frag(v2f i):SV_Target
            {
                float d = tex2D(_Density, i.uv).r;

                // simple tone map: 1 - exp(-k*d)
                float t = 1.0 - exp(-_K * max(0.0, d));
                // gamma
                t = pow(saturate(t), max(0.001, 1.0/_Gamma));

                fixed4 c = tex2D(_LUT, float2(t, 0.5));
                // blend from background near zero
                float edge = smoothstep(0.0, 0.05, t);
                return lerp(_Bg, c, edge);
            }
            ENDHLSL
        }
    }
}
