using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SmoothNet
{
    /// <summary>
    /// Host: a remote player's cart steering target is their avatar position, which on the host is
    /// (client->host one-way + send interval) old. For players WITHOUT working cart prediction we lead
    /// that position by their velocity. Players with 1.0.0-style SmoothNet prediction (rigid shift of the host pose)
    /// are left alone, compensating both would double-count. Players running the 1.1.0 local cart simulation
    /// ("s" flag) simulate the present, so the host leads for them just like for players without the mod.
    ///
    /// Implemented as a transpiler that only changes the *values read* for steering
    /// (target point + look-at), never moves any transform, and leaves the first read
    /// (the "is the player standing inside the cart" test) untouched.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabCart), "CartSteer")]
    internal static class HostCartCompensationPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var fAvatar = AccessTools.Field(typeof(PhysGrabber), nameof(PhysGrabber.playerAvatar));
            var mTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
            var mPosition = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
            var mLead = AccessTools.Method(typeof(HostCartCompensationPatch), nameof(Lead));

            int avatarReads = 0, replaced = 0;
            int grabberLocal = -1;
            for (int i = 0; i + 3 < list.Count; i++)
            {
                // ldloc item2 ; ldfld playerAvatar ; callvirt get_transform ; callvirt get_position
                if (LocIdx(list[i]) >= 0 && list[i + 1].LoadsField(fAvatar) && list[i + 2].Calls(mTransform) && list[i + 3].Calls(mPosition))
                {
                    avatarReads++;
                    if (avatarReads == 1) { grabberLocal = LocIdx(list[i]); continue; } // in-cart test: keep vanilla
                    if (LocIdx(list[i]) != grabberLocal) continue;
                    list.InsertRange(i + 4, new[] { new CodeInstruction(list[i].opcode, list[i].operand), new CodeInstruction(OpCodes.Call, mLead) });
                    replaced++;
                    i += 5;
                    continue;
                }
                // ldloc item2 ; callvirt get_transform ; callvirt get_position   (look-at target)
                if (grabberLocal >= 0 && LocIdx(list[i]) == grabberLocal && list[i + 1].Calls(mTransform) && list[i + 2].Calls(mPosition))
                {
                    list.InsertRange(i + 3, new[] { new CodeInstruction(list[i].opcode, list[i].operand), new CodeInstruction(OpCodes.Call, mLead) });
                    replaced++;
                    i += 4;
                }
            }
            Plugin.Log.LogInfo($"HostCartCompensationPatch: {replaced} steering reads redirected (expected 2)");
            return list;
        }

        /// <summary>Local index loaded by an ldloc instruction, or -1. (Avoids HarmonyX helpers whose
        /// LocalBuilder signature doesn't resolve against netstandard.)</summary>
        private static int LocIdx(CodeInstruction ci)
        {
            var op = ci.opcode;
            if (op == OpCodes.Ldloc_0) return 0;
            if (op == OpCodes.Ldloc_1) return 1;
            if (op == OpCodes.Ldloc_2) return 2;
            if (op == OpCodes.Ldloc_3) return 3;
            if (op != OpCodes.Ldloc_S && op != OpCodes.Ldloc) return -1;
            object o = ci.operand;
            switch (o)
            {
                case null: return -1;
                case int i: return i;
                case short s: return s;
                case byte b: return b;
            }
            var p = o.GetType().GetProperty("LocalIndex");
            return p != null ? (int)p.GetValue(o, null) : -1;
        }

        private static int cacheFrame = -1;
        private static PhysGrabber cacheGrabber;
        private static Vector3 cacheShift;

        public static Vector3 Lead(Vector3 pos, PhysGrabber g)
        {
            return pos + Shift(g);
        }

        private static Vector3 Shift(PhysGrabber g)
        {
            if (cacheFrame == Time.frameCount && cacheGrabber == g) return cacheShift;
            cacheFrame = Time.frameCount;
            cacheGrabber = g;
            cacheShift = Vector3.zero;

            if (!Cfg.Enabled.Value || !Cfg.CompensateUnmoddedCart.Value) return cacheShift;
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient) return cacheShift;
            if (!g || g.isLocal || !g.playerAvatar || g.playerAvatar.isTumbling) return cacheShift;
            var pv = g.playerAvatar.photonView;
            if (!pv || (NetSync.PredictsCart(pv.Owner) && !NetSync.SimsCart(pv.Owner))) return cacheShift;
            if (!NetSync.TryGetAvatarAge(g.playerAvatar, out float age)) return cacheShift;

            float t = Mathf.Clamp(age + 0.5f / Mathf.Max(1, PhotonNetwork.SerializationRate), 0f, 0.2f);
            Vector3 vel = g.playerAvatar.rbVelocityRaw;
            vel.y = 0f;
            cacheShift = Vector3.ClampMagnitude(vel * t, 1f);
            return cacheShift;
        }
    }

    /// <summary>
    /// Vanilla bug: PhysGrabCart.FixedUpdate calls MoveRotation + sets angularVelocity on every
    /// physics step while awake, and its "tilted" check compares eulerAngles (0..360) with 0.05,
    /// so a -0.01 deg tilt reads as 359.99 and re-wakes a sleeping cart. Carts therefore never
    /// sleep, and neither does anything touching them (all the loot inside). Same logic, but only
    /// correcting when actually tilted, so idle carts and their contents can finally sleep.
    ///
    /// Sleeping bodies may stop receiving OnTriggerStay, which is how items keep their "in cart"
    /// protection alive. While the cart sleeps we refresh that state ourselves, exactly as the
    /// trigger would, so loot in a parked cart stays protected when someone grabs the cart.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabCart), "FixedUpdate")]
    internal static class CartSleepFixPatch
    {
        private static bool Prefix(PhysGrabCart __instance)
        {
            if (!Cfg.Enabled.Value || !Cfg.CartSleepFix.Value) return true;
            var c = __instance;

            if (c.physGrabObjectGrabArea && c.physGrabObjectGrabArea.listOfAllGrabbers.Count > 0) c.CartSteer();
            else c.cartBeingPulled = false;

            if (!LevelGenerator.Instance.Generated || (GameManager.instance.gameMode == 1 && !PhotonNetwork.IsMasterClient))
                return false;

            var rb = c.rb;
            if (!rb.isKinematic)
            {
                Vector3 e = c.transform.rotation.eulerAngles;
                bool tilted = Mathf.Abs(Mathf.DeltaAngle(0f, e.x)) > 0.05f || Mathf.Abs(Mathf.DeltaAngle(0f, e.z)) > 0.05f;
                if (tilted)
                {
                    rb.MoveRotation(Quaternion.Euler(0f, e.y, 0f));
                    rb.angularVelocity = new Vector3(0f, rb.angularVelocity.y, 0f);
                }
                else if (!rb.IsSleeping())
                {
                    Vector3 av = rb.angularVelocity;
                    if (Mathf.Abs(av.x) > 1e-3f || Mathf.Abs(av.z) > 1e-3f)
                        rb.angularVelocity = new Vector3(0f, av.y, 0f);
                }
            }

            if (rb.IsSleeping()) KeepContentsInCart(c);

            c.actualVelocity = (c.transform.position - c.actualVelocityLastPosition) / Time.fixedDeltaTime;
            c.actualVelocityLastPosition = c.transform.position;
            return false;
        }

        private static void KeepContentsInCart(PhysGrabCart c)
        {
            var items = c.itemsInCart;
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (!it) continue;
                var id = it.impactDetector;
                if (!id || id.currentCart != c) continue; // only items vanilla already considered inside this cart
                if (id.timerInCart < 0.1f) id.timerInCart = 0.1f;
                if (c.physGrabInCart) c.physGrabInCart.Add(it);
            }
        }
    }

    /// <summary>
    /// PhysGrabObjectImpactDetector.OnCollisionStay reads collision.contacts three times per call,
    /// and each read allocates a new array. With ~100 physics objects this is a steady stream of
    /// garbage -> GC spikes -> host hitches -> everyone lags. Replace with a pooled GetContacts.
    /// </summary>
    [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), "OnCollisionStay")]
    internal static class CollisionStayGcPatch
    {
        private static void Prefix() => ContactCache.Fresh = true;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getter = AccessTools.PropertyGetter(typeof(Collision), nameof(Collision.contacts));
            var repl = AccessTools.Method(typeof(ContactCache), nameof(ContactCache.Get));
            int n = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(getter))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = repl;
                    n++;
                }
                yield return ins;
            }
            Plugin.Log.LogInfo($"CollisionStayGcPatch: replaced {n} Collision.contacts reads");
        }
    }

    internal static class ContactCache
    {
        public static bool Fresh = true;
        private static readonly ContactPoint[][] pool = new ContactPoint[129][];
        private static ContactPoint[] last;

        public static ContactPoint[] Get(Collision c)
        {
            if (!Cfg.CollisionGcFix.Value) return c.contacts;
            if (Fresh || last == null)
            {
                int n = c.contactCount;
                ContactPoint[] arr;
                if (n < pool.Length) { arr = pool[n] ?? (pool[n] = new ContactPoint[n]); }
                else arr = new ContactPoint[n];
                if (n > 0) c.GetContacts(arr);
                last = arr;
                Fresh = false;
            }
            return last;
        }
    }
}
