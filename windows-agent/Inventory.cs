using System.Diagnostics;
using System.Text.Json;
namespace MooT.Agent;
internal static class Inventory
{
    private static JsonElement? cached;
    private static DateTime updated;
    internal static async Task<JsonElement> Read()
    {
        if(cached.HasValue&&DateTime.UtcNow-updated<TimeSpan.FromMinutes(5))return cached.Value;
        // Fixed, read-only Windows inventory. No model-generated PowerShell is accepted.
        const string script="""
        $ErrorActionPreference='Stop'
        $result=@{
          cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed)
          gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
          board=@(Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer,Product)
          bios=@(Get-CimInstance Win32_BIOS | Select-Object Manufacturer,SMBIOSBIOSVersion)
          memory=@(Get-CimInstance Win32_PhysicalMemory | Select-Object Capacity,Speed,ConfiguredClockSpeed)
          disks=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | Select-Object DeviceID,Size,FreeSpace)
          startup=@(Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue | Select-Object Name | Select-Object -First 40)
          services=@(Get-CimInstance Win32_Service -Filter "State='Running'" -ErrorAction SilentlyContinue | Select-Object Name,StartMode | Select-Object -First 60)
          network=@(Get-CimInstance Win32_NetworkAdapter -Filter 'PhysicalAdapter=True' -ErrorAction SilentlyContinue | Select-Object Name,NetConnectionStatus)
          applications=@(Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue | Where-Object DisplayName | Select-Object -ExpandProperty DisplayName -Unique | Select-Object -First 50)
        }
        $result | ConvertTo-Json -Depth 4 -Compress
        """;
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=System.Text.Encoding.UTF8};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");start.ArgumentList.Add("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "+script);
        try
        {
            using var process=Process.Start(start)??throw new InvalidOperationException();
            var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try{await process.WaitForExitAsync(limit.Token);}catch{try{process.Kill(true);}catch{}throw;}
            var text=await output;await errors;
            if(process.ExitCode!=0||text.Length>28000)throw new InvalidOperationException();
            using var doc=JsonDocument.Parse(text);cached=doc.RootElement.Clone();updated=DateTime.UtcNow;
        }
        catch{cached=JsonSerializer.SerializeToElement(new{unavailable=true});updated=DateTime.UtcNow;}
        return cached.Value;
    }
    internal static Dictionary<int,(string Name,double Cpu)> Sample()
    {
        var sample=new Dictionary<int,(string,double)>();using var self=Process.GetCurrentProcess();
        foreach(var p in Process.GetProcesses())using(p)try{sample[p.Id]=(p.ProcessName,p.TotalProcessorTime.TotalMilliseconds);}catch{}
        return sample;
    }
    internal static object[] Processes(Dictionary<int,(string Name,double Cpu)> before,double elapsed)
    {
        var rows=new List<(string name,double cpu,double ram)>();
        using var self=Process.GetCurrentProcess();
        foreach(var p in Process.GetProcesses())using(p)try
        {
            if(!before.TryGetValue(p.Id,out var previous)||previous.Name!=p.ProcessName)continue;
            rows.Add((p.ProcessName,Math.Round(Math.Clamp((p.TotalProcessorTime.TotalMilliseconds-previous.Cpu)/Math.Max(1,elapsed)/Environment.ProcessorCount*100,0,100),1),Math.Round(p.WorkingSet64/1048576d,1)));
        }catch{}
        return rows.OrderByDescending(p=>p.cpu).ThenByDescending(p=>p.ram).Take(15).Select(p=>(object)new{name=p.name,cpuPercent=p.cpu,memoryMB=p.ram}).ToArray();
    }
}
