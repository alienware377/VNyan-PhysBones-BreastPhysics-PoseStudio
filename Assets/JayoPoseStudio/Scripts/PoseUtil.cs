using System;
using System.Collections.Generic;
using UnityEngine;

namespace JayoPoseStudio
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
                return null;
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
