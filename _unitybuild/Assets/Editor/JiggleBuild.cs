using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class JiggleBuild
{
    // Invoked via: Unity.exe -batchmode -quit -executeMethod JiggleBuild.Build
    public static void Build()
    {
        const string windowPrefabPath = "Assets/JigglePhysicsWindow.prefab";
        const string browserPrefabPath = "Assets/JigglePhysicsBoneBrowser.prefab";
        const string starterPrefabPath = "Assets/JayoJigglePhysicsStarter.prefab";
        const string bundleName = "jayojigglephysics";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);
        GameObject browserAsset = BuildBoneBrowserPrefab(browserPrefabPath);

        // VNyan loads the autostarter by addressable name "vnyanitem"; root GameObject "VNyanTemp".
        GameObject go = new GameObject("VNyanTemp");
        JayoJiggle.JigglePhysicsPlugin plugin = go.AddComponent<JayoJiggle.JigglePhysicsPlugin>();
        plugin.windowPrefab = windowAsset;
        plugin.boneBrowserPrefab = browserAsset;
        GameObject starterAsset = PrefabUtility.SaveAsPrefabAsset(go, starterPrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

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
            Debug.LogError("[JiggleBuild] BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "JayoJigglePhysics.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[JiggleBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    static readonly string[] SLIDER_KEYS =
    {
        "weight", "bounce", "stiffness", "damping", "pull",
        "maxAngle", "stretch", "squish", "radius"
    };
    static readonly string[] SLIDER_LABELS =
    {
        "Weight", "Bounce", "Stiffness", "Damping", "Pull",
        "Bra Angle", "Stretch", "Squish", "Radius"
    };
    static readonly float[] SLIDER_MIN = { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
    static readonly float[] SLIDER_MAX = { 1f, 1f, 1f, 1f, 1f, 120f, 1f, 1f, 0.2f };
    static readonly float[] SLIDER_DEF = { 0.5f, 0.6f, 0.2f, 0.1f, 0.15f, 60f, 0.4f, 0.5f, 0.04f };

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

        GameObject root = new GameObject("JigglePhysicsWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.12f, 0.13f, 0.16f, 0.96f);

        root.AddComponent<JayoJiggle.JiggleWindowDrag>();

        float y = 10f;

        MakeText(root.transform, "Title", "Jiggle Physics — Live Tuning",
            contentX, y, contentW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 26f;

        MakeText(root.transform, "Label_Status", "ready",
            contentX, y, contentW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 24f;

        MakeText(root.transform, "Lbl_Bone", "Bone", contentX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        GameObject dd = DefaultControls.CreateDropdown(_res);
        dd.name = "Dropdown_Bone";
        Place(dd.transform, root.transform, contentX + 76f, y, contentW - 76f, 30f);
        y += 36f;

        MakeToggle(root.transform, "Toggle_Enabled", "Jiggle enabled (global)",
            contentX, y, contentW, 24f);
        y += 28f;

        MakeToggle(root.transform, "Toggle_Limit", "Bra limiter (angle cap, this bone)",
            contentX, y, contentW, 24f);
        y += 28f;

        MakeToggle(root.transform, "Toggle_Self", "Left/right self-collision (this bone)",
            contentX, y, contentW, 24f);
        y += 30f;

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
            sc.value = Mathf.Clamp(SLIDER_DEF[i], SLIDER_MIN[i], SLIDER_MAX[i]);
            Place(sl.transform, root.transform, contentX + 104f, y + 6f, contentW - 104f - 60f, 18f);

            MakeText(root.transform, "Value_" + key, "0.00",
                contentX + contentW - 56f, y, 56f, 28f, 12, TextAnchor.MiddleRight, FontStyle.Normal);

            y += 32f;
        }

        y += 4f;

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

    // ---- bone-browser prefab (scrollable tree; rows generated at runtime) ----
    static GameObject BuildBoneBrowserPrefab(string prefabPath)
    {
        const float W = 360f;
        const float H = 460f;
        const float P = 12f;
        const float contentW = W - 2f * P;

        GameObject root = new GameObject("JigglePhysicsBoneBrowser",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, H);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.12f, 0.13f, 0.16f, 0.97f);

        root.AddComponent<JayoJiggle.JiggleWindowDrag>();

        float y = 10f;

        MakeText(root.transform, "Title", "Model Bones — pick a jiggle bone",
            P, y, contentW, 22f, 14, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 26f;

        MakeText(root.transform, "Label_BoneStatus", "no avatar",
            P, y, contentW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 24f;

        MakeButton(root.transform, "Button_BoneClose", "Close",
            P + contentW - 80f, y, 80f, 26f);
        y += 32f;

        float scrollTop = y;
        float scrollH = H - scrollTop - P;

        GameObject scroll = new GameObject("BoneScrollView",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        Image sbg = scroll.GetComponent<Image>();
        sbg.color = new Color(0.08f, 0.08f, 0.10f, 1f);
        Place(scroll.transform, root.transform, P, scrollTop, contentW, scrollH);

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
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null)
        {
            bt.text = label;
            bt.color = Color.black;
            bt.fontSize = 13;
        }
    }
}
