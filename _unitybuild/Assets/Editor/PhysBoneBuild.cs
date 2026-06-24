using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class PhysBoneBuild
{
    // Invoked via: Unity.exe -batchmode -quit -executeMethod PhysBoneBuild.Build
    public static void Build()
    {
        const string windowPrefabPath = "Assets/PhysBonesWindow.prefab";
        const string browserPrefabPath = "Assets/PhysBonesBoneBrowser.prefab";
        const string starterPrefabPath = "Assets/JayoPhysBonesStarter.prefab";
        const string bundleName = "jayophysbones";
        const string outDir = "AssetBundles";

        // 1) Build the tuning window prefab (panel + sliders).
        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);

        // 1b) Build the bone-browser prefab (panel + scroll view, rows generated at runtime).
        GameObject browserAsset = BuildBoneBrowserPrefab(browserPrefabPath);

        // 2) Build the starter prefab carrying PhysBonePlugin, pointing at the window.
        //    VNyan loads the autostarter by the addressable asset name "vnyanitem" and
        //    the root GameObject is named "VNyanTemp" (matches the official SDK output).
        GameObject go = new GameObject("VNyanTemp");
        JayoPhysBones.PhysBonePlugin plugin = go.AddComponent<JayoPhysBones.PhysBonePlugin>();
        plugin.windowPrefab = windowAsset;
        plugin.boneBrowserPrefab = browserAsset;
        GameObject starterAsset = PrefabUtility.SaveAsPrefabAsset(go, starterPrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 3) Bundle the starter prefab under the addressable name VNyan expects
        //    ("vnyanitem"). The window prefab is pulled in as a dependency.
        AssetBundleBuild abb = new AssetBundleBuild();
        abb.assetBundleName = bundleName;
        abb.assetNames = new string[] { starterPrefabPath };
        abb.addressableNames = new string[] { "vnyanitem" };

        Directory.CreateDirectory(outDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outDir, new AssetBundleBuild[] { abb },
            BuildAssetBundleOptions.None, BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[PhysBoneBuild] BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "JayoPhysBones.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[PhysBoneBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    // ---- window prefab construction ----

    static readonly string[] SLIDER_KEYS =
    {
        "pull", "spring", "stiffness", "gravity", "gravityFalloff",
        "immobile", "maxAngle", "radius", "maxStretch"
    };
    static readonly string[] SLIDER_LABELS =
    {
        "Pull", "Spring", "Stiffness", "Gravity", "Grav Falloff",
        "Immobile", "Max Angle", "Radius", "Max Stretch"
    };
    static readonly float[] SLIDER_MIN = { 0f, 0f, 0f, -1f, 0f, 0f, 0f, 0f, 0f };
    static readonly float[] SLIDER_MAX = { 1f, 1f, 1f, 1f, 1f, 1f, 180f, 0.5f, 1f };

    static DefaultControls.Resources _res;

    static GameObject BuildWindowPrefab(string prefabPath)
    {
        _res = new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            inputField = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/InputFieldBackground.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
            checkmark = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Checkmark.psd"),
            dropdown = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd"),
            mask = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UIMask.psd")
        };

        const float W = 440f;
        const float P = 12f;
        const float contentX = P;
        const float contentW = W - 2f * P;

        // Root panel.
        GameObject root = new GameObject("PhysBonesWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.13f, 0.15f, 0.96f);

        // Drag-to-move the whole window (sliders consume their own drags).
        root.AddComponent<JayoPhysBones.WindowDrag>();

        float y = 10f;

        // Title.
        MakeText(root.transform, "Title", "VRChat PhysBones — Live Tuning",
            contentX, y, contentW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 26f;

        // Status line.
        MakeText(root.transform, "Label_Status", "ready",
            contentX, y, contentW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 24f;

        // Chain selector.
        MakeText(root.transform, "Lbl_Chain", "Chain", contentX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        GameObject dd = DefaultControls.CreateDropdown(_res);
        dd.name = "Dropdown_Chain";
        Place(dd.transform, root.transform, contentX + 76f, y, contentW - 76f, 30f);
        y += 36f;

        // Global enabled toggle.
        MakeToggle(root.transform, "Toggle_Enabled", "Physics enabled (global)",
            contentX, y, contentW, 24f);
        y += 28f;

        // Per-chain angle-limit toggle.
        MakeToggle(root.transform, "Toggle_Limit", "Angle limit (this chain)",
            contentX, y, contentW, 24f);
        y += 28f;

        // Global: disable the model's built-in physics (VRM SpringBone / MagicaCloth / SPCR).
        MakeToggle(root.transform, "Toggle_DisableNative", "Override native physics (use only PhysBones)",
            contentX, y, contentW, 24f);
        y += 28f;

        // Scope the override to bones under configured chains only.
        MakeToggle(root.transform, "Toggle_ScopeNative", "    └ only my configured chains",
            contentX, y, contentW, 24f);
        y += 30f;

        // Sliders.
        for (int i = 0; i < SLIDER_KEYS.Length; i++)
        {
            string key = SLIDER_KEYS[i];

            MakeText(root.transform, "Lbl_" + key, SLIDER_LABELS[i],
                contentX, y, 100f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);

            GameObject sl = DefaultControls.CreateSlider(_res);
            sl.name = "Slider_" + key;
            Slider sc = sl.GetComponent<Slider>();
            sc.minValue = SLIDER_MIN[i];
            sc.maxValue = SLIDER_MAX[i];
            sc.value = Mathf.Clamp(0f, SLIDER_MIN[i], SLIDER_MAX[i]);
            Place(sl.transform, root.transform, contentX + 104f, y + 6f, contentW - 104f - 60f, 18f);

            MakeText(root.transform, "Value_" + key, "0.00",
                contentX + contentW - 56f, y, 56f, 28f, 12, TextAnchor.MiddleRight, FontStyle.Normal);

            y += 32f;
        }

        y += 4f;

        // Buttons row (5 across: Bones, Remove, Reload, Save, Close).
        const float gap = 8f;
        float bw = (contentW - 4f * gap) / 5f;
        MakeButton(root.transform, "Button_Bones", "Bones", contentX, y, bw, 30f);
        MakeButton(root.transform, "Button_Remove", "Remove", contentX + (bw + gap), y, bw, 30f);
        MakeButton(root.transform, "Button_Reload", "Reload", contentX + 2f * (bw + gap), y, bw, 30f);
        MakeButton(root.transform, "Button_Save", "Save", contentX + 3f * (bw + gap), y, bw, 30f);
        MakeButton(root.transform, "Button_Close", "Close", contentX + 4f * (bw + gap), y, bw, 30f);
        y += 30f + P;

        rrt.sizeDelta = new Vector2(W, y);

        GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return asset;
    }

    // ---- bone-browser prefab construction ----
    // A scrollable panel. The collapsible bone-tree rows are generated at runtime by the
    // plugin (it can't know the avatar's bones at build time), so this prefab only provides
    // the chrome: title, status line, a ScrollRect with a top-stretch content container
    // ("BoneContent"), and a close button.
    static GameObject BuildBoneBrowserPrefab(string prefabPath)
    {
        const float W = 360f;
        const float H = 460f;
        const float P = 12f;
        const float contentW = W - 2f * P;

        GameObject root = new GameObject("PhysBonesBoneBrowser",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, H);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.13f, 0.13f, 0.15f, 0.97f);

        root.AddComponent<JayoPhysBones.WindowDrag>();

        float y = 10f;

        MakeText(root.transform, "Title", "Model Bones — pick a root",
            P, y, contentW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 26f;

        MakeText(root.transform, "Label_BoneStatus", "no avatar",
            P, y, contentW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 24f;

        // Close button (top-right corner area, on its own row).
        MakeButton(root.transform, "Button_BoneClose", "Close",
            P + contentW - 80f, y, 80f, 26f);
        y += 32f;

        // Scroll view fills the rest of the panel.
        float scrollTop = y;
        float scrollH = H - scrollTop - P;

        GameObject scroll = new GameObject("BoneScrollView",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        Image sbg = scroll.GetComponent<Image>();
        sbg.color = new Color(0.08f, 0.08f, 0.10f, 1f);
        Place(scroll.transform, root.transform, P, scrollTop, contentW, scrollH);

        // Viewport (masks the content).
        GameObject viewport = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        RectTransform vrt = viewport.GetComponent<RectTransform>();
        vrt.SetParent(scroll.transform, false);
        vrt.anchorMin = new Vector2(0f, 0f);
        vrt.anchorMax = new Vector2(1f, 1f);
        vrt.pivot = new Vector2(0f, 1f);
        vrt.offsetMin = new Vector2(0f, 0f);
        vrt.offsetMax = new Vector2(0f, 0f);
        Image vimg = viewport.GetComponent<Image>();
        vimg.color = new Color(1f, 1f, 1f, 0.01f);

        // Content: top-stretched, height set at runtime as rows are added.
        GameObject content = new GameObject("BoneContent", typeof(RectTransform));
        RectTransform crt = content.GetComponent<RectTransform>();
        crt.SetParent(viewport.transform, false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0f, 1f);
        crt.offsetMin = new Vector2(0f, 0f);
        crt.offsetMax = new Vector2(0f, 0f);
        crt.sizeDelta = new Vector2(0f, 0f);

        ScrollRect sr = scroll.GetComponent<ScrollRect>();
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 18f;
        sr.viewport = vrt;
        sr.content = crt;

        GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return asset;
    }

    // Position a control under parent using top-left anchoring (y grows downward).
    static RectTransform Place(Transform t, Transform parent, float x, float y, float w, float h)
    {
        RectTransform rt = t as RectTransform;
        if (rt == null) rt = t.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x, -y);
        return rt;
    }

    static Text MakeText(Transform parent, string name, string text,
        float x, float y, float w, float h, int size, TextAnchor anchor, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        Text t = go.GetComponent<Text>();
        t.text = text;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = anchor;
        t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        Place(go.transform, parent, x, y, w, h);
        return t;
    }

    static void MakeToggle(Transform parent, string name, string label,
        float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateToggle(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Transform lbl = go.transform.Find("Label");
        if (lbl != null)
        {
            Text lt = lbl.GetComponent<Text>();
            if (lt != null)
            {
                lt.text = label;
                lt.color = Color.white;
                lt.fontSize = 12;
            }
        }
    }

    static void MakeButton(Transform parent, string name, string label,
        float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        // The label child is named "Text" or "Text (Legacy)" depending on Unity version,
        // so grab the Text component directly instead of by child name.
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null)
        {
            bt.text = label;
            bt.color = Color.black;
            bt.fontSize = 13;
        }
    }
}
