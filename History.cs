using UnityEngine;

namespace SmoothNet
{
    /// <summary>
    /// Ring buffer of the local player's input-relevant pose, sampled every frame.
    /// Used to answer "where was my puller / my body L seconds ago" so we can
    /// predict how far the host's (old) state will still move.
    /// </summary>
    internal static class History
    {
        private struct Sample
        {
            public float T;
            public Vector3 Puller;
            public Vector3 BodyPos;
            public float GrabberYaw;
        }

        private const int Size = 256; // ~2-4 s at typical frame rates
        private static readonly Sample[] buf = new Sample[Size];
        private static int head = -1;
        private static int count;

        public static void Clear() { head = -1; count = 0; }

        public static void Record()
        {
            var g = PhysGrabber.instance;
            var a = PlayerAvatar.instance;
            if (!g || !a) { Clear(); return; }

            var s = new Sample
            {
                T = Time.time,
                Puller = g.physGrabPointPullerPosition,
                BodyPos = a.transform.position,
                GrabberYaw = g.transform.eulerAngles.y
            };
            if (count > 0 && Mathf.Approximately(buf[head].T, s.T)) { buf[head] = s; return; }
            head = (head + 1) % Size;
            buf[head] = s;
            if (count < Size) count++;
        }

        public static bool Has => count > 1;

        /// <summary>Interpolated sample at absolute time t (clamped to buffer range).</summary>
        public static bool At(float t, out Vector3 puller, out Vector3 bodyPos, out float yaw)
        {
            puller = default; bodyPos = default; yaw = 0;
            if (count == 0) return false;

            // walk backwards from newest
            int newer = head;
            for (int i = 1; i < count; i++)
            {
                int older = (head - i + Size) % Size;
                if (buf[older].T <= t)
                {
                    var a = buf[older]; var b = buf[newer];
                    float span = b.T - a.T;
                    float k = span > 1e-5f ? Mathf.Clamp01((t - a.T) / span) : 1f;
                    puller = Vector3.LerpUnclamped(a.Puller, b.Puller, k);
                    bodyPos = Vector3.LerpUnclamped(a.BodyPos, b.BodyPos, k);
                    yaw = Mathf.LerpAngle(a.GrabberYaw, b.GrabberYaw, k);
                    return true;
                }
                newer = older;
            }
            // older than buffer: use the oldest
            var o = buf[(head - count + 1 + Size) % Size];
            puller = o.Puller; bodyPos = o.BodyPos; yaw = o.GrabberYaw;
            return true;
        }
    }
}
