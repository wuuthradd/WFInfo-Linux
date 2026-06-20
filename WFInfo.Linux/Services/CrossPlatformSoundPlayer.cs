using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Sound player for Linux. Uses pw-play (PipeWire), paplay (PulseAudio), or aplay as fallback.
    /// </summary>
    public class CrossPlatformSoundPlayer : ISoundPlayer, IDisposable
    {
        private readonly string _tempWavPath;
        private readonly ILogger _logger;

        public CrossPlatformSoundPlayer(ILogger logger)
        {
            _logger = logger;

            _tempWavPath = Path.Combine(Path.GetTempPath(), $"wfinfo_notification_{Environment.UserName}.wav");
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("WFInfo.Linux.Resources.achievment_03.wav");
                if (stream != null)
                {
                    bool needsExtract = !File.Exists(_tempWavPath);
                    if (!needsExtract)
                    {
                        try { needsExtract = new FileInfo(_tempWavPath).Length != stream.Length; }
                        catch { needsExtract = true; }
                    }
                    if (needsExtract)
                    {
                        using var fs = File.Create(_tempWavPath);
                        stream.CopyTo(fs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"SoundPlayer: Failed to extract WAV: {ex.Message}");
            }
        }

        public void Play()
        {
            if (!File.Exists(_tempWavPath)) return;

            try
            {
                if (!TryPlay("pw-play", _tempWavPath))
                    if (!TryPlay("paplay", _tempWavPath))
                        TryPlay("aplay", _tempWavPath);
            }
            catch (Exception ex)
            {
                _logger.AddLog($"SoundPlayer error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try { File.Delete(_tempWavPath); } catch { }
        }

        private bool TryPlay(string command, string filePath)
        {
            try
            {
                var psi = new ProcessStartInfo(command, $"\"{filePath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
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