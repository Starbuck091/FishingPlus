using MelonLoader;
using MelonLoader.Utils;
using System;
using System.IO;

namespace FishingPlus
{
    internal static class FishingPlusLogger
    {
        private static readonly object _lock = new();
        private static StreamWriter _writer;
        private static string _logPath;

        // true — дублировать в MelonLoader (консоль + Latest.log), удобно при отладке.
        // false — писать только в UserData, чтобы не засорять основной лог.
        public static bool MirrorToMelonLoader { get; set; } = true;

        static FishingPlusLogger()
        {
            try
            {
                // UserData/FishingPlus/FishingPlus.log
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "FishingPlus");
                Directory.CreateDirectory(dir);
                _logPath = Path.Combine(dir, "FishingPlus.log");

                // Ротация: если > 5 МБ — переименовываем в .old
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 5 * 1024 * 1024)
                {
                    string old = _logPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_logPath, old);
                }

                _writer = new StreamWriter(_logPath, append: true) { AutoFlush = true };

                // Заголовок новой сессии для навигации по файлу
                _writer.WriteLine();
                _writer.WriteLine("===== FishingPlus session " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[FishingPlus] Не удалось открыть лог-файл: " + e.Message);
            }
        }

        public static void Msg(string message) => Write("INFO", message);
        public static void Warning(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

            lock (_lock)
            {
                try { _writer?.WriteLine(line); } catch { }
            }

            if (MirrorToMelonLoader)
            {
                try
                {
                    if (level == "WARN") MelonLogger.Warning(message);
                    else if (level == "ERROR") MelonLogger.Error(message);
                    else MelonLogger.Msg(message);
                }
                catch { }
            }
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try { _writer?.Flush(); _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}