using System;
using System.Collections.Generic;
using System.IO;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using UnityEngine;

namespace Streplay
{
    // On-disk layout (.strp), everything after the 8-byte prefix is deflate-compressed:
    //   "STRP" | int32 version | deflate( header, then a stream of records )
    // Each record is a one-byte tag followed by its payload. A file cut off by a crash is
    // still readable up to the last complete record.
    internal static class Rec
    {
        public const byte ObjectInfo = 1;
        public const byte Frame = 2;
        public const byte Despawn = 3;
        public const byte Event = 4;
        public const byte Marker = 5;
        public const byte PlayerInfo = 6;
        public const byte UiElement = 7;   // v4: HUD element (id, anchor, path, kind)
        public const byte UiFrame = 8;     // v4: HUD changes for the preceding frame
        public const byte UiString = 9;    // v4: string table entry (sprite names)
        public const byte Sound = 10;      // v6: one-shot sound (PlayOneShot, or Play on a non-networked source)
        public const byte AudioSrc = 11;   // v6: networked AudioSource whose clip/volume is tracked per frame
        public const byte AudioFrame = 12; // v6: AudioSource changes for the preceding frame
        public const byte Cosmetics = 13;  // v6: player suit / cigarette / hat
        public const byte View = 14;       // v6: local camera post-process + viewmodel FOV, for the preceding frame
        public const byte End = 255;
    }

    internal static class ArgTag
    {
        public const byte Null = 0, Bool = 1, Int = 2, Float = 3, String = 4, Vec3 = 5, Quat = 6,
            ObjRef = 7, SceneRef = 8, Long = 9, Double = 10, UInt = 11, Byte = 12, Short = 13,
            UShort = 14, ULong = 15, Color = 16, Vec2 = 17;
    }

    internal sealed class ReplayHeader
    {
        // v2: viewmodel; v3: player skeleton; v4: HUD; v5: spawned HUD pieces (hitmarkers);
        // v6: sounds, looping audio sources, cosmetics, camera post-process
        // 7: spawned HUD pieces record small moves (kill feed lines moving up); HUD under zero-scale containers is hidden
        public const int CurrentVersion = 7;
        public int Version = CurrentVersion;
        public string GameVersion = "";
        public string PluginVersion = "";
        public string Scene = "";
        public long StartUtcTicks;
        public string SessionId = "";
        public int RoundIndex;
        public float SampleRate;
    }

    internal sealed class ObjectInfo
    {
        public int Index;          // unique within the replay (object ids get reused by FishNet)
        public int ObjectId;
        public bool IsSceneObject;
        public ulong SceneId;
        public ushort PrefabId;
        public ushort CollectionId;
        public string Name = "";
        public bool IsPlayer;
        public string[] ChildPaths = new string[0];  // NetworkTransform children, in frame order
        public string[] AnimPaths = new string[0];   // Animators, in frame order
        public string[] VmPaths = new string[0];     // v2: local player's first-person arms (first = arms root)
        public string[] BonePaths = new string[0];   // v3: third-person skeleton bones
        public bool[] BoneIsRoot = new bool[0];      // v3: root bones also store their local position
        public PlayerInfo Player;
        public Cosmetics Cosmetics;  // v6 (older files: suit only, from PlayerInfo.Mat)
        public float SpawnTime;
        public float DespawnTime = float.MaxValue;
    }

    internal sealed class PlayerInfo
    {
        public int PlayerId = -1;
        public ulong SteamId;
        public string Name = "";
        public bool IsLocal;
        public int Mat;
    }

    internal sealed class Cosmetics
    {
        public int Mat = -1;
        public int Cig = -1;
        public string Hat = "";
    }

    internal static class SoundFlags
    {
        public const byte InRpc = 1;     // played inside an effect RPC body (the RPC replay normally plays it)
        public const byte FromPlay = 2;  // AudioSource.Play rather than PlayOneShot
        public const byte Ddol = 4;      // Path is a DontDestroyOnLoad scene path (SoundManager & co.)
        public const byte ScenePath = 8; // Path is a scene path (no NetworkObject)
    }

    internal sealed class SoundEvent
    {
        public float T;
        public byte Flags;
        public int Obj = -1;       // ObjectInfo.Index, -1 if not under a networked object
        public string Path = "";   // child path under Obj, or scene path (see flags)
        public byte SourceIndex;   // which AudioSource on that GameObject
        public Vector3 Pos;
        public string Clip = "";
        public float VolumeScale = 1f;
        public float SourceVolume = 1f;
        public float Pitch = 1f;
        public float SpatialBlend;
        public float MinDistance = 1f, MaxDistance = 500f;
        public byte Rolloff;
        public ReplayEvent Rpc;    // the effect RPC this sound was played by (InRpc only)
        public bool InRpc => (Flags & SoundFlags.InRpc) != 0;
    }

    internal struct AudioSrcState
    {
        public bool Playing;
        public bool Loop;
        public string Clip;
        public float Volume;
        public float Pitch;
        public int PlayCount;      // bumps every time Play() restarts the clip
        public float StartT;       // derived on load: replay time this play started

        public bool SameAs(in AudioSrcState o) =>
            Playing == o.Playing && Loop == o.Loop && Clip == o.Clip && PlayCount == o.PlayCount
            && Mathf.Abs(Volume - o.Volume) < 0.004f && Mathf.Abs(Pitch - o.Pitch) < 0.004f;
    }

    internal sealed class AudioSrcInfo
    {
        public int Id;
        public int Obj;            // ObjectInfo.Index
        public string Path = "";
        public byte SourceIndex;
        public readonly List<int> Frames = new List<int>();
        public readonly List<AudioSrcState> States = new List<AudioSrcState>();
    }

    internal struct ViewState
    {
        public float Lens;         // LensDistortion.intensity (kill shockwave, falling)
        public float Saturation;   // ColorGrading.saturation (kill flash)
        public float VmFov;        // first viewmodel camera FOV, 0 if none

        public bool SameAs(in ViewState o) =>
            Mathf.Abs(Lens - o.Lens) < 0.05f && Mathf.Abs(Saturation - o.Saturation) < 0.05f && Mathf.Abs(VmFov - o.VmFov) < 0.01f;
    }

    internal struct AnimLayer
    {
        public int Hash;
        public float Time;
        public float Weight;
    }

    internal sealed class AnimState
    {
        public bool Enabled;
        public AnimLayer[] Layers;
        public float[] Params;
    }

    internal sealed class ObjState
    {
        public int Obj;            // ObjectInfo.Index
        public bool Active;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Scale;
        public Vector3[] ChildPos;
        public Quaternion[] ChildRot;
        public AnimState[] Anims;
        // player only
        public bool HasPlayer;
        public bool GraphicsActive;
        public Vector3 PovPos;
        public Quaternion PovRot;
        public int HeldRight = -1;  // ObjectInfo.Index or -1
        public int HeldLeft = -1;
        // v2, local player only: first-person arms pose (root in world space, the rest local)
        public bool VmActive;
        public Vector3[] VmPos;
        public Quaternion[] VmRot;
        // v3, players: third-person skeleton (local space)
        public Quaternion[] BoneRot;
        public Vector3[] BonePos;
    }

    internal sealed class Frame
    {
        public float T;
        public Vector3 CamPos;
        public Quaternion CamRot;
        public float CamFov;
        public ObjState[] Objects;
    }

    internal sealed class ReplayEvent
    {
        public float T;
        public int Obj;            // ObjectInfo.Index, -1 if unresolved
        public string ChildPath;   // path from the NetworkObject root to the component's GameObject
        public string ComponentType;
        public string Method;      // RPC name without the RpcLogic___ prefix and hash suffix
        public object[] Args;
        public List<SoundEvent> Sounds;  // v6: sounds this RPC played while recording
    }

    internal sealed class ArgObjRef
    {
        public int Obj;            // ObjectInfo.Index
        public string Path;
    }

    internal sealed class ArgSceneRef
    {
        public string Path;
    }

    internal sealed class Marker
    {
        public float T;
        public string Label = "";
        public bool Auto;
    }

    internal sealed class UiElementInfo
    {
        public int Id;
        public byte Anchor;
        public string Path;
        public UiKind Kind;
        // v5: elements living under a spawned (Clone) object get rebuilt on playback from this template;
        // their Pos/Size are normalized screen coordinates instead of local RectTransform values.
        public bool Dynamic;
        public byte ImageType;
        public bool PreserveAspect;
        public string Font;
        public float FontSize;
        public int Alignment;
        public int FontStyle;
        // change list: frame index -> state (sorted by frame)
        public readonly List<int> Frames = new List<int>();
        public readonly List<UiState> States = new List<UiState>();
    }

    internal sealed class ReplayData
    {
        public readonly Dictionary<int, UiElementInfo> Ui = new Dictionary<int, UiElementInfo>();
        public readonly Dictionary<int, string> UiStrings = new Dictionary<int, string>();
        public ReplayHeader Header;
        public readonly List<ObjectInfo> Objects = new List<ObjectInfo>();
        public readonly List<Frame> Frames = new List<Frame>();
        public readonly List<ReplayEvent> Events = new List<ReplayEvent>();
        public readonly List<Marker> Markers = new List<Marker>();
        public readonly List<SoundEvent> Sounds = new List<SoundEvent>();
        public readonly Dictionary<int, AudioSrcInfo> AudioSources = new Dictionary<int, AudioSrcInfo>();
        public readonly List<int> ViewFrames = new List<int>();
        public readonly List<ViewState> ViewStates = new List<ViewState>();
        public float Duration;
        public bool Truncated;
    }

    internal static class ReplayIO
    {
        static readonly byte[] Magic = { (byte)'S', (byte)'T', (byte)'R', (byte)'P' };

        public static BinaryWriter OpenWrite(string path, ReplayHeader h)
        {
            var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            fs.Write(Magic, 0, 4);
            fs.Write(BitConverter.GetBytes(h.Version), 0, 4);
            var deflate = new DeflaterOutputStream(fs, new Deflater(Deflater.BEST_SPEED), 1 << 16);
            var w = new BinaryWriter(deflate);
            w.Write(h.GameVersion);
            w.Write(h.PluginVersion);
            w.Write(h.Scene);
            w.Write(h.StartUtcTicks);
            w.Write(h.SessionId);
            w.Write(h.RoundIndex);
            w.Write(h.SampleRate);
            return w;
        }

        public static void Write(this BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        public static void Write(this BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        // Rotations packed as 4 x int16; plenty for bone poses and 2x smaller than floats.
        public static void WriteQuatPacked(this BinaryWriter w, Quaternion q)
        {
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.x, -1f, 1f) * 32767f));
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.y, -1f, 1f) * 32767f));
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.z, -1f, 1f) * 32767f));
            w.Write((short)Mathf.RoundToInt(Mathf.Clamp(q.w, -1f, 1f) * 32767f));
        }

        public static Quaternion ReadQuatPacked(BinaryReader r)
        {
            var q = new Quaternion(r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f, r.ReadInt16() / 32767f);
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 1e-5f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }

        public static Vector3 ReadVec3(this BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        public static Quaternion ReadQuat(this BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static ReplayData Read(string path)
        {
            var data = new ReplayData();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var prefix = new byte[8];
            if (fs.Read(prefix, 0, 8) != 8 || prefix[0] != Magic[0] || prefix[1] != Magic[1] || prefix[2] != Magic[2] || prefix[3] != Magic[3])
                throw new InvalidDataException("Not a Streplay file");
            int version = BitConverter.ToInt32(prefix, 4);
            if (version > ReplayHeader.CurrentVersion)
                throw new InvalidDataException($"Replay version {version} is newer than this plugin supports ({ReplayHeader.CurrentVersion})");

            using var r = new BinaryReader(new BufferedStream(new InflaterInputStream(fs), 1 << 16));
            var h = new ReplayHeader
            {
                Version = version,
                GameVersion = r.ReadString(),
                PluginVersion = r.ReadString(),
                Scene = r.ReadString(),
                StartUtcTicks = r.ReadInt64(),
                SessionId = r.ReadString(),
                RoundIndex = r.ReadInt32(),
                SampleRate = r.ReadSingle(),
            };
            data.Header = h;

            // ObjectId -> current ObjectInfo.Index while reading sequentially
            var live = new Dictionary<int, int>();
            var boneRoots = new Dictionary<int, bool[]>();
            var pendingRpcSounds = new List<SoundEvent>();
            try
            {
                while (true)
                {
                    int tag = r.BaseStream.ReadByte();
                    if (tag < 0) { data.Truncated = true; break; }
                    if (tag == Rec.End) { data.Duration = r.ReadSingle(); break; }
                    switch ((byte)tag)
                    {
                        case Rec.ObjectInfo:
                        {
                            var o = new ObjectInfo
                            {
                                Index = data.Objects.Count,
                                SpawnTime = r.ReadSingle(),
                                ObjectId = r.ReadInt32(),
                                IsSceneObject = r.ReadBoolean(),
                                SceneId = r.ReadUInt64(),
                                PrefabId = r.ReadUInt16(),
                                CollectionId = r.ReadUInt16(),
                                Name = r.ReadString(),
                                IsPlayer = r.ReadBoolean(),
                            };
                            o.ChildPaths = new string[r.ReadUInt16()];
                            for (int i = 0; i < o.ChildPaths.Length; i++) o.ChildPaths[i] = r.ReadString();
                            o.AnimPaths = new string[r.ReadUInt16()];
                            for (int i = 0; i < o.AnimPaths.Length; i++) o.AnimPaths[i] = r.ReadString();
                            if (version >= 2)
                            {
                                o.VmPaths = new string[r.ReadUInt16()];
                                for (int i = 0; i < o.VmPaths.Length; i++) o.VmPaths[i] = r.ReadString();
                            }
                            if (version >= 3)
                            {
                                int nb = r.ReadUInt16();
                                o.BonePaths = new string[nb];
                                o.BoneIsRoot = new bool[nb];
                                for (int i = 0; i < nb; i++) { o.BonePaths[i] = r.ReadString(); o.BoneIsRoot[i] = r.ReadBoolean(); }
                                boneRoots[o.Index] = o.BoneIsRoot;
                            }
                            if (live.TryGetValue(o.ObjectId, out int prev) && data.Objects[prev].DespawnTime == float.MaxValue)
                                data.Objects[prev].DespawnTime = o.SpawnTime;
                            live[o.ObjectId] = o.Index;
                            data.Objects.Add(o);
                            break;
                        }
                        case Rec.PlayerInfo:
                        {
                            int id = r.ReadInt32();
                            var p = new PlayerInfo
                            {
                                PlayerId = r.ReadInt32(),
                                SteamId = r.ReadUInt64(),
                                Name = r.ReadString(),
                                IsLocal = r.ReadBoolean(),
                                Mat = r.ReadInt32(),
                            };
                            if (live.TryGetValue(id, out int idx))
                            {
                                data.Objects[idx].Player = p;
                                if (version < 6 && data.Objects[idx].Cosmetics == null) data.Objects[idx].Cosmetics = new Cosmetics { Mat = p.Mat };
                            }
                            break;
                        }
                        case Rec.Despawn:
                        {
                            float t = r.ReadSingle();
                            int id = r.ReadInt32();
                            if (live.TryGetValue(id, out int idx))
                            {
                                data.Objects[idx].DespawnTime = t;
                                live.Remove(id);
                            }
                            break;
                        }
                        case Rec.Frame:
                            data.Frames.Add(ReadFrame(r, live, version, boneRoots));
                            break;
                        case Rec.Event:
                        {
                            var ev = ReadEvent(r, live);
                            // sounds played inside this RPC's body were written just before it, at the same time
                            foreach (var snd in pendingRpcSounds)
                            {
                                if (snd.T != ev.T) continue;
                                snd.Rpc = ev;
                                (ev.Sounds ??= new List<SoundEvent>()).Add(snd);
                            }
                            pendingRpcSounds.Clear(); // sounds of an RPC that wasn't written play on their own
                            data.Events.Add(ev);
                            break;
                        }
                        case Rec.Sound:
                        {
                            var snd = ReadSound(r, live);
                            data.Sounds.Add(snd);
                            if (snd.InRpc) pendingRpcSounds.Add(snd);
                            break;
                        }
                        case Rec.AudioSrc:
                        {
                            var src = new AudioSrcInfo { Id = r.ReadInt32() };
                            src.Obj = Resolve(live, r.ReadInt32());
                            src.Path = r.ReadString();
                            src.SourceIndex = r.ReadByte();
                            data.AudioSources[src.Id] = src;
                            break;
                        }
                        case Rec.AudioFrame:
                        {
                            int fi = Math.Max(0, data.Frames.Count - 1);
                            float ft = data.Frames.Count > 0 ? data.Frames[fi].T : 0f;
                            int n = r.ReadUInt16();
                            for (int i = 0; i < n; i++)
                            {
                                int id = r.ReadInt32();
                                var st = new AudioSrcState
                                {
                                    Playing = r.ReadBoolean(), Loop = r.ReadBoolean(), Clip = r.ReadString(),
                                    Volume = r.ReadSingle(), Pitch = r.ReadSingle(), PlayCount = r.ReadInt32(),
                                };
                                if (!data.AudioSources.TryGetValue(id, out var src)) throw new InvalidDataException($"Audio source {id} used before definition");
                                int last = src.States.Count - 1;
                                bool restarted = last < 0 || !src.States[last].Playing || src.States[last].PlayCount != st.PlayCount || src.States[last].Clip != st.Clip;
                                st.StartT = !restarted ? src.States[last].StartT : ft;
                                if (last >= 0 && src.Frames[last] == fi) src.States[last] = st;
                                else { src.Frames.Add(fi); src.States.Add(st); }
                            }
                            break;
                        }
                        case Rec.Cosmetics:
                        {
                            int id = r.ReadInt32();
                            var c = new Cosmetics { Mat = r.ReadInt32(), Cig = r.ReadInt32(), Hat = r.ReadString() };
                            if (live.TryGetValue(id, out int idx)) data.Objects[idx].Cosmetics = c;
                            break;
                        }
                        case Rec.View:
                        {
                            int fi = Math.Max(0, data.Frames.Count - 1);
                            var v = new ViewState { Lens = r.ReadSingle(), Saturation = r.ReadSingle(), VmFov = r.ReadSingle() };
                            if (data.ViewFrames.Count > 0 && data.ViewFrames[data.ViewFrames.Count - 1] == fi) data.ViewStates[data.ViewStates.Count - 1] = v;
                            else { data.ViewFrames.Add(fi); data.ViewStates.Add(v); }
                            break;
                        }
                        case Rec.UiElement:
                        {
                            var el = new UiElementInfo { Id = r.ReadInt32(), Anchor = r.ReadByte(), Path = r.ReadString(), Kind = (UiKind)r.ReadByte() };
                            if (version >= 5 && (el.Dynamic = r.ReadBoolean()))
                            {
                                el.ImageType = r.ReadByte();
                                el.PreserveAspect = r.ReadBoolean();
                                el.Font = r.ReadString();
                                el.FontSize = r.ReadSingle();
                                el.Alignment = r.ReadInt32();
                                el.FontStyle = r.ReadInt32();
                            }
                            data.Ui[el.Id] = el;
                            break;
                        }
                        case Rec.UiString:
                        {
                            int id = r.ReadInt32();
                            data.UiStrings[id] = r.ReadString();
                            break;
                        }
                        case Rec.UiFrame:
                        {
                            int fi = Math.Max(0, data.Frames.Count - 1);
                            int n = r.ReadUInt16();
                            for (int i = 0; i < n; i++)
                            {
                                int id = r.ReadInt32();
                                if (!data.Ui.TryGetValue(id, out var el)) throw new InvalidDataException($"HUD element {id} used before definition");
                                var st = UiState.Read(r, el.Kind);
                                if (el.Frames.Count > 0 && el.Frames[el.Frames.Count - 1] == fi)
                                    el.States[el.States.Count - 1] = st;
                                else { el.Frames.Add(fi); el.States.Add(st); }
                            }
                            break;
                        }
                        case Rec.Marker:
                            data.Markers.Add(new Marker { T = r.ReadSingle(), Label = r.ReadString(), Auto = r.ReadBoolean() });
                            break;
                        default:
                            throw new InvalidDataException($"Unknown record tag {tag}");
                    }
                }
            }
            catch (Exception e) when (e is EndOfStreamException || e is ICSharpCode.SharpZipLib.SharpZipBaseException || e is IOException)
            {
                // Recording was interrupted (game closed or crashed mid-round); keep what we have.
                data.Truncated = true;
            }

            if (data.Frames.Count > 0 && (data.Truncated || data.Duration <= 0f))
                data.Duration = data.Frames[data.Frames.Count - 1].T;
            return data;
        }

        static int Resolve(Dictionary<int, int> live, int objectId) =>
            objectId >= 0 && live.TryGetValue(objectId, out int idx) ? idx : -1;

        static Frame ReadFrame(BinaryReader r, Dictionary<int, int> live, int version, Dictionary<int, bool[]> boneRoots)
        {
            var f = new Frame
            {
                T = r.ReadSingle(),
                CamPos = r.ReadVec3(),
                CamRot = r.ReadQuat(),
                CamFov = r.ReadSingle(),
            };
            int n = r.ReadUInt16();
            var list = new List<ObjState>(n);
            for (int i = 0; i < n; i++)
            {
                int id = r.ReadInt32();
                var s = new ObjState
                {
                    Obj = Resolve(live, id),
                    Active = r.ReadBoolean(),
                    Pos = r.ReadVec3(),
                    Rot = r.ReadQuat(),
                    Scale = r.ReadVec3(),
                };
                int nc = r.ReadByte();
                s.ChildPos = new Vector3[nc];
                s.ChildRot = new Quaternion[nc];
                for (int c = 0; c < nc; c++) { s.ChildPos[c] = r.ReadVec3(); s.ChildRot[c] = r.ReadQuat(); }
                int na = r.ReadByte();
                s.Anims = new AnimState[na];
                for (int a = 0; a < na; a++)
                {
                    var an = new AnimState { Enabled = r.ReadBoolean() };
                    int nl = r.ReadByte();
                    an.Layers = new AnimLayer[nl];
                    for (int l = 0; l < nl; l++)
                        an.Layers[l] = new AnimLayer { Hash = r.ReadInt32(), Time = r.ReadSingle(), Weight = r.ReadSingle() };
                    int np = r.ReadByte();
                    an.Params = new float[np];
                    for (int p = 0; p < np; p++) an.Params[p] = r.ReadSingle();
                    s.Anims[a] = an;
                }
                s.HasPlayer = r.ReadBoolean();
                if (s.HasPlayer)
                {
                    s.GraphicsActive = r.ReadBoolean();
                    s.PovPos = r.ReadVec3();
                    s.PovRot = r.ReadQuat();
                    s.HeldRight = Resolve(live, r.ReadInt32());
                    s.HeldLeft = Resolve(live, r.ReadInt32());
                    int nvm = version >= 2 ? r.ReadByte() : 0;
                    if (nvm > 0)
                    {
                        s.VmActive = r.ReadBoolean();
                        s.VmPos = new Vector3[nvm];
                        s.VmRot = new Quaternion[nvm];
                        for (int v = 0; v < nvm; v++) { s.VmPos[v] = r.ReadVec3(); s.VmRot[v] = r.ReadQuat(); }
                    }
                    int nb = version >= 3 ? r.ReadUInt16() : 0;
                    if (nb > 0)
                    {
                        boneRoots.TryGetValue(s.Obj, out var roots);
                        s.BoneRot = new Quaternion[nb];
                        s.BonePos = new Vector3[nb];
                        for (int b = 0; b < nb; b++)
                        {
                            s.BoneRot[b] = ReadQuatPacked(r);
                            if (roots != null && b < roots.Length && roots[b]) s.BonePos[b] = r.ReadVec3();
                        }
                    }
                }
                if (s.Obj >= 0) list.Add(s);
            }
            f.Objects = list.ToArray();
            return f;
        }

        public static void WriteSound(BinaryWriter w, SoundEvent s, int objectId)
        {
            w.Write(s.T);
            w.Write(s.Flags);
            w.Write(objectId);
            w.Write(s.Path ?? "");
            w.Write(s.SourceIndex);
            w.Write(s.Pos);
            w.Write(s.Clip ?? "");
            w.Write(s.VolumeScale);
            w.Write(s.SourceVolume);
            w.Write(s.Pitch);
            w.Write(s.SpatialBlend);
            w.Write(s.MinDistance);
            w.Write(s.MaxDistance);
            w.Write(s.Rolloff);
        }

        static SoundEvent ReadSound(BinaryReader r, Dictionary<int, int> live) => new SoundEvent
        {
            T = r.ReadSingle(),
            Flags = r.ReadByte(),
            Obj = Resolve(live, r.ReadInt32()),
            Path = r.ReadString(),
            SourceIndex = r.ReadByte(),
            Pos = r.ReadVec3(),
            Clip = r.ReadString(),
            VolumeScale = r.ReadSingle(),
            SourceVolume = r.ReadSingle(),
            Pitch = r.ReadSingle(),
            SpatialBlend = r.ReadSingle(),
            MinDistance = r.ReadSingle(),
            MaxDistance = r.ReadSingle(),
            Rolloff = r.ReadByte(),
        };

        static ReplayEvent ReadEvent(BinaryReader r, Dictionary<int, int> live)
        {
            var e = new ReplayEvent
            {
                T = r.ReadSingle(),
                Obj = Resolve(live, r.ReadInt32()),
                ChildPath = r.ReadString(),
                ComponentType = r.ReadString(),
                Method = r.ReadString(),
            };
            int argc = r.ReadByte();
            e.Args = new object[argc];
            for (int i = 0; i < argc; i++) e.Args[i] = ReadArg(r, live);
            return e;
        }

        static object ReadArg(BinaryReader r, Dictionary<int, int> live)
        {
            byte tag = r.ReadByte();
            switch (tag)
            {
                case ArgTag.Null: return null;
                case ArgTag.Bool: return r.ReadBoolean();
                case ArgTag.Int: return r.ReadInt32();
                case ArgTag.Float: return r.ReadSingle();
                case ArgTag.String: return r.ReadString();
                case ArgTag.Vec3: return r.ReadVec3();
                case ArgTag.Quat: return r.ReadQuat();
                case ArgTag.ObjRef:
                {
                    int id = r.ReadInt32();
                    string path = r.ReadString();
                    int idx = Resolve(live, id);
                    return idx >= 0 ? new ArgObjRef { Obj = idx, Path = path } : null;
                }
                case ArgTag.SceneRef: return new ArgSceneRef { Path = r.ReadString() };
                case ArgTag.Long: return r.ReadInt64();
                case ArgTag.Double: return r.ReadDouble();
                case ArgTag.UInt: return r.ReadUInt32();
                case ArgTag.Byte: return r.ReadByte();
                case ArgTag.Short: return r.ReadInt16();
                case ArgTag.UShort: return r.ReadUInt16();
                case ArgTag.ULong: return r.ReadUInt64();
                case ArgTag.Color: return new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                case ArgTag.Vec2: return new Vector2(r.ReadSingle(), r.ReadSingle());
                default: throw new InvalidDataException($"Unknown arg tag {tag}");
            }
        }
    }
}
