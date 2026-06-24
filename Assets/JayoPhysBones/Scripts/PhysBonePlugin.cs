using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

namespace JayoPhysBones
{
    // VNyan plugin entry point. Attach this MonoBehaviour to the plugin's Custom Object
    // (via the VNyan SDK). It registers a plugin button, loads physbones.json, binds to
    // the active avatar, runs the PhysBone simulation in LateUpdate, and shows an in-VNyan
    // window of sliders for live tuning.
    public class PhysBonePlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "VRChat PhysBones";
        const string CONFIG_FILE = "physbones.json";

        // Slider keys -> ChainConfig fields. Order also defines the UI layout order.
        static readonly string[] SLIDER_KEYS =
        {
            "pull", "spring", "stiffness", "gravity", "gravityFalloff",
            "immobile", "maxAngle", "radius", "maxStretch"
        };

        // Component-type-name substrings (lower-case) treated as native bone physics to disable.
        static readonly string[] DEFAULT_NATIVE_TOKENS =
        {
            "springbone", "dynamicbone", "magicacloth", "spcrjointdynamics"
        };

        // The UI window prefab is assigned by the AssetBundle build (PhysBoneBuild.cs).
        public GameObject windowPrefab;

        // The bone-browser prefab (scrollable tree picker), also assigned by the build.
        public GameObject boneBrowserPrefab;

        PhysBoneConfig config;
        string configPath;   // where we loaded from (may be a read-only shipped copy)
        string savePath;     // always-writable target for Save (VNyan's AppData folder)

        GameObject boundAvatar;
        Animator boundAnimator;

        readonly List<PhysBoneChain> chains = new List<PhysBoneChain>();
        readonly List<PhysBoneCollider> runtimeColliders = new List<PhysBoneCollider>();
        readonly Dictionary<string, PhysBoneCollider> colliderMap =
            new Dictionary<string, PhysBoneCollider>();

        // Native bone-physics components we have switched off (so we can restore them).
        readonly List<Behaviour> disabledNative = new List<Behaviour>();
        bool nativeDisabled;

        // Root transforms of successfully-bound chains (used to scope the native override).
        readonly List<Transform> chainRoots = new List<Transform>();

        // ----- UI state -----
        GameObject window;
        Dropdown chainDropdown;
        Toggle enabledToggle;
        Toggle limitToggle;
        Toggle disableNativeToggle;
        Toggle scopeNativeToggle;
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, Text> valueLabels = new Dictionary<string, Text>();
        Text statusLabel;
        ChainConfig selectedChain;
        bool suppressCallbacks;

        // ----- bone-browser state -----
        GameObject boneWindow;
        RectTransform boneContent;
        Text boneStatus;
        // Which subtrees are expanded in the tree view (keyed by bone Transform).
        readonly Dictionary<Transform, bool> boneExpanded = new Dictionary<Transform, bool>();
        // The avatar whose hierarchy is currently shown (to detect avatar changes).
        GameObject boneTreeAvatar;
        // Cached builtin font for runtime-generated rows.
        Font uiFont;

        void Awake()
        {
            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON_NAME, this); }
            catch (Exception e) { Debug.LogWarning("[PhysBones] registerPluginButton failed: " + e.Message); }

            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            ResolveConfigPath();
            LoadConfig();
            SetupWindow();
            SetupBoneBrowser();
            Debug.Log("[PhysBones] initialized. Config: " + configPath);
        }

        // VNyan plugin button: toggle the tuning window.
        public void pluginButtonClicked()
        {
            if (window == null)
            {
                // No window (prefab missing): fall back to hot-reload behavior.
                LoadConfig();
                RebindAll();
                return;
            }

            bool show = !window.activeSelf;
            window.SetActive(show);
            if (show)
            {
                window.transform.SetAsLastSibling();
                RefreshChainList();
            }
        }

        void LateUpdate()
        {
            if (config == null || config.settings == null || !config.settings.enabled) return;

            EnsureAvatar();
            if (boundAvatar == null) return;

            float dt = Mathf.Min(Time.deltaTime, 1f / 30f); // clamp lag spikes
            if (dt <= 0f) return;

            for (int i = 0; i < chains.Count; i++)
                chains[i].Solve(dt);
        }

        // ----- UI -----

        void SetupWindow()
        {
            if (windowPrefab == null) return;
            try
            {
                window = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(windowPrefab);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PhysBones] instantiateUIPrefab failed: " + e.Message);
                window = null;
            }
            if (window == null) return;

            RectTransform wrt = window.GetComponent<RectTransform>();
            if (wrt != null) wrt.anchoredPosition = new Vector2(0f, 0f);

            // Discover controls by name.
            chainDropdown = FindControl<Dropdown>("Dropdown_Chain");
            enabledToggle = FindControl<Toggle>("Toggle_Enabled");
            limitToggle = FindControl<Toggle>("Toggle_Limit");
            disableNativeToggle = FindControl<Toggle>("Toggle_DisableNative");
            scopeNativeToggle = FindControl<Toggle>("Toggle_ScopeNative");
            statusLabel = FindControl<Text>("Label_Status");

            sliders.Clear();
            valueLabels.Clear();
            for (int i = 0; i < SLIDER_KEYS.Length; i++)
            {
                string key = SLIDER_KEYS[i];
                Slider s = FindControl<Slider>("Slider_" + key);
                if (s != null) sliders[key] = s;
                Text vl = FindControl<Text>("Value_" + key);
                if (vl != null) valueLabels[key] = vl;
            }

            // Wire callbacks.
            if (chainDropdown != null)
                chainDropdown.onValueChanged.AddListener(OnChainSelected);

            if (enabledToggle != null)
                enabledToggle.onValueChanged.AddListener(OnEnabledToggled);

            if (limitToggle != null)
                limitToggle.onValueChanged.AddListener(OnLimitToggled);

            if (disableNativeToggle != null)
                disableNativeToggle.onValueChanged.AddListener(OnDisableNativeToggled);

            if (scopeNativeToggle != null)
                scopeNativeToggle.onValueChanged.AddListener(OnScopeNativeToggled);

            foreach (KeyValuePair<string, Slider> kv in sliders)
            {
                string key = kv.Key; // capture
                kv.Value.onValueChanged.AddListener(delegate(float v) { OnSliderChanged(key, v); });
            }

            WireButton("Button_Bones", OnBonesClicked);
            WireButton("Button_Remove", OnRemoveClicked);
            WireButton("Button_Reload", OnReloadClicked);
            WireButton("Button_Save", OnSaveClicked);
            WireButton("Button_Close", OnCloseClicked);

            RefreshChainList();

            // Hidden until the plugin button is clicked.
            window.SetActive(false);
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

        // Rebuild the chain dropdown options from the current config and reselect.
        void RefreshChainList()
        {
            if (config == null) return;

            if (enabledToggle != null || disableNativeToggle != null || scopeNativeToggle != null)
            {
                suppressCallbacks = true;
                if (enabledToggle != null)
                    enabledToggle.isOn = config.settings != null && config.settings.enabled;
                if (disableNativeToggle != null)
                    disableNativeToggle.isOn = config.settings != null && config.settings.disableNativePhysics;
                if (scopeNativeToggle != null)
                    scopeNativeToggle.isOn = config.settings != null && config.settings.nativePhysicsScoped;
                suppressCallbacks = false;
            }

            if (chainDropdown == null) return;

            List<string> opts = new List<string>();
            if (config.chains != null)
                for (int i = 0; i < config.chains.Count; i++)
                {
                    ChainConfig c = config.chains[i];
                    string nm = (c != null && !string.IsNullOrEmpty(c.name)) ? c.name : ("chain " + i);
                    opts.Add(nm);
                }

            suppressCallbacks = true;
            chainDropdown.ClearOptions();
            chainDropdown.AddOptions(opts);
            chainDropdown.value = 0;
            chainDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (opts.Count > 0) SelectChain(0);
            else selectedChain = null;
        }

        void OnChainSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectChain(index);
        }

        void SelectChain(int index)
        {
            if (config == null || config.chains == null ||
                index < 0 || index >= config.chains.Count)
            {
                selectedChain = null;
                return;
            }
            selectedChain = config.chains[index];
            PushChainToUI();
        }

        // Set every slider/toggle to match the selected chain (without firing callbacks).
        void PushChainToUI()
        {
            if (selectedChain == null) return;
            suppressCallbacks = true;

            SetSlider("pull", selectedChain.pull);
            SetSlider("spring", selectedChain.spring);
            SetSlider("stiffness", selectedChain.stiffness);
            SetSlider("gravity", selectedChain.gravity);
            SetSlider("gravityFalloff", selectedChain.gravityFalloff);
            SetSlider("immobile", selectedChain.immobile);
            SetSlider("maxAngle", selectedChain.maxAngle);
            SetSlider("radius", selectedChain.radius);
            SetSlider("maxStretch", selectedChain.maxStretch);

            if (limitToggle != null)
                limitToggle.isOn = string.Equals(selectedChain.limitType, "angle",
                    StringComparison.OrdinalIgnoreCase);

            suppressCallbacks = false;
        }

        void SetSlider(string key, float value)
        {
            Slider s;
            if (sliders.TryGetValue(key, out s) && s != null)
                s.value = value;
            UpdateValueLabel(key, value);
        }

        void UpdateValueLabel(string key, float value)
        {
            Text t;
            if (valueLabels.TryGetValue(key, out t) && t != null)
                t.text = value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        void OnSliderChanged(string key, float v)
        {
            if (suppressCallbacks || selectedChain == null) return;

            switch (key)
            {
                case "pull": selectedChain.pull = v; break;
                case "spring": selectedChain.spring = v; break;
                case "stiffness": selectedChain.stiffness = v; break;
                case "gravity": selectedChain.gravity = v; break;
                case "gravityFalloff": selectedChain.gravityFalloff = v; break;
                case "immobile": selectedChain.immobile = v; break;
                case "maxAngle": selectedChain.maxAngle = v; break;
                case "radius": selectedChain.radius = v; break;
                case "maxStretch": selectedChain.maxStretch = v; break;
            }
            UpdateValueLabel(key, v);
            // PhysBoneChain reads ChainConfig live each substep, so this is instant.
        }

        void OnEnabledToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (config != null && config.settings != null) config.settings.enabled = on;
            // Turning the plugin off restores native physics; turning it on re-applies the override.
            ApplyNativePhysicsOverride();
        }

        void OnDisableNativeToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (config != null && config.settings != null) config.settings.disableNativePhysics = on;
            ApplyNativePhysicsOverride();
        }

        void OnScopeNativeToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (config != null && config.settings != null) config.settings.nativePhysicsScoped = on;
            ApplyNativePhysicsOverride();
        }

        void OnLimitToggled(bool on)
        {
            if (suppressCallbacks || selectedChain == null) return;
            selectedChain.limitType = on ? "angle" : "none";
        }

        void OnReloadClicked()
        {
            LoadConfig();
            RebindAll();
            RefreshChainList();
            SetStatus("Reloaded " + Path.GetFileName(configPath));
        }

        // Remove the currently selected chain (e.g. one added by mistake).
        void OnRemoveClicked()
        {
            if (config == null || config.chains == null || config.chains.Count == 0)
            {
                SetStatus("nothing to remove");
                return;
            }
            if (selectedChain == null || chainDropdown == null)
            {
                SetStatus("no chain selected");
                return;
            }

            int idx = config.chains.IndexOf(selectedChain);
            if (idx < 0) idx = chainDropdown.value;
            if (idx < 0 || idx >= config.chains.Count)
            {
                SetStatus("no chain selected");
                return;
            }

            string removed = selectedChain.name;
            config.chains.RemoveAt(idx);

            RebindAll();
            RefreshChainList();

            // Reselect a sensible neighbour so the window keeps showing a valid chain.
            if (config.chains.Count > 0 && chainDropdown != null)
            {
                int next = Mathf.Clamp(idx, 0, config.chains.Count - 1);
                suppressCallbacks = true;
                chainDropdown.value = next;
                chainDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectChain(next);
            }

            SetStatus("removed chain '" + removed + "' — Save to keep this change");
        }

        void OnSaveClicked()
        {
            SaveConfig();
        }

        void OnCloseClicked()
        {
            if (window != null) window.SetActive(false);
        }

        void SetStatus(string msg)
        {
            if (statusLabel != null) statusLabel.text = msg;
            Debug.Log("[PhysBones] " + msg);
        }

        // ----- bone browser (hierarchy picker) -----

        void SetupBoneBrowser()
        {
            if (boneBrowserPrefab == null) return;
            try
            {
                boneWindow = (GameObject)VNyanInterface.VNyanInterface.VNyanUI.instantiateUIPrefab(boneBrowserPrefab);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PhysBones] instantiateUIPrefab (bone browser) failed: " + e.Message);
                boneWindow = null;
            }
            if (boneWindow == null) return;

            RectTransform brt = boneWindow.GetComponent<RectTransform>();
            if (brt != null) brt.anchoredPosition = new Vector2(0f, 0f);

            boneStatus = FindIn<Text>(boneWindow, "Label_BoneStatus");
            boneContent = FindIn<RectTransform>(boneWindow, "BoneContent");

            Button close = FindIn<Button>(boneWindow, "Button_BoneClose");
            if (close != null) close.onClick.AddListener(CloseBoneBrowser);

            boneWindow.SetActive(false);
        }

        void OnBonesClicked()
        {
            if (boneWindow == null)
            {
                SetStatus("bone browser unavailable (prefab missing)");
                return;
            }

            bool show = !boneWindow.activeSelf;
            boneWindow.SetActive(show);
            if (show)
            {
                boneWindow.transform.SetAsLastSibling();
                EnsureAvatar();
                RebuildBoneTree();
            }
        }

        void CloseBoneBrowser()
        {
            if (boneWindow != null) boneWindow.SetActive(false);
        }

        void SetBoneStatus(string msg)
        {
            if (boneStatus != null) boneStatus.text = msg;
        }

        const float BONE_ROW_H = 22f;
        const float BONE_INDENT = 14f;

        // Regenerate the collapsible tree under BoneContent for the current avatar.
        void RebuildBoneTree()
        {
            if (boneContent == null) return;

            // Clear existing rows.
            for (int i = boneContent.childCount - 1; i >= 0; i--)
                Destroy(boneContent.GetChild(i).gameObject);

            if (boundAvatar == null)
            {
                SetBoneStatus("no avatar loaded");
                boneContent.sizeDelta = new Vector2(0f, 0f);
                return;
            }

            // Reset expansion state when the avatar changes; expand the root by default.
            if (!ReferenceEquals(boneTreeAvatar, boundAvatar))
            {
                boneExpanded.Clear();
                boneTreeAvatar = boundAvatar;
                boneExpanded[boundAvatar.transform] = true;
            }

            float y = 0f;
            AddBoneRow(boundAvatar.transform, 0, ref y);

            boneContent.sizeDelta = new Vector2(0f, y);
            SetBoneStatus("avatar '" + boundAvatar.name + "' — click a bone to add a chain");
        }

        // Recursively emit a row for 'bone' and (if expanded) its children. 'y' grows downward.
        void AddBoneRow(Transform bone, int depth, ref float y)
        {
            bool hasChildren = bone.childCount > 0;
            bool expanded;
            if (!boneExpanded.TryGetValue(bone, out expanded)) expanded = false;

            float indent = depth * BONE_INDENT;

            GameObject row = new GameObject("Row_" + bone.name, typeof(RectTransform));
            RectTransform rrt = row.GetComponent<RectTransform>();
            rrt.SetParent(boneContent, false);
            rrt.anchorMin = new Vector2(0f, 1f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0f, 1f);
            rrt.offsetMin = new Vector2(0f, 0f);
            rrt.offsetMax = new Vector2(0f, 0f);
            rrt.anchoredPosition = new Vector2(0f, -y);
            rrt.sizeDelta = new Vector2(0f, BONE_ROW_H);

            // Expand/collapse toggle (only when there are children).
            if (hasChildren)
            {
                Transform captured = bone;
                MakeRuntimeButton(row.transform, expanded ? "-" : "+",
                    indent, 0f, BONE_INDENT + 2f, BONE_ROW_H,
                    new Color(0.25f, 0.25f, 0.30f, 1f), Color.white, 13,
                    delegate { ToggleBoneExpanded(captured); });
            }

            // Select button (the bone name). Picks this bone as a new chain root.
            Transform pick = bone;
            float nameX = indent + BONE_INDENT + 4f;
            string label = bone.name + (hasChildren ? "" : "  •");
            MakeRuntimeButton(row.transform, label,
                nameX, 0f, 1000f, BONE_ROW_H,
                new Color(0f, 0f, 0f, 0.0f), new Color(0.85f, 0.9f, 1f, 1f), 13,
                delegate { OnBonePicked(pick); });

            y += BONE_ROW_H;

            if (hasChildren && expanded)
                for (int i = 0; i < bone.childCount; i++)
                    AddBoneRow(bone.GetChild(i), depth + 1, ref y);
        }

        void ToggleBoneExpanded(Transform bone)
        {
            bool cur;
            if (!boneExpanded.TryGetValue(bone, out cur)) cur = false;
            boneExpanded[bone] = !cur;
            RebuildBoneTree();
        }

        // A bone was chosen: add a new chain rooted at it and refresh the tuning window.
        void OnBonePicked(Transform bone)
        {
            if (bone == null || config == null) return;
            if (config.chains == null) config.chains = new List<ChainConfig>();

            ChainConfig ch = new ChainConfig();
            ch.name = UniqueChainName(bone.name);
            ch.rootBone = bone.name;
            config.chains.Add(ch);

            RebindAll();
            RefreshChainList();

            // Select the newly added chain in the tuning window.
            int idx = config.chains.Count - 1;
            if (chainDropdown != null && idx >= 0 && idx < chainDropdown.options.Count)
            {
                suppressCallbacks = true;
                chainDropdown.value = idx;
                chainDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectChain(idx);
            }

            SetBoneStatus("added chain '" + ch.name + "' — tune it in the main window, then Save");
            SetStatus("added chain '" + ch.name + "' (root: " + ch.rootBone + ")");
        }

        string UniqueChainName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "chain";
            if (!ChainNameExists(baseName)) return baseName;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = baseName + " " + n;
                if (!ChainNameExists(candidate)) return candidate;
            }
            return baseName + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        bool ChainNameExists(string name)
        {
            if (config == null || config.chains == null) return false;
            for (int i = 0; i < config.chains.Count; i++)
            {
                ChainConfig c = config.chains[i];
                if (c != null && string.Equals(c.name, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Build a flat-colored Button with a Text label at runtime (no builtin sprites needed).
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

        // ----- avatar binding -----

        void EnsureAvatar()
        {
            GameObject av = null;
            try { av = (GameObject)VNyanInterface.VNyanInterface.VNyanAvatar.getAvatarObject(); }
            catch { /* avatar not ready yet */ }

            if (av == null) { boundAvatar = null; return; }
            if (ReferenceEquals(av, boundAvatar)) return;

            boundAvatar = av;
            boundAnimator = av.GetComponentInChildren<Animator>();
            RebindAll();
            Debug.Log("[PhysBones] bound to avatar '" + av.name + "'");
        }

        void RebindAll()
        {
            chains.Clear();
            chainRoots.Clear();
            runtimeColliders.Clear();
            colliderMap.Clear();

            if (boundAvatar == null || config == null) return;

            Vector3 gravityDir = BoneUtil.ToVector3(config.settings.gravityDir, Vector3.down);
            if (gravityDir.sqrMagnitude < 1e-8f) gravityDir = Vector3.down;
            int substeps = Mathf.Clamp(config.settings.substeps, 1, 8);

            if (config.colliders != null)
            {
                foreach (ColliderConfig cc in config.colliders)
                {
                    PhysBoneCollider col = BuildCollider(cc);
                    if (col == null) continue;
                    runtimeColliders.Add(col);
                    if (!string.IsNullOrEmpty(cc.name))
                        colliderMap[cc.name.ToLowerInvariant()] = col;
                }
            }

            if (config.chains != null)
            {
                foreach (ChainConfig ch in config.chains)
                {
                    Transform rootT = BoneUtil.Find(boundAvatar.transform, boundAnimator, ch.rootBone);
                    if (rootT == null)
                    {
                        Debug.LogWarning(string.Format(
                            "[PhysBones] chain '{0}': rootBone '{1}' not found", ch.name, ch.rootBone));
                        continue;
                    }

                    HashSet<string> ignore = new HashSet<string>();
                    if (ch.ignore != null)
                        foreach (string ig in ch.ignore)
                            if (!string.IsNullOrEmpty(ig)) ignore.Add(ig.ToLowerInvariant());

                    List<PhysBoneCollider> cols = new List<PhysBoneCollider>();
                    if (ch.colliders != null)
                        foreach (string cn in ch.colliders)
                        {
                            PhysBoneCollider c;
                            if (!string.IsNullOrEmpty(cn) &&
                                colliderMap.TryGetValue(cn.ToLowerInvariant(), out c))
                                cols.Add(c);
                        }

                    PhysBoneChain chain = new PhysBoneChain(ch);
                    chain.substeps = substeps;
                    chain.gravityDir = gravityDir;

                    if (chain.Bind(rootT, ignore, cols))
                    {
                        chains.Add(chain);
                        chainRoots.Add(rootT);
                    }
                    else
                        Debug.LogWarning(string.Format(
                            "[PhysBones] chain '{0}': no simulatable bones under '{1}'", ch.name, ch.rootBone));
                }
            }

            ApplyNativePhysicsOverride();
        }

        // ----- native (VRM SpringBone / MagicaCloth / SPCR / DynamicBone) override -----

        // Restore anything we disabled, then (if requested and the plugin is enabled) disable
        // every native bone-physics component on the avatar so only our PhysBones drive the bones.
        void ApplyNativePhysicsOverride()
        {
            // Re-enable previously disabled components (refs may be stale after a rebind; null-safe).
            for (int i = 0; i < disabledNative.Count; i++)
                if (disabledNative[i] != null) disabledNative[i].enabled = true;
            disabledNative.Clear();
            nativeDisabled = false;

            if (boundAvatar == null) return;

            bool want = config != null && config.settings != null
                        && config.settings.enabled && config.settings.disableNativePhysics;
            bool scoped = config != null && config.settings != null
                          && config.settings.nativePhysicsScoped;

            string[] tokens = GetNativeTokens();
            Behaviour[] comps = boundAvatar.GetComponentsInChildren<Behaviour>(true);
            int found = 0, off = 0, skippedScope = 0;
            for (int i = 0; i < comps.Length; i++)
            {
                Behaviour c = comps[i];
                if (c == null || c is PhysBonePlugin || c is WindowDrag) continue;
                if (!MatchesToken(c.GetType().Name, tokens)) continue;

                found++;
                if (!want || !c.enabled) continue;

                if (scoped && !ComponentDrivesOurChains(c))
                {
                    skippedScope++;
                    continue;
                }

                c.enabled = false;
                disabledNative.Add(c);
                off++;
                Debug.Log("[PhysBones] disabled native physics: "
                    + c.GetType().FullName + " on '" + c.gameObject.name + "'");
            }

            nativeDisabled = want && off > 0;
            if (want)
                Debug.Log(string.Format(
                    "[PhysBones] native physics override ON ({0}): {1} matched, {2} disabled, {3} left (out of scope)",
                    scoped ? "scoped" : "all", found, off, skippedScope));
            else if (found > 0)
                Debug.Log(string.Format(
                    "[PhysBones] native physics override OFF: {0} native physics component(s) left running", found));
        }

        // Scoped mode: does this native physics component drive any bone under one of our chains?
        // We reflect over the component's referenced Transforms and test them against the chain
        // root subtrees. If none can be found, we leave the component running (and warn), so we
        // never kill physics we couldn't positively attribute.
        bool ComponentDrivesOurChains(Behaviour c)
        {
            List<Transform> refs = new List<Transform>();
            try { CollectReferencedTransforms(c, refs); }
            catch (Exception e) { Debug.LogWarning("[PhysBones] reflect failed on " + c.GetType().Name + ": " + e.Message); }

            if (refs.Count == 0)
            {
                Debug.LogWarning("[PhysBones] scoped: could not determine bones for "
                    + c.GetType().FullName + " on '" + c.gameObject.name + "' — leaving it running");
                return false;
            }

            for (int i = 0; i < refs.Count; i++)
            {
                Transform r = refs[i];
                if (r == null) continue;
                for (int k = 0; k < chainRoots.Count; k++)
                {
                    Transform cr = chainRoots[k];
                    if (cr == null) continue;
                    if (IsDescendantOrSelf(r, cr) || IsDescendantOrSelf(cr, r)) return true;
                }
            }
            return false;
        }

        static bool IsDescendantOrSelf(Transform t, Transform ancestor)
        {
            Transform p = t;
            while (p != null)
            {
                if (p == ancestor) return true;
                p = p.parent;
            }
            return false;
        }

        // Gather every Transform a component references via its (serialized) fields, including
        // Transform, Transform[]/List<Transform>, and Transforms nested inside [Serializable]
        // data objects (e.g. MagicaCloth's SerializeData.rootBones). Bounded by depth/visited set.
        static void CollectReferencedTransforms(object root, List<Transform> outList)
        {
            HashSet<object> visited = new HashSet<object>(ReferenceComparer.Instance);
            VisitForTransforms(root, outList, visited, 0);
        }

        static void VisitForTransforms(object obj, List<Transform> outList, HashSet<object> visited, int depth)
        {
            if (obj == null || depth > 5) return;

            Transform asT = obj as Transform;
            if (asT != null) { outList.Add(asT); return; }

            // Don't walk into arbitrary scene objects (GameObjects/Components other than Transform).
            if (obj is UnityEngine.Object) return;
            if (obj is string) return;
            if (!visited.Add(obj)) return;

            IEnumerable seq = obj as IEnumerable;
            if (seq != null)
            {
                int count = 0;
                foreach (object el in seq)
                {
                    VisitForTransforms(el, outList, visited, depth + 1);
                    if (++count > 8192) break;
                }
                return;
            }

            Type ty = obj.GetType();
            if (ty.IsPrimitive || ty.IsEnum) return;
            string ns = ty.Namespace ?? "";
            // Only recurse into "interesting" data types (skip System.* and UnityEngine.* structs
            // like Vector3/Quaternion that can't contain a Transform).
            if (ns.StartsWith("System") || ns.StartsWith("UnityEngine")) return;

            FieldInfo[] fields = ty.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                object v;
                try { v = fields[i].GetValue(obj); }
                catch { continue; }
                VisitForTransforms(v, outList, visited, depth + 1);
            }
        }

        string[] GetNativeTokens()
        {
            if (config != null && config.settings != null &&
                config.settings.nativePhysicsTypes != null &&
                config.settings.nativePhysicsTypes.Count > 0)
            {
                List<string> t = new List<string>();
                foreach (string s in config.settings.nativePhysicsTypes)
                    if (!string.IsNullOrEmpty(s)) t.Add(s.ToLowerInvariant());
                if (t.Count > 0) return t.ToArray();
            }
            return DEFAULT_NATIVE_TOKENS;
        }

        static bool MatchesToken(string typeName, string[] tokens)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            string low = typeName.ToLowerInvariant();
            for (int i = 0; i < tokens.Length; i++)
                if (low.Contains(tokens[i])) return true;
            return false;
        }

        // Identity comparer so the reflection walk dedupes by reference, never by overridden Equals.
        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }
            public int GetHashCode(object o) { return RuntimeHelpers.GetHashCode(o); }
        }

        PhysBoneCollider BuildCollider(ColliderConfig cc)
        {
            if (cc == null) return null;

            PhysBoneCollider col = new PhysBoneCollider();
            string t = string.IsNullOrEmpty(cc.type) ? "sphere" : cc.type.ToLowerInvariant();
            if (t == "capsule") col.type = ColliderType.Capsule;
            else if (t == "plane") col.type = ColliderType.Plane;
            else col.type = ColliderType.Sphere;

            col.bone = string.IsNullOrEmpty(cc.bone)
                ? null
                : BoneUtil.Find(boundAvatar.transform, boundAnimator, cc.bone);
            col.offset = BoneUtil.ToVector3(cc.offset, Vector3.zero);
            col.radius = cc.radius;
            col.height = cc.height;
            if (cc.axis != null) col.axis = BoneUtil.ToVector3(cc.axis, Vector3.up);
            if (cc.offsetEnd != null)
            {
                col.offsetEnd = BoneUtil.ToVector3(cc.offsetEnd, Vector3.zero);
                col.useEndPoint = true;
            }
            return col;
        }

        // ----- config IO -----

        void ResolveConfigPath()
        {
            // Application.dataPath is <VNyan>/VNyan_Data at runtime.
            string vnyanRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            // Always-writable location (no admin needed): VNyan's persistent data folder,
            // e.g. C:\Users\<you>\AppData\LocalLow\Suvidriel\VNyan\physbones.json
            savePath = Path.Combine(Application.persistentDataPath, CONFIG_FILE);

            // Load priority: the user's writable copy wins; otherwise fall back to a copy
            // shipped alongside VNyan (read-only is fine for loading, just not for Save).
            string[] candidates =
            {
                savePath,
                Path.Combine(vnyanRoot, CONFIG_FILE),
                Path.Combine(vnyanRoot, Path.Combine("Items", Path.Combine("Assemblies", Path.Combine("PhysBones", CONFIG_FILE)))),
                Path.Combine(vnyanRoot, Path.Combine("Items", CONFIG_FILE)),
            };

            foreach (string c in candidates)
                if (File.Exists(c)) { configPath = c; return; }

            // None found: default to the writable AppData path and write a starter file there.
            configPath = savePath;
        }

        void LoadConfig()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    WriteExampleConfig(configPath);
                    Debug.LogWarning("[PhysBones] no config found; wrote a starter file at " + configPath);
                }

                string json = File.ReadAllText(configPath);
                PhysBoneConfig parsed = JsonConvert.DeserializeObject<PhysBoneConfig>(json);
                if (parsed != null)
                {
                    if (parsed.settings == null) parsed.settings = new PhysBoneSettings();
                    config = parsed;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[PhysBones] failed to load config: " + e.Message);
                if (config == null) config = new PhysBoneConfig();
            }
        }

        void SaveConfig()
        {
            try
            {
                if (config == null) { SetStatus("nothing to save"); return; }

                // Always save to the writable AppData copy (Program Files needs admin).
                string target = string.IsNullOrEmpty(savePath)
                    ? Path.Combine(Application.persistentDataPath, CONFIG_FILE)
                    : savePath;

                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(target, json);

                // Future loads/reloads should read the copy we just wrote.
                configPath = target;
                SetStatus("Saved to " + target);
            }
            catch (Exception e)
            {
                Debug.LogError("[PhysBones] failed to save config: " + e.Message);
                SetStatus("save failed: " + e.Message);
            }
        }

        void WriteExampleConfig(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                PhysBoneConfig example = new PhysBoneConfig();

                ColliderConfig head = new ColliderConfig();
                head.name = "head";
                head.type = "sphere";
                head.bone = "Head";
                head.offset = new float[] { 0f, 0.08f, 0f };
                head.radius = 0.11f;
                example.colliders.Add(head);

                ChainConfig hair = new ChainConfig();
                hair.name = "hair";
                hair.rootBone = "Hair";
                hair.pull = 0.2f;
                hair.spring = 0.2f;
                hair.stiffness = 0.2f;
                hair.gravity = 0.2f;
                hair.radius = 0.02f;
                hair.colliders.Add("head");
                example.chains.Add(hair);

                File.WriteAllText(path, JsonConvert.SerializeObject(example, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError("[PhysBones] could not write example config: " + e.Message);
            }
        }
    }
}
