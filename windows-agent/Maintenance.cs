using System.Diagnostics;
using System.Runtime.InteropServices;
namespace MooT.Agent;
internal static class Maintenance
{
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool K32EmptyWorkingSet(IntPtr process);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern int SHEmptyRecycleBinW(IntPtr window,string? path,uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    internal static object CleanMemory()
    {
        GetWindowThreadProcessId(GetForegroundWindow(),out var foreground);
        using var self=Process.GetCurrentProcess();
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"chrome","msedge","firefox","Discord","Spotify","Telegram"};
        string? foregroundName=null;
        try{using var fg=Process.GetProcessById((int)foreground);foregroundName=fg.ProcessName;}catch{}
        // Without a reliable foreground identity, do not trim an application the user may be using.
        if(foregroundName==null)return new{status="failed",snapshotId="",summary="Не удалось определить активное приложение. Очистка не запускалась; повторите запрос после переключения в нужное окно."};
        int count=0,skipped=0,denied=0;long released=0;
        foreach(var p in Process.GetProcesses())
        {
            using(p)try
            {
                if(!names.Contains(p.ProcessName)||p.Id==foreground||p.SessionId!=self.SessionId)continue;
                // Skip the whole foreground application, including browser child processes.
                if(string.Equals(foregroundName,p.ProcessName,StringComparison.OrdinalIgnoreCase))continue;
                long before=p.WorkingSet64;
                if(!K32EmptyWorkingSet(p.Handle)){if(Marshal.GetLastWin32Error()==5)denied++;skipped++;continue;}
                p.Refresh();released+=Math.Max(0,before-p.WorkingSet64);count++;
            }catch(System.ComponentModel.Win32Exception e){if(e.NativeErrorCode==5)denied++;skipped++;}catch{skipped++;}
        }
        using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
        bool elevated=new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return MemoryResult(count,skipped,denied,released,elevated);
    }
    internal sealed record MemoryCleanupResult(string status,string snapshotId,string summary);
    internal static MemoryCleanupResult MemoryResult(int count,int skipped,int denied,long released,bool elevated)
    {
        if(count==0&&skipped==0)return new("complete","","Проверка завершена: подходящих фоновых браузеров и мессенджеров нет. Освобождено 0 МБ. Активное приложение не затрагивалось; повторно выдавать права администратора не требуется.");
        var summary=$"Обработано {count} фоновых процессов; освобождено примерно {released/1048576} МБ рабочих наборов. Пропущено {skipped}. Программы не закрывались; память может быть занята повторно.";
        if(denied>0)summary+=elevated?" Права администратора уже есть; Windows ограничила доступ к отдельным процессам.":" Windows отказала в доступе к отдельным процессам. Для них можно перезапустить MooT от администратора.";
        else if(count==0)summary+=" Процессы завершились или изменились во время проверки. Повторите запрос.";
        return new(count==0?"failed":"complete","",summary);
    }
    internal static string CloseBackgroundWindows(string[] names)
    {
        using var self=Process.GetCurrentProcess();int requested=0;
        foreach(var name in names.Where(n=>n is "Discord" or "Spotify"))foreach(var p in Process.GetProcessesByName(name))using(p)try{if(p.SessionId==self.SessionId&&p.MainWindowHandle!=IntPtr.Zero&&p.CloseMainWindow())requested++;}catch{}
        return $"Отправлено запросов закрытия окон Discord/Spotify: {requested}. Принудительное завершение не выполнялось.";
    }
    private static string[] TemporaryRoots()=>new[]{Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Temp"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Temp")}.Select(p=>Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal static long TemporaryBytes(List<string> files)
    {
        long bytes=0;foreach(var file in files)try{bytes+=new FileInfo(file).Length;}catch{}return bytes;
    }
    internal static List<string> TemporaryCandidates()
    {
        var pending=new Stack<string>();foreach(var candidate in TemporaryRoots())try{if(Directory.Exists(candidate)&&(File.GetAttributes(candidate)&FileAttributes.ReparsePoint)==0)pending.Push(candidate);}catch{}var files=new List<string>();int scanned=0;var timer=Stopwatch.StartNew();
        while(pending.Count>0&&scanned<100000&&timer.Elapsed<TimeSpan.FromSeconds(30))
        {
            var folder=pending.Pop();
            try
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    if(++scanned>100000||timer.Elapsed>=TimeSpan.FromSeconds(30))break;
                    try
                    {
                        var full=Path.GetFullPath(path);if(!TemporaryRoots().Any(r=>full.StartsWith(r,StringComparison.OrdinalIgnoreCase)))continue;
                        var attributes=File.GetAttributes(full);if((attributes&FileAttributes.ReparsePoint)!=0)continue;
                        if((attributes&FileAttributes.Directory)!=0){pending.Push(full);continue;}
                        if(File.GetLastWriteTimeUtc(full)<DateTime.UtcNow.AddDays(-7)&&File.GetLastAccessTimeUtc(full)<DateTime.UtcNow.AddDays(-7))files.Add(full);
                    }catch{}
                }
            }catch{}
        }
        return files;
    }
    internal static object CleanTemporary(List<string> files)
    {
        int deleted=0,skipped=0;long bytes=0;
        foreach(var path in files)try
        {
            var full=Path.GetFullPath(path);var root=TemporaryRoots().FirstOrDefault(r=>full.StartsWith(r,StringComparison.OrdinalIgnoreCase));
            if(root==null||!Directory.Exists(root)||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0||!File.Exists(full)){skipped++;continue;}
            // Revalidate every ancestor, the age and the file immediately before deletion.
            bool linked=false;for(var dir=Path.GetDirectoryName(full);dir!=null&&dir.StartsWith(root,StringComparison.OrdinalIgnoreCase);dir=Path.GetDirectoryName(dir))if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0){linked=true;break;}
            var info=new FileInfo(full);
            if(linked||(info.Attributes&FileAttributes.ReparsePoint)!=0||info.LastWriteTimeUtc>=DateTime.UtcNow.AddDays(-7)||info.LastAccessTimeUtc>=DateTime.UtcNow.AddDays(-7)){skipped++;continue;}
            var size=info.Length;
            using(var unlocked=new FileStream(full,FileMode.Open,FileAccess.ReadWrite,FileShare.None,4096,FileOptions.DeleteOnClose)){}
            deleted++;bytes+=size;
        }catch{skipped++;}
        return new{status=deleted==0&&skipped>0?"failed":"complete",snapshotId="",summary=$"Очистка временных файлов: удалено {deleted}, объём {bytes/1073741824.0:F2} ГБ ({bytes/1048576} МБ). Пропущено {skipped} занятых или изменившихся файлов. Личные папки не очищались."};
    }
    internal static object EmptyRecycle()
    {
        var result=SHEmptyRecycleBinW(IntPtr.Zero,null,7);
        return new{status=result==0?"complete":"failed",snapshotId="",summary=result==0?"Корзина очищена после подтверждения на ПК.":"Windows не выполнила очистку корзины. Код: "+result.ToString("X8")};
    }
}
