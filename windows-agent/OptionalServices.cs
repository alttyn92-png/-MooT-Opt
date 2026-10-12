using System.Runtime.InteropServices;
namespace MooT.Agent;
internal static class OptionalServices
{
    internal static readonly string[] Allowed={"WSearch","SysMain"};
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type,State,Controls,Exit,ServiceExit,Check,Wait; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenServiceW(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool QueryServiceStatus(IntPtr service,out Status status);
    [DllImport("advapi32.dll",SetLastError=true)] private static extern bool ControlService(IntPtr service,uint control,out Status status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool StartServiceW(IntPtr service,uint count,IntPtr args);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
    private static T Use<T>(string name,uint access,Func<IntPtr,T> action)
    {
        if(!Allowed.Contains(name))throw new InvalidOperationException("Эта служба не входит в план.");
        var manager=OpenSCManagerW(null,null,1);if(manager==IntPtr.Zero)throw new InvalidOperationException("Диспетчер служб недоступен.");
        try { var service=OpenServiceW(manager,name,access);if(service==IntPtr.Zero)throw new InvalidOperationException("Нет доступа к службе "+name+". Для изменения служб запустите MooT от имени администратора.");try{return action(service);}finally{CloseServiceHandle(service);} }
        finally {CloseServiceHandle(manager);}
    }
    internal static bool Running(string name)=>Use(name,4,h=>{if(!QueryServiceStatus(h,out var s))throw new InvalidOperationException("Не удалось прочитать состояние "+name);return s.State==4;});
    internal static void Set(string name,bool running)=>Use(name,4u|(running?16u:32u),h=>
    {
        if(!QueryServiceStatus(h,out var s))throw new InvalidOperationException("Не удалось прочитать состояние службы.");
        uint target=running?4u:1u;if(s.State==target)return true;
        bool accepted=running?StartServiceW(h,0,IntPtr.Zero):ControlService(h,1,out s);
        if(!accepted)throw new InvalidOperationException("Windows отклонила изменение службы "+name+". Код "+Marshal.GetLastWin32Error());
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(DateTime.UtcNow<deadline){if(QueryServiceStatus(h,out s)&&s.State==target)return true;Thread.Sleep(200);}
        throw new InvalidOperationException("Служба "+name+" не завершила переход. Выполните откат.");
    });
}
