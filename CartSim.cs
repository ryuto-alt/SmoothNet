using System;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SmoothNet
{
    /// <summary>
    /// Client-side local simulation of the cart the local player pushes.
    ///
    /// The host steers the cart with a P-controller (PhysGrabCart.CartSteer) fed by our avatar, which it only
    /// learns one uplink later, and we only see its result one downlink after that. Instead of showing a delayed
    /// (or rigidly shifted) host cart, the client runs the very same steering math on its own copy of the cart
    /// with fresh local input, so the cart behaves like a locally pushed cart from the first frame.
    /// A soft spring toward the host's (extrapolated) cart keeps the copy honest. Nothing is sent to the host:
    /// the host stays the single source of truth and every host packet re-anchors the spring.
    /// </summary>
    internal static class CartSim
    {
        // HUD
        public static float LastPosError;
        public static float LastYawError;

        /// <summary>Cart simulated this frame (null = not simulating).</summary>
        public static PhysGrabCart Cart;
        private static int evalFrame = -1;

        // Grab-level latch: we start on our own grab, the host's grabber list reaches us a round trip later.
        private static PhysGrabCart latched;
        private static float latchTime;
        private static bool confirmed;
        private static bool steering;

        // The reconciliation velocity we added last step, removed again before the next steering step so it never accumulates.
        private static Vector3 corrLin;
        private static float corrYaw;

        private static BoxCollider inBox;
        private static PhysGrabCart inBoxOwner;

        private const float ConfirmTimeout = 1.5f;
        private const float MaxHostAge = 0.25f;

        public static bool IsSimulating(PhysGrabObject pgo)
        {
            Eval();
            return Cart && Cart.physGrabObject == pgo;
        }

        public static bool Eval()
        {
            if (evalFrame == Time.frameCount) return Cart;
            evalFrame = Time.frameCount;
            Cart = null;

            if (!Gate.On || !Cfg.LocalCartSim.Value || !Cfg.PredictCart.Value
                || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) { Stop("disabled/not client"); return false; }

            var g = PhysGrabber.instance;
            var a = PlayerAvatar.instance;
            var pgo = (g && g.grabbed) ? g.grabbedPhysGrabObject : null;
            var cart = (pgo && pgo.isCart) ? pgo.physGrabCart : null;
            var area = cart ? cart.physGrabObjectGrabArea : null;
            var ptv = pgo ? pgo.photonTransformView : null;
            if (!cart || !area || !a || !pgo.rb || !ptv || !ptv.receivedAny || ptv.receivedRigidbodyKinematic) { Stop("released"); return false; }
            if (a.isTumbling || (cart.isSmallCart && cart.itemEquippable && cart.itemEquippable.isUnequipping)) { Stop("tumble/unequip"); return false; }

            var list = area.listOfAllGrabbers;
            bool inList = list.Contains(g);
            if (inList)
            {
                if (list[0] != g) { Stop("another player steers"); return false; }
                if (latched != cart) Start(cart, true);
                confirmed = true;
            }
            else if (list.Count > 0) { Stop("another player steers"); return false; }
            else if (latched == cart)
            {
                if (confirmed) { Stop("host dropped us"); return false; }
                if (Time.time - latchTime > ConfirmTimeout) { Stop("host never confirmed"); return false; }
            }
            else
            {
                // Same test the host uses to add a grabber: grab point inside a grab area during the initial press.
                if (g.initialPressTimer > 0f && InGrabArea(area, g.physGrabPoint.position)) Start(cart, false);
                else return false;
            }

            // Vanilla CartSteer only steers while the player stands outside the cart's loading box.
            steering = !InsideBox(cart, a);
            if (steering) Cart = cart;
            return steering;
        }

        private static bool InGrabArea(PhysGrabObjectGrabArea area, Vector3 p)
        {
            foreach (var ga in area.grabAreas)
                foreach (var col in ga.grabAreaColliders)
                    if (col && col.ClosestPoint(p) == p) return true;
            return false;
        }

        private static bool InsideBox(PhysGrabCart c, PlayerAvatar a)
        {
            if (inBoxOwner != c || !inBox) { inBoxOwner = c; inBox = c.inCart.GetComponent<BoxCollider>(); }
            if (!inBox) return true;
            Vector3 p = a.transform.position + Vector3.up * 0.25f;
            return Vector3.Distance(inBox.ClosestPoint(p), p) < 0.01f;
        }

        private static void Start(PhysGrabCart c, bool isConfirmed)
        {
            latched = c;
            latchTime = Time.time;
            confirmed = isConfirmed;
            corrLin = Vector3.zero;
            corrYaw = 0f;
            Plugin.Log.LogInfo($"CartSim start ({(isConfirmed ? "host confirmed" : "predicted grab")})");
        }

        private static void Stop(string why)
        {
            steering = false;
            if (!latched) return;
            latched = null;
            confirmed = false;
            Plugin.Log.LogInfo($"CartSim stop ({why})");
        }

        /// <summary>Rigid transform from the host's cart pose to our simulated one, for the items inside.</summary>
        public static bool ItemDelta(PhysGrabCart c, out Delta d)
        {
            d = default;
            var ptv = c.physGrabObject.photonTransformView;
            var rb = c.rb;
            if (!ptv || !rb) return false;
            Vector3 hostPos = ptv.m_NetworkPosition;
            d = new Delta
            {
                Pivot = hostPos,
                Translation = rb.position - hostPos,
                Rot = rb.rotation * Quaternion.Inverse(ptv.m_NetworkRotation)
            };
            return true;
        }

        /// <summary>A host packet may flag the cart asleep/kinematic or wake state; the sim body must stay awake.</summary>
        public static void OnReceive(PhotonTransformView v)
        {
            if (!Cart || Cart.physGrabObject.photonTransformView != v) return;
            var rb = Cart.rb;
            if (!rb || v.receivedRigidbodyKinematic) return;
            if (rb.isKinematic) rb.isKinematic = false;
            if (rb.IsSleeping()) rb.WakeUp();
        }

        /// <summary>One physics step: vanilla steering with local input, then the spring toward the host.</summary>
        public static void Step(PhysGrabCart c)
        {
            if (!c.physGrabObject) return;
            Eval();
            if (Cart != c) return;

            var g = PhysGrabber.instance;
            var a = PlayerAvatar.instance;
            var rb = c.rb;
            var ptv = c.physGrabObject.photonTransformView;

            // The client keeps parked bodies asleep/kinematic until the host's wake-up arrives; we move it now.
            if (rb.isKinematic) rb.isKinematic = false;
            if (rb.IsSleeping()) rb.WakeUp();
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // take our previous correction out so steering sees only its own state
            rb.linearVelocity -= corrLin;
            Vector3 av = rb.angularVelocity;
            rb.angularVelocity = new Vector3(av.x, av.y - corrYaw, av.z);

            Steer(c, rb, g, a);

            Reconcile(c, rb, ptv);
        }

        // Verbatim port of PhysGrabCart.CartSteer (steering part), reading local, fresh inputs.
        private static void Steer(PhysGrabCart c, Rigidbody rb, PhysGrabber g, PlayerAvatar a)
        {
            Transform ct = c.transform;
            float num = 2f;
            float num2 = 2.5f;
            if (c.isSmallCart) { num = 1.5f; num2 = 2f; }
            Vector3 lhs = PlayerController.instance.rb.linearVelocity;
            bool flag = Vector3.Dot(lhs, ct.forward) > 0f;
            float num3 = 5f;
            if (a.isSprinting) num3 = 7f;
            if (a.isSprinting && flag) { num = 3f; num2 = 4f; }
            float t = Mathf.Clamp(Vector3.Dot(rb.linearVelocity, g.transform.forward) / num3, 0f, 1f);
            float num4 = Mathf.Lerp(num, num2, t);
            Vector3 back = g.transform.rotation * Vector3.back;
            Vector3 target = a.transform.position - back * num4;
            float num5 = Mathf.Clamp(Vector3.Distance(ct.position, target), 0f, 1f);
            Vector3 vel = (target - ct.position).normalized * 5f * num5;
            vel = Vector3.ClampMagnitude(vel, 5f);
            float y = rb.linearVelocity.y;
            Vector3 nv = Vector3.MoveTowards(rb.linearVelocity, vel, num5 * 2f);
            nv = new Vector3(nv.x, y, nv.z);
            rb.linearVelocity = Vector3.ClampMagnitude(nv, 5f);

            Vector3 look = g.transform.position - ct.position;
            if (look.sqrMagnitude < 1e-6f) return;
            Quaternion want = Quaternion.Euler(0f, Quaternion.LookRotation(look, Vector3.up).eulerAngles.y + 180f, 0f);
            Quaternion cur = Quaternion.Euler(0f, rb.rotation.eulerAngles.y, 0f);
            (want * Quaternion.Inverse(cur)).ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            float value = Mathf.Clamp(Mathf.Abs(angle) / 180f, 0.2f, 1f) * 20f;
            value = Mathf.Clamp(value, 0f, 4f);
            Vector3 target4 = Mathf.PI / 180f * angle * axis.normalized * value;
            target4 = Vector3.ClampMagnitude(target4, 4f);
            Vector3 w = Vector3.MoveTowards(rb.angularVelocity, target4, value);
            rb.angularVelocity = Vector3.ClampMagnitude(w, 4f);
        }

        private static void Reconcile(PhysGrabCart c, Rigidbody rb, PhotonTransformView ptv)
        {
            float age = Mathf.Clamp(ptv.receivedAge + (Time.time - ptv.receivedLocalTime), 0f, MaxHostAge);
            Vector3 hostPos = ptv.m_NetworkPosition + ptv.receivedVelocity * age;
            float hostYaw = ptv.m_NetworkRotation.eulerAngles.y + ptv.receivedAngularVelocity.y * Mathf.Rad2Deg * age;

            Vector3 e = hostPos - rb.position;
            float dist = e.magnitude;
            LastPosError = dist;

            // Hard failure only: the host cart is far away (teleport, fell, sent elsewhere).
            if (dist > 3f)
            {
                SnapStats.Report("CartSim", c.name, dist);
                rb.position = hostPos;
                rb.rotation = Quaternion.Euler(0f, hostYaw, 0f);
                rb.linearVelocity = ptv.receivedVelocity;
                rb.angularVelocity = new Vector3(0f, ptv.receivedAngularVelocity.y, 0f);
                corrLin = Vector3.zero;
                corrYaw = 0f;
                LastYawError = 0f;
                return;
            }

            // Soft spring; stiffens when the host cart is blocked/stuck so we don't drive through walls it can't.
            float baseGain = Cfg.CartSimCorrection.Value;
            float gain = Mathf.Lerp(baseGain, 10f, Mathf.InverseLerp(1f, 1.5f, dist));

            Vector3 lin = Vector3.zero;
            if (dist > 0.05f) lin = e * ((dist - 0.05f) / dist) * gain;
            lin = Vector3.ClampMagnitude(lin, 8f);

            float eYaw = Mathf.DeltaAngle(rb.rotation.eulerAngles.y, hostYaw);
            LastYawError = eYaw;
            float yaw = 0f;
            if (Mathf.Abs(eYaw) > 2f) yaw = Mathf.Sign(eYaw) * (Mathf.Abs(eYaw) - 2f) * Mathf.Deg2Rad * gain;
            yaw = Mathf.Clamp(yaw, -6f, 6f);

            corrLin = lin;
            corrYaw = yaw;
            rb.linearVelocity += lin;
            Vector3 av = rb.angularVelocity;
            rb.angularVelocity = new Vector3(0f, av.y + yaw, 0f);

            // Client FixedUpdate returns before vanilla's upright correction, so keep the cart level here.
            Vector3 eul = rb.rotation.eulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(0f, eul.x)) > 0.05f || Mathf.Abs(Mathf.DeltaAngle(0f, eul.z)) > 0.05f)
                rb.MoveRotation(Quaternion.Euler(0f, eul.y, 0f));
        }
    }

    [HarmonyPatch(typeof(PhysGrabCart), "FixedUpdate")]
    internal static class CartSimFixedUpdatePatch
    {
        private static bool errLogged;

        // Postfix still runs when CartSleepFixPatch's prefix skips the original.
        private static void Postfix(PhysGrabCart __instance)
        {
            if (PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom) return;
            try { CartSim.Step(__instance); }
            catch (Exception e)
            {
                if (errLogged) return;
                errLogged = true;
                Plugin.Log.LogError("CartSim.Step failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(PhotonTransformView), nameof(PhotonTransformView.OnPhotonSerializeView))]
    internal static class CartSimReceivePatch
    {
        // Every host packet resets rb.isKinematic and may put the body to sleep; undo that for the cart we simulate.
        private static void Postfix(PhotonTransformView __instance, PhotonStream stream)
        {
            if (stream.IsWriting) return;
            CartSim.OnReceive(__instance);
        }
    }
}
