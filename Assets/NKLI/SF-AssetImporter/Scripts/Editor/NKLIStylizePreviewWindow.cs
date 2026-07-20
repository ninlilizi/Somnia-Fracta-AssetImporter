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

    [MenuItem("NKLI/Bulk Stylize Assets/Somnia Fracta - Preview")]
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
    }

    void OnGUI()
    {
        EditorGUI.BeginChangeCheck();
        sourceTex = (Texture2D)EditorGUILayout.ObjectField("Texture", sourceTex, typeof(Texture2D), false);
        bool selectionChanged = EditorGUI.EndChangeCheck();

        EditorGUILayout.BeginHorizontal();
        bool refresh = GUILayout.Button("Refresh", GUILayout.Width(90));
        zoom = EditorGUILayout.Slider("Zoom", zoom, 0.125f, 4.0f);
        EditorGUILayout.EndHorizontal();

        if ((selectionChanged || refresh) && sourceTex != null)
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

        if (resultTex != null)
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

        if (NKLIAssetStylizer.IsOcclusion(path) || NKLIAssetStylizer.IsNameExcluded(path) ||
            NKLIAssetStylizer.IsExtensionExcluded(path))
        {
            info = "This texture imports pristine (occlusion / name / file-type exclusion).";
            return;
        }

        bool isNormalMap = importer.textureType == TextureImporterType.NormalMap;
        bool isSpecMetallic = !isNormalMap && NKLIAssetStylizer.IsSpecMetallic(path);
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

        if (rawTex == null)
            info = "Previewing the imported asset: for marked textures this is already a stylized bake, so the effect shows doubled.";
        if (isSpecMetallic)
            info += " Classified spec/metallic: facet shimmer only.";

        int w = chainSource.width;
        int h = chainSource.height;
        RenderTexture dest = RenderTexture.GetTemporary(
            NKLITextureProcessor.ChainDescriptor(w, h, chainSource.mipmapCount));

        if (!NKLITextureProcessor.RenderStylized(chainSource, dest, w, h, chainSource.mipmapCount,
            isNormalMap, isSpecMetallic, importer.wrapMode == TextureWrapMode.Repeat, srgbEncode, path))
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
}
