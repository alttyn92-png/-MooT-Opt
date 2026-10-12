using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;
namespace MooT.Agent;

internal static class UserMaintenance
{
    internal static bool UserFile(string path)
    {
        var full=Path.GetFullPath(path);
        var roots=new[]{Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.MyPictures,Environment.SpecialFolder.MyMusic,Environment.SpecialFolder.MyVideos}.Select(Environment.GetFolderPath).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads"));
        if(!roots.Any(r=>!string.IsNullOrEmpty(r)&&full.StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))return false;
        if(!File.Exists(full))return false;
        var current=full;
        while(!string.IsNullOrEmpty(current)){if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return false;current=Path.GetDirectoryName(current);}
        return true;
    }
    private static string[] FindPersonalFile(string name)
    {
        var roots=new[]{Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.MyPictures,Environment.SpecialFolder.MyMusic,Environment.SpecialFolder.MyVideos}.Select(Environment.GetFolderPath).Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
        var pending=new Stack<(string Path,int Depth)>(roots.Select(p=>(p,0)));var found=new List<string>();int inspected=0;
        while(pending.Count>0&&inspected<30000){var (folder,depth)=pending.Pop();try{if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;foreach(var path in Directory.EnumerateFileSystemEntries(folder)){if(++inspected>30000)break;var attrs=File.GetAttributes(path);if((attrs&FileAttributes.ReparsePoint)!=0)continue;if((attrs&FileAttributes.Directory)!=0){if(depth<8)pending.Push((path,depth+1));}else if(Path.GetFileName(path).Equals(name,StringComparison.OrdinalIgnoreCase)&&UserFile(path))found.Add(path);}}catch{}}
        return found.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
    }
    internal static object Recycle(Form owner,string name,string location,Func<bool> expired)
    {
        var folder=location switch {"downloads"=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads"),"documents"=>Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),_=>Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)};
        string[] files;
        if(name.Length>0){if(name!=Path.GetFileName(name)||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidOperationException("Укажите имя файла без пути.");files=location=="auto"?FindPersonalFile(name):new[]{Path.Combine(folder,name)};if(files.Length==0)throw new InvalidOperationException("Файл с таким именем не найден в доступных личных папках. Уточните название или папку.");if(files.Length>1)throw new InvalidOperationException("Найдено несколько одноимённых файлов. Уточните папку перед удалением.");}
        else {using var picker=new OpenFileDialog{Title="Выберите файлы для перемещения в корзину",InitialDirectory=folder,Multiselect=true,CheckFileExists=true};if(picker.ShowDialog(owner)!=DialogResult.OK)return Declined();files=picker.FileNames;}
        if(files.Length>30||files.Any(f=>!UserFile(f)))throw new InvalidOperationException("Можно выбрать до 30 обычных файлов в личных папках. Системные пути и ссылки не поддерживаются.");
        var selected=files.Select(p=>new FileInfo(p)).Select(f=>(Path:f.FullName,Size:f.Length,Time:f.LastWriteTimeUtc)).ToArray();
        if(MessageBox.Show(owner,"Переместить в корзину?\n\n"+string.Join("\n",selected.Select(f=>f.Path))+"\n\nВосстановление доступно через корзину Windows.","MooT · Удаление файлов",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes||expired())return Declined();
        int done=0;
        foreach(var f in selected){var current=new FileInfo(f.Path);if(expired())break;if(!UserFile(f.Path)||current.Length!=f.Size||current.LastWriteTimeUtc!=f.Time)throw new InvalidOperationException("Файл изменился после подтверждения. Повторите запрос.");FileSystem.DeleteFile(f.Path,UIOption.OnlyErrorDialogs,RecycleOption.SendToRecycleBin,UICancelOption.ThrowException);done++;}
        return new{status=done==selected.Length?"complete":"failed",snapshotId="",summary=$"Перемещено в корзину: {done} из {selected.Length} файлов. Восстановить их можно из корзины Windows."};
    }
    private record AppEntry(string Name,string Command,string Key,bool Msi);
    private static List<AppEntry> Apps()
    {
        var list=new List<AppEntry>();
        foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
        {
            using var root=RegistryKey.OpenBaseKey(hive,view);using var key=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");if(key==null)continue;
            foreach(var id in key.GetSubKeyNames())try{using var app=key.OpenSubKey(id);var name=app?.GetValue("DisplayName") as string;var command=app?.GetValue("UninstallString") as string;
                if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(command)||name.StartsWith("MooT",StringComparison.OrdinalIgnoreCase)||Convert.ToInt32(app?.GetValue("SystemComponent",0))!=0||app?.GetValue("ParentKeyName")!=null)continue;
                list.Add(new(name,command,id,Convert.ToInt32(app?.GetValue("WindowsInstaller",0))==1));
            }catch{}
        }
        return list.DistinctBy(a=>a.Name+"|"+a.Command).OrderBy(a=>a.Name).ToList();
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    internal static string[] Split(string command)
    {
        var ptr=CommandLineToArgvW(command,out var count);if(ptr==IntPtr.Zero)throw new InvalidOperationException("Некорректная команда удаления.");
        try{return Enumerable.Range(0,count).Select(i=>Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr,i*IntPtr.Size))!).ToArray();}finally{LocalFree(ptr);}
    }
    private static ProcessStartInfo Uninstaller(AppEntry entry)
    {
        if(entry.Msi&&Guid.TryParse(entry.Key,out var product))return new(Path.Combine(Environment.SystemDirectory,"msiexec.exe"),"/x {"+product+"}"){UseShellExecute=true};
        var args=Split(entry.Command);var exe=Path.GetFullPath(args[0]);var baseName=Path.GetFileName(exe);
        var allowedRoots=new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs")};
        if(!Path.IsPathFullyQualified(args[0])||!File.Exists(exe)||!allowedRoots.Any(r=>r.Length>0&&exe.StartsWith(r.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))||!(baseName.Contains("unins",StringComparison.OrdinalIgnoreCase)||baseName.Equals("Update.exe",StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("У этой программы нестандартный деинсталлятор. Удалите её в Параметры Windows → Приложения.");
        var start=new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(exe)};foreach(var arg in args.Skip(1))start.ArgumentList.Add(arg);return start;
    }
    internal static async Task<object> Uninstall(Form owner,string query,Func<bool> expired)
    {
        var apps=Apps().Where(a=>query.Length==0||a.Name.Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
        if(apps.Count==0)throw new InvalidOperationException("Программа не найдена среди доступных деинсталляторов. Уточните её название.");
        using var dialog=new Form{Text="MooT · Выберите программу",ClientSize=new Size(520,350),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
        var list=new ListBox{Dock=DockStyle.Fill};foreach(var a in apps)list.Items.Add(a.Name);dialog.Controls.Add(list);
        var button=new Button{Text="Выбрать",Dock=DockStyle.Bottom,Height=40,DialogResult=DialogResult.OK};dialog.Controls.Add(button);dialog.AcceptButton=button;list.SelectedIndex=0;
        if((apps.Count>1&&dialog.ShowDialog(owner)!=DialogResult.OK)||expired())return Declined();var app=apps[list.SelectedIndex];var start=Uninstaller(app);
        if(MessageBox.Show(owner,"Запустить удаление «"+app.Name+"»?\n\nОткроется штатный деинсталлятор программы. Он может запросить права администратора. Удаление не входит в откат настроек MooT.","MooT · Удаление программы",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes||expired())return Declined();
        using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось запустить деинсталлятор.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){return new{status="complete",snapshotId="",summary="Деинсталлятор запущен и ещё работает. Завершите его диалог; удаление пока не подтверждено."};}
        bool remains=Apps().Any(a=>a.Key==app.Key&&a.Name==app.Name);
        return new{status=remains?"failed":"complete",snapshotId="",summary=remains?"Деинсталлятор завершился, но программа всё ещё зарегистрирована: удаление не подтверждено.":"Программа больше не зарегистрирована в списке установленных: "+app.Name};
    }
    private static object Declined()=>new{status="failed",snapshotId="",summary="Действие отменено пользователем или срок подтверждения истёк."};
}
