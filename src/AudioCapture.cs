using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Streplay
{
    // Observes every sound the game starts on an AudioSource (postfix-only, never changes what plays).
    // Most remote sounds already come back through effect RPCs; this catches what never goes through
    // one: hit dings, the local player's own jump/landing/ceiling sounds, slide and fall loops, menu and
    // HUD sounds from SoundManager, and anything a third-party script (e.g. NetworkAudioSource) plays.
    internal static class AudioCapture
    {
        // > 0 while an effect RPC body is running (set by RpcCapture's prefix/finalizer).
        [ThreadStatic] internal static int RpcDepth;

        // During playback: how many sounds game code started (used to tell whether an effect RPC replay
        // made its own sound), and whether the replay itself is playing one (not counted).
        internal static int ReplayPlays;
        internal static readonly System.Collections.Generic.List<string> ReplayClips = new System.Collections.Generic.List<string>();
        internal static bool OwnPlay;

        public static void Install(Harmony harmony)
        {
            var t = typeof(AudioSource);
            Patch(harmony, t.GetMethod(nameof(AudioSource.PlayOneShot), new[] { typeof(AudioClip), typeof(float) }), nameof(PlayOneShotPostfix));
            Patch(harmony, t.GetMethod(nameof(AudioSource.Play), Type.EmptyTypes), nameof(PlayPostfix));
            Patch(harmony, t.GetMethod(nameof(AudioSource.Play), new[] { typeof(ulong) }), nameof(PlayPostfix));
            Patch(harmony, t.GetMethod(nameof(AudioSource.PlayDelayed), new[] { typeof(float) }), nameof(PlayPostfix));
        }

        static void Patch(Harmony harmony, MethodInfo target, string postfix)
        {
            try
            {
                if (target == null) throw new MissingMethodException("AudioSource", postfix);
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(AudioCapture).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Could not hook AudioSource for sound capture: {e.Message}");
            }
        }

        static void PlayOneShotPostfix(AudioSource __instance, AudioClip clip, float volumeScale) => Observe(__instance, clip, volumeScale, false);

        static void PlayPostfix(AudioSource __instance) => Observe(__instance, __instance != null ? __instance.clip : null, 1f, true);

        static void Observe(AudioSource src, AudioClip clip, float volumeScale, bool fromPlay)
        {
            try
            {
                if (src == null || clip == null) return;
                if (ReplayPlayer.Active)
                {
                    if (!OwnPlay && src.isActiveAndEnabled)
                    {
                        ReplayPlays++;
                        if (ReplayClips.Count < 64) ReplayClips.Add(clip.name);
                    }
                    return;
                }
                var rec = Recorder.Instance;
                if (rec != null && rec.IsRecording) rec.OnSound(src, clip, volumeScale, fromPlay, RpcDepth > 0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"Sound capture failed: {e.Message}");
            }
        }
    }
}
