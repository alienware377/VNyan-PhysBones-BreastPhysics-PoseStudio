using System.Collections.Generic;
using UnityEngine;

namespace JayoJiggle
{
    // A single-bone jiggle solver with squash-and-stretch deformation.
    //
    // Breast/pectoral/belly bones are usually leaf bones (no child), so a chain solver has
    // nothing to aim. Instead we synthesise a virtual "tip" a short distance along the bone's
    // local forward axis and simulate THAT with Verlet integration. The bone is then rotated so
    // its forward points at the solved tip (the swing/jiggle), and its localScale is driven by
    // how far the tip has sagged (stretch) and how deep colliders are pressing in (squish),
    // with volume preservation so it reads as soft tissue rather than a balloon.
    public class JiggleBone
    {
        readonly JiggleBoneConfig cfg;

        Transform t;
        Quaternion bindLocalRot;
        Vector3 bindLocalScale;
        Vector3 tipLocal;     // rest tip in bone-local space (axisN * length)
        Vector3 axisN;        // normalized local forward axis

        Vector3 tip, prevTip; // simulated world tip
        bool bound;

        // Filled fresh at the top of each Solve() from the animated rest pose.
        Vector3 restTip0, parentPos0;
        float boneLen0;
        float framePenetration;

        // Smoothed scale multiplier (relative to bindLocalScale).
        Vector3 curMult = Vector3.one;

        List<JiggleCollider> colliders;
        readonly List<JiggleBone> partners = new List<JiggleBone>(); // self-collision peers

        public bool IsBound { get { return bound; } }
        public Vector3 Tip { get { return tip; } }
        public float SelfRadius { get { return Mathf.Max(0f, cfg.selfRadius); } }
        public string Name { get { return cfg.name; } }

        public JiggleBone(JiggleBoneConfig c) { cfg = c; }

        public void AddPartner(JiggleBone other)
        {
            if (other != null && other != this && cfg.selfCollide) partners.Add(other);
        }

        public bool Bind(Transform bone, List<JiggleCollider> cols)
        {
            colliders = cols;
            t = bone;
            partners.Clear();
            if (t == null) { bound = false; return false; }

            bindLocalRot = t.localRotation;
            bindLocalScale = t.localScale;

            axisN = JiggleUtil.ToVector3(cfg.axis, new Vector3(0f, 0f, 1f));
            if (axisN.sqrMagnitude < 1e-8f) axisN = new Vector3(0f, 0f, 1f);
            axisN = axisN.normalized;

            float len = Mathf.Max(0.005f, cfg.length);
            tipLocal = axisN * len;

            tip = t.TransformPoint(tipLocal);
            prevTip = tip;
            curMult = Vector3.one;
            bound = true;
            return true;
        }

        // Put the bone back to its authored rest rotation/scale (when the plugin is disabled).
        public void Restore()
        {
            if (!bound || t == null) return;
            t.localRotation = bindLocalRot;
            t.localScale = bindLocalScale;
            curMult = Vector3.one;
            tip = t.TransformPoint(tipLocal);
            prevTip = tip;
        }

        public void Solve(float dt, int substeps, Vector3 gravityDir)
        {
            if (!bound || t == null) return;

            // Capture the animated rest pose once per frame (drives both the spring rest target
            // and the deformation reference).
            t.localRotation = bindLocalRot;
            parentPos0 = t.position;
            restTip0 = t.TransformPoint(tipLocal);
            boneLen0 = (restTip0 - parentPos0).magnitude;
            if (boneLen0 < 1e-6f) return;

            framePenetration = 0f;

            int sub = Mathf.Clamp(substeps, 1, 8);
            float sdt = dt / sub;
            for (int s = 0; s < sub; s++)
                Substep(sdt, gravityDir);

            UpdateDeformation(dt, gravityDir);
        }

        void Substep(float dt, Vector3 gravityDir)
        {
            // Reset to rest so restTip is measured from the animated (current) pose this substep.
            t.localRotation = bindLocalRot;

            Vector3 parentPos = t.position;
            Vector3 restTip = t.TransformPoint(tipLocal);
            float boneLen = (restTip - parentPos).magnitude;
            if (boneLen < 1e-6f) return;

            float bounce = Mathf.Clamp01(cfg.bounce);
            float damp = Mathf.Clamp01(Mathf.Lerp(0.22f, 0.02f, bounce) + 0.4f * Mathf.Clamp01(cfg.damping));
            float pull = Mathf.Clamp01(cfg.pull) * 0.4f;
            float stiff = Mathf.Clamp01(cfg.stiffness) * 0.3f;
            float gPow = Mathf.Clamp01(cfg.weight) * 9.8f;
            float boneRadius = Mathf.Max(0f, cfg.radius);
            bool limit = cfg.limitType == "angle";

            // Verlet inertia + gravity.
            Vector3 vel = (tip - prevTip) * (1f - damp);
            Vector3 g = gravityDir * gPow * dt * dt;
            Vector3 next = tip + vel + g;

            // Pull toward the animated rest tip.
            next = Vector3.Lerp(next, restTip, pull);

            // Rigid length (the visible elongation is done in scale, not by stretching the arm).
            next = PinLength(parentPos, next, boneLen);

            // Stiffness: bias the swing direction back toward rest.
            if (stiff > 0f)
            {
                Vector3 nd = (next - parentPos).normalized;
                Vector3 rd = (restTip - parentPos).normalized;
                Vector3 bd = Vector3.Slerp(nd, rd, stiff).normalized;
                next = parentPos + bd * boneLen;
            }

            // Bra limiter: clamp the swing to a cone around rest.
            if (limit)
            {
                Vector3 rd = restTip - parentPos;
                Vector3 nd = next - parentPos;
                float ang = Vector3.Angle(rd, nd);
                if (ang > cfg.maxAngle && ang > 1e-4f)
                {
                    Vector3 cd = Vector3.Slerp(rd.normalized, nd.normalized, cfg.maxAngle / ang).normalized;
                    next = parentPos + cd * boneLen;
                }
            }

            // World colliders.
            if (colliders != null && colliders.Count > 0)
            {
                for (int ci = 0; ci < colliders.Count; ci++)
                {
                    float pen = colliders[ci].Resolve(ref next, boneRadius);
                    if (pen > framePenetration) framePenetration = pen;
                }
                next = PinLength(parentPos, next, boneLen);
            }

            // Self-collision (e.g. left vs right): treat each partner's tip as a sphere.
            for (int pi = 0; pi < partners.Count; pi++)
            {
                JiggleBone other = partners[pi];
                if (other == null || !other.bound) continue;
                float r = SelfRadius + other.SelfRadius;
                Vector3 d = next - other.tip;
                float m = d.magnitude;
                if (m < r)
                {
                    next = m > 1e-6f ? other.tip + d * (r / m) : other.tip + Vector3.right * r;
                    float pen = r - m;
                    if (pen > framePenetration) framePenetration = pen;
                    next = PinLength(parentPos, next, boneLen);
                }
            }

            prevTip = tip;
            tip = next;

            // Aim the bone's forward at the solved tip.
            Vector3 restDirW = restTip - parentPos;
            Vector3 newDirW = next - parentPos;
            if (restDirW.sqrMagnitude > 1e-12f && newDirW.sqrMagnitude > 1e-12f)
                t.rotation = Quaternion.FromToRotation(restDirW, newDirW) * t.rotation;
        }

        static Vector3 PinLength(Vector3 from, Vector3 p, float len)
        {
            Vector3 d = p - from;
            float m = d.magnitude;
            return m > 1e-6f ? from + d * (len / m) : from + Vector3.up * len;
        }

        // Volume-preserving squash & stretch driven by sag (gravity displacement) and squish
        // (collider penetration). Eased toward the target each frame for a soft feel.
        void UpdateDeformation(float dt, Vector3 gravityDir)
        {
            float stretch = Mathf.Clamp01(cfg.stretch);
            float squish = Mathf.Clamp01(cfg.squish);

            // How far the tip has sagged along gravity, as a fraction of bone length.
            float sag = Mathf.Clamp01(Vector3.Dot(tip - restTip0, gravityDir) / boneLen0);
            float squishAmt = Mathf.Clamp01(framePenetration / boneLen0) * squish;

            float stretchFactor = 1f + stretch * sag;
            float forward = stretchFactor * (1f - squishAmt);
            float lateral = (1f / Mathf.Sqrt(Mathf.Max(stretchFactor, 1e-3f))) * (1f + 0.5f * squishAmt);

            float lo = Mathf.Max(0.05f, cfg_minScale);
            float hi = Mathf.Max(lo, cfg_maxScale);
            forward = Mathf.Clamp(forward, lo, hi);
            lateral = Mathf.Clamp(lateral, lo, hi);

            // Distribute forward/lateral onto the bone's local axes by how forward-aligned each is.
            Vector3 mult;
            mult.x = Mathf.Lerp(lateral, forward, Mathf.Abs(axisN.x));
            mult.y = Mathf.Lerp(lateral, forward, Mathf.Abs(axisN.y));
            mult.z = Mathf.Lerp(lateral, forward, Mathf.Abs(axisN.z));

            float k = Mathf.Clamp01(cfg_scaleSpeed * dt);
            curMult = Vector3.Lerp(curMult, mult, k);

            t.localScale = Vector3.Scale(bindLocalScale, curMult);
        }

        // Global deformation clamps/speed are pushed in by the plugin before Solve.
        float cfg_minScale = 0.65f, cfg_maxScale = 1.6f, cfg_scaleSpeed = 14f;
        public void SetDeformLimits(float minScale, float maxScale, float scaleSpeed)
        {
            cfg_minScale = minScale;
            cfg_maxScale = maxScale;
            cfg_scaleSpeed = Mathf.Max(0.01f, scaleSpeed);
        }
    }
}
