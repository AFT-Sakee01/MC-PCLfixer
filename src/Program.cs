using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

[assembly:AssemblyTitle("PCL (Local Download Repair)")]
[assembly:AssemblyDescription("Independent third-party repair tool; upstream PCL by 龙腾猫跃")]
[assembly:AssemblyVersion("1.0.0.4")]
[assembly:AssemblyFileVersion("1.0.0.4")]

namespace PclRepair
{
    public static class Program
    {
        public const string Version="1.0.0.4";
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]
        static extern int MessageBox(IntPtr window,string text,string caption,uint type);
        public static int Main(string[] args)
        {
            Console.OutputEncoding=new UTF8Encoding(false);
            bool interactive=args.Length==0;
            string action=interactive?"run":args[0];
            if(action=="--version")
            {
                Console.WriteLine("PCL (Local Download Repair) "+Version+" | "+RuntimeInformation.ProcessArchitecture+" | "+RuntimeInformation.OSDescription);
                return 0;
            }
            bool sourceAction=action=="source-status"||action=="source-disable";
            if(action=="--help" || (args.Length<2&&!interactive&&!sourceAction))
            {
                Console.WriteLine("PCL (Local Download Repair) "+Version+"\ninspect <PCL.exe> | apply <PCL.exe> | run <PCL.exe> | restore <PCL.exe>\nenable-auto <PCL.exe> | disable-auto <PCL.exe>\nsource-set <HTTPS-feed-url> <publisher-public.pem> | source-status | source-disable\nNo arguments: repair, enable automatic update repair, and start Plain Craft Launcher 2.exe next to this tool.\nIncompatible updates can fetch signed recipes after a feed is configured once. inspect uses local recipes only.\nWindows automatic repair needs this helper beside PCL. disable-auto preserves the download patch; restore also disables automatic repair.\nUpstream PCL: 龙腾猫跃 https://github.com/Meloong-Git/PCL\nSponsor: https://meloong.com/afd/a/LTCat");
                return 0;
            }
            string target=interactive?Path.Combine(AppContext.BaseDirectory,"Plain Craft Launcher 2.exe"):(sourceAction?null:args[1]);
            using(var journal=new Journal(Path.Combine(AppContext.BaseDirectory,"logs")))
            {
                journal.Write("Info","start","Repair tool started",action);
                try
                {
                    var feeds=new RecipeFeeds(AppContext.BaseDirectory,journal,useBundledSource:true);
                    if(action=="source-set")
                    {
                        if(args.Length!=3||new FileInfo(args[2]).Length>16384) throw new Unsupported("Use source-set <HTTPS-feed-url> <publisher-public.pem>.");
                        feeds.Configure(args[1],File.ReadAllText(args[2]));
                        Console.WriteLine(feeds.Status());return 0;
                    }
                    if(sourceAction)
                    {
                        if(action=="source-disable") feeds.Disable();
                        Console.WriteLine(feeds.Status());return 0;
                    }
                    if((action=="run"||action=="enable-auto"||action=="disable-auto"||action=="resume")
                        &&!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        throw new Unsupported("Automatic startup repair requires Windows .NET Framework. On Ubuntu use inspect/apply/restore.");
                    if(action=="resume")
                    {
                        if(args.Length<6||args[5]!="--") throw new Unsupported("Invalid bootstrap handoff arguments.");
                        AutoRepair.Resume(target,Int32.Parse(args[2]),Int64.Parse(args[3]),args[4],args.Skip(6).ToArray(),bytes=>feeds.Select(bytes),journal);
                        return 0;
                    }
                    if(action=="disable-auto")
                    {
                        AutoRepair.Disable(target);
                        journal.Write("Info","auto_disabled","Automatic startup repair disabled",null);
                        Console.WriteLine("AUTOMATIC REPAIR DISABLED; download patch preserved.");
                        return 0;
                    }
                    if(action=="restore")
                    {
                        if(File.Exists(AutoRepair.StatePath(Path.GetFullPath(target)))||File.Exists(Path.GetFullPath(target)+".config"))
                            AutoRepair.Disable(target);
                        string restored=Repair.Restore(target);
                        Console.WriteLine("RESTORED SHA256="+restored);
                        journal.Write("Info","restore","Original executable restored",restored);
                        return 0;
                    }
                    if(action!="inspect"&&action!="apply"&&action!="run"&&action!="enable-auto") throw new Unsupported("Unknown command. Use --help.");
                    Inspection result;
                    if(action=="inspect")
                    {
                        byte[] bytes=File.ReadAllBytes(target);
                        result=Repair.Inspect(bytes,feeds.Select(bytes,false));
                    }
                    else result=feeds.Apply(target);
                    Console.WriteLine(result.State.ToUpperInvariant()+" PCL="+result.Version+" METHODS="+result.Methods+" FULL_UPDATES="+result.FullUpdates+" SHA256="+result.Sha256);
                    journal.Write("Info",action,"Launcher checked",result.State+" "+result.Version+" "+result.Sha256);
                    if(action=="run"||action=="enable-auto")
                    {
                        AutoRepair.Enable(target,result);
                        journal.Write("Info","auto_enabled","Automatic startup repair enabled",result.Sha256);
                        Console.WriteLine("AUTOMATIC REPAIR ENABLED. Compatible updates will be repaired before PCL opens.");
                        if(action=="run") using(var started=AutoRepair.StartLauncher(target,new string[0])) { }
                    }
                    return 0;
                }
                catch(Exception ex)
                {
                    Console.Error.WriteLine("REFUSED: "+ex.Message);
                    // Logs deliberately store the exception type, not messages containing user paths.
                    journal.Write("Error","refused","Operation failed; inspect console for details",ex.GetType().Name);
                    if(action=="resume"&&RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        MessageBox(IntPtr.Zero,"PCL 更新后的自动修复未完成，因此没有继续启动。\n已配置配方源时，可能暂时无法联网或发布者尚未提供兼容配方；稍后直接打开 PCL 即可重试。\n尚未配置配方源时，需要先绑定一次 HTTPS 发布地址及公钥。\n详情见 logs/error.jsonl。若需以当前状态打开，可用 disable-auto 关闭自动修复。",
                            "PCL (Local Download Repair)",0x10);
                    if(interactive) { Console.Error.WriteLine("Press Enter to close.");Console.ReadLine(); }
                    return 2;
                }
            }
        }
    }
}
