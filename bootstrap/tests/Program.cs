using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using PclRepair.Bootstrap;

internal static class Program
{
    private static int checks;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--echo")
        {
            for (int i = 1; i < args.Length; i++)
                Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i])));
            return 0;
        }
        try
        {
            string[] values = {
                "", "plain", "with spaces", "a\tb", "中文路径", "\"", "\\", "\\\\",
                "ends in slash\\", "ends in two\\\\", "a\\\"b", "a\\\\\"b",
                " & | < > %PATH% ! $() ` ; ", "line\r\nbreak", "literal -- marker"
            };
            string exe;
            using (Process current = Process.GetCurrentProcess()) exe = current.MainModule.FileName;
            var encoded = new StringBuilder("--echo");
            foreach (string value in values) encoded.Append(" ").Append(BootstrapProtocol.QuoteArgument(value));
            using (Process process = Process.Start(new ProcessStartInfo
            {
                FileName = exe, Arguments = encoded.ToString(), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true
            }))
            {
                for (int i = 0; i < values.Length; i++)
                    Check(process.StandardOutput.ReadLine() ==
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(values[i])), "CRT round trip " + i);
                Check(process.StandardOutput.ReadLine() == null, "CRT no extra arguments");
                Check(process.WaitForExit(10000) && process.ExitCode == 0, "CRT child exited");
            }

            string hash = new string('a', 64);
            string valid = "{\"schema_version\":1,\"target_name\":\"target.exe\",\"verified_sha256\":\"" + hash + "\"}";
            Check(Read(valid).VerifiedSha256 == hash, "minimal valid state");
            Check(Read(valid.Replace("target.exe", "TARGET.EXE")).VerifiedSha256 == hash,
                "Windows target basename casing");
            Check(Read(valid.TrimEnd('}') + ",\"future_field\":true}").SchemaVersion == 1, "unknown field tolerated");
            Reject(valid.Replace("target.exe", "other.exe"), "wrong target");
            Reject(valid.Replace("target.exe", "folder/target.exe"), "path is not basename");
            Reject(valid.Replace("\"schema_version\":1", "\"schema_version\":2"), "unknown schema");
            Reject(valid.Replace(hash, hash.ToUpperInvariant()), "uppercase hash");
            Reject(valid.Replace(hash, new string('a', 63)), "short hash");
            Reject(valid.Replace(hash, new string('g', 64)), "nonhex hash");
            Reject("{}", "missing fields");
            Reject("{", "invalid JSON");
            Reject("", "empty state");
            Reject(new string(' ', 16385), "oversized state");
            Check(!BootstrapProtocol.IsHash(null), "null hash");
            Check(BootstrapProtocol.IsUtilityInvocation(new[] { "pcl.exe", "--update", "args" }),
                "updater invocation bypass");
            Check(BootstrapProtocol.IsUtilityInvocation(new[] { "pcl.exe", "--GPU" }),
                "GPU invocation bypass case insensitive");
            Check(BootstrapProtocol.IsUtilityInvocation(new[] { "pcl.exe", "--memory" }),
                "memory invocation bypass");
            Check(!BootstrapProtocol.IsUtilityInvocation(new[] { "pcl.exe", "--wait" }),
                "wait restart must repair");
            Check(!BootstrapProtocol.IsUtilityInvocation(new[] { "pcl.exe" }),
                "normal startup must repair");
            Check(!BootstrapProtocol.IsUtilityInvocation(new[] { "pcl.exe", "other", "--update" }),
                "only first argument controls utility invocation");

            string protocol = BootstrapProtocol.BuildResumeArguments("C:\\test directory\\PCL.exe", 123,
                123456789, hash, new[] { "original.exe", "test arg", "", "x\\" });
            string expected = "\"resume\" \"C:\\test directory\\PCL.exe\" \"123\" \"123456789\" \"" +
                hash + "\" \"--\" \"test arg\" \"\" \"x\\\\\"";
            Check(protocol == expected, "resume envelope");
            if (args.Length == 1)
                VerifyFrameworkStartup(Path.GetFullPath(args[0]));
            Console.WriteLine("PASS " + checks + " bootstrap component checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
    }

    private static void VerifyFrameworkStartup(string fixture)
    {
        // The regression must use the real CLR startup hook. Calling the manager
        // as an ordinary method cannot reproduce early AppContext initialization.
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "framework-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "BootstrapFixture.exe");
        File.Copy(fixture, target);
        File.Copy(typeof(Manager).Assembly.Location, Path.Combine(directory, "PCL.Repair.Bootstrap.dll"));
        File.WriteAllText(target + ".config", "<configuration><runtime>" +
            "<appDomainManagerAssembly value=\"PCL.Repair.Bootstrap\" />" +
            "<appDomainManagerType value=\"PclRepair.Bootstrap.Manager\" />" +
            "</runtime></configuration>");
        File.WriteAllText(target + ".pcl-repair.json",
            "{\"schema_version\":1,\"target_name\":\"BootstrapFixture.exe\",\"verified_sha256\":\"" +
            BootstrapProtocol.HashFile(target) + "\"}");
        using (Process child = Process.Start(new ProcessStartInfo
        {
            FileName = target, Arguments = "--diagnose-paths", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }))
        {
            string output = child.StandardOutput.ReadToEnd();
            string errors = child.StandardError.ReadToEnd();
            Check(child.WaitForExit(10000) && child.ExitCode == 0 && errors.Length == 0,
                "framework regression process exited");
            Check(output.Contains("DOMAIN_MANAGER=PclRepair.Bootstrap.Manager"), "real manager loaded");
            Check(output.Contains("TARGET_FRAMEWORK=.NETFramework,Version=v4.8"),
                "net48 target framework restored");
            Check(output.Contains("Switch.System.IO.UseLegacyPathHandling=False"),
                "modern path handling preserved");
            Check(output.Contains("Switch.System.IO.BlockLongPaths=False"), "long paths preserved");
            Check(output.Contains("EXTENDED_DIRECTORY=OK"), "extended path directory creation");
        }
    }

    private static BootstrapState Read(string json)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            return BootstrapProtocol.ReadState(stream, "target.exe");
    }

    private static void Reject(string json, string name)
    {
        try { Read(json); }
        catch (Exception) { Check(true, name); return; }
        Check(false, name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }
}
