using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Streplay
{
    // In-game the player's cameras render through post-processing (the player camera's "Camera Profile"
    // volume, plus the viewmodel cameras' own profiles). The replay camera is the map's camera, which has
    // none, so this rebuilds the same stack on the replay cameras from the local player's prefab and
    // animates the values the game animates (kill shockwave lens distortion, kill desaturation).
    internal sealed class ReplayPostFx
    {
        static readonly FieldInfo F_Resources = AccessTools.Field(typeof(PostProcessLayer), "m_Resources");

        readonly List<GameObject> _created = new List<GameObject>();
        readonly List<PostProcessLayer> _layers = new List<PostProcessLayer>();
        LensDistortion _lens;
        ColorGrading _grading;
        readonly List<(MotionBlur mb, bool on)> _blur = new List<(MotionBlur, bool)>();
        public bool Active => _layers.Count > 0;
        // the stack's shaders (the render export borrows its texture lerp for sub-frame averaging)
        public PostProcessResources Resources { get; private set; }

        // cams: the player's cameras on the local puppet (main first), with the replay camera drawing each.
        public void Setup(IList<(Camera src, Camera dst)> cams)
        {
            Dispose();
            foreach (var (src, dst) in cams)
            {
                if (src == null || dst == null) continue;
                var srcLayer = src.GetComponent<PostProcessLayer>();
                if (srcLayer == null) continue;
                var res = F_Resources?.GetValue(srcLayer) as PostProcessResources;
                if (res == null) { Plugin.Log.LogWarning("Post-process resources not found; replay has no post-processing"); return; }
                Resources = res;

                var layer = dst.GetComponent<PostProcessLayer>();
                if (layer == null)
                {
                    // added on an inactive object so OnEnable runs after the resources are set
                    bool was = dst.gameObject.activeSelf;
                    dst.gameObject.SetActive(false);
                    layer = dst.gameObject.AddComponent<PostProcessLayer>();
                    layer.Init(res);
                    layer.volumeLayer = srcLayer.volumeLayer;
                    layer.volumeTrigger = dst.transform;
                    layer.antialiasingMode = srcLayer.antialiasingMode;
                    layer.stopNaNPropagation = srcLayer.stopNaNPropagation;
                    layer.fog = srcLayer.fog;
                    dst.gameObject.SetActive(was);
                    _layers.Add(layer);
                }

                // the volumes this camera sees on the player prefab, copied so they don't depend on the
                // puppet's (often inactive) camera objects
                foreach (var vol in src.GetComponentsInChildren<PostProcessVolume>(true).Concat(src.GetComponentsInParent<PostProcessVolume>(true)).Distinct())
                {
                    if (vol.sharedProfile == null || !vol.isGlobal || (srcLayer.volumeLayer.value & (1 << vol.gameObject.layer)) == 0) continue;
                    if (_created.Any(g => g.name == "Streplay PostFx " + vol.name)) continue;
                    var go = new GameObject("Streplay PostFx " + vol.name) { layer = vol.gameObject.layer };
                    var v = go.AddComponent<PostProcessVolume>();
                    v.isGlobal = true;
                    v.priority = vol.priority;
                    v.weight = 1f;
                    v.sharedProfile = Object.Instantiate(vol.sharedProfile);
                    _created.Add(go);
                    if (v.sharedProfile.TryGetSettings(out MotionBlur anyBlur)) _blur.Add((anyBlur, false));
                    if (src == cams[0].src)
                    {
                        v.sharedProfile.TryGetSettings(out _lens);
                        v.sharedProfile.TryGetSettings(out _grading);
                        // PlayerSetup turns motion blur on from the user's setting (and never off)
                        if (Settings.Instance != null && Settings.Instance.motionBlur && v.sharedProfile.TryGetSettings(out MotionBlur mb))
                            mb.enabled.value = true;
                        if (_grading != null)
                        {
                            // the game feeds the brightness setting into gamma every frame
                            if (Settings.Instance != null) _grading.gamma.value = new Vector4(1f, 1f, 1f, Settings.Instance.brightness - 1f);
                        }
                    }
                    Plugin.Log.LogDebug($"[pp] {dst.name}: volume '{vol.sharedProfile.name}' layer {vol.gameObject.layer} (" +
                                       string.Join(", ", v.sharedProfile.settings.Where(s => s != null && s.active).Select(s => s.GetType().Name)) + ")");
                }
            }
        }

        // My view replays the recorded lens distortion / saturation; other cameras get the resting look. That's 0 for both:
        // the game eases them back to 0 every frame after a kill flash or a fall. Not the profile's current values:
        // the profile is shared with the live player camera, and stays grey (or warped) when the game stops easing it
        // back mid-flash (a round ending while you're dead).
        public void Apply(bool myView, ViewState? recorded)
        {
            if (_lens != null) _lens.intensity.value = myView && recorded.HasValue ? recorded.Value.Lens : 0f;
            if (_grading != null) _grading.saturation.value = myView && recorded.HasValue ? recorded.Value.Saturation : 0f;
        }

        // The render export makes its own motion blur from sub-frames; the per-frame effect would only add noise.
        public void SuppressMotionBlur(bool suppress)
        {
            for (int i = 0; i < _blur.Count; i++)
            {
                var (mb, was) = _blur[i];
                if (mb == null) continue;
                if (suppress) { _blur[i] = (mb, mb.enabled.value); mb.enabled.value = false; }
                else mb.enabled.value = was;
            }
        }

        public void Dispose()
        {
            foreach (var l in _layers) if (l != null) Object.Destroy(l);
            foreach (var g in _created) if (g != null) Object.Destroy(g);
            _layers.Clear();
            _created.Clear();
            _blur.Clear();
            _lens = null;
            _grading = null;
        }
    }
}
