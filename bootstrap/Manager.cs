using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("PCL.Repair.Bootstrap.Tests")]

namespace PclRepair.Bootstrap
{
    // Loaded by this launcher's local .exe.config before PCL's entry point.
    // Do not install a machine-wide CLR hook or modify any PCL method here.
    public sealed class Manager : AppDomainManager
    {
        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            bool isDefaultDomain = AppDomain.CurrentDomain.IsDefaultAppDomain();
            // The CLR runs this manager before discovering PCL's entry assembly.
            // Reading files first can freeze AppContext at legacy .NET 4 defaults,
            // making PCL's \\?\ paths fail as apparent folder-permission errors.
            // The verified PCL profile targets net48: establish that compatibility
            // contract before base initialization or any file/process/argument I/O.
            // Leave secondary domains' own framework settings untouched.
            if (isDefaultDomain) appDomainInfo.TargetFrameworkName = ".NETFramework,Version=v4.8";
            base.InitializeNewDomain(appDomainInfo);
            if (!isDefaultDomain) return;

            try
            {
                string[] originalCommandLine = Environment.GetCommandLineArgs();
                // PCL starts short-lived utility invocations with exit-code and
                // parent-wait contracts. Let those complete unmodified; --wait is
                // still a normal launcher restart and must reach the repair path.
                if (BootstrapProtocol.IsUtilityInvocation(originalCommandLine)) return;

                string target;
                int processId;
                long startTicks;
                using (Process current = Process.GetCurrentProcess())
                {
                    target = current.MainModule.FileName;
                    processId = current.Id;
                    startTicks = current.StartTime.ToUniversalTime().Ticks;
                }

                BootstrapState state;
                using (FileStream stream = new FileStream(target + ".pcl-repair.json",
                    FileMode.Open, FileAccess.Read, FileShare.Read))
                    state = BootstrapProtocol.ReadState(stream, Path.GetFileName(target));

                string currentHash = BootstrapProtocol.HashFile(target);
                if (String.Equals(currentHash, state.VerifiedSha256, StringComparison.Ordinal))
                    return;

                string directory = Path.GetDirectoryName(target);
                string helper = Path.Combine(directory, "PCL-Download-Repair.exe");
                if (!File.Exists(helper)) throw new FileNotFoundException();

                // The running EXE cannot be replaced safely. Transfer its identity and
                // original arguments to the helper, then exit before the helper writes.
                // PID plus creation time prevents a recycled PID being mistaken for us.
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = helper,
                    Arguments = BootstrapProtocol.BuildResumeArguments(target, processId,
                        startTicks, currentHash, originalCommandLine),
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process child = Process.Start(start))
                {
                    if (child == null) throw new InvalidOperationException();
                }
                Environment.Exit(0);
            }
            catch (Exception)
            {
                // Never persist command-line arguments or raw exceptions: they can
                // contain user data. A damaged local installation must not lock PCL
                // into a restart loop; show one fixed warning and allow it to start.
                ShowRecoveryMessage();
            }
        }

        private static void ShowRecoveryMessage()
        {
            try
            {
                MessageBoxW(IntPtr.Zero,
                    "PCL 自动修复引导未能完成检查，当前将继续启动 PCL，下载功能可能尚未恢复。\n\n请关闭 PCL 后运行同目录的 PCL-Download-Repair.exe，重新启用自动修复。",
                    "PCL 本地下载修复", 0x00000000U | 0x00000030U);
            }
            catch (Exception) { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
    }

    [DataContract]
    internal sealed class BootstrapState
    {
        [DataMember(Name = "schema_version", IsRequired = true)]
        public int SchemaVersion;
        [DataMember(Name = "timestamp_utc")]
        public string TimestampUtc;
        [DataMember(Name = "tool_version")]
        public string ToolVersion;
        [DataMember(Name = "target_name", IsRequired = true)]
        public string TargetName;
        [DataMember(Name = "verified_sha256", IsRequired = true)]
        public string VerifiedSha256;
    }

    internal static class BootstrapProtocol
    {
        internal static bool IsUtilityInvocation(string[] originalCommandLine)
        {
            if (originalCommandLine.Length < 2) return false;
            string command = originalCommandLine[1];
            return String.Equals(command, "--update", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(command, "--gpu", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(command, "--memory", StringComparison.OrdinalIgnoreCase);
        }

        internal static BootstrapState ReadState(Stream stream, string targetName)
        {
            // The sidecar is tiny. Bound it before deserialization so accidental
            // corruption cannot allocate arbitrary amounts during CLR startup.
            if (!stream.CanSeek || stream.Length <= 0 || stream.Length > 16384)
                throw new InvalidDataException();
            var serializer = new DataContractJsonSerializer(typeof(BootstrapState));
            BootstrapState state = (BootstrapState)serializer.ReadObject(stream);
            if (state == null || state.SchemaVersion != 1 ||
                // This component only runs on Windows; casing differences in the
                // same basename must not invalidate an otherwise identical target.
                !String.Equals(state.TargetName, targetName, StringComparison.OrdinalIgnoreCase) ||
                !IsHash(state.VerifiedSha256))
                throw new InvalidDataException();
            return state;
        }

        internal static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        internal static string HashFile(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] hash = algorithm.ComputeHash(stream);
                var result = new StringBuilder(64);
                foreach (byte item in hash) result.Append(item.ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static string BuildResumeArguments(string target, int processId, long startTicks,
            string currentHash, string[] originalCommandLine)
        {
            var result = new StringBuilder();
            AppendArgument(result, "resume");
            AppendArgument(result, target);
            AppendArgument(result, processId.ToString(CultureInfo.InvariantCulture));
            AppendArgument(result, startTicks.ToString(CultureInfo.InvariantCulture));
            AppendArgument(result, currentHash);
            AppendArgument(result, "--");
            for (int i = 1; i < originalCommandLine.Length; i++)
                AppendArgument(result, originalCommandLine[i]);
            return result.ToString();
        }

        private static void AppendArgument(StringBuilder result, string argument)
        {
            if (result.Length != 0) result.Append(' ');
            result.Append(QuoteArgument(argument));
        }

        internal static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            // Windows CRT parsing consumes backslashes only before a quote or the
            // closing delimiter. Always quote, doubling that run in those places.
            // UseShellExecute=false means shell metacharacters stay ordinary data.
            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                }
                else
                {
                    result.Append('\\', backslashes);
                    result.Append(c);
                }
                backslashes = 0;
            }
            result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }
}
