using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Streplay
{
    // STRAFTH support: its arenas aren't part of the map, so a replay (which only reloads the map) would show empty
    // space. While recording with STRAFTH loaded, the arena you're in is written with the round: each part once when it
    // appears, then only what changed. Playback builds it again. Recordings without STRAFTH have none of it.
    internal static class ModArenaRec
    {
        public const byte Texture = 15;   // id, w, h, wrap, filter, PNG
        public const byte Material = 16;  // id, shader, queue, keywords, properties
        public const byte Mesh = 17;      // id, vertices, normals, uvs, colours, submeshes
        public const byte Part = 18;      // a renderer or light appearing, for the preceding frame
        public const byte Pose = 19;      // parts that moved, for the preceding frame
        public const byte Vis = 20;       // parts shown or hidden, for the preceding frame
        public const byte Mats = 21;      // a renderer's materials changed, for the preceding frame
        public const byte Sky = 22;       // the skybox changed (None = the map's own), for the preceding frame

        // "no texture / material / sky": instance ids of things made at runtime are negative, so -1 can't mean none
        public const int None = int.MinValue;
    }

    // ---------------------------------------------------------------- what a recording holds

    internal sealed class ModArenaData
    {
        public sealed class Tex { public int W, H; public byte Wrap, Filter; public byte[] Png; }

        public sealed class Prop
        {
            public string Name;
            public byte Type; // 0 colour, 1 vector, 2 float, 3 texture
            public Vector4 V;
            public int Tex = ModArenaRec.None;
            public Vector2 Scale = Vector2.one, Offset;
        }

        public sealed class Mat { public string Shader; public int Queue; public string[] Keywords; public List<Prop> Props = new List<Prop>(); }

        public sealed class MeshData
        {
            public Vector3[] Verts, Normals;
            public Vector2[] Uv;
            public Color32[] Colors;
            public int[][] Subs;
        }

        public sealed class Part
        {
            public int Id, Frame;
            public byte Kind; // 0 renderer, 1 light
            public string Name;
            public int Layer;
            public int Mesh = ModArenaRec.None;
            public byte Shadows;
            public bool Receive;
            public LightType LightType;
            public Color LightColor;
            public float Intensity, Range, Spot;
            public readonly List<int> PoseFrames = new List<int>();
            public readonly List<(Vector3 pos, Quaternion rot, Vector3 scale)> Poses = new List<(Vector3, Quaternion, Vector3)>();
            public readonly List<int> VisFrames = new List<int>();
            public readonly List<bool> Visible = new List<bool>();
            public readonly List<int> MatFrames = new List<int>();
            public readonly List<int[]> Mats = new List<int[]>();
        }

        public readonly Dictionary<int, Tex> Textures = new Dictionary<int, Tex>();
        public readonly Dictionary<int, Mat> Materials = new Dictionary<int, Mat>();
        public readonly Dictionary<int, MeshData> Meshes = new Dictionary<int, MeshData>();
        public readonly Dictionary<int, Part> Parts = new Dictionary<int, Part>();
        public readonly List<int> SkyFrames = new List<int>();
        public readonly List<int> Skies = new List<int>();
        public bool Any => Parts.Count > 0 || Skies.Count > 0;

        // one record (its tag already read); fi = the frame it belongs to
        public void Read(byte tag, BinaryReader r, int fi)
        {
            switch (tag)
            {
                case ModArenaRec.Texture:
                {
                    int id = r.ReadInt32();
                    var t = new Tex { W = r.ReadInt32(), H = r.ReadInt32(), Wrap = r.ReadByte(), Filter = r.ReadByte() };
                    t.Png = r.ReadBytes(r.ReadInt32());
                    Textures[id] = t;
                    break;
                }
                case ModArenaRec.Material:
                {
                    int id = r.ReadInt32();
                    var m = new Mat { Shader = r.ReadString(), Queue = r.ReadInt32() };
                    m.Keywords = new string[r.ReadUInt16()];
                    for (int i = 0; i < m.Keywords.Length; i++) m.Keywords[i] = r.ReadString();
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        var p = new Prop { Name = r.ReadString(), Type = r.ReadByte() };
                        if (p.Type == 3) { p.Tex = r.ReadInt32(); p.Scale = new Vector2(r.ReadSingle(), r.ReadSingle()); p.Offset = new Vector2(r.ReadSingle(), r.ReadSingle()); }
                        else if (p.Type == 2) p.V = new Vector4(r.ReadSingle(), 0f, 0f, 0f);
                        else p.V = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        m.Props.Add(p);
                    }
                    Materials[id] = m;
                    break;
                }
                case ModArenaRec.Mesh:
                {
                    int id = r.ReadInt32();
                    int nv = r.ReadInt32();
                    var d = new MeshData { Verts = new Vector3[nv] };
                    for (int i = 0; i < nv; i++) d.Verts[i] = r.ReadVec3();
                    if (r.ReadBoolean()) { d.Normals = new Vector3[nv]; for (int i = 0; i < nv; i++) d.Normals[i] = r.ReadVec3(); }
                    if (r.ReadBoolean()) { d.Uv = new Vector2[nv]; for (int i = 0; i < nv; i++) d.Uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle()); }
                    if (r.ReadBoolean()) { d.Colors = new Color32[nv]; for (int i = 0; i < nv; i++) d.Colors[i] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()); }
                    d.Subs = new int[r.ReadUInt16()][];
                    for (int s = 0; s < d.Subs.Length; s++)
                    {
                        var idx = new int[r.ReadInt32()];
                        for (int i = 0; i < idx.Length; i++) idx[i] = r.ReadInt32();
                        d.Subs[s] = idx;
                    }
                    Meshes[id] = d;
                    break;
                }
                case ModArenaRec.Part:
                {
                    var p = new Part { Id = r.ReadInt32(), Frame = fi, Kind = r.ReadByte(), Name = r.ReadString(), Layer = r.ReadInt32() };
                    p.PoseFrames.Add(fi);
                    p.Poses.Add((r.ReadVec3(), r.ReadQuat(), r.ReadVec3()));
                    p.VisFrames.Add(fi);
                    p.Visible.Add(r.ReadBoolean());
                    if (p.Kind == 0)
                    {
                        p.Mesh = r.ReadInt32();
                        var mats = new int[r.ReadByte()];
                        for (int i = 0; i < mats.Length; i++) mats[i] = r.ReadInt32();
                        p.MatFrames.Add(fi);
                        p.Mats.Add(mats);
                        p.Shadows = r.ReadByte();
                        p.Receive = r.ReadBoolean();
                    }
                    else
                    {
                        p.LightType = (LightType)r.ReadByte();
                        p.LightColor = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        p.Intensity = r.ReadSingle();
                        p.Range = r.ReadSingle();
                        p.Spot = r.ReadSingle();
                    }
                    Parts[p.Id] = p;
                    break;
                }
                case ModArenaRec.Pose:
                {
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        int id = r.ReadInt32();
                        var pose = (r.ReadVec3(), r.ReadQuat(), r.ReadVec3());
                        if (!Parts.TryGetValue(id, out var p)) continue;
                        if (p.PoseFrames[p.PoseFrames.Count - 1] == fi) p.Poses[p.Poses.Count - 1] = pose;
                        else { p.PoseFrames.Add(fi); p.Poses.Add(pose); }
                    }
                    break;
                }
                case ModArenaRec.Vis:
                {
                    int n = r.ReadUInt16();
                    for (int i = 0; i < n; i++)
                    {
                        int id = r.ReadInt32();
                        bool v = r.ReadBoolean();
                        if (!Parts.TryGetValue(id, out var p)) continue;
                        if (p.VisFrames[p.VisFrames.Count - 1] == fi) p.Visible[p.Visible.Count - 1] = v;
                        else { p.VisFrames.Add(fi); p.Visible.Add(v); }
                    }
                    break;
                }
                case ModArenaRec.Mats:
                {
                    int id = r.ReadInt32();
                    var mats = new int[r.ReadByte()];
                    for (int i = 0; i < mats.Length; i++) mats[i] = r.ReadInt32();
                    if (Parts.TryGetValue(id, out var p)) { p.MatFrames.Add(fi); p.Mats.Add(mats); }
                    break;
                }
                case ModArenaRec.Sky:
                    SkyFrames.Add(fi);
                    Skies.Add(r.ReadInt32());
                    break;
            }
        }

        // index of the last key at or before frame (or -1)
        public static int KeyAt(List<int> frames, int frame)
        {
            int lo = 0, hi = frames.Count - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (frames[mid] <= frame) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return best;
        }
    }

    // ---------------------------------------------------------------- recording

    internal sealed class ModArenaCapture
    {
        const string StrafthGuid = "dorsth.straftat.strafth";
        public const string StrafthName = "STRAFTH";

        static bool? _available;
        static FieldInfo _live, _root;

        // STRAFTH is loaded and is the version this expects; checked once
        public static bool Available
        {
            get
            {
                if (_available.HasValue) return _available.Value;
                _available = false;
                try
                {
                    if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(StrafthGuid)) return false;
                    var t = AccessTools.TypeByName("Strafth.Arena");
                    _live = t != null ? AccessTools.Field(t, "live") : null;
                    _root = t != null ? AccessTools.Field(t, "Root") : null;
                    _available = _live != null && _live.IsStatic && _root != null;
                    Plugin.Log.LogInfo(_available.Value ? "STRAFTH found: its arenas are recorded with the round"
                        : "STRAFTH found, but not where its arenas are kept (a newer STRAFTH?): replays won't show them");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Couldn't look for STRAFTH's arenas: {e.Message}"); }
                return _available.Value;
            }
        }

        // your kills and deaths on STRAFTH's duel scoreboard, for the recorder's kill and death markers
        static bool _scoreLooked;
        static FieldInfo _duelI, _board, _kills, _deaths;
        static PropertyInfo _duelActive, _me;

        public static bool TryDuelScore(out int kills, out int deaths)
        {
            kills = deaths = 0;
            if (!_scoreLooked)
            {
                _scoreLooked = true;
                var duel = AccessTools.TypeByName("Strafth.Duels.DuelSession");
                var line = duel != null ? AccessTools.Inner(duel, "Line") : null;
                _duelI = duel != null ? AccessTools.Field(duel, "I") : null;
                _board = duel != null ? AccessTools.Field(duel, "Board") : null;
                _duelActive = duel != null ? AccessTools.Property(duel, "Active") : null;
                _me = AccessTools.Property(AccessTools.TypeByName("Strafth.Net"), "Me");
                _kills = line != null ? AccessTools.Field(line, "Kills") : null;
                _deaths = line != null ? AccessTools.Field(line, "Deaths") : null;
            }
            if (_duelI == null || _board == null || _duelActive == null || _me == null || _kills == null || _deaths == null) return false;
            if (!(_duelActive.GetValue(null) is bool on) || !on) return false;
            var duelI = _duelI.GetValue(null);
            if (duelI == null || !(_board.GetValue(duelI) is IDictionary board) || !(_me.GetValue(null) is ulong me) || !board.Contains(me)) return false;
            var mine = board[me];
            kills = (int)_kills.GetValue(mine);
            deaths = (int)_deaths.GetValue(mine);
            return true;
        }

        sealed class Tracked
        {
            public Component C;
            public Transform T;
            public bool Visible;
            public int[] Mats;
        }

        readonly Dictionary<int, Tracked> _parts = new Dictionary<int, Tracked>();
        readonly HashSet<int> _meshes = new HashSet<int>(), _mats = new HashSet<int>(), _texs = new HashSet<int>();
        readonly List<(int id, Tracked t)> _moved = new List<(int, Tracked)>(), _shown = new List<(int, Tracked)>();
        Material _mapSky;
        int _sky = ModArenaRec.None;
        float _nextScan;
        public bool Any { get; private set; } // an arena was recorded: this is a STRAFTH round

        public void Reset()
        {
            _parts.Clear();
            _meshes.Clear();
            _mats.Clear();
            _texs.Clear();
            _mapSky = RenderSettings.skybox;
            _sky = ModArenaRec.None;
            _nextScan = 0f;
            Any = false;
        }

        // after the sample's Frame record
        public void Sample(BinaryWriter w)
        {
            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                Scan(w);
            }
            if (_parts.Count == 0) return;
            _moved.Clear();
            _shown.Clear();
            foreach (var kv in _parts)
            {
                var t = kv.Value;
                bool vis = IsShown(t.C);
                if (vis != t.Visible) { t.Visible = vis; _shown.Add((kv.Key, t)); }
                if (vis && t.T != null && t.T.hasChanged) { t.T.hasChanged = false; _moved.Add((kv.Key, t)); }
            }
            if (_moved.Count > 0)
            {
                w.Write(ModArenaRec.Pose);
                w.Write((ushort)Math.Min(_moved.Count, ushort.MaxValue));
                for (int i = 0; i < _moved.Count && i < ushort.MaxValue; i++)
                {
                    w.Write(_moved[i].id);
                    WritePose(w, _moved[i].t.T);
                }
            }
            if (_shown.Count > 0)
            {
                w.Write(ModArenaRec.Vis);
                w.Write((ushort)Math.Min(_shown.Count, ushort.MaxValue));
                for (int i = 0; i < _shown.Count && i < ushort.MaxValue; i++)
                {
                    w.Write(_shown[i].id);
                    w.Write(_shown[i].t.Visible);
                }
            }
        }

        static bool IsShown(Component c) =>
            c != null && c.gameObject.activeInHierarchy && (c is Renderer r ? r.enabled : c is Light l && l.enabled);

        static void WritePose(BinaryWriter w, Transform t)
        {
            w.Write(t.position);
            w.Write(t.rotation);
            w.Write(t.lossyScale);
        }

        void Scan(BinaryWriter w)
        {
            if (_live.GetValue(null) is IList arenas)
            {
                foreach (var a in arenas)
                {
                    if (a == null || !(_root.GetValue(a) is GameObject root) || root == null) continue;
                    foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        int id = r.GetInstanceID();
                        if (_parts.TryGetValue(id, out var known)) { CheckMats(w, id, known, r); continue; }
                        AddRenderer(w, id, r);
                    }
                    foreach (var l in root.GetComponentsInChildren<Light>(true))
                    {
                        int id = l.GetInstanceID();
                        if (!_parts.ContainsKey(id)) AddLight(w, id, l);
                    }
                }
            }
            // the arena's sky, while it's up
            var sky = RenderSettings.skybox;
            int skyId = sky == null || sky == _mapSky ? ModArenaRec.None : sky.GetInstanceID();
            if (skyId != _sky && (skyId == ModArenaRec.None || WriteMaterial(w, sky)))
            {
                _sky = skyId;
                w.Write(ModArenaRec.Sky);
                w.Write(skyId);
            }
        }

        void AddRenderer(BinaryWriter w, int id, MeshRenderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null || !mesh.isReadable) return; // nothing a replay could draw
            if (_meshes.Add(mesh.GetInstanceID())) WriteMesh(w, mesh);
            var mats = MatIds(w, r);
            var t = new Tracked { C = r, T = r.transform, Visible = IsShown(r), Mats = mats };
            _parts[id] = t;
            w.Write(ModArenaRec.Part);
            w.Write(id);
            w.Write((byte)0);
            w.Write(r.name ?? "");
            w.Write(r.gameObject.layer);
            WritePose(w, t.T);
            w.Write(t.Visible);
            w.Write(mesh.GetInstanceID());
            w.Write((byte)mats.Length);
            foreach (int m in mats) w.Write(m);
            w.Write((byte)r.shadowCastingMode);
            w.Write(r.receiveShadows);
            t.T.hasChanged = false;
            if (!Any) Plugin.Log.LogInfo("Recording a STRAFTH arena with this round");
            Any = true;
        }

        void AddLight(BinaryWriter w, int id, Light l)
        {
            var t = new Tracked { C = l, T = l.transform, Visible = IsShown(l) };
            _parts[id] = t;
            w.Write(ModArenaRec.Part);
            w.Write(id);
            w.Write((byte)1);
            w.Write(l.name ?? "");
            w.Write(l.gameObject.layer);
            WritePose(w, t.T);
            w.Write(t.Visible);
            w.Write((byte)l.type);
            w.Write(l.color.r); w.Write(l.color.g); w.Write(l.color.b); w.Write(l.color.a);
            w.Write(l.intensity);
            w.Write(l.range);
            w.Write(l.spotAngle);
            t.T.hasChanged = false;
        }

        // a part's materials can change during the round
        void CheckMats(BinaryWriter w, int id, Tracked t, MeshRenderer r)
        {
            var shared = r.sharedMaterials;
            bool same = shared.Length == t.Mats.Length;
            for (int i = 0; same && i < shared.Length; i++) same = (shared[i] != null ? shared[i].GetInstanceID() : ModArenaRec.None) == t.Mats[i];
            if (same) return;
            t.Mats = MatIds(w, r);
            w.Write(ModArenaRec.Mats);
            w.Write(id);
            w.Write((byte)t.Mats.Length);
            foreach (int m in t.Mats) w.Write(m);
        }

        int[] MatIds(BinaryWriter w, Renderer r)
        {
            var shared = r.sharedMaterials;
            var ids = new int[Math.Min(shared.Length, byte.MaxValue)];
            for (int i = 0; i < ids.Length; i++) ids[i] = shared[i] != null && WriteMaterial(w, shared[i]) ? shared[i].GetInstanceID() : ModArenaRec.None;
            return ids;
        }

        bool WriteMaterial(BinaryWriter w, Material m)
        {
            int id = m.GetInstanceID();
            if (_mats.Contains(id)) return true;
            var sh = m.shader;
            if (sh == null) return false;
            var props = new List<(string name, ShaderPropertyType type)>();
            for (int i = 0; i < sh.GetPropertyCount(); i++) props.Add((sh.GetPropertyName(i), sh.GetPropertyType(i)));
            // textures first, so the material can point at them
            var texIds = new Dictionary<string, int>();
            foreach (var (name, type) in props)
                if (type == ShaderPropertyType.Texture) texIds[name] = m.GetTexture(name) is Texture2D t2 ? WriteTexture(w, t2) : ModArenaRec.None;
            _mats.Add(id);
            w.Write(ModArenaRec.Material);
            w.Write(id);
            w.Write(sh.name);
            w.Write(m.renderQueue);
            var kw = m.shaderKeywords ?? Array.Empty<string>();
            w.Write((ushort)kw.Length);
            foreach (var k in kw) w.Write(k);
            w.Write((ushort)props.Count);
            foreach (var (name, type) in props)
            {
                w.Write(name);
                switch (type)
                {
                    case ShaderPropertyType.Texture:
                        w.Write((byte)3);
                        w.Write(texIds[name]);
                        var sc = m.GetTextureScale(name); var of = m.GetTextureOffset(name);
                        w.Write(sc.x); w.Write(sc.y); w.Write(of.x); w.Write(of.y);
                        break;
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        w.Write((byte)2);
                        w.Write(m.GetFloat(name));
                        break;
                    case ShaderPropertyType.Color:
                        w.Write((byte)0);
                        var c = m.GetColor(name);
                        w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a);
                        break;
                    default:
                        w.Write((byte)1);
                        var v = m.GetVector(name);
                        w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w);
                        break;
                }
            }
            return true;
        }

        // any texture, readable or not: drawn into a render texture and read back (at most 1024 across)
        int WriteTexture(BinaryWriter w, Texture2D t)
        {
            int id = t.GetInstanceID();
            if (_texs.Contains(id)) return id;
            byte[] png;
            int tw = t.width, th = t.height;
            try
            {
                float k = Mathf.Min(1f, 1024f / Mathf.Max(tw, th));
                int rw = Mathf.Max(1, Mathf.RoundToInt(tw * k)), rh = Mathf.Max(1, Mathf.RoundToInt(th * k));
                var rt = RenderTexture.GetTemporary(rw, rh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var prev = RenderTexture.active;
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(rw, rh, TextureFormat.RGBA32, false, false);
                read.ReadPixels(new Rect(0, 0, rw, rh), 0, 0);
                read.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                png = read.EncodeToPNG();
                UnityEngine.Object.Destroy(read);
                tw = rw; th = rh;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"STRAFTH arena texture {t.name}: {e.Message}");
                return ModArenaRec.None;
            }
            _texs.Add(id);
            w.Write(ModArenaRec.Texture);
            w.Write(id);
            w.Write(tw);
            w.Write(th);
            w.Write((byte)t.wrapMode);
            w.Write((byte)t.filterMode);
            w.Write(png.Length);
            w.Write(png);
            return id;
        }

        static void WriteMesh(BinaryWriter w, Mesh mesh)
        {
            w.Write(ModArenaRec.Mesh);
            w.Write(mesh.GetInstanceID());
            var v = mesh.vertices;
            w.Write(v.Length);
            foreach (var p in v) w.Write(p);
            var n = mesh.normals;
            w.Write(n != null && n.Length == v.Length);
            if (n != null && n.Length == v.Length) foreach (var p in n) w.Write(p);
            var uv = mesh.uv;
            w.Write(uv != null && uv.Length == v.Length);
            if (uv != null && uv.Length == v.Length) foreach (var p in uv) { w.Write(p.x); w.Write(p.y); }
            var c = mesh.colors32;
            w.Write(c != null && c.Length == v.Length);
            if (c != null && c.Length == v.Length) foreach (var p in c) { w.Write(p.r); w.Write(p.g); w.Write(p.b); w.Write(p.a); }
            w.Write((ushort)mesh.subMeshCount);
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var idx = mesh.GetTriangles(s);
                w.Write(idx.Length);
                foreach (int i in idx) w.Write(i);
            }
        }
    }

    // ---------------------------------------------------------------- playback

    internal sealed class ModArenaReplay
    {
        sealed class Built
        {
            public ModArenaData.Part P;
            public GameObject Go;
            public Renderer R;
            public int MatKey = -1, VisKey = -1, PoseKey = -1;
        }

        readonly List<Built> _built = new List<Built>();
        readonly Dictionary<int, Material> _materials = new Dictionary<int, Material>();
        readonly Dictionary<int, Texture2D> _textures = new Dictionary<int, Texture2D>();
        readonly List<Mesh> _meshes = new List<Mesh>();
        ModArenaData _data;
        GameObject _root;
        Material _mapSky, _sky;
        int _skyKey = -2;
        bool _skyFought;

        public bool Active => _root != null;

        public void Build(ReplayData replay)
        {
            Dispose();
            _data = replay.Mod;
            if (_data == null || !_data.Any) return;
            _mapSky = RenderSettings.skybox;
            _root = new GameObject("STREPLAY mod arena");
            SceneManager.MoveGameObjectToScene(_root, SceneManager.GetActiveScene());
            var meshes = new Dictionary<int, Mesh>();
            foreach (var p in _data.Parts.Values)
            {
                var go = new GameObject(p.Name);
                go.layer = p.Layer;
                go.transform.SetParent(_root.transform, false);
                var b = new Built { P = p, Go = go };
                if (p.Kind == 0)
                {
                    if (!meshes.TryGetValue(p.Mesh, out var mesh))
                    {
                        mesh = _data.Meshes.TryGetValue(p.Mesh, out var md) ? MakeMesh(md) : null;
                        meshes[p.Mesh] = mesh;
                        if (mesh != null) _meshes.Add(mesh);
                    }
                    if (mesh == null) { UnityEngine.Object.Destroy(go); continue; }
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.shadowCastingMode = (ShadowCastingMode)p.Shadows;
                    r.receiveShadows = p.Receive;
                    b.R = r;
                    // so ragdolls and dropped weapons come to rest on the arena, as they did in the round
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
                else
                {
                    var l = go.AddComponent<Light>();
                    l.type = p.LightType;
                    l.color = p.LightColor;
                    l.intensity = p.Intensity;
                    l.range = p.Range;
                    l.spotAngle = p.Spot;
                    l.shadows = LightShadows.None;
                }
                _built.Add(b);
            }
            Plugin.Log.LogInfo($"Rebuilt the round's STRAFTH arena: {_built.Count} parts, {_meshes.Count} meshes, {_data.Materials.Count} materials, {_data.Textures.Count} textures");
        }

        public void Apply(int frame, float alpha)
        {
            if (_root == null) return;
            foreach (var b in _built)
            {
                var p = b.P;
                int vk = ModArenaData.KeyAt(p.VisFrames, frame);
                bool vis = vk >= 0 && p.Visible[vk];
                if (b.Go.activeSelf != vis) b.Go.SetActive(vis);
                if (!vis) continue;
                int pk = ModArenaData.KeyAt(p.PoseFrames, frame);
                if (pk >= 0 && (pk != b.PoseKey || pk + 1 < p.PoseFrames.Count))
                {
                    var a = p.Poses[pk];
                    var (pos, rot, scale) = a;
                    // between two recorded poses (a part that moves): follow it smoothly, slow motion included
                    if (pk + 1 < p.PoseFrames.Count && p.PoseFrames[pk + 1] == frame + 1)
                    {
                        var n = p.Poses[pk + 1];
                        pos = Vector3.Lerp(a.pos, n.pos, alpha);
                        rot = Quaternion.Slerp(a.rot, n.rot, alpha);
                        scale = Vector3.Lerp(a.scale, n.scale, alpha);
                    }
                    var t = b.Go.transform;
                    t.SetPositionAndRotation(pos, rot);
                    t.localScale = scale;
                    b.PoseKey = pk;
                }
                if (b.R != null)
                {
                    int mk = ModArenaData.KeyAt(p.MatFrames, frame);
                    if (mk >= 0 && mk != b.MatKey)
                    {
                        b.MatKey = mk;
                        b.R.sharedMaterials = p.Mats[mk].Select(MaterialFor).ToArray();
                    }
                }
            }
            int sk = ModArenaData.KeyAt(_data.SkyFrames, frame);
            if (sk != _skyKey)
            {
                _skyKey = sk;
                int id = sk >= 0 ? _data.Skies[sk] : ModArenaRec.None;
                _sky = id != ModArenaRec.None ? MaterialFor(id) ?? _mapSky : _mapSky;
                Plugin.Log.LogDebug($"STRAFTH arena sky: {(_sky != null ? _sky.name + " (" + _sky.shader?.name + ")" : "none")} from frame {(sk >= 0 ? _data.SkyFrames[sk] : 0)}");
            }
            if (_sky != null && RenderSettings.skybox != _sky)
            {
                // something in the map set its own sky again
                if (!_skyFought) Plugin.Log.LogDebug($"STRAFTH arena sky: {RenderSettings.skybox?.name ?? "none"} was put back over it, setting it again");
                _skyFought = true;
                RenderSettings.skybox = _sky;
            }
        }

        Material MaterialFor(int id)
        {
            if (id == ModArenaRec.None) return null;
            if (_materials.TryGetValue(id, out var made)) return made;
            Material m = null;
            if (_data.Materials.TryGetValue(id, out var d))
            {
                var sh = ShaderFor(d.Shader);
                if (sh == null) { _materials[id] = null; return null; }
                m = new Material(sh) { renderQueue = d.Queue, shaderKeywords = d.Keywords };
                foreach (var p in d.Props)
                {
                    if (!m.HasProperty(p.Name)) continue;
                    switch (p.Type)
                    {
                        case 0: m.SetColor(p.Name, new Color(p.V.x, p.V.y, p.V.z, p.V.w)); break;
                        case 1: m.SetVector(p.Name, p.V); break;
                        case 2: m.SetFloat(p.Name, p.V.x); break;
                        case 3:
                            m.SetTexture(p.Name, TextureFor(p.Tex));
                            m.SetTextureScale(p.Name, p.Scale);
                            m.SetTextureOffset(p.Name, p.Offset);
                            break;
                    }
                }
            }
            _materials[id] = m;
            return m;
        }

        // A shader by name: Shader.Find returns whichever copy was loaded first, and another mod's asset bundle can bring
        // a copy of "Standard" that can't draw here (everything comes out magenta). So: a loaded copy that's supported,
        // preferably one the map itself draws with, else a common fallback.
        readonly Dictionary<string, Shader> _shaders = new Dictionary<string, Shader>();
        HashSet<Shader> _mapShaders;

        Shader ShaderFor(string name)
        {
            if (_shaders.TryGetValue(name, out var known)) return known;
            if (_mapShaders == null)
            {
                _mapShaders = new HashSet<Shader>();
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    foreach (var m in r.sharedMaterials)
                        if (m != null && m.shader != null) _mapShaders.Add(m.shader);
            }
            var copies = Resources.FindObjectsOfTypeAll<Shader>().Where(x => x != null && x.name == name).ToList();
            var pick = copies.FirstOrDefault(x => x.isSupported && _mapShaders.Contains(x)) ?? copies.FirstOrDefault(x => x.isSupported);
            string how = pick != null ? (_mapShaders.Contains(pick) ? "the map's" : "a loaded one") : null;
            if (pick == null)
                foreach (var fallback in new[] { "Standard", "Legacy Shaders/Diffuse", "Unlit/Texture" })
                {
                    pick = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(x => x != null && x.name == fallback && x.isSupported);
                    if (pick != null) { how = "falling back to " + fallback; break; }
                }
            Plugin.Log.LogDebug($"STRAFTH arena shader {name}: {copies.Count} loaded ({copies.Count(x => x.isSupported)} supported), using {how ?? "none"}");
            _shaders[name] = pick;
            return pick;
        }

        Texture2D TextureFor(int id)
        {
            if (id == ModArenaRec.None) return null;
            if (_textures.TryGetValue(id, out var made)) return made;
            Texture2D t = null;
            if (_data.Textures.TryGetValue(id, out var d))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { wrapMode = (TextureWrapMode)d.Wrap, filterMode = (FilterMode)d.Filter };
                if (!t.LoadImage(d.Png)) { UnityEngine.Object.Destroy(t); t = null; }
            }
            _textures[id] = t;
            return t;
        }

        static Mesh MakeMesh(ModArenaData.MeshData d)
        {
            var m = new Mesh { indexFormat = d.Verts.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.vertices = d.Verts;
            if (d.Normals != null) m.normals = d.Normals;
            if (d.Uv != null) m.uv = d.Uv;
            if (d.Colors != null) m.colors32 = d.Colors;
            m.subMeshCount = d.Subs.Length;
            for (int s = 0; s < d.Subs.Length; s++) m.SetTriangles(d.Subs[s], s);
            if (d.Normals == null) m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        public void Dispose()
        {
            if (_root != null)
            {
                RenderSettings.skybox = _mapSky;
                UnityEngine.Object.Destroy(_root);
            }
            _root = null;
            foreach (var m in _meshes) if (m != null) UnityEngine.Object.Destroy(m);
            foreach (var m in _materials.Values) if (m != null) UnityEngine.Object.Destroy(m);
            foreach (var t in _textures.Values) if (t != null) UnityEngine.Object.Destroy(t);
            _meshes.Clear();
            _materials.Clear();
            _textures.Clear();
            _built.Clear();
            _skyKey = -2;
            _sky = null;
            _skyFought = false;
            _shaders.Clear();
            _mapShaders = null;
            _data = null;
        }
    }
}
