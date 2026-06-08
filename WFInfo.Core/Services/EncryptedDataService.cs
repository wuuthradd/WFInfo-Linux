using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace WFInfo.Services
{
    public class EncryptedDataService
    {
        private static readonly IDataProtector JwtProtector;

        static EncryptedDataService()
        {
            try
            {
                var keysDir = Path.Combine(PlatformPaths.AppDataPath, "keys");
                Directory.CreateDirectory(keysDir);
                if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        File.SetUnixFileMode(keysDir,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch { }
                }

                var serviceCollection = new ServiceCollection();
                serviceCollection.AddDataProtection()
                    .SetApplicationName("WFInfo")
                    .PersistKeysToFileSystem(new DirectoryInfo(keysDir));
                var services = serviceCollection.BuildServiceProvider();
                IDataProtectionProvider provider = services.GetService<IDataProtectionProvider>();
                JwtProtector = provider?.CreateProtector("WFInfo.JWT.v1");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DataProtection init failed: {ex.Message}");
                JwtProtector = null;
            }
        }

        public static string LoadStoredJWT()
        {
            try
            {
                var path = Path.Combine(PlatformPaths.AppDataPath, "jwt_encrypted");
                var fileText = File.ReadAllText(path);
                return JwtProtector?.Unprotect(fileText);
            }
            catch (FileNotFoundException e)
            {
                AppMain.AddLog($"{e.Message} JWT not set");
            }
            catch (CryptographicException e)
            {
                AppMain.AddLog($"{e.Message} JWT decryption failed");
            }
            catch (Exception e)
            {
                AppMain.AddLog($"JWT load error: {e.Message}");
            }
            return null;
        }

        public static void PersistJWT(string jwt)
        {
            var encryptedJWT = JwtProtector?.Protect(jwt);
            if (encryptedJWT == null)
            {
                AppMain.AddLog("WARNING: DataProtection unavailable - JWT not persisted");
                return;
            }
            var path = Path.Combine(PlatformPaths.AppDataPath, "jwt_encrypted");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, encryptedJWT);
            if (OperatingSystem.IsLinux())
            {
                try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { }
            }
        }
    }
}