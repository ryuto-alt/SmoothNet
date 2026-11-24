using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SmoothNet
{
    /// <summary>
    /// A rigid transform "how much has my input moved since the moment the host's
    /// state was produced". Applied to the host's (old) pose to get a predicted pose.
    /// </summary>
    internal struct Delta
    {
        public Vector3 Pivot;
        public Vector3 Translation;
        public Quaternion Rot;

        public Vector3 Apply(Vector3 p) => Pivot + Translation + Rot * (p - Pivot);
        public Quaternion Apply(Quaternion q) => Rot * q;
    }

    /// <summary>
    /// Client-side prediction of what the local player is manipulating.
    ///
    /// The host is authoritative for all physics objects. When a client pulls an object,
    /// the host's response arrives ~one full round trip later. Instead of showing that old
    /// state, we take the host's latest pose and apply the motion the local player's input
    /// made since then (puller delta for held objects, body pose delta for carts).
    /// Every new host packet re-anchors the prediction, so errors never accumulate,
    /// and nothing we do is ever sent back - the host stays the single source of truth.
    /// </summary>
    internal static class Predictor
    {
        public enum Mode { None, Held, Cart, CartSim }

        private static int frame = -1;
        private static bool valid;
        private static Delta delta;
        private static PhysGrabObject root;
        private static readonly HashSet<PhysGrabObject> items = new HashSet<PhysGrabObject>();

        private static PhysGrabObject lastRoot;
        private static float rootStart;

        // HUD stats
        public static Mode CurrentMode;
        public static float LastLag;
        public static float LastError;
        public static float LastFollow = 1f;

        public static void Compute()
        {
            if (frame == Time.frameCount) return;
            frame = Time.frameCount;
            valid = false;
            root = null;
            items.Clear();
            CurrentMode = Mode.None;

            var g = PhysGrabber.instance;
            var pgo = (g && g.grabbed) ? g.grabbedPhysGrabObject : null;
            if (pgo != lastRoot) { lastRoot = pgo; rootStart = Time.time; }

            if (!Cfg.Enabled.Value || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient || !History.Has) return;
            if (!pgo || pgo.isPlayer || pgo.isEnemy || pgo.dead || !pgo.rb) return;
            var ptv = pgo.photonTransformView;
            if (!ptv || !ptv.receivedAny) return;

            // Local cart simulation: the cart itself is driven by CartSim; the items inside follow it rigidly.
            if (pgo.isCart && pgo.physGrabCart && CartSim.IsSimulating(pgo) && CartSim.ItemDelta(pgo.physGrabCart, out delta))
            {
                CurrentMode = Mode.CartSim;
                LastLag = 0f;
                LastFollow = 1f;
                root = pgo;
                foreach (var it in pgo.physGrabCart.itemsInCart)
                    if (it && it != pgo && it.playerGrabbing.Count == 0 && !it.isPlayer && !it.isEnemy) items.Add(it);
                valid = true;
                return;
            }

            float lag = LagFor(ptv);
            // Never look back before the grab started: before that, the host wasn't following our input at all.
            lag = Mathf.Min(lag, Time.time - rootStart);
            LastLag = lag;
            if (lag <= 0.001f) return;

            float s = Cfg.PredictionStrength.Value;
            if (!History.At(Time.time - lag, out var pullerPast, out var bodyPast, out var yawPast)) return;

            var cart = pgo.isCart ? pgo.physGrabCart : null;
            var area = cart ? cart.physGrabObjectGrabArea : null;
            bool handled = cart && area && cart.currentState == PhysGrabCart.State.Handled
                           && area.listOfAllGrabbers.Count > 0 && area.listOfAllGrabbers[0] == g;

            if (handled)
            {
                if (!Cfg.PredictCart.Value) return;
                var a = PlayerAvatar.instance;
                if (!a) return;
                Vector3 bodyNow = a.transform.position;
                float yawNow = g.transform.eulerAngles.y;
                Vector3 move = Vector3.ClampMagnitude(bodyNow - bodyPast, 3f) * s;
                float dyaw = Mathf.Clamp(Mathf.DeltaAngle(yawPast, yawNow), -120f, 120f) * s;
                delta = new Delta { Pivot = bodyPast, Translation = move, Rot = Quaternion.Euler(0f, dyaw, 0f) };
                CurrentMode = Mode.Cart;
                LastFollow = 1f;
            }
            else
            {
                if (!Cfg.PredictHeld.Value) return;
                if (pgo.playerGrabbing.Count > 1) return; // shared grabs: host blends several pullers
                Vector3 pullerNow = g.physGrabPointPullerPosition;

                // Adaptive: if on the host the object isn't keeping up with where we pulled
                // (too heavy / stuck / blocked), predicting the full motion would rubber-band.
                float follow = 1f;
                var rb = pgo.rb;
                Vector3 localGrab = Quaternion.Inverse(rb.rotation) * (g.physGrabPoint.position - rb.position);
                Vector3 hostGrab = ptv.m_NetworkPosition + ptv.m_NetworkRotation * localGrab;
                float gap = Vector3.Distance(hostGrab, pullerPast);
                follow = 1f - Mathf.Clamp01((gap - 0.5f) / 1.0f);
                LastFollow = follow;

                Vector3 move = Vector3.ClampMagnitude(pullerNow - pullerPast, 2f) * (s * follow);
                float dyaw = Mathf.Clamp(Mathf.DeltaAngle(yawPast, g.transform.eulerAngles.y), -90f, 90f) * (s * follow);
                // Pivot on the grab point: the puller delta already moves the grab point, turning happens around it.
                delta = new Delta { Pivot = hostGrab, Translation = move, Rot = Quaternion.Euler(0f, dyaw, 0f) };
                CurrentMode = Mode.Held;
            }

            root = pgo;
            if (cart)
                foreach (var it in cart.itemsInCart)
                    if (it && it != pgo && it.playerGrabbing.Count == 0 && !it.isPlayer && !it.isEnemy) items.Add(it);
            valid = true;
        }

        public static float LagFor(PhotonTransformView ptv)
        {
            float age = ptv.receivedAge + (Time.time - ptv.receivedLocalTime);
            float sendWait = 0.5f / Mathf.Max(1, PhotonNetwork.SerializationRate);
            float lag = age + NetSync.UplinkToHost + sendWait + Cfg.ExtraLatency.Value;
            return Mathf.Clamp(lag, 0f, Cfg.MaxPredictionTime.Value);
        }

        public static bool TryGet(PhysGrabObject pgo, out Delta d)
        {
            d = delta;
            if (!valid) return false;
            return pgo == root || items.Contains(pgo);
        }

        public static bool IsRoot(PhysGrabObject pgo) => valid && pgo == root;
    }

    [HarmonyPatch(typeof(PhotonTransformView), "Update")]
    internal static class TransformViewUpdatePatch
    {
        private class State
        {
            public float W;
            public Vector3 OffPos;
            public Quaternion OffRot = Quaternion.identity;
            public Vector3 PrevTarget;
            public Quaternion PrevTargetRot = Quaternion.identity;
            public Vector3 Vel;
            public Vector3 AngVel;
            public bool HasPrev;
            public bool WasSim;
        }

        /// <summary>Max distance a predicted pose may sit from the host pose. Keeps us well under
        /// vanilla's 5 m teleport threshold and the 3 m hard-snap, so prediction never fights them.</summary>
        private const float MaxOffset = 2.5f;

        private class PgoRef { public PhysGrabObject Pgo; public bool Looked; }

        private static readonly ConditionalWeakTable<PhotonTransformView, State> states = new ConditionalWeakTable<PhotonTransformView, State>();
        private static readonly ConditionalWeakTable<PhotonTransformView, PgoRef> pgos = new ConditionalWeakTable<PhotonTransformView, PgoRef>();

        private static PhysGrabObject GetPgo(PhotonTransformView v)
        {
            var r = pgos.GetOrCreateValue(v);
            if (!r.Looked) { r.Pgo = v.GetComponent<PhysGrabObject>(); r.Looked = true; }
            return r.Pgo;
        }

        private static bool Prefix(PhotonTransformView __instance)
        {
            var v = __instance;
            if (PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom || !Cfg.Enabled.Value) return true;

            var rb = v.rb;
            if (!rb || v.teleport || !v.receivedAny)
            {
                ResetState(v);
                return true;
            }

            var pgo = GetPgo(v);
            if (!pgo || pgo.isPlayer || pgo.isEnemy) { ResetState(v); return true; }

            Predictor.Compute();
            bool active = Predictor.TryGet(pgo, out var d);

            if (CartSim.IsSimulating(pgo))
            {
                // CartSim drives this body in FixedUpdate; vanilla follow must stay out of the way.
                var ss = states.GetOrCreateValue(v);
                ss.WasSim = true;
                ss.W = 0f;
                ss.HasPrev = false;
                v.smoothedPosition = rb.position;
                v.smoothedRotation = rb.rotation;
                if (v.throttleDisableTimer > 0f) v.throttleDisableTimer -= Time.deltaTime;
                if (v.overrideExtrapolateTimer > 0f) v.overrideExtrapolateTimer -= Time.deltaTime;
                if (v.kinematicClientForcedTimer > 0f) v.kinematicClientForcedTimer = Mathf.Max(0f, v.kinematicClientForcedTimer - Time.deltaTime);
                return false;
            }
            if (states.TryGetValue(v, out var simSt) && simSt.WasSim)
            {
                // Sim ended: blend from where the local cart is to the host's extrapolated pose, no pop.
                simSt.WasSim = false;
                Extrapolated(v, Cfg.ExtrapolateOthers.Value ? Cfg.ExtrapolateMaxTime.Value : 0f, out var hPos, out var hRot);
                simSt.OffPos = rb.position - hPos;
                simSt.OffRot = rb.rotation * Quaternion.Inverse(hRot);
                simSt.W = 1f;
                simSt.HasPrev = false;
            }

            if (v.isSleeping || rb.isKinematic)
            {
                // Asleep on the host (e.g. a parked cart and its loot right as we grab it): the client keeps it
                // kinematic until the host's wake-up packet arrives one round trip later. Move it kinematically
                // along the prediction meanwhile, so the cart and contents respond instantly and stay together.
                if (active && v.isSleeping)
                {
                    if (v.throttleDisableTimer > 0f) v.throttleDisableTimer -= Time.deltaTime;
                    if (!rb.isKinematic) rb.isKinematic = true;
                    Vector3 kp = v.m_NetworkPosition + Vector3.ClampMagnitude(d.Apply(v.m_NetworkPosition) - v.m_NetworkPosition, MaxOffset);
                    Quaternion kr = d.Apply(v.m_NetworkRotation);
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.MovePosition(kp);
                    rb.MoveRotation(kr);
                    v.smoothedPosition = kp;
                    v.smoothedRotation = kr;
                    v.teleport = false;
                    ResetState(v);
                    if (v.overrideExtrapolateTimer > 0f) v.overrideExtrapolateTimer -= Time.deltaTime;
                    if (v.kinematicClientForcedTimer > 0f) v.kinematicClientForcedTimer = Mathf.Max(0f, v.kinematicClientForcedTimer - Time.deltaTime);
                    return false;
                }
                ResetState(v);
                return true;
            }

            states.TryGetValue(v, out var st);
            if (!active && (st == null || st.W <= 0f))
            {
                if (st != null) st.HasPrev = false;
                if (Cfg.ExtrapolateOthers.Value && ShouldExtrapolate(pgo, v))
                    v.OverrideExtrapolate(Cfg.ExtrapolateMaxTime.Value, 0.15f);
                return true; // vanilla path (with or without built-in extrapolation)
            }
            if (st == null) st = states.GetOrCreateValue(v);

            float dt = Time.deltaTime;
            if (v.throttleDisableTimer > 0f) v.throttleDisableTimer -= dt;

            Extrapolated(v, Cfg.ExtrapolateOthers.Value ? Cfg.ExtrapolateMaxTime.Value : 0f, out var exPos, out var exRot);

            Vector3 tPos;
            Quaternion tRot;
            if (active)
            {
                tPos = v.m_NetworkPosition + Vector3.ClampMagnitude(d.Apply(v.m_NetworkPosition) - v.m_NetworkPosition, MaxOffset);
                tRot = d.Apply(v.m_NetworkRotation);
                st.OffPos = tPos - exPos;
                st.OffRot = tRot * Quaternion.Inverse(exRot);
                st.W = 1f;
            }
            else
            {
                // released: fade the remaining prediction offset out while the host catches up
                st.W = Mathf.Max(0f, st.W - dt / Mathf.Max(0.01f, Cfg.ReleaseBlendTime.Value));
                tPos = exPos + st.OffPos * st.W;
                tRot = Quaternion.Slerp(Quaternion.identity, st.OffRot, st.W) * exRot;
            }

            Drive(v, rb, st, tPos, tRot, dt);
            if (Predictor.IsRoot(pgo)) Predictor.LastError = Vector3.Distance(rb.position, tPos);

            if (v.overrideExtrapolateTimer > 0f) v.overrideExtrapolateTimer -= dt;
            if (v.kinematicClientForcedTimer > 0f) v.kinematicClientForcedTimer = Mathf.Max(0f, v.kinematicClientForcedTimer - dt);
            return false;
        }

        private static void ResetState(PhotonTransformView v)
        {
            if (states.TryGetValue(v, out var s)) { s.W = 0f; s.HasPrev = false; }
        }

        private static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt)
        {
            (to * Quaternion.Inverse(from)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            if (Mathf.Abs(angle) < 1e-3f || float.IsNaN(axis.x) || float.IsInfinity(axis.x)) return Vector3.zero;
            return axis * (angle * Mathf.Deg2Rad / dt);
        }

        private static bool ShouldExtrapolate(PhysGrabObject pgo, PhotonTransformView v)
        {
            if (pgo.grabbed || pgo.playerGrabbing.Count > 0) return true;
            return v.receivedVelocity.sqrMagnitude > 0.3f * 0.3f;
        }

        private static void Extrapolated(PhotonTransformView v, float maxTime, out Vector3 pos, out Quaternion rot)
        {
            pos = v.m_NetworkPosition;
            rot = v.m_NetworkRotation;
            if (maxTime <= 0f) return;
            float t = Mathf.Clamp(v.receivedAge + Time.time - v.receivedLocalTime, 0f, maxTime);
            pos += v.receivedVelocity * t;
            float ang = Mathf.Min(v.receivedAngularVelocity.magnitude * Mathf.Rad2Deg * t, 25f);
            if (ang > 0.01f) rot = Quaternion.AngleAxis(ang, v.receivedAngularVelocity.normalized) * rot;
        }

        private static void Drive(PhotonTransformView v, Rigidbody rb, State st, Vector3 tPos, Quaternion tRot, float dt)
        {
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            Vector3 err = tPos - rb.position;
            if (err.sqrMagnitude > 9f)
            {
                rb.position = tPos;
                rb.rotation = tRot;
                rb.linearVelocity = v.receivedVelocity;
                rb.angularVelocity = v.receivedAngularVelocity;
                st.HasPrev = false;
                v.smoothedPosition = tPos;
                v.smoothedRotation = tRot;
                return;
            }

            // Feed-forward = how fast the *target* itself moves/turns (smoothed), feedback = error * k.
            // Using the target's own derivative (not the host's received velocity) avoids double counting
            // motion the prediction already contains.
            bool canDiff = st.HasPrev && dt > 1e-4f;
            Vector3 targetVel = canDiff ? (tPos - st.PrevTarget) / dt : v.receivedVelocity;
            targetVel = Vector3.ClampMagnitude(targetVel, 25f);
            Vector3 targetAng = canDiff ? AngularVelocity(st.PrevTargetRot, tRot, dt) : v.receivedAngularVelocity;
            targetAng = Vector3.ClampMagnitude(targetAng, 20f);
            float blend = 1f - Mathf.Exp(-dt * 15f);
            st.Vel = st.HasPrev ? Vector3.Lerp(st.Vel, targetVel, blend) : targetVel;
            st.AngVel = st.HasPrev ? Vector3.Lerp(st.AngVel, targetAng, blend) : targetAng;
            st.PrevTarget = tPos;
            st.PrevTargetRot = tRot;
            st.HasPrev = true;

            float k = v.velocityFollowStrength > 0f ? v.velocityFollowStrength : 10f;
            rb.linearVelocity = st.Vel + err * k;

            (tRot * Quaternion.Inverse(rb.rotation)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            Vector3 corr = Vector3.zero;
            if (Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x))
                corr = axis * (angle * Mathf.Deg2Rad * k);
            rb.angularVelocity = Vector3.ClampMagnitude(st.AngVel + corr, 30f);

            v.smoothedPosition = rb.position;
            v.smoothedRotation = rb.rotation;
        }
    }
}
