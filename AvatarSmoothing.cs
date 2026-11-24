using System.Runtime.CompilerServices;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SmoothNet
{
    /// <summary>
    /// Remote players are snapped to their last received position and then lerped
    /// (~40 ms), so on screen they are always (one-way latency + 40 ms) in the past.
    /// We draw them ahead along their received velocity by the measured packet age.
    /// Visual transform only - their gameplay rigidbody/colliders are untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerAvatarVisuals), "Update")]
    internal static class AvatarSmoothingPatch
    {
        private class State { public Vector3 Off; }
        private static readonly ConditionalWeakTable<PlayerAvatarVisuals, State> states = new ConditionalWeakTable<PlayerAvatarVisuals, State>();

        private static void Postfix(PlayerAvatarVisuals __instance)
        {
            var v = __instance;
            // Never on the host: host-side enemy AI reads these visual transforms (eye/face targets).
            if (!Gate.On || !Cfg.SmoothRemotePlayers.Value || !PhotonNetwork.InRoom || PhotonNetwork.IsMasterClient) { Reset(v); return; }
            if (v.isMenuAvatar || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated) { Reset(v); return; }
            var a = v.playerAvatar;
            if (!a || a.isLocal || a.isDisabled || a.isTumbling || a.clientPhysRiding) { Reset(v); return; }
            if (v.mountSeatVisualTimer > 0f || v.mountOffsetOverrideTimer > 0f || v.mountOffsetEaseOutTimer > 0f) { Reset(v); return; }

            // Only touch the frame if vanilla just assigned transform.position = visualPosition this frame,
            // otherwise we'd stack offsets.
            var tr = v.transform;
            if ((tr.position - v.visualPosition).sqrMagnitude > 1e-6f) { Reset(v); return; }
            if (!NetSync.TryGetAvatarAge(a, out float age)) { Reset(v); return; }

            float t = Mathf.Clamp(age, 0f, Cfg.RemotePlayerMaxTime.Value);
            Vector3 target = Vector3.ClampMagnitude(a.rbVelocityRaw * t, 1f);
            var st = states.GetOrCreateValue(v);
            st.Off = Vector3.Lerp(st.Off, target, 1f - Mathf.Exp(-Time.deltaTime * 12f));
            tr.position = v.visualPosition + st.Off;
        }

        private static void Reset(PlayerAvatarVisuals v)
        {
            if (states.TryGetValue(v, out var st)) st.Off = Vector3.zero;
        }
    }
}
