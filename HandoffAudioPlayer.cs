using System;
using System.IO;
using System.Media;
using System.Reflection;
using vatsys;

namespace HandoffAlertPlugin
{
    public class HandoffAudioPlayer
    {
        private readonly string _alertWavPath;

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
            try
            {
                // Attempt to invoke vatSys's internal Audio playback method so the sound
                // uses the exact Alert/VSCS output device & volume configured in vatSys Settings.
                Type audioType = typeof(FDP2).Assembly.GetType("vatsys.Audio");
                if (audioType != null && !string.IsNullOrEmpty(_alertWavPath))
                {
                    MethodInfo playMethod = audioType.GetMethod(
                        "PlayAlert",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null,
                        new[] { typeof(string) },
                        null)
                        ?? audioType.GetMethod(
                        "PlaySound",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null,
                        new[] { typeof(string) },
                        null);

                    if (playMethod != null)
                    {
                        playMethod.Invoke(null, new object[] { _alertWavPath });
                        return;
                    }
                }

                // Fallback: play the resolved WAV file or system alert chime
                if (!string.IsNullOrEmpty(_alertWavPath) && File.Exists(_alertWavPath))
                {
                    using (var player = new SoundPlayer(_alertWavPath))
                    {
                        player.Play();
                    }
                }
                else
                {
                    using (MemoryStream alert = CreateBuiltInAlert())
                    using (var player = new SoundPlayer(alert))
                    {
                        player.Play();
                    }
                }
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"HandoffAlertPlugin audio playback error: {ex.Message}"), "Handoff Alert");
            }
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