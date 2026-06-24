using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

namespace JayoJiggle
{
    // VNyan plugin entry point for the Jiggle Physics plugin. Built on the same pattern as the
    // PhysBones plugin: registers a plugin button, loads jigglephysics.json, binds to the active
    // avatar, runs the single-bone jiggle + deformation solver in LateUpdate, and shows an
    // in-VNyan tuning window. An original implementation inspired by VRChat breast-physics assets.
    public class JigglePhysicsPlugin : MonoBehaviour, VNyanInterface.IButtonClickedHandler
    {
        const string BUTTON_NAME = "Jiggle Physics";
        const string CONFIG_FILE = "jigglephysics.json";

        // Slider keys -> JiggleBoneConfig fields. Order defines the UI layout order.
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

        // Assigned by the AssetBundle build (JiggleBuild.cs).
        public GameObject windowPrefab;
        public GameObject boneBrowserPrefab;

        JiggleConfig config;
        string configPath;
        string savePath;

        GameObject boundAvatar;
        Animator boundAnimator;

        readonly List<JiggleBone> bones = new List<JiggleBone>();
        readonly List<JiggleCollider> runtimeColliders = new List<JiggleCollider>();
        readonly Dictionary<string, JiggleCollider> colliderMap =
            new Dictionary<string, JiggleCollider>();

        // ----- UI state -----
        GameObject window;
        Dropdown boneDropdown;
        Toggle enabledToggle;
        Toggle limitToggle;
        Toggle selfToggle;
        readonly Dictionary<string, Slider> sliders = new Dictionary<string, Slider>();
        readonly Dictionary<string, Text> valueLabels = new Dictionary<string, Text>();
        Text statusLabel;
        JiggleBoneConfig selected;
        bool suppressCallbacks;
        Font uiFont;

        // ----- bone browser state -----
        GameObject boneWindow;
        RectTransform boneContent;
        Text boneStatus;
        GameObject boneTreeAvatar;
        readonly Dictionary<Transform, bool> boneExpanded = new Dictionary<Transform, bool>();

        void Awake()
        {
            uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            try { VNyanInterface.VNyanInterface.VNyanUI.registerPluginButton(BUTTON_NAME, this); }
            catch (Exception e) { Debug.LogWarning("[Jiggle] registerPluginButton failed: " + e.Message); }

            ResolveConfigPath();
            LoadConfig();
            SetupWindow();
            SetupBoneBrowser();
            Debug.Log("[Jiggle] initialized. Config: " + configPath);
        }

        public void pluginButtonClicked()
        {
            if (window == null)
            {
                LoadConfig();
                RebindAll();
                return;
            }

            bool show = !window.activeSelf;
            window.SetActive(show);
            if (show)
            {
                window.transform.SetAsLastSibling();
                RefreshBoneList();
            }
        }

        void LateUpdate()
        {
            if (config == null || config.settings == null || !config.settings.enabled) return;

            EnsureAvatar();
            if (boundAvatar == null) return;

            float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
            if (dt <= 0f) return;

            Vector3 gravityDir = JiggleUtil.ToVector3(config.settings.gravityDir, Vector3.down);
            if (gravityDir.sqrMagnitude < 1e-8f) gravityDir = Vector3.down;
            int substeps = Mathf.Clamp(config.settings.substeps, 1, 8);
            float minS = config.settings.minScale;
            float maxS = config.settings.maxScale;
            float spd = config.settings.scaleSpeed;

            for (int i = 0; i < bones.Count; i++)
            {
                bones[i].SetDeformLimits(minS, maxS, spd);
                bones[i].Solve(dt, substeps, gravityDir);
            }
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
                Debug.LogWarning("[Jiggle] instantiateUIPrefab failed: " + e.Message);
                window = null;
            }
            if (window == null) return;

            RectTransform wrt = window.GetComponent<RectTransform>();
            if (wrt != null) wrt.anchoredPosition = new Vector2(0f, 0f);

            boneDropdown = FindControl<Dropdown>("Dropdown_Bone");
            enabledToggle = FindControl<Toggle>("Toggle_Enabled");
            limitToggle = FindControl<Toggle>("Toggle_Limit");
            selfToggle = FindControl<Toggle>("Toggle_Self");
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

            if (boneDropdown != null)
                boneDropdown.onValueChanged.AddListener(OnBoneSelected);
            if (enabledToggle != null)
                enabledToggle.onValueChanged.AddListener(OnEnabledToggled);
            if (limitToggle != null)
                limitToggle.onValueChanged.AddListener(OnLimitToggled);
            if (selfToggle != null)
                selfToggle.onValueChanged.AddListener(OnSelfToggled);

            foreach (KeyValuePair<string, Slider> kv in sliders)
            {
                string key = kv.Key;
                kv.Value.onValueChanged.AddListener(delegate(float v) { OnSliderChanged(key, v); });
            }

            WireButton("Button_Bones", OnBonesClicked);
            WireButton("Button_Remove", OnRemoveClicked);
            WireButton("Button_Reload", OnReloadClicked);
            WireButton("Button_Save", OnSaveClicked);
            WireButton("Button_Close", OnCloseClicked);

            RefreshBoneList();
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

        void RefreshBoneList()
        {
            if (config == null) return;

            if (enabledToggle != null)
            {
                suppressCallbacks = true;
                enabledToggle.isOn = config.settings != null && config.settings.enabled;
                suppressCallbacks = false;
            }

            if (boneDropdown == null) return;

            List<string> opts = new List<string>();
            if (config.bones != null)
                for (int i = 0; i < config.bones.Count; i++)
                {
                    JiggleBoneConfig c = config.bones[i];
                    string nm = (c != null && !string.IsNullOrEmpty(c.name)) ? c.name : ("bone " + i);
                    opts.Add(nm);
                }

            suppressCallbacks = true;
            boneDropdown.ClearOptions();
            boneDropdown.AddOptions(opts);
            boneDropdown.value = 0;
            boneDropdown.RefreshShownValue();
            suppressCallbacks = false;

            if (opts.Count > 0) SelectBone(0);
            else selected = null;
        }

        void OnBoneSelected(int index)
        {
            if (suppressCallbacks) return;
            SelectBone(index);
        }

        void SelectBone(int index)
        {
            if (config == null || config.bones == null ||
                index < 0 || index >= config.bones.Count)
            {
                selected = null;
                return;
            }
            selected = config.bones[index];
            PushToUI();
        }

        void PushToUI()
        {
            if (selected == null) return;
            suppressCallbacks = true;

            SetSlider("weight", selected.weight);
            SetSlider("bounce", selected.bounce);
            SetSlider("stiffness", selected.stiffness);
            SetSlider("damping", selected.damping);
            SetSlider("pull", selected.pull);
            SetSlider("maxAngle", selected.maxAngle);
            SetSlider("stretch", selected.stretch);
            SetSlider("squish", selected.squish);
            SetSlider("radius", selected.radius);

            if (limitToggle != null)
                limitToggle.isOn = string.Equals(selected.limitType, "angle",
                    StringComparison.OrdinalIgnoreCase);
            if (selfToggle != null)
                selfToggle.isOn = selected.selfCollide;

            suppressCallbacks = false;
        }

        void SetSlider(string key, float value)
        {
            Slider s;
            if (sliders.TryGetValue(key, out s) && s != null) s.value = value;
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
            if (suppressCallbacks || selected == null) return;
            switch (key)
            {
                case "weight": selected.weight = v; break;
                case "bounce": selected.bounce = v; break;
                case "stiffness": selected.stiffness = v; break;
                case "damping": selected.damping = v; break;
                case "pull": selected.pull = v; break;
                case "maxAngle": selected.maxAngle = v; break;
                case "stretch": selected.stretch = v; break;
                case "squish": selected.squish = v; break;
                case "radius": selected.radius = v; break;
            }
            UpdateValueLabel(key, v);
            // JiggleBone reads its config live each substep, so edits apply instantly.
        }

        void OnEnabledToggled(bool on)
        {
            if (suppressCallbacks) return;
            if (config != null && config.settings != null) config.settings.enabled = on;
            if (!on) RestoreAll();
        }

        void OnLimitToggled(bool on)
        {
            if (suppressCallbacks || selected == null) return;
            selected.limitType = on ? "angle" : "none";
        }

        void OnSelfToggled(bool on)
        {
            if (suppressCallbacks || selected == null) return;
            selected.selfCollide = on;
            RebindAll(); // partner links are established at bind time
        }

        void OnReloadClicked()
        {
            LoadConfig();
            RebindAll();
            RefreshBoneList();
            SetStatus("Reloaded " + Path.GetFileName(configPath));
        }

        // Remove the currently selected jiggle bone (e.g. one added by mistake).
        void OnRemoveClicked()
        {
            if (config == null || config.bones == null || config.bones.Count == 0)
            {
                SetStatus("nothing to remove");
                return;
            }
            if (selected == null || boneDropdown == null)
            {
                SetStatus("no bone selected");
                return;
            }

            int idx = config.bones.IndexOf(selected);
            if (idx < 0) idx = boneDropdown.value;
            if (idx < 0 || idx >= config.bones.Count)
            {
                SetStatus("no bone selected");
                return;
            }

            string removed = selected.name;
            config.bones.RemoveAt(idx);

            RebindAll();
            RefreshBoneList();

            // Reselect a sensible neighbour so the window keeps showing a valid bone.
            if (config.bones.Count > 0 && boneDropdown != null)
            {
                int next = Mathf.Clamp(idx, 0, config.bones.Count - 1);
                suppressCallbacks = true;
                boneDropdown.value = next;
                boneDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectBone(next);
            }

            SetStatus("removed jiggle '" + removed + "' — Save to keep this change");
        }

        void OnSaveClicked() { SaveConfig(); }

        void OnCloseClicked() { if (window != null) window.SetActive(false); }

        void SetStatus(string msg)
        {
            if (statusLabel != null) statusLabel.text = msg;
            Debug.Log("[Jiggle] " + msg);
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
                Debug.LogWarning("[Jiggle] instantiateUIPrefab (bone browser) failed: " + e.Message);
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

            for (int i = boneContent.childCount - 1; i >= 0; i--)
                Destroy(boneContent.GetChild(i).gameObject);

            if (boundAvatar == null)
            {
                SetBoneStatus("no avatar loaded");
                boneContent.sizeDelta = new Vector2(0f, 0f);
                return;
            }

            if (!ReferenceEquals(boneTreeAvatar, boundAvatar))
            {
                boneExpanded.Clear();
                boneTreeAvatar = boundAvatar;
                boneExpanded[boundAvatar.transform] = true;
            }

            float y = 0f;
            AddBoneRow(boundAvatar.transform, 0, ref y);

            boneContent.sizeDelta = new Vector2(0f, y);
            SetBoneStatus("avatar '" + boundAvatar.name + "' — click a bone to add a jiggle");
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

            if (hasChildren)
            {
                Transform captured = bone;
                MakeRuntimeButton(row.transform, expanded ? "-" : "+",
                    indent, 0f, BONE_INDENT + 2f, BONE_ROW_H,
                    new Color(0.25f, 0.25f, 0.30f, 1f), Color.white, 13,
                    delegate { ToggleBoneExpanded(captured); });
            }

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

        // A bone was chosen: add a new jiggle bone rooted at it and refresh the tuning window.
        void OnBonePicked(Transform bone)
        {
            if (bone == null || config == null) return;
            if (config.bones == null) config.bones = new List<JiggleBoneConfig>();

            JiggleBoneConfig bc = new JiggleBoneConfig();
            bc.name = UniqueBoneName(bone.name);
            bc.bone = bone.name;
            config.bones.Add(bc);

            RebindAll();
            RefreshBoneList();

            int idx = config.bones.Count - 1;
            if (boneDropdown != null && idx >= 0 && idx < boneDropdown.options.Count)
            {
                suppressCallbacks = true;
                boneDropdown.value = idx;
                boneDropdown.RefreshShownValue();
                suppressCallbacks = false;
                SelectBone(idx);
            }

            SetBoneStatus("added jiggle '" + bc.name + "' — tune it in the main window, then Save");
            SetStatus("added jiggle '" + bc.name + "' (bone: " + bc.bone + ")");
        }

        string UniqueBoneName(string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "jiggle";
            if (!BoneNameExists(baseName)) return baseName;
            for (int n = 2; n < 1000; n++)
            {
                string candidate = baseName + " " + n;
                if (!BoneNameExists(candidate)) return candidate;
            }
            return baseName + " " + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        bool BoneNameExists(string name)
        {
            if (config == null || config.bones == null) return false;
            for (int i = 0; i < config.bones.Count; i++)
            {
                JiggleBoneConfig c = config.bones[i];
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
            catch { /* not ready */ }

            if (av == null) { boundAvatar = null; return; }
            if (ReferenceEquals(av, boundAvatar)) return;

            boundAvatar = av;
            boundAnimator = av.GetComponentInChildren<Animator>();
            RebindAll();
            Debug.Log("[Jiggle] bound to avatar '" + av.name + "'");
        }

        void RestoreAll()
        {
            for (int i = 0; i < bones.Count; i++) bones[i].Restore();
        }

        void RebindAll()
        {
            RestoreAll();
            bones.Clear();
            runtimeColliders.Clear();
            colliderMap.Clear();

            if (boundAvatar == null || config == null) return;

            if (config.colliders != null)
            {
                foreach (JiggleColliderConfig cc in config.colliders)
                {
                    JiggleCollider col = BuildCollider(cc);
                    if (col == null) continue;
                    runtimeColliders.Add(col);
                    if (!string.IsNullOrEmpty(cc.name))
                        colliderMap[cc.name.ToLowerInvariant()] = col;
                }
            }

            if (config.bones != null)
            {
                foreach (JiggleBoneConfig bc in config.bones)
                {
                    Transform boneT = JiggleUtil.Find(boundAvatar.transform, boundAnimator, bc.bone);
                    if (boneT == null)
                    {
                        Debug.LogWarning(string.Format(
                            "[Jiggle] bone '{0}': transform '{1}' not found", bc.name, bc.bone));
                        continue;
                    }

                    List<JiggleCollider> cols = new List<JiggleCollider>();
                    if (bc.colliders != null)
                        foreach (string cn in bc.colliders)
                        {
                            JiggleCollider c;
                            if (!string.IsNullOrEmpty(cn) &&
                                colliderMap.TryGetValue(cn.ToLowerInvariant(), out c))
                                cols.Add(c);
                        }

                    JiggleBone jb = new JiggleBone(bc);
                    if (jb.Bind(boneT, cols)) bones.Add(jb);
                    else Debug.LogWarning("[Jiggle] bone '" + bc.name + "' failed to bind");
                }
            }

            // Pair up self-colliding bones (e.g. left vs right) so they push apart.
            for (int i = 0; i < bones.Count; i++)
                for (int k = 0; k < bones.Count; k++)
                    if (i != k) bones[i].AddPartner(bones[k]);
        }

        JiggleCollider BuildCollider(JiggleColliderConfig cc)
        {
            if (cc == null) return null;

            JiggleCollider col = new JiggleCollider();
            string t = string.IsNullOrEmpty(cc.type) ? "sphere" : cc.type.ToLowerInvariant();
            if (t == "capsule") col.type = JiggleColliderType.Capsule;
            else if (t == "plane") col.type = JiggleColliderType.Plane;
            else col.type = JiggleColliderType.Sphere;

            col.bone = string.IsNullOrEmpty(cc.bone)
                ? null
                : JiggleUtil.Find(boundAvatar.transform, boundAnimator, cc.bone);
            col.offset = JiggleUtil.ToVector3(cc.offset, Vector3.zero);
            col.radius = cc.radius;
            col.height = cc.height;
            if (cc.axis != null) col.axis = JiggleUtil.ToVector3(cc.axis, Vector3.up);
            if (cc.offsetEnd != null)
            {
                col.offsetEnd = JiggleUtil.ToVector3(cc.offsetEnd, Vector3.zero);
                col.useEndPoint = true;
            }
            return col;
        }

        // ----- config IO -----

        void ResolveConfigPath()
        {
            string vnyanRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            savePath = Path.Combine(Application.persistentDataPath, CONFIG_FILE);

            string[] candidates =
            {
                savePath,
                Path.Combine(vnyanRoot, CONFIG_FILE),
                Path.Combine(vnyanRoot, Path.Combine("Items", Path.Combine("Assemblies", Path.Combine("JigglePhysics", CONFIG_FILE)))),
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
                    Debug.LogWarning("[Jiggle] no config found; wrote a starter file at " + configPath);
                }

                string json = File.ReadAllText(configPath);
                JiggleConfig parsed = JsonConvert.DeserializeObject<JiggleConfig>(json);
                if (parsed != null)
                {
                    if (parsed.settings == null) parsed.settings = new JiggleSettings();
                    config = parsed;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Jiggle] failed to load config: " + e.Message);
                if (config == null) config = new JiggleConfig();
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
                Debug.LogError("[Jiggle] failed to save config: " + e.Message);
                SetStatus("save failed: " + e.Message);
            }
        }

        void WriteExampleConfig(string path)
        {
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                JiggleConfig example = new JiggleConfig();

                JiggleBoneConfig l = new JiggleBoneConfig();
                l.name = "left";
                l.bone = "Bust_L";   // rename to your model's bone (e.g. Breast_L, 胸_L)
                example.bones.Add(l);

                JiggleBoneConfig r = new JiggleBoneConfig();
                r.name = "right";
                r.bone = "Bust_R";
                example.bones.Add(r);

                File.WriteAllText(path, JsonConvert.SerializeObject(example, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError("[Jiggle] could not write example config: " + e.Message);
            }
        }
    }
}
