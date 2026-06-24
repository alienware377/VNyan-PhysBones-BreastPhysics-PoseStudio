using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class PoseStudioBuild
{
    // Invoked via: Unity.exe -batchmode -quit -executeMethod PoseStudioBuild.Build
    public static void Build()
    {
        const string windowPrefabPath = "Assets/PoseStudioWindow.prefab";
        const string browserPrefabPath = "Assets/PoseStudioBrowser.prefab";
        const string starterPrefabPath = "Assets/JayoPoseStudioStarter.prefab";
        const string bundleName = "jayoposestudio";
        const string outDir = "AssetBundles";

        GameObject windowAsset = BuildWindowPrefab(windowPrefabPath);
        GameObject browserAsset = BuildBrowserPrefab(browserPrefabPath);

        GameObject go = new GameObject("VNyanTemp");
        JayoPoseStudio.PoseStudioPlugin plugin = go.AddComponent<JayoPoseStudio.PoseStudioPlugin>();
        plugin.windowPrefab = windowAsset;
        plugin.browserPrefab = browserAsset;
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
            Debug.LogError("[PoseStudioBuild] BuildAssetBundles returned null");
            EditorApplication.Exit(2);
            return;
        }

        string built = Path.Combine(outDir, bundleName);
        string final = Path.Combine(outDir, "JayoPoseStudio.vnobj");
        if (File.Exists(final)) File.Delete(final);
        File.Copy(built, final);
        Debug.Log("[PoseStudioBuild] wrote " + final);
        EditorApplication.Exit(0);
    }

    // Invoked via: Unity.exe -batchmode -quit -executeMethod PoseStudioBuild.Diagnose
    // Loads the built .vnobj, instantiates it, and reports the state of every
    // InputField so we can see why their text won't render.
    public static void Diagnose()
    {
        string path = "AssetBundles/JayoPoseStudio.vnobj";
        AssetBundle b = AssetBundle.LoadFromFile(path);
        if (b == null) { Debug.LogError("[Diag] could not load " + path); EditorApplication.Exit(2); return; }

        GameObject starter = b.LoadAsset<GameObject>("vnyanitem");
        if (starter == null) { Debug.LogError("[Diag] no vnyanitem asset"); b.Unload(true); EditorApplication.Exit(2); return; }
        JayoPoseStudio.PoseStudioPlugin pl = starter.GetComponent<JayoPoseStudio.PoseStudioPlugin>();
        if (pl == null) { Debug.LogError("[Diag] no PoseStudioPlugin on vnyanitem"); b.Unload(true); EditorApplication.Exit(2); return; }
        GameObject root = pl.windowPrefab;
        if (root == null) { Debug.LogError("[Diag] windowPrefab is null"); b.Unload(true); EditorApplication.Exit(2); return; }

        GameObject inst = Object.Instantiate(root);
        InputField[] fields = inst.GetComponentsInChildren<InputField>(true);
        Debug.Log("[Diag] found " + fields.Length + " InputField(s)");
        foreach (InputField f in fields)
        {
            Text tc = f.textComponent;
            Graphic ph = f.placeholder;
            string fontName = (tc != null && tc.font != null) ? tc.font.name : "<null>";
            string phName = (ph != null) ? ph.name : "<null>";
            string vOver = tc != null ? tc.verticalOverflow.ToString() : "-";
            string rectH = (tc != null) ? tc.rectTransform.rect.height.ToString("0.0") : "-";
            Debug.Log(string.Format(
                "[Diag] {0} | font={1} | text='{2}' | color={3} | size={4} | vOverflow={5} | textRectH={6}",
                f.gameObject.name,
                fontName,
                f.text,
                tc != null ? tc.color.ToString() : "-",
                tc != null ? tc.fontSize.ToString() : "-",
                vOver,
                rectH));
        }

        // Also report a known-good label font for comparison.
        Text[] texts = inst.GetComponentsInChildren<Text>(true);
        foreach (Text t in texts)
        {
            if (t.name == "Lbl_posX" || t.name == "Title")
                Debug.Log(string.Format("[Diag] LABEL {0} | font={1} | text='{2}'",
                    t.name, t.font != null ? t.font.name : "<null>", t.text));
        }

        Object.DestroyImmediate(inst);
        b.Unload(true);
        EditorApplication.Exit(0);
    }

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
        const float gap = 8f;

        // Fixed-size outer window so it always fits inside VNyan: a non-scrolling
        // header (title + status), a scrollable middle area that holds every
        // control, and a non-scrolling footer (Reload / Save / Close). The middle
        // grows as tall as it needs and the user scrolls it with the scrollbar.
        const float headerH  = 56f;
        const float viewportH = 470f;
        const float footerH  = 50f;
        const float totalH   = headerH + viewportH + footerH;
        const float sbW      = 14f;            // vertical scrollbar width
        const float outerX   = P;
        const float outerW   = W - 2f * P;     // header/footer content width

        GameObject root = new GameObject("PoseStudioWindow",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, totalH);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.12f, 0.13f, 0.16f, 0.97f);

        root.AddComponent<JayoPoseStudio.PoseWindowDrag>();

        // ---------- header (fixed) ----------
        float hy = 10f;
        MakeText(root.transform, "Title", "Pose Studio — Toggles & Animations",
            outerX, hy, outerW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
        hy += 26f;
        MakeText(root.transform, "Label_Status", "ready",
            outerX, hy, outerW, 18f, 11, TextAnchor.MiddleLeft, FontStyle.Italic);

        // ---------- scrollable middle ----------
        GameObject scroll = new GameObject("ScrollView",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
        Image sbg = scroll.GetComponent<Image>();
        sbg.color = new Color(0.09f, 0.10f, 0.12f, 1f);
        Place(scroll.transform, root.transform, P, headerH, outerW, viewportH);

        GameObject viewport = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        RectTransform vrt = viewport.GetComponent<RectTransform>();
        vrt.SetParent(scroll.transform, false);
        vrt.anchorMin = new Vector2(0f, 0f);
        vrt.anchorMax = new Vector2(1f, 1f);
        vrt.pivot = new Vector2(0f, 1f);
        vrt.offsetMin = new Vector2(0f, 0f);
        vrt.offsetMax = new Vector2(-sbW, 0f);   // leave room for the scrollbar
        Image vimg = viewport.GetComponent<Image>();
        vimg.color = new Color(1f, 1f, 1f, 0.01f);

        GameObject content = new GameObject("Content", typeof(RectTransform));
        RectTransform crt = content.GetComponent<RectTransform>();
        crt.SetParent(viewport.transform, false);
        crt.anchorMin = new Vector2(0f, 1f);
        crt.anchorMax = new Vector2(1f, 1f);
        crt.pivot = new Vector2(0f, 1f);
        crt.offsetMin = new Vector2(0f, 0f);
        crt.offsetMax = new Vector2(0f, 0f);
        crt.sizeDelta = new Vector2(0f, 0f);

        // Vertical scrollbar pinned to the right edge of the scroll area.
        GameObject sbar = DefaultControls.CreateScrollbar(_res);
        sbar.name = "Scrollbar_Vertical";
        Scrollbar sbc = sbar.GetComponent<Scrollbar>();
        sbc.SetDirection(Scrollbar.Direction.BottomToTop, true);
        RectTransform sbrt = sbar.GetComponent<RectTransform>();
        sbrt.SetParent(scroll.transform, false);
        sbrt.anchorMin = new Vector2(1f, 0f);
        sbrt.anchorMax = new Vector2(1f, 1f);
        sbrt.pivot = new Vector2(1f, 1f);
        sbrt.sizeDelta = new Vector2(sbW, 0f);
        sbrt.anchoredPosition = Vector2.zero;

        ScrollRect sr = scroll.GetComponent<ScrollRect>();
        sr.horizontal = false;
        sr.vertical = true;
        sr.movementType = ScrollRect.MovementType.Clamped;
        sr.scrollSensitivity = 24f;
        sr.viewport = vrt;
        sr.content = crt;
        sr.verticalScrollbar = sbc;
        sr.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

        // All controls below live INSIDE the scroll content.
        Transform c = content.transform;
        const float cX = 6f;
        float cW = outerW - sbW - 2f * cX;   // fit within the viewport

        float y = 6f;

        // Item selector.
        MakeText(c, "Lbl_Item", "Item", cX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Item", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        // Item operations row.
        float b3 = (cW - 2f * gap) / 3f;
        MakeButton(c, "Button_NewToggle", "+ Toggle", cX, y, b3, 28f);
        MakeButton(c, "Button_NewAnim", "+ Animation", cX + (b3 + gap), y, b3, 28f);
        MakeButton(c, "Button_RemoveItem", "Remove", cX + 2f * (b3 + gap), y, b3, 28f);
        y += 34f;

        // Name field.
        MakeText(c, "Lbl_Name", "Name", cX, y, 70f, 28f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_Name", "item name", cX + 76f, y, cW - 76f, 28f);
        y += 34f;

        // Hotkey row: label + current binding + Assign / Clear buttons.
        MakeText(c, "Lbl_Hotkey", "Hotkey", cX, y, 70f, 28f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        float hkBtnW = 64f;
        float hkLabelX = cX + 76f;
        float hkLabelW = cW - 76f - 2f * hkBtnW - 2f * gap;
        MakeText(c, "Label_Hotkey", "(none)", hkLabelX, y, hkLabelW, 28f,
            12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeButton(c, "Button_AssignHotkey", "Assign",
            hkLabelX + hkLabelW + gap, y, hkBtnW, 28f);
        MakeButton(c, "Button_ClearHotkey", "Clear",
            hkLabelX + hkLabelW + gap + hkBtnW + gap, y, hkBtnW, 28f);
        y += 34f;

        MakeToggle(c, "Toggle_Enabled", "Pose Studio enabled (global)",
            cX, y, cW, 24f);
        y += 26f;

        MakeToggle(c, "Toggle_Active", "Activate this item (preview on/off)",
            cX, y, cW, 24f);
        y += 26f;

        MakeToggle(c, "Toggle_Anim", "Animate on a loop (instead of a toggle)",
            cX, y, cW, 24f);
        y += 28f;

        // Animation params.
        MakeText(c, "Lbl_Wave", "Wave", cX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Wave", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        MakeSliderRow(c, cX, cW, "speed", "Anim Speed", 0f, 5f, 1f, ref y);
        MakeSliderRow(c, cX, cW, "blend", "Blend Time", 0.02f, 1.5f, 0.25f, ref y);

        // ---- Keyframe timeline section (animation only) ----
        // Each keyframe stores its OWN pose: select a key, then the Transform and
        // Blendshape sliders above edit that key's pose. "Secs to Next" is the time the
        // animation takes to interpolate from this key to the following one.
        MakeText(c, "Hdr_Keyframes", "— Keyframe poses —", cX, y, cW, 20f,
            12, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 24f;

        MakeToggle(c, "Toggle_Keyframes", "Use keyframe poses (instead of the wave)",
            cX, y, cW, 24f);
        y += 26f;

        MakeText(c, "Lbl_Keyframe", "Keyframe", cX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Keyframe", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        float kb2 = (cW - gap) / 2f;
        MakeButton(c, "Button_AddKey", "Add Keyframe", cX, y, kb2, 28f);
        MakeButton(c, "Button_RemoveKey", "Remove Keyframe", cX + (kb2 + gap), y, kb2, 28f);
        y += 34f;

        MakeText(c, "Hint_Keyframes", "Select a key above, then pose it with the Transform/Blendshape sliders.",
            cX, y, cW, 18f, 10, TextAnchor.MiddleLeft, FontStyle.Italic);
        y += 20f;

        MakeSliderRow(c, cX, cW, "kfsec", "Secs to Next", 0f, 10f, 0.5f, ref y);

        // ---- Blendshape trigger section ----
        MakeText(c, "Hdr_Trigger", "— Blendshape trigger —", cX, y, cW, 20f,
            12, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 24f;

        MakeToggle(c, "Toggle_Trigger", "Drive strength from a blendshape (instead of on/off)",
            cX, y, cW, 24f);
        y += 26f;

        // Source row: label + current source + Pick / Clear buttons.
        MakeText(c, "Lbl_TrigSrc", "Source", cX, y, 70f, 28f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        float trBtnW = 64f;
        float trLabelX = cX + 76f;
        float trLabelW = cW - 76f - 2f * trBtnW - 2f * gap;
        MakeText(c, "Label_TriggerSource", "(no source)", trLabelX, y, trLabelW, 28f,
            12, TextAnchor.MiddleLeft, FontStyle.Bold);
        MakeButton(c, "Button_PickTrigger", "Pick…",
            trLabelX + trLabelW + gap, y, trBtnW, 28f);
        MakeButton(c, "Button_ClearTrigger", "Clear",
            trLabelX + trLabelW + gap + trBtnW + gap, y, trBtnW, 28f);
        y += 34f;

        MakeSliderRow(c, cX, cW, "tcurve", "Curve", 0.1f, 4f, 1f, ref y);
        MakeSliderRow(c, cX, cW, "tscale", "Strength %", 0f, 100f, 100f, ref y);

        // ---- Transform section (bones AND mesh objects) ----
        MakeText(c, "Hdr_Bone", "— Transform (bones & meshes) —", cX, y, cW, 20f,
            12, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 24f;

        MakeText(c, "Lbl_Bone", "Target", cX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Bone", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        float tb3 = (cW - 2f * gap) / 3f;
        MakeButton(c, "Button_AddBone", "Add Bone…", cX, y, tb3, 28f);
        MakeButton(c, "Button_AddMesh", "Add Mesh…", cX + (tb3 + gap), y, tb3, 28f);
        MakeButton(c, "Button_RemoveBone", "Remove", cX + 2f * (tb3 + gap), y, tb3, 28f);
        y += 34f;

        float t3 = (cW - 2f * gap) / 3f;
        MakeToggle(c, "Toggle_UsePos", "Position", cX, y, t3, 24f);
        MakeToggle(c, "Toggle_UseRot", "Rotation", cX + (t3 + gap), y, t3, 24f);
        MakeToggle(c, "Toggle_UseScale", "Scale", cX + 2f * (t3 + gap), y, t3, 24f);
        y += 28f;

        MakeSliderRow(c, cX, cW, "posX", "Pos X", -0.5f, 0.5f, 0f, ref y);
        MakeSliderRow(c, cX, cW, "posY", "Pos Y", -0.5f, 0.5f, 0f, ref y);
        MakeSliderRow(c, cX, cW, "posZ", "Pos Z", -0.5f, 0.5f, 0f, ref y);
        MakeSliderRow(c, cX, cW, "rotX", "Rot X", -180f, 180f, 0f, ref y);
        MakeSliderRow(c, cX, cW, "rotY", "Rot Y", -180f, 180f, 0f, ref y);
        MakeSliderRow(c, cX, cW, "rotZ", "Rot Z", -180f, 180f, 0f, ref y);
        MakeSliderRow(c, cX, cW, "sclX", "Scale X", 0f, 3f, 1f, ref y);
        MakeSliderRow(c, cX, cW, "sclY", "Scale Y", 0f, 3f, 1f, ref y);
        MakeSliderRow(c, cX, cW, "sclZ", "Scale Z", 0f, 3f, 1f, ref y);

        // ---- Blendshape section ----
        MakeText(c, "Hdr_Blend", "— Blendshape —", cX, y, cW, 20f,
            12, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 24f;

        MakeText(c, "Lbl_Blend", "Shape", cX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_Blend", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        float bb2 = (cW - gap) / 2f;
        MakeButton(c, "Button_AddBlend", "Add Blendshape…", cX, y, bb2, 28f);
        MakeButton(c, "Button_RemoveBlend", "Remove Shape", cX + (bb2 + gap), y, bb2, 28f);
        y += 34f;

        MakeSliderRow(c, cX, cW, "weight", "Blend Wt", 0f, 100f, 100f, ref y);

        // ---- Inverse kinematics (IK) section ----
        MakeText(c, "Hdr_IK", "— Inverse kinematics (IK) —", cX, y, cW, 20f,
            12, TextAnchor.MiddleCenter, FontStyle.Bold);
        y += 24f;

        MakeText(c, "Lbl_IKGoal", "IK goal", cX, y, 70f, 30f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_IKGoal", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        float ik2 = (cW - gap) / 2f;
        MakeButton(c, "Button_AddIK", "Add IK Goal", cX, y, ik2, 28f);
        MakeButton(c, "Button_RemoveIK", "Remove IK Goal", cX + (ik2 + gap), y, ik2, 28f);
        y += 34f;

        MakeToggle(c, "Toggle_IKEnabled", "Enable this IK goal", cX, y, cW, 24f);
        y += 26f;

        MakeText(c, "Lbl_IKUpper", "Upper", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_IKUpper", "e.g. LeftUpperLeg", cX + 76f, y, cW - 76f, 28f);
        y += 32f;
        MakeText(c, "Lbl_IKLower", "Lower", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_IKLower", "e.g. LeftLowerLeg", cX + 76f, y, cW - 76f, 28f);
        y += 32f;
        MakeText(c, "Lbl_IKEnd", "End", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_IKEnd", "e.g. LeftFoot", cX + 76f, y, cW - 76f, 28f);
        y += 32f;
        MakeText(c, "Lbl_IKSpace", "Space", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_IKSpace", "root | world | bone", cX + 76f, y, cW - 76f, 28f);
        y += 32f;

        MakeText(c, "Lbl_IKCapture", "Capture", cX, y, 70f, 30f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeDropdown(c, "Dropdown_IKCapture", cX + 76f, y, cW - 76f, 30f);
        y += 36f;

        MakeToggle(c, "Toggle_IKHoldRot", "Hold end rotation (sole flat / hand angle)", cX, y, cW, 24f);
        y += 26f;

        MakeText(c, "Lbl_IKWeight", "Weight", cX, y, 70f, 28f, 12, TextAnchor.MiddleLeft, FontStyle.Normal);
        MakeInput(c, "Input_IKWeight", "1.0", cX + 76f, y, cW - 76f, 28f);
        y += 32f;

        MakeText(c, "Hint_IK", "Pin a foot/hand: set Upper/Lower/End bones + a Space, then Reload. 'bind' captures the target at the rest pose (feet stay centered); 'play' captures on the first frame (crossed hands).",
            cX, y, cW, 44f, 10, TextAnchor.UpperLeft, FontStyle.Italic);
        y += 48f;

        y += 6f;
        crt.sizeDelta = new Vector2(0f, y);   // total scrollable height

        // ---------- footer (fixed) ----------
        float fy = headerH + viewportH + 8f;
        float bw = (outerW - 2f * gap) / 3f;
        MakeButton(root.transform, "Button_Reload", "Reload", outerX, fy, bw, 30f);
        MakeButton(root.transform, "Button_Save", "Save", outerX + (bw + gap), fy, bw, 30f);
        MakeButton(root.transform, "Button_Close", "Close", outerX + 2f * (bw + gap), fy, bw, 30f);

        GameObject asset = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return asset;
    }

    // Bone/blendshape browser: scroll panel; rows generated at runtime by the plugin.
    static GameObject BuildBrowserPrefab(string prefabPath)
    {
        const float W = 360f;
        const float H = 480f;
        const float P = 12f;
        const float contentW = W - 2f * P;

        GameObject root = new GameObject("PoseStudioBrowser",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        RectTransform rrt = root.GetComponent<RectTransform>();
        rrt.anchorMin = new Vector2(0.5f, 0.5f);
        rrt.anchorMax = new Vector2(0.5f, 0.5f);
        rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(W, H);
        Image bg = root.GetComponent<Image>();
        bg.sprite = _res.background;
        bg.type = Image.Type.Sliced;
        bg.color = new Color(0.12f, 0.13f, 0.16f, 0.98f);

        root.AddComponent<JayoPoseStudio.PoseWindowDrag>();

        float y = 10f;

        MakeText(root.transform, "Title", "Model Bones — pick one to add",
            P, y, contentW, 22f, 15, TextAnchor.MiddleCenter, FontStyle.Bold);
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
        sbg.color = new Color(0.07f, 0.07f, 0.09f, 1f);
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

    // ---- helpers ----

    static void MakeSliderRow(Transform parent, float contentX, float contentW,
        string key, string label, float min, float max, float def, ref float y)
    {
        MakeText(parent, "Lbl_" + key, label, contentX, y, 100f, 28f,
            12, TextAnchor.MiddleLeft, FontStyle.Normal);

        GameObject sl = DefaultControls.CreateSlider(_res);
        sl.name = "Slider_" + key;
        Slider sc = sl.GetComponent<Slider>();
        sc.minValue = min;
        sc.maxValue = max;
        sc.value = Mathf.Clamp(def, min, max);
        Place(sl.transform, parent, contentX + 104f, y + 6f, contentW - 104f - 60f, 18f);

        MakeValueInput(parent, "Input_" + key, def.ToString("0.00"),
            contentX + contentW - 58f, y + 2f, 58f, 24f);

        y += 32f;
    }

    // Editable numeric box that sits to the right of a slider. Drives the slider
    // at runtime (wired up in PoseStudioPlugin). Named "Input_<key>".
    static void MakeValueInput(Transform parent, string name, string text,
        float x, float y, float w, float h)
    {
        GameObject inp = DefaultControls.CreateInputField(_res);
        inp.name = name;
        Place(inp.transform, parent, x, y, w, h);

        // Force a darker box background so dark text always reads.
        Image bg = inp.GetComponent<Image>();
        if (bg != null) bg.color = new Color(0.92f, 0.92f, 0.95f, 1f);

        InputField field = inp.GetComponent<InputField>();
        if (field != null)
        {
            // IMPORTANT: address the text/placeholder through the InputField's own
            // references — the child is named "Text (Legacy)", so Find("Text")
            // returns null. Use Overflow so a 12px line is never truncated to
            // nothing in the short box (the classic invisible-InputField bug).
            StyleInputText(field.textComponent, TextAnchor.MiddleRight,
                new Color(0.05f, 0.05f, 0.07f, 1f), false);

            Text pt = field.placeholder as Text;
            if (pt != null)
            {
                pt.text = "";
                StyleInputText(pt, TextAnchor.MiddleRight,
                    new Color(0.4f, 0.4f, 0.4f, 0.6f), false);
            }

            field.contentType = InputField.ContentType.DecimalNumber;
            field.text = text;
        }
    }

    // Configure an InputField's text/placeholder Text so it renders reliably in a
    // bundle: builtin font, modest size, no truncation, tight margins.
    static void StyleInputText(Text t, TextAnchor anchor, Color color, bool rich)
    {
        if (t == null) return;
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = 12;
        t.alignment = anchor;
        t.color = color;
        t.supportRichText = rich;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        RectTransform trt = t.rectTransform;
        if (trt != null)
        {
            // Full-height text area with small horizontal padding.
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(6f, 0f);
            trt.offsetMax = new Vector2(-6f, 0f);
        }
    }

    static void MakeDropdown(Transform parent, string name, float x, float y, float w, float h)
    {
        GameObject dd = DefaultControls.CreateDropdown(_res);
        dd.name = name;
        Place(dd.transform, parent, x, y, w, h);
    }

    static void MakeInput(Transform parent, string name, string placeholder,
        float x, float y, float w, float h)
    {
        GameObject inp = DefaultControls.CreateInputField(_res);
        inp.name = name;
        Place(inp.transform, parent, x, y, w, h);

        InputField field = inp.GetComponent<InputField>();
        if (field != null)
        {
            // Same invisible-text fix as MakeValueInput: configure via the
            // InputField's real text/placeholder refs (child is "Text (Legacy)").
            StyleInputText(field.textComponent, TextAnchor.MiddleLeft,
                new Color(0.05f, 0.05f, 0.07f, 1f), false);

            Text pt = field.placeholder as Text;
            if (pt != null)
            {
                pt.text = placeholder;
                StyleInputText(pt, TextAnchor.MiddleLeft,
                    new Color(0.4f, 0.4f, 0.4f, 0.6f), false);
            }
        }
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
            if (lt != null) { lt.text = label; lt.color = Color.white; lt.fontSize = 12; }
        }
    }

    static void MakeButton(Transform parent, string name, string label,
        float x, float y, float w, float h)
    {
        GameObject go = DefaultControls.CreateButton(_res);
        go.name = name;
        Place(go.transform, parent, x, y, w, h);
        Text bt = go.GetComponentInChildren<Text>(true);
        if (bt != null) { bt.text = label; bt.color = Color.black; bt.fontSize = 12; }
    }
}
