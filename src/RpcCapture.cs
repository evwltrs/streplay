using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FishNet.Object;
using HarmonyLib;

namespace Streplay
{
    // Observes every ObserversRpc / TargetRpc body in the game's code. FishNet generates an
    // RpcLogic___<Name>_<hash> method for each RPC, and that method runs exactly once per
    // client whether the RPC came from the network or was run locally by its owner — so a
    // postfix on it sees the same visual events for every player, including ourselves.
    // The patch is postfix-only and never changes game behaviour.
    internal static class RpcCapture
    {
        internal const string LogicPrefix = "RpcLogic___";

        public static void Install(Harmony harmony)
        {
            var gameAsm = typeof(FirstPersonController).Assembly;
            var postfix = new HarmonyMethod(typeof(RpcCapture).GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));
            // observe-only depth counter so sounds played inside an RPC body can be attributed to it
            var enter = new HarmonyMethod(typeof(RpcCapture).GetMethod(nameof(Enter), BindingFlags.Static | BindingFlags.NonPublic));
            var leave = new HarmonyMethod(typeof(RpcCapture).GetMethod(nameof(Leave), BindingFlags.Static | BindingFlags.NonPublic));
            int patched = 0, failed = 0;

            foreach (var type in SafeTypes(gameAsm))
            {
                if (!typeof(NetworkBehaviour).IsAssignableFrom(type)) continue;
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (var rpc in methods)
                {
                    if (!rpc.IsDefined(typeof(ObserversRpcAttribute), false) && !rpc.IsDefined(typeof(TargetRpcAttribute), false)) continue;
                    string prefix = LogicPrefix + rpc.Name + "_";
                    var logic = methods.FirstOrDefault(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)
                                                            && m.GetParameters().Length == rpc.GetParameters().Length);
                    if (logic == null) continue;
                    try
                    {
                        harmony.Patch(logic, prefix: enter, postfix: postfix, finalizer: leave);
                        patched++;
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Plugin.Log.LogWarning($"Could not hook {type.Name}.{rpc.Name}: {e.Message}");
                    }
                }
            }
            Plugin.Log.LogInfo($"Hooked {patched} effect RPCs" + (failed > 0 ? $" ({failed} failed)" : ""));
        }

        static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
        }

        // "RpcLogic___ShootObserversEffect_2166136261" -> "ShootObserversEffect". The hash changes
        // between game builds, so replays store and look up methods by the bare name.
        internal static string BaseName(string logicName)
        {
            if (!logicName.StartsWith(LogicPrefix, StringComparison.Ordinal)) return logicName;
            string s = logicName.Substring(LogicPrefix.Length);
            int us = s.LastIndexOf('_');
            return us > 0 ? s.Substring(0, us) : s;
        }

        static readonly Dictionary<MethodBase, string> NameCache = new Dictionary<MethodBase, string>();

        static void Enter() => AudioCapture.RpcDepth++;

        // void finalizer without __exception: runs even if the body throws, and rethrows unchanged
        static void Leave() => AudioCapture.RpcDepth--;

        static void Postfix(NetworkBehaviour __instance, object[] __args, MethodBase __originalMethod)
        {
#if STREPLAY_AUTOTEST
            Perf.Rpc.Start(); Perf.Rpcs++;
            try { PostfixInner(__instance, __args, __originalMethod); } finally { Perf.Rpc.Stop(); }
        }

        static void PostfixInner(NetworkBehaviour __instance, object[] __args, MethodBase __originalMethod)
        {
#endif
            var rec = Recorder.Instance;
            if (rec == null || !rec.IsRecording) return;
            try
            {
                if (!NameCache.TryGetValue(__originalMethod, out var name))
                    NameCache[__originalMethod] = name = BaseName(__originalMethod.Name);
                rec.OnRpc(__instance, name, __args);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"RPC capture failed for {__originalMethod.Name}: {e.Message}");
            }
        }
    }
}
