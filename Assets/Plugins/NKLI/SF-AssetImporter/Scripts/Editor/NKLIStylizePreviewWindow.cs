using Unity.Collections;

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Side-by-side tuning preview: renders the current stylization chain on a
// chosen texture without committing anything to the asset database. Adjust
// the constants in NKLITextureProcessor, let the domain reload, press Refresh
public class NKLIStylizePreviewWindow : EditorWindow
{
    Texture2D sourceTex;
    Texture2D rawTex;
    Texture2D resultTex;
    float zoom = 1.0f;
    Vector2 scroll;
    string info = "";

    // Class alignment test: bake the same source through all four class
    // paths in one session and vote each lattice against the colour
    // reference, straight versus row-flipped
    bool classTest;
    Texture2D[] classTex;
    static readonly string[] classNames = { "Colour", "Spec/Metallic", "Occlusion", "Normal" };

    [MenuItem("Tools/NKLI/Bulk Stylize Assets/Somnia Fracta - Preview")]
    static void Open()
    {
        GetWindow<NKLIStylizePreviewWindow>("Somnia Fracta");
    }

    void OnEnable()
    {
        if (sourceTex == null && Selection.activeObject is Texture2D selected)
            sourceTex = selected;
        if (sourceTex != null)
            Render();
    }

    void OnDisable()
    {
        DestroyResult();
    }

    void DestroyResult()
    {
        if (resultTex != null)
            DestroyImmediate(resultTex);
        resultTex = null;
        if (rawTex != null)
            DestroyImmediate(rawTex);
        rawTex = null;
        if (classTex != null)
        {
            foreach (Texture2D tex in classTex)
                if (tex != null)
                    DestroyImmediate(tex);
            classTex = null;
        }
    }

    void OnGUI()
    {
        EditorGUI.BeginChangeCheck();
        sourceTex = (Texture2D)EditorGUILayout.ObjectField("Texture", sourceTex, typeof(Texture2D), false);
        bool selectionChanged = EditorGUI.EndChangeCheck();

        EditorGUILayout.BeginHorizontal();
        bool refresh = GUILayout.Button("Refresh", GUILayout.Width(90));
        EditorGUI.BeginChangeCheck();
        classTest = EditorGUILayout.ToggleLeft("Class alignment test", classTest, GUILayout.Width(160));
        bool modeChanged = EditorGUI.EndChangeCheck();
        zoom = EditorGUILayout.Slider("Zoom", zoom, 0.125f, 4.0f);
        EditorGUILayout.EndHorizontal();

        if ((selectionChanged || refresh || modeChanged) && sourceTex != null)
            Render();

        if (!string.IsNullOrEmpty(info))
            EditorGUILayout.HelpBox(info, MessageType.Info);

        if (sourceTex == null)
            return;

        float w = sourceTex.width * zoom;
        float h = sourceTex.height * zoom;

        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.BeginHorizontal();

        EditorGUILayout.BeginVertical(GUILayout.Width(w));
        GUILayout.Label(rawTex != null ? "Source (file)" : "Source (imported asset)");
        Rect rs = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
        GUI.DrawTexture(rs, rawTex != null ? rawTex : (Texture)sourceTex, ScaleMode.StretchToFill);
        EditorGUILayout.EndVertical();

        if (classTest && classTex != null)
        {
            for (int c = 0; c < classTex.Length; c++)
            {
                if (classTex[c] == null)
                    continue;
                EditorGUILayout.BeginVertical(GUILayout.Width(w));
                GUILayout.Label(classNames[c]);
                Rect rc = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
                GUI.DrawTexture(rc, classTex[c], ScaleMode.StretchToFill);
                EditorGUILayout.EndVertical();
            }
        }
        else if (resultTex != null)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(w));
            GUILayout.Label("Stylized");
            Rect rr = GUILayoutUtility.GetRect(w, h, GUILayout.Width(w), GUILayout.Height(h));
            GUI.DrawTexture(rr, resultTex, ScaleMode.StretchToFill);
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.EndScrollView();
    }

    void Render()
    {
        DestroyResult();
        info = "";

        string path = AssetDatabase.GetAssetPath(sourceTex);
        if (string.IsNullOrEmpty(path))
        {
            info = "Not an asset texture.";
            return;
        }

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            info = "No texture importer for this asset.";
            return;
        }

        bool isOcclusionRole = NKLIAssetStylizer.IsOcclusion(path);
        if ((NKLIAssetStylizer.IsNameExcluded(path) && !isOcclusionRole) ||
            NKLIAssetStylizer.IsExtensionExcluded(path) || NKLIAssetStylizer.IsGeneratedOutput(path))
        {
            info = "This texture imports pristine (name / file-type / synthesized-output exclusion).";
            return;
        }

        bool isNormalMap = importer.textureType == TextureImporterType.NormalMap;
        bool isOcclusion = !isNormalMap && isOcclusionRole;
        bool isSpecMetallic = !isNormalMap && !isOcclusion && NKLIAssetStylizer.IsSpecMetallic(path);
        bool srgbEncode = !isNormalMap && importer.sRGBTexture && PlayerSettings.colorSpace == ColorSpace.Linear;

        // Marked textures' imported assets are ALREADY stylized bakes, so
        // preview from the original source file whenever it can be decoded;
        // the asset is only a fallback
        Texture2D chainSource = sourceTex;
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
        {
            rawTex = new Texture2D(2, 2, TextureFormat.RGBA32, true, isNormalMap || !importer.sRGBTexture)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            if (ImageConversion.LoadImage(rawTex, System.IO.File.ReadAllBytes(path)))
                chainSource = rawTex;
            else
            {
                DestroyImmediate(rawTex);
                rawTex = null;
            }
        }
        else if (ext == ".tga")
        {
            rawTex = NKLIMapSynthesizer.LoadTga(System.IO.File.ReadAllBytes(path),
                isNormalMap || !importer.sRGBTexture,
                importer.wrapMode == TextureWrapMode.Repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp);
            if (rawTex != null)
                chainSource = rawTex;
        }

        if (rawTex == null)
            info = "Previewing the imported asset: for marked textures this is already a stylized bake, so the effect shows doubled.";
        if (isSpecMetallic)
            info += " Classified spec/metallic: facet shimmer only.";
        if (isOcclusion)
            info += " Classified occlusion: paint and facets, no grade.";

        if (classTest)
        {
            RenderClasses(chainSource, importer, path);
            return;
        }

        int w = chainSource.width;
        int h = chainSource.height;
        RenderTexture dest = RenderTexture.GetTemporary(
            NKLITextureProcessor.ChainDescriptor(w, h, chainSource.mipmapCount));

        if (!NKLITextureProcessor.RenderStylized(chainSource, dest, w, h, chainSource.mipmapCount,
            isNormalMap, isSpecMetallic, isOcclusion, importer.wrapMode == TextureWrapMode.Repeat, srgbEncode, path))
        {
            RenderTexture.ReleaseTemporary(dest);
            info = "Effect shaders unavailable.";
            return;
        }

        NativeArray<byte> arr = new NativeArray<byte>(w * h * 4, Allocator.Persistent);
        AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref arr, dest, 0, TextureFormat.RGBA32);
        request.WaitForCompletion();
        if (!request.hasError)
        {
            // The gamma pass already encoded sRGB textures, so flag the
            // preview texture to match and the GUI displays it faithfully
            resultTex = new Texture2D(w, h, TextureFormat.RGBA32, false, !srgbEncode)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            resultTex.LoadRawTextureData(arr);
            resultTex.Apply(false);
        }
        else
            info = "GPU readback failed; press Refresh to retry.";

        arr.Dispose();
        RenderTexture.ReleaseTemporary(dest);
    }

    // Bakes the same source through all four class paths in one session and
    // votes each lattice against the colour reference by mean-centred
    // correlation, straight versus row-flipped - a mirrored class names
    // itself regardless of how its content differs
    void RenderClasses(Texture2D chainSource, TextureImporter importer, string path)
    {
        int w = chainSource.width;
        int h = chainSource.height;
        bool wraps = importer.wrapMode == TextureWrapMode.Repeat;
        classTex = new Texture2D[4];

        for (int c = 0; c < 4; c++)
        {
            bool isNormal = c == 3;
            bool isSpecMet = c == 1;
            bool isOcc = c == 2;

            RenderTexture dest = RenderTexture.GetTemporary(
                NKLITextureProcessor.ChainDescriptor(w, h, chainSource.mipmapCount));
            if (!NKLITextureProcessor.RenderStylized(chainSource, dest, w, h, chainSource.mipmapCount,
                isNormal, isSpecMet, isOcc, wraps, false, path))
            {
                RenderTexture.ReleaseTemporary(dest);
                info = "Effect shaders unavailable.";
                return;
            }

            NativeArray<byte> arr = new NativeArray<byte>(w * h * 4, Allocator.Persistent);
            AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref arr, dest, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (!request.hasError)
            {
                classTex[c] = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                classTex[c].LoadRawTextureData(arr);
                classTex[c].Apply(false);
            }
            arr.Dispose();
            RenderTexture.ReleaseTemporary(dest);

            if (classTex[c] == null)
            {
                info = "GPU readback failed; press Refresh to retry.";
                return;
            }
        }

        info = ClassVerdicts(w, h);
    }

    // The lattice signal per class: luminance for the scalar classes, the
    // green slope channel for normals - all four ride the shared per-facet
    // lean, so aligned bakes correlate positively with the colour reference
    static double Channel(Color32 c, int cls)
    {
        return cls == 3 ? c.g : 0.299 * c.r + 0.587 * c.g + 0.114 * c.b;
    }

    string ClassVerdicts(int w, int h)
    {
        Color32[][] px = new Color32[4][];
        double[] mean = new double[4];
        for (int c = 0; c < 4; c++)
        {
            px[c] = classTex[c].GetPixels32();
            double sum = 0.0;
            int samples = 0;
            for (int p = 0; p < px[c].Length; p += 7)
            {
                sum += Channel(px[c][p], c);
                samples++;
            }
            mean[c] = sum / samples;
        }

        System.Text.StringBuilder verdicts = new System.Text.StringBuilder("Lattice vote vs Colour:");
        for (int c = 1; c < 4; c++)
        {
            double straight = 0.0;
            double flipped = 0.0;
            for (int p = 0; p < w * h; p += 7)
            {
                int row = p / w;
                int pf = (h - 1 - row) * w + (p - row * w);
                double reference = Channel(px[0][p], 0) - mean[0];
                straight += reference * (Channel(px[c][p], c) - mean[c]);
                flipped += reference * (Channel(px[c][pf], c) - mean[c]);
            }

            double gap = System.Math.Abs(straight - flipped);
            double scale = System.Math.Max(System.Math.Abs(straight), System.Math.Abs(flipped));
            string state = scale <= 0.0 || gap < scale * 0.15
                ? "INDECISIVE"
                : (straight > flipped ? "aligned" : "MIRRORED");
            verdicts.Append("  " + classNames[c] + ": " + state +
                " (straight " + straight.ToString("0.##e0") + " vs flipped " + flipped.ToString("0.##e0") + ")");
        }
        return verdicts.ToString();
    }
}
