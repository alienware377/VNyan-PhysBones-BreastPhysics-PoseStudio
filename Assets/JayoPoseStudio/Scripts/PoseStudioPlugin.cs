using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

namespace JayoPoseStudio
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
    public class PoseStudioPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "Pose Studio";
        const string CONFIG_FILE = "posestudio.json";

        // Slider keys -> fields. Order also defines UI layout order in the build script.
        static readonly string[] SLIDER_KEYS =
        {
            "posX", "posY", "posZ", "rotX", "rotY", "rotZ",
            "sclX", "sclY", "sclZ", "weight", "speed", "blend",
            "tcurve", "tscale", "kfsec"
        };

        // Assigned by the AssetBundle build (PoseStudioBuild.cs).
        public GameObject windowPrefab;
        public GameObject browserPrefab;

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
        Dropdown boneDropdown;       // combined bone + mesh transform target list
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

            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON_NAME, this); }
            catch (Exception e) { Debug.LogWarning("[PoseStudio] registerPluginButton failed: " + e.Message); }

            ResolveConfigPath();
            LoadConfig();
            SetupWindow();
            SetupBrowser();
            Debug.Log("[PoseStudio] initialized. Config: " + configPath);
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
            applier.Apply(dt);
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

            itemDropdown = FindControl<Dropdown>("Dropdown_Item");
            boneDropdown = FindControl<Dropdown>("Dropdown_Bone");
            blendDropdown = FindControl<Dropdown>("Dropdown_Blend");
            waveDropdown = FindControl<Dropdown>("Dropdown_Wave");
            keyframeDropdown = FindControl<Dropdown>("Dropdown_Keyframe");
            keyframeToggle = FindControl<Toggle>("Toggle_Keyframes");
            nameInput = FindControl<InputField>("Input_Name");
            enabledToggle = FindControl<Toggle>("Toggle_Enabled");
            activeToggle = FindControl<Toggle>("Toggle_Active");
            animToggle = FindControl<Toggle>("Toggle_Anim");
            usePosToggle = FindControl<Toggle>("Toggle_UsePos");
            useRotToggle = FindControl<Toggle>("Toggle_UseRot");
            useScaleToggle = FindControl<Toggle>("Toggle_UseScale");
            triggerToggle = FindControl<Toggle>("Toggle_Trigger");
            triggerSourceLabel = FindControl<Text>("Label_TriggerSource");
            statusLabel = FindControl<Text>("Label_Status");
            hotkeyLabel = FindControl<Text>("Label_Hotkey");
            assignHotkeyButton = FindControl<Button>("Button_AssignHotkey");

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
            WireButton("Button_RemoveBone", OnRemoveBone);
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

            if (keyframeToggle != null) keyframeToggle.isOn = selectedItem.useKeyframes;

            if (triggerToggle != null) triggerToggle.isOn = selectedItem.useTrigger;
            SetSlider("tcurve", selectedItem.triggerCurve);
            SetSlider("tscale", selectedItem.triggerScale);

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
            SetStatus("added " + type + " '" + it.name + "' — add bones/blendshapes, then Save");
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

            SetStatus("removed '" + removed + "' — Save to keep this change");
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

        // The transform section edits one target at a time — either a bone or a mesh
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
            if (index >= 0 && index < transformTargets.Count)
            {
                object o = transformTargets[index];
                selectedBone = o as BoneTarget;
                selectedMesh = o as MeshTarget;
            }
            PushTransformToUI();
        }

        // The currently edited transform target via the shared interface (or null).
        ITransformTarget CurTarget()
        {
            if (selectedBone != null) return selectedBone;
            if (selectedMesh != null) return selectedMesh;
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
        // "Activate" control while enabled — e.g. bind a mouth-open shape so the more the
        // user opens their mouth, the more a toggle/animation engages.

        void OnTriggerToggled(bool on)
        {
            if (suppressCallbacks || selectedItem == null) return;
            selectedItem.useTrigger = on;
            Rebind();
            UpdateTriggerLabel();
            if (on && string.IsNullOrEmpty(selectedItem.triggerShape))
                SetStatus("trigger on — click 'Pick Source…' to choose a driving blendshape");
            else if (!on)
                SetStatus("trigger off — '" + selectedItem.name + "' uses its Activate toggle/hotkey again");
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
            SetStatus("trigger: '" + shape + "' now drives '" + selectedItem.name + "' — Save to keep");
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
                    SetStatus("removed bone '" + removed + "' — Save to keep this change");
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
                    SetStatus("removed mesh '" + removed + "' — Save to keep this change");
                    return;
                }
            }
            SetStatus("no transform selected");
        }

        // ========================= keyframe timeline =========================
        //
        // Animation items can play through many keyframes instead of the
        // sine/triangle/pulse wave. Each keyframe holds its OWN full pose — distinct
        // bone/mesh offsets and blendshape weights — and the seconds until the next
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
                ? "keyframe timeline on for '" + selectedItem.name + "' — pick a key, then pose it with the sliders"
                : "keyframe timeline off — '" + selectedItem.name + "' uses the wave again");
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
            SetStatus("added keyframe " + (last + 1) + " — pose it with the sliders, then Save");
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
            SetStatus("removed keyframe " + (idx + 1) + " — Save to keep this change");
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
                    opts.Add(nm);
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
            SetStatus("removed blendshape '" + removed + "' — Save to keep this change");
        }

        // ========================= sliders =========================

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
            if (key == "speed") { if (selectedItem != null) selectedItem.speed = v; return; }
            if (key == "blend") { if (selectedItem != null) selectedItem.blendTime = v; return; }
            if (key == "tcurve") { if (selectedItem != null) selectedItem.triggerCurve = v; return; }
            if (key == "tscale") { if (selectedItem != null) selectedItem.triggerScale = v; return; }

            // Keyframe seconds-to-next.
            if (key == "kfsec") { if (selectedKey != null) { selectedKey.seconds = v; RefreshKeyframeOptionText(); } return; }

            // Blendshape weight — to the selected keyframe's channel when posing a keyframe,
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

            // Transform sliders — write to whichever transform target (bone or mesh) is
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
            LoadConfig();
            Rebind();
            RefreshItemList();
            SetStatus("Reloaded " + Path.GetFileName(configPath));
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
        // Pressing it toggles that item's `active` flag — exactly like flipping its
        // "Activate this item" checkbox — so it works for toggles and animations alike,
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

        void Update()
        {
            if (listeningForHotkey) { CaptureHotkey(); return; }
            DetectHotkeys();
        }

        void OnAssignHotkey()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            listeningForHotkey = true;
            if (hotkeyLabel != null) hotkeyLabel.text = "press keys… (Esc cancels)";
            SetAssignButtonLabel("…");
            SetStatus("listening — press a key, optionally holding Ctrl/Shift/Alt");
        }

        void OnClearHotkey()
        {
            listeningForHotkey = false;
            if (selectedItem != null)
            {
                selectedItem.hotkey = "";
                SetStatus("cleared hotkey for '" + selectedItem.name + "' — Save to keep this change");
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
                SetStatus("bound '" + selectedItem.name + "' to " + PrettyCombo(combo) + " — Save to keep this change");
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
            SetStatus("added IK goal — set Upper/Lower/End bones + Space, then Reload");
        }

        void OnRemoveIK()
        {
            if (selectedItem == null || selectedItem.ikGoals == null || selectedIK == null) return;
            selectedItem.ikGoals.Remove(selectedIK);
            selectedIK = null;
            RefreshIKList();
            Rebind();
            SetStatus("removed IK goal — Save to keep this change");
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

            RectTransform brt = browserWindow.GetComponent<RectTransform>();
            if (brt != null) brt.anchoredPosition = new Vector2(0f, 0f);

            browserStatus = FindIn<Text>(browserWindow, "Label_BoneStatus");
            browserContent = FindIn<RectTransform>(browserWindow, "BoneContent");
            browserTitle = FindIn<Text>(browserWindow, "Title");

            Button close = FindIn<Button>(browserWindow, "Button_BoneClose");
            if (close != null) close.onClick.AddListener(CloseBrowser);

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
                    browserTitle.text = "Trigger Source — pick a driving blendshape";
                else if (mode == "blend")
                    browserTitle.text = "Blendshapes — pick one to add";
                else if (mode == "mesh")
                    browserTitle.text = "Mesh Objects — pick one to add";
                else
                    browserTitle.text = "Model Bones — pick one to add";
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

        const float ROW_H = 22f;
        const float INDENT = 14f;

        void RebuildTree()
        {
            if (browserContent == null) return;
            for (int i = browserContent.childCount - 1; i >= 0; i--)
                Destroy(browserContent.GetChild(i).gameObject);

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
            else if (browserMode == "mesh") BuildMeshTree(ref y);
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

            SetBrowserStatus("avatar '" + boundAvatar.name + "' — click a bone to add it");
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
            SetStatus("added bone '" + bone.name + "' — set its offsets, then Save");
        }

        // ----- mesh-object transform targets -----

        void OnAddMeshClicked()
        {
            if (selectedItem == null) { SetStatus("select or create an item first"); return; }
            OpenBrowser("mesh");
        }

        void OnMeshPicked(Transform mesh)
        {
            if (mesh == null || selectedItem == null) return;
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
            SetStatus("added mesh '" + mesh.name + "' — set its offsets, then Save");
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

            for (int i = 0; i < meshes.Count; i++)
            {
                Transform mt = meshes[i];
                GameObject row = NewRow("Mesh_" + mt.name, y);
                Transform captured = mt;
                MakeRuntimeButton(row.transform, mt.name + "  •", INDENT, 0f, 1000f, ROW_H,
                    new Color(0f, 0f, 0f, 0f), new Color(0.95f, 0.9f, 0.8f, 1f), 13,
                    delegate { OnMeshPicked(captured); });
                y += ROW_H;
            }

            SetBrowserStatus("avatar '" + boundAvatar.name + "' — click a mesh object to add it");
        }

        // ----- blendshape tree (mesh -> shapes) -----

        void BuildBlendTree(ref float y)
        {
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

            SetBrowserStatus("avatar '" + boundAvatar.name + "' — expand a mesh, click a blendshape");
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
            SetStatus("added blendshape '" + shape + "' — set its weight, then Save");
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
                PoseConfig parsed = JsonConvert.DeserializeObject<PoseConfig>(json);
                if (parsed != null)
                {
                    if (parsed.settings == null) parsed.settings = new PoseSettings();
                    if (parsed.items == null) parsed.items = new List<PoseItem>();
                    // Older configs predate meshes/keyframes — make sure no list is null.
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
                    config = parsed;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PoseStudio] failed to load config: " + e.Message);
                if (config == null) config = new PoseConfig();
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

                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(target, json);
                configPath = target;
                SetStatus("Saved to " + target);
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
