Shader "Hidden/NKLITriangleFacet"
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
            float4 _TexSize;
            float _Density;
            float _Jitter;
            float _HueJitter;
            float _SatJitter;
            float _FractalChance;
            float _FractalShade;
            float _NormalPerturb;
            float _LatticeWarp;
            float _Wrap;

            float hash21(float2 p)
            {
                p = frac(p * float2(234.34, 435.345));
                p += dot(p, p + 34.23);
                return frac(p.x * p.y);
            }

            float3 rgb2hsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 hsv2rgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
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

            fixed4 frag (v2f i) : SV_Target
            {
                // Snap to an even row count so the lattice wraps at the texture border
                float rowsIdeal = (_TexSize.y * _Density) / (_TexSize.x * 0.8660254);
                float rows = max(2.0, floor(rowsIdeal * 0.5 + 0.5) * 2.0);

                float2 p = float2(i.uv.x * _Density, i.uv.y * rows * 0.8660254);

                // Low-frequency warp bends the lattice so triangle shapes drift
                // organically instead of repeating with mechanical regularity
                p += (float2(fbm(i.uv, 5.0, _Wrap),
                             fbm(i.uv + 7.77, 5.0, _Wrap)) - 0.47) * _LatticeWarp;

                // Skew so each unit cell holds two equilateral triangles
                float2x2 toSkewed = float2x2(1.0, -0.5773503, 0.0, 1.1547005);
                float2x2 fromSkewed = float2x2(1.0, 0.5, 0.0, 0.8660254);

                float2 s = mul(toSkewed, p);
                float2 cell = floor(s);
                float2 f = s - cell;
                float upper = step(1.0, f.x + f.y);

                float2 facetS = cell + lerp(float2(1.0, 1.0) / 3.0, float2(2.0, 2.0) / 3.0, upper);
                float2 facetP = mul(fromSkewed, facetS);
                float2 facetUV = float2(facetP.x / _Density, facetP.y / (rows * 0.8660254));
                facetUV = _Wrap > 0.5 ? frac(facetUV) : saturate(facetUV);

                // Explicit LOD serves two masters: centroid UVs jump at facet
                // borders, where derivative-picked mips would read garbage; and
                // sampling the mip whose texels span roughly one facet averages
                // the triangle's area, so a lone dark pixel at the centroid can
                // no longer flood its whole facet with an outlier colour
                float facetMip = log2(_TexSize.x / _Density) - 0.5;
                float4 col = tex2Dlod(_MainTex, float4(facetUV, 0.0, facetMip));

                float h1 = hash21(facetUV * 289.0);
                float h2 = hash21(facetUV * 289.0 + 17.31);
                float h3 = hash21(facetUV * 289.0 + 41.17);
                float h4 = hash21(facetUV * 289.0 + 71.3);

                // A minority of facets subdivide into Sierpinski gaskets: fold
                // the cell coordinates through three generations; a point that
                // lands in an inverted child triangle belongs to that hole
                float holeDepth = 0.0;
                if (h4 < _FractalChance)
                {
                    float2 g = lerp(f, 1.0 - f, upper);
                    [unroll]
                    for (int l = 1; l <= 3; l++)
                    {
                        g *= 2.0;
                        if (g.x >= 1.0) g.x -= 1.0;
                        else if (g.y >= 1.0) g.y -= 1.0;
                        else if (g.x + g.y >= 1.0 && holeDepth == 0.0) holeDepth = (float)l;
                    }
                }

                // Normal maps: tilt each facet's normal by a small hashed lean,
                // gasket children leaning their own way. Gentle tilts survive
                // mip averaging, where hard flat facets shaded as dark seams
                if (_NormalPerturb > 0.0)
                {
                    float2 tilt = (float2(h1, h2) - 0.5) * 2.0 * _NormalPerturb;
                    if (holeDepth > 0.0)
                        tilt += (float2(h3, h4) - 0.5) * 2.0 * _NormalPerturb / holeDepth;

                    float3 n = normalize(col.rgb * 2.0 - 1.0);
                    n.xy += tilt;
                    return float4(normalize(n) * 0.5 + 0.5, col.a);
                }

                // Colour maps: fills drift in hue, saturation and luminance.
                // Hue rotates the existing colour, saturation scales it (greys
                // stay grey), luminance scales it (blacks stay black); gasket
                // children shade lighter or darker, deeper generations gentler
                float3 hsv = rgb2hsv(saturate(col.rgb));
                hsv.x = frac(hsv.x + (h1 - 0.5) * _HueJitter);
                hsv.y = saturate(hsv.y * (1.0 + (h2 - 0.5) * 2.0 * _SatJitter));
                col.rgb = hsv2rgb(hsv);
                col.rgb *= 1.0 + (h3 - 0.5) * 2.0 * _Jitter;

                if (holeDepth > 0.0)
                {
                    float dir = hash21(facetUV * 289.0 + 113.7) < 0.5 ? -1.0 : 1.0;
                    col.rgb *= 1.0 + dir * _FractalShade / holeDepth;
                }

                return col;
            }
            ENDCG
        }
    }
}
