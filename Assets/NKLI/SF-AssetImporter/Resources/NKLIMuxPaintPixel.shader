Shader "Hidden/NKLIMuxPaintPixel"
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

        #define TAU 6.2831853

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

        v2f vertFlip (appdata v)
        {
            v2f o;
            o.vertex = UnityObjectToClipPos(v.vertex);
            o.uv.x = v.uv.x;
            o.uv.y = 1 - v.uv.y;
            return o;
        }

        sampler2D _MainTex;
        sampler2D _PaintTex;
        sampler2D _PaintStrongTex;
        sampler2D _FacetTex;
        sampler2D _MaskTex;

        float4 _JuliaC;      // xy = Julia constant, zw = domain phase
        float4 _TexSize;
        float _JuliaZoom;
        float _JuliaRot;
        float _JuliaWarp;
        float _Filigree;
        float _Pool;
        float _MaskNoise;
        float _MaskLo;
        float _MaskHi;
        float _MaskBlur;
        float _MaskSoften;
        float _CrystalMax;
        float _GuardLo;
        float _GuardHi;
        float _Wrap;

        float hash21(float2 p)
        {
            p = frac(p * float2(234.34, 435.345));
            p += dot(p, p + 34.23);
            return frac(p.x * p.y);
        }

        float vnoise(float2 p, float period)
        {
            float2 cell = floor(p);
            float2 f = p - cell;
            f = f * f * (3.0 - 2.0 * f);

            float a = hash21(fmod(cell, period));
            float b = hash21(fmod(cell + float2(1, 0), period));
            float c = hash21(fmod(cell + float2(0, 1), period));
            float d = hash21(fmod(cell + float2(1, 1), period));
            return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
        }

        float fbm(float2 uv, float freq, float wrap)
        {
            float v = 0.0;
            float amp = 0.5;
            [unroll]
            for (int k = 0; k < 4; k++)
            {
                v += amp * vnoise(uv * freq, wrap > 0.5 ? freq : 1e6);
                freq *= 2.0;
                amp *= 0.5;
            }
            return v;
        }

        // Orbit-trapped Julia field: filigree from a line trap, pools from a point trap
        float JuliaMask(float2 uv)
        {
            float2 z;
            if (_Wrap > 0.5)
            {
                // Periodic seed coordinates so the fractal tiles with the texture
                z = float2(sin(TAU * (uv.x + _JuliaC.z)),
                           sin(TAU * (uv.y + _JuliaC.w))) * (_JuliaZoom * 0.5);
            }
            else
            {
                z = (uv - 0.5) * _JuliaZoom;
                z.x *= _TexSize.x / _TexSize.y;
                float sr = sin(_JuliaRot);
                float cr = cos(_JuliaRot);
                z = float2(z.x * cr - z.y * sr, z.x * sr + z.y * cr);
                z += (_JuliaC.zw - 0.5) * 0.7;
            }

            // Noise warp breaks the mirror symmetry the periodic domain folds in
            z += (float2(fbm(uv, 5.0, _Wrap),
                         fbm(uv + 7.77, 5.0, _Wrap)) - 0.47) * _JuliaWarp;

            float2 c = _JuliaC.xy;
            float trapP = 1e9;
            float trapL = 1e9;

            [loop]
            for (int k = 0; k < 80; k++)
            {
                z = float2(z.x * z.x - z.y * z.y, 2.0 * z.x * z.y) + c;
                trapP = min(trapP, length(z));
                trapL = min(trapL, abs(z.y));
                if (dot(z, z) > 16.0) break;
            }

            float filigree = exp(-trapL * 3.5);
            float pool = exp(-trapP * 1.2);
            return saturate(filigree * _Filigree + pool * _Pool);
        }
        ENDCG

        // Pass 0: render the crystallization mask
        Pass
        {
            CGPROGRAM
            #pragma vertex vertStraight
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag (v2f i) : SV_Target
            {
                float m = JuliaMask(i.uv);
                m += (fbm(i.uv, 4.0, _Wrap) - 0.47) * _MaskNoise;
                return float4(m, m, m, 1.0);
            }
            ENDCG
        }

        // Pass 1: composite paint, deep paint and facets through the blurred mask
        Pass
        {
            CGPROGRAM
            #pragma vertex vertFlip
            #pragma fragment frag
            #pragma target 3.0

            fixed4 frag (v2f i) : SV_Target
            {
                // The mip-blurred mask spreads each transition across a wide
                // border; a measure of the raw field is folded back in so the
                // boundary keeps fine grain
                float mBlur = tex2Dlod(_MaskTex, float4(i.uv, 0.0, _MaskBlur)).r;
                float mRaw = tex2Dlod(_MaskTex, float4(i.uv, 0.0, 0.0)).r;
                float m = lerp(mRaw, mBlur, _MaskSoften);

                float crystal = smoothstep(_MaskLo, _MaskHi, m) * _CrystalMax;

                float4 paintBase = tex2D(_PaintTex, i.uv);
                float4 facet = tex2D(_FacetTex, i.uv);

                // Content guard: a facet whose fill strays far from the local
                // paint has sampled unrelated content — a neighbouring atlas
                // island or the gutter between them — so crystallization is
                // suppressed there and the paint holds the surface
                float stray = dot(abs(facet.rgb - paintBase.rgb), float3(0.299, 0.587, 0.114));
                crystal *= 1.0 - smoothstep(_GuardLo, _GuardHi, stray);

                // Painterly strength swells where the crystal recedes
                float4 paint = lerp(tex2D(_PaintStrongTex, i.uv), paintBase, crystal);

                float4 col = lerp(paint, facet, crystal);
                col.a = tex2D(_MainTex, i.uv).a;
                return col;
            }
            ENDCG
        }
    }
}
