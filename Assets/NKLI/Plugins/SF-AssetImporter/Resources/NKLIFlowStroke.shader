Shader "Hidden/NKLIFlowStroke"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
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

        v2f vertStraight (appdata v)
        {
            v2f o;
            o.vertex = UnityObjectToClipPos(v.vertex);
            o.uv = v.uv;
            return o;
        }

        sampler2D _MainTex;
        sampler2D _FlowTex;
        float4 _TexSize;
        float _Wrap;
        float _FlowMip;
        float _StrokeLength;

        float2 WrapUV(float2 p)
        {
            return _Wrap > 0.5 ? frac(p) : saturate(p);
        }
        ENDCG

        // Pass 0: flow field — the tangent of the luma gradient, taken at a
        // blurred mip so the field is broad and coherent rather than jittery.
        // RG = tangent packed 0..1, B = gradient magnitude
        Pass
        {
            CGPROGRAM
            #pragma vertex vertStraight
            #pragma fragment frag
            #pragma target 3.0

            float Luma(float2 p)
            {
                return dot(tex2Dlod(_MainTex, float4(WrapUV(p), 0.0, _FlowMip)).rgb,
                    float3(0.299, 0.587, 0.114));
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 t = exp2(_FlowMip) / _TexSize.xy;

                float tl = Luma(i.uv + float2(-t.x, -t.y));
                float tc = Luma(i.uv + float2( 0.0, -t.y));
                float tr = Luma(i.uv + float2( t.x, -t.y));
                float ml = Luma(i.uv + float2(-t.x,  0.0));
                float mr = Luma(i.uv + float2( t.x,  0.0));
                float bl = Luma(i.uv + float2(-t.x,  t.y));
                float bc = Luma(i.uv + float2( 0.0,  t.y));
                float br = Luma(i.uv + float2( t.x,  t.y));

                float gx = (tr + 2.0 * mr + br) - (tl + 2.0 * ml + bl);
                float gy = (bl + 2.0 * bc + br) - (tl + 2.0 * tc + tr);

                float mag = length(float2(gx, gy));
                float2 tangent = mag > 1.0e-5 ? normalize(float2(-gy, gx)) : float2(1.0, 0.0);

                return float4(tangent * 0.5 + 0.5, saturate(mag * 4.0), 1.0);
            }
            ENDCG
        }

        // Pass 1: stroke — a line integral convolution of the paint along the
        // flow tangent, so smearing follows grain and contour like brushwork
        Pass
        {
            CGPROGRAM
            #pragma vertex vertStraight
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag (v2f i) : SV_Target
            {
                float2 dir = tex2Dlod(_FlowTex, float4(i.uv, 0.0, 0.0)).rg * 2.0 - 1.0;
                dir /= max(length(dir), 1.0e-5);

                float2 step = dir * (_StrokeLength / 8.0) / _TexSize.xy;

                float4 sum = 0.0;
                float wsum = 0.0;
                [unroll]
                for (int k = -8; k <= 8; k++)
                {
                    float w = exp(-(k * k) / 32.0);
                    float2 p = WrapUV(i.uv + step * k);
                    sum += tex2Dlod(_MainTex, float4(p, 0.0, 0.0)) * w;
                    wsum += w;
                }
                return sum / wsum;
            }
            ENDCG
        }
    }
}
