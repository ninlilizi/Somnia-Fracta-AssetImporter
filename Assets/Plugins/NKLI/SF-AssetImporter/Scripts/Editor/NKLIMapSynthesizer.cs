using System.Collections.Generic;

using Unity.Collections;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

using Object = UnityEngine.Object;

// Synthesizes specular, metallic and occlusion maps for a material from its
// albedo and normal content alone, invoked from the material's right-click
// menu. Spec and metallic bakes pass through the same type-specific
// stylization an existing map of their class receives; occlusion is left
// pure, as imported occlusion maps are. Results land in an sf-generated
// folder beside the albedo, import pristine, and are bound to the material
public static class NKLIMapSynthesizer
{
    // Specular intensity: a luminance curve swept between floor and ceiling,
    // carrying a whisper of the albedo's own chroma
    const float synthSpecFloor = 0.04f;
    const float synthSpecCeil = 0.55f;
    const float synthSpecCurve = 1.6f;
    const float synthSpecTint = 0.25f;

    // Smoothness (alpha of both spec and metallic maps): brighter and flatter
    // surfaces polish up; mip-scale micro-contrast reads as roughness
    const float synthSmoothBase = 0.25f;
    const float synthSmoothLuma = 0.35f;
    const float synthSmoothDetail = 1.5f;
    const float synthSmoothFlat = 0.15f;

    // Metal response: bright, chroma-poor albedo reads as metal
    const float synthMetalLumaLo = 0.35f;
    const float synthMetalLumaHi = 0.8f;
    const float synthMetalSatReject = 1.75f;
    const float synthMetalAmount = 0.85f;

    // Crevice response from the normal field
    const float synthCavityGain = 6.0f;
    const float synthCavitySpec = 0.6f;
    const float synthCavitySmooth = 0.4f;

    // Occlusion shaping
    const float synthOccCavity = 1.2f;
    const float synthOccShade = 0.75f;
    const float synthOccFloor = 0.25f;

    static readonly string[] albedoProperties = { "_MainTex", "_BaseMap", "_BaseColorMap" };
    static readonly string[] normalProperties = { "_BumpMap", "_NormalMap" };

    const string menuPath = "Assets/NKLI/Somnia Fracta - Generate Spec-Metal-Occlusion Maps";

    static Shader shaderSynth;
    static Material materialSynth;

    static bool EnsureSynthShader()
    {
        if (materialSynth != null)
            return true;

        shaderSynth = NKLITextureProcessor.FindShaderRobust("Hidden/NKLIMapSynth", "NKLIMapSynth");
        if (shaderSynth == null)
            return false;

        materialSynth = new Material(shaderSynth) { hideFlags = HideFlags.HideAndDontSave };
        return true;
    }

    [MenuItem(menuPath, true)]
    static bool ValidateGenerate()
    {
        return Selection.GetFiltered<Material>(SelectionMode.Assets).Length > 0;
    }

    [MenuItem(menuPath)]
    static void Generate()
    {
        Material[] materials = Selection.GetFiltered<Material>(SelectionMode.Assets);
        if (materials.Length == 0)
            return;

        if (!EnsureSynthShader())
        {
            Debug.LogWarning("Somnia Fracta: synthesis shader unavailable; no maps generated.");
            return;
        }

        // Materials sharing an albedo share one bake; each still receives its
        // own slot assignments
        Dictionary<string, string[]> generatedCache = new Dictionary<string, string[]>();
        try
        {
            for (int i = 0; i < materials.Length; i++)
            {
                EditorUtility.DisplayProgressBar("Somnia Fracta",
                    "Synthesizing maps - " + materials[i].name, (float)i / materials.Length);
                GenerateForMaterial(materials[i], generatedCache);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
        }
    }

    static Texture FindSlot(Material mat, string[] properties)
    {
        foreach (string property in properties)
        {
            if (mat.HasProperty(property))
            {
                Texture tex = mat.GetTexture(property);
                if (tex != null)
                    return tex;
            }
        }
        return null;
    }

    // Marked textures' imported assets are already stylized bakes, so the
    // synthesis reads the original file whenever it can be decoded; the
    // imported asset is only a fallback
    static Texture2D TryLoadRaw(string path, bool linear, TextureWrapMode wrapMode)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
            return null;

        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        if (!ImageConversion.LoadImage(tex, System.IO.File.ReadAllBytes(path)))
        {
            Object.DestroyImmediate(tex);
            return null;
        }
        tex.wrapMode = wrapMode;
        return tex;
    }

    static void GenerateForMaterial(Material mat, Dictionary<string, string[]> generatedCache)
    {
        Texture2D albedo = (FindSlot(mat, albedoProperties) ?? mat.mainTexture) as Texture2D;
        if (albedo == null)
        {
            Debug.LogWarning("Somnia Fracta: no albedo texture on material, skipped: " + mat.name);
            return;
        }

        string albedoPath = AssetDatabase.GetAssetPath(albedo);
        if (string.IsNullOrEmpty(albedoPath))
        {
            Debug.LogWarning("Somnia Fracta: albedo has no asset path, skipped: " + mat.name);
            return;
        }

        string[] outputs;
        if (!generatedCache.TryGetValue(albedoPath, out outputs))
        {
            outputs = SynthesizeMaps(mat, albedo, albedoPath);
            if (outputs == null)
                return;
            generatedCache[albedoPath] = outputs;
        }

        AssignMaps(mat, outputs);
    }

    static string[] SynthesizeMaps(Material mat, Texture2D albedo, string albedoPath)
    {
        TextureImporter albedoImporter = AssetImporter.GetAtPath(albedoPath) as TextureImporter;
        bool wraps = albedoImporter == null || albedoImporter.wrapMode == TextureWrapMode.Repeat;
        TextureWrapMode wrapMode = wraps ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
        bool linearProject = PlayerSettings.colorSpace == ColorSpace.Linear;

        Texture2D rawAlbedo = TryLoadRaw(albedoPath,
            albedoImporter != null && !albedoImporter.sRGBTexture, wrapMode);
        Texture2D chainAlbedo = rawAlbedo != null ? rawAlbedo : albedo;

        Texture2D normal = FindSlot(mat, normalProperties) as Texture2D;
        Texture2D rawNormal = null;
        if (normal != null)
        {
            string normalPath = AssetDatabase.GetAssetPath(normal);
            if (!string.IsNullOrEmpty(normalPath))
                rawNormal = TryLoadRaw(normalPath, true, wrapMode);
        }
        Texture2D chainNormal = rawNormal != null ? rawNormal : normal;

        int w = chainAlbedo.width;
        int h = chainAlbedo.height;
        int mipCount = chainAlbedo.mipmapCount;

        // The flat fallback is never sampled: _HasNormal gates every read
        materialSynth.SetTexture("_NormalTex",
            chainNormal != null ? (Texture)chainNormal : Texture2D.linearGrayTexture);
        materialSynth.SetFloat("_HasNormal", chainNormal != null ? 1.0f : 0.0f);
        materialSynth.SetVector("_NormalSize", chainNormal != null
            ? new Vector4(chainNormal.width, chainNormal.height, 0.0f, 0.0f)
            : new Vector4(w, h, 0.0f, 0.0f));
        materialSynth.SetFloat("_SpecFloor", synthSpecFloor);
        materialSynth.SetFloat("_SpecCeil", synthSpecCeil);
        materialSynth.SetFloat("_SpecCurve", synthSpecCurve);
        materialSynth.SetFloat("_SpecTint", synthSpecTint);
        materialSynth.SetFloat("_SmoothBase", synthSmoothBase);
        materialSynth.SetFloat("_SmoothLuma", synthSmoothLuma);
        materialSynth.SetFloat("_SmoothDetail", synthSmoothDetail);
        materialSynth.SetFloat("_SmoothFlat", synthSmoothFlat);
        materialSynth.SetFloat("_MetalLumaLo", synthMetalLumaLo);
        materialSynth.SetFloat("_MetalLumaHi", synthMetalLumaHi);
        materialSynth.SetFloat("_MetalSatReject", synthMetalSatReject);
        materialSynth.SetFloat("_MetalAmount", synthMetalAmount);
        materialSynth.SetFloat("_CavityGain", synthCavityGain);
        materialSynth.SetFloat("_CavitySpec", synthCavitySpec);
        materialSynth.SetFloat("_CavitySmooth", synthCavitySmooth);
        materialSynth.SetFloat("_OccCavity", synthOccCavity);
        materialSynth.SetFloat("_OccShade", synthOccShade);
        materialSynth.SetFloat("_OccFloor", synthOccFloor);

        RenderTexture RTActive = RenderTexture.active;
        RenderTexture synthRT = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBFloat);
        synthRT.wrapMode = wrapMode;

        string dir = System.IO.Path.GetDirectoryName(albedoPath).Replace('\\', '/') +
            "/" + NKLIAssetStylizer.generatedDirName;
        System.IO.Directory.CreateDirectory(dir);
        string baseName = System.IO.Path.GetFileNameWithoutExtension(albedoPath);

        string specPath = dir + "/" + baseName + "_specular.png";
        string metalPath = dir + "/" + baseName + "_metallic.png";
        string occPath = dir + "/" + baseName + "_occlusion.png";

        // The Julia seed comes from the albedo's own folder, so the crystal
        // mask and facet lattice land in step with the albedo's bake
        bool ok =
            BakeStylized(chainAlbedo, 0, synthRT, w, h, mipCount, wraps, linearProject, albedoPath, specPath) &&
            BakeStylized(chainAlbedo, 1, synthRT, w, h, mipCount, wraps, false, albedoPath, metalPath) &&
            BakePristine(chainAlbedo, 2, synthRT, w, h, occPath);

        RenderTexture.ReleaseTemporary(synthRT);
        RenderTexture.active = RTActive;
        if (rawAlbedo != null)
            Object.DestroyImmediate(rawAlbedo);
        if (rawNormal != null)
            Object.DestroyImmediate(rawNormal);

        if (!ok)
            return null;

        ImportGenerated(specPath, true, wrapMode);
        ImportGenerated(metalPath, false, wrapMode);
        ImportGenerated(occPath, false, wrapMode);

        Debug.Log("Somnia Fracta: generated " + specPath + ", " + metalPath + ", " + occPath);
        return new string[] { specPath, metalPath, occPath };
    }

    static bool BakeStylized(Texture source, int pass, RenderTexture synthRT,
        int w, int h, int mipCount, bool wraps, bool srgbEncode, string seedPath, string outputPath)
    {
        Graphics.Blit(source, synthRT, materialSynth, pass);

        RenderTexture dest = RenderTexture.GetTemporary(NKLITextureProcessor.ChainDescriptor(w, h, mipCount));
        bool ok = NKLITextureProcessor.RenderStylized(synthRT, dest, w, h, mipCount,
            false, true, wraps, srgbEncode, seedPath);
        if (ok)
            ok = SavePng(dest, w, h, srgbEncode, outputPath);
        else
            Debug.LogWarning("Somnia Fracta: effect shaders unavailable; map not generated: " + outputPath);
        RenderTexture.ReleaseTemporary(dest);
        return ok;
    }

    // Occlusion maps import pristine, so this bake takes no stylization; the
    // synthesis pass writes mirrored to pair with the top-down readback
    static bool BakePristine(Texture source, int pass, RenderTexture synthRT,
        int w, int h, string outputPath)
    {
        Graphics.Blit(source, synthRT, materialSynth, pass);
        return SavePng(synthRT, w, h, false, outputPath);
    }

    static bool SavePng(RenderTexture rt, int w, int h, bool srgbBytes, string outputPath)
    {
        NativeArray<byte> arr = new NativeArray<byte>(w * h * 4, Allocator.Persistent);
        AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref arr, rt, 0, TextureFormat.RGBA32);
        request.WaitForCompletion();
        bool ok = !request.hasError;
        if (ok)
        {
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgbBytes)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.LoadRawTextureData(arr);
            tex.Apply(false);
            System.IO.File.WriteAllBytes(outputPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
        else
            Debug.LogWarning("Somnia Fracta: GPU readback failed; map not generated: " + outputPath);
        arr.Dispose();
        return ok;
    }

    static void ImportGenerated(string path, bool srgb, TextureWrapMode wrapMode)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
            return;

        if (importer.sRGBTexture != srgb || importer.wrapMode != wrapMode)
        {
            importer.sRGBTexture = srgb;
            importer.wrapMode = wrapMode;
            importer.SaveAndReimport();
        }
    }

    // Keywords cover the built-in Standard family and the URP/HDRP Lit
    // shaders; enabling one a shader never declares is inert
    static void AssignMaps(Material mat, string[] outputs)
    {
        Texture2D spec = AssetDatabase.LoadAssetAtPath<Texture2D>(outputs[0]);
        Texture2D metal = AssetDatabase.LoadAssetAtPath<Texture2D>(outputs[1]);
        Texture2D occ = AssetDatabase.LoadAssetAtPath<Texture2D>(outputs[2]);

        Undo.RecordObject(mat, "Somnia Fracta map assignment");
        bool assigned = false;
        if (spec != null && mat.HasProperty("_SpecGlossMap"))
        {
            mat.SetTexture("_SpecGlossMap", spec);
            mat.EnableKeyword("_SPECGLOSSMAP");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            assigned = true;
        }
        if (metal != null && mat.HasProperty("_MetallicGlossMap"))
        {
            mat.SetTexture("_MetallicGlossMap", metal);
            mat.EnableKeyword("_METALLICGLOSSMAP");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            assigned = true;
        }
        if (occ != null && mat.HasProperty("_OcclusionMap"))
        {
            mat.SetTexture("_OcclusionMap", occ);
            mat.EnableKeyword("_OCCLUSIONMAP");
            assigned = true;
        }

        if (assigned)
            EditorUtility.SetDirty(mat);
        else
            Debug.LogWarning("Somnia Fracta: material has no spec/metallic/occlusion slots to fill: " + mat.name);
    }
}
