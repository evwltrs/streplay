using BepInEx.Bootstrap;
using BepInEx.Configuration;
using UnityEngine;

namespace Streplay
{
    public enum ViewmodelStyle { Default, Kestrel }

    // Kestrel's Minimal Viewmodels Reborn, when it's installed: the viewmodel settings it currently has. They're read
    // from its config entries every time, so a change made in game (Mod Menu) shows up in the replay right away.
    // Nothing of the mod is referenced at build time; without it, TryGet says no and the default viewmodel stays.
    internal static class KestrelViewmodels
    {
        const string Guid = "kestrel.straftat.minimalviewmodels";

        internal struct Settings
        {
            public bool HideWeapon, HideArms, Mirror;
            public float Fov;
            public Vector3 Offset;
        }

        static bool _looked;
        static ConfigEntry<bool> _hideWeapon, _hideArms, _mirror;
        static ConfigEntry<float> _fov, _x, _y, _z;

        internal static bool Installed
        {
            get
            {
                if (_looked) return _fov != null;
                _looked = true;
                if (!Chainloader.PluginInfos.TryGetValue(Guid, out var info) || info.Instance == null) return false;
                var cfg = info.Instance.Config;
                const string vis = "Viewmodels.Visibility", off = "Viewmodels.Offset";
                if (cfg.TryGetEntry(vis, "Invisible Viewmodels", out _hideWeapon) && cfg.TryGetEntry(vis, "Invisible Arms", out _hideArms)
                    && cfg.TryGetEntry(vis, "Mirror Viewmodel", out _mirror) && cfg.TryGetEntry(vis, "Viewmodel FOV", out _fov)
                    && cfg.TryGetEntry(off, "Viewmodel X Offset", out _x) && cfg.TryGetEntry(off, "Viewmodel Y Offset", out _y)
                    && cfg.TryGetEntry(off, "Viewmodel Z Offset", out _z))
                {
                    Plugin.Log.LogInfo($"Kestrel's Minimal Viewmodels Reborn {info.Metadata.Version} found: its viewmodel can be used in replays");
                    return true;
                }
                Plugin.Log.LogWarning($"Kestrel's Minimal Viewmodels Reborn {info.Metadata.Version} has settings this version of STREPLAY doesn't know; replays use the default viewmodel");
                _fov = null;
                return false;
            }
        }

        internal static bool TryGet(out Settings s)
        {
            s = default;
            if (!Installed) return false;
            s.HideWeapon = _hideWeapon.Value;
            s.HideArms = _hideArms.Value;
            s.Mirror = _mirror.Value;
            s.Fov = _fov.Value;
            s.Offset = new Vector3(_x.Value, _y.Value, _z.Value);
            return true;
        }
    }
}
