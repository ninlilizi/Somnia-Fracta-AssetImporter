using UnityEditor;

public class NKLIModelPreProcess : AssetPostprocessor
{
    void OnPreprocessAsset()
    {
        if (!assetImporter.importSettingsMissing)
            return;

        ModelImporter modelImporter = assetImporter as ModelImporter;
        if (modelImporter == null || !assetPath.Contains("NKLI"))
            return;

        // Calculate normals on import to avoid flipped normal problem with vertex baked prefabs
        modelImporter.importNormals = ModelImporterNormals.Calculate;
        // To be safe we need lightmap UVs
        modelImporter.generateSecondaryUV = true;
    }
}
