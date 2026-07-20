Shader "Hidden/NKLIGloamingGrade"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex;
            float _GlowAmount;
            float _GlowMip;
            float _Lift;
            float4 _LiftColor;
            float4 _ShadowTint;
            float4 _HighlightTint;
            float _Desaturate;

            fixed4 frag (v2f i) : SV_Target
            {
                float4 base = tex2D(_MainTex, i.uv);

                // Halation: screen-blend a high mip of the same texture over itself
                float3 glow = tex2Dlod(_MainTex, float4(i.uv, 0.0, _GlowMip)).rgb;
                float3 col = 1.0 - (1.0 - base.rgb) * (1.0 - glow * _GlowAmount);

                // Lift the black point so nothing ever fully resolves
                col = col * (1.0 - _Lift) + _LiftColor.rgb * _Lift;

                float luma = dot(col, float3(0.299, 0.587, 0.114));
                col = lerp(col, luma.xxx, _Desaturate);

                // Split-tone: dusk in the shadows, last light in the highlights
                float t = smoothstep(0.25, 0.8, luma);
                col *= lerp(_ShadowTint.rgb, _HighlightTint.rgb, t);

                return float4(col, base.a);
            }
            ENDCG
        }
    }
}
