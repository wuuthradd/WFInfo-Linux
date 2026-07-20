using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using WFInfo.Services;

namespace WFInfo.Linux.Services
{
    /// <summary>
    /// Sends desktop notifications via notify-send (freedesktop.org spec).
    /// Works on all Linux desktops regardless of display server or compositor.
    /// </summary>
    public class DesktopNotificationService
    {
        private readonly ILogger _logger;
        private readonly string _iconPath;

        public DesktopNotificationService(ILogger logger)
        {
            _logger = logger;
            _iconPath = ExtractIcon();
        }

        private string ExtractIcon()
        {
            try
            {
                var destDir = Path.Combine(Path.GetTempPath(), $"wfinfo_sounds_{Environment.UserName}");
                Directory.CreateDirectory(destDir);
                var destPath = Path.Combine(destDir, "WFLogo.png");
                if (File.Exists(destPath)) return destPath;

                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("WFInfo.Linux.Resources.WFLogo.png");
                if (stream != null)
                {
                    using var fs = File.Create(destPath);
                    stream.CopyTo(fs);
                    return destPath;
                }
            }
            catch (Exception ex)
            {
                _logger.AddLog($"DesktopNotification: Failed to extract icon: {ex.Message}");
            }
            return "dialog-information";
        }

        public void SendWhisperNotification(string playerName)
        {
            Send("You have a new whisper!", $"From: {playerName}", _iconPath);
        }

        private void Send(string summary, string body, string icon)
        {
            try
            {
                var psi = new ProcessStartInfo("notify-send")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--app-name=WFInfo-Linux");
                psi.ArgumentList.Add($"--icon={icon}");
                psi.ArgumentList.Add("--urgency=normal");
                psi.ArgumentList.Add("--hint=string:suppress-sound:true");
                psi.ArgumentList.Add("--category=im.received");
                psi.ArgumentList.Add(summary);
                psi.ArgumentList.Add(body);

                using var proc = Process.Start(psi);
            }
            catch (Exception ex)
            {
                _logger.AddLog($"DesktopNotification: Failed to send: {ex.Message}");
            }
        }
    }
}