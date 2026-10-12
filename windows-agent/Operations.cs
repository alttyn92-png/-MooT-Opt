using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MooT.Agent;

internal record GameState(int Pid, long Started, int Priority);
internal record Snapshot(string Id, string Mode, GameState? Game, Guid? Power, DateTime Created, string[]? Services=null,Dictionary<string,int?>? Settings=null);
internal interface IMachine
{
    int? Setting(string id);
    void Setting(string id,int? value);
    GameState Game();
    void Priority(GameState game, int priority);
    Guid Power();
    void Power(Guid value);
    bool ServiceRunning(string name);
    void Service(string name,bool running);
}
internal sealed class WindowsMachine : IMachine
{
    public int? Setting(string id){var (path,name,value)=Operations.GamingSettings[id];using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);var current=key?.GetValue(name);if(current!=null&&current is not int)throw new InvalidOperationException("Неожиданный тип настройки Windows");return (int?)current;}
    public void Setting(string id,int? value){var (path,name,target)=Operations.GamingSettings[id];using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path);if(value.HasValue)key.SetValue(name,value.Value,Microsoft.Win32.RegistryValueKind.DWord);else key.DeleteValue(name,false);}
    public bool ServiceRunning(string name)=>OptionalServices.Running(name);
    public void Service(string name,bool running)=>OptionalServices.Set(name,running);
    public GameState Game()
    {
        var games=Process.GetProcessesByName("cs2").Concat(Process.GetProcessesByName("csgo")).ToArray();
        try
        {
            if(games.Length!=1) throw new InvalidOperationException("Запустите CS2 или CS:GO. Должен работать один процесс игры.");
            var p=games[0];
            return new(p.Id,p.StartTime.ToUniversalTime().Ticks,(int)p.PriorityClass);
        }
        finally { foreach(var p in games) p.Dispose(); }
    }
    public void Priority(GameState game,int priority)
    {
        using var p=Process.GetProcessById(game.Pid);
        if((p.ProcessName!="cs2" && p.ProcessName!="csgo") || p.StartTime.ToUniversalTime().Ticks!=game.Started)
            throw new ArgumentException("Исходный процесс игры уже завершён.");
        p.PriorityClass=(ProcessPriorityClass)priority;
    }
    public Guid Power()
    {
        if(PowerGetActiveScheme(IntPtr.Zero,out var ptr)!=0) throw new InvalidOperationException("Не удалось прочитать схему питания.");
        try { return Marshal.PtrToStructure<Guid>(ptr); } finally { LocalFree(ptr); }
    }
    public void Power(Guid value)
    {
        if(PowerSetActiveScheme(IntPtr.Zero,ref value)!=0) throw new InvalidOperationException("Windows не разрешила изменить схему питания. Возможно, она недоступна на этом ПК.");
    }
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root,out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root,ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
}

internal sealed class Operations
{
    private readonly IMachine machine;
    private readonly string folder;
    internal static readonly Dictionary<string,(string Path,string Name,int Value)> GamingSettings=new(){
        ["modeAllow"]=(@"Software\Microsoft\GameBar","AllowAutoGameMode",1),["modeAuto"]=(@"Software\Microsoft\GameBar","AutoGameModeEnabled",1),
        ["capture"]=(@"Software\Microsoft\Windows\CurrentVersion\GameDVR","AppCaptureEnabled",0),["dvr"]=(@"System\GameConfigStore","GameDVR_Enabled",0)};
    internal static readonly Guid Performance=new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public Operations(IMachine machine,string folder) { this.machine=machine;this.folder=folder; }
    public Dictionary<string,object> Apply(string mode,string[]? services=null)
    {
        if(mode is not ("basic" or "maximum" or "power")) throw new InvalidOperationException("Неизвестный режим.");
        Directory.CreateDirectory(folder);
        var active=Path.Combine(folder,"active.json");
        if(File.Exists(active)) throw new InvalidOperationException("Сначала выполните откат предыдущей настройки.");
        services ??= Array.Empty<string>();
        if(services.Any(name=>!OptionalServices.Allowed.Contains(name))||services.Length>2||mode!="maximum"&&services.Length>0)throw new InvalidOperationException("Некорректный план служб.");
        var running=services.Distinct().Where(machine.ServiceRunning).ToArray();
        var snapshot=new Snapshot(Guid.NewGuid().ToString("N"),mode,mode=="power"?null:machine.Game(),mode!="basic"?machine.Power():null,DateTime.UtcNow,running,mode=="maximum"?GamingSettings.Keys.ToDictionary(k=>k,k=>machine.Setting(k)):null);
        var data=JsonSerializer.Serialize(snapshot);
        File.WriteAllText(Path.Combine(folder,snapshot.Id+".json"),data);
        File.WriteAllText(active,data); // Save before the first change.
        try
        {
            if(snapshot.Game is GameState game)machine.Priority(game,(int)ProcessPriorityClass.High);
            if(mode!="basic") machine.Power(Performance);
            foreach(var name in running)machine.Service(name,false);
            if(mode=="maximum")foreach(var (id,value) in GamingSettings)machine.Setting(id,value.Value);
            return new(){{"status","complete"},{"snapshotId",snapshot.Id},{"summary",mode=="power"?"Включена схема «Высокая производительность». Исходная схема сохранена.":mode=="maximum"?"Применено: высокий приоритет, схема питания, игровой режим Windows; запись в фоне отключена. Перезапустите игру. Службы: "+(running.Length==0?"нет":string.Join(", ",running))+". FPS не измерялся.":"Применено: высокий приоритет игры. FPS не измерялся."}};
        }
        catch
        {
            try { Restore(snapshot.Id); }
            catch { throw new InvalidOperationException("Применение прервано; откат не завершён. Используйте «Вернуть настройки» в приложении."); }
            throw new InvalidOperationException("Windows отклонила изменение. Исходные настройки восстановлены.");
        }
    }
    public Dictionary<string,object> Restore(string? id=null)
    {
        var active=Path.Combine(folder,"active.json");
        if(!File.Exists(active))throw new InvalidOperationException("Нет активных изменений для отката.");
        var s=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(active)) ?? throw new InvalidOperationException("Не удалось прочитать сохранённые настройки.");
        if(id!=null && s.Id!=id)throw new InvalidOperationException("Запрошен другой снимок настроек.");
        if(s.Mode is not ("basic" or "maximum" or "power"))throw new InvalidOperationException("Некорректный снимок.");
        if(s.Settings!=null){if(s.Settings.Keys.Any(k=>!GamingSettings.ContainsKey(k)))throw new InvalidOperationException("Некорректный снимок настроек.");foreach(var (settingId,value) in s.Settings)machine.Setting(settingId,value);}
        if(s.Power is Guid power) machine.Power(power);
        foreach(var name in s.Services??Array.Empty<string>())machine.Service(name,true);
        try { if(s.Game is GameState game)machine.Priority(game,game.Priority); }
        catch(ArgumentException) { /* Process exited; its priority no longer exists. */ }
        File.Delete(active);
        return new(){{"status","complete"},{"snapshotId",s.Id},{"summary","Исходные настройки восстановлены."}};
    }
    public bool HasActive => File.Exists(Path.Combine(folder,"active.json"));
    private sealed class Fake : IMachine
    {
        public Dictionary<string,int?> Settings=GamingSettings.Keys.ToDictionary(k=>k,k=>(int?)null);
        public int? Setting(string id)=>Settings[id];
        public void Setting(string id,int? value)=>Settings[id]=value;
        public int CurrentPriority=(int)ProcessPriorityClass.Normal;
        public Guid CurrentPower=Guid.NewGuid(); public bool RejectPerformance;
        public HashSet<string> RunningServices=new(){"WSearch","SysMain"};
        public bool ServiceRunning(string name)=>RunningServices.Contains(name);
        public void Service(string name,bool running){if(running)RunningServices.Add(name);else RunningServices.Remove(name);}
        public GameState Game()=>new(42,123,CurrentPriority);
        public void Priority(GameState game,int priority)=>CurrentPriority=priority;
        public Guid Power()=>CurrentPower;
        public void Power(Guid value) { if(RejectPerformance && value==Performance)throw new InvalidOperationException();CurrentPower=value; }
    }
    public static string SelfTest()
    {
        var folder=Path.Combine(Path.GetTempPath(),"MooT-test-"+Guid.NewGuid().ToString("N"));
        var fake=new Fake();var initial=fake.CurrentPower;var o=new Operations(fake,folder);int checks=0;
        void Check(bool v){if(!v)throw new Exception("Self-test failed");checks++;}
        try
        {
            var result=o.Apply("basic");Check(fake.CurrentPriority==(int)ProcessPriorityClass.High);Check(fake.CurrentPower==initial);Check(o.HasActive);
            o.Restore((string)result["snapshotId"]);Check(fake.CurrentPriority==(int)ProcessPriorityClass.Normal);Check(!o.HasActive);
            result=o.Apply("maximum");Check(fake.CurrentPower==Performance);Check(fake.Settings["modeAllow"]==1);Check(fake.Settings["capture"]==0);o.Restore((string)result["snapshotId"]);Check(fake.CurrentPower==initial);Check(fake.Settings.Values.All(v=>v==null));
            result=o.Apply("maximum",new[]{"WSearch"});Check(!fake.RunningServices.Contains("WSearch"));o.Restore((string)result["snapshotId"]);Check(fake.RunningServices.Contains("WSearch"));
            result=o.Apply("power");Check(fake.CurrentPower==Performance);o.Restore((string)result["snapshotId"]);Check(fake.CurrentPower==initial);
            try{o.Apply("maximum",new[]{"WinDefend"});throw new Exception("Expected rejection");}catch(InvalidOperationException){checks++;}
            fake.RejectPerformance=true;try{o.Apply("maximum");throw new Exception("Expected rejection");}catch(InvalidOperationException){}
            Check(fake.CurrentPriority==(int)ProcessPriorityClass.Normal);Check(!o.HasActive);
            try{o.Apply("arbitrary-command");throw new Exception("Expected rejection");}catch(InvalidOperationException){checks++;}
            return JsonSerializer.Serialize(new{ok=true,checks,systemChanges=false});
        }
        finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
    }
}

internal static class Telemetry
{
    [StructLayout(LayoutKind.Sequential)] private struct Memory
    {
        public uint Length,Load;public ulong Total,Available,Page,FreePage,Virtual,FreeVirtual,Extended;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetSystemTimes(out ulong idle,out ulong kernel,out ulong user);
    private static readonly SemaphoreSlim gate=new(1,1);
    private static DateTime detailedAt;
    private static object[] cachedProcesses=Array.Empty<object>();
    private static object? cachedGpu;
    internal static async Task<object> Read()
    {
        await gate.WaitAsync();try{
        var inventory=await Inventory.Read();var detailed=DateTime.UtcNow-detailedAt>TimeSpan.FromSeconds(30);
        var processesBefore=detailed?Inventory.Sample():null;var timer=Stopwatch.StartNew();
        if(!GetSystemTimes(out var i,out var k,out var u))throw new InvalidOperationException("CPU недоступен");
        await Task.Delay(500);
        if(!GetSystemTimes(out var i2,out var k2,out var u2))throw new InvalidOperationException("CPU недоступен");
        var memory=new Memory{Length=(uint)Marshal.SizeOf<Memory>()};
        if(!GlobalMemoryStatusEx(ref memory))throw new InvalidOperationException("RAM недоступна");
        var delta=(double)((k2-k)+(u2-u));
        if(detailed){cachedProcesses=Inventory.Processes(processesBefore!,timer.Elapsed.TotalMilliseconds);cachedGpu=GpuOverclock.Capability();detailedAt=DateTime.UtcNow;}
        var processes=cachedProcesses;
        var elevated=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return new {inventory,processes,elevated,gpuControl=cachedGpu,cpuPercent=Math.Round(Math.Clamp(100*(1-(i2-i)/Math.Max(1,delta)),0,100),1),memoryPercent=memory.Load,memoryGB=Math.Round(memory.Total/1073741824d,1),cpuName=Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")??"CPU",os=Environment.OSVersion.VersionString,cores=Environment.ProcessorCount};
        }finally{gate.Release();}
    }
}
