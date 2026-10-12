using System.Diagnostics;
namespace MooT.Agent;
internal static class AppControl
{
    internal static async Task<object> LaunchCs2(Form owner)
    {
        using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steam=key?.GetValue("SteamExe") as string;
        if(string.IsNullOrEmpty(steam)||!Path.IsPathFullyQualified(steam)||!File.Exists(steam)||!Path.GetFileName(steam).Equals("steam.exe",StringComparison.OrdinalIgnoreCase))
            return new{status="failed",snapshotId="",summary="Steam не найден в профиле Windows. Установите или запустите Steam, затем повторите запуск CS2."};
        if(MessageBox.Show(owner,"Запустить Counter-Strike 2 через установленный Steam?", "MooT · CS2",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)
            return new{status="failed",snapshotId="",summary="Запуск CS2 отменён на компьютере."};
        var start=new ProcessStartInfo(steam){UseShellExecute=false};start.ArgumentList.Add("-applaunch");start.ArgumentList.Add("730");
        using var launcher=Process.Start(start);
        if(launcher==null)throw new InvalidOperationException("Windows не запустила Steam.");
        for(var i=0;i<15;i++)
        {
            var processes=Process.GetProcessesByName("cs2");var found=false;
            foreach(var process in processes)using(process){try{if(process.SessionId==Process.GetCurrentProcess().SessionId)found=true;}catch{}}
            if(found)return new{status="complete",snapshotId="",summary="Процесс CS2 обнаружен в вашей сессии Windows. Игра запускается или уже работает."};
            await Task.Delay(1000);
        }
        return new{status="failed",snapshotId="",summary="Запрос запуска передан Steam, но процесс CS2 пока не появился. Проверьте окно Steam: вход, установку игры или обновление. Повторный запуск автоматически не выполнялся."};
    }
    internal static bool IsWebsite(string address)=>Uri.TryCreate(address,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps&&!string.IsNullOrEmpty(uri.Host)&&string.IsNullOrEmpty(uri.UserInfo);
    internal static object OpenWebsite(Form owner,string address)
    {
        if(!IsWebsite(address))throw new InvalidOperationException("Для открытия сайта требуется полный HTTPS-адрес без логина и пароля.");
        if(MessageBox.Show(owner,"Открыть в браузере по умолчанию?\n\n"+address,"MooT · Открыть сайт",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return new{status="failed",snapshotId="",summary="Открытие сайта отменено на компьютере."};
        Process.Start(new ProcessStartInfo(address){UseShellExecute=true});
        return new{status="complete",snapshotId="",summary="Адрес сайта передан браузеру по умолчанию. Загрузку страницы и вход в аккаунт приложение не проверяет."};
    }
    private static readonly Dictionary<string,string> Aliases=new(StringComparer.OrdinalIgnoreCase){["word"]="WINWORD",["ворд"]="WINWORD",["microsoft word"]="WINWORD",["excel"]="EXCEL",["эксель"]="EXCEL",["microsoft excel"]="EXCEL",["whatsapp"]="WhatsApp",["ватсап"]="WhatsApp",["telegram"]="Telegram",["телеграм"]="Telegram",["discord"]="Discord",["spotify"]="Spotify",["chrome"]="chrome",["браузер chrome"]="chrome"};
    internal static async Task<object> Close(string app)
    {
        var name=Aliases.GetValueOrDefault(app.Trim(),app.Trim());if(name.Length<1||name.Length>80||name.IndexOfAny(Path.GetInvalidFileNameChars())>=0)throw new InvalidOperationException("Уточните название приложения.");
        using var self=Process.GetCurrentProcess();var candidates=new List<Process>();
        foreach(var p in Process.GetProcesses())try{if(p.Id!=self.Id&&p.SessionId==self.SessionId&&p.MainWindowHandle!=IntPtr.Zero&&(p.ProcessName.Equals(name,StringComparison.OrdinalIgnoreCase)||p.MainWindowTitle.Contains(name,StringComparison.OrdinalIgnoreCase))&&!p.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase))candidates.Add(p);else p.Dispose();}catch{p.Dispose();}
        if(candidates.Count==0)return new{status="failed",snapshotId="",summary="Открытое окно приложения «"+app+"» не найдено. Уточните название."};
        if(candidates.Select(p=>p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1){foreach(var p in candidates)p.Dispose();return new{status="failed",snapshotId="",summary="Название соответствует нескольким приложениям. Уточните, какое закрыть."};}
        int requested=0,closed=0;try{
            foreach(var p in candidates)try{if(p.CloseMainWindow())requested++;}catch{}
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            foreach(var p in candidates)try{await p.WaitForExitAsync(timeout.Token);closed++;}catch(OperationCanceledException){}catch{}
        }finally{foreach(var p in candidates)p.Dispose();}
        return new{status=closed==candidates.Count?"complete":"failed",snapshotId="",summary=closed==candidates.Count?"Приложение «"+app+"» закрыто.":$"Запрошено закрытие: {requested}; завершилось процессов: {closed}. Приложение ещё открыто — возможно, ждёт сохранения документа. Принудительное завершение не выполнялось."};
    }
}
