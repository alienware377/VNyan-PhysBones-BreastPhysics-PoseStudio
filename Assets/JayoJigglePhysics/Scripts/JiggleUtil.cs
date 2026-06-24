using System;
using UnityEngine;

namespace JayoJiggle
{
    public static class JiggleUtil
    {
        // Resolves a bone by Unity humanoid enum name first (e.g. "Chest"), then falls back
        // to a case-insensitive transform-name search over the avatar hierarchy.
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
    }
}
