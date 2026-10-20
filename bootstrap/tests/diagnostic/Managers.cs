using System;
using System.Diagnostics;
using System.IO;

// Diagnostic alternatives only. These types are never compiled into production.
public sealed class DiagnosticFrameworkManager : AppDomainManager
{
    public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
    {
        appDomainInfo.TargetFrameworkName = ".NETFramework,Version=v4.8";
        base.InitializeNewDomain(appDomainInfo);
        TriggerFileAccess();
    }

    internal static void TriggerFileAccess()
    {
        using (Process current = Process.GetCurrentProcess())
        using (FileStream stream = File.OpenRead(current.MainModule.FileName))
            stream.ReadByte();
    }
}

public sealed class DiagnosticSwitchManager : AppDomainManager
{
    public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
    {
        AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
        AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        base.InitializeNewDomain(appDomainInfo);
        DiagnosticFrameworkManager.TriggerFileAccess();
    }
}
