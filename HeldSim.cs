using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SmoothNet
{
    /// <summary>Counts hard snaps (teleports) of objects we simulate/predict so field reports are actionable.</summary>
    internal static class SnapStats
    {
        public static int Count;
        public static string LastName = "";
        public static string LastKind = "";
        public static float LastError;
        public static float LastTime = -999f;

        private static float lastLog = -999f;
        private static int suppressed;

        public static void Report(string kind, string name, float err)
        {
            Count++;
            LastName = name;
            LastKind = kind;
            LastError = err;
            LastTime = Time.unscaledTime;
            if (Time.unscaledTime - lastLog < 1f) { suppressed++; return; }
            lastLog = Time.unscaledTime;
            Plugin.Log.LogInfo($"Snap #{Count} [{kind}] {name} err={err:0.00}m" + (suppressed > 0 ? $" (+{suppressed} more in the last second)" : ""));
            suppressed = 0;
        }
    }

    /// <summary>
    /// Client-side local simulation of the object the local player holds (same idea as CartSim).
    ///
    /// Vanilla runs the grab spring + camera-relative rotation target (PhysGrabObject.PhysicsGrabbingManipulation) only on the
    /// host. Here the client runs that very method on its own copy of the body, for the local grabber only (the client's
    /// playerGrabbing list is filled by a host RPC one round trip late, so it is swapped for a one-element list meanwhile),
    /// and a soft spring keeps the copy near the host's extrapolated pose. Nothing is sent: the host stays authoritative.
    ///
    /// Doors: the client destroys the HingeJoint (PhysGrabHinge.Awake), so a door has no hinge constraint there. Predicting it
    /// (rigid delta or local sim) would drag it to an impossible pose, so live hinges are left to vanilla PTV.
    /// </summary>
    internal static class HeldSim
    {
        public enum Kind { None, Sim, Vanilla, Legacy }

        public static PhysGrabObject Obj;
        public static Kind Current;
        public static float LastPosError;
        public static float LastRotError;

        private static int evalFrame = -1;
        private static PhysGrabObject latched;
        private static int latchedRevision = -1;
        private static Vector3 corrLin;
        private static Vector3 corrAng;
        private const float MaxHostAge = 0.25f;

        private static readonly List<PhysGrabber> tmpList = new List<PhysGrabber>(1);

        public static bool IsLiveHinge(PhysGrabObject pgo)
        {
            return pgo && pgo.hasHinge && pgo.hinge && !pgo.hinge.dead && !pgo.hinge.broken;
        }

        public static bool IsSimulating(PhysGrabObject pgo)
        {
            Eval();
            return Obj && Obj == pgo;
        }

        public static bool Eval()
        {
            if (evalFrame == Time.frameCount) return Obj;
            evalFrame = Time.frameCount;
            Obj = null;
            Current = Kind.None;

            if (!Gate.On || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) { Stop("not a client"); return false; }

            var g = PhysGrabber.instance;
            var a = PlayerAvatar.instance;
            var pgo = (g && g.grabbed) ? g.grabbedPhysGrabObject : null;
            if (!pgo || !a) { Stop("released"); return false; }
            if (pgo.isPlayer || pgo.isEnemy || pgo.isCart || pgo.physGrabCart || pgo.dead || !pgo.rb) { Stop("not eligible"); return false; }
            var ptv = pgo.photonTransformView;
            if (!ptv || !ptv.receivedAny) { Stop("no host data"); return false; }

            string mode;
            Kind kind;
            if (IsLiveHinge(pgo)) { kind = Kind.Vanilla; mode = "Vanilla (hinge: the joint only exists on the host, no prediction)"; }
            else if (!Cfg.PredictHeld.Value) { kind = Kind.None; mode = null; }
            else if (!Cfg.LocalHeldSim.Value) { kind = Kind.Legacy; mode = "Legacy rigid prediction (LocalHeldSim off)"; }
            else if (pgo.isMelee || pgo.isGun) { kind = Kind.Legacy; mode = "Legacy rigid prediction (melee/gun)"; }
            else if (ptv.receivedRigidbodyKinematic) { kind = Kind.None; mode = null; }
            else if (a.isTumbling) { kind = Kind.None; mode = null; }
            else
            {
                kind = Kind.Sim;
                mode = "HeldSim";
                foreach (var p in pgo.playerGrabbing)
                    if (p && p != g) { kind = Kind.Legacy; mode = "Legacy rigid prediction (shared grab)"; break; }
            }

            if (latched != pgo || latchedRevision != g.grabRevision)
            {
                latched = pgo;
                latchedRevision = g.grabRevision;
                corrLin = corrAng = Vector3.zero;
                Plugin.Log.LogInfo($"Grab {pgo.name}: {mode ?? "no prediction"}");
            }

            Current = kind;
            if (kind != Kind.Sim) return false;
            Obj = pgo;
            return true;
        }

        private static void Stop(string why)
        {
            Obj = null;
            if (!latched) return;
            latched = null;
            latchedRevision = -1;
            corrLin = corrAng = Vector3.zero;
        }

        public static void OnReceive(PhotonTransformView v)
        {
            if (!Obj || Obj.photonTransformView != v) return;
            var rb = Obj.rb;
            if (!rb || v.receivedRigidbodyKinematic) return;
            if (rb.isKinematic) rb.isKinematic = false;
            if (rb.IsSleeping()) rb.WakeUp();
        }

        /// <summary>One physics step: vanilla grab physics with local input, then the spring toward the host.</summary>
        public static void Step(PhysGrabObject pgo)
        {
            if (!pgo || pgo.frozenTimer > 0f || pgo.frozen) return;
            Eval();
            if (Obj != pgo) return;

            var g = PhysGrabber.instance;
            var rb = pgo.rb;
            var ptv = pgo.photonTransformView;

            // The client keeps parked bodies asleep/kinematic until the host's wake-up arrives; we move it now.
            if (rb.isKinematic) rb.isKinematic = false;
            if (rb.IsSleeping()) rb.WakeUp();
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // take our previous correction out so the grab physics sees only its own state
            rb.linearVelocity -= corrLin;
            rb.angularVelocity -= corrAng;

            // PhysicsGrabbingManipulation bails out on clients (isMaster == false) and walks playerGrabbing, which the host fills late.
            var orig = pgo.playerGrabbing;
            tmpList.Clear();
            tmpList.Add(g);
            pgo.playerGrabbing = tmpList;
            pgo.isMaster = true;
            try { pgo.PhysicsGrabbingManipulation(); }
            finally
            {
                pgo.isMaster = false;
                pgo.playerGrabbing = orig;
                tmpList.Clear();
            }

            Reconcile(pgo, rb, ptv);
        }

        private static void Reconcile(PhysGrabObject pgo, Rigidbody rb, PhotonTransformView ptv)
        {
            float age = Mathf.Clamp(ptv.receivedAge + (Time.time - ptv.receivedLocalTime), 0f, MaxHostAge);
            Vector3 hostPos = ptv.m_NetworkPosition + ptv.receivedVelocity * age;
            Quaternion hostRot = ptv.m_NetworkRotation;
            float ang = Mathf.Min(ptv.receivedAngularVelocity.magnitude * Mathf.Rad2Deg * age, 25f);
            if (ang > 0.01f) hostRot = Quaternion.AngleAxis(ang, ptv.receivedAngularVelocity.normalized) * hostRot;

            Vector3 e = hostPos - rb.position;
            float dist = e.magnitude;
            LastPosError = dist;

            if (dist > 3f)
            {
                SnapStats.Report("HeldSim", pgo.name, dist);
                rb.position = hostPos;
                rb.rotation = hostRot;
                rb.linearVelocity = ptv.receivedVelocity;
                rb.angularVelocity = ptv.receivedAngularVelocity;
                corrLin = corrAng = Vector3.zero;
                LastRotError = 0f;
                return;
            }

            float baseGain = Cfg.HeldSimCorrection.Value;
            float gain = Mathf.Lerp(baseGain, 12f, Mathf.InverseLerp(1f, 1.5f, dist));

            Vector3 lin = Vector3.zero;
            if (dist > 0.05f) lin = e * ((dist - 0.05f) / dist) * gain;
            lin = Vector3.ClampMagnitude(lin, 8f);

            Vector3 angCorr = Vector3.zero;
            (hostRot * Quaternion.Inverse(rb.rotation)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            LastRotError = Mathf.Abs(angle);
            if (Mathf.Abs(angle) > 2f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                angCorr = axis.normalized * (Mathf.Sign(angle) * (Mathf.Abs(angle) - 2f) * Mathf.Deg2Rad * gain);
            angCorr = Vector3.ClampMagnitude(angCorr, 6f);

            corrLin = lin;
            corrAng = angCorr;
            rb.linearVelocity += lin;
            rb.angularVelocity += angCorr;
        }
    }

    [HarmonyPatch(typeof(PhysGrabObject), "FixedUpdate")]
    internal static class HeldSimFixedUpdatePatch
    {
        private static bool errLogged;

        private static void Postfix(PhysGrabObject __instance)
        {
            if (PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom) return;
            try { HeldSim.Step(__instance); }
            catch (Exception e)
            {
                if (errLogged) return;
                errLogged = true;
                Plugin.Log.LogError("HeldSim.Step failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(PhotonTransformView), nameof(PhotonTransformView.OnPhotonSerializeView))]
    internal static class HeldSimReceivePatch
    {
        private static void Postfix(PhotonTransformView __instance, PhotonStream stream)
        {
            if (stream.IsWriting) return;
            HeldSim.OnReceive(__instance);
        }
    }
}
