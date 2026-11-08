using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CupheadOnline.Net;
using HarmonyLib;
using UnityEngine;

namespace CupheadOnline.Sync
{
    /// <summary>Restore current BGM after joining/loading, then repair stopped sources without restarting healthy tracks.</summary>
    public static class MusicSync
    {
        private static readonly FieldInfo bgm = AccessTools.Field(typeof(AudioManagerComponent), "bgmSources");
        private static readonly FieldInfo alternates = AccessTools.Field(typeof(AudioManagerComponent), "bgmAlternates");
        private static readonly FieldInfo playlist = AccessTools.Field(typeof(AudioManagerComponent), "bgmPlaylist");
        private static readonly FieldInfo groupSources = AccessTools.Field(typeof(AudioManagerComponent.SoundGroup), "sources");
        private static readonly FieldInfo audio = AccessTools.Field(typeof(AudioManagerComponent.SoundGroup.Source), "audio");

        private struct State
        {
            public string Clip;
            public bool Playing, Loop, IgnorePause;
            public float Time, Volume, Pitch;
        }

        private static readonly Dictionary<string, State> pending = new Dictionary<string, State>();
        private static ushort pendingSerial;
        private static bool complete, paused, resetMixer;
        private static float receivedAt, deadline;
        private static readonly List<string> applied = new List<string>();
        private static ushort readySerial;
        private static float nextSend;
        private static bool bootstrap;

        public static void ResetHost() { readySerial = 0; bootstrap = false; }

        public static void SceneReady(ushort serial)
        {
            readySerial = serial;
            bootstrap = true;
            nextSend = Time.realtimeSinceStartup + 0.1f;
        }

        public static void TickHost(Connection connection, ushort serial)
        {
            if (readySerial == 0 || readySerial != serial || SceneLoader.CurrentlyLoading || Time.realtimeSinceStartup < nextSend) return;
            Send(connection, serial, bootstrap);
            bootstrap = false;
            nextSend = Time.realtimeSinceStartup + 1f;
        }

        // Use hierarchy names and same-name sibling ordinals: unrelated runtime player spawns must not change keys.
        private static string Path(Transform t)
        {
            string part = t.name;
            if (t.parent == null) return part;
            int ordinal = 0;
            for (int i = 0; i < t.GetSiblingIndex(); i++) if (t.parent.GetChild(i).name == t.name) ordinal++;
            return Path(t.parent) + "/" + part + "[" + ordinal + "]";
        }

        private static Dictionary<string, AudioSource> Sources()
        {
            var result = new Dictionary<string, AudioSource>();
            foreach (AudioManagerComponent component in UnityEngine.Object.FindObjectsOfType<AudioManagerComponent>())
            {
                string path = Path(component.transform);
                var siblings = component.GetComponents<AudioManagerComponent>();
                int index = Array.IndexOf(siblings, component);
                string prefix = path + ":" + index;
                Add(result, prefix + "/bgm", (IList)bgm.GetValue(component));
                Add(result, prefix + "/alternate", (IList)alternates.GetValue(component));
                IList groups = (IList)playlist.GetValue(component);
                for (int i = 0; groups != null && i < groups.Count; i++)
                    Add(result, prefix + "/playlist/" + i, (IList)groupSources.GetValue(groups[i]));
            }
            return result;
        }

        private static void Add(Dictionary<string, AudioSource> result, string prefix, IList sources)
        {
            for (int i = 0; sources != null && i < sources.Count; i++)
            {
                if (sources[i] == null) continue;
                AudioSource source = (AudioSource)audio.GetValue(sources[i]);
                if (source != null) result[prefix + "/" + i] = source;
            }
        }

        public static void Send(Connection connection, ushort serial, bool bootstrapMixer = true)
        {
            NetWriter w = Header(serial, true, bootstrapMixer);
            foreach (var pair in Sources())
            {
                AudioSource s = pair.Value;
                NetWriter entry = new NetWriter(256);
                entry.Str(pair.Key);
                entry.Str(s.clip != null ? s.clip.name : "");
                entry.Bool(s.isPlaying); entry.Bool(s.loop); entry.Bool(s.ignoreListenerPause);
                entry.F32(s.time); entry.F32(s.volume); entry.F32(s.pitch);
                if (entry.Length + 4 > Connection.MaxReliablePayload)
                {
                    Plugin.Log.LogWarning("Music source key too long: " + pair.Key);
                    continue;
                }
                if (w.Length + entry.Length > Connection.MaxReliablePayload)
                {
                    connection.SendReliable(MsgType.MusicState, w);
                    w = Header(serial, false, bootstrapMixer);
                }
                w.Bytes(entry.Buffer, 0, entry.Length);
            }
            w.Buffer[2] |= 2; // final chunk: apply only once the complete bootstrap arrived
            connection.SendReliable(MsgType.MusicState, w);
        }

        private static NetWriter Header(ushort serial, bool first, bool bootstrapMixer)
        {
            NetWriter w = new NetWriter(1024);
            w.U16(serial); w.U8((byte)((first ? 1 : 0) | (bootstrapMixer ? 4 : 0))); w.Bool(AudioListener.pause);
            return w;
        }

        public static void Read(NetReader r)
        {
            ushort serial = r.U16();
            byte flags = r.U8();
            bool listenerPaused = r.Bool();
            if ((flags & 1) != 0)
            {
                // A slow-loading client can receive several snapshots before it can apply the first one.
                // Preserve the initial mixer reset across later periodic snapshots of the same scene.
                bool needsReset = (resetMixer && pendingSerial == serial) || (flags & 4) != 0;
                Clear();
                pendingSerial = serial;
                receivedAt = Time.realtimeSinceStartup;
                deadline = receivedAt + 5f;
                paused = listenerPaused;
                resetMixer = needsReset;
            }
            if (serial != pendingSerial) return;
            while (r.Remaining > 0)
            {
                string key = r.Str();
                pending[key] = new State { Clip = r.Str(), Playing = r.Bool(), Loop = r.Bool(), IgnorePause = r.Bool(),
                    Time = r.F32(), Volume = r.F32(), Pitch = r.F32() };
            }
            complete = (flags & 2) != 0;
        }

        public static void Update()
        {
            if (!complete || ClientMirror.Loading || SceneLoader.CurrentlyLoading) return;
            if (pendingSerial != ClientMirror.LoadedSerial) { Clear(); return; }
            if (resetMixer)
            {
                // The local loader's mixer reset is blocked by AudioSync while mirroring. Leave Loadscreen now.
                AudioSync.Replaying = true;
                try { AudioManager.SnapshotReset(ClientMirror.LoadedScene, 0f); }
                finally { AudioSync.Replaying = false; }
                resetMixer = false;
            }
            AudioListener.pause = paused;
            Dictionary<string, AudioSource> sources = Sources();
            applied.Clear();
            foreach (var pair in pending)
            {
                AudioSource s;
                State state = pair.Value;
                if (!sources.TryGetValue(pair.Key, out s)) continue;
                // DeferredAudioSource can still be waiting on its asset bundle after sceneLoaded.
                if (state.Playing && (s.clip == null || s.clip.name != state.Clip))
                {
                    // A retry may initialize DeferredAudioSource before its new scene's music is cached.
                    DeferredAudioSource deferred = s.GetComponent<DeferredAudioSource>();
                    if (deferred != null)
                    {
                        try { deferred.Initialize(); }
                        catch (Exception) { } // wait for the loader/cache; the next state will retry too
                    }
                    if (s.clip == null || s.clip.name != state.Clip) continue;
                }
                s.loop = state.Loop; s.volume = state.Volume; s.pitch = state.Pitch;
                s.ignoreListenerPause = state.IgnorePause;
                if (!state.Playing)
                {
                    if (s.isPlaying) s.Stop();
                    applied.Add(pair.Key);
                    continue;
                }
                if (state.Playing && s.clip != null && s.clip.length > 0f)
                {
                    float elapsed = Time.realtimeSinceStartup - receivedAt;
                    if (Session.Connection != null) elapsed += (float)Session.Connection.RttSeconds * 0.5f;
                    float position = state.Time + (paused && !state.IgnorePause ? 0f : elapsed * state.Pitch);
                    if (state.Loop || position < s.clip.length)
                    {
                        // Recovery snapshots must not restart healthy tracks (or their mixer fades) every second.
                        if (!s.isPlaying)
                        {
                            s.Play();
                            try { s.time = Mathf.Clamp(state.Loop ? Mathf.Repeat(position, s.clip.length) : position, 0f, s.clip.length - 0.001f); }
                            catch (Exception ex) { Plugin.Log.LogDebug("Music seek failed: " + ex.Message); }
                        }
                    }
                }
                applied.Add(pair.Key);
            }
            foreach (string key in applied) pending.Remove(key);
            if (pending.Count == 0) Clear();
            else if (Time.realtimeSinceStartup > deadline)
            {
                Plugin.Log.LogWarning("Music bootstrap: " + pending.Count + " source(s) unavailable on client.");
                Clear();
            }
        }

        public static void Clear()
        {
            pending.Clear(); complete = false; resetMixer = false;
        }

        public static string Summary()
        {
            var sources = Sources();
            int playing = 0;
            string sample = "";
            foreach (var pair in sources)
            {
                AudioSource source = pair.Value;
                if (!source.isPlaying) continue;
                playing++;
                if (sample.Length == 0) sample = " clip=" + (source.clip != null ? source.clip.name : "null") +
                    " volume=" + source.volume.ToString("0.00") + " pitch=" + source.pitch.ToString("0.00") + " ignorePause=" + source.ignoreListenerPause;
            }
            return "music=" + playing + "/" + sources.Count + " listenerPaused=" + AudioListener.pause + sample;
        }

        public static void StopLocalSourcesForTest()
        {
            foreach (AudioSource source in Sources().Values) source.Stop();
        }
    }
}
