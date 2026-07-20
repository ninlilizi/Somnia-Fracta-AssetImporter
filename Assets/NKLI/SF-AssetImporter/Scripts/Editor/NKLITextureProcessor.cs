using System;
using System.Threading.Tasks;

using Unity.Collections;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using Object = UnityEngine.Object;

static class NKLITextureProcessorArrayStorage
{
    public static NativeArray<float> nativeArray4096;
    public static NativeArray<float> nativeArray2048;
    public static NativeArray<float> nativeArray1024;
    public static NativeArray<float> nativeArray512;
    public static NativeArray<float> nativeArray256;
    public static NativeArray<float> nativeArray128;
    public static NativeArray<float> nativeArray64;
    public static NativeArray<float> nativeArray32;
    public static NativeArray<float> nativeArray16;
    public static NativeArray<float> nativeArray8;
    public static NativeArray<float> nativeArray4;
    public static NativeArray<float> nativeArray2;
    public static NativeArray<float> nativeArray1;

    public const int size4096 = 67108864;
    public const int size2048 = 16777216;
    public const int size1024 = 4194304;
    public const int size512 = 1048576;
    public const int size256 = 262144;
    public const int size128 = 65536;
    public const int size64 = 16384;
    public const int size32 = 4096;
    public const int size16 = 1024;
    public const int size8 = 256;
    public const int size4 = 64;
    public const int size2 = 16;
    public const int size1 = 4;

    public static bool allocated4096;
    public static bool allocated2048;
    public static bool allocated1024;
    public static bool allocated512;
    public static bool allocated256;
    public static bool allocated128;
    public static bool allocated64;
    public static bool allocated32;
    public static bool allocated16;
    public static bool allocated8;
    public static bool allocated4;
    public static bool allocated2;
    public static bool allocated1;

    private static bool resourcesAllocated;

    public static void AllocateResources()
    {
        if (!resourcesAllocated)
        {
            if (!allocated4096)
            {
                allocated4096 = true;
                nativeArray4096 = new NativeArray<float>(size4096, Allocator.Persistent);
            }
            if (!allocated2048)
            {
                allocated2048 = true;
                nativeArray2048 = new NativeArray<float>(size2048, Allocator.Persistent);
            }
            if (!allocated1024)
            {
                allocated1024 = true;
                nativeArray1024 = new NativeArray<float>(size1024, Allocator.Persistent);
            }
            if (!allocated512)
            {
                allocated512 = true;
                nativeArray512 = new NativeArray<float>(size512, Allocator.Persistent);
            }
            if (!allocated256)
            {
                allocated256 = true;
                nativeArray256 = new NativeArray<float>(size256, Allocator.Persistent);
            }
            if (!allocated128)
            {
                allocated128 = true;
                nativeArray128 = new NativeArray<float>(size128, Allocator.Persistent);
            }
            if (!allocated64)
            {
                allocated64 = true;
                nativeArray64 = new NativeArray<float>(size64, Allocator.Persistent);
            }
            if (!allocated32)
            {
                allocated32 = true;
                nativeArray32 = new NativeArray<float>(size32, Allocator.Persistent);
            }
            if (!allocated16)
            {
                allocated16 = true;
                nativeArray16 = new NativeArray<float>(size16, Allocator.Persistent);
            }
            if (!allocated8)
            {
                allocated8 = true;
                nativeArray8 = new NativeArray<float>(size8, Allocator.Persistent);
            }
            if (!allocated4)
            {
                allocated4 = true;
                nativeArray4 = new NativeArray<float>(size4, Allocator.Persistent);
            }
            if (!allocated2)
            {
                allocated2 = true;
                nativeArray2 = new NativeArray<float>(size2, Allocator.Persistent);
            }
            if (!allocated1)
            {
                allocated1 = true;
                nativeArray1 = new NativeArray<float>(size1, Allocator.Persistent);
            }
        }

        resourcesAllocated = true;
    }

    public static void ReleaseResources()
    {
        resourcesAllocated = false;

        if (allocated4096)
        {
            allocated4096 = false;
            nativeArray4096.Dispose();
        }

        if (allocated2048)
        {
            allocated2048 = false;
            nativeArray2048.Dispose();
        }

        if (allocated1024)
        {
            allocated1024 = false;
            nativeArray1024.Dispose();
        }

        if (allocated512)
        {
            allocated512 = false;
            nativeArray512.Dispose();
        }

        if (allocated256)
        {
            allocated256 = false;
            nativeArray256.Dispose();
        }

        if (allocated128)
        {
            allocated128 = false;
            nativeArray128.Dispose();
        }

        if (allocated64)
        {
            allocated64 = false;
            nativeArray64.Dispose();
        }

        if (allocated32)
        {
            allocated32 = false;
            nativeArray32.Dispose();
        }

        if (allocated16)
        {
            allocated16 = false;
            nativeArray16.Dispose();
        }

        if (allocated8)
        {
            allocated8 = false;
            nativeArray8.Dispose();
        }

        if (allocated4)
        {
            allocated4 = false;
            nativeArray4.Dispose();
        }

        if (allocated2)
        {
            allocated2 = false;
            nativeArray2.Dispose();
        }

        if (allocated1)
        {
            allocated1 = false;
            nativeArray1.Dispose();
        }
    }
}

public class NKLITextureProcessor : AssetPostprocessor
{
    // Strength of painterly effect; Max applies where the crystal fades to none
    const float effectStrengthPainterly = 2.0f;
    const float effectStrengthPainterlyMax = 4.0f;

    // Sobel edge guard on the painterly passes: colour/luma gradients above Lo
    // begin restoring source detail, fully restored by Hi; Keep caps the
    // restoration so the paint still unifies the surface
    const float effectEdgeLo = 0.12f;
    const float effectEdgeHi = 0.55f;
    const float effectEdgeKeep = 0.85f;

    // Triangular facets across the width of each texture (keep integer so tiling textures wrap)
    const float effectFacetDensity = 48.0f;

    // Per-facet fill variance: luminance, hue rotation and saturation drift
    const float effectFacetJitter = 0.05f;
    const float effectFacetHueJitter = 0.04f;
    const float effectFacetSatJitter = 0.06f;

    // A minority of facets subdivide into Sierpinski gaskets
    const float effectFractalChance = 0.35f;
    const float effectFractalShade = 0.2f;

    // Perturbation of specular/metallic maps so facets catch the light
    const float effectSpecMetJitter = 0.06f;
    const float effectSpecMetFractalShade = 0.1f;

    // Per-facet normal tilt; gentle enough to survive mip averaging
    const float effectNormalPerturb = 0.05f;

    // Lattice warp in cell units; melts the mechanical regularity of the grid
    const float effectLatticeWarp = 1.0f;

    // Julia-set crystallization mask
    const float effectJuliaZoom = 1.2f;
    const float effectJuliaWarp = 0.35f;
    const float effectFiligree = 0.85f;
    const float effectPool = 0.55f;
    const float effectMaskNoise = 0.45f;
    const float effectMaskLo = 0.2f;
    const float effectMaskHi = 1.0f;

    // Mask softening: mip level of the spatial blur and how strongly the
    // blurred field replaces the raw one
    const float effectMaskBlur = 5.0f;
    const float effectMaskSoften = 0.7f;

    // Content guard: crystallization fades where a facet's fill strays this far
    // from the local paint (neighbouring atlas islands, gutters)
    const float effectGuardLo = 0.18f;
    const float effectGuardHi = 0.45f;

    // Ceiling on crystallization so facets always keep a painterly residue
    const float effectCrystalMax = 1.0f;

    // Gloaming grade
    const float effectGlowAmount = 0.35f;
    const float effectGlowMip = 3.0f;
    const float effectLift = 0.045f;

    // Vibrance strength: muted colours are enriched more than vivid ones
    const float effectVibrance = 0.35f;
    static readonly Color effectLiftColor = new Color(0.10f, 0.07f, 0.16f);
    static readonly Color effectShadowTint = new Color(0.88f, 0.82f, 0.98f);
    static readonly Color effectHighlightTint = new Color(1.0f, 0.97f, 0.88f);

    // Julia constants; each texture folder is seeded with one member of the family
    static readonly Vector2[] juliaFamily =
    {
        new Vector2(-0.7269f,  0.1889f),
        new Vector2( 0.285f,   0.01f),
        new Vector2(-0.8f,     0.156f),
        new Vector2(-0.4f,     0.6f),
        new Vector2( 0.355f,   0.355f),
        new Vector2(-0.1f,     0.651f),
        new Vector2(-0.835f,  -0.2321f),
        new Vector2(-0.7885f,  0.0f),
    };



    static Shader shaderGamma;
    static Shader shaderFlip;
    static Shader shaderMux;
    static Shader shaderPaint;
    static Shader shaderFacet;
    static Shader shaderGrade;

    static Material materialGamma;
    static Material materialFlip;
    static Material materialMux;
    static Material materialPaint;
    static Material materialFacet;
    static Material materialGrade;

    static RenderTextureDescriptor rtDesc = new RenderTextureDescriptor
    {
        msaaSamples = 1,
        volumeDepth = 1,
        useMipMap = true,
        width = 0,
        height = 0,
        mipCount = 0,
        dimension = TextureDimension.Tex2D,
        colorFormat = RenderTextureFormat.ARGBFloat
    };

    static int width = 0;
    static int height = 0;

    // Bump when shader code changes; the constants join the fingerprint automatically
    const string stylizationVersion = "22";

    // Fingerprint of every setting that shapes the effect; hashed into the
    // custom dependency so changed settings invalidate stale artifacts
    public static string EffectFingerprint()
    {
        return stylizationVersion + "|" + string.Join(",", NKLIAssetStylizer.excludedNameTokens) + "|" +
            string.Join(",", NKLIAssetStylizer.excludedNameSuffixes) + "|" +
            effectStrengthPainterly + "|" + effectStrengthPainterlyMax + "|" +
            effectEdgeLo + "|" + effectEdgeHi + "|" + effectEdgeKeep + "|" + effectFacetDensity + "|" +
            effectFacetJitter + "|" + effectFacetHueJitter + "|" + effectFacetSatJitter + "|" +
            effectFractalChance + "|" + effectFractalShade + "|" +
            effectSpecMetJitter + "|" + effectSpecMetFractalShade + "|" +
            effectNormalPerturb + "|" +
            effectLatticeWarp + "|" + effectJuliaZoom + "|" + effectJuliaWarp + "|" +
            effectFiligree + "|" + effectPool + "|" + effectMaskNoise + "|" +
            effectMaskLo + "|" + effectMaskHi + "|" + effectMaskBlur + "|" +
            effectMaskSoften + "|" + effectCrystalMax + "|" +
            effectGuardLo + "|" + effectGuardHi + "|" +
            effectGlowAmount + "|" + effectGlowMip + "|" + effectLift + "|" +
            effectVibrance + "|" + effectLiftColor + "|" + effectShadowTint + "|" +
            effectHighlightTint;
    }

    static Shader FindShaderRobust(string shaderName, string assetName)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader != null)
            return shader;

        // During a clean Library rebuild the shader may not be imported yet;
        // loading it by asset path imports it on demand
        foreach (string guid in AssetDatabase.FindAssets(assetName + " t:Shader"))
        {
            shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
            if (shader != null && shader.name == shaderName)
                return shader;
        }
        return null;
    }

    static Material CreateEffectMaterial(Shader shader)
    {
        // HideAndDontSave shields the material from the editor's
        // unused-asset sweeps, which otherwise destroy it mid-session
        return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
    }

    // Liveness is judged on the materials themselves rather than a flag:
    // destroyed Unity objects compare equal to null, so a purged cache
    // rebuilds instead of dereferencing corpses
    static bool EnsureShaders()
    {
        if (materialGamma != null && materialFlip != null && materialMux != null &&
            materialPaint != null && materialFacet != null && materialGrade != null)
            return true;

        shaderGamma = FindShaderRobust("Hidden/NKLIGammaCorrect", "NKLIGammaCorrect");
        shaderFlip = FindShaderRobust("Hidden/NKLIBlitFlip", "NKLIBlitFlip");
        shaderMux = FindShaderRobust("Hidden/NKLIMuxPaintPixel", "NKLIMuxPaintPixel");
        shaderPaint = FindShaderRobust("CameraFilterPack/Deep_OilPaintHQ", "CameraFilterPack_Pixelisation_DeepOilPaintHQ");
        shaderFacet = FindShaderRobust("Hidden/NKLITriangleFacet", "NKLITriangleFacet");
        shaderGrade = FindShaderRobust("Hidden/NKLIGloamingGrade", "NKLIGloamingGrade");

        if (shaderGamma == null || shaderFlip == null || shaderMux == null ||
            shaderPaint == null || shaderFacet == null || shaderGrade == null)
            return false;

        if (materialGamma == null) materialGamma = CreateEffectMaterial(shaderGamma);
        if (materialFlip == null) materialFlip = CreateEffectMaterial(shaderFlip);
        if (materialMux == null) materialMux = CreateEffectMaterial(shaderMux);
        if (materialPaint == null) materialPaint = CreateEffectMaterial(shaderPaint);
        if (materialFacet == null) materialFacet = CreateEffectMaterial(shaderFacet);
        if (materialGrade == null) materialGrade = CreateEffectMaterial(shaderGrade);

        return true;
    }

    // FNV-1a; stable across editor sessions, unlike string.GetHashCode
    static uint HashPath(string path)
    {
        uint hash = 2166136261u;
        foreach (char c in path)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    // Force mipmap settings
    void OnPreprocessTexture()
    {
        TextureImporter textureImporter = (TextureImporter)assetImporter;
        if (textureImporter.textureType == TextureImporterType.Default || textureImporter.textureType == TextureImporterType.NormalMap)
        {
            textureImporter.streamingMipmaps = true;
        }

        // Marked textures depend on the global settings fingerprint and on their
        // own classification entry, so material slot changes re-bake exactly the
        // textures whose role changed. Excluded file types stay inert: no
        // dependencies, so fingerprint changes never reimport them
        if (assetPath.ToLower().IndexOf(NKLIAssetStylizer.targetString) != -1 &&
            !NKLIAssetStylizer.IsExtensionExcluded(assetPath))
        {
            context.DependsOnCustomDependency(NKLIAssetStylizer.dependencyName);
            context.DependsOnCustomDependency(NKLIAssetStylizer.ClassDependencyName(AssetDatabase.AssetPathToGUID(assetPath)));
        }
    }

    // Feed material imports, deletions and moves to the live classification database
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        NKLIAssetStylizer.OnMaterialAssetsChanged(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
    }

    // Processes textures. Runs for every import of a marked texture — bulk menu
    // runs, right-click reimports and fresh imports alike — keeping results
    // deterministic so the asset pipeline never sees two outcomes for one input
    void OnPostprocessTexture(Texture2D texture)
    {
        // Only post process textures whose path carries the stylization marker
        string lowerCaseAssetPath = assetPath.ToLower();
        if (lowerCaseAssetPath.IndexOf(NKLIAssetStylizer.targetString) == -1)
            return;

        TextureImporter textureImporter = (TextureImporter)assetImporter;
        if (textureImporter.textureType == TextureImporterType.Default || textureImporter.textureType == TextureImporterType.NormalMap)
        {
            // Occlusion maps, name-excluded textures and excluded file types
            // (skybox .exr) pass through in their pure state
            if (NKLIAssetStylizer.IsOcclusion(assetPath) || NKLIAssetStylizer.IsNameExcluded(assetPath) ||
                NKLIAssetStylizer.IsExtensionExcluded(assetPath))
            {
                Debug.Log("Texture left pristine: " + assetPath);
                return;
            }

            // Import workers and headless editors have no GPU; the blit chain
            // would silently no-op and commit garbage, so decline loudly instead
            if (AssetDatabase.IsAssetImportWorkerProcess() ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogWarning("Somnia Fracta: no GPU available to this import process; texture left unstylized: " + assetPath);
                return;
            }

            if (!EnsureShaders())
            {
                Debug.LogWarning("Somnia Fracta: effect shaders unavailable during this import; texture left unstylized: " + assetPath +
                    ". Run 'NKLI/Bulk Stylize Assets/Somnia-Fracta' once the import completes.");
                return;
            }

            RenderTexture RTActive = RenderTexture.active;

            // Allocate Native Arrays
            NKLITextureProcessorArrayStorage.AllocateResources();

            rtDesc.width = texture.width;
            rtDesc.height = texture.height;
            rtDesc.mipCount = texture.mipmapCount;
            RenderTexture refRTSrc = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTDst = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTInt = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTIntFacet = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTMask = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTIntPaint = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTIntPaintStrong = RenderTexture.GetTemporary(rtDesc);
            RenderTexture refRTGrade = RenderTexture.GetTemporary(rtDesc);

            // Regenerating mips here is load-bearing: the CPU-side mip levels
            // are not reliably populated at postprocess time, and the alpha
            // splice reads them via GetPixels — without this it splices
            // uninitialized memory into every mip's alpha
            texture.Apply(true, false);
            Graphics.Blit(texture, refRTSrc);
            refRTSrc.filterMode = FilterMode.Trilinear;
            refRTSrc.GenerateMips();


            bool isNormalMap = textureImporter.textureType == TextureImporterType.NormalMap;
            bool isSpecMetallic = !isNormalMap && NKLIAssetStylizer.IsSpecMetallic(assetPath);
            bool wraps = textureImporter.wrapMode == TextureWrapMode.Repeat;
            Vector4 texSize = new Vector4(texture.width, texture.height, 0.0f, 0.0f);

            // Apply 'painterly' filter
            NKLIAssetStylizer.ReportSubStage("Painterly pass");
            materialPaint.SetFloat("_TimeX", 10);
            materialPaint.SetFloat("_Far", 0.5f);
            materialPaint.SetFloat("_Near", 0.0f);
            materialPaint.SetFloat("_Visualize", 0);
            materialPaint.SetFloat("_FarCamera", 1.0f);
            // Negative sentinel: bypasses the shader's depth path (see the
            // NKLI note in the shader), which otherwise samples a stale scene
            // depth texture mid-import
            materialPaint.SetFloat("_FixDistance", -1.0f);
            materialPaint.SetFloat("_LightIntensity", effectStrengthPainterly);
            materialPaint.SetVector("_ScreenResolution", new Vector4(texture.width, texture.height, 0.0f, 0.0f));
            Graphics.Blit(refRTSrc, refRTInt, materialPaint);

            // Sobel edge guard: restore source detail where the paint would
            // smear strong colour or luma edges
            materialMux.SetVector("_TexSize", texSize);
            materialMux.SetFloat("_Wrap", wraps ? 1.0f : 0.0f);
            materialMux.SetFloat("_EdgeLo", effectEdgeLo);
            materialMux.SetFloat("_EdgeHi", effectEdgeHi);
            materialMux.SetFloat("_EdgeKeep", effectEdgeKeep);
            materialMux.SetTexture("_PaintTex", refRTInt);
            Graphics.Blit(refRTSrc, refRTIntPaint, materialMux, 2);

            // Seed the Julia constant from the containing folder, so every map
            // of one asset shares a mask and every folder is a unique variation
            string seedPath = System.IO.Path.GetDirectoryName(assetPath).ToLower().Replace('\\', '/');
            uint seed = HashPath(seedPath);
            Vector2 juliaConstant = juliaFamily[(int)(seed % (uint)juliaFamily.Length)];
            juliaConstant.x += (((seed >> 3) & 0xFFu) / 255.0f - 0.5f) * 0.03f;
            juliaConstant.y += (((seed >> 11) & 0xFFu) / 255.0f - 0.5f) * 0.03f;
            Vector4 juliaC = new Vector4(juliaConstant.x, juliaConstant.y,
                ((seed >> 19) & 0xFFu) / 255.0f, ((seed >> 24) & 0xFFu) / 255.0f);
            float juliaRotation = ((seed * 2654435761u) & 0xFFFFu) / 65535.0f * Mathf.PI * 2.0f;

            RenderTexture refRTGraded;
            bool isColour = !isNormalMap && !isSpecMetallic;

            if (isColour)
            {
                // Deeper painterly pass for the regions the crystal leaves
                // untouched, through the same edge guard
                NKLIAssetStylizer.ReportSubStage("Painterly pass (deep)");
                materialPaint.SetFloat("_LightIntensity", effectStrengthPainterlyMax);
                Graphics.Blit(refRTSrc, refRTInt, materialPaint);
                materialMux.SetTexture("_PaintTex", refRTInt);
                Graphics.Blit(refRTSrc, refRTIntPaintStrong, materialMux, 2);
            }

            if (isNormalMap)
            {
                // The facet pass reads the painterly normals through tex2Dlod
                refRTIntPaint.filterMode = FilterMode.Trilinear;
                refRTIntPaint.GenerateMips();
            }

            // Apply triangular facet filter. Colour maps take the full fill
            // drift; spec/metallic maps a luminance-only whisper; normal maps a
            // gentle per-facet tilt — all on the same lattice and gasket hashes
            // so every layer catches the light in step
            NKLIAssetStylizer.ReportSubStage("Triangular facets");
            materialFacet.SetVector("_TexSize", texSize);
            materialFacet.SetFloat("_Density", effectFacetDensity);
            materialFacet.SetFloat("_Jitter", isColour ? effectFacetJitter : (isSpecMetallic ? effectSpecMetJitter : 0.0f));
            materialFacet.SetFloat("_HueJitter", isColour ? effectFacetHueJitter : 0.0f);
            materialFacet.SetFloat("_SatJitter", isColour ? effectFacetSatJitter : 0.0f);
            materialFacet.SetFloat("_FractalChance", effectFractalChance);
            materialFacet.SetFloat("_FractalShade", isColour ? effectFractalShade : (isSpecMetallic ? effectSpecMetFractalShade : 0.0f));
            materialFacet.SetFloat("_NormalPerturb", isNormalMap ? effectNormalPerturb : 0.0f);
            materialFacet.SetFloat("_LatticeWarp", effectLatticeWarp);
            materialFacet.SetFloat("_Wrap", wraps ? 1.0f : 0.0f);
            Graphics.Blit(isNormalMap ? refRTIntPaint : refRTSrc, refRTIntFacet, materialFacet);

            // Render the Julia crystallization mask, then mip-blur it so
            // crystal and paint trade places across wide, gentle borders
            NKLIAssetStylizer.ReportSubStage("Julia crystallization");
            materialMux.SetVector("_JuliaC", juliaC);
            materialMux.SetVector("_TexSize", texSize);
            materialMux.SetFloat("_JuliaZoom", effectJuliaZoom);
            materialMux.SetFloat("_JuliaRot", juliaRotation);
            materialMux.SetFloat("_JuliaWarp", effectJuliaWarp);
            materialMux.SetFloat("_Filigree", effectFiligree);
            materialMux.SetFloat("_Pool", effectPool);
            materialMux.SetFloat("_MaskNoise", effectMaskNoise);
            materialMux.SetFloat("_Wrap", wraps ? 1.0f : 0.0f);
            refRTMask.filterMode = FilterMode.Trilinear;
            Graphics.Blit(refRTSrc, refRTMask, materialMux, 0);
            refRTMask.GenerateMips();

            // Composite the base and facets through the softened mask. Colour
            // maps blend between the two paint strengths; spec/metallic maps
            // blend facets over the untouched source; normals over the paint
            RenderTexture refRTBase = isSpecMetallic ? refRTSrc : refRTIntPaint;
            materialMux.SetTexture("_PaintTex", refRTBase);
            materialMux.SetTexture("_PaintStrongTex", isColour ? refRTIntPaintStrong : refRTBase);
            materialMux.SetTexture("_FacetTex", refRTIntFacet);
            materialMux.SetTexture("_MaskTex", refRTMask);
            materialMux.SetFloat("_MaskLo", effectMaskLo);
            materialMux.SetFloat("_MaskHi", effectMaskHi);
            materialMux.SetFloat("_MaskBlur", effectMaskBlur);
            materialMux.SetFloat("_MaskSoften", effectMaskSoften);
            materialMux.SetFloat("_CrystalMax", effectCrystalMax);
            materialMux.SetFloat("_GuardLo", effectGuardLo);
            materialMux.SetFloat("_GuardHi", effectGuardHi);
            Graphics.Blit(refRTSrc, refRTInt, materialMux, 1);

            if (isColour)
            {
                // Gloaming grade; glow samples the mip chain
                NKLIAssetStylizer.ReportSubStage("Gloaming grade");
                refRTInt.filterMode = FilterMode.Trilinear;
                refRTInt.GenerateMips();
                materialGrade.SetFloat("_GlowAmount", effectGlowAmount);
                materialGrade.SetFloat("_GlowMip", effectGlowMip);
                materialGrade.SetFloat("_Lift", effectLift);
                materialGrade.SetColor("_LiftColor", effectLiftColor);
                materialGrade.SetColor("_ShadowTint", effectShadowTint);
                materialGrade.SetColor("_HighlightTint", effectHighlightTint);
                materialGrade.SetFloat("_Vibrance", effectVibrance);
                Graphics.Blit(refRTInt, refRTGrade, materialGrade);
                refRTGraded = refRTGrade;
            }
            else
                refRTGraded = refRTInt;

            // Re-encode only what the initial sample decoded: sRGB-flagged
            // textures in a linear-colour-space project. Gamma-space projects
            // sample raw, so their pixels round-trip without conversion
            if (!isNormalMap && textureImporter.sRGBTexture && PlayerSettings.colorSpace == ColorSpace.Linear)
            {
                Graphics.Blit(refRTGraded, refRTDst, materialGamma);
            }
            else
                Graphics.Blit(refRTGraded, refRTDst, materialFlip);


            // Only the base level is read back from the GPU; the lower mips
            // are rebuilt on the CPU from it, beyond the reach of per-mip
            // readback orientation and stride hazards
            NKLIAssetStylizer.ReportSubStage("Readback");
            width = texture.width;
            height = texture.height;
            bool readbackFailed = false;
            {
                bool npotArray = false;
                NativeArray<float> refArray;
                switch ((int)(width * height * 4))
                {
                    case NKLITextureProcessorArrayStorage.size4096:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray4096;
                        break;
                    case NKLITextureProcessorArrayStorage.size2048:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray2048;
                        break;
                    case NKLITextureProcessorArrayStorage.size1024:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray1024;
                        break;
                    case NKLITextureProcessorArrayStorage.size512:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray512;
                        break;
                    case NKLITextureProcessorArrayStorage.size256:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray256;
                        break;
                    case NKLITextureProcessorArrayStorage.size128:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray128;
                        break;
                    case NKLITextureProcessorArrayStorage.size64:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray64;
                        break;
                    case NKLITextureProcessorArrayStorage.size32:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray32;
                        break;
                    case NKLITextureProcessorArrayStorage.size16:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray16;
                        break;
                    case NKLITextureProcessorArrayStorage.size8:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray8;
                        break;
                    case NKLITextureProcessorArrayStorage.size4:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray4;
                        break;
                    case NKLITextureProcessorArrayStorage.size2:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray2;
                        break;
                    case NKLITextureProcessorArrayStorage.size1:
                        refArray = NKLITextureProcessorArrayStorage.nativeArray1;
                        break;
                    default:
                        refArray = new NativeArray<float>(width * height * 4, Allocator.Persistent);
                        npotArray = true;
                        break;
                }

                Texture2D intTex = new Texture2D(width, height, texture.format, false);

                // A failed readback leaves the shared array holding the previous
                // texture's data; splicing that in would commit silent corruption,
                // so failures retry once and then abort the import loudly
                AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref refArray, refRTDst, 0, texture.format);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    request = AsyncGPUReadback.RequestIntoNativeArray(ref refArray, refRTDst, 0, texture.format);
                    request.WaitForCompletion();
                }
                if (request.hasError)
                {
                    readbackFailed = true;
                }
                else
                {
                    intTex.LoadRawTextureData(refArray);

                    // Alpha passes through unmolested: splice the original base
                    // alpha back over the processed colour, bit for bit, beyond
                    // the reach of any GPU pass or readback format conversion
                    Color[] processed = intTex.GetPixels(0);
                    Color[] original = texture.GetPixels(0);

                    // The chain's net row order follows graphics-API blit
                    // conventions; measure which orientation correlates with
                    // the source and correct in place, so bakes land upright
                    // on every backend
                    float diffStraight = 0.0f;
                    float diffFlipped = 0.0f;
                    int stride = Mathf.Max(1, (width * height) / 16384);
                    for (int p = 0; p < processed.Length; p += stride)
                    {
                        int row = p / width;
                        int pf = (height - 1 - row) * width + (p - row * width);
                        float lo = original[p].r * 0.299f + original[p].g * 0.587f + original[p].b * 0.114f;
                        diffStraight += Mathf.Abs(processed[p].r * 0.299f + processed[p].g * 0.587f + processed[p].b * 0.114f - lo);
                        diffFlipped += Mathf.Abs(processed[pf].r * 0.299f + processed[pf].g * 0.587f + processed[pf].b * 0.114f - lo);
                    }
                    if (diffFlipped < diffStraight)
                    {
                        Color[] flipped = new Color[processed.Length];
                        for (int row = 0; row < height; row++)
                            System.Array.Copy(processed, row * width, flipped, (height - 1 - row) * width, width);
                        processed = flipped;
                    }

                    for (int p = 0; p < processed.Length; p++)
                        processed[p].a = original[p].a;
                    texture.SetPixels(processed, 0);

                    // Box-filter the corrected base down the whole chain,
                    // spliced alpha included; the final mip is no longer left
                    // to chance
                    NKLIAssetStylizer.ReportSubStage("Building mips");
                    Color[] prev = processed;
                    int pw = width;
                    int ph = height;
                    for (int m = 1; m < texture.mipmapCount; ++m)
                    {
                        int mw = Mathf.Max(1, pw / 2);
                        int mh = Mathf.Max(1, ph / 2);
                        Color[] level = new Color[mw * mh];
                        for (int y = 0; y < mh; y++)
                        {
                            int y0 = Mathf.Min(y * 2, ph - 1);
                            int y1 = Mathf.Min(y * 2 + 1, ph - 1);
                            for (int x = 0; x < mw; x++)
                            {
                                int x0 = Mathf.Min(x * 2, pw - 1);
                                int x1 = Mathf.Min(x * 2 + 1, pw - 1);
                                level[y * mw + x] = (prev[y0 * pw + x0] + prev[y0 * pw + x1] +
                                    prev[y1 * pw + x0] + prev[y1 * pw + x1]) * 0.25f;
                            }
                        }
                        texture.SetPixels(level, m);
                        prev = level;
                        pw = mw;
                        ph = mh;
                    }
                }

                Object.DestroyImmediate(intTex);
                if (npotArray)
                    refArray.Dispose();
            }


            // Clean up
            RenderTexture.active = RTActive;
            RTActive = null;

            RenderTexture.ReleaseTemporary(refRTSrc);
            RenderTexture.ReleaseTemporary(refRTDst);
            RenderTexture.ReleaseTemporary(refRTInt);
            RenderTexture.ReleaseTemporary(refRTIntFacet);
            RenderTexture.ReleaseTemporary(refRTMask);
            RenderTexture.ReleaseTemporary(refRTIntPaint);
            RenderTexture.ReleaseTemporary(refRTIntPaintStrong);
            RenderTexture.ReleaseTemporary(refRTGrade);

            if (readbackFailed)
            {
                if (!NKLIAssetStylizer.bulkRunActive)
                    NKLITextureProcessorArrayStorage.ReleaseResources();
                throw new Exception("Somnia Fracta: GPU readback failed twice; texture left unstylized: " + assetPath + " — reimport it to retry.");
            }

            Debug.Log("Stylized: " + assetPath);

            // Outside a bulk run, release the native arrays at once rather than
            // holding hundreds of megabytes after a lone import
            if (!NKLIAssetStylizer.bulkRunActive)
                NKLITextureProcessorArrayStorage.ReleaseResources();
        }
    }
}