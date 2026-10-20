using System;
using System.IO;
using System.Text;

internal static class Fixture
{
    private static void Main(string[] args)
    {
        Console.WriteLine("FIXTURE_STARTED");
        Console.WriteLine("DOMAIN_MANAGER=" + (AppDomain.CurrentDomain.DomainManager == null
            ? "none" : AppDomain.CurrentDomain.DomainManager.GetType().FullName));
        if (args.Length == 1 && args[0] == "--diagnose-paths")
        {
            DiagnosePaths();
            return;
        }
        // Synthetic test data only; the production bootstrap never writes args.
        foreach (string arg in args)
            Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(arg)));
    }

    private static void DiagnosePaths()
    {
        Console.WriteLine("TARGET_FRAMEWORK=" +
            (AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName ?? "<null>"));
        foreach (string name in new[] { "Switch.System.IO.UseLegacyPathHandling", "Switch.System.IO.BlockLongPaths" })
        {
            bool value;
            bool present = AppContext.TryGetSwitch(name, out value);
            Console.WriteLine(name + "=" + value + ";present=" + present);
        }
        // This fixture writes only its own temporary test directory. It never
        // examines PCL data or any protected original/backup material.
        string testPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic-created-directory");
        try
        {
            Directory.CreateDirectory(@"\\?\" + testPath);
            Console.WriteLine("EXTENDED_DIRECTORY=OK");
        }
        catch (Exception error)
        {
            Console.WriteLine("EXTENDED_DIRECTORY=" + error.GetType().FullName);
        }
    }
}

