Shader "Hidden/NKLIGammaCorrect"
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

            // No flip here: the mux composite's flip and the top-down readback
            // pair to even parity. A third mirror y-flipped every baked texture
            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            uniform sampler2D _MainTex;

            fixed4 frag(v2f i) : SV_Target
            {
                float4 col = tex2D(_MainTex, i.uv);
                float alpha = col.a;

                // Exact inverse of the piecewise sRGB decode the hardware
                // applied when the source was sampled; a pow(1/2.2)
                // approximation would lift the darks the sRGB toe keeps linear.
                // Saturate first: pow of a negative is NaN
                float3 c = saturate(col.rgb);
                float3 lo = c * 12.92;
                float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
                col.rgb = lerp(hi, lo, step(c, 0.0031308));

                return float4(col.rgb, alpha);
            }

        ENDCG
        }
    }
}
