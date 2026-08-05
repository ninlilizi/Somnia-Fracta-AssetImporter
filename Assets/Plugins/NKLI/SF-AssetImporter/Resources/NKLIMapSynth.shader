Shader "Hidden/NKLIMapSynth"
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
        sampler2D _NormalTex;
        float4 _NormalSize;
        float _HasNormal;

        float _SpecFloor;
        float _SpecCeil;
        float _SpecCurve;
        float _SpecTint;
        float _SmoothBase;
        float _SmoothLuma;
        float _SmoothDetail;
        float _SmoothFlat;
        float _MetalLumaLo;
        float _MetalLumaHi;
        float _MetalSatReject;
        float _MetalAmount;
        float _CavityGain;
        float _CavitySpec;
        float _CavitySmooth;
        float _OccCavity;
        float _OccShade;
        float _OccFloor;

        static const float3 lumaWeights = float3(0.299, 0.587, 0.114);

        // r*a decodes both AG-swizzled platform normals and plain RGB files
        float3 NormalAt(float2 uv, float lod)
        {
            float4 p = tex2Dlod(_NormalTex, float4(uv, 0.0, lod));
            float3 n;
            n.xy = float2(p.r * p.a, p.g) * 2.0 - 1.0;
            n.z = sqrt(saturate(1.0 - dot(n.xy, n.xy)));
            return n;
        }

        // Multi-scale crevice measure: where the slope field converges
        // (negative divergence) the surface folds inward and light struggles
        // to reach. Each octave reads a coarser mip over a wider step
        float Cavity(float2 uv)
        {
            if (_HasNormal < 0.5)
                return 0.0;

            float sum = 0.0;
            float amp = 1.0;
            float total = 0.0;
            [unroll]
            for (int m = 0; m < 4; m++)
            {
                float2 t = exp2((float)m) / _NormalSize.xy;
                float dnx = NormalAt(uv + float2(t.x, 0.0), (float)m).x - NormalAt(uv - float2(t.x, 0.0), (float)m).x;
                float dny = NormalAt(uv + float2(0.0, t.y), (float)m).y - NormalAt(uv - float2(0.0, t.y), (float)m).y;
                sum += amp * max(0.0, -(dnx + dny) * 0.5);
                total += amp;
                amp *= 0.6;
            }
            return saturate(sum / total * _CavityGain);
        }

        float LumaAt(float2 uv, float lod)
        {
            return dot(tex2Dlod(_MainTex, float4(uv, 0.0, lod)).rgb, lumaWeights);
        }

        // Micro-contrast between fine and coarse mips: busy surfaces read rough
        float Detail(float2 uv, float l0)
        {
            float l2 = LumaAt(uv, 2.0);
            float l4 = LumaAt(uv, 4.0);
            return abs(l0 - l2) + abs(l2 - l4) * 0.5;
        }

        float Smoothness(float2 uv, float l0, float cav)
        {
            float flatness = _HasNormal > 0.5 ? NormalAt(uv, 0.0).z : 1.0;
            float sm = _SmoothBase + l0 * _SmoothLuma - Detail(uv, l0) * _SmoothDetail
                + smoothstep(0.8, 1.0, flatness) * _SmoothFlat;
            return saturate(sm) * (1.0 - cav * _CavitySmooth);
        }
        ENDCG

        // Pass 0: specular colour (RGB) + smoothness (A). Intensity follows a
        // luminance curve, carries a whisper of the albedo's own chroma and
        // recedes into crevices
        Pass
        {
            CGPROGRAM
            #pragma vertex vertStraight
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag (v2f i) : SV_Target
            {
                float3 alb = tex2Dlod(_MainTex, float4(i.uv, 0.0, 0.0)).rgb;
                float l0 = dot(alb, lumaWeights);
                float cav = Cavity(i.uv);

                float spec = lerp(_SpecFloor, _SpecCeil, pow(saturate(l0), _SpecCurve));
                spec *= 1.0 - cav * _CavitySpec;
                float3 tint = lerp(float3(1.0, 1.0, 1.0), alb / max(l0, 1.0e-4), _SpecTint);
                return float4(saturate(spec * tint), Smoothness(i.uv, l0, cav));
            }
            ENDCG
        }

        // Pass 1: metallic (RGB) + smoothness (A). Bright, chroma-poor albedo
        // reads as metal; saturated or shadowed content stays dielectric
        Pass
        {
            CGPROGRAM
            #pragma vertex vertStraight
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag (v2f i) : SV_Target
            {
                float3 alb = tex2Dlod(_MainTex, float4(i.uv, 0.0, 0.0)).rgb;
                float l0 = dot(alb, lumaWeights);
                float mx = max(alb.r, max(alb.g, alb.b));
                float sat = (mx - min(alb.r, min(alb.g, alb.b))) / max(mx, 1.0e-4);
                float cav = Cavity(i.uv);

                float metal = smoothstep(_MetalLumaLo, _MetalLumaHi, l0)
                    * pow(1.0 - saturate(sat * _MetalSatReject), 2.0) * _MetalAmount;
                metal *= 1.0 - cav * 0.5;
                return float4(metal.xxx, Smoothness(i.uv, l0, cav));
            }
            ENDCG
        }

        // Pass 2: occlusion. Crevices found in the normal field darken, joined
        // by a gentle pooled-shadow term where the albedo sits darker than its
        // own neighbourhood. Straight UVs like its siblings: the bake rides
        // the stylization chain, whose composite supplies the lone row flip
        Pass
        {
            CGPROGRAM
            #pragma vertex vertStraight
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag (v2f i) : SV_Target
            {
                float l0 = LumaAt(i.uv, 0.0);
                float lWide = LumaAt(i.uv, 4.0);
                float dark = saturate((lWide - l0) * _OccShade);

                float occ = 1.0 - saturate(Cavity(i.uv) * _OccCavity + dark);
                occ = max(occ, _OccFloor);
                return float4(occ.xxx, 1.0);
            }
            ENDCG
        }
    }
}
