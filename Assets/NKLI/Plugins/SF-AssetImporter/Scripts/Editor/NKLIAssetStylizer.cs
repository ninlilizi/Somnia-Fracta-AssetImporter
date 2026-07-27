using System;
using System.Collections;
using System.Collections.Generic;

using UnityEditor;
using UnityEngine;

public class NKLIAssetStylizer : MonoBehaviour
{
    // This string when found in the asset path will mark the asset for stylization
    public const string targetString = "-style-sf";

    // Textures whose file name carries any of these tokens import untouched
    public static readonly string[] excludedNameTokens = { "Mask", "AO" };

    // Suffix conventions for occlusion/mask maps (e.g. prop_barrel_01_o);
    // matched case-insensitively against the end of the file name, where even
    // a short token is unambiguous
    public static readonly string[] excludedNameSuffixes = { "_o", "_ao", "_occ", "_occlusion", "_mask" };

    // File types never stylized: .exr and .hdr carry skybox/HDR data rather
    // than scene-geometry texture, .fbx textures are embedded in models
    static readonly string[] excludedExtensions = { ".exr", ".hdr", ".fbx" };

    public static bool IsExtensionExcluded(string path)
    {
        foreach (string ext in excludedExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    const string progressTitle = "Somnia Fracta";

    // Custom dependency binding marked textures to the stylization settings, so
    // changed settings invalidate stale artifacts
    public const string dependencyName = "NKLI/SomniaFracta";

    const string dependencyHashFile = "Library/NKLI-SomniaFracta.dep";
    const string specMetCacheFile = "Library/NKLI-SomniaFracta-specmet.txt";
    const string stylizedLedgerFile = "Library/NKLI-SomniaFracta-stylized.txt";

    // Material texture slots treated as specular/metallic maps
    static readonly string[] specMetProperties = { "_MetallicGlossMap", "_SpecGlossMap" };

    // Material texture slots treated as occlusion maps; these import untouched
    static readonly string[] occlusionProperties = { "_OcclusionMap" };

    // Classification database: material path -> texture guids occupying its
    // spec/metallic and occlusion slots, plus the union of each class and
    // tombstones for textures that lost every role. Maintained live by material
    // imports and rebuilt from scratch by each bulk run.
    static readonly Dictionary<string, HashSet<string>> materialAssignments = new Dictionary<string, HashSet<string>>();
    static readonly Dictionary<string, HashSet<string>> materialOccAssignments = new Dictionary<string, HashSet<string>>();
    static readonly HashSet<string> classifiedTextures = new HashSet<string>();
    static readonly HashSet<string> occlusionTextures = new HashSet<string>();
    static readonly HashSet<string> classTombstones = new HashSet<string>();
    static bool registrationScheduled;

    public static bool IsSpecMetallic(string path)
    {
        return classifiedTextures.Contains(AssetDatabase.AssetPathToGUID(path));
    }

    public static bool IsOcclusion(string path)
    {
        return occlusionTextures.Contains(AssetDatabase.AssetPathToGUID(path));
    }

    // Would this texture import untouched? Occlusion and name-excluded roles
    // pass through pristine, as do importer types outside Default/NormalMap
    static bool ImportsPristine(string path)
    {
        if (IsNameExcluded(path) || IsOcclusion(path))
            return true;

        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
        return importer == null ||
            (importer.textureType != TextureImporterType.Default &&
             importer.textureType != TextureImporterType.NormalMap);
    }

    // Contains-tokens are case-sensitive, so "AO" cannot ambush the "ao"
    // inside ordinary words; suffix-tokens are anchored to the name's end and
    // so can afford to ignore case
    public static bool IsNameExcluded(string path)
    {
        string fileName = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (string token in excludedNameTokens)
            if (fileName.IndexOf(token, StringComparison.Ordinal) != -1)
                return true;
        foreach (string suffix in excludedNameSuffixes)
            if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Per-texture dependency, so a classification change invalidates only the
    // textures whose role changed
    public static string ClassDependencyName(string guid)
    {
        return "NKLI/SF-Class/" + guid;
    }

    // True while a bulk run is underway; governs native array lifetime only.
    // Stylization itself is unconditional for marked textures, keeping imports
    // deterministic so the artifact database never sees two results for one input.
    public static bool bulkRunActive;

    // Re-register the last run's fingerprint and every classification
    // dependency after each domain reload, keeping hashes stable across sessions
    [InitializeOnLoadMethod]
    static void RestoreDependency()
    {
        try
        {
            if (System.IO.File.Exists(dependencyHashFile))
                AssetDatabase.RegisterCustomDependency(dependencyName,
                    Hash128.Parse(System.IO.File.ReadAllText(dependencyHashFile)));

            LoadClassificationDb();
            LoadStylizedLedger();
            RegisterClassDependencies();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    static void RegisterRunDependency()
    {
        Hash128 hash = Hash128.Compute(NKLITextureProcessor.EffectFingerprint());
        AssetDatabase.RegisterCustomDependency(dependencyName, hash);
        System.IO.File.WriteAllText(dependencyHashFile, hash.ToString());
    }

    // Ledger of textures whose committed artifact is stylized, and under which
    // settings fingerprint, so incremental bulk runs pass over finished work.
    // Kept in Library/ deliberately: a Library rebuild that voids every
    // artifact voids the ledger with it
    static readonly Dictionary<string, string> stylizedLedger = new Dictionary<string, string>();
    static readonly HashSet<string> ledgerTouchedThisBatch = new HashSet<string>();

    static string CurrentFingerprintHash()
    {
        return Hash128.Compute(NKLITextureProcessor.EffectFingerprint()).ToString();
    }

    static void LoadStylizedLedger()
    {
        stylizedLedger.Clear();
        if (!System.IO.File.Exists(stylizedLedgerFile))
            return;

        foreach (string line in System.IO.File.ReadAllLines(stylizedLedgerFile))
        {
            string[] parts = line.Split('|');
            if (parts.Length == 2)
                stylizedLedger[parts[0]] = parts[1];
        }
    }

    static void SaveStylizedLedger()
    {
        List<string> lines = new List<string>();
        foreach (KeyValuePair<string, string> entry in stylizedLedger)
            lines.Add(entry.Key + "|" + entry.Value);
        lines.Sort(StringComparer.Ordinal);
        System.IO.File.WriteAllLines(stylizedLedgerFile, lines.ToArray());
    }

    // Both recorders are called by the texture postprocessor on every import of
    // a marked texture, so the ledger tracks the committed artifact exactly.
    // Worker processes must not write: their ledger copy would race the main
    // editor's file; ReconcileLedger settles their imports instead
    public static void RecordStylized(string assetPath)
    {
        if (AssetDatabase.IsAssetImportWorkerProcess())
            return;

        ledgerTouchedThisBatch.Add(assetPath);
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid))
            return;

        string hash = CurrentFingerprintHash();
        string existing;
        if (stylizedLedger.TryGetValue(guid, out existing) && existing == hash)
            return;
        stylizedLedger[guid] = hash;
        SaveStylizedLedger();
    }

    public static void RecordUnstylized(string assetPath)
    {
        if (AssetDatabase.IsAssetImportWorkerProcess())
            return;

        ledgerTouchedThisBatch.Add(assetPath);
        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid))
            return;

        if (stylizedLedger.Remove(guid))
            SaveStylizedLedger();
    }

    // Imports that ran in a parallel import worker never pass through the main
    // editor's texture postprocessor, and workers always decline stylization —
    // so any marked texture imported this batch without touching the ledger
    // came back unstylized and forfeits its entry
    public static void ReconcileLedger(string[] imported)
    {
        if (AssetDatabase.IsAssetImportWorkerProcess())
            return;

        bool changed = false;
        foreach (string path in imported)
        {
            if (path.ToLower().IndexOf(targetString) == -1 || ledgerTouchedThisBatch.Contains(path))
                continue;

            string guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid))
                changed |= stylizedLedger.Remove(guid);
        }
        ledgerTouchedThisBatch.Clear();

        if (changed)
            SaveStylizedLedger();
    }

    // A missing fingerprint file means a fresh Library (deleted, or first run):
    // the rebuild imported everything unstylized, so offer the bulk ritual once
    // the editor settles
    [InitializeOnLoadMethod]
    static void DetectFreshLibrary()
    {
        if (System.IO.File.Exists(dependencyHashFile))
            return;

        if (SessionState.GetBool("NKLI.SomniaFracta.RebuildPrompted", false))
            return;
        SessionState.SetBool("NKLI.SomniaFracta.RebuildPrompted", true);

        EditorApplication.delayCall += () =>
        {
            if (EditorUtility.DisplayDialog("Somnia Fracta",
                "The Library appears freshly rebuilt, so marked textures have imported unstylized.\n\nRun bulk stylization now?",
                "Stylize", "Later"))
                DoStylize();
        };
    }

    static void LoadClassificationDb()
    {
        materialAssignments.Clear();
        materialOccAssignments.Clear();
        classifiedTextures.Clear();
        occlusionTextures.Clear();
        classTombstones.Clear();

        if (!System.IO.File.Exists(specMetCacheFile))
            return;

        foreach (string line in System.IO.File.ReadAllLines(specMetCacheFile))
        {
            string[] parts = line.Split('|');
            if (parts.Length == 3 && (parts[0] == "M" || parts[0] == "O"))
            {
                Dictionary<string, HashSet<string>> db = parts[0] == "M" ? materialAssignments : materialOccAssignments;
                HashSet<string> set;
                if (!db.TryGetValue(parts[1], out set))
                    db[parts[1]] = set = new HashSet<string>();
                set.Add(parts[2]);
                (parts[0] == "M" ? classifiedTextures : occlusionTextures).Add(parts[2]);
            }
            else if (parts.Length == 2 && parts[0] == "T")
                classTombstones.Add(parts[1]);
        }

        classifiedTextures.ExceptWith(occlusionTextures);
    }

    static void SaveClassificationDb()
    {
        List<string> lines = new List<string>();
        foreach (KeyValuePair<string, HashSet<string>> entry in materialAssignments)
            foreach (string guid in entry.Value)
                lines.Add("M|" + entry.Key + "|" + guid);
        foreach (KeyValuePair<string, HashSet<string>> entry in materialOccAssignments)
            foreach (string guid in entry.Value)
                lines.Add("O|" + entry.Key + "|" + guid);
        foreach (string guid in classTombstones)
            lines.Add("T|" + guid);
        lines.Sort(StringComparer.Ordinal);
        System.IO.File.WriteAllLines(specMetCacheFile, lines.ToArray());
    }

    // Recompute the unions; textures that fell out of every slot become
    // tombstones so their dependency can announce the demotion
    static void RebuildClassification()
    {
        HashSet<string> previous = new HashSet<string>(classifiedTextures);
        previous.UnionWith(occlusionTextures);

        classifiedTextures.Clear();
        foreach (KeyValuePair<string, HashSet<string>> entry in materialAssignments)
            classifiedTextures.UnionWith(entry.Value);

        occlusionTextures.Clear();
        foreach (KeyValuePair<string, HashSet<string>> entry in materialOccAssignments)
            occlusionTextures.UnionWith(entry.Value);

        // A texture claimed by both roles imports pure; occlusion is the
        // conservative verdict
        classifiedTextures.ExceptWith(occlusionTextures);

        foreach (string guid in previous)
            if (!classifiedTextures.Contains(guid) && !occlusionTextures.Contains(guid))
                classTombstones.Add(guid);
        classTombstones.ExceptWith(classifiedTextures);
        classTombstones.ExceptWith(occlusionTextures);
    }

    static void RegisterClassDependencies()
    {
        foreach (string guid in classifiedTextures)
            AssetDatabase.RegisterCustomDependency(ClassDependencyName(guid), Hash128.Compute("1"));
        foreach (string guid in occlusionTextures)
            AssetDatabase.RegisterCustomDependency(ClassDependencyName(guid), Hash128.Compute("AO"));
        foreach (string guid in classTombstones)
            AssetDatabase.RegisterCustomDependency(ClassDependencyName(guid), Hash128.Compute("0"));
    }

    static HashSet<string> ReadSlots(Material mat, string[] properties)
    {
        HashSet<string> result = new HashSet<string>();
        foreach (string property in properties)
        {
            if (mat.HasProperty(property))
            {
                Texture tex = mat.GetTexture(property);
                if (tex != null)
                {
                    string texPath = AssetDatabase.GetAssetPath(tex);
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        string guid = AssetDatabase.AssetPathToGUID(texPath);
                        if (!string.IsNullOrEmpty(guid))
                            result.Add(guid);
                    }
                }
            }
        }
        return result;
    }

    static bool UpdateAssignment(Dictionary<string, HashSet<string>> db, string key, HashSet<string> slots)
    {
        HashSet<string> existing;
        if (db.TryGetValue(key, out existing))
        {
            if (existing.SetEquals(slots))
                return false;
            if (slots.Count > 0)
                db[key] = slots;
            else
                db.Remove(key);
            return true;
        }
        if (slots.Count == 0)
            return false;
        db[key] = slots;
        return true;
    }

    // Live database upkeep, fed material changes by the asset postprocessor
    public static void OnMaterialAssetsChanged(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        bool changed = false;

        for (int i = 0; i < moved.Length; i++)
        {
            if (!moved[i].EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                continue;

            string oldKey = movedFrom[i].ToLower();
            string newKey = moved[i].ToLower();
            HashSet<string> set;
            if (materialAssignments.TryGetValue(oldKey, out set))
            {
                materialAssignments.Remove(oldKey);
                materialAssignments[newKey] = set;
                changed = true;
            }
            if (materialOccAssignments.TryGetValue(oldKey, out set))
            {
                materialOccAssignments.Remove(oldKey);
                materialOccAssignments[newKey] = set;
                changed = true;
            }
        }

        foreach (string path in deleted)
        {
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                continue;

            string key = path.ToLower();
            changed |= materialAssignments.Remove(key);
            changed |= materialOccAssignments.Remove(key);
        }

        foreach (string path in imported)
        {
            if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
                continue;

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
                continue;

            string key = path.ToLower();
            changed |= UpdateAssignment(materialAssignments, key, ReadSlots(mat, specMetProperties));
            changed |= UpdateAssignment(materialOccAssignments, key, ReadSlots(mat, occlusionProperties));
        }

        if (!changed)
            return;

        RebuildClassification();
        SaveClassificationDb();

        // Dependency registration is forbidden mid-import; defer it, then let a
        // refresh re-bake only the textures whose classification changed
        if (!registrationScheduled)
        {
            registrationScheduled = true;
            EditorApplication.delayCall += () =>
            {
                registrationScheduled = false;
                RegisterClassDependencies();
                AssetDatabase.Refresh();
            };
        }
    }


    // Minimal owner-less coroutine pump, replacing the Editor Coroutines package.
    // Advances the head routine one step per editor update; supports 'yield return null' only.
    static readonly Queue<IEnumerator> routines = new Queue<IEnumerator>();
    static bool pumping;

    static int progressIndex;
    static int progressCount;
    static string progressName;
    static string progressStage;
    static bool progressActive;
    static bool progressCancelled;
    static NKLIStylizeAbortWindow abortWindow;

    // Read by the abort window; RequestAbort is honoured at the same seams as
    // the progress bar's cancel button — after the texture in flight
    public static bool RunActive { get { return progressActive; } }
    public static float RunFraction { get { return progressCount > 0 ? (float)progressIndex / progressCount : 0.0f; } }
    public static string RunCounter { get { return progressIndex + " / " + progressCount; } }
    public static string RunStage { get { return progressStage; } }
    public static string RunName { get { return progressName; } }

    public static void RequestAbort()
    {
        progressCancelled = true;
    }

    static void StartRoutine(IEnumerator routine)
    {
        routines.Enqueue(routine);
        if (!pumping)
        {
            pumping = true;
            EditorApplication.update += Pump;
        }
    }

    static void Pump()
    {
        if (routines.Count > 0)
        {
            bool alive;
            try
            {
                alive = routines.Peek().MoveNext();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                alive = false;
                FinishRun("Stylization aborted by exception");
            }

            if (!alive)
                routines.Dequeue();
        }

        if (routines.Count == 0)
        {
            pumping = false;
            EditorApplication.update -= Pump;
        }
    }


    // Progress dialogue; returns true once the user has pressed cancel
    static bool ReportStage(string stage)
    {
        progressStage = stage;
        float progress = progressCount > 0 ? (float)progressIndex / progressCount : 0.0f;
        progressCancelled |= EditorUtility.DisplayCancelableProgressBar(
            progressTitle + " — " + stage,
            "(" + progressIndex + "/" + progressCount + ") " + progressName,
            progress);
        return progressCancelled;
    }

    // Called by NKLITextureProcessor to show the pass currently running on a texture
    public static void ReportSubStage(string subStage)
    {
        if (!progressActive)
            return;

        progressStage = "Stylizing — " + subStage;
        float progress = progressCount > 0 ? (float)progressIndex / progressCount : 0.0f;
        progressCancelled |= EditorUtility.DisplayCancelableProgressBar(
            progressTitle + " — Stylizing",
            "(" + progressIndex + "/" + progressCount + ") " + progressName + " — " + subStage,
            progress);
    }

    static void FinishRun(string message)
    {
        progressActive = false;
        bulkRunActive = false;
        EditorUtility.ClearProgressBar();
        if (abortWindow != null)
        {
            abortWindow.Close();
            abortWindow = null;
        }
        NKLITextureProcessorArrayStorage.ReleaseResources();
        Debug.Log(message);
    }


    // When false, the run consults the stylized ledger and touches only
    // textures whose artifact is missing or baked under stale settings
    static bool runForceAll;

    // Progress state must be live before the window opens, or its first OnGUI
    // sees a dormant run and retires itself at birth
    static void BeginRun(bool forceAll)
    {
        runForceAll = forceAll;
        bulkRunActive = true;
        progressActive = true;
        progressCancelled = false;
        progressStage = "Preparing";
        progressName = "";
        abortWindow = NKLIStylizeAbortWindow.Open();
        StartRoutine(ScanMaterials());
    }

    [MenuItem("NKLI/Bulk Stylize Assets/Somnia-Fracta - Apply")]
    public static void DoStylize()
    {
        BeginRun(false);
    }

    [MenuItem("NKLI/Bulk Stylize Assets/Somnia-Fracta - Re-Apply All")]
    public static void DoStylizeForce()
    {
        BeginRun(true);
    }


    // Rebuild the classification database from every material in the project —
    // a seeding and repair pass; day-to-day upkeep happens live as materials import
    // Only BeginRun clears the cancel flag: a phase clearing it on entry would
    // swallow an abort clicked at the boundary between two phases
    public static IEnumerator ScanMaterials()
    {
        progressActive = true;
        progressIndex = 0;
        progressCount = 0;
        progressName = "Searching asset database";
        ReportStage("Scanning materials");
        yield return null;

        materialAssignments.Clear();
        materialOccAssignments.Clear();
        string[] guids = AssetDatabase.FindAssets("t:Material");
        progressCount = guids.Length;
        for (int i = 0; i < guids.Length; i++)
        {
            string matPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            progressIndex = i + 1;
            progressName = matPath;

            if (ReportStage("Scanning materials"))
            {
                FinishRun("Stylization cancelled");
                yield break;
            }

            Material mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat != null)
            {
                HashSet<string> slots = ReadSlots(mat, specMetProperties);
                if (slots.Count > 0)
                    materialAssignments[matPath.ToLower()] = slots;

                HashSet<string> occSlots = ReadSlots(mat, occlusionProperties);
                if (occSlots.Count > 0)
                    materialOccAssignments[matPath.ToLower()] = occSlots;
            }

            if (i % 20 == 0)
                yield return null;
        }

        RebuildClassification();
        SaveClassificationDb();
        RegisterClassDependencies();

        Debug.Log("Specular/metallic textures identified: " + classifiedTextures.Count +
            "; occlusion textures identified: " + occlusionTextures.Count);
        RegisterRunDependency();
        StartRoutine(FindAssetsByType<Texture>());
    }


    public static IEnumerator TextureProcessor(List<string> textures)
    {
        yield return null;

        progressCount = textures.Count;
        for (int i = 0; i < textures.Count; i++)
        {
            string texture = textures[i];
            progressIndex = i + 1;
            progressName = texture;

            if (ReportStage("Stylizing"))
            {
                FinishRun("Stylization cancelled");
                yield break;
            }

            // ForceUpdate makes a bulk run an unconditional re-bake even when the
            // artifact is current; ForceSynchronousImport keeps the import on the
            // main process, where the GPU lives
            AssetDatabase.ImportAsset(texture, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            // Clean memory and wait untill next frame
            EditorApplication.QueuePlayerLoopUpdate();
            yield return null;
            GC.Collect();

            if (progressCancelled)
            {
                FinishRun("Stylization cancelled");
                yield break;
            }
        }

        FinishRun("Finished processing " + textures.Count + " textures");
    }


    public static IEnumerator FindAssetsByType<T>() where T : UnityEngine.Object
    {
        progressActive = true;
        progressIndex = 0;
        progressCount = 0;
        progressName = "Searching asset database";
        ReportStage("Gathering textures");
        yield return null;

        // Paths alone decide eligibility, so the sweep costs string comparisons
        // instead of dragging every texture in the project through memory
        List<string> assets = new List<string>();
        int alreadyStylized = 0;
        string currentHash = CurrentFingerprintHash();
        string[] guids = AssetDatabase.FindAssets(string.Format("t:{0}", typeof(T).ToString().Replace("UnityEngine.", "")));
        progressCount = guids.Length;
        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);

            string lowerCaseAssetPath = assetPath.ToLower();
            if (lowerCaseAssetPath.IndexOf(targetString) != -1 &&
                !IsExtensionExcluded(assetPath))
            {
                // A current ledger entry means the artifact already carries
                // this fingerprint — only forced runs re-bake it. A stale
                // entry re-runs (settings or role changed since); no entry
                // re-runs unless the import would pass through pristine anyway
                string entry;
                bool hasEntry = stylizedLedger.TryGetValue(guids[i], out entry);
                if (!runForceAll && hasEntry && entry == currentHash)
                    alreadyStylized++;
                else if (runForceAll || hasEntry || !ImportsPristine(assetPath))
                    assets.Add(assetPath);
            }

            if (i % 200 == 0)
            {
                progressIndex = i + 1;
                progressName = assetPath;
                if (ReportStage("Gathering textures"))
                {
                    FinishRun("Stylization cancelled");
                    yield break;
                }
            }
        }

        Debug.Log("Textures to process: " + assets.Count +
            (runForceAll ? " (forced re-bake)" : "; already stylized and skipped: " + alreadyStylized));
        StartRoutine(TextureProcessor(assets));
    }
}
