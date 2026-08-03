using UnityEditor;
using UnityEngine;

// Floating companion to a bulk stylization run. Unity's own import overlay
// commandeers the shared progress bar for most of each texture's import,
// leaving its cancel button hidden or unclickable — so the abort lives in a
// window of its own. Clicks land between imports; the run stops once the
// texture in flight completes
public class NKLIStylizeAbortWindow : EditorWindow
{
    public static NKLIStylizeAbortWindow Open()
    {
        NKLIStylizeAbortWindow window = GetWindow<NKLIStylizeAbortWindow>(true, "Somnia Fracta", true);
        window.minSize = new Vector2(440.0f, 110.0f);
        return window;
    }

    void OnEnable()
    {
        EditorApplication.update += Repaint;
    }

    void OnDisable()
    {
        EditorApplication.update -= Repaint;
    }

    void OnGUI()
    {
        // Retire once the run ends, or when a domain reload revives the
        // window without a run behind it
        if (!NKLIAssetStylizer.RunActive)
        {
            Close();
            return;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(NKLIAssetStylizer.RunStage ?? "", EditorStyles.boldLabel);

        Rect bar = EditorGUILayout.GetControlRect(false, 18.0f);
        EditorGUI.ProgressBar(bar, NKLIAssetStylizer.RunFraction, NKLIAssetStylizer.RunCounter);

        EditorGUILayout.LabelField(NKLIAssetStylizer.RunName ?? "", EditorStyles.miniLabel);

        GUILayout.FlexibleSpace();

        if (GUILayout.Button("Abort", GUILayout.Height(30.0f)))
            NKLIAssetStylizer.RequestAbort();
    }
}
