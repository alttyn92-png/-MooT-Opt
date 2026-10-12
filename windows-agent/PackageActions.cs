using System.Diagnostics;
using System.Text;
namespace MooT.Agent;
internal static class PackageActions
{
    private static string Winget=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps","winget.exe");
    private static async Task<(int Exit,string Text)> Run(string[] args,int seconds)
    {
        if(!File.Exists(Winget))throw new InvalidOperationException("Нужен Windows Package Manager — приложение App Installer из Microsoft Store.");
        var start=new ProcessStartInfo(Winget){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("Пакетный менеджер не запущен.");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        async Task<string> Read(StreamReader reader){var result=new StringBuilder();var buffer=new char[1024];try{int n;while((n=await reader.ReadAsync(buffer.AsMemory(),timeout.Token))>0)if(result.Length<8000)result.Append(buffer,0,Math.Min(n,8000-result.Length));}catch(OperationCanceledException){}return result.ToString();}
        var stdout=Read(process.StandardOutput);var stderr=Read(process.StandardError);
        try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{process.Kill(true);}catch{}throw new InvalidOperationException("Время ожидания истекло. Проверьте состояние установки в Windows перед повтором.");}
        return (process.ExitCode,(await stdout)+"\n"+(await stderr));
    }
    internal static async Task<object> Diagnose(string app)
    {
        if(string.IsNullOrWhiteSpace(app))throw new InvalidOperationException("Укажите название приложения.");
        var result=await Run(new[]{"list","--query",app,"--disable-interactivity"},30);
        return Result(result.Exit==0,result.Exit==0?"Сведения об установленном приложении:\n"+result.Text.Trim():"Пакетный менеджер не подтвердил установку. Приложение может отсутствовать или каталог требует входа/принятия условий. Код: "+result.Exit.ToString("X8")+". Данные программы не изменялись.");
    }
    internal static async Task<object> Install(Form owner,string app,string packageId,Func<bool> expired,bool launchAfter=false)
    {
        bool whatsapp=app.Equals("whatsapp",StringComparison.OrdinalIgnoreCase)||app.Contains("ватсап",StringComparison.OrdinalIgnoreCase);
        bool minecraft=app.Equals("Minecraft Launcher",StringComparison.OrdinalIgnoreCase);
        var id=minecraft?"9PGW18NPBZV5":whatsapp?"9NKSQGP7F2NH":packageId;var source=(whatsapp||minecraft)?"msstore":"winget";
        if(!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[A-Za-z0-9][A-Za-z0-9._-]{2,120}$"))throw new InvalidOperationException("Нужно точное имя пакета из каталога Windows Package Manager.");
        string description=whatsapp?"WhatsApp из Microsoft Store, пакет 9NKSQGP7F2NH. Официальная страница: whatsapp.com/download.":"Пакет "+id+" из каталога winget.";
        if(!whatsapp&&!minecraft){var info=await Run(new[]{"show","--id",id,"--exact","--source",source,"--disable-interactivity"},30);if(info.Exit!=0)throw new InvalidOperationException("Каталог не подтвердил пакет. Установка не выполнялась.");description+="\n"+info.Text[..Math.Min(2500,info.Text.Length)];}
        if(minecraft)description="Официальный Minecraft Launcher из Microsoft Store. Для игры требуются аккаунт и соответствующая лицензия.";
        if(MessageBox.Show(owner,"Установить «"+app+"»?\n\n"+description+"\n\nЛицензии не принимаются автоматически. Если магазину требуется вход или отдельное согласие, установка остановится и сообщит об этом.","MooT · Установка",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes||expired())return Result(false,"Установка отменена.");
        var installed=await Run(new[]{"list","--id",id,"--exact","--disable-interactivity"},30);
        var result=installed.Exit==0?installed:await Run(new[]{"install","--id",id,"--exact","--source",source,"--disable-interactivity"},600);
        var verify=await Run(new[]{"list","--id",id,"--exact","--disable-interactivity"},30);
        if(verify.Exit==0&&launchAfter)return await OpenInstalled(app);
        return Result(verify.Exit==0,verify.Exit==0?"Приложение «"+app+"» зарегистрировано в Windows. При первом запуске может потребоваться вход в аккаунт.":"Установка не подтверждена. Код: "+result.Exit.ToString("X8")+". Если нужны условия или вход, откройте Microsoft Store. "+result.Text.Trim());
    }
    internal static async Task<object> Launch(Form owner,string app,Func<bool> expired)
    {
        if(string.IsNullOrWhiteSpace(app)||app.Length>100)throw new InvalidOperationException("Уточните название приложения.");
        if(MessageBox.Show(owner,"Открыть установленное приложение «"+app+"»?", "MooT · Запуск",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes||expired())return Result(false,"Запуск отменён.");
        return await OpenInstalled(app);
    }
    internal static async Task<object> OpenInstalled(string app)
    {
        // Resolve actual Start-menu registrations instead of guessing an executable path.
        var script="[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | Select-Object Name,AppID | ConvertTo-Json -Compress";
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("Не удалось прочитать приложения Windows.");
        var text=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try{await process.WaitForExitAsync(deadline.Token);}catch(OperationCanceledException){try{process.Kill(true);}catch{}return Result(false,"Запись приложения не найдена или поиск для запуска превысил время ожидания.");}
        using var json=System.Text.Json.JsonDocument.Parse(await text);await errors;
        var entries=json.RootElement.ValueKind==System.Text.Json.JsonValueKind.Array?json.RootElement.EnumerateArray().ToArray():new[]{json.RootElement};
        var name=app.Contains("ватсап",StringComparison.OrdinalIgnoreCase)?"WhatsApp":app;
        var matches=entries.Where(e=>e.TryGetProperty("Name",out var n)&&string.Equals(n.GetString(),name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(matches.Length!=1)return Result(false,"Запись приложения не найдена или однозначная запись запуска не найдена. Уточните название приложения.");
        var id=matches[0].GetProperty("AppID").GetString();
        if(string.IsNullOrEmpty(id)||id.Any(c=>char.IsControl(c)||c=='"'))return Result(false,"Запись приложения не найдена или Windows вернула некорректную запись запуска.");
        var launch=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"explorer.exe")){UseShellExecute=false};launch.ArgumentList.Add("shell:AppsFolder\\"+id);using var opened=Process.Start(launch);
        return Result(opened!=null,"Найдена запись приложения в Windows и отправлена команда открытия. Завершение входа в аккаунт может потребовать вашего участия.");
    }
    private static object Result(bool success,string text)=>new{status=success?"complete":"failed",snapshotId="",summary=text.Length>490?text[..490]:text};
}
