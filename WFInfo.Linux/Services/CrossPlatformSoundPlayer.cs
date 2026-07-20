using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Sound player for Linux. Uses pw-play (PipeWire), paplay (PulseAudio), or aplay as fallback.
    /// </summary>
    public class CrossPlatformSoundPlayer : ISoundPlayer, IDisposable
    {
        private readonly string _rewardSoundPath;
        private readonly string _tempDir;
        private readonly ILogger _logger;
        private readonly Dictionary<string, string> _notificationSounds = new();
        private string _extractedWhisperPath;

        public static readonly string[] AvailableSounds = { "Time Is Now", "Anxious", "Bubbling Up", "Hold On", "Maybe One Day", "Slow Spring Board" };

        private static readonly Dictionary<string, string> SoundResourceNames = new()
        {
            ["Time Is Now"] = "WFInfo.Linux.Resources.Notifications.time-is-now.ogg",
            ["Anxious"] = "WFInfo.Linux.Resources.Notifications.anxious.ogg",
            ["Bubbling Up"] = "WFInfo.Linux.Resources.Notifications.bubbling-up.ogg",
            ["Hold On"] = "WFInfo.Linux.Resources.Notifications.hold-on.ogg",
            ["Maybe One Day"] = "WFInfo.Linux.Resources.Notifications.maybe-one-day.ogg",
            ["Slow Spring Board"] = "WFInfo.Linux.Resources.Notifications.slow-spring-board.ogg",
        };

        public CrossPlatformSoundPlayer(ILogger logger)
        {
            _logger = logger;
            _tempDir = Path.Combine(Path.GetTempPath(), $"wfinfo_sounds_{Environment.UserName}");
            Directory.CreateDirectory(_tempDir);

            _rewardSoundPath = Path.Combine(_tempDir, "reward.wav");
            ExtractResource("WFInfo.Linux.Resources.achievment_03.wav", _rewardSoundPath);
        }

        private void ExtractResource(string resourceName, string destPath)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream == null) return;
                bool needsExtract = !File.Exists(destPath);
                if (!needsExtract)
                {
                    try { needsExtract = new FileInfo(destPath).Length != stream.Length; }
                    catch { needsExtract = true; }
                }
                if (needsExtract)
                {
                    using var fs = File.Create(destPath);
                    stream.CopyTo(fs);
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"SoundPlayer: Failed to extract {resourceName}: {ex.Message}");
            }
        }

        public void Play()
        {
            PlayFile(_rewardSoundPath);
        }

        public void PlayWhisper(string soundName)
        {
            if (string.IsNullOrEmpty(soundName) || !SoundResourceNames.ContainsKey(soundName))
                soundName = "Time Is Now";

            if (_extractedWhisperPath != null && _extractedWhisperPath.Contains(soundName))
            {
                PlayFile(_extractedWhisperPath);
                return;
            }

            var destPath = Path.Combine(_tempDir, $"whisper_{soundName}.ogg");
            ExtractResource(SoundResourceNames[soundName], destPath);
            _extractedWhisperPath = destPath;
            PlayFile(destPath);
        }

        private void PlayFile(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                if (!TryPlay("pw-play", path))
                    if (!TryPlay("paplay", path))
                        if (!TryPlay("aplay", path))
                            _logger.AddLog("SoundPlayer: no audio player found");
            }
            catch (Exception ex)
            {
                _logger.AddLog($"SoundPlayer error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        private bool TryPlay(string command, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(command)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var arg in args)
                    psi.ArgumentList.Add(arg);
                using var proc = Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}