using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

namespace PoseStudio
{
    // Global keyboard state via Win32. Unity's Input only sees keys while the VNyan
    // window has OS focus; VNyan is usually in the background when a streamer fires a
    // hotkey, so item-activation hotkeys must read the GLOBAL physical key state instead.
    // GetAsyncKeyState returns the async physical state regardless of foreground window;
    // polling it once per frame (in Update) gives reliable edge detection without a
    // low-level hook or a background message loop. C#-5 safe (no interpolation / lambdas).
    internal static class GlobalKeys
    {
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        const int VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12;

        public static bool IsDown(int vk)
        {
            if (vk == 0) return false;
            return (GetAsyncKeyState(vk) & 0x8000) != 0;
        }
        public static bool Ctrl() { return IsDown(VK_CONTROL); }
        public static bool Shift() { return IsDown(VK_SHIFT); }
        public static bool Alt() { return IsDown(VK_MENU); }

        // Map a Unity KeyCode to its Windows virtual-key code. 0 = unmapped.
        public static int ToVk(KeyCode k)
        {
            if (k >= KeyCode.A && k <= KeyCode.Z) return (int)k - (int)KeyCode.A + 0x41;          // A..Z
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return (int)k - (int)KeyCode.Alpha0 + 0x30; // 0..9
            if (k >= KeyCode.F1 && k <= KeyCode.F15) return (int)k - (int)KeyCode.F1 + 0x70;      // F1..F15
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return (int)k - (int)KeyCode.Keypad0 + 0x60; // numpad 0..9
            switch (k)
            {
                case KeyCode.Space: return 0x20;
                case KeyCode.Return: return 0x0D;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Backspace: return 0x08;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.KeypadPlus: return 0x6B;
                case KeyCode.KeypadMinus: return 0x6D;
                case KeyCode.KeypadMultiply: return 0x6A;
                case KeyCode.KeypadDivide: return 0x6F;
                case KeyCode.KeypadPeriod: return 0x6E;
                case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.Minus: return 0xBD;
                case KeyCode.Equals: return 0xBB;
                case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.RightBracket: return 0xDD;
                case KeyCode.Semicolon: return 0xBA;
                case KeyCode.Quote: return 0xDE;
                case KeyCode.Comma: return 0xBC;
                case KeyCode.Period: return 0xBE;
                case KeyCode.Slash: return 0xBF;
                case KeyCode.BackQuote: return 0xC0;
                case KeyCode.Backslash: return 0xDC;
            }
            return 0;
        }
    }
    // VNyan plugin: Pose Studio. Create user-friendly TOGGLES and looping ANIMATIONS that
    // drive bone position/rotation/scale offsets and blendshape weights. Bones are picked
    // from a collapsible model tree (same approach as the PhysBones plugin); blendshapes are
    // picked from a mesh -> shape tree. Everything saves to a shareable JSON file in AppData.
    // Apply the pose EARLY in LateUpdate (negative order) so the avatar's spring/jiggle
    // physics, native VRM SpringBone, PhysBones, Jiggle, all at default order, run AFTER
    // the dance is posed and therefore react to it. Without this, when Pose Studio (not face
    // tracking) drives the body, the springs may simulate before the pose is applied and never
    // see the motion, so hair/tail/breast look frozen.
    [DefaultExecutionOrder(-100)]
    public class PoseStudioPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler,
        VNyanInterface.IBlendshapeProcessingListener
    {
        // VNyan's VRM clip pipeline runs tracking, then expressions, then node graphs, and calls
        // each listener after every stage. Pose Studio adds its clips at the LAST stage so a graph
        // or expression rule cannot wipe them, on top of whatever came before, clamped to 0..1.
        public void onAfterTracking(ref Dictionary<string, float> shapes) { }
        public void onAfterExpressions(ref Dictionary<string, float> shapes) { }
        public void onAfterNodes(ref Dictionary<string, float> shapes)
        {
            if (applier == null || shapes == null || applier.clipValues.Count == 0) return;
            foreach (KeyValuePair<string, float> kv in applier.clipValues)
            {
                float cur;
                bool had = shapes.TryGetValue(kv.Key, out cur);
                shapes[kv.Key] = Mathf.Clamp(cur + kv.Value, -PoseApplier.BLEND_LIMIT / 100f, PoseApplier.BLEND_LIMIT / 100f);
                if (clipsLogged.Add(kv.Key))
                    Debug.Log("[PoseStudio] VRM clip '" + kv.Key + "' driven: +" + kv.Value.ToString("0.00")
                              + (had ? "" : " (VNyan had no value for it this frame)"));
            }
        }
        readonly HashSet<string> clipsLogged = new HashSet<string>();

        const string BUTTON_NAME = "Pose Studio";
        const string CONFIG_FILE = "posestudio.json";

        // Slider keys -> fields. Order also defines UI layout order in the build script.
        static readonly string[] SLIDER_KEYS =
        {
            "posX", "posY", "posZ", "rotX", "rotY", "rotZ",
            "sclX", "sclY", "sclZ", "weight", "speed", "blend",
            "tcurve", "tscale", "kfsec", "idlewait",
            // Fit to this model: one multiplier per bone group, saved against the bound body
            "fitArmsOut", "fitArmsUp", "fitArmsFwd", "fitFeetOut", "fitFeetFwd", "fitHipsUp"
        };

        // Each fit row drives one axis of one bone group: "how far out", "how much higher".
        // { key, label, group, axis, range }
        static readonly string[][] FIT_ROWS =
        {
            new[] { "fitArmsOut", "Fit: Arms Out",   "Hands", "x", "0.6" },
            new[] { "fitArmsUp",  "Fit: Arms Up",    "Hands", "y", "0.6" },
            new[] { "fitArmsFwd", "Fit: Arms Fwd",   "Hands", "z", "0.6" },
            new[] { "fitFeetOut", "Fit: Feet Apart", "Feet",  "x", "0.4" },
            new[] { "fitFeetFwd", "Fit: Feet Fwd",   "Feet",  "z", "0.4" },
            new[] { "fitHipsUp",  "Fit: Hips Up",    "Hips",  "y", "0.4" },
        };
        bool fitRowsBuilt;

        // Assigned by the AssetBundle build (PoseStudioBuild.cs).
        public GameObject windowPrefab;
        public GameObject browserPrefab;
        public GameObject timelinePrefab;

        PoseConfig config;
        string configPath;
        string savePath;

        GameObject boundAvatar;
        Animator boundAnimator;
        readonly PoseApplier applier = new PoseApplier();
        bool restoredWhileDisabled;

        // ----- UI state -----
        GameObject window;
        Dropdown itemDropdown;
        Dropdown boneDropdown;       // combined bone + mesh + fit-up transform target list
        GroupAdjust selectedFit;     // a "(fit) <group>" row, edited with the same sliders
        Dropdown blendDropdown;
        Dropdown waveDropdown;
        Dropdown keyframeDropdown;
        Toggle keyframeToggle;

        // IK goal editor controls.
        Dropdown ikGoalDropdown;
        Toggle ikEnabledToggle;
        Toggle ikHoldRotToggle;
        InputField ikUpperInput;
        InputField ikLowerInput;
        InputField ikEndInput;
        InputField ikSpaceInput;
        InputField ikWeightInput;
        Dropdown ikCaptureDropdown;
        IKGoal selectedIK;
        InputField nameInput;
        Toggle enabledToggle;
        Toggle activeToggle;
        Toggle idleToggle;
        Toggle animToggle;
        Toggle usePosToggle;
        Toggle useRotToggle;
        Toggle useScaleToggle;
        Toggle triggerToggle;
        Text triggerSourceLabel;
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, InputField> valueInputs = new Dictionary<string, InputField>();
        Text statusLabel;

        // Hotkey binding UI/state.
        Text hotkeyLabel;
        Button assignHotkeyButton;
        bool listeningForHotkey;

        PoseItem selectedItem;
        BoneTarget selectedBone;
        MeshTarget selectedMesh;
        BlendTarget selectedBlend;
        PoseKeyframe selectedKey;
        // Combined transform-target dropdown order (BoneTarget or MeshTarget entries).
        readonly List<object> transformTargets = new List<object>();
        bool suppressCallbacks;
        Font uiFont;

        static readonly string[] WAVE_OPTIONS = { "sine", "triangle", "pulse" };

        // ----- browser state -----
        GameObject browserWindow;
        TimelineEditor timeline;
        RectTransform browserContent;
        Text browserStatus;
        Text browserTitle;
        string browserMode = "bone";       // "bone", "mesh", "blend", or "trigger"
        GameObject browserAvatar;          // avatar whose tree is shown
        readonly Dictionary<Transform, bool> boneExpanded = new Dictionary<Transform, bool>();
        readonly Dictionary<int, bool> meshExpanded = new Dictionary<int, bool>();

        void Awake()
        {
            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            try
            {
                VNyanInterface.VNyanInterface.VNyanAvatar.registerBlendshapeProcessingListener(this);
                Debug.Log("[PoseStudio] VRM clip control hooked into VNyan's expressions");
            }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] could not hook VRM clips: " + e.Message); }
            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON_NAME, this); }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] registerPluginButton failed: " + e.Message); }

            ResolveConfigPath();
            LoadConfig();
            SetupWindow();
            SetupBrowser();
            timeline = new TimelineEditor();
            timeline.Setup(this, timelinePrefab);

            // Late pass: after the physics have run (they overwrite scale on jiggle bones),
            // re-apply the pose's bone scale so a "bigger chest"-style toggle still wins.
            GameObject lateGo = new GameObject("PoseStudioLateScale");
            lateGo.transform.SetParent(transform, false);
            lateGo.AddComponent<PoseStudioLateScale>().applier = applier;
            InstallPostLate();

            Debug.Log("[PoseStudio] initialized. Config: " + configPath);
            CheckSafetyCopy();
        }

        public void pluginButtonClicked()
        {
            if (window == null)
            {
                LoadConfig();
                Rebind();
                return;
            }

            bool show = !window.activeSelf;
            window.SetActive(show);
            if (show)
            {
                window.transform.SetAsLastSibling();
                RefreshItemList();
            }
        }

        void LateUpdate()
        {
            EnsureAvatar();
            if (boundAvatar == null) return;

            if (config == null || config.settings == null || !config.settings.enabled)
            {
                if (!restoredWhileDisabled) { applier.Restore(); restoredWhileDisabled = true; }
                return;
            }
            restoredWhileDisabled = false;

            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            if (dt <= 0f) return;
            UpdateIdle();          // sample tracked head BEFORE we pose; auto start/stop idle anim
            applier.Apply(dt);
            SampleAppliedHead();   // remember what we wrote so next frame can tell tracker vs us
        }

        // ========================= main window =========================

        void SetupWindow()
        {
            if (windowPrefab == null) return;
            try
            {
                window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PoseStudio] instantiateUIPrefab failed: " + e.Message);
                window = null;
            }
            if (window == null) return;

            RectTransform wrt = window.GetComponent<RectTransform>();
            if (wrt != null) wrt.anchoredPosition = new Vector2(0f, 0f);
            PoseUtil.CleanDashes(window);

            itemDropdown = FindControl<Dropdown>("Dropdown_Item");
            boneDropdown = FindControl<Dropdown>("Dropdown_Bone");
            blendDropdown = FindControl<Dropdown>("Dropdown_Blend");
            waveDropdown = FindControl<Dropdown>("Dropdown_Wave");
            keyframeDropdown = FindControl<Dropdown>("Dropdown_Keyframe");
            keyframeToggle = FindControl<Toggle>("Toggle_Keyframes");
            nameInput = FindControl<InputField>("Input_Name");
            enabledToggle = FindControl<Toggle>("Toggle_Enabled");
            activeToggle = FindControl<Toggle>("Toggle_Active");
            idleToggle = FindControl<Toggle>("Toggle_IdleAnim");
            animToggle = FindControl<Toggle>("Toggle_Anim");
            usePosToggle = FindControl<Toggle>("Toggle_UsePos");
            useRotToggle = FindControl<Toggle>("Toggle_UseRot");
            useScaleToggle = FindControl<Toggle>("Toggle_UseScale");
            triggerToggle = FindControl<Toggle>("Toggle_Trigger");
            triggerSourceLabel = FindControl<Text>("Label_TriggerSource");
            statusLabel = FindControl<Text>("Label_Status");
            hotkeyLabel = FindControl<Text>("Label_Hotkey");
            assignHotkeyButton = FindControl<Button>("Button_AssignHotkey");

            EnsureFitRows();

            sliders.Clear();
            valueInputs.Clear();
            for (int i = 0; i < SLIDER_KEYS.Length; i++)
            {
                string key = SLIDER_KEYS[i];
                Slider s = FindControl<Slider>("Slider_" + key);
                if (s != null) sliders[key] = s;
                InputField vi = FindControl<InputField>("Input_" + key);
                if (vi != null)
                {
                    valueInputs[key] = vi;
                    // Force readable dark text/caret on the light input background.
                    // Also re-assign the builtin font: DefaultControls can leave a
                    // non-rendering font in the bundle, giving blank-looking boxes.
                    StyleInputText(vi.textComponent, TextAnchor.MiddleRight,
                        new Color(0.05f, 0.05f, 0.07f, 1f));
                    StyleInputText(vi.placeholder as Text, TextAnchor.MiddleRight,
                        new Color(0.4f, 0.4f, 0.4f, 0.6f));
                    vi.customCaretColor = true;
                    vi.caretColor = new Color(0.05f, 0.05f, 0.07f, 1f);
                    string k = key;
                    vi.onEndEdit.AddListener(delegate(string txt) { OnValueInput(k, txt); });
                    // Seed the box from its slider so it's never empty on first show.
                    if (s != null)
                        vi.text = s.value.ToString(FmtFor(key), CultureInfo.InvariantCulture);
                }
            }

            // Blendshape weight goes from -1000% to +1000%. The bundled window was built 0..100,
            // so the range is widened here rather than in the asset.
            Slider ws;
            if (sliders.TryGetValue("weight", out ws) && ws != null)
            {
                ws.minValue = -PoseApplier.BLEND_LIMIT;
                ws.maxValue = PoseApplier.BLEND_LIMIT;
            }

            BuildSections();   // every row exists and is in place by now
            PoseUtil.FastDropdownScroll(window, 40f);

            if (itemDropdown != null) itemDropdown.onValueChanged.AddListener(OnItemSelected);
            if (boneDropdown != null) boneDropdown.onValueChanged.AddListener(OnBoneTargetSelected);
            if (blendDropdown != null) blendDropdown.onValueChanged.AddListener(OnBlendSelected);
            if (keyframeDropdown != null) keyframeDropdown.onValueChanged.AddListener(OnKeyframeSelected);
            if (keyframeToggle != null) keyframeToggle.onValueChanged.AddListener(OnKeyframesToggled);
            if (waveDropdown != null)
            {
                suppressCallbacks = true;
                waveDropdown.ClearOptions();
                waveDropdown.AddOptions(new List<string>(WAVE_OPTIONS));
                suppressCallbacks = false;
                waveDropdown.onValueChanged.AddListener(OnWaveChanged);
            }
            if (nameInput != null)
            {
                nameInput.onEndEdit.AddListener(OnNameChanged);
                // Force readable dark text + no-truncation so the value renders (the
                // prefab's built-in InputField text can otherwise be clipped/invisible).
                StyleInputText(nameInput.textComponent, TextAnchor.MiddleLeft,
                    new Color(0.05f, 0.05f, 0.07f, 1f));
                StyleInputText(nameInput.placeholder as Text, TextAnchor.MiddleLeft,
                    new Color(0.4f, 0.4f, 0.4f, 0.6f));
                nameInput.customCaretColor = true;
                nameInput.caretColor = new Color(0.05f, 0.05f, 0.07f, 1f);
            }
            if (enabledToggle != null) enabledToggle.onValueChanged.AddListener(OnEnabledToggled);
            if (activeToggle != null) activeToggle.onValueChanged.AddListener(OnActiveToggled);
            if (idleToggle != null) idleToggle.onValueChanged.AddListener(OnIdleToggled);
            if (animToggle != null) animToggle.onValueChanged.AddListener(OnAnimToggled);
            if (usePosToggle != null) usePosToggle.onValueChanged.AddListener(OnUsePosToggled);
            if (useRotToggle != null) useRotToggle.onValueChanged.AddListener(OnUseRotToggled);
            if (useScaleToggle != null) useScaleToggle.onValueChanged.AddListener(OnUseScaleToggled);
            if (triggerToggle != null) triggerToggle.onValueChanged.AddListener(OnTriggerToggled);

            foreach (KeyValuePair<string, Slider> kv in sliders)
            {
                string key = kv.Key;
                kv.Value.onValueChanged.AddListener(delegate(float v) { OnSliderChanged(key, v); });
            }

            WireButton("Button_NewToggle", OnNewToggle);
            WireButton("Button_NewAnim", OnNewAnim);
            WireButton("Button_RemoveItem", OnRemoveItem);
            WireButton("Button_AssignHotkey", OnAssignHotkey);
            WireButton("Button_ClearHotkey", OnClearHotkey);
            WireButton("Button_PickTrigger", OnPickTrigger);
            WireButton("Button_ClearTrigger", OnClearTrigger);
            WireButton("Button_AddBone", OnAddBoneClicked);
            WireButton("Button_AddMesh", OnAddMeshClicked);
            WireButton("Button_HideMesh", OnHideMeshClicked);
            WireButton("Button_ImportAnim", OnImportAnimClicked);
            WireButton("Button_ExportPoses", OnExportPosesClicked);
            WireButton("Button_ImportPoses", OnImportPosesClicked);
            WireButton("Button_RemoveBone", OnRemoveBone);
            WireButton("Button_Timeline", delegate() { if (timeline != null) timeline.ToggleVisible(); });
            WireButton("Button_AddKey", OnAddKeyframe);
            WireButton("Button_RemoveKey", OnRemoveKeyframe);
            WireButton("Button_AddBlend", OnAddBlendClicked);
            WireButton("Button_RemoveBlend", OnRemoveBlend);
            WireButton("Button_Reload", OnReloadClicked);
            WireButton("Button_Save", OnSaveClicked);
            WireButton("Button_Close", OnCloseClicked);

            // IK goal editor controls.
            ikGoalDropdown = FindControl<Dropdown>("Dropdown_IKGoal");
            ikEnabledToggle = FindControl<Toggle>("Toggle_IKEnabled");
            ikHoldRotToggle = FindControl<Toggle>("Toggle_IKHoldRot");
            ikUpperInput = FindControl<InputField>("Input_IKUpper");
            ikLowerInput = FindControl<InputField>("Input_IKLower");
            ikEndInput = FindControl<InputField>("Input_IKEnd");
            ikSpaceInput = FindControl<InputField>("Input_IKSpace");
            ikWeightInput = FindControl<InputField>("Input_IKWeight");
            ikCaptureDropdown = FindControl<Dropdown>("Dropdown_IKCapture");
            StyleIKInput(ikUpperInput); StyleIKInput(ikLowerInput); StyleIKInput(ikEndInput);
            StyleIKInput(ikSpaceInput); StyleIKInput(ikWeightInput);
            if (ikGoalDropdown != null) ikGoalDropdown.onValueChanged.AddListener(OnIKSelected);
            if (ikEnabledToggle != null) ikEnabledToggle.onValueChanged.AddListener(OnIKEnabledChanged);
            if (ikHoldRotToggle != null) ikHoldRotToggle.onValueChanged.AddListener(OnIKHoldRotChanged);
            if (ikUpperInput != null) ikUpperInput.onEndEdit.AddListener(OnIKUpperChanged);
            if (ikLowerInput != null) ikLowerInput.onEndEdit.AddListener(OnIKLowerChanged);
            if (ikEndInput != null) ikEndInput.onEndEdit.AddListener(OnIKEndChanged);
            if (ikSpaceInput != null) ikSpaceInput.onEndEdit.AddListener(OnIKSpaceChanged);
            if (ikWeightInput != null) ikWeightInput.onEndEdit.AddListener(OnIKWeightChanged);
            if (ikCaptureDropdown != null)
            {
                suppressCallbacks = true;
                ikCaptureDropdown.ClearOptions();
                ikCaptureDropdown.AddOptions(new List<string> { "bind (rest pose)", "play (first frame)" });
                suppressCallbacks = false;
                ikCaptureDropdown.onValueChanged.AddListener(OnIKCaptureChanged);
            }
            WireButton("Button_AddIK", OnAddIK);
            WireButton("Button_RemoveIK", OnRemoveIK);

            RefreshItemList();
            window.SetActive(false);
        }

        void StyleIKInput(InputField f)
        {
            if (f == null) return;
            StyleInputText(f.textComponent, TextAnchor.MiddleLeft, new Color(0.05f, 0.05f, 0.07f, 1f));
            StyleInputText(f.placeholder as Text, TextAnchor.MiddleLeft, new Color(0.4f, 0.4f, 0.4f, 0.6f));
            f.customCaretColor = true;
            f.caretColor = new Color(0.05f, 0.05f, 0.07f, 1f);
        }

        // ---- collapsible sections -------------------------------------------------------
        // The panel is a flat list of absolutely placed rows, so a section is simply "everything
        // between one Hdr_* label and the next". Rows keep the positions they were built at; a
        // shut section just pulls what follows it upward.
        class UiSection
        {
            public string label;
            public Text hdr;
            public readonly List<RectTransform> rows = new List<RectTransform>();
            public readonly List<float> rowY = new List<float>();
            public float hdrY, height;
            public bool open;
            public string baseText = "";
        }

        readonly List<UiSection> sections = new List<UiSection>();
        RectTransform sectionContent;
        float sectionContentH;

        static string Marker(bool open) { return open ? "[-] " : "[+] "; }

        void BuildSections()
        {
            sections.Clear();
            Text first = FindControl<Text>("Hdr_Bone");
            if (first == null) first = FindControl<Text>("Hdr_Keyframes");
            if (first == null || first.transform.parent == null) return;
            Transform content = first.transform.parent;
            sectionContent = content as RectTransform;
            if (sectionContent != null) sectionContentH = sectionContent.sizeDelta.y;

            List<RectTransform> kids = new List<RectTransform>();
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform rt = content.GetChild(i) as RectTransform;
                if (rt != null) kids.Add(rt);
            }
            // visual order: anchoredPosition y runs negative downwards
            kids.Sort(delegate(RectTransform a, RectTransform b)
            { return b.anchoredPosition.y.CompareTo(a.anchoredPosition.y); });

            UiSection cur = null;
            for (int i = 0; i < kids.Count; i++)
            {
                RectTransform rt = kids[i];
                if (rt.name.StartsWith("Hdr_"))
                {
                    cur = new UiSection();
                    cur.label = rt.name.Substring(4);
                    cur.hdr = rt.GetComponent<Text>();
                    cur.hdrY = rt.anchoredPosition.y;
                    sections.Add(cur);
                    continue;
                }
                if (cur == null) continue;      // rows above the first header stay put
                cur.rows.Add(rt);
                cur.rowY.Add(rt.anchoredPosition.y);
            }

            for (int i = 0; i < sections.Count; i++)
            {
                UiSection sec = sections[i];
                float bottom = sec.hdrY;
                for (int r = 0; r < sec.rows.Count; r++)
                {
                    float b = sec.rowY[r] - sec.rows[r].rect.height;
                    if (b < bottom) bottom = b;
                }
                sec.height = sec.hdrY - bottom;
                if (sec.hdr == null) continue;

                UiSection captured = sec;
                Button btn = sec.hdr.gameObject.GetComponent<Button>();
                if (btn == null) btn = sec.hdr.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(delegate { ToggleSection(captured); });
                sec.baseText = sec.hdr.text;
                sec.hdr.text = Marker(sec.open) + sec.baseText;
            }
            RelayoutSections();
            Debug.Log("[PoseStudio] " + sections.Count + " foldout sections in the window");
        }

        void ToggleSection(UiSection sec)
        {
            sec.open = !sec.open;
            if (sec.hdr != null) sec.hdr.text = Marker(sec.open) + sec.baseText;
            RelayoutSections();
        }

        void RelayoutSections()
        {
            float shift = 0f;
            for (int i = 0; i < sections.Count; i++)
            {
                UiSection sec = sections[i];
                if (sec.hdr != null)
                {
                    RectTransform h = sec.hdr.rectTransform;
                    h.anchoredPosition = new Vector2(h.anchoredPosition.x, sec.hdrY + shift);
                }
                for (int r = 0; r < sec.rows.Count; r++)
                {
                    RectTransform rt = sec.rows[r];
                    if (rt == null) continue;
                    if (rt.gameObject.activeSelf != sec.open) rt.gameObject.SetActive(sec.open);
                    if (sec.open)
                        rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, sec.rowY[r] + shift);
                }
                if (!sec.open) shift += Mathf.Max(0f, sec.height - 24f);
            }
            if (sectionContent != null)
                sectionContent.sizeDelta = new Vector2(sectionContent.sizeDelta.x,
                    Mathf.Max(80f, sectionContentH - shift));
        }

        T FindControl<T>(string name) where T : Component
        {
            return FindIn<T>(window, name);
        }

        T FindIn<T>(GameObject root, string name) where T : Component
        {
            if (root == null) return null;
            Transform t = FindDeep(root.transform, name);
            if (t == null) return null;
            return t.GetComponent<T>();
        }

        // Make an InputField's text/placeholder render reliably: builtin font,
        // modest size, dark color, and crucially Overflow (not Truncate) so a line
        // is never clipped to nothing inside a short box. Also widens the text rect
        // to full height with small padding.
        static void StyleInputText(Text t, TextAnchor anchor, Color color)
        {
            if (t == null) return;
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f != null) t.font = f;
            t.fontSize = 12;
            t.alignment = anchor;
            t.color = color;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform trt = t.rectTransform;
            if (trt != null)
            {
                trt.anchorMin = new Vector2(0f, 0f);
                trt.anchorMax = new Vector2(1f, 1f);
                trt.offsetMin = new Vector2(6f, 0f);
                trt.offsetMax = new Vector2(-6f, 0f);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        void WireButton(string name, UnityEngine.Events.UnityAction action)
        {
            Button b = FindControl<Button>(name);
            if (b != null) b.onClick.AddListener(action);
        }

        // ========================= item selection =========================

        void RefreshItemList()
        {
            if (config == null) return;

            if (enabledToggle != null)
            {
                suppressCallbacks = true;
                enabledToggle.isOn = config.settings != null && config.settings.enabled;
                suppressCallbacks = false;
            }

            if (itemDropdown == null) return;

            List<string> opts = new List<string>();
            if (config.items != null)
                for (int i = 0; i < config.items.Count; i++)
                {
                    PoseItem it = config.items[i];
                    string nm = (it != null && !string.IsNullOrEmpty(it.name)) ? it.name : ("item " + i);
                    string tag = (it != null && it.type == "animation") ? "  (anim)" : "  (toggle)";
                    opts.Add(nm + tag);
                }

            suppressCallbacks = true;
            itemDropdown.ClearOptions();
            itemDropdown.AddOptions(opts);
            itemDropdown.value = 0;
            itemDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (opts.Count > 0) SelectItem(0);
            else { selectedItem = null; selectedBone = null; selectedBlend = null; listeningForHotkey = false; UpdateHotkeyLabel(); }
        }

        void OnItemSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectItem(index);
        }

        void SelectItem(int index)
        {
            if (config == null || config.items == null || index < 0 || index >= config.items.Count)
            {
                selectedItem = null;
                return;
            }
            selectedItem = config.items[index];
            selectedBone = null;
            selectedMesh = null;
            selectedBlend = null;
            selectedKey = null;
            PushItemToUI();
            RefreshBoneList();
            RefreshKeyframeList();
            RefreshBlendList();
            RefreshIKList();
        }

        void PushItemToUI()
        {
            if (selectedItem == null) return;
            suppressCallbacks = true;

            if (nameInput != null) nameInput.text = selectedItem.name == null ? "" : selectedItem.name;
            if (activeToggle != null) activeToggle.isOn = selectedItem.active;
            if (idleToggle != null) idleToggle.isOn = selectedItem.idleAnim;
            if (animToggle != null) animToggle.isOn = selectedItem.type == "animation";
            if (waveDropdown != null)
            {
                int wi = Array.IndexOf(WAVE_OPTIONS, selectedItem.waveform);
                if (wi < 0) wi = 0;
                waveDropdown.value = wi;
                waveDropdown.RefreshShownValue();
            }
            SetSlider("speed", selectedItem.speed);
            SetSlider("blend", selectedItem.blendTime);
            if (config.settings != null) SetSlider("idlewait", config.settings.idleDelay);

            if (keyframeToggle != null) keyframeToggle.isOn = selectedItem.useKeyframes;

            if (triggerToggle != null) triggerToggle.isOn = selectedItem.useTrigger;
            SetSlider("tcurve", selectedItem.triggerCurve);
            SetSlider("tscale", selectedItem.triggerScale);
            RefreshFitSliders();

            suppressCallbacks = false;

            UpdateTriggerLabel();
            listeningForHotkey = false;
            UpdateHotkeyLabel();
        }

        void OnNameChanged(string txt)
        {
            if (suppressCallbacks || selectedItem == null) return;
            if (string.IsNullOrEmpty(txt)) return;
            selectedItem.name = txt;
            int keep = itemDropdown != null ? itemDropdown.value : 0;
            RefreshItemListKeepIndex(keep);
            SetStatus("renamed to '" + txt + "'");
        }

        void RefreshItemListKeepIndex(int index)
        {
            RefreshItemList();
            if (itemDropdown != null && index >= 0 && index < itemDropdown.options.Count)
            {
                suppressCallbacks = true;
                itemDropdown.value = index;
                itemDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectItem(index);
            }
        }

        void OnEnabledToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (config != null && config.settings != null) config.settings.enabled = on;
            if (!on) { applier.Restore(); restoredWhileDisabled = true; }
        }

        void OnActiveToggled(bool on)
        {
            if (suppressCallbacks || selectedItem == null) return;
            selectedItem.active = on;
            // manual control of the idle item overrides the auto state
            if (selectedItem.idleAnim) { idleAutoOn = false; idleSuppressed = !on; }
        }

        void OnIdleToggled(bool on)
        {
            if (suppressCallbacks || selectedItem == null) return;
            if (on && selectedItem.type != "animation")
            {
                suppressCallbacks = true;
                if (idleToggle != null) idleToggle.isOn = false;
                suppressCallbacks = false;
                SetStatus("idle animation must be an animation item");
                return;
            }
            selectedItem.idleAnim = on;
            if (on)
            {
                // only one idle item at a time
                for (int i = 0; i < config.items.Count; i++)
                    if (config.items[i] != null && !ReferenceEquals(config.items[i], selectedItem))
                        config.items[i].idleAnim = false;
                SetStatus("'" + selectedItem.name + "' will auto-play when tracking is lost. Save to keep");
            }
            else SetStatus("idle animation cleared");
            RecomputeIdleCaches();
        }

        // ===== Idle animation engine =====
        // Auto-plays the item flagged `idleAnim` when tracking is lost and no other MAJOR
        // animation is active. Tracking loss = the head bone stops receiving fresh motion:
        // live face tracking jitters the head constantly; when the tracker disconnects the
        // animator's output freezes (or stops being written). We sample the head at the START
        // of LateUpdate (animator has written, we haven't), ignore frames where the value is
        // just our own last write, and time out after settings.idleDelay seconds.
        PoseItem idleItem;                       // cached: the flagged item
        readonly List<PoseItem> majorItems = new List<PoseItem>();  // cached: "major" animations
        Quaternion prevHeadSample = Quaternion.identity;
        Quaternion lastAppliedHead = Quaternion.identity;
        bool headSampleValid, lastAppliedValid;
        float lastMotionTime = -1f;
        bool idleAutoOn;                         // we turned the idle item on (so we may turn it off)
        bool idleSuppressed;                     // user manually stopped it; re-arm on next real motion

        void RecomputeIdleCaches()
        {
            idleItem = null;
            majorItems.Clear();
            if (config == null || config.items == null) return;
            for (int i = 0; i < config.items.Count; i++)
            {
                PoseItem it = config.items[i];
                if (it == null) continue;
                if (it.idleAnim && idleItem == null) idleItem = it;
                else if (IsMajorAnim(it)) majorItems.Add(it);
            }
        }

        // Major = keyframe animation with >2 keyframes driving >4 major humanoid bones
        // (fingers and toes excluded).
        bool IsMajorAnim(PoseItem it)
        {
            if (it == null || it.type != "animation") return false;
            if (!it.useKeyframes || it.keyframes == null || it.keyframes.Count <= 2) return false;
            if (it.bones == null || boundAnimator == null || !boundAnimator.isHuman) return false;
            HashSet<HumanBodyBones> majors = new HashSet<HumanBodyBones>();
            for (int b = 0; b < it.bones.Count; b++)
            {
                BoneTarget bt = it.bones[b];
                if (bt == null || string.IsNullOrEmpty(bt.bone)) continue;
                Transform tr = PoseUtil.Find(boundAvatar.transform, boundAnimator, bt.bone);
                if (tr == null) continue;
                HumanBodyBones hb;
                if (!humanBoneOf.TryGetValue(tr, out hb)) continue;
                int idx = (int)hb;
                if (hb == HumanBodyBones.LeftToes || hb == HumanBodyBones.RightToes) continue;
                if (idx >= (int)HumanBodyBones.LeftThumbProximal && idx <= (int)HumanBodyBones.RightLittleDistal) continue;
                majors.Add(hb);
            }
            return majors.Count > 4;
        }

        // reverse map transform -> humanoid bone, rebuilt when the avatar (re)binds
        readonly Dictionary<Transform, HumanBodyBones> humanBoneOf = new Dictionary<Transform, HumanBodyBones>();
        void RebuildHumanBoneMap()
        {
            humanBoneOf.Clear();
            if (boundAnimator == null || !boundAnimator.isHuman) return;
            for (int b = 0; b < (int)HumanBodyBones.LastBone; b++)
            {
                Transform t = boundAnimator.GetBoneTransform((HumanBodyBones)b);
                if (t != null && !humanBoneOf.ContainsKey(t)) humanBoneOf[t] = (HumanBodyBones)b;
            }
        }

        void UpdateIdle()
        {
            if (idleItem == null || boundAnimator == null || !boundAnimator.isHuman) return;
            Transform head = boundAnimator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) return;

            Quaternion cur = head.localRotation;

            // Was this frame's value written by something other than us last frame?
            bool externalWrite = !lastAppliedValid || Quaternion.Angle(cur, lastAppliedHead) > 0.0005f;
            if (externalWrite && headSampleValid)
            {
                float thresh = config.settings != null ? config.settings.idleMotionThreshold : 0.02f;
                if (Quaternion.Angle(cur, prevHeadSample) > thresh)
                {
                    lastMotionTime = Time.time;
                    idleSuppressed = false;      // real motion re-arms the auto behaviour
                }
            }
            if (externalWrite) { prevHeadSample = cur; headSampleValid = true; }
            else if (!headSampleValid) { prevHeadSample = cur; headSampleValid = true; }
            if (lastMotionTime < 0f) lastMotionTime = Time.time;   // assume alive at start

            float delay = config.settings != null ? config.settings.idleDelay : 1.5f;
            bool lost = (Time.time - lastMotionTime) > Mathf.Max(0.2f, delay);

            bool majorOn = false;
            for (int i = 0; i < majorItems.Count; i++)
                if (majorItems[i].active) { majorOn = true; break; }

            // user turned the idle item off while we had it on -> stand down until motion returns
            if (idleAutoOn && !idleItem.active) { idleAutoOn = false; idleSuppressed = true; }

            bool want = lost && !majorOn && !idleSuppressed;
            // Holding still for a moment while tracking is perfectly alive used to start the
            // idle animation for a frame or two before the next flicker of motion killed it,
            // read on screen as a twitch. Once it starts it now stays for a beat, and it has
            // to be quiet a good while longer than the delay before starting at all.
            if (want && !idleAutoOn && (Time.time - lastMotionTime) < Mathf.Max(0.2f, delay) + IDLE_CONFIRM)
                want = false;
            if (!want && idleAutoOn && (Time.time - idleOnSince) < IDLE_MIN_ON) want = true;

            if (want && !idleItem.active)
            {
                idleItem.active = true; idleAutoOn = true; idleOnSince = Time.time;
                if (ReferenceEquals(selectedItem, idleItem) && activeToggle != null)
                { suppressCallbacks = true; activeToggle.isOn = true; suppressCallbacks = false; }
            }
            else if (!want && idleItem.active && idleAutoOn)
            {
                idleItem.active = false; idleAutoOn = false;
                if (ReferenceEquals(selectedItem, idleItem) && activeToggle != null)
                { suppressCallbacks = true; activeToggle.isOn = false; suppressCallbacks = false; }
            }
        }

        // extra quiet time before the idle animation may start, and the shortest it may
        // stay once it does, together these stop the one-frame twitch
        const float IDLE_CONFIRM = 0.75f;
        const float IDLE_MIN_ON = 1.25f;
        float idleOnSince;

        void SampleAppliedHead()
        {
            if (boundAnimator == null || !boundAnimator.isHuman) { lastAppliedValid = false; return; }
            Transform head = boundAnimator.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) { lastAppliedValid = false; return; }
            lastAppliedHead = head.localRotation;
            lastAppliedValid = true;
        }

        void OnAnimToggled(bool on)
        {
            if (suppressCallbacks || selectedItem == null) return;
            selectedItem.type = on ? "animation" : "toggle";
            int keep = itemDropdown != null ? itemDropdown.value : 0;
            RefreshItemListKeepIndex(keep);
        }

        void OnWaveChanged(int index)
        {
            if (suppressCallbacks || selectedItem == null) return;
            if (index >= 0 && index < WAVE_OPTIONS.Length)
                selectedItem.waveform = WAVE_OPTIONS[index];
        }

        // ----- new / remove item -----

        void OnNewToggle() { CreateItem("toggle"); }
        void OnNewAnim() { CreateItem("animation"); }

        // ----- import animation (.vmd / .bvh / .vrma) -----
        void OnImportAnimClicked()
        {
            if (boundAvatar == null || boundAnimator == null) { SetStatus("load a humanoid avatar first"); return; }
            string path = NativeFileDialog.OpenFile("Import animation (MMD .vmd / .bvh / VRM .vrma)", null,
                "Animation (*.vmd;*.bvh;*.vrma;*.glb;*.gltf)", "*.vmd;*.bvh;*.vrma;*.glb;*.gltf");
            if (string.IsNullOrEmpty(path)) return;

            // MMD .vmd opens the live adjust dialog (orientation is finicky); others import directly.
            if (path.ToLowerInvariant().EndsWith(".vmd")) { StartMmdImport(path); return; }

            string report;
            PoseItem it = AnimImport.Import(path, boundAvatar, boundAnimator, out report);
            if (it == null) { SetStatus("import failed: " + report); return; }
            if (config == null) config = new PoseConfig();
            if (config.items == null) config.items = new List<PoseItem>();
            it.name = UniqueItemName(it.name);
            config.items.Add(it);

            Rebind();
            RefreshItemListKeepIndex(config.items.Count - 1);
            SetStatus(report + " Press Save to keep, then Activate to play.");
        }

        // ----- MMD (.vmd) live-adjust import -----
        AnimImport.VmdRaw mmdRaw;
        AnimImport.MmdOpts mmdOpts;
        int mmdIndex = -1;

        void StartMmdImport(string path)
        {
            string report;
            mmdRaw = AnimImport.ParseVmd(path, out report);
            if (mmdRaw == null || mmdRaw.mappedBones == 0)
            {
                SetStatus("MMD import failed: " + (mmdRaw == null ? report : "no known MMD bones found"
                    + (mmdRaw != null && !mmdRaw.sjOk ? " (Shift-JIS unavailable)" : "")));
                mmdRaw = null; return;
            }
            mmdOpts = new AnimImport.MmdOpts();
            if (config == null) config = new PoseConfig();
            if (config.items == null) config.items = new List<PoseItem>();
            PoseItem it = AnimImport.BuildVmd(mmdRaw, mmdOpts, boundAnimator);
            it.name = UniqueItemName(it.name); it.active = true;     // play it so the dialog previews live
            config.items.Add(it);
            mmdIndex = config.items.Count - 1;
            Rebind();
            RefreshItemListKeepIndex(mmdIndex);
            OpenBrowser("mmdadjust");
        }

        void RebuildMmdItem()
        {
            if (mmdRaw == null || mmdIndex < 0 || mmdIndex >= config.items.Count) return;
            string keepName = config.items[mmdIndex].name;
            PoseItem it = AnimImport.BuildVmd(mmdRaw, mmdOpts, boundAnimator);
            it.name = keepName; it.active = true;
            config.items[mmdIndex] = it;
            Rebind();
        }

        void BuildMmdAdjustTree(ref float y)
        {
            string[] presetNames = { "A · default", "B · flip X/Y", "C · flip Y/Z", "D · flip X/W", "E · raw" };
            GameObject h = NewRow("Mmd_Hdr", y);
            MakeRuntimeButton(h.transform, "Orientation preset: click each and watch, keep the one that looks right:",
                INDENT, 0f, 1000f, ROW_H, new Color(0,0,0,0), new Color(0.85f,0.9f,1f,1f), 12, delegate {});
            y += ROW_H + 4f;
            int count = AnimImport.MmdPresetCount;
            for (int i = 0; i < count && i < presetNames.Length; i++)
            {
                bool cur = mmdOpts.preset == i;
                int pi = i;
                GameObject row = NewRow("Mmd_P" + i, y);
                MakeRuntimeButton(row.transform, (cur ? "● " : "○ ") + presetNames[i], INDENT, 0f, 1000f, ROW_H + 2f,
                    cur ? new Color(0.25f,0.4f,0.5f,1f) : new Color(0.18f,0.18f,0.2f,1f),
                    new Color(0.95f,0.95f,0.95f,1f), 13, delegate { mmdOpts.preset = pi; RebuildMmdItem(); RebuildTree(); });
                y += ROW_H + 6f;
            }
            y += 4f;
            MmdToggleRow(ref y, "Mirror left / right", mmdOpts.mirror, delegate { mmdOpts.mirror = !mmdOpts.mirror; RebuildMmdItem(); RebuildTree(); });
            MmdToggleRow(ref y, "Face 180° (turned around)", mmdOpts.face180, delegate { mmdOpts.face180 = !mmdOpts.face180; RebuildMmdItem(); RebuildTree(); });
            MmdToggleRow(ref y, "Lay flat / stand up (±90° X)", mmdOpts.layFlat, delegate { mmdOpts.layFlat = !mmdOpts.layFlat; RebuildMmdItem(); RebuildTree(); });
            y += 8f;
            GameObject ap = NewRow("Mmd_Apply", y);
            MakeRuntimeButton(ap.transform, "✔  Keep this", INDENT, 0f, 480f, ROW_H + 4f,
                new Color(0.2f,0.45f,0.25f,1f), new Color(0.9f,1f,0.9f,1f), 13, delegate { ApplyMmd(); });
            MakeRuntimeButton(ap.transform, "✖  Cancel import", INDENT + 500f, 0f, 480f, ROW_H + 4f,
                new Color(0.45f,0.2f,0.2f,1f), new Color(1f,0.9f,0.9f,1f), 13, delegate { CancelMmd(); });
            y += ROW_H + 12f;
            SetBrowserStatus("mapped " + mmdRaw.mappedBones + " bones, " + mmdRaw.morphs.Count + " morphs ("
                + mmdRaw.duration.ToString("0.0") + "s)" + (mmdRaw.sjOk ? "" : " (Shift-JIS unavailable, JP names may be unmatched)"));
        }

        void MmdToggleRow(ref float y, string label, bool on, UnityEngine.Events.UnityAction act)
        {
            GameObject row = NewRow("Mmd_T_" + label, y);
            MakeRuntimeButton(row.transform, (on ? "☒ " : "☐ ") + label, INDENT, 0f, 1000f, ROW_H,
                new Color(0,0,0,0), on ? new Color(0.8f,1f,0.8f,1f) : new Color(0.85f,0.85f,0.85f,1f), 13, act);
            y += ROW_H + 4f;
        }

        void ApplyMmd()
        {
            if (mmdIndex >= 0 && mmdIndex < config.items.Count) config.items[mmdIndex].active = false;
            CloseBrowser();
            SetStatus("MMD animation imported. Press Save to keep, then Activate to play.");
            mmdRaw = null; mmdIndex = -1;
        }

        void CancelMmd()
        {
            if (mmdIndex >= 0 && mmdIndex < config.items.Count) config.items.RemoveAt(mmdIndex);
            mmdRaw = null; mmdIndex = -1;
            CloseBrowser();
            Rebind();
            RefreshItemList();
            SetStatus("MMD import cancelled");
        }

        void CreateItem(string type)
        {
            if (config == null) config = new PoseConfig();
            if (config.items == null) config.items = new List<PoseItem>();

            PoseItem it = new PoseItem();
            it.type = type;
            it.name = UniqueItemName(type == "animation" ? "Animation" : "Toggle");
            config.items.Add(it);

            Rebind();
            int idx = config.items.Count - 1;
            RefreshItemListKeepIndex(idx);
            SetStatus("added " + type + " '" + it.name + "': add bones/blendshapes, then Save");
        }

        void OnRemoveItem()
        {
            if (config == null || config.items == null || config.items.Count == 0)
            { SetStatus("nothing to remove"); return; }
            if (selectedItem == null || itemDropdown == null)
            { SetStatus("no item selected"); return; }

            int idx = config.items.IndexOf(selectedItem);
            if (idx < 0) idx = itemDropdown.value;
            if (idx < 0 || idx >= config.items.Count) { SetStatus("no item selected"); return; }

            string removed = selectedItem.name;
            config.items.RemoveAt(idx);
            Rebind();

            if (config.items.Count > 0)
                RefreshItemListKeepIndex(Mathf.Clamp(idx, 0, config.items.Count - 1));
            else
            { RefreshItemList(); selectedItem = null; }

            SetStatus("removed '" + removed + "'. Save to keep this change");
        }

        string UniqueItemName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "item";
            if (!ItemNameExists(baseName)) return baseName;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = baseName + " " + n;
                if (!ItemNameExists(candidate)) return candidate;
            }
            return baseName + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        bool ItemNameExists(string name)
        {
            if (config == null || config.items == null) return false;
            for (int i = 0; i < config.items.Count; i++)
            {
                PoseItem it = config.items[i];
                if (it != null && string.Equals(it.name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // ========================= bone targets =========================

        // The transform section edits one target at a time, either a bone or a mesh
        // object. Both are listed in the same dropdown (tagged [B] / [M]) and share the
        // Position / Rotation / Scale toggles and the pos/rot/scale sliders below.
        void RefreshBoneList()
        {
            if (boneDropdown == null) return;

            transformTargets.Clear();
            List<string> opts = new List<string>();
            if (selectedItem != null)
            {
                if (selectedItem.bones != null)
                    for (int i = 0; i < selectedItem.bones.Count; i++)
                    {
                        BoneTarget bt = selectedItem.bones[i];
                        string nm = (bt != null && !string.IsNullOrEmpty(bt.bone)) ? bt.bone : ("bone " + i);
                        transformTargets.Add(bt);
                        opts.Add("[B] " + nm);
                    }
                if (selectedItem.meshes != null)
                    for (int i = 0; i < selectedItem.meshes.Count; i++)
                    {
                        MeshTarget mt = selectedItem.meshes[i];
                        string nm = (mt != null && !string.IsNullOrEmpty(mt.mesh)) ? mt.mesh : ("mesh " + i);
                        transformTargets.Add(mt);
                        opts.Add("[M] " + nm);
                    }
            }
            // Whole-animation fit (per body group) is edited with the "Fit to this model"
            // sliders. Per-group rows used to be listed here too, but they were never wired to
            // the transform sliders (a keyframe edit wrote a channel with no id), so they're gone.

            if (opts.Count == 0) opts.Add("(no transforms)");

            suppressCallbacks = true;
            boneDropdown.ClearOptions();
            boneDropdown.AddOptions(opts);
            boneDropdown.value = 0;
            boneDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (transformTargets.Count > 0) SelectTransformTarget(0);
            else { selectedBone = null; selectedMesh = null; PushTransformToUI(); }
        }

        void OnBoneTargetSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectTransformTarget(index);
        }

        void SelectTransformTarget(int index)
        {
            selectedBone = null;
            selectedMesh = null;
            selectedFit = null;
            if (index >= 0 && index < transformTargets.Count)
            {
                object o = transformTargets[index];
                selectedBone = o as BoneTarget;
                selectedMesh = o as MeshTarget;
                selectedFit = o as GroupAdjust;
            }
            PushTransformToUI();
        }

        // The currently edited transform target via the shared interface (or null).
        ITransformTarget CurTarget()
        {
            if (selectedBone != null) return selectedBone;
            if (selectedMesh != null) return selectedMesh;
            if (selectedFit != null) return selectedFit;
            return null;
        }

        void SelectTransformInDropdown(int index)
        {
            if (boneDropdown == null || index < 0 || index >= boneDropdown.options.Count) return;
            suppressCallbacks = true;
            boneDropdown.value = index;
            boneDropdown.RefreshShownValue();
            suppressCallbacks = false;
            SelectTransformTarget(index);
        }

        // True when we should be editing the SELECTED keyframe's pose rather than the
        // target's single static pose (animation items with the keyframe timeline on and a
        // keyframe selected).
        bool KeyframeEditing()
        {
            return selectedItem != null && selectedItem.useKeyframes && selectedKey != null;
        }

        // Keyframe-channel id for the currently selected transform target (or null).
        string CurTargetChannelId()
        {
            if (selectedBone != null) return KeyChannels.BoneId(selectedBone.bone);
            if (selectedMesh != null) return KeyChannels.MeshId(selectedMesh.mesh);
            return null;
        }

        void PushTransformToUI()
        {
            suppressCallbacks = true;
            ITransformTarget t = CurTarget();
            // Use-flags always reflect the static target (they gate whether a channel is
            // applied at all, and are shared across keyframes).
            if (usePosToggle != null) usePosToggle.isOn = t != null && t.UsePosition;
            if (useRotToggle != null) useRotToggle.isOn = t != null && t.UseRotation;
            if (useScaleToggle != null) useScaleToggle.isOn = t != null && t.UseScale;

            Vector3 p = t != null ? PoseUtil.ToVector3(t.Pos, Vector3.zero) : Vector3.zero;
            Vector3 r = t != null ? PoseUtil.ToVector3(t.Rot, Vector3.zero) : Vector3.zero;
            Vector3 s = t != null ? PoseUtil.ToVector3(t.Scl, Vector3.one) : Vector3.one;

            // In keyframe mode the sliders show THIS keyframe's pose for this target. If the
            // keyframe doesn't yet have a channel for it, fall back to the static pose.
            if (KeyframeEditing() && t != null)
            {
                KeyframeChannel c = KeyChannels.Find(selectedKey, CurTargetChannelId());
                if (c != null)
                {
                    p = PoseUtil.ToVector3(c.position, Vector3.zero);
                    r = PoseUtil.ToVector3(c.rotation, Vector3.zero);
                    s = PoseUtil.ToVector3(c.scale, Vector3.one);
                }
            }

            SetSlider("posX", p.x); SetSlider("posY", p.y); SetSlider("posZ", p.z);
            SetSlider("rotX", r.x); SetSlider("rotY", r.y); SetSlider("rotZ", r.z);
            SetSlider("sclX", s.x); SetSlider("sclY", s.y); SetSlider("sclZ", s.z);
            suppressCallbacks = false;
        }

        void OnUsePosToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (selectedBone != null) selectedBone.usePosition = on;
            else if (selectedMesh != null) selectedMesh.usePosition = on;
        }
        void OnUseRotToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (selectedBone != null) selectedBone.useRotation = on;
            else if (selectedMesh != null) selectedMesh.useRotation = on;
        }
        void OnUseScaleToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (selectedBone != null) selectedBone.useScale = on;
            else if (selectedMesh != null) selectedMesh.useScale = on;
        }

        // ========================= blendshape trigger =========================
        //
        // A source blendshape's weight (0..100%) drives this item's strength continuously,
        // shaped by a response curve and an overall strength %. This replaces the on/off
        // "Activate" control while enabled, e.g. bind a mouth-open shape so the more the
        // user opens their mouth, the more a toggle/animation engages.

        void OnTriggerToggled(bool on)
        {
            if (suppressCallbacks || selectedItem == null) return;
            selectedItem.useTrigger = on;
            Rebind();
            UpdateTriggerLabel();
            if (on && string.IsNullOrEmpty(selectedItem.triggerShape))
                SetStatus("trigger on: click 'Pick Source…' to choose a driving blendshape");
            else if (!on)
                SetStatus("trigger off: '" + selectedItem.name + "' uses its Activate toggle/hotkey again");
        }

        void OnPickTrigger()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            OpenBrowser("trigger");
        }

        void OnClearTrigger()
        {
            if (selectedItem == null) return;
            selectedItem.triggerShape = "";
            selectedItem.triggerMesh = "";
            Rebind();
            UpdateTriggerLabel();
            SetStatus("cleared trigger source for '" + selectedItem.name + "'");
        }

        void OnTriggerSourcePicked(SkinnedMeshRenderer r, string shape)
        {
            if (r == null || string.IsNullOrEmpty(shape) || selectedItem == null) return;
            selectedItem.useTrigger = true;
            selectedItem.triggerMesh = r.name;
            selectedItem.triggerShape = shape;
            Rebind();

            suppressCallbacks = true;
            if (triggerToggle != null) triggerToggle.isOn = true;
            suppressCallbacks = false;

            UpdateTriggerLabel();
            SetBrowserStatus("trigger source set to '" + shape + "'");
            SetStatus("trigger: '" + shape + "' now drives '" + selectedItem.name + "'. Save to keep");
            CloseBrowser();
        }

        void UpdateTriggerLabel()
        {
            if (triggerSourceLabel == null) return;
            string s = selectedItem != null ? selectedItem.triggerShape : "";
            triggerSourceLabel.text = string.IsNullOrEmpty(s) ? "(no source)" : s;
        }

        // Removes whichever transform target is selected (a bone or a mesh).
        void OnRemoveBone()
        {
            if (selectedItem == null) { SetStatus("no transform to remove"); return; }

            if (selectedBone != null && selectedItem.bones != null)
            {
                int idx = selectedItem.bones.IndexOf(selectedBone);
                if (idx >= 0)
                {
                    string removed = selectedBone.bone;
                    selectedItem.bones.RemoveAt(idx);
                    Rebind();
                    RefreshBoneList();
                    SetStatus("removed bone '" + removed + "'. Save to keep this change");
                    return;
                }
            }
            if (selectedMesh != null && selectedItem.meshes != null)
            {
                int idx = selectedItem.meshes.IndexOf(selectedMesh);
                if (idx >= 0)
                {
                    string removed = selectedMesh.mesh;
                    selectedItem.meshes.RemoveAt(idx);
                    Rebind();
                    RefreshBoneList();
                    SetStatus("removed mesh '" + removed + "'. Save to keep this change");
                    return;
                }
            }
            SetStatus("no transform selected");
        }

        // ========================= keyframe timeline =========================
        //
        // Animation items can play through many keyframes instead of the
        // sine/triangle/pulse wave. Each keyframe holds its OWN full pose, distinct
        // bone/mesh offsets and blendshape weights, and the seconds until the next
        // keyframe (the last loops back to the first). When the timeline is on and a
        // keyframe is selected, the Transform and Blendshape sliders edit THAT keyframe's
        // pose; the applier interpolates between consecutive keyframes over the seconds.
        // Generous safety cap only (keeps the dropdown usable); effectively unlimited for
        // hand-authored dances. Configs loaded from JSON can hold as many as they like.
        const int MAX_KEYFRAMES = 240;

        // Build a keyframe whose channels capture the item's current static pose (so a new
        // timeline starts from a sensible pose the user can then vary per keyframe).
        PoseKeyframe NewKeyframeFromStatic(PoseItem it, float seconds)
        {
            PoseKeyframe k = new PoseKeyframe();
            k.seconds = seconds;
            if (it != null)
            {
                if (it.bones != null)
                    for (int i = 0; i < it.bones.Count; i++)
                    {
                        BoneTarget b = it.bones[i];
                        if (b == null || string.IsNullOrEmpty(b.bone)) continue;
                        KeyChannels.GetOrCreateTransform(k, KeyChannels.BoneId(b.bone), b.position, b.rotation, b.scale);
                    }
                if (it.meshes != null)
                    for (int i = 0; i < it.meshes.Count; i++)
                    {
                        MeshTarget m = it.meshes[i];
                        if (m == null || string.IsNullOrEmpty(m.mesh)) continue;
                        KeyChannels.GetOrCreateTransform(k, KeyChannels.MeshId(m.mesh), m.position, m.rotation, m.scale);
                    }
                if (it.blendshapes != null)
                    for (int i = 0; i < it.blendshapes.Count; i++)
                    {
                        BlendTarget b = it.blendshapes[i];
                        if (b == null || string.IsNullOrEmpty(b.shape)) continue;
                        KeyChannels.GetOrCreateBlend(k, KeyChannels.BlendId(b.mesh, b.shape), b.weight);
                    }
            }
            return k;
        }

        void OnKeyframesToggled(bool on)
        {
            if (suppressCallbacks || selectedItem == null) return;
            selectedItem.useKeyframes = on;
            if (on && (selectedItem.keyframes == null || selectedItem.keyframes.Count == 0))
            {
                // Seed two keyframes that both capture the current pose; the user then edits
                // each to a different pose to author the motion.
                selectedItem.keyframes = new List<PoseKeyframe>
                {
                    NewKeyframeFromStatic(selectedItem, 0.5f),
                    NewKeyframeFromStatic(selectedItem, 0.5f)
                };
            }
            Rebind();
            RefreshKeyframeList();
            SetStatus(on
                ? "keyframe timeline on for '" + selectedItem.name + "': pick a key, then pose it with the sliders"
                : "keyframe timeline off: '" + selectedItem.name + "' uses the wave again");
        }

        void RefreshKeyframeList()
        {
            if (keyframeDropdown == null) return;

            List<string> opts = new List<string>();
            if (selectedItem != null && selectedItem.keyframes != null)
                for (int i = 0; i < selectedItem.keyframes.Count; i++)
                {
                    PoseKeyframe k = selectedItem.keyframes[i];
                    string s = k != null ? k.seconds.ToString("0.000", CultureInfo.InvariantCulture) : "?";
                    opts.Add("Key " + (i + 1) + "  (" + s + "s to next)");
                }
            if (opts.Count == 0) opts.Add("(no keyframes)");

            suppressCallbacks = true;
            keyframeDropdown.ClearOptions();
            keyframeDropdown.AddOptions(opts);
            keyframeDropdown.value = 0;
            keyframeDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (selectedItem != null && selectedItem.keyframes != null && selectedItem.keyframes.Count > 0)
                SelectKeyframe(0);
            else { selectedKey = null; PushKeyframeToUI(); }
            if (timeline != null) timeline.OnKeysChanged();
        }

        void OnKeyframeSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectKeyframe(index);
        }

        void SelectKeyframe(int index)
        {
            if (selectedItem == null || selectedItem.keyframes == null ||
                index < 0 || index >= selectedItem.keyframes.Count)
            { selectedKey = null; PushKeyframeToUI(); return; }
            selectedKey = selectedItem.keyframes[index];
            if (timeline != null) timeline.OnPluginKeySelected(index);
            PushKeyframeToUI();
            // Selecting a keyframe re-poses the editor: the Transform and Blendshape sliders
            // now reflect THIS keyframe's stored pose.
            PushTransformToUI();
            PushBlendToUI();
        }

        void PushKeyframeToUI()
        {
            suppressCallbacks = true;
            SetSlider("kfsec", selectedKey != null ? selectedKey.seconds : 0f);
            suppressCallbacks = false;
        }

        void OnAddKeyframe()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            if (selectedItem.keyframes == null) selectedItem.keyframes = new List<PoseKeyframe>();
            if (selectedItem.keyframes.Count >= MAX_KEYFRAMES)
            { SetStatus("reached the " + MAX_KEYFRAMES + "-keyframe safety cap"); return; }

            // New keyframe starts as a copy of the currently selected keyframe's pose (or
            // the static pose if none yet), so the user tweaks from a known state.
            PoseKeyframe nk = new PoseKeyframe();
            nk.seconds = 0.5f;
            if (selectedKey != null) nk.channels = KeyChannels.CloneChannels(selectedKey.channels);
            else nk = NewKeyframeFromStatic(selectedItem, 0.5f);
            selectedItem.keyframes.Add(nk);

            if (!selectedItem.useKeyframes)
            {
                selectedItem.useKeyframes = true;
                suppressCallbacks = true;
                if (keyframeToggle != null) keyframeToggle.isOn = true;
                suppressCallbacks = false;
            }
            Rebind();
            RefreshKeyframeList();
            int last = selectedItem.keyframes.Count - 1;
            if (keyframeDropdown != null && last >= 0 && last < keyframeDropdown.options.Count)
            {
                suppressCallbacks = true;
                keyframeDropdown.value = last;
                keyframeDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectKeyframe(last);
            }
            SetStatus("added keyframe " + (last + 1) + ": pose it with the sliders, then Save");
        }

        void OnRemoveKeyframe()
        {
            if (selectedItem == null || selectedItem.keyframes == null || selectedItem.keyframes.Count == 0)
            { SetStatus("no keyframe to remove"); return; }
            if (selectedKey == null) { SetStatus("no keyframe selected"); return; }

            int idx = selectedItem.keyframes.IndexOf(selectedKey);
            if (idx < 0) idx = keyframeDropdown != null ? keyframeDropdown.value : -1;
            if (idx < 0 || idx >= selectedItem.keyframes.Count) { SetStatus("no keyframe selected"); return; }

            selectedItem.keyframes.RemoveAt(idx);
            Rebind();
            RefreshKeyframeList();
            SetStatus("removed keyframe " + (idx + 1) + ". Save to keep this change");
        }

        // ========================= blendshape targets =========================

        void RefreshBlendList()
        {
            if (blendDropdown == null) return;

            List<string> opts = new List<string>();
            if (selectedItem != null && selectedItem.blendshapes != null)
                for (int i = 0; i < selectedItem.blendshapes.Count; i++)
                {
                    BlendTarget bt = selectedItem.blendshapes[i];
                    string nm = (bt != null && !string.IsNullOrEmpty(bt.shape)) ? bt.shape : ("shape " + i);
                    opts.Add(bt != null && bt.IsClip ? "[VRM] " + nm : nm);
                }
            if (opts.Count == 0) opts.Add("(no blendshapes)");

            suppressCallbacks = true;
            blendDropdown.ClearOptions();
            blendDropdown.AddOptions(opts);
            blendDropdown.value = 0;
            blendDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (selectedItem != null && selectedItem.blendshapes != null && selectedItem.blendshapes.Count > 0)
                SelectBlendTarget(0);
            else { selectedBlend = null; PushBlendToUI(); }
        }

        void OnBlendSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectBlendTarget(index);
        }

        void SelectBlendTarget(int index)
        {
            if (selectedItem == null || selectedItem.blendshapes == null ||
                index < 0 || index >= selectedItem.blendshapes.Count)
            { selectedBlend = null; PushBlendToUI(); return; }
            selectedBlend = selectedItem.blendshapes[index];
            PushBlendToUI();
        }

        void PushBlendToUI()
        {
            suppressCallbacks = true;
            float w = selectedBlend != null ? selectedBlend.weight : 0f;
            // In keyframe mode the weight slider shows THIS keyframe's stored weight for the
            // selected shape (falling back to the static weight if not yet authored here).
            if (KeyframeEditing() && selectedBlend != null)
            {
                KeyframeChannel c = KeyChannels.Find(selectedKey, KeyChannels.BlendId(selectedBlend.mesh, selectedBlend.shape));
                if (c != null) w = c.weight;
            }
            SetSlider("weight", w);
            suppressCallbacks = false;
        }

        void OnRemoveBlend()
        {
            if (selectedItem == null || selectedItem.blendshapes == null || selectedItem.blendshapes.Count == 0)
            { SetStatus("no blendshape to remove"); return; }
            if (selectedBlend == null) { SetStatus("no blendshape selected"); return; }

            int idx = selectedItem.blendshapes.IndexOf(selectedBlend);
            if (idx < 0) idx = blendDropdown != null ? blendDropdown.value : -1;
            if (idx < 0 || idx >= selectedItem.blendshapes.Count) { SetStatus("no blendshape selected"); return; }

            string removed = selectedBlend.shape;
            selectedItem.blendshapes.RemoveAt(idx);
            Rebind();
            RefreshBlendList();
            SetStatus("removed blendshape '" + removed + "'. Save to keep this change");
        }

        // ========================= sliders =========================

        // The window's layout is baked into the asset bundle, so the fit rows are made by cloning
        // a row that is already there (label, slider, value box) and dropping the copies under the
        // last one. That way a new control needs no Unity rebuild.
        void EnsureFitRows()
        {
            if (fitRowsBuilt) return;
            Text lbl = FindControl<Text>("Lbl_tscale");
            Slider sl = FindControl<Slider>("Slider_tscale");
            InputField inp = FindControl<InputField>("Input_tscale");
            if (lbl == null || sl == null || inp == null) return;
            Transform content = sl.transform.parent;
            if (content == null) return;

            float bottom = 0f;
            for (int i = 0; i < content.childCount; i++)
            {
                RectTransform r = content.GetChild(i) as RectTransform;
                if (r != null && r.anchoredPosition.y < bottom) bottom = r.anchoredPosition.y;
            }

            RectTransform lr = lbl.transform as RectTransform;
            RectTransform sr = sl.transform as RectTransform;
            RectTransform ir = inp.transform as RectTransform;
            float row = 32f;
            float y = bottom - row;

            Text hdr = Instantiate(lbl, content);
            hdr.name = "Hdr_Fit";
            hdr.text = "Fit to this model";
            hdr.fontStyle = FontStyle.Bold;
            RectTransform hr = hdr.transform as RectTransform;
            if (hr != null && lr != null)
            {
                hr.anchoredPosition = new Vector2(lr.anchoredPosition.x, y);
                hr.sizeDelta = new Vector2(260f, lr.sizeDelta.y);
            }
            y -= row;

            for (int i = 0; i < FIT_ROWS.Length; i++)
            {
                string key = FIT_ROWS[i][0];

                Text nl = Instantiate(lbl, content);
                nl.name = "Lbl_" + key;
                nl.text = FIT_ROWS[i][1];
                RectTransform nlr = nl.transform as RectTransform;
                if (nlr != null && lr != null)
                {
                    nlr.anchoredPosition = new Vector2(lr.anchoredPosition.x, y);
                    nlr.sizeDelta = new Vector2(130f, lr.sizeDelta.y);
                }

                Slider ns = Instantiate(sl, content);
                ns.name = "Slider_" + key;
                ns.onValueChanged.RemoveAllListeners();
                ns.minValue = 0f;
                // A push in metres, either way, so the middle of the slider is "leave it alone".
                float span = float.Parse(FIT_ROWS[i][4], CultureInfo.InvariantCulture);
                ns.minValue = -span;
                ns.maxValue = span;
                ns.value = 0f;
                RectTransform nsr = ns.transform as RectTransform;
                if (nsr != null && sr != null)
                    nsr.anchoredPosition = new Vector2(sr.anchoredPosition.x + 26f, y + 6f);

                InputField ni = Instantiate(inp, content);
                ni.name = "Input_" + key;
                ni.onEndEdit.RemoveAllListeners();
                ni.onValueChanged.RemoveAllListeners();
                ni.text = "1.00";
                RectTransform nir = ni.transform as RectTransform;
                if (nir != null && ir != null)
                    nir.anchoredPosition = new Vector2(ir.anchoredPosition.x, y + 2f);

                y -= row;
            }

            RectTransform crt = content as RectTransform;
            if (crt != null)
                crt.sizeDelta = new Vector2(crt.sizeDelta.x,
                    crt.sizeDelta.y + row * (FIT_ROWS.Length + 1));

            fitRowsBuilt = true;
            Debug.Log("[PoseStudio] fit sliders added to the window");
        }

        static int FitRowIndex(string key)
        {
            for (int i = 0; i < FIT_ROWS.Length; i++)
                if (FIT_ROWS[i][0] == key) return i;
            return -1;
        }

        // The group entry a fit row drives, made on demand.
        GroupAdjust FitGroup(string group, bool create)
        {
            if (selectedItem == null) return null;
            if (selectedItem.fit == null)
            {
                if (!create) return null;
                selectedItem.fit = new List<GroupAdjust>();
            }
            for (int i = 0; i < selectedItem.fit.Count; i++)
                if (selectedItem.fit[i] != null && selectedItem.fit[i].group == group)
                    return selectedItem.fit[i];
            if (!create) return null;
            GroupAdjust g = new GroupAdjust();
            g.group = group;
            selectedItem.fit.Add(g);
            return g;
        }

        static int AxisIndex(string axis)
        {
            return axis == "x" ? 0 : (axis == "y" ? 1 : 2);
        }

        void RefreshFitSliders()
        {
            for (int i = 0; i < FIT_ROWS.Length; i++)
            {
                GroupAdjust g = FitGroup(FIT_ROWS[i][2], false);
                float v = 0f;
                if (g != null && g.position != null)
                {
                    int ax = AxisIndex(FIT_ROWS[i][3]);
                    if (ax < g.position.Length) v = g.position[ax];
                }
                SetSlider(FIT_ROWS[i][0], v);
            }
        }

        void SetSlider(string key, float value)
        {
            Slider s;
            if (sliders.TryGetValue(key, out s) && s != null) s.value = value;
            UpdateValueLabel(key, value);
        }

        void UpdateValueLabel(string key, float value)
        {
            InputField inp;
            if (valueInputs.TryGetValue(key, out inp) && inp != null && !inp.isFocused)
                inp.text = value.ToString(FmtFor(key), CultureInfo.InvariantCulture);
        }

        // Keyframe seconds get 3-decimal precision; everything else uses 2 decimals.
        static string FmtFor(string key)
        {
            return key == "kfsec" ? "0.000" : "0.00";
        }

        // User typed a number into the box next to a slider. Parse, clamp to the
        // slider's range, and push it through the slider so all the normal
        // OnSliderChanged data-writing happens.
        void OnValueInput(string key, string txt)
        {
            if (suppressCallbacks) return;
            Slider s;
            if (!sliders.TryGetValue(key, out s) || s == null) return;

            float v;
            if (!float.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                // Restore the displayed value to the slider's current value.
                InputField inp;
                if (valueInputs.TryGetValue(key, out inp) && inp != null)
                    inp.text = s.value.ToString(FmtFor(key), CultureInfo.InvariantCulture);
                return;
            }

            v = Mathf.Clamp(v, s.minValue, s.maxValue);
            s.value = v; // fires OnSliderChanged → writes data + UpdateValueLabel
            // Normalize the box text (e.g. clamped or reformatted).
            InputField box;
            if (valueInputs.TryGetValue(key, out box) && box != null)
                box.text = v.ToString(FmtFor(key), CultureInfo.InvariantCulture);
        }

        void OnSliderChanged(string key, float v)
        {
            if (suppressCallbacks) return;
            UpdateValueLabel(key, v);

            // Item-level sliders.
            if (key == "idlewait") { if (config.settings != null) config.settings.idleDelay = v; return; }
            if (key == "speed") { if (selectedItem != null) selectedItem.speed = v; return; }
            if (key == "blend") { if (selectedItem != null) selectedItem.blendTime = v; return; }
            if (key == "tcurve") { if (selectedItem != null) selectedItem.triggerCurve = v; return; }
            if (key == "tscale") { if (selectedItem != null) selectedItem.triggerScale = v; return; }
            if (key.StartsWith("fit"))
            {
                int row = FitRowIndex(key);
                if (row < 0) return;
                GroupAdjust g = FitGroup(FIT_ROWS[row][2], true);
                if (g == null) return;
                if (g.position == null || g.position.Length < 3) g.position = new float[3];
                g.position[AxisIndex(FIT_ROWS[row][3])] = v;
                Rebind();                 // the fit is worked out when an item binds
                SetStatus(FIT_ROWS[row][1] + " " + v.ToString("0.00") + " m");
                return;
            }

            // Keyframe seconds-to-next.
            if (key == "kfsec") { if (selectedKey != null) { selectedKey.seconds = v; RefreshKeyframeOptionText(); } return; }

            // Blendshape weight, to the selected keyframe's channel when posing a keyframe,
            // otherwise to the static target.
            if (key == "weight")
            {
                if (KeyframeEditing() && selectedBlend != null)
                {
                    KeyframeChannel c = KeyChannels.GetOrCreateBlend(selectedKey,
                        KeyChannels.BlendId(selectedBlend.mesh, selectedBlend.shape), selectedBlend.weight);
                    c.weight = v;
                }
                else if (selectedBlend != null) selectedBlend.weight = v;
                return;
            }

            // Transform sliders, write to whichever transform target (bone or mesh) is
            // active, or to that target's channel in the selected keyframe when posing one.
            float[] pos, rot, scl;
            if (KeyframeEditing() && CurTarget() != null)
            {
                ITransformTarget tg = CurTarget();
                KeyframeChannel c = KeyChannels.GetOrCreateTransform(selectedKey,
                    CurTargetChannelId(), tg.Pos, tg.Rot, tg.Scl);
                pos = c.position; rot = c.rotation; scl = c.scale; // already length-3
            }
            else if (selectedBone != null)
            {
                EnsureArrays(selectedBone.position, selectedBone.rotation, selectedBone.scale,
                    out pos, out rot, out scl);
                selectedBone.position = pos; selectedBone.rotation = rot; selectedBone.scale = scl;
            }
            else if (selectedMesh != null)
            {
                EnsureArrays(selectedMesh.position, selectedMesh.rotation, selectedMesh.scale,
                    out pos, out rot, out scl);
                selectedMesh.position = pos; selectedMesh.rotation = rot; selectedMesh.scale = scl;
            }
            else return;

            switch (key)
            {
                case "posX": pos[0] = v; break;
                case "posY": pos[1] = v; break;
                case "posZ": pos[2] = v; break;
                case "rotX": rot[0] = v; break;
                case "rotY": rot[1] = v; break;
                case "rotZ": rot[2] = v; break;
                case "sclX": scl[0] = v; break;
                case "sclY": scl[1] = v; break;
                case "sclZ": scl[2] = v; break;
            }
        }

        static void EnsureArrays(float[] p, float[] r, float[] s,
            out float[] pos, out float[] rot, out float[] scl)
        {
            pos = (p != null && p.Length >= 3) ? p : new float[] { 0f, 0f, 0f };
            rot = (r != null && r.Length >= 3) ? r : new float[] { 0f, 0f, 0f };
            scl = (s != null && s.Length >= 3) ? s : new float[] { 1f, 1f, 1f };
        }

        // Refresh the keyframe dropdown labels in place (they show value/seconds) without
        // disturbing the current selection.
        void RefreshKeyframeOptionText()
        {
            if (keyframeDropdown == null || selectedItem == null || selectedItem.keyframes == null) return;
            int cur = keyframeDropdown.value;
            suppressCallbacks = true;
            for (int i = 0; i < keyframeDropdown.options.Count && i < selectedItem.keyframes.Count; i++)
            {
                PoseKeyframe k = selectedItem.keyframes[i];
                string s = k != null ? k.seconds.ToString("0.000", CultureInfo.InvariantCulture) : "?";
                keyframeDropdown.options[i].text = "Key " + (i + 1) + "  (" + s + "s to next)";
            }
            keyframeDropdown.value = cur;
            keyframeDropdown.RefreshShownValue();
            suppressCallbacks = false;
        }

        // ========================= buttons =========================

        void OnReloadClicked()
        {
            // Reload throws away unsaved edits, so keep them first: Restore unsaved undoes it.
            bool kept = safetyDirty;
            if (kept) WriteSafetyCopy("before Reload");
            LoadConfig();
            Rebind();
            RefreshItemList();
            SetStatus("Reloaded " + Path.GetFileName(configPath));
            if (kept) ShowRestoreButton(System.DateTime.Now);
        }

        void OnSaveClicked() { SaveConfig(); }

        void OnCloseClicked() { if (window != null) window.SetActive(false); }

        void SetStatus(string msg)
        {
            if (statusLabel != null) statusLabel.text = msg;
            Debug.Log("[PoseStudio] " + msg);
        }

        // ========================= hotkeys =========================
        //
        // Each PoseItem can carry a mono or combo hotkey (e.g. "F8" or "Ctrl+Shift+E").
        // Pressing it toggles that item's `active` flag, exactly like flipping its
        // "Activate this item" checkbox, so it works for toggles and animations alike,
        // and works whether or not the Pose Studio window is open.

        // Keys never accepted as the "main" key of a binding (modifiers, mouse, cancel).
        static readonly KeyCode[] HOTKEY_IGNORE =
        {
            KeyCode.None, KeyCode.Escape,
            KeyCode.LeftControl, KeyCode.RightControl,
            KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr,
            KeyCode.LeftCommand, KeyCode.RightCommand,
            KeyCode.LeftWindows, KeyCode.RightWindows,
            KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Mouse2,
            KeyCode.Mouse3, KeyCode.Mouse4, KeyCode.Mouse5, KeyCode.Mouse6
        };

        float lastConfigPoll;
        System.DateTime configStamp = System.DateTime.MinValue;
        System.DateTime ourWrite = System.DateTime.MinValue;

        // ---- safety copy of unsaved work -------------------------------------------------
        // Edits only reach posestudio.json when Save is pressed. If VNyan closes or the PC
        // crashes first they are gone, so anything unsaved is also copied to
        // posestudio.unsaved.json beside it: a minute after an edit (once mouse and keys have
        // been still for a moment, since copying a 30 MB config takes a beat), when the editor
        // windows are closed, before a reload, and when VNyan quits. posestudio.json itself is
        // never written by any of this. Next start, a "Restore unsaved" button offers it back.
        const string SAFETY_FILE = "posestudio.unsaved.json";
        bool safetyDirty;              // something may have changed since the last load, save or copy
        float safetyDirtySince;
        float lastInputTime;
        bool editorsWereOpen;
        GameObject restoreButton;

        string SafetyPath()
        {
            string basis = !string.IsNullOrEmpty(configPath) ? configPath : savePath;
            string dir = string.IsNullOrEmpty(basis) ? Application.persistentDataPath : Path.GetDirectoryName(basis);
            return Path.Combine(dir, SAFETY_FILE);
        }

        bool EditorsOpen()
        {
            return (window != null && window.activeSelf)
                || (browserWindow != null && browserWindow.activeSelf)
                || (timeline != null && timeline.IsOpen);
        }

        // Every edit takes a click or a key press while an editor window is open, so that counts
        // as a possible change. Far cheaper than comparing a 30 MB config every frame.
        void SafetyTick()
        {
            bool open = EditorsOpen();
            bool press = Input.anyKeyDown || Input.GetMouseButton(0);
            if (press || Input.mouseScrollDelta.y != 0f) lastInputTime = Time.unscaledTime;
            if (open && press && !safetyDirty) { safetyDirty = true; safetyDirtySince = Time.unscaledTime; }

            if (safetyDirty)
            {
                if (editorsWereOpen && !open) WriteSafetyCopy("editor closed");
                else if (Time.unscaledTime - safetyDirtySince > 60f && Time.unscaledTime - lastInputTime > 2f)
                    WriteSafetyCopy("autosave");
            }
            editorsWereOpen = open;
        }

        void OnApplicationQuit()
        {
            if (safetyDirty) WriteSafetyCopy("VNyan closing");
        }

        void WriteSafetyCopy(string why)
        {
            safetyDirty = false;          // cleared first, so a failing disk is not retried every frame
            if (config == null) return;
            string path = SafetyPath();
            string tmp = path + ".tmp";
            try
            {
                float t0 = Time.realtimeSinceStartup;
                // Streamed straight to disk, same output as SaveConfig, without a 30 MB string.
                using (StreamWriter sw = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
                using (JsonTextWriter jw = new JsonTextWriter(sw))
                {
                    jw.Formatting = Formatting.None;
                    JsonSerializer.CreateDefault().Serialize(jw, config);
                }
                // Swapped in whole, the copy before it kept as .prev: a crash on this PC can leave
                // the newest file zero-filled, and then the one before it is still good.
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, path + ".prev"); }
                    catch { File.Copy(path, path + ".prev", true); File.Delete(path); File.Move(tmp, path); }
                }
                else File.Move(tmp, path);
                Debug.Log("[PoseStudio] unsaved work copied to " + path + " (" + why + ", "
                          + ((Time.realtimeSinceStartup - t0) * 1000f).ToString("0") + " ms)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PoseStudio] could not keep a safety copy: " + e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // At start: offer last session's unsaved work back if it is newer than the saved config
        // and actually different from it.
        void CheckSafetyCopy()
        {
            try
            {
                string path = SafetyPath();
                if (!File.Exists(path)) return;
                if (File.Exists(configPath))
                {
                    if (File.GetLastWriteTimeUtc(path) <= File.GetLastWriteTimeUtc(configPath)) return;   // saved since
                    if (SameBytes(path, configPath)) return;
                }
                ShowRestoreButton(File.GetLastWriteTime(path));
            }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] checking for unsaved work: " + e.Message); }
        }

        static bool SameBytes(string a, string b)
        {
            FileInfo fa = new FileInfo(a), fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            byte[] ba = new byte[1 << 20], bb = new byte[1 << 20];
            using (FileStream sa = fa.OpenRead())
            using (FileStream sb = fb.OpenRead())
            {
                while (true)
                {
                    int na = sa.Read(ba, 0, ba.Length);
                    int nb = sb.Read(bb, 0, na);
                    if (na != nb) return false;
                    if (na == 0) return true;
                    for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
                }
            }
        }

        void ShowRestoreButton(System.DateTime when)
        {
            if (restoreButton == null) restoreButton = MakeRestoreButton();
            if (restoreButton != null)
            {
                restoreButton.SetActive(true);
                SetStatus("Unsaved work from " + when.ToString("MMM d, h:mm tt")
                          + " was kept safe. Press Restore unsaved to bring it back.");
            }
            else
                SetStatus("Unsaved work from " + when.ToString("MMM d, h:mm tt") + " was kept in " + SafetyPath());
        }

        // A copy of the Save button, placed in the first free spot around it.
        GameObject MakeRestoreButton()
        {
            Button save = FindControl<Button>("Button_Save");
            if (save == null) return null;
            RectTransform srt = save.GetComponent<RectTransform>();
            GameObject go = Instantiate(save.gameObject, srt.parent, false);
            go.name = "Button_RestoreUnsaved";
            Button b = go.GetComponent<Button>();
            b.onClick = new Button.ButtonClickedEvent();
            b.onClick.AddListener(OnRestoreUnsaved);
            Text label = go.GetComponentInChildren<Text>(true);
            if (label != null)
            {
                label.text = "Restore unsaved";
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 8;
                label.resizeTextMaxSize = Mathf.Max(label.fontSize, 9);
            }

            RectTransform rt = go.GetComponent<RectTransform>();
            float w = srt.rect.width, h = srt.rect.height;
            float nw = w;
            if (Mathf.Approximately(rt.anchorMin.x, rt.anchorMax.x))
            {
                nw = Mathf.Max(w, 120f);
                rt.sizeDelta = new Vector2(nw, rt.sizeDelta.y);
            }
            Vector2 p = srt.anchoredPosition, pv = srt.pivot;
            const float GAP = 6f;
            Vector2[] spots =
            {
                new Vector2(p.x - pv.x * w - GAP - (1f - pv.x) * nw, p.y),      // left of Save
                new Vector2(p.x, p.y + h + GAP),                                  // above
                new Vector2(p.x + (1f - pv.x) * w + GAP + pv.x * nw, p.y),      // right
                new Vector2(p.x, p.y - h - GAP),                                  // below
            };
            string[] names = { "left of", "above", "right of", "below" };
            int pick = -1;
            for (int i = 0; i < spots.Length && pick < 0; i++)
            {
                rt.anchoredPosition = spots[i];
                if (FitsFree(rt)) pick = i;
            }
            if (pick < 0) { pick = 0; rt.anchoredPosition = spots[0]; }
            Debug.Log("[PoseStudio] Restore unsaved button placed " + names[pick] + " Save");
            go.SetActive(false);
            return go;
        }

        // Inside its parent and not on top of any visible sibling.
        static bool FitsFree(RectTransform rt)
        {
            Rect me = WorldRect(rt);
            RectTransform parent = rt.parent as RectTransform;
            if (parent != null)
            {
                Rect pr = WorldRect(parent);
                if (me.xMin < pr.xMin - 0.5f || me.xMax > pr.xMax + 0.5f || me.yMin < pr.yMin - 0.5f || me.yMax > pr.yMax + 0.5f)
                    return false;
            }
            Transform par = rt.parent;
            for (int i = 0; i < par.childCount; i++)
            {
                RectTransform o = par.GetChild(i) as RectTransform;
                if (o == null || o == rt || !o.gameObject.activeSelf) continue;
                Rect or = WorldRect(o);
                float m = 1f;
                if (me.xMin + m < or.xMax && me.xMax - m > or.xMin && me.yMin + m < or.yMax && me.yMax - m > or.yMin)
                    return false;
            }
            return true;
        }

        static Rect WorldRect(RectTransform t)
        {
            Vector3[] c = new Vector3[4];
            t.GetWorldCorners(c);
            return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
        }

        void OnRestoreUnsaved()
        {
            string path = SafetyPath();
            PoseConfig got = null;
            string from = null;
            string[] tries = { path, path + ".prev" };
            for (int i = 0; i < tries.Length && got == null; i++)
            {
                try
                {
                    if (!File.Exists(tries[i])) continue;
                    got = ParseConfigJson(File.ReadAllText(tries[i]));
                    if (got != null) from = tries[i];
                }
                catch (Exception e) { Debug.LogWarning("[PoseStudio] could not read " + tries[i] + ": " + e.Message); }
            }
            if (got == null) { SetStatus("couldn't read the unsaved copy at " + path); return; }

            config = got;
            MergeBundled();
            Rebind();
            RefreshItemList();
            safetyDirty = false;          // it is still on disk, exactly as restored
            if (restoreButton != null) restoreButton.SetActive(false);
            SetStatus("Restored your unsaved work from " + File.GetLastWriteTime(from).ToString("MMM d, h:mm tt")
                      + ". Press Save to keep it.");
        }

        // ---- post-LateUpdate step --------------------------------------------------------
        // A player-loop step at the very start of PostLateUpdate: after EVERY script's LateUpdate
        // whatever its execution order (VNyan's expression pass included), before skinning and
        // rendering. Blendshapes written here are the ones that get drawn.
        public struct PoseStudioPostLate { }
        static PoseApplier postLateApplier;
        static bool postLateFailed;

        void InstallPostLate()
        {
            postLateApplier = applier;
            try
            {
                UnityEngine.LowLevel.PlayerLoopSystem loop = UnityEngine.LowLevel.PlayerLoop.GetCurrentPlayerLoop();
                for (int i = 0; i < loop.subSystemList.Length; i++)
                {
                    if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.PostLateUpdate)) continue;
                    UnityEngine.LowLevel.PlayerLoopSystem sys = loop.subSystemList[i];
                    List<UnityEngine.LowLevel.PlayerLoopSystem> subs =
                        new List<UnityEngine.LowLevel.PlayerLoopSystem>(sys.subSystemList);
                    for (int k = 0; k < subs.Count; k++)
                        if (subs[k].type == typeof(PoseStudioPostLate)) return;    // already in
                    UnityEngine.LowLevel.PlayerLoopSystem mine = new UnityEngine.LowLevel.PlayerLoopSystem();
                    mine.type = typeof(PoseStudioPostLate);
                    mine.updateDelegate = PostLateTick;
                    subs.Insert(0, mine);
                    sys.subSystemList = subs.ToArray();
                    loop.subSystemList[i] = sys;
                    UnityEngine.LowLevel.PlayerLoop.SetPlayerLoop(loop);
                    Debug.Log("[PoseStudio] blendshapes now applied after VNyan's expressions (post-LateUpdate)");
                    return;
                }
                Debug.LogWarning("[PoseStudio] PostLateUpdate not found, blendshapes fall back to LateUpdate");
            }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] post-LateUpdate hook failed: " + e.Message); }
            postLateFailed = true;
            PoseApplier.lateBlendFallback = true;
        }

        static void PostLateTick()
        {
            try { if (postLateApplier != null) postLateApplier.ApplyLateBlends(); }
            catch (Exception e)
            {
                if (!postLateFailed) Debug.LogWarning("[PoseStudio] late blendshape pass: " + e.Message);
                postLateFailed = true;
                PoseApplier.lateBlendFallback = true;     // keep blendshapes alive via LateUpdate
            }
        }

        // ---- VRM clip guard -----------------------------------------------------------------
        // A VRM blendshape clip whose mesh link has no path makes UniVRM throw inside VNyan's
        // expression update EVERY frame, and the whole update aborts: face tracking, blinks and
        // every clip stop together. Find any such link, say which clip it is, and blank the
        // missing path to "" so UniVRM simply skips it instead of crashing.
        float lastClipGuard = -999f;
        readonly HashSet<string> clipGuardReported = new HashSet<string>();

        void GuardVrmClips()
        {
            if (boundAvatar == null) return;
            try
            {
                Component[] comps = boundAvatar.GetComponentsInChildren<Component>(true);
                for (int c = 0; c < comps.Length; c++)
                {
                    if (comps[c] == null || comps[c].GetType().Name != "VRMBlendShapeProxy") continue;
                    Transform root = comps[c].transform;
                    System.Collections.IEnumerable clips =
                        GetMember(GetMember(comps[c], "BlendShapeAvatar"), "Clips") as System.Collections.IEnumerable;
                    if (clips == null) continue;
                    foreach (object clip in clips)
                    {
                        if (clip == null) continue;
                        string clipName = GetMember(clip, "BlendShapeName") as string;
                        System.Reflection.FieldInfo vf = clip.GetType().GetField("Values");
                        Array vals = vf != null ? vf.GetValue(clip) as Array : null;
                        if (vals == null) continue;
                        for (int i = 0; i < vals.Length; i++)
                        {
                            object b = vals.GetValue(i);
                            if (b == null) continue;
                            System.Reflection.FieldInfo pf = b.GetType().GetField("RelativePath");
                            if (pf == null) continue;
                            string path = pf.GetValue(b) as string;
                            object idx = GetMember(b, "Index");
                            string what = null;
                            if (path == null)
                            {
                                pf.SetValue(b, "");          // boxed copy, written back below
                                vals.SetValue(b, i);
                                what = "has a mesh link with NO path (patched so VNyan stops crashing)";
                            }
                            else if (path.Length > 0)
                            {
                                Transform t = root.Find(path);
                                SkinnedMeshRenderer r = t != null ? t.GetComponent<SkinnedMeshRenderer>() : null;
                                if (r == null) what = "points at a mesh that is not on the avatar: '" + path + "'";
                                else if (r.sharedMesh != null && idx != null &&
                                         (Convert.ToInt32(idx) < 0 || Convert.ToInt32(idx) >= r.sharedMesh.blendShapeCount))
                                    what = "points at blendshape #" + idx + " which '" + path + "' does not have";
                            }
                            if (what != null && clipGuardReported.Add(clipName + "|" + i + "|" + what))
                                Debug.LogWarning("[PoseStudio] VRM clip '" + clipName + "' " + what);
                        }
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] VRM clip check failed: " + e.Message); }
        }

        void Update()
        {
            SafetyTick();
            if (Time.unscaledTime - lastClipGuard > 3f) { lastClipGuard = Time.unscaledTime; GuardVrmClips(); }
            // Live config reload: if the file changes underneath us (hand edited, or written by
            // a tool while tuning an animation to a model) pick it up instead of making the user
            // restart VNyan, which on this app means reloading the avatar too.
            if (Time.unscaledTime - lastConfigPoll > 1f)
            {
                lastConfigPoll = Time.unscaledTime;
                try
                {
                    if (File.Exists(configPath))
                    {
                        System.DateTime w = File.GetLastWriteTimeUtc(configPath);
                        if (configStamp != System.DateTime.MinValue && w != configStamp && w != ourWrite)
                        {
                            configStamp = w;
                            bool kept = safetyDirty;
                            if (kept) WriteSafetyCopy("before taking the changed file");
                            LoadConfig();
                            Rebind();
                            RefreshItemList();
                            Debug.Log("[PoseStudio] config changed on disk, reloaded");
                            if (kept) ShowRestoreButton(System.DateTime.Now);
                        }
                        configStamp = w;
                    }
                }
                catch { }
            }
            if (timeline != null) timeline.Frame();
            if (listeningForHotkey) { CaptureHotkey(); return; }
            DetectHotkeys();
        }

        void OnAssignHotkey()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            listeningForHotkey = true;
            if (hotkeyLabel != null) hotkeyLabel.text = "press keys… (Esc cancels)";
            SetAssignButtonLabel("…");
            SetStatus("listening: press a key, optionally holding Ctrl/Shift/Alt");
        }

        void OnClearHotkey()
        {
            listeningForHotkey = false;
            if (selectedItem != null)
            {
                selectedItem.hotkey = "";
                SetStatus("cleared hotkey for '" + selectedItem.name + "'. Save to keep this change");
            }
            UpdateHotkeyLabel();
        }

        void CaptureHotkey()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                listeningForHotkey = false;
                UpdateHotkeyLabel();
                SetStatus("hotkey assignment cancelled");
                return;
            }

            KeyCode pressed = KeyCode.None;
            foreach (KeyCode k in (KeyCode[])System.Enum.GetValues(typeof(KeyCode)))
            {
                if (IsIgnoredHotkey(k)) continue;
                if (Input.GetKeyDown(k)) { pressed = k; break; }
            }
            if (pressed == KeyCode.None) return;

            List<string> parts = new List<string>();
            if (HeldCtrl()) parts.Add("Ctrl");
            if (HeldShift()) parts.Add("Shift");
            if (HeldAlt()) parts.Add("Alt");
            parts.Add(pressed.ToString());
            string combo = string.Join("+", parts.ToArray());

            listeningForHotkey = false;
            if (selectedItem != null)
            {
                selectedItem.hotkey = combo;
                SetStatus("bound '" + selectedItem.name + "' to " + PrettyCombo(combo) + ". Save to keep this change");
            }
            UpdateHotkeyLabel();
        }

        // Global key edge-detection state (held VKs last frame / this frame).
        readonly HashSet<int> prevDown = new HashSet<int>();
        readonly HashSet<int> curDown = new HashSet<int>();
        readonly HashSet<int> edgeDown = new HashSet<int>();

        // Fires item hotkeys from GLOBAL key state so they work no matter which window is
        // in the foreground (VNyan is usually in the background when a hotkey is pressed).
        void DetectHotkeys()
        {
            if (config == null || config.items == null) return;
            // Don't fire bindings while the user is typing into our own name field.
            if (nameInput != null && nameInput.isFocused) return;

            // Pass 1: sample global state for every bound main key, build this frame's
            // held set and the rising-edge set (down now, up last frame). Done once so a
            // key shared by several bindings produces one consistent edge for all of them.
            curDown.Clear();
            edgeDown.Clear();
            for (int i = 0; i < config.items.Count; i++)
            {
                PoseItem it = config.items[i];
                if (it == null || string.IsNullOrEmpty(it.hotkey)) continue;
                int vk = MainVkOf(it.hotkey);
                if (vk == 0) continue;
                if (GlobalKeys.IsDown(vk))
                {
                    curDown.Add(vk);
                    if (!prevDown.Contains(vk)) edgeDown.Add(vk);
                }
            }

            // Pass 2: fire each binding whose main key just went down and whose modifier
            // state matches exactly (so "F8" won't fire while Ctrl is held, etc.).
            bool ctrl = GlobalKeys.Ctrl(), shift = GlobalKeys.Shift(), alt = GlobalKeys.Alt();
            for (int i = 0; i < config.items.Count; i++)
            {
                PoseItem it = config.items[i];
                if (it == null || string.IsNullOrEmpty(it.hotkey)) continue;
                int vk; bool needCtrl, needShift, needAlt;
                if (!ParseComboVk(it.hotkey, out vk, out needCtrl, out needShift, out needAlt)) continue;
                if (vk == 0 || !edgeDown.Contains(vk)) continue;
                if (needCtrl != ctrl || needShift != shift || needAlt != alt) continue;
                TriggerItem(it);
            }

            // Roll the held set forward for next frame's edge detection.
            prevDown.Clear();
            foreach (int vk in curDown) prevDown.Add(vk);
        }

        void TriggerItem(PoseItem it)
        {
            it.active = !it.active;
            if (it == selectedItem && activeToggle != null)
            {
                suppressCallbacks = true;
                activeToggle.isOn = it.active;
                suppressCallbacks = false;
            }
            SetStatus((it.active ? "activated '" : "deactivated '") + it.name + "' (" + PrettyCombo(it.hotkey) + ")");
        }

        // Parse a binding string ("Ctrl+Shift+F8") into its Windows VK + modifier needs.
        // Returns false if there is no valid mappable main key.
        bool ParseComboVk(string combo, out int vk, out bool needCtrl, out bool needShift, out bool needAlt)
        {
            vk = 0; needCtrl = false; needShift = false; needAlt = false;
            if (string.IsNullOrEmpty(combo)) return false;
            string[] parts = combo.Split('+');
            KeyCode main = KeyCode.None;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                if (p == "Ctrl" || p == "Control") needCtrl = true;
                else if (p == "Shift") needShift = true;
                else if (p == "Alt") needAlt = true;
                else
                {
                    try { main = (KeyCode)System.Enum.Parse(typeof(KeyCode), p, true); }
                    catch { main = KeyCode.None; }
                }
            }
            if (main == KeyCode.None) return false;
            vk = GlobalKeys.ToVk(main);
            return vk != 0;
        }

        int MainVkOf(string combo)
        {
            int vk; bool c, s, a;
            if (!ParseComboVk(combo, out vk, out c, out s, out a)) return 0;
            return vk;
        }

        void UpdateHotkeyLabel()
        {
            SetAssignButtonLabel("Assign");
            if (hotkeyLabel == null) return;
            string hk = selectedItem != null ? selectedItem.hotkey : "";
            hotkeyLabel.text = string.IsNullOrEmpty(hk) ? "(none)" : PrettyCombo(hk);
        }

        void SetAssignButtonLabel(string label)
        {
            if (assignHotkeyButton == null) return;
            Text bt = assignHotkeyButton.GetComponentInChildren<Text>(true);
            if (bt != null) bt.text = label;
        }

        static bool HeldCtrl() { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }
        static bool HeldShift() { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }
        static bool HeldAlt() { return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt); }

        static bool IsIgnoredHotkey(KeyCode k)
        {
            for (int i = 0; i < HOTKEY_IGNORE.Length; i++)
                if (HOTKEY_IGNORE[i] == k) return true;
            return false;
        }

        // "Ctrl+Shift+Alpha1" -> "Ctrl + Shift + 1" for display.
        static string PrettyCombo(string combo)
        {
            if (string.IsNullOrEmpty(combo)) return "(none)";
            string[] parts = combo.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.StartsWith("Alpha")) p = p.Substring(5);
                else if (p.StartsWith("Keypad")) p = "Num " + p.Substring(6);
                parts[i] = p;
            }
            return string.Join(" + ", parts);
        }

        // ========================= IK goals =========================
        //
        // Each item can carry 2-bone IK goals (foot/hand pins). The applier solves them
        // after FK. This editor lists the goals, lets you add/remove, and edit a goal's
        // bones / space / capture mode / weight. Changes Rebind() so they take effect live.

        void RefreshIKList()
        {
            if (ikGoalDropdown == null) return;

            List<string> opts = new List<string>();
            if (selectedItem != null && selectedItem.ikGoals != null)
                for (int i = 0; i < selectedItem.ikGoals.Count; i++)
                {
                    IKGoal g = selectedItem.ikGoals[i];
                    string nm = (g != null && !string.IsNullOrEmpty(g.name)) ? g.name : ("goal " + (i + 1));
                    opts.Add(nm);
                }
            if (opts.Count == 0) opts.Add("(no IK goals)");

            suppressCallbacks = true;
            ikGoalDropdown.ClearOptions();
            ikGoalDropdown.AddOptions(opts);
            ikGoalDropdown.value = 0;
            ikGoalDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (selectedItem != null && selectedItem.ikGoals != null && selectedItem.ikGoals.Count > 0)
                SelectIK(0);
            else { selectedIK = null; PushIKToUI(); }
        }

        void OnIKSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectIK(index);
        }

        void SelectIK(int index)
        {
            if (selectedItem == null || selectedItem.ikGoals == null ||
                index < 0 || index >= selectedItem.ikGoals.Count)
            { selectedIK = null; PushIKToUI(); return; }
            selectedIK = selectedItem.ikGoals[index];
            PushIKToUI();
        }

        void PushIKToUI()
        {
            suppressCallbacks = true;
            IKGoal g = selectedIK;
            if (ikEnabledToggle != null) ikEnabledToggle.isOn = g != null && g.enabled;
            if (ikHoldRotToggle != null) ikHoldRotToggle.isOn = g != null && g.holdRotation;
            if (ikUpperInput != null) ikUpperInput.text = (g != null && g.upper != null) ? g.upper : "";
            if (ikLowerInput != null) ikLowerInput.text = (g != null && g.lower != null) ? g.lower : "";
            if (ikEndInput != null) ikEndInput.text = (g != null && g.end != null) ? g.end : "";
            if (ikSpaceInput != null) ikSpaceInput.text = (g != null && g.space != null) ? g.space : "";
            if (ikWeightInput != null)
                ikWeightInput.text = (g != null ? g.weight : 1f).ToString("0.00", CultureInfo.InvariantCulture);
            if (ikCaptureDropdown != null)
            {
                ikCaptureDropdown.value = (g != null && g.captureMode == "play") ? 1 : 0;
                ikCaptureDropdown.RefreshShownValue();
            }
            suppressCallbacks = false;
        }

        void OnAddIK()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            if (selectedItem.ikGoals == null) selectedItem.ikGoals = new List<IKGoal>();
            IKGoal g = new IKGoal();
            g.name = "IK " + (selectedItem.ikGoals.Count + 1);
            selectedItem.ikGoals.Add(g);
            RefreshIKList();
            int idx = selectedItem.ikGoals.Count - 1;
            if (ikGoalDropdown != null)
            {
                suppressCallbacks = true;
                ikGoalDropdown.value = idx;
                ikGoalDropdown.RefreshShownValue();
                suppressCallbacks = false;
            }
            SelectIK(idx);
            SetStatus("added IK goal: set Upper/Lower/End bones + Space, then Reload");
        }

        void OnRemoveIK()
        {
            if (selectedItem == null || selectedItem.ikGoals == null || selectedIK == null) return;
            selectedItem.ikGoals.Remove(selectedIK);
            selectedIK = null;
            RefreshIKList();
            Rebind();
            SetStatus("removed IK goal. Save to keep this change");
        }

        void OnIKEnabledChanged(bool on) { if (suppressCallbacks || selectedIK == null) return; selectedIK.enabled = on; Rebind(); }
        void OnIKHoldRotChanged(bool on) { if (suppressCallbacks || selectedIK == null) return; selectedIK.holdRotation = on; }
        void OnIKUpperChanged(string t) { if (suppressCallbacks || selectedIK == null) return; selectedIK.upper = t == null ? "" : t.Trim(); Rebind(); }
        void OnIKLowerChanged(string t) { if (suppressCallbacks || selectedIK == null) return; selectedIK.lower = t == null ? "" : t.Trim(); Rebind(); }
        void OnIKEndChanged(string t) { if (suppressCallbacks || selectedIK == null) return; selectedIK.end = t == null ? "" : t.Trim(); Rebind(); }
        void OnIKSpaceChanged(string t) { if (suppressCallbacks || selectedIK == null) return; selectedIK.space = t == null ? "" : t.Trim(); Rebind(); }
        void OnIKCaptureChanged(int idx) { if (suppressCallbacks || selectedIK == null) return; selectedIK.captureMode = (idx == 1) ? "play" : "bind"; Rebind(); }

        void OnIKWeightChanged(string t)
        {
            if (suppressCallbacks || selectedIK == null) return;
            float v;
            if (float.TryParse(t, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                selectedIK.weight = Mathf.Clamp01(v);
                PushIKToUI();
            }
        }

        // ========================= browser (bone + blendshape trees) =========================

        // ---------------- timeline editor bridge ----------------
        // The timeline window is a second VIEW over the same keyframe data; these small
        // public hooks keep the classic dropdown/slider workflow authoritative.
        public PoseItem CurrentItem { get { return selectedItem; } }
        public PoseApplier ApplierRef { get { return applier; } }
        public void PublicStatus(string msg) { SetStatus(msg); }
        public int CurrentKeyIndex()
        {
            if (selectedItem == null || selectedItem.keyframes == null || selectedKey == null) return -1;
            return selectedItem.keyframes.IndexOf(selectedKey);
        }
        public void TimelineSelectKey(int idx)
        {
            if (selectedItem == null || selectedItem.keyframes == null ||
                idx < 0 || idx >= selectedItem.keyframes.Count) return;
            suppressCallbacks = true;
            if (keyframeDropdown != null) { keyframeDropdown.value = idx; keyframeDropdown.RefreshShownValue(); }
            suppressCallbacks = false;
            SelectKeyframe(idx);
        }
        public void TimelineRefreshOptionText() { RefreshKeyframeOptionText(); }
        public void TimelineCaptureInto(int idx)
        {
            if (selectedItem == null || selectedItem.keyframes == null ||
                idx < 0 || idx >= selectedItem.keyframes.Count) return;
            PoseKeyframe key = selectedItem.keyframes[idx];
            key.channels = NewKeyframeFromStatic(selectedItem, key.seconds).channels;
        }
        // Masked capture: only channels whose id passes `allow` are re-captured from the
        // item's current pose; excluded channels keep their existing keyed values.
        public void TimelineCaptureIntoMasked(int idx, Func<string, bool> allow)
        {
            if (selectedItem == null || selectedItem.keyframes == null ||
                idx < 0 || idx >= selectedItem.keyframes.Count) return;
            PoseKeyframe key = selectedItem.keyframes[idx];
            PoseKeyframe fresh = NewKeyframeFromStatic(selectedItem, key.seconds);
            if (allow == null) { key.channels = fresh.channels; return; }
            List<KeyframeChannel> keep = new List<KeyframeChannel>();
            if (key.channels != null)
                for (int i = 0; i < key.channels.Count; i++)
                    if (key.channels[i] != null && !allow(key.channels[i].id)) keep.Add(key.channels[i]);
            if (fresh.channels != null)
                for (int i = 0; i < fresh.channels.Count; i++)
                    if (fresh.channels[i] != null && allow(fresh.channels[i].id)) keep.Add(fresh.channels[i]);
            key.channels = keep;
        }
        public void TimelineRemoveKey(int idx)
        {
            if (selectedItem == null || selectedItem.keyframes == null ||
                idx < 0 || idx >= selectedItem.keyframes.Count) return;
            TimelineSelectKey(idx);
            OnRemoveKeyframe();
        }
        public void TimelineInsertKey(int insertAt, PoseKeyframe key)
        {
            if (selectedItem == null || key == null) return;
            if (selectedItem.keyframes == null) selectedItem.keyframes = new List<PoseKeyframe>();
            insertAt = Mathf.Clamp(insertAt, 0, selectedItem.keyframes.Count);
            selectedItem.keyframes.Insert(insertAt, key);
            RefreshKeyframeList();
            TimelineSelectKey(insertAt);
        }

        void SetupBrowser()
        {
            if (browserPrefab == null) return;
            try
            {
                browserWindow = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(browserPrefab);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PoseStudio] instantiateUIPrefab (browser) failed: " + e.Message);
                browserWindow = null;
            }
            if (browserWindow == null) return;
            PoseUtil.CleanDashes(browserWindow);

            RectTransform brt = browserWindow.GetComponent<RectTransform>();
            if (brt != null) brt.anchoredPosition = new Vector2(0f, 0f);

            browserStatus = FindIn<Text>(browserWindow, "Label_BoneStatus");
            browserContent = FindIn<RectTransform>(browserWindow, "BoneContent");
            browserTitle = FindIn<Text>(browserWindow, "Title");

            Button close = FindIn<Button>(browserWindow, "Button_BoneClose");
            if (close != null) close.onClick.AddListener(CloseBrowser);

            PoseUtil.FastDropdownScroll(browserWindow, 40f);
            browserWindow.SetActive(false);
        }

        void OnAddBoneClicked()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            OpenBrowser("bone");
        }

        void OnAddBlendClicked()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            OpenBrowser("blend");
        }

        void OpenBrowser(string mode)
        {
            if (browserWindow == null) { SetStatus("browser unavailable (prefab missing)"); return; }
            browserMode = mode;
            browserWindow.SetActive(true);
            browserWindow.transform.SetAsLastSibling();
            if (browserTitle != null)
            {
                if (mode == "trigger")
                    browserTitle.text = "Trigger Source: pick a driving blendshape";
                else if (mode == "blend")
                    browserTitle.text = "Blendshapes: pick one to add";
                else if (mode == "mesh")
                    browserTitle.text = "Mesh Objects: pick one to add";
                else if (mode == "hidemesh")
                    browserTitle.text = "Hide Meshes: toggle which to hide";
                else if (mode == "export")
                    browserTitle.text = "Export Poses: tick items, then Export";
                else if (mode == "importconflict")
                    browserTitle.text = "Import: some names already exist";
                else if (mode == "mmdadjust")
                    browserTitle.text = "MMD Import: adjust orientation (previews live)";
                else
                    browserTitle.text = "Model Bones: pick one to add";
            }
            EnsureAvatar();
            RebuildTree();
        }

        void CloseBrowser()
        {
            if (browserWindow != null) browserWindow.SetActive(false);
        }

        void SetBrowserStatus(string msg)
        {
            if (browserStatus != null) browserStatus.text = msg;
        }

        // ===== Export / Import Pose Studio items (share toggles & animations) =====
        readonly HashSet<string> exportSel = new HashSet<string>();
        List<PoseItem> pendingImport;
        List<string> importCollisions;

        void OnExportPosesClicked()
        {
            if (config == null || config.items == null || config.items.Count == 0) { SetStatus("nothing to export"); return; }
            exportSel.Clear();
            for (int i = 0; i < config.items.Count; i++) if (config.items[i] != null) exportSel.Add(config.items[i].name);
            OpenBrowser("export");
        }

        void BuildExportTree(ref float y)
        {
            int n = (config != null && config.items != null) ? config.items.Count : 0;
            GameObject arow = NewRow("Export_Do", y);
            MakeRuntimeButton(arow.transform, "➤  Export " + exportSel.Count + " selected…", INDENT, 0f, 1000f, ROW_H + 2f,
                new Color(0.2f, 0.45f, 0.25f, 1f), new Color(0.9f, 1f, 0.9f, 1f), 13, delegate { DoExport(); });
            y += ROW_H + 8f;
            for (int i = 0; i < n; i++)
            {
                PoseItem it = config.items[i]; if (it == null) continue;
                bool sel = exportSel.Contains(it.name);
                string label = (sel ? "☒ " : "☐ ") + it.name + "  (" + it.type + ")";
                GameObject row = NewRow("Exp_" + i, y);
                string nm = it.name;
                MakeRuntimeButton(row.transform, label, INDENT, 0f, 1000f, ROW_H,
                    new Color(0, 0, 0, 0), sel ? new Color(0.8f, 1f, 0.8f, 1f) : new Color(0.9f, 0.85f, 0.75f, 1f), 13,
                    delegate { if (exportSel.Contains(nm)) exportSel.Remove(nm); else exportSel.Add(nm); RebuildTree(); });
                y += ROW_H;
            }
            SetBrowserStatus("tick the toggles / animations to share, then Export");
        }

        void DoExport()
        {
            if (exportSel.Count == 0) { SetBrowserStatus("select at least one item"); return; }
            string path = NativeFileDialog.SaveFile("Export Pose Studio items", "my_poses.json", "json",
                "Pose Studio (*.json)", "*.json");
            if (string.IsNullOrEmpty(path)) return;
            PoseConfig outCfg = new PoseConfig();
            outCfg.items = new List<PoseItem>();
            for (int i = 0; i < config.items.Count; i++)
                if (config.items[i] != null && exportSel.Contains(config.items[i].name)) outCfg.items.Add(config.items[i]);
            try { File.WriteAllText(path, JsonConvert.SerializeObject(outCfg, Formatting.None)); }
            catch (System.Exception e) { SetStatus("export failed: " + e.Message); return; }
            CloseBrowser();
            SetStatus("exported " + outCfg.items.Count + " item(s) to " + Path.GetFileName(path));
        }

        void OnImportPosesClicked()
        {
            string path = NativeFileDialog.OpenFile("Import Pose Studio items", null, "Pose Studio (*.json)", "*.json");
            if (string.IsNullOrEmpty(path)) return;
            PoseConfig incoming;
            try { incoming = JsonConvert.DeserializeObject<PoseConfig>(File.ReadAllText(path)); }
            catch (System.Exception e) { SetStatus("import failed: " + e.Message); return; }
            if (incoming == null || incoming.items == null || incoming.items.Count == 0) { SetStatus("no items found in file"); return; }
            if (config == null) config = new PoseConfig();
            if (config.items == null) config.items = new List<PoseItem>();

            HashSet<string> existing = new HashSet<string>();
            for (int i = 0; i < config.items.Count; i++) if (config.items[i] != null) existing.Add(config.items[i].name);
            pendingImport = incoming.items;
            importCollisions = new List<string>();
            for (int i = 0; i < pendingImport.Count; i++)
                if (pendingImport[i] != null && existing.Contains(pendingImport[i].name)) importCollisions.Add(pendingImport[i].name);

            if (importCollisions.Count == 0) { FinalizeImport("append"); return; }
            OpenBrowser("importconflict");
        }

        void BuildConflictTree(ref float y)
        {
            int nc = importCollisions != null ? importCollisions.Count : 0;
            int total = pendingImport != null ? pendingImport.Count : 0;
            MakeConflictAction(ref y, "Overwrite same-named (" + nc + ")", new Color(0.5f, 0.3f, 0.2f, 1f), delegate { FinalizeImport("overwrite"); });
            MakeConflictAction(ref y, "Keep both (rename imported)", new Color(0.25f, 0.4f, 0.5f, 1f), delegate { FinalizeImport("keepboth"); });
            MakeConflictAction(ref y, "Skip same-named (append " + (total - nc) + ")", new Color(0.35f, 0.35f, 0.35f, 1f), delegate { FinalizeImport("skip"); });
            MakeConflictAction(ref y, "Cancel", new Color(0.3f, 0.2f, 0.2f, 1f), delegate { CloseBrowser(); });
            y += 6f;
            for (int i = 0; i < nc; i++)
            {
                GameObject row = NewRow("Conf_" + i, y);
                MakeRuntimeButton(row.transform, "• " + importCollisions[i], INDENT, 0f, 1000f, ROW_H,
                    new Color(0, 0, 0, 0), new Color(1f, 0.8f, 0.6f, 1f), 12, delegate { });
                y += ROW_H;
            }
            SetBrowserStatus(nc + " of " + total + " imported item(s) share a name with an existing one");
        }

        void MakeConflictAction(ref float y, string label, Color col, UnityEngine.Events.UnityAction act)
        {
            GameObject row = NewRow("Act_" + label, y);
            MakeRuntimeButton(row.transform, label, INDENT, 0f, 1000f, ROW_H + 4f, col, new Color(0.95f, 0.95f, 0.95f, 1f), 13, act);
            y += ROW_H + 8f;
        }

        void FinalizeImport(string mode)
        {
            if (pendingImport == null) { CloseBrowser(); return; }
            int added = 0, replaced = 0, skipped = 0;
            HashSet<string> collide = importCollisions != null ? new HashSet<string>(importCollisions) : new HashSet<string>();
            for (int i = 0; i < pendingImport.Count; i++)
            {
                PoseItem it = pendingImport[i]; if (it == null) continue;
                bool clash = collide.Contains(it.name);
                if (!clash) { config.items.Add(it); added++; continue; }
                if (mode == "overwrite")
                {
                    int idx = -1;
                    for (int j = 0; j < config.items.Count; j++) if (config.items[j] != null && config.items[j].name == it.name) { idx = j; break; }
                    if (idx >= 0) { config.items[idx] = it; replaced++; } else { config.items.Add(it); added++; }
                }
                else if (mode == "keepboth") { it.name = UniqueItemName(it.name); config.items.Add(it); added++; }
                else { skipped++; }
            }
            pendingImport = null; importCollisions = null;
            CloseBrowser();
            Rebind();
            RefreshItemListKeepIndex(0);
            SetStatus("imported: " + added + " added"
                + (replaced > 0 ? ", " + replaced + " overwritten" : "")
                + (skipped > 0 ? ", " + skipped + " skipped" : "") + ". Save to keep.");
        }

        const float ROW_H = 22f;
        const float INDENT = 14f;

        void RebuildTree()
        {
            if (browserContent == null) return;
            for (int i = browserContent.childCount - 1; i >= 0; i--)
                Destroy(browserContent.GetChild(i).gameObject);

            // item export / import-conflict / MMD-adjust pickers don't need an avatar tree
            if (browserMode == "export") { float ye = 0f; BuildExportTree(ref ye); browserContent.sizeDelta = new Vector2(0f, ye); return; }
            if (browserMode == "importconflict") { float yc = 0f; BuildConflictTree(ref yc); browserContent.sizeDelta = new Vector2(0f, yc); return; }
            if (browserMode == "mmdadjust") { float ym = 0f; BuildMmdAdjustTree(ref ym); browserContent.sizeDelta = new Vector2(0f, ym); return; }

            if (boundAvatar == null)
            {
                SetBrowserStatus("no avatar loaded");
                browserContent.sizeDelta = new Vector2(0f, 0f);
                return;
            }

            if (!ReferenceEquals(browserAvatar, boundAvatar))
            {
                boneExpanded.Clear();
                meshExpanded.Clear();
                browserAvatar = boundAvatar;
                boneExpanded[boundAvatar.transform] = true;
            }

            float y = 0f;
            if (browserMode == "blend" || browserMode == "trigger") BuildBlendTree(ref y);
            else if (browserMode == "mesh" || browserMode == "hidemesh") BuildMeshTree(ref y);
            else AddBoneRow(boundAvatar.transform, 0, ref y);

            browserContent.sizeDelta = new Vector2(0f, y);
        }

        // ----- bone tree -----

        void AddBoneRow(Transform bone, int depth, ref float y)
        {
            bool hasChildren = bone.childCount > 0;
            bool expanded;
            if (!boneExpanded.TryGetValue(bone, out expanded)) expanded = false;

            float indent = depth * INDENT;
            GameObject row = NewRow("Row_" + bone.name, y);

            if (hasChildren)
            {
                Transform captured = bone;
                MakeRuntimeButton(row.transform, expanded ? "-" : "+",
                    indent, 0f, INDENT + 2f, ROW_H,
                    new Color(0.25f, 0.25f, 0.30f, 1f), Color.white, 13,
                    delegate { ToggleBoneExpanded(captured); });
            }

            Transform pick = bone;
            float nameX = indent + INDENT + 4f;
            string label = bone.name + (hasChildren ? "" : "  •");
            MakeRuntimeButton(row.transform, label, nameX, 0f, 1000f, ROW_H,
                new Color(0f, 0f, 0f, 0f), new Color(0.85f, 0.9f, 1f, 1f), 13,
                delegate { OnBonePicked(pick); });

            y += ROW_H;
            if (hasChildren && expanded)
                for (int i = 0; i < bone.childCount; i++)
                    AddBoneRow(bone.GetChild(i), depth + 1, ref y);

            SetBrowserStatus("avatar '" + boundAvatar.name + "': click a bone to add it");
        }

        void ToggleBoneExpanded(Transform bone)
        {
            bool cur;
            if (!boneExpanded.TryGetValue(bone, out cur)) cur = false;
            boneExpanded[bone] = !cur;
            RebuildTree();
        }

        void OnBonePicked(Transform bone)
        {
            if (bone == null || selectedItem == null) return;
            if (selectedItem.bones == null) selectedItem.bones = new List<BoneTarget>();

            BoneTarget bt = new BoneTarget();
            bt.bone = bone.name;
            selectedItem.bones.Add(bt);

            Rebind();
            RefreshBoneList();
            // Bones are listed first, so the new bone is the last bone entry.
            SelectTransformInDropdown(selectedItem.bones.Count - 1);
            SetBrowserStatus("added bone '" + bone.name + "' to '" + selectedItem.name + "'");
            SetStatus("added bone '" + bone.name + "': set its offsets, then Save");
        }

        // ----- mesh-object transform targets -----

        void OnAddMeshClicked()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            OpenBrowser("mesh");
        }

        void OnHideMeshClicked()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            OpenBrowser("hidemesh");
        }

        void OnMeshPicked(Transform mesh)
        {
            if (mesh == null || selectedItem == null) return;

            // Hide-mesh mode: toggle this renderer's name in the item's hide list.
            if (browserMode == "hidemesh")
            {
                if (selectedItem.hideMeshes == null) selectedItem.hideMeshes = new List<string>();
                if (selectedItem.hideMeshes.Contains(mesh.name))
                {
                    selectedItem.hideMeshes.Remove(mesh.name);
                    SetBrowserStatus("un-hid '" + mesh.name + "'");
                }
                else
                {
                    selectedItem.hideMeshes.Add(mesh.name);
                    SetBrowserStatus("'" + mesh.name + "' will hide while '" + selectedItem.name + "' is active");
                }
                Rebind();
                RebuildTree();
                SetStatus("hide list: " + selectedItem.hideMeshes.Count + " mesh(es). Save to keep");
                return;
            }

            if (selectedItem.meshes == null) selectedItem.meshes = new List<MeshTarget>();

            MeshTarget mt = new MeshTarget();
            mt.mesh = mesh.name;
            selectedItem.meshes.Add(mt);

            Rebind();
            RefreshBoneList();
            // Meshes are listed after bones in the combined dropdown.
            int bones = selectedItem.bones != null ? selectedItem.bones.Count : 0;
            SelectTransformInDropdown(bones + selectedItem.meshes.Count - 1);
            SetBrowserStatus("added mesh '" + mesh.name + "' to '" + selectedItem.name + "'");
            SetStatus("added mesh '" + mesh.name + "': set its offsets, then Save");
        }

        // ----- mesh-object tree (flat list of renderer transforms) -----

        void BuildMeshTree(ref float y)
        {
            List<Transform> meshes = PoseUtil.MeshTransforms(boundAvatar);
            if (meshes.Count == 0)
            {
                SetBrowserStatus("no mesh objects on this model");
                return;
            }

            bool hideMode = browserMode == "hidemesh";
            for (int i = 0; i < meshes.Count; i++)
            {
                Transform mt = meshes[i];
                bool isHidden = hideMode && selectedItem != null && selectedItem.hideMeshes != null
                                && selectedItem.hideMeshes.Contains(mt.name);
                string label = hideMode ? (isHidden ? "☒ " + mt.name + "  (hidden)" : "☐ " + mt.name)
                                        : mt.name + "  •";
                GameObject row = NewRow("Mesh_" + mt.name, y);
                Transform captured = mt;
                MakeRuntimeButton(row.transform, label, INDENT, 0f, 1000f, ROW_H,
                    new Color(0f, 0f, 0f, 0f),
                    isHidden ? new Color(1f, 0.7f, 0.7f, 1f) : new Color(0.95f, 0.9f, 0.8f, 1f), 13,
                    delegate { OnMeshPicked(captured); });
                y += ROW_H;
            }

            SetBrowserStatus(hideMode
                ? "click a mesh to hide/show it while this item is active"
                : "avatar '" + boundAvatar.name + "': click a mesh object to add it");
        }

        // ----- blendshape tree (mesh -> shapes) -----

        // Every VRM blendshape clip the bound avatar has. Read from the avatar's own clip list
        // (VRMBlendShapeProxy, via reflection so UniVRM is not a build dependency), plus anything
        // VNyan currently reports, which also catches clips added by other plugins.
        List<string> VrmClipNames()
        {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            if (boundAvatar != null)
            {
                try
                {
                    Component[] comps = boundAvatar.GetComponentsInChildren<Component>(true);
                    for (int c = 0; c < comps.Length; c++)
                    {
                        if (comps[c] == null || comps[c].GetType().Name != "VRMBlendShapeProxy") continue;
                        object avatarAsset = GetMember(comps[c], "BlendShapeAvatar");
                        System.Collections.IEnumerable clips = GetMember(avatarAsset, "Clips") as System.Collections.IEnumerable;
                        if (clips == null) continue;
                        foreach (object clip in clips)
                        {
                            string n = GetMember(clip, "BlendShapeName") as string;
                            if (!string.IsNullOrEmpty(n) && seen.Add(n)) names.Add(n);
                        }
                    }
                }
                catch (Exception e) { Debug.LogWarning("[PoseStudio] reading VRM clips: " + e.Message); }
            }
            try
            {
                Dictionary<string, float> live = VNyanInterface.VNyanInterface.VNyanAvatar.getBlendshapesInstant();
                if (live != null)
                    foreach (string k in live.Keys)
                        if (!string.IsNullOrEmpty(k) && seen.Add(k)) names.Add(k);
            }
            catch { }
            return names;
        }

        static object GetMember(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            System.Reflection.PropertyInfo p = t.GetProperty(name);
            if (p != null) return p.GetValue(o, null);
            System.Reflection.FieldInfo f = t.GetField(name);
            return f != null ? f.GetValue(o) : null;
        }

        bool vrmClipsExpanded = true;

        void BuildBlendTree(ref float y)
        {
            // VRM blendshape clips first: the expressions most avatars are driven by. Not offered
            // as a trigger source, which reads raw mesh weights.
            if (browserMode == "blend")
            {
                List<string> clipNames = VrmClipNames();
                if (clipNames.Count > 0)
                {
                    GameObject crow = NewRow("VRMClips", y);
                    MakeRuntimeButton(crow.transform, vrmClipsExpanded ? "-" : "+",
                        0f, 0f, INDENT + 2f, ROW_H,
                        new Color(0.30f, 0.25f, 0.35f, 1f), Color.white, 13,
                        delegate { vrmClipsExpanded = !vrmClipsExpanded; RebuildTree(); });
                    MakeRuntimeButton(crow.transform, "VRM Blendshape Clips  (" + clipNames.Count + ")",
                        INDENT + 4f, 0f, 1000f, ROW_H,
                        new Color(0f, 0f, 0f, 0f), new Color(1f, 0.85f, 1f, 1f), 13,
                        delegate { vrmClipsExpanded = !vrmClipsExpanded; RebuildTree(); });
                    y += ROW_H;
                    if (vrmClipsExpanded)
                        for (int c = 0; c < clipNames.Count; c++)
                        {
                            string capClip = clipNames[c];
                            GameObject srow = NewRow("Clip_" + capClip, y);
                            MakeRuntimeButton(srow.transform, capClip, INDENT + 18f, 0f, 1000f, ROW_H,
                                new Color(0f, 0f, 0f, 0f), new Color(1f, 0.9f, 1f, 1f), 12,
                                delegate { OnClipPicked(capClip); });
                            y += ROW_H;
                        }
                }
            }

            List<SkinnedMeshRenderer> rends = PoseUtil.Renderers(boundAvatar);
            if (rends.Count == 0)
            {
                SetBrowserStatus("no blendshapes on this model");
                return;
            }

            for (int i = 0; i < rends.Count; i++)
            {
                SkinnedMeshRenderer r = rends[i];
                int id = r.GetInstanceID();
                bool expanded;
                if (!meshExpanded.TryGetValue(id, out expanded)) expanded = false;

                GameObject row = NewRow("Mesh_" + r.name, y);
                int capturedId = id;
                MakeRuntimeButton(row.transform, expanded ? "-" : "+",
                    0f, 0f, INDENT + 2f, ROW_H,
                    new Color(0.25f, 0.25f, 0.30f, 1f), Color.white, 13,
                    delegate { ToggleMeshExpanded(capturedId); });
                MakeRuntimeButton(row.transform, r.name + "  (" + r.sharedMesh.blendShapeCount + ")",
                    INDENT + 4f, 0f, 1000f, ROW_H,
                    new Color(0f, 0f, 0f, 0f), new Color(0.8f, 1f, 0.85f, 1f), 13,
                    delegate { ToggleMeshExpanded(capturedId); });
                y += ROW_H;

                if (expanded)
                {
                    int count = r.sharedMesh.blendShapeCount;
                    for (int s = 0; s < count; s++)
                    {
                        string shape = r.sharedMesh.GetBlendShapeName(s);
                        GameObject srow = NewRow("Shape_" + shape, y);
                        SkinnedMeshRenderer capR = r;
                        string capShape = shape;
                        MakeRuntimeButton(srow.transform, shape, INDENT + 18f, 0f, 1000f, ROW_H,
                            new Color(0f, 0f, 0f, 0f), new Color(0.85f, 0.9f, 1f, 1f), 12,
                            delegate { OnBlendLeafPicked(capR, capShape); });
                        y += ROW_H;
                    }
                }
            }

            SetBrowserStatus("avatar '" + boundAvatar.name + "': expand a mesh, click a blendshape");
        }

        void ToggleMeshExpanded(int id)
        {
            bool cur;
            if (!meshExpanded.TryGetValue(id, out cur)) cur = false;
            meshExpanded[id] = !cur;
            RebuildTree();
        }

        // A blendshape leaf was clicked: route to "add as target" or "set as trigger source".
        void OnBlendLeafPicked(SkinnedMeshRenderer r, string shape)
        {
            if (browserMode == "trigger") OnTriggerSourcePicked(r, shape);
            else OnBlendPicked(r, shape);
        }

        void OnClipPicked(string clip)
        {
            if (string.IsNullOrEmpty(clip) || selectedItem == null) return;
            if (selectedItem.blendshapes == null) selectedItem.blendshapes = new List<BlendTarget>();
            BlendTarget bt = new BlendTarget();
            bt.mesh = BlendTarget.VRM_CLIP;
            bt.shape = clip;
            bt.weight = 100f;
            selectedItem.blendshapes.Add(bt);

            Rebind();
            RefreshBlendList();
            int idx = selectedItem.blendshapes.Count - 1;
            if (blendDropdown != null && idx < blendDropdown.options.Count)
            {
                suppressCallbacks = true;
                blendDropdown.value = idx;
                blendDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectBlendTarget(idx);
            }
            SetBrowserStatus("added VRM clip '" + clip + "'");
            SetStatus("added VRM clip '" + clip + "': set its weight, then Save");
        }

        void OnBlendPicked(SkinnedMeshRenderer r, string shape)
        {
            if (r == null || string.IsNullOrEmpty(shape) || selectedItem == null) return;
            if (selectedItem.blendshapes == null) selectedItem.blendshapes = new List<BlendTarget>();

            BlendTarget bt = new BlendTarget();
            bt.mesh = r.name;
            bt.shape = shape;
            bt.weight = 100f;
            selectedItem.blendshapes.Add(bt);

            Rebind();
            RefreshBlendList();
            int idx = selectedItem.blendshapes.Count - 1;
            if (blendDropdown != null && idx >= 0 && idx < blendDropdown.options.Count)
            {
                suppressCallbacks = true;
                blendDropdown.value = idx;
                blendDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectBlendTarget(idx);
            }
            SetBrowserStatus("added blendshape '" + shape + "'");
            SetStatus("added blendshape '" + shape + "': set its weight, then Save");
        }

        // ----- runtime row + button helpers -----

        GameObject NewRow(string name, float y)
        {
            GameObject row = new GameObject(name, typeof(RectTransform));
            RectTransform rrt = row.GetComponent<RectTransform>();
            rrt.SetParent(browserContent, false);
            rrt.anchorMin = new Vector2(0f, 1f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0f, 1f);
            rrt.offsetMin = new Vector2(0f, 0f);
            rrt.offsetMax = new Vector2(0f, 0f);
            rrt.anchoredPosition = new Vector2(0f, -y);
            rrt.sizeDelta = new Vector2(0f, ROW_H);
            return row;
        }

        void MakeRuntimeButton(Transform parent, string label, float x, float y, float w, float h,
            Color bgColor, Color textColor, int fontSize, UnityEngine.Events.UnityAction onClick)
        {
            GameObject go = new GameObject("Btn", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);

            Image img = go.GetComponent<Image>();
            img.color = bgColor;

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null) btn.onClick.AddListener(onClick);

            GameObject txt = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            RectTransform trt = txt.GetComponent<RectTransform>();
            trt.SetParent(go.transform, false);
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(4f, 0f);
            trt.offsetMax = new Vector2(-2f, 0f);
            Text t = txt.GetComponent<Text>();
            t.text = label;
            t.font = uiFont;
            t.fontSize = fontSize;
            t.color = textColor;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
        }

        // ========================= avatar binding =========================

        void EnsureAvatar()
        {
            GameObject av = null;
            try { av = (GameObject)VNyanInterface.VNyanInterface.VNyanAvatar.getAvatarObject(); }
            catch { /* not ready */ }

            if (av == null) { boundAvatar = null; return; }
            if (ReferenceEquals(av, boundAvatar)) return;

            boundAvatar = av;
            boundAnimator = av.GetComponentInChildren<Animator>();
            Rebind();
            Debug.Log("[PoseStudio] bound to avatar '" + av.name + "'");
        }

        void Rebind()
        {
            applier.Bind(boundAvatar, boundAnimator, config);
            restoredWhileDisabled = false;
            RebuildHumanBoneMap();
            RecomputeIdleCaches();
            headSampleValid = false; lastAppliedValid = false;   // fresh idle detection state
        }

        // ========================= config IO =========================

        void ResolveConfigPath()
        {
            string vnyanRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            savePath = Path.Combine(Application.persistentDataPath, CONFIG_FILE);

            string[] candidates =
            {
                savePath,
                Path.Combine(vnyanRoot, CONFIG_FILE),
                Path.Combine(vnyanRoot, Path.Combine("Items", Path.Combine("Assemblies", Path.Combine("PoseStudio", CONFIG_FILE)))),
                Path.Combine(vnyanRoot, Path.Combine("Items", CONFIG_FILE)),
            };

            foreach (string c in candidates)
                if (File.Exists(c)) { configPath = c; return; }

            configPath = savePath;
        }

        void LoadConfig()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    WriteExampleConfig(configPath);
                    Debug.LogWarning("[PoseStudio] no config found; wrote a starter file at " + configPath);
                }

                string json = File.ReadAllText(configPath);
                PoseConfig parsed = ParseConfigJson(json);
                if (parsed != null)
                {
                    config = parsed;
                    MergeBundled();
                    safetyDirty = false;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PoseStudio] failed to load config: " + e.Message);
                if (config == null) config = new PoseConfig();
            }
        }

        // Shared by LoadConfig and Restore unsaved.
        static PoseConfig ParseConfigJson(string json)
        {
            PoseConfig parsed = JsonConvert.DeserializeObject<PoseConfig>(json);
            if (parsed != null)
            {
                if (parsed.settings == null) parsed.settings = new PoseSettings();
                if (parsed.items == null) parsed.items = new List<PoseItem>();
                // Older configs predate meshes/keyframes, make sure no list is null.
                for (int i = 0; i < parsed.items.Count; i++)
                {
                    PoseItem it = parsed.items[i];
                    if (it == null) continue;
                    if (it.bones == null) it.bones = new List<BoneTarget>();
                    if (it.meshes == null) it.meshes = new List<MeshTarget>();
                    if (it.blendshapes == null) it.blendshapes = new List<BlendTarget>();
                    if (it.keyframes == null) it.keyframes = new List<PoseKeyframe>();
                    for (int kfi = 0; kfi < it.keyframes.Count; kfi++)
                    {
                        PoseKeyframe kf = it.keyframes[kfi];
                        if (kf != null && kf.channels == null) kf.channels = new List<KeyframeChannel>();
                    }
                }
            }
            return parsed;
        }

        // Animations that ship WITH the plugin. Each .json beside the DLL (in "builtin\\") holds
        // one or more items. Each one is offered once: it is added if the user does not already
        // have that name, and its name is remembered in settings.bundledOffered, so deleting or
        // renaming it later sticks. Nothing is saved here; the user's next Save keeps it.
        void MergeBundled()
        {
            try
            {
                // A VNyan plugin is loaded from bytes, so the assembly has no file path to
                // work from: look next to VNyan itself first and only fall back to the
                // assembly location (which is empty in the app, but set in a test host).
                string folder = null;
                string vnyanRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string[] tries = {
                    Path.Combine(vnyanRoot, Path.Combine("Items", Path.Combine("Assemblies", Path.Combine("PoseStudio", "builtin")))),
                    Path.Combine(vnyanRoot, Path.Combine("Items", Path.Combine("Assemblies", "builtin"))),
                };
                for (int t = 0; t < tries.Length && folder == null; t++)
                    if (Directory.Exists(tries[t])) folder = tries[t];
                if (folder == null)
                {
                    string dir = null;
                    try { dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location); }
                    catch { }
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(Path.Combine(dir, "builtin")))
                        folder = Path.Combine(dir, "builtin");
                }
                if (folder == null) return;

                if (config.settings == null) config.settings = new PoseSettings();
                if (config.settings.bundledOffered == null) config.settings.bundledOffered = new List<string>();
                List<string> offered = config.settings.bundledOffered;
                string[] files = Directory.GetFiles(folder, "*.json");
                for (int f = 0; f < files.Length; f++)
                {
                    PoseConfig bundled;
                    try { bundled = JsonConvert.DeserializeObject<PoseConfig>(File.ReadAllText(files[f])); }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[PoseStudio] bundled animation '" + Path.GetFileName(files[f])
                                         + "' could not be read: " + ex.Message);
                        continue;
                    }
                    if (bundled == null || bundled.items == null) continue;
                    for (int i = 0; i < bundled.items.Count; i++)
                    {
                        PoseItem it = bundled.items[i];
                        if (it == null || string.IsNullOrEmpty(it.name)) continue;
                        bool have = offered.Contains(it.name);
                        for (int j = 0; j < config.items.Count && !have; j++)
                            if (config.items[j] != null && config.items[j].name == it.name) have = true;
                        if (!offered.Contains(it.name)) offered.Add(it.name);
                        if (have) continue;
                        it.active = false;                 // never start moving on its own
                        if (it.bones == null) it.bones = new List<BoneTarget>();
                        if (it.meshes == null) it.meshes = new List<MeshTarget>();
                        if (it.blendshapes == null) it.blendshapes = new List<BlendTarget>();
                        if (it.keyframes == null) it.keyframes = new List<PoseKeyframe>();
                        config.items.Add(it);
                        Debug.Log("[PoseStudio] added bundled animation '" + it.name + "'");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PoseStudio] bundled animations skipped: " + e.Message);
            }
        }

        void SaveConfig()
        {
            try
            {
                if (config == null) { SetStatus("nothing to save"); return; }

                string target = string.IsNullOrEmpty(savePath)
                    ? Path.Combine(Application.persistentDataPath, CONFIG_FILE)
                    : savePath;

                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Compact, never indented: one 500-key animation is 6 MB compact and 18 MB pretty,
                // and past about 45 MB the JSON reader throws while the plugin is starting,
                // which takes every plugin loaded after this one down with it.
                string json = JsonConvert.SerializeObject(config, Formatting.None);
                File.WriteAllText(target, json);
                configPath = target;
                try { ourWrite = File.GetLastWriteTimeUtc(configPath); configStamp = ourWrite; } catch { }
                SetStatus("Saved to " + target);
                safetyDirty = false;
                if (restoreButton != null) restoreButton.SetActive(false);
            }
            catch (Exception e)
            {
                Debug.LogError("[PoseStudio] failed to save config: " + e.Message);
                SetStatus("save failed: " + e.Message);
            }
        }

        void WriteExampleConfig(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                PoseConfig example = new PoseConfig();

                // A simple "Head Tilt" toggle as a friendly starting point.
                PoseItem tilt = new PoseItem();
                tilt.name = "Head Tilt";
                tilt.type = "toggle";
                BoneTarget head = new BoneTarget();
                head.bone = "Head";
                head.useRotation = true;
                head.rotation = new float[] { 0f, 0f, 20f };
                tilt.bones.Add(head);
                example.items.Add(tilt);

                File.WriteAllText(path, JsonConvert.SerializeObject(example, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError("[PoseStudio] could not write example config: " + e.Message);
            }
        }
    }
}
