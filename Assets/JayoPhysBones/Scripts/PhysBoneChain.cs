using System.Collections.Generic;
using UnityEngine;

namespace JayoPhysBones
{
    // A simulated bone chain. Implements a PhysBone-style solver:
    // each bone with exactly one included child becomes a "joint" that aims at the
    // simulated position of that child (VRChat multiChildType = Ignore behaviour, so
    // a bone with several children is treated as a fixed split point and each branch
    // is solved independently). Verlet integration + position constraints give the
    // pull / spring / stiffness / gravity / immobile / limit / collider behaviours.
    public class PhysBoneChain
    {
        class Joint
        {
            public Transform t;            // bone that gets rotated
            public Vector3 childLocalPos;  // child.localPosition at bind (defines the aim axis + rest length)
            public Vector3 tip;            // simulated world position of the child
            public Vector3 prevTip;
        }

        public string name;
        public int substeps = 2;
        public Vector3 gravityDir = Vector3.down;

        readonly ChainConfig cfg;
        Transform root;
        List<PhysBoneCollider> colliders;

        readonly List<Joint> joints = new List<Joint>();
        readonly List<Transform> allBones = new List<Transform>();      // every included bone, for per-frame rest reset
        readonly List<Quaternion> bindLocalRot = new List<Quaternion>();

        Vector3 prevRootPos;
        bool bound;

        public bool IsBound { get { return bound; } }
        public int JointCount { get { return joints.Count; } }

        public PhysBoneChain(ChainConfig c)
        {
            cfg = c;
            name = c.name;
        }

        public bool Bind(Transform rootBone, HashSet<string> ignoreLower, List<PhysBoneCollider> cols)
        {
            joints.Clear();
            allBones.Clear();
            bindLocalRot.Clear();

            root = rootBone;
            colliders = cols;
            if (root == null) return false;

            BuildJoints(root, ignoreLower);

            prevRootPos = root.position;
            bound = joints.Count > 0;
            return bound;
        }

        void BuildJoints(Transform t, HashSet<string> ignoreLower)
        {
            List<Transform> kids = new List<Transform>();
            for (int i = 0; i < t.childCount; i++)
            {
                Transform c = t.GetChild(i);
                if (ignoreLower != null && ignoreLower.Contains(c.name.ToLowerInvariant())) continue;
                kids.Add(c);
            }

            allBones.Add(t);
            bindLocalRot.Add(t.localRotation);

            if (kids.Count == 1)
            {
                Joint j = new Joint();
                j.t = t;
                j.childLocalPos = kids[0].localPosition;
                j.tip = kids[0].position;
                j.prevTip = j.tip;
                joints.Add(j);
            }
            // kids.Count == 0 (leaf) or > 1 (split point): t is a fixed anchor, not aimed.

            for (int i = 0; i < kids.Count; i++)
                BuildJoints(kids[i], ignoreLower);
        }

        public void Solve(float dt)
        {
            if (!bound) return;

            Vector3 rootDelta = root.position - prevRootPos;
            prevRootPos = root.position;

            float immobile = Mathf.Clamp01(cfg.immobile);
            if (immobile > 0f)
            {
                // Shift history along with the avatar so root motion is partly ignored.
                for (int i = 0; i < joints.Count; i++)
                    joints[i].prevTip += rootDelta * immobile;
            }

            int sub = Mathf.Clamp(substeps, 1, 8);
            float sdt = dt / sub;
            for (int s = 0; s < sub; s++)
                Substep(sdt);
        }

        void Substep(float dt)
        {
            float damping = Mathf.Lerp(0.20f, 0.02f, Mathf.Clamp01(cfg.spring));
            float pullStrength = Mathf.Clamp01(cfg.pull) * 0.4f;
            float stiffBlend = Mathf.Clamp01(cfg.stiffness) * 0.3f;
            float gPow = cfg.gravity * 9.8f;
            float falloff = Mathf.Clamp01(cfg.gravityFalloff);
            float maxStretch = Mathf.Max(0f, cfg.maxStretch);
            bool limit = cfg.limitType == "angle";
            float boneRadius = Mathf.Max(0f, cfg.radius);

            // Reset every included bone to its rest pose so each joint's rest direction
            // is measured from its (already-aimed) parent this substep.
            for (int i = 0; i < allBones.Count; i++)
                allBones[i].localRotation = bindLocalRot[i];

            for (int i = 0; i < joints.Count; i++)
            {
                Joint j = joints[i];
                Vector3 parentPos = j.t.position;
                Vector3 restTip = j.t.TransformPoint(j.childLocalPos);
                float boneLen = (restTip - parentPos).magnitude;
                if (boneLen < 1e-6f) continue;

                // Verlet inertia.
                Vector3 vel = (j.tip - j.prevTip) * (1f - damping);

                // Gravity with rest-pose falloff.
                float gFactor = 1f;
                if (falloff > 0f)
                {
                    float disp = Mathf.Clamp01((j.tip - restTip).magnitude / boneLen);
                    gFactor = Mathf.Lerp(1f, disp, falloff);
                }
                Vector3 g = gravityDir * (gPow * gFactor) * dt * dt;

                Vector3 next = j.tip + vel + g;

                // Pull back toward the animated rest position.
                next = Vector3.Lerp(next, restTip, pullStrength);

                // Length constraint (allow stretch up to maxStretch, no squish).
                Vector3 dir = next - parentPos;
                float dlen = dir.magnitude;
                float L = Mathf.Clamp(dlen, boneLen, boneLen * (1f + maxStretch));
                next = dlen > 1e-6f ? parentPos + dir * (L / dlen) : restTip;

                // Stiffness: bend the bone back toward its rest direction.
                if (stiffBlend > 0f)
                {
                    Vector3 nd = (next - parentPos).normalized;
                    Vector3 rd = (restTip - parentPos).normalized;
                    Vector3 bd = Vector3.Slerp(nd, rd, stiffBlend).normalized;
                    next = parentPos + bd * L;
                }

                // Angle limit cone.
                if (limit)
                {
                    Vector3 rd = restTip - parentPos;
                    Vector3 nd = next - parentPos;
                    float ang = Vector3.Angle(rd, nd);
                    if (ang > cfg.maxAngle && ang > 1e-4f)
                    {
                        Vector3 cd = Vector3.Slerp(rd.normalized, nd.normalized, cfg.maxAngle / ang).normalized;
                        next = parentPos + cd * L;
                    }
                }

                // Colliders, then restore length so collisions don't shorten the bone.
                if (colliders != null && colliders.Count > 0)
                {
                    for (int ci = 0; ci < colliders.Count; ci++)
                        colliders[ci].Resolve(ref next, boneRadius);

                    Vector3 d2 = next - parentPos;
                    float l2 = d2.magnitude;
                    if (l2 > 1e-6f) next = parentPos + d2 * (L / l2);
                }

                j.prevTip = j.tip;
                j.tip = next;

                // Aim the bone so its rest child-direction points at the solved tip.
                Vector3 restDirW = restTip - parentPos;
                Vector3 newDirW = next - parentPos;
                if (restDirW.sqrMagnitude > 1e-12f && newDirW.sqrMagnitude > 1e-12f)
                    j.t.rotation = Quaternion.FromToRotation(restDirW, newDirW) * j.t.rotation;
            }
        }
    }
}
