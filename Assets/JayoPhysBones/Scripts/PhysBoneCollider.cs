using UnityEngine;

namespace JayoPhysBones
{
    public enum ColliderType { Sphere, Capsule, Plane }

    // Runtime collider that pushes simulated bone tips out of its volume.
    // Sphere/Capsule push outward; Plane keeps tips on the positive side of its normal.
    public class PhysBoneCollider
    {
        public ColliderType type = ColliderType.Sphere;
        public Transform bone;            // null => offset/axis are world-space
        public Vector3 offset;            // local (or world) center
        public Vector3 offsetEnd;         // capsule second endpoint when useEndPoint
        public Vector3 axis = Vector3.up; // capsule axis / plane normal
        public float radius = 0.1f;
        public float height = 0f;
        public bool useEndPoint = false;

        Vector3 WorldPoint(Vector3 local)
        {
            return bone != null ? bone.TransformPoint(local) : local;
        }

        Vector3 WorldDir(Vector3 local)
        {
            Vector3 d = bone != null ? bone.TransformDirection(local) : local;
            return d.sqrMagnitude > 1e-12f ? d.normalized : Vector3.up;
        }

        // Returns the two sphere centers that define a capsule's core segment.
        void CapsuleSegment(out Vector3 a, out Vector3 b)
        {
            Vector3 c = WorldPoint(offset);
            if (useEndPoint)
            {
                a = c;
                b = WorldPoint(offsetEnd);
                return;
            }
            Vector3 dir = WorldDir(axis);
            float half = Mathf.Max(0f, height * 0.5f - radius);
            a = c + dir * half;
            b = c - dir * half;
        }

        public void Resolve(ref Vector3 p, float boneRadius)
        {
            switch (type)
            {
                case ColliderType.Sphere:
                {
                    float r = radius + boneRadius;
                    Vector3 c = WorldPoint(offset);
                    Vector3 d = p - c;
                    float m = d.magnitude;
                    if (m < r)
                        p = m > 1e-6f ? c + d * (r / m) : c + Vector3.up * r;
                    break;
                }
                case ColliderType.Capsule:
                {
                    float r = radius + boneRadius;
                    Vector3 a, b;
                    CapsuleSegment(out a, out b);
                    Vector3 cp = ClosestOnSegment(a, b, p);
                    Vector3 d = p - cp;
                    float m = d.magnitude;
                    if (m < r)
                        p = m > 1e-6f ? cp + d * (r / m) : cp + Vector3.up * r;
                    break;
                }
                case ColliderType.Plane:
                {
                    Vector3 c = WorldPoint(offset);
                    Vector3 n = WorldDir(axis);
                    float dist = Vector3.Dot(p - c, n);
                    if (dist < boneRadius)
                        p += n * (boneRadius - dist);
                    break;
                }
            }
        }

        static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return a + ab * t;
        }
    }
}
