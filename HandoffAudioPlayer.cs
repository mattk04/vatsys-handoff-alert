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
        private readonly string _acceptedWavPath;
        private readonly object _playbackLock = new object();
        private readonly VatSysSoundCache _handoffSoundCache = new VatSysSoundCache();
        private readonly VatSysSoundCache _acceptedSoundCache = new VatSysSoundCache();
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

            string acceptedPath = Path.Combine(pluginDir, "HandoffAccepted.wav");
            if (File.Exists(acceptedPath))
                _acceptedWavPath = acceptedPath;
        }

        public void PlayHandoffAlert()
        {
            QueueAlertPlayback(false);
        }

        public void PlayHandoffAcceptedAlert()
        {
            QueueAlertPlayback(true);
        }

        private void QueueAlertPlayback(bool isAcceptedHandoff)
        {
            if (!ThreadPool.QueueUserWorkItem(_ => PlayAlertSynchronously(isAcceptedHandoff)))
                Errors.Add(new Exception("Handoff Alert could not queue audio playback."), "Handoff Alert");
        }

        private void PlayAlertSynchronously(bool isAcceptedHandoff)
        {
            try
            {
                lock (_playbackLock)
                {
                    bool attemptedVatSysPlayback;
                    if (TryPlayThroughVatSys(isAcceptedHandoff, out attemptedVatSysPlayback) || attemptedVatSysPlayback)
                        return;

                    string wavPath = isAcceptedHandoff ? _acceptedWavPath : _alertWavPath;
                    if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
                    {
                        using (var player = new SoundPlayer(wavPath))
                        {
                            player.PlaySync();
                        }
                    }
                    else
                    {
                        using (MemoryStream alert = CreateBuiltInAlert(48000, 2, isAcceptedHandoff))
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

        private bool TryPlayThroughVatSys(bool isAcceptedHandoff, out bool attemptedPlayback)
        {
            attemptedPlayback = false;
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

            object alertEngine = audioType
                .GetProperty("AlertSoundsEngine", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(audioInstance, null);
            if (!TryGetEngineFormat(alertEngine, out int sampleRate, out short channels))
            {
                ReportVatSysRouteFallback("vatSys alert mixer format is unavailable; using the Windows default audio output.");
                return false;
            }

            try
            {
                VatSysSoundCache cache = isAcceptedHandoff ? _acceptedSoundCache : _handoffSoundCache;
                if (cache.Sound == null || cache.SampleRate != sampleRate || cache.Channels != channels)
                {
                    cache.Sound = CreateVatSysCachedSound(isAcceptedHandoff, sampleRate, channels);
                    cache.SampleRate = sampleRate;
                    cache.Channels = channels;
                }

                attemptedPlayback = true;
                playMethod.Invoke(audioInstance, new[] { cache.Sound, (object)false });
                return true;
            }
            catch (Exception ex)
            {
                ReportVatSysRouteFallback(
                    "vatSys alert audio failed; using the Windows default audio output. " +
                    ex.GetBaseException().Message,
                    ex);
                return false;
            }
        }

        private static bool TryGetEngineFormat(object alertEngine, out int sampleRate, out short channels)
        {
            sampleRate = 0;
            channels = 0;
            if (alertEngine == null)
                return false;

            FieldInfo waveFormatField = alertEngine.GetType().GetField(
                "waveFormat",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object waveFormat = waveFormatField?.GetValue(alertEngine);
            if (waveFormat == null)
                return false;

            PropertyInfo sampleRateProperty = waveFormat.GetType().GetProperty("SampleRate");
            PropertyInfo channelsProperty = waveFormat.GetType().GetProperty("Channels");
            if (sampleRateProperty == null || channelsProperty == null)
                return false;

            sampleRate = (int)sampleRateProperty.GetValue(waveFormat, null);
            channels = Convert.ToInt16(channelsProperty.GetValue(waveFormat, null));
            return sampleRate > 0 && channels > 0;
        }

        private object CreateVatSysCachedSound(bool isAcceptedHandoff, int sampleRate, short channels)
        {
            Type cachedSoundType = typeof(FDP2).Assembly.GetType("vatsys.CachedSound");
            if (cachedSoundType == null)
                throw new InvalidOperationException("vatSys CachedSound type was not found.");

            string wavPath = isAcceptedHandoff ? _acceptedWavPath : _alertWavPath;
            if (!string.IsNullOrEmpty(wavPath) && File.Exists(wavPath))
                return Activator.CreateInstance(cachedSoundType, new object[] { wavPath });

            string temporaryWavPath = Path.Combine(
                Path.GetTempPath(),
                "HandoffAlert-" + Guid.NewGuid().ToString("N") + ".wav");

            try
            {
                using (MemoryStream alert = CreateBuiltInAlert(sampleRate, channels, isAcceptedHandoff))
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

        private static MemoryStream CreateBuiltInAlert(int sampleRate, short channels, bool isAcceptedHandoff)
        {
            const short bitsPerSample = 16;
            int[] frequencies = isAcceptedHandoff
                ? new[] { 1175, 880, 880 }
                : new[] { 880, 880, 1175 };
            const int toneMilliseconds = 110;
            const int gapMilliseconds = 45;
            int toneSamples = sampleRate * toneMilliseconds / 1000;
            int gapSamples = sampleRate * gapMilliseconds / 1000;
            int frameCount = frequencies.Length * toneSamples + (frequencies.Length - 1) * gapSamples;
            int sampleCount = frameCount * channels;

            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
            {
                int dataLength = sampleCount * bitsPerSample / 8;
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
                        short value = (short)(sample * 14000);
                        for (int channel = 0; channel < channels; channel++)
                            writer.Write(value);
                    }

                    if (toneIndex < frequencies.Length - 1)
                    {
                        for (int i = 0; i < gapSamples; i++)
                        {
                            for (int channel = 0; channel < channels; channel++)
                                writer.Write((short)0);
                        }
                    }
                }
            }

            stream.Position = 0;
            return stream;
        }

        private sealed class VatSysSoundCache
        {
            public object Sound;
            public int SampleRate;
            public short Channels;
        }
    }
}