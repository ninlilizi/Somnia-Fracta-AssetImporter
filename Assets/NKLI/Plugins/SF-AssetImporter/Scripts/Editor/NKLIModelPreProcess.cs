using UnityEditor;

public class NKLIModelPreProcess : AssetPostprocessor
{
    void OnPreprocessAsset()
    {
        if (assetImporter.importSettingsMissing)
        {
            if (assetImporter.SupportsRemappedAssetType(typeof(ModelImporter)))
            {
                ModelImporter modelImporter = (ModelImporter)assetImporter;
                if (modelImporter != null)
                {
                    if (assetPath.Contains("NKLI")) // Apply this to everything under the NKLI subtree
                    {
                        // Calculate normals on import to avoid flipped normal problem with vertex baked prefabs
                        modelImporter.importNormals = ModelImporterNormals.Calculate;
                        // To be safe we need lightmap UVs
                        modelImporter.generateSecondaryUV = true;
                    }
                }
            }
        }
    }
}