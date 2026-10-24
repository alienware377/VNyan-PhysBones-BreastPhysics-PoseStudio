using System;
using System.Collections.Generic;
using UnityEngine;

namespace PoseStudio
{
    public static class PoseUtil
    {
        // Resolve a bone by Unity humanoid enum name first (e.g. "Head", "Hips"), then
        // fall back to a case-insensitive transform-name search over the avatar.
        public static Transform Find(Transform root, Animator anim, string name)
        {
            if (string.IsNullOrEmpty(name) || root == null) return null;

            if (anim != null && anim.isHuman)
            {
                HumanBodyBones hb;
                if (Enum.TryParse(name, true, out hb) && hb != HumanBodyBones.LastBone)
                {
                    Transform t = anim.GetBoneTransform(hb);
                    if (t != null) return t;
                }
            }

            return FindRecursive(root, name.ToLowerInvariant());
        }

        static Transform FindRecursive(Transform t, string lowerName)
        {
            if (t.name.ToLowerInvariant() == lowerName) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform r = FindRecursive(t.GetChild(i), lowerName);
                if (r != null) return r;
            }
            return null;
        }

        public static Vector3 ToVector3(float[] a, Vector3 fallback)
        {
            if (a == null || a.Length < 3) return fallback;
            return new Vector3(a[0], a[1], a[2]);
        }

        // Find a SkinnedMeshRenderer by name. If meshName is empty, returns the first
        // renderer that owns the given blendshape (or null).
        public static SkinnedMeshRenderer FindRenderer(GameObject avatar, string meshName, string shape)
        {
            if (avatar == null) return null;
            SkinnedMeshRenderer[] rends = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            if (!string.IsNullOrEmpty(meshName))
            {
                string lower = meshName.ToLowerInvariant();
                for (int i = 0; i < rends.Length; i++)
                    if (rends[i] != null && rends[i].name.ToLowerInvariant() == lower)
                        return rends[i];
                if (string.IsNullOrEmpty(shape)) return null;

                // The named mesh is not on THIS avatar (an animation made on a model whose face
                // mesh was "Face" playing on one where it is "Head Face"). Use a mesh that has the
                // shape, preferring one whose name contains the wanted name, so face shapes land
                // on the face rather than on a tongue or hair mesh that shares the shape names.
                SkinnedMeshRenderer any = null;
                for (int i = 0; i < rends.Length; i++)
                {
                    SkinnedMeshRenderer r = rends[i];
                    if (r == null || r.sharedMesh == null || r.sharedMesh.GetBlendShapeIndex(shape) < 0) continue;
                    if (r.name.ToLowerInvariant().Contains(lower)) return r;
                    if (any == null) any = r;
                }
                return any;
            }

            if (!string.IsNullOrEmpty(shape))
                for (int i = 0; i < rends.Length; i++)
                {
                    SkinnedMeshRenderer r = rends[i];
                    if (r != null && r.sharedMesh != null &&
                        r.sharedMesh.GetBlendShapeIndex(shape) >= 0)
                        return r;
                }

            return null;
        }

        // Titles and section headers baked into the .vnobj still carry long dashes
        // (e.g. "Pose Studio: Toggles" written with an em dash); show them as ": " or nothing.
        public static void CleanDashes(GameObject root)
        {
            if (root == null) return;
            UnityEngine.UI.Text[] texts = root.GetComponentsInChildren<UnityEngine.UI.Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                string s = texts[i].text;
                if (string.IsNullOrEmpty(s) || s.IndexOf('\u2014') < 0) continue;
                texts[i].text = s.Replace(" \u2014 ", ": ").Replace("\u2014", "").Trim();
            }
        }

        // Unity's dropdown list scrolls ONE pixel per mouse-wheel notch by default, which crawls
        // through a long item list. Give every dropdown under `root` a sensible speed.
        public static void FastDropdownScroll(GameObject root, float pixelsPerNotch)
        {
            if (root == null) return;
            UnityEngine.UI.Dropdown[] dds = root.GetComponentsInChildren<UnityEngine.UI.Dropdown>(true);
            for (int i = 0; i < dds.Length; i++)
            {
                if (dds[i] == null || dds[i].template == null) continue;
                UnityEngine.UI.ScrollRect sr = dds[i].template.GetComponent<UnityEngine.UI.ScrollRect>();
                if (sr != null) sr.scrollSensitivity = pixelsPerNotch;
            }
        }

        public static List<SkinnedMeshRenderer> Renderers(GameObject avatar)
        {
            List<SkinnedMeshRenderer> list = new List<SkinnedMeshRenderer>();
            if (avatar == null) return list;
            SkinnedMeshRenderer[] rends = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < rends.Length; i++)
                if (rends[i] != null && rends[i].sharedMesh != null &&
                    rends[i].sharedMesh.blendShapeCount > 0)
                    list.Add(rends[i]);
            return list;
        }

        // Every renderer GameObject transform on the avatar (skinned + static meshes).
        // Used to pick a mesh object to transform. Returned in hierarchy order, de-duped.
        public static List<Transform> MeshTransforms(GameObject avatar)
        {
            List<Transform> list = new List<Transform>();
            if (avatar == null) return list;
            Renderer[] rends = avatar.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (!list.Contains(r.transform)) list.Add(r.transform);
            }
            return list;
        }

        // Find a mesh object's transform by GameObject name (case-insensitive).
        public static Transform FindMeshTransform(GameObject avatar, string name)
        {
            if (avatar == null || string.IsNullOrEmpty(name)) return null;
            string lower = name.ToLowerInvariant();
            Renderer[] rends = avatar.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rends.Length; i++)
            {
                Renderer r = rends[i];
                if (r == null) continue;
                if (!(r is SkinnedMeshRenderer || r is MeshRenderer)) continue;
                if (r.name.ToLowerInvariant() == lower) return r.transform;
            }
            return null;
        }
    }
}
