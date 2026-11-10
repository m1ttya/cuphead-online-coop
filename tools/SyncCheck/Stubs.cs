// Minimal engine boundary for deterministic tests of the actual production sync code.
// Does not emulate Unity rendering, Rewired polling, Harmony patch application or audio playback.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    public class Object
    {
        public string name;
        public static Object[] Loaded = new Object[0];
        public static T[] FindObjectsOfType<T>() where T : Object
        {
            var found = new List<T>();
            foreach (Object value in Loaded) if (value is T item) found.Add(item);
            return found.ToArray();
        }
    }
    public class Transform : Object
    {
        public Transform parent;
        public readonly List<Transform> Children = new List<Transform>();
        public int GetSiblingIndex() => parent == null ? 0 : parent.Children.IndexOf(this);
        public Transform GetChild(int index) => Children[index];
    }
    public class AudioClip : Object { public float length = 120f; }
    public class AudioSource : Object
    {
        public AudioClip clip;
        public bool isPlaying, loop, ignoreListenerPause;
        public float time, volume = 1f, pitch = 1f;
        public int Plays;
        public void Stop() { isPlaying = false; }
        public void Play() { isPlaying = true; Plays++; }
        public DeferredAudioSource Deferred;
        public T GetComponent<T>() where T : class => Deferred as T;
    }
    public static class AudioListener { public static bool pause; }
    public static class Time
    {
        public static bool inFixedTimeStep;
        public static float fixedTime, realtimeSinceStartup, unscaledTime;
        public static int frameCount;
    }
    public static class Mathf
    {
        public static int RoundToInt(float x) => (int)Math.Round(x);
        public static float Clamp(float x, float min, float max) => Math.Max(min, Math.Min(max, x));
        public static float Repeat(float x, float length) => x - (float)Math.Floor(x / length) * length;
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type type, string method) { }
        public HarmonyPatch(Type type, string method, Type[] args) { }
    }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }
}
namespace Rewired
{
    public enum UpdateLoopType { Update, FixedUpdate, OnGUI }
    public enum ControllerType { Keyboard, Joystick }
    public enum ControllerElementType { Button, Axis }
    public enum ModifierKey { None, Control }
    public enum KeyCode { None, LeftShift, Tab, K }
    public class Controller
    {
        public uint Buttons;
        public bool GetButtonById(int id) => (Buttons & (1u << id)) != 0;
    }
    public class Keyboard : Controller
    {
        public KeyCode HeldKey;
        public bool Control;
        public bool GetKey(KeyCode key) => key != KeyCode.None && key == HeldKey;
        public bool GetModifierKey(ModifierKey key) => key == ModifierKey.None || Control;
    }
    public class ActionElementMap
    {
        public int actionId, elementIdentifierId;
        public bool enabled = true;
        public ControllerElementType elementType;
        public KeyCode keyCode;
        public ModifierKey modifierKey1, modifierKey2, modifierKey3;
    }
    public class ControllerMap
    {
        public bool enabled = true;
        public ControllerType controllerType;
        public int controllerId;
        public ActionElementMap[] Bindings = Array.Empty<ActionElementMap>();
        public ActionElementMap[] GetElementMaps() => Bindings;
        public IList<ActionElementMap> AllMaps => Bindings;
    }
    public class MapHelper
    {
        public List<ControllerMap> All = new List<ControllerMap>();
        public IEnumerable<ControllerMap> GetAllMaps() => All;
    }
    public class ControllerHelper
    {
        public MapHelper maps = new MapHelper();
        public Controller Assigned;
        public Controller GetController(ControllerType type, int id) => Assigned;
    }
    public class Player
    {
        public int id;
        public uint Held;
        public uint Down;
        public ControllerHelper controllers = new ControllerHelper();
        public bool GetButton(int action) => (Held & (1u << action)) != 0;
        public bool GetButtonDown(int action) => (Down & (1u << action)) != 0;
        public float GetAxis(int action) => 0f;
    }
    public static class ReInput
    {
        internal static event Action<UpdateLoopType> UpdateEndedEvent;
        public static void EndUpdate(UpdateLoopType loop) { UpdateEndedEvent?.Invoke(loop); }
        public class Players
        {
            public Player Local = new Player();
            public Player GetPlayer(int id) => Local;
        }
        public static Players players = new Players();
    }
}
public enum PlayerId { PlayerOne, PlayerTwo }
public class PlayerManager { public static bool Multiplayer; }
public static class SceneLoader { public static bool CurrentlyLoading; }
public class DeferredAudioSource
{
    public Action OnInitialize;
    public void Initialize() { OnInitialize?.Invoke(); }
}
public static class AudioManager
{
    public static int MixerResets;
    public static void SnapshotReset(string scene, float time) { MixerResets++; }
}
public class AudioManagerComponent : UnityEngine.Object
{
    public UnityEngine.Transform transform = new UnityEngine.Transform { name = "SceneAudio" };
    public List<SoundGroup.Source> bgmSources = new List<SoundGroup.Source>();
    public List<SoundGroup.Source> bgmAlternates = new List<SoundGroup.Source>();
    public List<SoundGroup> bgmPlaylist = new List<SoundGroup>();
    public T[] GetComponents<T>() => new[] { (T)(object)this };
    public class SoundGroup
    {
        public List<Source> sources = new List<Source>();
        public class Source { public UnityEngine.AudioSource audio; }
    }
}
namespace CupheadOnline
{
    public static class Plugin
    {
        public class Logger
        {
            public readonly List<string> Warnings = new List<string>();
            public void LogError(object message) { throw new Exception(message.ToString()); }
            public void LogWarning(object message) { Warnings.Add(message.ToString()); }
            public void LogDebug(object message) { }
            public void LogInfo(object message) { }
        }
        public static Logger Log = new Logger();
    }
}
namespace CupheadOnline.Sync
{
    internal static class ClientController { public static void EnsureAssigned(Rewired.Player player) { } }
    public static class TestDriver { public static VirtualPad HostPad; public static int KeyboardPulse; public static bool NativeButtonTest; }
    public static class ClientMirror { public static bool Loading; public static ushort LoadedSerial = 1; public static string LoadedScene = "scene_map"; }
    public static class AudioSync { public static bool Replaying; }
}
namespace CupheadOnline.Net
{
    public enum Role { None, Host, Client }
    public static class Session { public static Role Role; public static bool Reconnecting; public static Connection Connection; public static bool IsClient => Role == Role.Client; public static bool IsHost => Role == Role.Host; }
    public interface IDatagramTransport : IDisposable
    {
        bool HasPeer { get; }
        string PeerDescription { get; }
        int MaxDatagramSize { get; }
        void Send(byte[] data, int length);
        bool TryReceive(out byte[] data, out int length);
    }
    public class MemoryTransport : IDatagramTransport
    {
        public readonly List<byte[]> Sent = new List<byte[]>();
        public readonly Queue<byte[]> Received = new Queue<byte[]>();
        public bool HasPeer => true;
        public string PeerDescription => "test";
        public int MaxDatagramSize => 1200;
        public void Send(byte[] data, int length) { byte[] copy = new byte[length]; Array.Copy(data, copy, length); Sent.Add(copy); }
        public bool TryReceive(out byte[] data, out int length)
        {
            data = Received.Count > 0 ? Received.Dequeue() : null;
            length = data == null ? 0 : data.Length;
            return data != null;
        }
        public void Dispose() { }
    }
}
