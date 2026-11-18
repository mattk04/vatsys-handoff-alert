using System;
using System.IO;
using System.Media;
using System.Reflection;
using System.Threading;
using vatsys;

namespace HandoffAlertPlugin
{
    public class HandoffAudioPlayer
    {
        private readonly string _alertWavPath;
        private readonly object _playbackLock = new object();
        private object _vatSysCachedSound;
        private bool _reportedVatSysRouteFallback;

        public HandoffAudioPlayer()
        {
            // Look for a custom HandoffAlert.wav in the plugin directory first,
            // otherwise fall back to vatSys's built-in alert/ring WAV files.
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            string vatSysDir = AppDomain.CurrentDomain.BaseDirectory;

            string[] candidatePaths =
            {
                Path.Combine(pluginDir, "HandoffAlert.wav"),
                Path.Combine(vatSysDir, "sounds", "handoff.wav"),
                Path.Combine(vatSysDir, "sounds", "alert.wav"),
                Path.Combine(vatSysDir, "sounds", "ring.wav")
            };

            foreach (string path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    _alertWavPath = path;
                    break;
                }
            }
        }

        public void PlayHandoffAlert()
        {
            if (!ThreadPool.QueueUserWorkItem(_ => PlayAlertSynchronously()))
                Errors.Add(new Exception("Handoff Alert could not queue audio playback."), "Handoff Alert");
        }

        public void PlayTestAlert()
        {
            PlayHandoffAlert();
        }

        private void PlayAlertSynchronously()
        {
            try
            {
                lock (_playbackLock)
                {
                    if (TryPlayThroughVatSys())
                        return;

                    if (!string.IsNullOrEmpty(_alertWavPath) && File.Exists(_alertWavPath))
                    {
                        using (var player = new SoundPlayer(_alertWavPath))
                        {
                            player.PlaySync();
                        }
                    }
                    else
                    {
                        using (MemoryStream alert = CreateBuiltInAlert())
                        using (var player = new SoundPlayer(alert))
                        {
                            player.PlaySync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception("Handoff Alert audio playback failed.", ex), "Handoff Alert");
            }
        }

        private bool TryPlayThroughVatSys()
        {
            Type audioType = typeof(FDP2).Assembly.GetType("vatsys.Audio");
            FieldInfo instanceField = audioType?.GetField(
                "Instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Type cachedSoundType = typeof(FDP2).Assembly.GetType("vatsys.CachedSound");
            MethodInfo playMethod = cachedSoundType == null
                ? null
                : audioType?.GetMethod(
                    "PlayAlertSound",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { cachedSoundType, typeof(bool) },
                    null);
            object audioInstance = instanceField?.GetValue(null);

            if (audioInstance == null || playMethod == null)
            {
                ReportVatSysRouteFallback("vatSys alert audio is not available; using the Windows default audio output.");
                return false;
            }

            try
            {
                if (_vatSysCachedSound == null)
                    _vatSysCachedSound = CreateVatSysCachedSound();

                playMethod.Invoke(audioInstance, new[] { _vatSysCachedSound, (object)false });
                return true;
            }
            catch (Exception ex)
            {
                ReportVatSysRouteFallback("vatSys alert audio failed; using the Windows default audio output.", ex);
                return false;
            }
        }

        private object CreateVatSysCachedSound()
        {
            Type cachedSoundType = typeof(FDP2).Assembly.GetType("vatsys.CachedSound");
            if (cachedSoundType == null)
                throw new InvalidOperationException("vatSys CachedSound type was not found.");

            if (!string.IsNullOrEmpty(_alertWavPath) && File.Exists(_alertWavPath))
                return Activator.CreateInstance(cachedSoundType, new object[] { _alertWavPath });

            string temporaryWavPath = Path.Combine(
                Path.GetTempPath(),
                "HandoffAlert-" + Guid.NewGuid().ToString("N") + ".wav");

            try
            {
                using (MemoryStream alert = CreateBuiltInAlert())
                using (FileStream file = new FileStream(temporaryWavPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    alert.CopyTo(file);

                return Activator.CreateInstance(cachedSoundType, new object[] { temporaryWavPath });
            }
            finally
            {
                if (File.Exists(temporaryWavPath))
                {
                    try
                    {
                        File.Delete(temporaryWavPath);
                    }
                    catch (Exception ex)
                    {
                        Errors.Add(new Exception("Handoff Alert could not remove its temporary sound file.", ex), "Handoff Alert");
                    }
                }
            }
        }

        private void ReportVatSysRouteFallback(string message, Exception innerException = null)
        {
            if (_reportedVatSysRouteFallback)
                return;

            _reportedVatSysRouteFallback = true;
            Errors.Add(
                innerException == null ? new Exception(message) : new Exception(message, innerException),
                "Handoff Alert");
        }

        private static MemoryStream CreateBuiltInAlert()
        {
            const int sampleRate = 22050;
            const short bitsPerSample = 16;
            const short channels = 1;
            int[] frequencies = { 880, 880, 1175 };
            const int toneMilliseconds = 110;
            const int gapMilliseconds = 45;
            int toneSamples = sampleRate * toneMilliseconds / 1000;
            int gapSamples = sampleRate * gapMilliseconds / 1000;
            int sampleCount = frequencies.Length * toneSamples + (frequencies.Length - 1) * gapSamples;

            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
            {
                int dataLength = sampleCount * channels * bitsPerSample / 8;
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + dataLength);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write(channels);
                writer.Write(sampleRate);
                writer.Write(sampleRate * channels * bitsPerSample / 8);
                writer.Write((short)(channels * bitsPerSample / 8));
                writer.Write(bitsPerSample);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(dataLength);

                for (int toneIndex = 0; toneIndex < frequencies.Length; toneIndex++)
                {
                    for (int i = 0; i < toneSamples; i++)
                    {
                        double envelope = Math.Min(1.0, Math.Min(i / 40.0, (toneSamples - i) / 40.0));
                        double sample = Math.Sin(2.0 * Math.PI * frequencies[toneIndex] * i / sampleRate) * envelope;
                        writer.Write((short)(sample * 14000));
                    }

                    if (toneIndex < frequencies.Length - 1)
                    {
                        for (int i = 0; i < gapSamples; i++)
                            writer.Write((short)0);
                    }
                }
            }

            stream.Position = 0;
            return stream;
        }
    }
}