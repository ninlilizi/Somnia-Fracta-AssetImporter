using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Unity.Collections;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using Object = UnityEngine.Object;

// Pooled persistent readback buffers, keyed by length and allocated only on
// first demand, so a bulk run reuses one buffer per texture size instead of
// paying an allocation per import. Float elements give sixteen bytes per
// pixel: headroom over every uncompressed format the readback may deliver
static class NKLITextureProcessorArrayStorage
{
    static readonly Dictionary<int, NativeArray<float>> pool = new Dictionary<int, NativeArray<float>>();

    public static NativeArray<float> GetArray(int length)
    {
        NativeArray<float> array;
        if (!pool.TryGetValue(length, out array))
        {
            array = new NativeArray<float>(length, Allocator.Persistent);
            pool[length] = array;
        }
        return array;
    }

    public static void ReleaseResources()
    {
        foreach (NativeArray<float> array in pool.Values)
            array.Dispose();
        pool.Clear();
    }
}

public class NKLITextureProcessor : AssetPostprocessor
{
    // Strength of painterly effect; Max applies where the crystal fades to none
    const float effectStrengthPainterly = 1.5f;
    const float effectStrengthPainterlyMax = 3.0f;

    // Sobel edge guard on the painterly passes: colour/luma gradients above Lo
    // begin restoring source detail, fully restored by Hi; Keep caps the
    // restoration. Every class now runs guardless (Keep 0). Colour maps: the
    // Kuwahara preserves edges on its own, and restoration left detailed
    // surfaces reading as bare source. Normal maps: sources rich in
    // near-discontinuous relief (crystal striations, dense ridges) tripped
    // the guard across their whole body and resurrected the raw relief over
    // the facet planes - the lit surface then followed the source, not the
    // lattice. Raise KeepNormal only if unpainted seams and creases must
    // survive into the paint share of the composite
    const float effectEdgeLo = 0.12f;
    const float effectEdgeHi = 0.55f;
    const float effectEdgeKeep = 0.0f;
    const float effectEdgeKeepNormal = 0.0f;

    // A normal map is a gradient field by construction: its whole body trips
    // the colour thresholds and the guard smothers the paint. These wait for
    // the near-discontinuous gradients of seams, creases and panel lines
    const float effectEdgeLoNormal = 0.6f;
    const float effectEdgeHiNormal = 2.0f;

    // Flow-guided brushwork: the paint is smeared along the content's own
    // gradient flow, so strokes follow grain and contour. Lengths in texels;
    // the deep pass strokes longer. FlowMip picks the blurred mip steering the
    // field — higher values give broader, calmer flow. Zero lengths collapse
    // the stroke integral to an identity copy: smearing washes out the
    // crystal mask's macro darkness
    const float effectStrokeLength = 0.0f;
    const float effectStrokeLengthDeep = 0.0f;
    const float effectFlowMip = 2.0f;

    // Width in texels of the border band cross-faded with the opposite
    // border's mirrored strip on tiling textures. The facet fill averages a
    // facet-sized footprint, so even a texel-thin mismatch between a source's
    // wrap edges would otherwise smear into facet-wide tonal blocks
    const float effectSeamFade = 0.0f;

    // Triangular facets across the width of each texture (keep integer so tiling textures wrap)
    const float effectFacetDensity = 48.0f;

    // Per-facet fill variance: luminance, hue rotation and saturation drift
    const float effectFacetJitter = 0.05f;
    const float effectFacetHueJitter = 0.04f;
    const float effectFacetSatJitter = 0.06f;

    // A minority of facets subdivide into Sierpinski gaskets
    const float effectFractalChance = 0.35f;
    const float effectFractalShade = 0.2f;

    // Gasket shade strength for the darkening children on colour maps;
    // lightening children keep the full effectFractalShade. Multiplicative
    // in linear space, so the perceptual step is uniform across bright and
    // dim albedos - the sunken triangles stay flavour, never the meal
    const float effectFractalShadeDark = 0.05f;

    // Slope shade of gasket children on normal maps: the imprint pressing
    // the fractal into the relief. Far gentler than the colour shade - a
    // slope step is amplified by direct light, where a tint is not
    const float effectNormalFractalShade = 0.05f;

    // Perturbation of specular/metallic maps so facets catch the light;
    // their gasket shade is shared with the normal maps
    const float effectSpecMetJitter = 0.06f;

    // Spec/metallic maps take the facet layer outright - their crystal
    // share pins to this - so the specular response clips to the triangles
    // and no raw surface detail bleeds into the highlights
    const float effectSpecMetCrystal = 1.0f;

    // Facet sparkle: this fraction of facets spike their smoothness (alpha)
    // on spec/metallic maps, so crystal zones glint as the view moves
    const float effectSparkleChance = 0.18f;
    const float effectSparkleAmount = 0.35f;

    // Prismatic dispersion: facet fills split R and B along each facet's
    // hashed direction by this many source texels, as light through crystal
    const float effectDispersion = 0.0f;

    // Unsharp strength applied to each CPU-built mip's RGB, keeping the
    // crystalline character legible at distance
    const float effectMipSharpen = 0.3f;

    // Unsharp strength on the painted base of colour maps: the paint's soft
    // plateau borders regain definition without resurrecting the raw
    // texture the paint unified
    const float effectPaintSharpen = 0.8f;

    // Per-facet normal tilt amplitude. The lean follows a smooth
    // low-frequency field, so the crystal rolls in broad waves; Deviation is
    // the fraction of that amplitude each facet may stray from the field -
    // the true gap between neighbouring triangles under direct light. The
    // fraction is held low as the amplitude climbs, so bolder waves do not
    // drag the facet discord up with them
    const float effectNormalPerturb = 0.2f;
    const float effectNormalDeviation = 0.15f;

    // Downsample divisor of the blur preceding the normals' Kuwahara. The
    // filter's quadrant selection preserves any edge it is given, so relief
    // is dissolved first and the paint forms its plateaus on the broken
    // field. The divisor sets the size of feature that melts — relief
    // smaller than roughly twice this many texels dissolves; 1 disables
    const float effectNormalPreBlur = 16.0f;

    // How far facet interiors flatten normal relief toward their centroid
    // average. Full: each facet is a true cut plane, and all relief within
    // it yields to the tilt waves and the gasket imprint
    const float effectNormalFacetFlatten = 1.0f;

    // How far the facet base plane abandons the source's melted orientation
    // for the flat surface normal. At one, facet orientation is purely the
    // lattice's lean - wholly synthetic cut-gem planes
    const float effectNormalFlatBase = 1.0f;

    // Normal maps composite as pure facet planes: the crystal share pins to
    // one, so each triangle carries a single constant normal and catches
    // light as one rigid body - whole facets flash at once rather than
    // hosting a sheen that travels across them
    const float effectNormalCrystalFloor = 1.0f;
    const float effectNormalCrystalMax = 1.0f;

    // Occlusion maps keep a lifted floor instead, so the painterly wash and
    // the facet lattice stay mingled across the whole surface
    const float effectOccCrystalFloor = 0.6f;

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



    static Material materialGamma;
    static Material materialFlip;
    static Material materialMux;
    static Material materialPaint;
    static Material materialFacet;
    static Material materialGrade;
    static Material materialFlow;

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

    // Bump when shader code changes; the constants join the fingerprint automatically
    const string stylizationVersion = "50";

    // Fingerprint of every setting that shapes the effect; hashed into the
    // custom dependency so changed settings invalidate stale artifacts
    public static string EffectFingerprint()
    {
        return stylizationVersion + "|" + string.Join(",", NKLIAssetStylizer.excludedNameTokens) + "|" +
            string.Join(",", NKLIAssetStylizer.excludedNameSuffixes) + "|" +
            effectStrengthPainterly + "|" + effectStrengthPainterlyMax + "|" +
            effectEdgeLo + "|" + effectEdgeHi + "|" + effectEdgeKeep + "|" + effectEdgeKeepNormal + "|" +
            effectEdgeLoNormal + "|" + effectEdgeHiNormal + "|" +
            effectStrokeLength + "|" + effectStrokeLengthDeep + "|" + effectFlowMip + "|" + effectFacetDensity + "|" +
            effectFacetJitter + "|" + effectFacetHueJitter + "|" + effectFacetSatJitter + "|" +
            effectSeamFade + "|" +
            effectFractalChance + "|" + effectFractalShade + "|" + effectFractalShadeDark + "|" +
            effectNormalFractalShade + "|" +
            effectSpecMetJitter + "|" + effectSpecMetCrystal + "|" +
            effectSparkleChance + "|" + effectSparkleAmount + "|" +
            effectDispersion + "|" + effectMipSharpen + "|" + effectPaintSharpen + "|" +
            effectNormalPerturb + "|" + effectNormalDeviation + "|" + effectNormalPreBlur + "|" + effectNormalFacetFlatten + "|" +
            effectNormalFlatBase + "|" +
            effectNormalCrystalFloor + "|" + effectNormalCrystalMax + "|" + effectOccCrystalFloor + "|" +
            effectLatticeWarp + "|" + effectJuliaZoom + "|" + effectJuliaWarp + "|" +
            effectFiligree + "|" + effectPool + "|" + effectMaskNoise + "|" +
            effectMaskLo + "|" + effectMaskHi + "|" + effectMaskBlur + "|" +
            effectMaskSoften + "|" + effectCrystalMax + "|" +
            effectGuardLo + "|" + effectGuardHi + "|" +
            effectGlowAmount + "|" + effectGlowMip + "|" + effectLift + "|" +
            effectVibrance + "|" + effectLiftColor + "|" + effectShadowTint + "|" +
            effectHighlightTint;
    }

    public static Shader FindShaderRobust(string shaderName, string assetName)
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
            materialPaint != null && materialFacet != null && materialGrade != null &&
            materialFlow != null)
            return true;

        Shader shaderGamma = FindShaderRobust("Hidden/NKLIGammaCorrect", "NKLIGammaCorrect");
        Shader shaderFlip = FindShaderRobust("Hidden/NKLIBlitFlip", "NKLIBlitFlip");
        Shader shaderMux = FindShaderRobust("Hidden/NKLIMuxPaintPixel", "NKLIMuxPaintPixel");
        Shader shaderPaint = FindShaderRobust("CameraFilterPack/Deep_OilPaintHQ", "CameraFilterPack_Pixelisation_DeepOilPaintHQ");
        Shader shaderFacet = FindShaderRobust("Hidden/NKLITriangleFacet", "NKLITriangleFacet");
        Shader shaderGrade = FindShaderRobust("Hidden/NKLIGloamingGrade", "NKLIGloamingGrade");
        Shader shaderFlow = FindShaderRobust("Hidden/NKLIFlowStroke", "NKLIFlowStroke");

        if (shaderGamma == null || shaderFlip == null || shaderMux == null ||
            shaderPaint == null || shaderFacet == null || shaderGrade == null ||
            shaderFlow == null)
            return false;

        if (materialGamma == null) materialGamma = CreateEffectMaterial(shaderGamma);
        if (materialFlip == null) materialFlip = CreateEffectMaterial(shaderFlip);
        if (materialMux == null) materialMux = CreateEffectMaterial(shaderMux);
        if (materialPaint == null) materialPaint = CreateEffectMaterial(shaderPaint);
        if (materialFacet == null) materialFacet = CreateEffectMaterial(shaderFacet);
        if (materialGrade == null) materialGrade = CreateEffectMaterial(shaderGrade);
        if (materialFlow == null) materialFlow = CreateEffectMaterial(shaderFlow);

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
        // dependencies, so fingerprint changes never reimport them. Synthesized
        // outputs likewise: they are finished bakes no setting can stale
        if (assetPath.ToLower().IndexOf(NKLIAssetStylizer.targetString) != -1 &&
            !NKLIAssetStylizer.IsExtensionExcluded(assetPath) &&
            !NKLIAssetStylizer.IsGeneratedOutput(assetPath))
        {
            context.DependsOnCustomDependency(NKLIAssetStylizer.dependencyName);
            context.DependsOnCustomDependency(NKLIAssetStylizer.ClassDependencyName(AssetDatabase.AssetPathToGUID(assetPath)));
        }
    }

    // Feed material imports, deletions and moves to the live classification database
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
    {
        NKLIAssetStylizer.OnMaterialAssetsChanged(importedAssets, deletedAssets, movedAssets, movedFromAssetPaths);
        NKLIAssetStylizer.ReconcileLedger(importedAssets, deletedAssets);
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
            bool isNormalMap = textureImporter.textureType == TextureImporterType.NormalMap;
            bool isOcclusion = !isNormalMap && NKLIAssetStylizer.IsOcclusion(assetPath);

            // Name-excluded textures, excluded file types (skybox .exr) and
            // synthesized outputs (already-stylized bakes) pass through in
            // their pure state. Classification outranks a name exclusion:
            // an AO map seated in an occlusion slot takes the stylization
            // rather than importing untouched
            if ((NKLIAssetStylizer.IsNameExcluded(assetPath) && !isOcclusion) ||
                NKLIAssetStylizer.IsExtensionExcluded(assetPath) || NKLIAssetStylizer.IsGeneratedOutput(assetPath))
            {
                NKLIAssetStylizer.RecordUnstylized(assetPath);
                Debug.Log("Texture left pristine: " + assetPath);
                return;
            }

            // Import workers and headless editors have no GPU; the blit chain
            // would silently no-op and commit garbage, so decline loudly instead
            if (AssetDatabase.IsAssetImportWorkerProcess() ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                NKLIAssetStylizer.RecordUnstylized(assetPath);
                Debug.LogWarning("Somnia Fracta: no GPU available to this import process; texture left unstylized: " + assetPath);
                return;
            }

            bool isSpecMetallic = !isNormalMap && !isOcclusion && NKLIAssetStylizer.IsSpecMetallic(assetPath);
            bool srgbEncode = !isNormalMap && textureImporter.sRGBTexture && PlayerSettings.colorSpace == ColorSpace.Linear;

            // Regenerating mips here is load-bearing: the CPU-side mip levels
            // are not reliably populated at postprocess time, and the alpha
            // splice reads them via GetPixels — without this it splices
            // uninitialized memory into every mip's alpha
            texture.Apply(true, false);

            rtDesc.width = texture.width;
            rtDesc.height = texture.height;
            rtDesc.mipCount = texture.mipmapCount;
            RenderTexture refRTDst = RenderTexture.GetTemporary(rtDesc);

            if (!RenderStylized(texture, refRTDst, texture.width, texture.height, texture.mipmapCount,
                isNormalMap, isSpecMetallic, isOcclusion,
                textureImporter.wrapMode == TextureWrapMode.Repeat, srgbEncode, assetPath))
            {
                RenderTexture.ReleaseTemporary(refRTDst);
                NKLIAssetStylizer.RecordUnstylized(assetPath);
                Debug.LogWarning("Somnia Fracta: effect shaders unavailable during this import; texture left unstylized: " + assetPath +
                    ". Run 'Tools/NKLI/Bulk Stylize Assets/Somnia-Fracta' once the import completes.");
                return;
            }

            // Only the base level is read back from the GPU; the lower mips
            // are rebuilt on the CPU from it, beyond the reach of per-mip
            // readback orientation and stride hazards
            NKLIAssetStylizer.ReportSubStage("Readback");
            int width = texture.width;
            int height = texture.height;
            bool readbackFailed = false;
            {
                NativeArray<float> refArray = NKLITextureProcessorArrayStorage.GetArray(width * height * 4);

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

                    // No orientation vote: the chain's parity is deterministic,
                    // and the preview window - unvoted, reading back the same
                    // way - has always shown true. A per-texture content vote
                    // here once mirrored individual maps of a family whenever
                    // heavy stylization decorrelated a bake from its source
                    // (normals under full facet flattening especially), and a
                    // lone mirrored map splits the facet lattices apart in a
                    // way no per-map correctness can excuse

                    // Spec/metallic maps keep their processed alpha so the
                    // facet sparkle can live in smoothness. Normal maps keep
                    // theirs too: Unity hands them to the postprocessor
                    // AG-swizzled — X slope in alpha, R pinned to one — so
                    // splicing the original alpha would resurrect the source's
                    // X relief over the processed result, leaving the effect
                    // visible in only one slope of the lighting. Only colour
                    // maps splice the original alpha back bit-for-bit
                    if (!isSpecMetallic && !isNormalMap)
                    {
                        Color[] proc = processed;
                        Parallel.For(0, height, row =>
                        {
                            int end = row * width + width;
                            for (int p = row * width; p < end; p++)
                                proc[p].a = original[p].a;
                        });
                    }
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

                        // Rows are independent, so each level fans out across
                        // the cores; levels stay sequential, each feeding the
                        // next. The lambdas read locals, never the mutating
                        // loop-carried variables
                        Color[] src = prev;
                        int sw = pw;
                        int sh = ph;
                        Parallel.For(0, mh, y =>
                        {
                            int y0 = Mathf.Min(y * 2, sh - 1);
                            int y1 = Mathf.Min(y * 2 + 1, sh - 1);
                            for (int x = 0; x < mw; x++)
                            {
                                int x0 = Mathf.Min(x * 2, sw - 1);
                                int x1 = Mathf.Min(x * 2 + 1, sw - 1);
                                level[y * mw + x] = (src[y0 * sw + x0] + src[y0 * sw + x1] +
                                    src[y1 * sw + x0] + src[y1 * sw + x1]) * 0.25f;
                            }
                        });
                        // Unsharp the RGB so the crystalline character stays
                        // legible at distance; alpha keeps the plain box
                        // average and normal maps are left untouched. Deeper
                        // mips cascade from the unsharpened box, so the
                        // sharpening never compounds
                        Color[] toStore = level;
                        if (!isNormalMap && effectMipSharpen > 0.0f && mw >= 4 && mh >= 4)
                        {
                            Color[] sharp = new Color[mw * mh];
                            Parallel.For(0, mh, y =>
                            {
                                for (int x = 0; x < mw; x++)
                                {
                                    float br = 0.0f;
                                    float bg = 0.0f;
                                    float bb = 0.0f;
                                    for (int oy = -1; oy <= 1; oy++)
                                    {
                                        int sy = Mathf.Clamp(y + oy, 0, mh - 1);
                                        for (int ox = -1; ox <= 1; ox++)
                                        {
                                            int sx = Mathf.Clamp(x + ox, 0, mw - 1);
                                            Color c = level[sy * mw + sx];
                                            br += c.r;
                                            bg += c.g;
                                            bb += c.b;
                                        }
                                    }
                                    Color p0 = level[y * mw + x];
                                    sharp[y * mw + x] = new Color(
                                        Mathf.Clamp01(p0.r + (p0.r - br / 9.0f) * effectMipSharpen),
                                        Mathf.Clamp01(p0.g + (p0.g - bg / 9.0f) * effectMipSharpen),
                                        Mathf.Clamp01(p0.b + (p0.b - bb / 9.0f) * effectMipSharpen),
                                        p0.a);
                                }
                            });
                            toStore = sharp;
                        }

                        texture.SetPixels(toStore, m);
                        prev = level;
                        pw = mw;
                        ph = mh;
                    }
                }

                Object.DestroyImmediate(intTex);
            }


            RenderTexture.ReleaseTemporary(refRTDst);

            if (readbackFailed)
            {
                if (!NKLIAssetStylizer.bulkRunActive)
                    NKLITextureProcessorArrayStorage.ReleaseResources();
                NKLIAssetStylizer.RecordUnstylized(assetPath);
                throw new Exception("Somnia Fracta: GPU readback failed twice; texture left unstylized: " + assetPath + " — reimport it to retry.");
            }

            NKLIAssetStylizer.RecordStylized(assetPath);
            Debug.Log("Stylized: " + assetPath);

            // Outside a bulk run, release the native arrays at once rather than
            // holding hundreds of megabytes after a lone import
            if (!NKLIAssetStylizer.bulkRunActive)
                NKLITextureProcessorArrayStorage.ReleaseResources();
        }
    }

    // Diagnostic hook: when set, each chain stage is handed over for dumping
    public static System.Action<RenderTexture, string> diagDump;

    // The one descriptor for every RT in and around the chain. The blit
    // orientation convention follows the destination's descriptor, so dest
    // MUST be allocated from this and nothing else — a plain temporary
    // flips the final image
    public static RenderTextureDescriptor ChainDescriptor(int width, int height, int mipCount)
    {
        rtDesc.width = width;
        rtDesc.height = height;
        rtDesc.mipCount = mipCount;
        return rtDesc;
    }

    // Renders the full stylization chain for one texture into dest (mip 0).
    // Shared by the import postprocessor and the preview window, so the two
    // can never drift apart. Returns false if the effect shaders are missing
    public static bool RenderStylized(Texture source, RenderTexture dest, int texWidth, int texHeight, int mipCount,
        bool isNormalMap, bool isSpecMetallic, bool isOcclusion, bool wraps, bool srgbEncode, string assetPath)
    {
        if (!EnsureShaders())
            return false;

        RenderTexture RTActive = RenderTexture.active;

        rtDesc.width = texWidth;
        rtDesc.height = texHeight;
        rtDesc.mipCount = mipCount;
        RenderTexture refRTSrc = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTInt = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTIntFacet = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTMask = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTIntPaint = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTIntPaintStrong = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTGrade = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTFlow = RenderTexture.GetTemporary(rtDesc);
        RenderTexture refRTStroke = RenderTexture.GetTemporary(rtDesc);

        // Explicit wrap mode on every chain RT: tiling textures need the
        // mip-footprint samples of paint, facet, mask blur and glow to wrap at
        // the borders — and pooled temporaries otherwise carry whatever wrap
        // state their previous user left behind
        TextureWrapMode rtWrap = wraps ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        refRTSrc.wrapMode = rtWrap;
        refRTInt.wrapMode = rtWrap;
        refRTIntFacet.wrapMode = rtWrap;
        refRTMask.wrapMode = rtWrap;
        refRTIntPaint.wrapMode = rtWrap;
        refRTIntPaintStrong.wrapMode = rtWrap;
        refRTGrade.wrapMode = rtWrap;
        refRTFlow.wrapMode = rtWrap;
        refRTStroke.wrapMode = rtWrap;

        // Material blit, never a bare one: a material-less Blit picks internal
        // copy-versus-draw paths whose row conventions differ with the
        // source's format and colour space, flipping some texture classes and
        // not others. A material draw is one deterministic convention for all
        Graphics.Blit(source, refRTSrc, materialFlip);

        // Harmonize the wrap borders before anything samples the source, so
        // the whole chain inherits edges that meet exactly. Two material
        // blits, so the row-inversion parity downstream is undisturbed; the
        // band is capped for small textures so the fade never eats the image
        if (wraps && effectSeamFade > 0.0f)
        {
            materialMux.SetVector("_SeamBand", new Vector4(
                Mathf.Min(effectSeamFade / texWidth, 0.0625f),
                Mathf.Min(effectSeamFade / texHeight, 0.0625f), 0.0f, 0.0f));
            Graphics.Blit(refRTSrc, refRTFlow, materialMux, 3);
            Graphics.Blit(refRTFlow, refRTSrc, materialFlip);
        }

        refRTSrc.filterMode = FilterMode.Trilinear;
        refRTSrc.GenerateMips();

        bool isColour = !isNormalMap && !isSpecMetallic && !isOcclusion;
        Vector4 texSize = new Vector4(texWidth, texHeight, 0.0f, 0.0f);

        if (!isSpecMetallic)
        {
            // Apply 'painterly' filter
            NKLIAssetStylizer.ReportSubStage("Painterly pass");
            materialPaint.SetFloat("_TimeX", 10);
            materialPaint.SetFloat("_Far", 0.5f);
            materialPaint.SetFloat("_Near", 0.0f);
            materialPaint.SetFloat("_Visualize", 0);
            materialPaint.SetFloat("_FarCamera", 1.0f);
            // Negative sentinel: bypasses the shader's depth path (see the
            // NKLI note in the shader), which otherwise samples a stale
            // scene depth texture mid-import
            materialPaint.SetFloat("_FixDistance", -1.0f);
            materialPaint.SetFloat("_LightIntensity", effectStrengthPainterly);
            materialPaint.SetVector("_ScreenResolution", texSize);

            if (isNormalMap)
            {
                // Kuwahara over a pre-broken field: the filter's own quadrant
                // selection preserves any edge it is given, so a mip down/up
                // round trip melts the relief first and the paint calms what
                // remains. No flow strokes — they would drag the encoded
                // vectors off their meaning. The final copy leaves the paint
                // at the even parity the guard's straight sampling expects
                RenderTexture refRTBlur = RenderTexture.GetTemporary(
                    Mathf.Max(1, (int)(texWidth / effectNormalPreBlur)),
                    Mathf.Max(1, (int)(texHeight / effectNormalPreBlur)),
                    0, RenderTextureFormat.ARGBFloat);
                refRTBlur.wrapMode = rtWrap;
                refRTBlur.filterMode = FilterMode.Bilinear;

                // The downsample reads the source's trilinear mip chain, so
                // it is a true box average rather than a sparse skim
                Graphics.Blit(refRTSrc, refRTBlur, materialFlip);
                Graphics.Blit(refRTBlur, refRTStroke, materialFlip);
                RenderTexture.ReleaseTemporary(refRTBlur);
                Graphics.Blit(refRTStroke, refRTInt, materialPaint);
                Graphics.Blit(refRTInt, refRTStroke, materialFlip);
            }
            else
            {
                Graphics.Blit(refRTSrc, refRTInt, materialPaint);

                // Flow field from the source's own gradients; the stroke pass
                // then smears the paint along it, following grain and contour
                NKLIAssetStylizer.ReportSubStage("Flow strokes");
                materialFlow.SetVector("_TexSize", texSize);
                materialFlow.SetFloat("_Wrap", wraps ? 1.0f : 0.0f);
                materialFlow.SetFloat("_FlowMip", effectFlowMip);
                Graphics.Blit(refRTSrc, refRTFlow, materialFlow, 0);

                materialFlow.SetTexture("_FlowTex", refRTFlow);
                materialFlow.SetFloat("_StrokeLength", effectStrokeLength);
                Graphics.Blit(refRTInt, refRTStroke, materialFlow, 1);
            }

            // Sobel edge guard: restore source detail where the paint would
            // smear strong colour or luma edges
            materialMux.SetVector("_TexSize", texSize);
            materialMux.SetFloat("_Wrap", wraps ? 1.0f : 0.0f);
            materialMux.SetFloat("_EdgeLo", isNormalMap ? effectEdgeLoNormal : effectEdgeLo);
            materialMux.SetFloat("_EdgeHi", isNormalMap ? effectEdgeHiNormal : effectEdgeHi);
            materialMux.SetFloat("_EdgeKeep", isNormalMap ? effectEdgeKeepNormal : effectEdgeKeep);
            materialMux.SetFloat("_SharpenAmount", isColour ? effectPaintSharpen : 0.0f);
            materialMux.SetTexture("_PaintTex", refRTStroke);
            Graphics.Blit(refRTSrc, refRTIntPaint, materialMux, 2);

            if (diagDump != null)
            {
                diagDump(refRTInt, "d_paint");
                diagDump(refRTFlow, "d_flow");
                diagDump(refRTStroke, "d_stroke");
                diagDump(refRTIntPaint, "d_sobel");
            }
        }
        else
        {
            // Spec/metallic maps stay unpainted, but still pass through one
            // copy blit so the composite's bound layers share the painted
            // branch's blit generation and stay row-aligned. The copy MUST go
            // through a material: a material-less Blit copies without the
            // row inversion every material blit applies, leaving this layer
            // a generation adrift and part-mirroring the composite
            Graphics.Blit(refRTSrc, refRTIntPaint, materialFlip);
        }

        if (diagDump != null)
        {
            diagDump(refRTSrc, "e_src");
            diagDump(refRTIntPaint, "e_base");
        }

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

        if (isColour)
        {
            // Deeper painterly pass for the regions the crystal leaves
            // untouched, with longer strokes, through the same edge guard
            NKLIAssetStylizer.ReportSubStage("Painterly pass (deep)");
            materialPaint.SetFloat("_LightIntensity", effectStrengthPainterlyMax);
            Graphics.Blit(refRTSrc, refRTInt, materialPaint);
            materialFlow.SetFloat("_StrokeLength", effectStrokeLengthDeep);
            Graphics.Blit(refRTInt, refRTStroke, materialFlow, 1);
            materialMux.SetTexture("_PaintTex", refRTStroke);
            Graphics.Blit(refRTSrc, refRTIntPaintStrong, materialMux, 2);
        }

        // Facets tilt the painted normals, not the raw source — the raw
        // relief would ride the facet layer straight back into the composite.
        // The copy realigns rows (the guarded paint is one blit generation
        // from the source), and the mip chain feeds the facet-averaging
        // tex2Dlod
        if (isNormalMap)
        {
            Graphics.Blit(refRTIntPaint, refRTFlow, materialFlip);
            refRTFlow.filterMode = FilterMode.Trilinear;
            refRTFlow.GenerateMips();
        }

        // Apply triangular facet filter. Colour maps take the full fill
        // drift; spec/metallic and occlusion maps a luminance-only whisper;
        // normal maps a gentle per-facet tilt plus the full gasket shade -
        // all on the same lattice and gasket hashes so every layer catches
        // the light in step
        NKLIAssetStylizer.ReportSubStage("Triangular facets");
        materialFacet.SetVector("_TexSize", texSize);
        materialFacet.SetFloat("_Density", effectFacetDensity);
        materialFacet.SetFloat("_Jitter", isColour ? effectFacetJitter : (isNormalMap ? 0.0f : effectSpecMetJitter));
        materialFacet.SetFloat("_HueJitter", isColour ? effectFacetHueJitter : 0.0f);
        materialFacet.SetFloat("_SatJitter", isColour ? effectFacetSatJitter : 0.0f);
        materialFacet.SetFloat("_FractalChance", effectFractalChance);
        materialFacet.SetFloat("_FractalShade", isNormalMap ? effectNormalFractalShade : effectFractalShade);
        materialFacet.SetFloat("_FractalShadeDark", isColour ? effectFractalShadeDark : effectFractalShade);
        materialFacet.SetFloat("_NormalPerturb", isNormalMap ? effectNormalPerturb : 0.0f);
        materialFacet.SetFloat("_NormalDeviation", effectNormalDeviation);
        materialFacet.SetFloat("_NormalFlatten", effectNormalFacetFlatten);
        materialFacet.SetFloat("_NormalFlatBase", effectNormalFlatBase);
        materialFacet.SetFloat("_LatticeWarp", effectLatticeWarp);
        materialFacet.SetFloat("_Wrap", wraps ? 1.0f : 0.0f);
        materialFacet.SetFloat("_Dispersion", isColour ? effectDispersion : 0.0f);
        materialFacet.SetFloat("_SparkleChance", isSpecMetallic ? effectSparkleChance : 0.0f);
        materialFacet.SetFloat("_SparkleAmount", effectSparkleAmount);
        Graphics.Blit(isNormalMap ? refRTFlow : refRTSrc, refRTIntFacet, materialFacet);

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
        // maps blend between the two paint strengths; normal and occlusion
        // maps blend facets over their single base on the lifted crystal
        // floor; spec/metallic maps take the facet layer outright
        RenderTexture refRTBase = refRTIntPaint;
        materialMux.SetTexture("_PaintTex", refRTBase);
        materialMux.SetTexture("_PaintStrongTex", isColour ? refRTIntPaintStrong : refRTBase);
        materialMux.SetTexture("_FacetTex", refRTIntFacet);
        materialMux.SetTexture("_MaskTex", refRTMask);
        materialMux.SetFloat("_MaskLo", effectMaskLo);
        materialMux.SetFloat("_MaskHi", effectMaskHi);
        materialMux.SetFloat("_MaskBlur", effectMaskBlur);
        materialMux.SetFloat("_MaskSoften", effectMaskSoften);
        materialMux.SetFloat("_CrystalFloor", isSpecMetallic ? effectSpecMetCrystal :
            (isColour ? 0.0f : (isNormalMap ? effectNormalCrystalFloor : effectOccCrystalFloor)));
        materialMux.SetFloat("_CrystalMax", isSpecMetallic ? effectSpecMetCrystal :
            (isColour ? effectCrystalMax : effectNormalCrystalMax));
        materialMux.SetFloat("_GuardLo", effectGuardLo);
        materialMux.SetFloat("_GuardHi", effectGuardHi);
        Graphics.Blit(refRTSrc, refRTInt, materialMux, 1);

        if (diagDump != null)
        {
            diagDump(refRTIntFacet, "e_facet");
            diagDump(refRTInt, "e_mux");
        }

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
        {
            // Ungraded classes take an equalizing copy so both paths reach the
            // final blit at the same generation; skipping it lands spec and
            // normal maps one row-inversion adrift of the colour maps. Normal
            // maps take the copy through the mux's row-flipping pass: the
            // class alignment test proved their branch lands one further
            // inversion adrift, mirroring whole bakes against their siblings
            if (isNormalMap)
                Graphics.Blit(refRTInt, refRTGrade, materialMux, 4);
            else
                Graphics.Blit(refRTInt, refRTGrade, materialFlip);
            refRTGraded = refRTGrade;
        }

        if (diagDump != null)
            diagDump(refRTGraded, "e_graded");

        // Re-encode only what the initial sample decoded: sRGB-flagged
        // textures in a linear-colour-space project. Gamma-space projects
        // sample raw, so their pixels round-trip without conversion
        if (srgbEncode)
        {
            Graphics.Blit(refRTGraded, dest, materialGamma);
        }
        else
            Graphics.Blit(refRTGraded, dest, materialFlip);

        RenderTexture.active = RTActive;

        RenderTexture.ReleaseTemporary(refRTSrc);
        RenderTexture.ReleaseTemporary(refRTInt);
        RenderTexture.ReleaseTemporary(refRTIntFacet);
        RenderTexture.ReleaseTemporary(refRTMask);
        RenderTexture.ReleaseTemporary(refRTIntPaint);
        RenderTexture.ReleaseTemporary(refRTIntPaintStrong);
        RenderTexture.ReleaseTemporary(refRTGrade);
        RenderTexture.ReleaseTemporary(refRTFlow);
        RenderTexture.ReleaseTemporary(refRTStroke);

        return true;
    }
}