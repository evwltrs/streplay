using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FishNet.Object;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Streplay
{
    internal enum UiKind : byte { Graphic = 0, Text = 1, Group = 2 }

    // Snapshot of one HUD element. Equality drives the per-frame change list.
    internal sealed class UiState
    {
        public bool Visible;
        public Color32 Color;
        public int Sprite = -1;          // string table id of the sprite name
        public float Fill = 1f;
        public string Text;
        public Vector2 Pos, Size;
        public Vector3 Scale = Vector3.one;
        public float RotZ;
        public float Alpha = 1f;

        // dynamic: Pos/Size are screen fractions (v5+), so "unchanged" means within ~0.1 px instead of 0.1 units
        public bool SameAs(UiState o, bool dynamic = false) =>
            o != null && Visible == o.Visible && (!Visible ||
            (Color.r == o.Color.r && Color.g == o.Color.g && Color.b == o.Color.b && Color.a == o.Color.a
             && Sprite == o.Sprite && Mathf.Abs(Fill - o.Fill) < 0.001f && Text == o.Text
             && (Pos - o.Pos).sqrMagnitude < (dynamic ? 2.5e-9f : 0.01f) && (Size - o.Size).sqrMagnitude < (dynamic ? 2.5e-9f : 0.01f)
             && (Scale - o.Scale).sqrMagnitude < 1e-6f && Mathf.Abs(RotZ - o.RotZ) < 0.05f && Mathf.Abs(Alpha - o.Alpha) < 0.002f));

        public void Write(BinaryWriter w, UiKind kind)
        {
            w.Write(Visible);
            if (!Visible) return;
            if (kind == UiKind.Group) { w.Write(Alpha); return; }
            w.Write(Color.r); w.Write(Color.g); w.Write(Color.b); w.Write(Color.a);
            w.Write(Sprite);
            w.Write(Fill);
            if (kind == UiKind.Text) w.Write(Text ?? "");
            w.Write(Pos.x); w.Write(Pos.y); w.Write(Size.x); w.Write(Size.y);
            w.Write(Scale.x); w.Write(Scale.y); w.Write(Scale.z);
            w.Write(RotZ);
        }

        public static UiState Read(BinaryReader r, UiKind kind)
        {
            var s = new UiState { Visible = r.ReadBoolean() };
            if (!s.Visible) return s;
            if (kind == UiKind.Group) { s.Alpha = r.ReadSingle(); return s; }
            s.Color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
            s.Sprite = r.ReadInt32();
            s.Fill = r.ReadSingle();
            if (kind == UiKind.Text) s.Text = r.ReadString();
            s.Pos = new Vector2(r.ReadSingle(), r.ReadSingle());
            s.Size = new Vector2(r.ReadSingle(), r.ReadSingle());
            s.Scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            s.RotZ = r.ReadSingle();
            return s;
        }
    }

    // Records the on-screen HUD (crosshair, hitmarkers, ammo, health, kill feed, round banners...)
    // as a change list per UI element, so "My view" can show the game's own HUD exactly as it was.
    internal sealed class HudCapture
    {
        public const byte AnchorDdol = 0, AnchorLocalPlayer = 1;

        sealed class Elem
        {
            public int Id;
            public UiKind Kind;
            public Component C;
            public UiState Last;
            public bool SeenThisFrame;
            public bool Dynamic;
        }

        sealed class Root
        {
            public Transform T;
            public byte Anchor;
            public Transform AnchorRoot;   // local player root for AnchorLocalPlayer
        }

        readonly Dictionary<Component, Elem> _elems = new Dictionary<Component, Elem>();
        readonly List<Elem> _visibleLast = new List<Elem>();
        readonly Dictionary<Transform, Component[]> _comps = new Dictionary<Transform, Component[]>();
        readonly Dictionary<string, int> _strings = new Dictionary<string, int>();
        readonly List<Root> _roots = new List<Root>();
        readonly HashSet<GameObject> _excluded = new HashSet<GameObject>();
        readonly List<(Elem e, UiState s)> _changes = new List<(Elem, UiState)>();
        float _nextRootScan;
        Transform _localRoot;

        // menus, and the "you're transmitting voice" note (voice isn't recorded, so it'd mean nothing in a replay)
        static readonly string[] ExcludedPauseFields = { "mainMenu", "pauseMenu", "optionsMenu", "firstInterface", "secondInterface", "voiceChatText" };

        public void Reset()
        {
            _elems.Clear();
            _visibleLast.Clear();
            _comps.Clear();
            _strings.Clear();
            _roots.Clear();
            _nextRootScan = 0f;
            _localRoot = null;
        }

        readonly MemoryStream _buf = new MemoryStream(4096);
        BinaryWriter _bw;

        // Everything is staged in a buffer and only copied to the replay once complete, so an
        // exception half way through can't leave a torn record in the file.
        public void Sample(BinaryWriter output, NetworkObject localPlayer)
        {
            _bw ??= new BinaryWriter(_buf);
            _buf.SetLength(0);
            SampleInto(_bw, localPlayer);
            _bw.Flush();
            if (_buf.Length > 0) output.Write(_buf.GetBuffer(), 0, (int)_buf.Length);
        }

        void SampleInto(BinaryWriter w, NetworkObject localPlayer)
        {
            var localRoot = localPlayer != null ? localPlayer.transform : null;
            if (Time.unscaledTime >= _nextRootScan || localRoot != _localRoot)
            {
                _nextRootScan = Time.unscaledTime + 1f;
                _localRoot = localRoot;
                ScanRoots(localRoot);
            }

            foreach (var e in _visibleLast) e.SeenThisFrame = false;
            _changes.Clear();
            foreach (var r in _roots)
                if (r.T != null && r.T.gameObject.activeInHierarchy) Walk(w, r, r.T);

            // anything visible last frame that we didn't reach this frame is now hidden
            for (int i = _visibleLast.Count - 1; i >= 0; i--)
            {
                var e = _visibleLast[i];
                if (e.SeenThisFrame) continue;
                var hidden = new UiState { Visible = false };
                if (!hidden.SameAs(e.Last)) { e.Last = hidden; _changes.Add((e, hidden)); }
                _visibleLast.RemoveAt(i);
            }

            if (_changes.Count == 0) return;
            w.Write(Rec.UiFrame);
            w.Write((ushort)Math.Min(_changes.Count, ushort.MaxValue));
            for (int i = 0; i < _changes.Count && i < ushort.MaxValue; i++)
            {
                w.Write(_changes[i].e.Id);
                _changes[i].s.Write(w, _changes[i].e.Kind);
            }
        }

        void ScanRoots(Transform localRoot)
        {
            _roots.Clear();
            _excluded.Clear();
            var pm = PauseManager.Instance;
            if (pm != null)
                foreach (var name in ExcludedPauseFields)
                    if (HarmonyLib.AccessTools.Field(typeof(PauseManager), name)?.GetValue(pm) is GameObject go && go != null)
                        _excluded.Add(go);
            // the proximity voice chat's "who's talking" tags (voice itself isn't recorded either)
            if (VoiceChatUI.Instance != null) _excluded.Add(VoiceChatUI.Instance.gameObject);

            var ddol = Net.DdolScene();
            if (ddol.IsValid())
                foreach (var go in ddol.GetRootGameObjects())
                    foreach (var c in go.GetComponentsInChildren<Canvas>(true))
                        if (c.isRootCanvas && c.renderMode != RenderMode.WorldSpace)
                            _roots.Add(new Root { T = c.transform, Anchor = AnchorDdol });

            if (localRoot != null)
                foreach (var c in localRoot.GetComponentsInChildren<Canvas>(true))
                    if (c.isRootCanvas && c.renderMode != RenderMode.WorldSpace)
                        _roots.Add(new Root { T = c.transform, Anchor = AnchorLocalPlayer, AnchorRoot = localRoot });
        }

        void Walk(BinaryWriter w, Root root, Transform t)
        {
            if (!t.gameObject.activeSelf || _excluded.Contains(t.gameObject)) return;
            if (!_comps.TryGetValue(t, out var comps))
            {
                comps = new Component[] { t.GetComponent<Canvas>(), t.GetComponent<CanvasGroup>(), t.GetComponent<Graphic>() };
                _comps[t] = comps;
            }
            if (comps[0] is Canvas canvas && canvas != null && !canvas.enabled) return;
            if (comps[1] is CanvasGroup group && group != null)
            {
                Visit(w, root, group, UiKind.Group);
                if (group.alpha <= 0.001f) return;
            }
            if (comps[2] is Graphic g && g != null && g.enabled && !(g is TMP_SubMeshUI))
                Visit(w, root, g, g is TMP_Text || g is Text ? UiKind.Text : UiKind.Graphic);
            // a container scaled to zero hides everything under it (the end-of-round scoreboard waits like that);
            // its own scale isn't recorded unless it has a Graphic, so its children count as hidden (v7)
            var ls = t.localScale;
            if (t != root.T && (Mathf.Abs(ls.x) < 1e-4f || Mathf.Abs(ls.y) < 1e-4f)) return;
            for (int i = 0; i < t.childCount; i++) Walk(w, root, t.GetChild(i));
        }

        void Visit(BinaryWriter w, Root root, Component c, UiKind kind)
        {
            if (!_elems.TryGetValue(c, out var e))
            {
                e = new Elem { Id = _elems.Count, Kind = kind, C = c, Dynamic = kind != UiKind.Group && IsSpawned(c.transform, root.T) };
                _elems[c] = e;
                w.Write(Rec.UiElement);
                w.Write(e.Id);
                w.Write(root.Anchor);
                w.Write(root.Anchor == AnchorLocalPlayer ? Recorder.RelPath(root.AnchorRoot, c.transform) : Recorder.ScenePath(c.transform));
                w.Write((byte)kind);
                w.Write(e.Dynamic);
                if (e.Dynamic)
                {
                    var img = c as Image;
                    var tmp = c as TMP_Text;
                    w.Write((byte)(img != null ? img.type : Image.Type.Simple));
                    w.Write(img != null && img.preserveAspect);
                    w.Write(tmp != null && tmp.font != null ? tmp.font.name : "");
                    w.Write(tmp != null ? tmp.fontSize : 0f);
                    w.Write(tmp != null ? (int)tmp.alignment : 0);
                    w.Write(tmp != null ? (int)tmp.fontStyle : 0);
                }
            }
            var s = Capture(w, c, kind, e.Dynamic);
            if (!e.SeenThisFrame && (e.Last == null || !e.Last.Visible)) _visibleLast.Add(e);
            e.SeenThisFrame = true;
            if (s.SameAs(e.Last, e.Dynamic)) return;
            e.Last = s;
            _changes.Add((e, s));
        }

        // HUD pieces the game instantiates on the fly (hitmarkers, feed entries) won't exist at playback.
        static bool IsSpawned(Transform t, Transform root)
        {
            for (var cur = t; cur != null && cur != root; cur = cur.parent)
                if (cur.name.EndsWith("(Clone)", StringComparison.Ordinal)) return true;
            return false;
        }

        UiState Capture(BinaryWriter w, Component c, UiKind kind, bool dynamic)
        {
            var s = new UiState { Visible = true };
            if (kind == UiKind.Group)
            {
                s.Alpha = ((CanvasGroup)c).alpha;
                return s;
            }
            var g = (Graphic)c;
            s.Color = g.color;
            if (g is Image img)
            {
                s.Sprite = img.sprite != null ? StringId(w, img.sprite.name) : -1;
                s.Fill = img.type == Image.Type.Filled ? img.fillAmount : 1f;
            }
            if (g is TMP_Text tmp) s.Text = tmp.text;
            else if (g is Text txt) s.Text = txt.text;
            var rt = g.rectTransform;
            if (dynamic)
            {
                // overlay canvas: world space is screen pixels; store resolution-independent values
                var center = rt.TransformPoint(rt.rect.center);
                var ls = rt.lossyScale;
                float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
                s.Pos = new Vector2(center.x / sw, center.y / sh);
                s.Size = new Vector2(rt.rect.width * ls.x / sw, rt.rect.height * ls.y / sw);
                s.Scale = Vector3.one;
                s.RotZ = rt.eulerAngles.z;
                return s;
            }
            s.Pos = rt.anchoredPosition;
            s.Size = rt.sizeDelta;
            s.Scale = rt.localScale;
            s.RotZ = rt.localEulerAngles.z;
            return s;
        }

        int StringId(BinaryWriter w, string s)
        {
            if (_strings.TryGetValue(s, out int id)) return id;
            id = _strings.Count;
            _strings[s] = id;
            w.Write(Rec.UiString);
            w.Write(id);
            w.Write(s);
            return id;
        }
    }
}
