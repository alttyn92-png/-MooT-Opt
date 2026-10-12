using System.Diagnostics;
using System.Text;
namespace MooT.Agent;
internal static class ReviewedCommand
{
    private static string? approvedLease;
    private static DateTimeOffset approvedUntil;
    internal static void Revoke(){approvedLease=null;approvedUntil=DateTimeOffset.MinValue;}
    internal static async Task<object> Run(Form owner,string script,string description,Func<bool> expired,string lease="",long leaseUntil=0)
    {
        if(script.Length<1||script.Length>10000||description.Length>800)throw new InvalidOperationException("Некорректный сценарий.");
        if(lease.Length==32&&leaseUntil>DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            if(approvedLease!=lease||approvedUntil<=DateTimeOffset.UtcNow)
            {
                if(MessageBox.Show(owner,"Разрешить автоматическое выполнение системных сценариев из вашего чата до "+DateTimeOffset.FromUnixTimeSeconds(leaseUntil).ToLocalTime().ToString("HH:mm")+"? Команды получают права MooT, могут удалять данные и менять систему без повторного вопроса. Откат не гарантируется. Отключить: значок MooT в трее → Отключить автоматические команды.\n\nПервая задача: "+description,"MooT · Автоматический доступ",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return new{status="failed",snapshotId="",summary="Автоматический доступ не разрешён на ПК."};
                approvedLease=lease;approvedUntil=DateTimeOffset.FromUnixTimeSeconds(leaseUntil);
            }
            if(expired()||approvedUntil<=DateTimeOffset.UtcNow)return new{status="failed",snapshotId="",summary="Срок команды истёк."};
            return await Execute(script);
        }
        using var dialog=new Form{Text="MooT · Проверка системной команды",ClientSize=new Size(760,570),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
        var explanation=new Label{Dock=DockStyle.Top,Height=110,Padding=new Padding(12),Text=description+"\n\nСценарий выполнится с правами MooT. Он может изменять или удалять данные. Автоматический откат не гарантируется. Подтвердите план — остальные шаги приложение выполнит само. Текст сценария приведён ниже."};
        var code=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,Dock=DockStyle.Fill,WordWrap=false,Font=new Font("Consolas",10),Text=script};
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=46,FlowDirection=FlowDirection.RightToLeft};
        var cancel=new Button{Text="Отмена",DialogResult=DialogResult.Cancel,Width=110};var run=new Button{Text="Выполнить",DialogResult=DialogResult.OK,Width=110};buttons.Controls.Add(cancel);buttons.Controls.Add(run);dialog.Controls.Add(code);dialog.Controls.Add(explanation);dialog.Controls.Add(buttons);dialog.CancelButton=cancel;dialog.AcceptButton=cancel;
        if(dialog.ShowDialog(owner)!=DialogResult.OK||expired())return new{status="failed",snapshotId="",summary="Сценарий не запущен: отмена или истёк срок подтверждения."};
        return await Execute(script);
    }
    private static async Task<object> Execute(string script)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        // The server credentials are not inherited by the execution child.
        foreach(var key in start.Environment.Keys.Where(k=>k.Contains("TOKEN",StringComparison.OrdinalIgnoreCase)||k.Contains("SECRET",StringComparison.OrdinalIgnoreCase)||k.Contains("API_KEY",StringComparison.OrdinalIgnoreCase)).ToArray())start.Environment.Remove(key);
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-OutputFormat","Text","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; try {\n"+script+"\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }"))})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new InvalidOperationException("PowerShell не запущен.");
        using var outputDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(95));
        async Task<string> Bounded(StreamReader reader){var result=new StringBuilder();var buffer=new char[1024];int n;try{while((n=await reader.ReadAsync(buffer.AsMemory(),outputDeadline.Token))>0)if(result.Length<8192)result.Append(buffer,0,Math.Min(n,8192-result.Length));}catch(OperationCanceledException){}return result.ToString();}
        var output=Bounded(process.StandardOutput);var error=Bounded(process.StandardError);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));bool timedOut=false;
        try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){timedOut=true;try{process.Kill(true);}catch{}}
        var text=await output;var errors=await error;
        var success=!timedOut&&process.ExitCode==0;
        var logFolder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MooT","commands");Directory.CreateDirectory(logFolder);
        File.WriteAllText(Path.Combine(logFolder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+".log"),script+"\n\nOUTPUT\n"+text+"\nERROR\n"+errors);
        var summary=timedOut?"Лимит 90 секунд истёк; процесс остановлен. Часть изменений могла примениться.":success?"Сценарий завершён без ошибки процесса. "+text:"Сценарий завершился с ошибкой. "+errors;
        if(summary.Length>490)summary=summary[..490];return new{status=success?"complete":"failed",snapshotId="",summary};
    }
    internal static Task<object> SelfTest()=>Execute("Write-Output 'MooT command transport fixture'; if (2+2 -ne 4) { throw 'fixture failed' }");
}
