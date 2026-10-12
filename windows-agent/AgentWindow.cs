using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace MooT.Agent;

internal sealed class AgentWindow : Form
{
    private static readonly Color Ink=Color.FromArgb(11,11,13),Panel=Color.FromArgb(25,26,30),Red=Color.FromArgb(238,238,241),Muted=Color.FromArgb(153,157,168);
    private readonly TextBox code=new(){BorderStyle=BorderStyle.None,TextAlign=HorizontalAlignment.Center,Font=new Font("Segoe UI",22),MaxLength=20,ShortcutsEnabled=true};
    private readonly Label state=new(),user=new(),detail=new(),activity=new();
    private readonly StatusLight connectionLight=new(),activityLight=new();
    private readonly Button primary=new(),paste=new(),disconnect=new(),restore=new();
    private readonly CheckBox consent=new(),autostart=new();
    private readonly NotifyIcon tray=new();
    private bool paused;
    private bool exitAfterResult;
    private readonly ToolStripMenuItem pauseItem=new("Приостановить");
    private readonly HttpClient client=new(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(12)};
    private readonly Operations operations=new(new WindowsMachine(),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MooT","snapshots"));
    private readonly GpuOverclock gpuTuning=new(()=>new NvidiaGpu(),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MooT","gpu-active.json"));
    private System.Threading.Timer? gpuGuard;
    private readonly SemaphoreSlim operationLock=new(1,1);
    private CancellationTokenSource? polling;
    private readonly string outbox=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MooT","outbox.json");
    private record PendingResult(string Owner,string Id,JsonElement Result);
    private string? token;private bool granted;private bool busy;private bool closing;
    public AgentWindow()
    {
        Text="MooT Optimization Boonderking";ClientSize=new Size(430,550);FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;BackColor=Ink;ForeColor=Color.White;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
        Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var logo=new Logo{Location=new Point(25,25),Size=new Size(49,42)};Controls.Add(logo);
        LabelAt("MooT",85,20,310,43,20,Color.White,FontStyle.Bold);LabelAt("Optimization Boonderking",87,64,310,24,9,Muted);
        LabelAt("ПОДКЛЮЧЕНИЕ К КОМПЬЮТЕРУ",25,104,380,20,8,Muted);
        state.SetBounds(25,133,380,28);state.Font=new Font("Segoe UI",14,FontStyle.Bold);state.ForeColor=Red;Controls.Add(state);
        user.SetBounds(25,167,380,21);user.ForeColor=Muted;Controls.Add(user);
        detail.SetBounds(25,199,380,43);detail.Font=new Font("Segoe UI",10);detail.ForeColor=Muted;Controls.Add(detail);
        var entryPanel=new System.Windows.Forms.Panel{Location=new Point(25,250),Size=new Size(268,54),BackColor=Panel,Padding=new Padding(8,9,8,8)};
        code.Dock=DockStyle.Fill;code.BackColor=Panel;code.ForeColor=Color.White;code.PlaceholderText="Код с сайта";entryPanel.Controls.Add(code);Controls.Add(entryPanel);
        ButtonAt(paste,"Вставить",305,250,100,54,false);paste.Click+=(_,_)=>PasteCode();
        code.KeyDown+=(_,e)=>{if(e.Control&&e.KeyCode==Keys.V){e.SuppressKeyPress=true;PasteCode();}if(e.KeyCode==Keys.Enter){e.SuppressKeyPress=true;_ = ConnectOrGrant();}};
        var menu=new ContextMenuStrip();menu.Items.Add("Вставить",null,(_,_)=>PasteCode());menu.Items.Add("Выделить всё",null,(_,_)=>code.SelectAll());code.ContextMenuStrip=menu;
        consent.SetBounds(25,248,380,60);consent.Text="Разрешаю передавать характеристики и нагрузку ассистенту\nи выполнять мои запросы. Серьёзные действия — с подтверждением.";consent.ForeColor=Color.FromArgb(190,194,205);consent.Visible=false;Controls.Add(consent);
        ButtonAt(primary,"Подключить ПК",25,326,380,43,true);primary.Click+=async(_,_)=>await ConnectOrGrant();
        ButtonAt(disconnect,"Отключить",25,380,180,35,false);disconnect.Click+=async(_,_)=>await Disconnect();disconnect.Visible=false;
        ButtonAt(restore,"Вернуть настройки",217,380,188,35,false);restore.Click+=async(_,_)=>await RestoreLocal();restore.Visible=operations.HasActive||gpuTuning.HasActive;
        autostart.SetBounds(25,438,380,30);autostart.Text="Запускать вместе с Windows";autostart.ForeColor=Muted;
        using(var run=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) autostart.Checked=run?.GetValue("MooT")!=null;
        autostart.CheckedChanged+=(_,_)=>{try{using var run=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");if(autostart.Checked)run.SetValue("MooT","\""+Application.ExecutablePath+"\" --background --server "+client.BaseAddress!.GetLeftPart(UriPartial.Authority));else run.DeleteValue("MooT",false);}catch{state.Text="Не удалось изменить автозагрузку";}};Controls.Add(autostart);
        LabelAt("Крестик скрывает окно · выход через значок в трее",25,502,390,20,8,Muted);
        var trayMenu=new ContextMenuStrip();trayMenu.Items.Add("Открыть MooT",null,(_,_)=>{Show();WindowState=FormWindowState.Normal;Activate();});
        pauseItem.Click+=(_,_)=>{paused=!paused;pauseItem.Text=paused?"Продолжить":"Приостановить";state.Text=paused?"Работа приостановлена":"Восстанавливаем соединение…";activity.Text=paused?"Приостановлен":"Ожидание соединения";activityLight.Tint=paused?Color.FromArgb(235,105,115):Muted;};trayMenu.Items.Add(pauseItem);
        trayMenu.Items.Add("Отключить автоматические команды",null,(_,_)=>{ReviewedCommand.Revoke();state.Text="Автоматические команды отключены";});
        trayMenu.Items.Add("Перезапустить администратором",null,(_,_)=>{if(Elevate()){closing=true;polling?.Cancel();tray.Visible=false;Close();}});
        trayMenu.Items.Add("Отвязать компьютер",null,async(_,_)=>await Disconnect());
        trayMenu.Items.Add("Выйти",null,(_,_)=>{closing=true;polling?.Cancel();tray.Visible=false;Close();});
        tray.Icon=Icon;tray.Text="MooT — помощник ПК";tray.ContextMenuStrip=trayMenu;tray.Visible=true;tray.DoubleClick+=(_,_)=>{Show();Activate();};
        var server=new Uri(Environment.GetEnvironmentVariable("MOOT_SERVER_URL")??"https://cultmoot.online");
        if(server.Scheme!="https"&&!server.IsLoopback)throw new InvalidOperationException("Для публичного сервера требуется HTTPS.");
        client.BaseAddress=new Uri(server.GetLeftPart(UriPartial.Authority)+"/api/agent/");
        ConfigureCompactWindow(entryPanel,logo);
        client.DefaultRequestHeaders.Add("X-MooT-Version",Application.ProductVersion.Split('+')[0]);
        SetDisconnected();
        if(Environment.GetEnvironmentVariable("MOOT_UI_PREVIEW")!="1")gpuGuard=new System.Threading.Timer(_=>{try{gpuTuning.Guard();}catch{if(!IsDisposed&&IsHandleCreated)BeginInvoke(new Action(()=>{state.Text="GPU: требуется восстановление";restore.Visible=true;}));}},null,3000,3000);
        FormClosing+=(_,e)=>{if(closing&&gpuTuning.HasActive){try{gpuTuning.Restore();}catch{closing=false;e.Cancel=true;Show();state.Text="Не удалось восстановить GPU";detail.Text="Повторите восстановление профиля перед выходом.";return;}}if(!closing&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();return;}polling?.Cancel();tray.Dispose();};
        void ToggleEntry(bool show){entryPanel.Visible=show;paste.Visible=show;consent.Visible=!show;}
        toggleEntry=ToggleEntry;
        Shown+=(_,_)=>
        {
            if(Environment.GetEnvironmentVariable("MOOT_UI_PREVIEW")=="1")return;
            if(gpuTuning.HasActive){try{gpuTuning.Restore();}catch{state.Text="Восстановите профиль GPU";restore.Visible=true;}}
            var saved=SessionStore.Load();
            _ = CheckForUpdate(saved?.Server==client.BaseAddress!.ToString()?saved.Token:null);
            if(Environment.GetCommandLineArgs().Contains("--background"))BeginInvoke(new Action(Hide));
            if(saved==null||saved.Server!=client.BaseAddress!.ToString())return;
            token=saved.Token;granted=saved.Granted&&saved.Version>=3;user.Text=saved.User;toggleEntry(false);consent.Visible=!granted;disconnect.Visible=false;
            CompactLayout();primary.Visible=!granted;primary.Text="Предоставить доступ";state.Text="Восстанавливаем соединение…";detail.Text="Сервер: "+client.BaseAddress.Host;
            polling=new CancellationTokenSource();_ = Poll(token,polling.Token);
        };
    }
    private async Task CheckForUpdate(string? credential)
    {
        try
        {
            var installer=await AgentUpdates.DownloadIfAccepted(this,new Uri(client.BaseAddress!.GetLeftPart(UriPartial.Authority)),credential);
            if(installer==null||IsDisposed)return;
            if(MessageBox.Show(this,"Обновление скачано и проверено. Завершить текущую задачу и открыть установщик?", "Обновление MooT",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)return;
            paused=true;
            await operationLock.WaitAsync();
            try
            {
                await FlushResult(token??"",CancellationToken.None);
                if(gpuTuning.HasActive)gpuTuning.Restore();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer){UseShellExecute=true});
                closing=true;polling?.Cancel();tray.Visible=false;Close();
            }
            finally{operationLock.Release();if(!closing)paused=false;}
        }
        catch{paused=false;if(!IsDisposed)MessageBox.Show(this,"Установщик не запущен. Текущая версия сохранена. Повторите обновление после завершения задачи.","Обновление MooT");}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){gpuGuard?.Dispose();polling?.Cancel();ResetStream();tray.Visible=false;tray.Dispose();client.Dispose();}
        base.Dispose(disposing);
    }
    private readonly Action<bool> toggleEntry;
    private void LabelAt(string text,int x,int y,int w,int h,float size,Color color,FontStyle style=FontStyle.Regular)=>Controls.Add(new Label{Text=text,Location=new Point(x,y),Size=new Size(w,h),Font=new Font("Segoe UI",size,style),ForeColor=color});
    private void ButtonAt(Button b,string text,int x,int y,int w,int h,bool accent)
    {
        b.Text=text;b.SetBounds(x,y,w,h);b.FlatStyle=FlatStyle.Flat;b.FlatAppearance.BorderColor=accent?Red:Color.FromArgb(55,57,65);b.BackColor=accent?Red:Panel;b.ForeColor=accent?Ink:Color.FromArgb(221,224,233);b.Cursor=Cursors.Hand;b.Font=new Font("Segoe UI",10,FontStyle.Bold);Controls.Add(b);
    }
    private void PasteCode()
    {
        try {if(Clipboard.ContainsText()){code.Text=Regex.Replace(Clipboard.GetText(),@"\s","");code.Focus();code.SelectionStart=code.Text.Length;}}
        catch {state.Text="Буфер обмена занят. Повторите вставку.";}
    }
    private async Task<JsonElement> Request(string path,object? data,string? credential,CancellationToken ct=default)
    {
        using var req=new HttpRequestMessage(data==null?HttpMethod.Get:HttpMethod.Post,path);
        if(data!=null)req.Content=new StringContent(JsonSerializer.Serialize(data),Encoding.UTF8,"application/json");
        if(credential!=null)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);
        using var response=await client.SendAsync(req,ct);
        if(response.StatusCode==HttpStatusCode.Unauthorized)throw new UnauthorizedAccessException();
        if(response.StatusCode==HttpStatusCode.BadRequest)throw new InvalidDataException("Сервер отклонил формат результата");
        if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Сервер отклонил запрос. Проверьте код и соединение.");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));return doc.RootElement.Clone();
    }
    private async Task ConnectOrGrant()
    {
        if(busy)return;
        if(token!=null&&granted)return;
        if(token==null&&!Regex.IsMatch(code.Text.Trim(),@"^[0-9]{7}$")){state.Text="Введите 7 цифр с сайта";return;}
        if(token!=null&&!consent.Checked){state.Text="Подтвердите доступ ниже";return;}
        busy=true;primary.Enabled=false;
        try
        {
            if(token==null)
            {
                var result=await Request("pair",new{code=code.Text.Trim(),name=Environment.MachineName},null);
                token=result.GetProperty("token").GetString();state.Text="ПК подключён";
                user.Text=result.GetProperty("user").GetString();detail.Text="Теперь разрешите действия ассистента.\nСерьёзные изменения подтверждаются на этом ПК.";
                toggleEntry(false);primary.Text="Предоставить доступ";disconnect.Visible=false;
                polling=new CancellationTokenSource();_ = Poll(token!,polling.Token);
                SessionStore.Save(new(token!,user.Text,false,client.BaseAddress!.ToString(),3));
            }
            else
            {
                await Request("grant",new{consent=true,scope="optimization.v1"},token);granted=true;CompactLayout();consent.Visible=false;autostart.Checked=true;
                state.Text="Подключён · доступ разрешён";detail.Text="Готов к вашим запросам на сайте.\nВсе выполненные действия отображаются в истории.";primary.Visible=false;
                SessionStore.Save(new(token,user.Text,true,client.BaseAddress!.ToString(),3));
            }
        }
        catch {state.Text="Не удалось подключиться";detail.Text="Проверьте, что сайт работает, и создайте новый код.";}
        finally {busy=false;primary.Enabled=true;}
    }
    private async Task Poll(string credential,CancellationToken ct)
    {
        _ = SendTelemetry(credential,ct);
        while(!ct.IsCancellationRequested)
        {
            try
            {
                if(paused){await Task.Delay(1000,ct);continue;}
                await FlushResult(credential,ct);
                if(exitAfterResult){closing=true;polling?.Cancel();tray.Visible=false;Close();return;}
                var result=await LiveJobs(credential,ct);
                while(paused)await Task.Delay(500,ct);
                foreach(var job in result.GetProperty("jobs").EnumerateArray())
                {
                    if(ct.IsCancellationRequested||!granted)break;
                    var id=job.GetProperty("id").GetString();var type=job.GetProperty("type").GetString();object output;
                    if(job.TryGetProperty("expires",out var expires)&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()>expires.GetDouble())continue;
                    await operationLock.WaitAsync(ct);
                    try
                    {
                        if(ct.IsCancellationRequested)break;
                        var awaitingConfirmation=type is "apps.launch" or "file.duplicates" or "game.cs2.launch" or "browser.open" or "file.recycle" or "apps.uninstall" or "apps.install" or "command.review" or "storage.clean" or "recycle.empty" or "optimization.apply" or "overclock.apply";
                        await Request("progress",new{id,phase=awaitingConfirmation?"waiting_confirmation":"running"},credential,ct);
                        if(awaitingConfirmation){Show();WindowState=FormWindowState.Normal;Activate();}
                        activity.Text=type=="diagnostics.read"?"Обновление диагностики":"Выполняется задача";activityLight.Tint=Color.FromArgb(96,220,155);
                        output=type switch
                        {
                            "diagnostics.read" => await Telemetry.Read(),
                            "optimization.apply" => await ApplyOptimization(job),
                            "power.apply" => await Task.Run(()=>operations.Apply("power")),
                            "elevation.request" => RequestElevation(),
                            "overclock.apply" => ApplyGpu(job),
                            "overclock.rollback" => gpuTuning.Restore(),
                            "memory.clean" => await Task.Run(Maintenance.CleanMemory),
                            "storage.clean" => await CleanTemporary(job),
                            "file.recycle" => UserMaintenance.Recycle(this,job.GetProperty("fileName").GetString()!,job.GetProperty("location").GetString()!,()=>Expired(job)),
                            "apps.install" => await PackageActions.Install(this,job.GetProperty("appName").GetString()!,job.GetProperty("packageId").GetString()!,()=>Expired(job),job.TryGetProperty("launchAfter",out var launchAfter)&&launchAfter.ValueKind==JsonValueKind.True),
                            "apps.diagnose" => await PackageActions.Diagnose(job.GetProperty("appName").GetString()!),
                            "apps.close" => await AppControl.Close(job.GetProperty("appName").GetString()!),
                            "apps.launch" => await PackageActions.Launch(this,job.GetProperty("appName").GetString()!,()=>Expired(job)),
                            "file.duplicates" => await DuplicateFiles.Review(this,job.GetProperty("location").GetString()!,()=>Expired(job)),
                            "game.cs2.launch" => await AppControl.LaunchCs2(this),
                            "browser.open" => AppControl.OpenWebsite(this,job.GetProperty("url").GetString()!),
                            "apps.uninstall" => await UserMaintenance.Uninstall(this,job.GetProperty("appName").GetString()!,()=>Expired(job)),
                            "command.review" => await ReviewedCommand.Run(this,job.GetProperty("script").GetString()!,job.GetProperty("description").GetString()!,()=>Expired(job),job.TryGetProperty("lease",out var lease)?lease.GetString()!:"",job.TryGetProperty("leaseUntil",out var until)?(long)until.GetDouble():0),
                            "recycle.empty" => ConfirmLocal("Очистить корзину на всех дисках? Файлы будут удалены без обычного отката.") && !Expired(job) ? await Task.Run(Maintenance.EmptyRecycle) : Declined(),
                            "optimization.rollback" => await Task.Run(()=>operations.Restore(job.GetProperty("snapshotId").GetString())),
                            _ => throw new InvalidOperationException("Неизвестный тип задачи отклонён.")
                        };
                    }
                    catch(Exception e){output=new{status="failed",summary=e is InvalidOperationException?e.Message:"Windows отклонила действие. Настройки можно восстановить в приложении.",snapshotId=""};}
                    finally{operationLock.Release();restore.Visible=operations.HasActive||gpuTuning.HasActive;}
                    Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);
                    File.WriteAllText(outbox,JsonSerializer.Serialize(new PendingResult(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))),id!,JsonSerializer.SerializeToElement(output))));
                    await FlushResult(credential,ct);
                    if(exitAfterResult){closing=true;polling?.Cancel();tray.Visible=false;Close();return;}
                }
                if(granted){state.Text="Подключён";activity.Text="На связи · LIVE";activityLight.Tint=Color.FromArgb(96,220,155);}
            }
            catch(UnauthorizedAccessException){await Disconnect(false);return;}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}
            catch {ResetStream();if(!ct.IsCancellationRequested){state.Text="Нет связи · переподключение…";activity.Text="Ожидание связи";activityLight.Tint=Color.FromArgb(235,105,115);}}
            try{await Task.Delay(1000,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}
        }
    }
    private HttpResponseMessage? streamResponse;
    private StreamReader? streamReader;
    private void ResetStream(){streamReader?.Dispose();streamReader=null;streamResponse?.Dispose();streamResponse=null;}
    private async Task<JsonElement> LiveJobs(string credential,CancellationToken ct)
    {
        if(streamReader==null){using var request=new HttpRequestMessage(HttpMethod.Get,"events");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credential);streamResponse=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);if(streamResponse.StatusCode==HttpStatusCode.Unauthorized)throw new UnauthorizedAccessException();streamResponse.EnsureSuccessStatusCode();streamReader=new StreamReader(await streamResponse.Content.ReadAsStreamAsync(ct));}
        while(true){using var heartbeatDeadline=CancellationTokenSource.CreateLinkedTokenSource(ct);heartbeatDeadline.CancelAfter(TimeSpan.FromSeconds(35));var line=await streamReader.ReadLineAsync(heartbeatDeadline.Token);if(line==null){ResetStream();await Task.Delay(500,ct);using var empty=JsonDocument.Parse("{\"jobs\":[]}");return empty.RootElement.Clone();}if(line.StartsWith(":")&&granted&&!paused){state.Text="Подключён · LIVE";activity.Text="На связи · LIVE";activityLight.Tint=Color.FromArgb(96,220,155);}if(line.StartsWith("data: ")){using var document=JsonDocument.Parse(line[6..]);return document.RootElement.Clone();}}
    }
    private async Task SendTelemetry(string credential,CancellationToken ct)
    {
        var fullAt=DateTime.MinValue;
        while(!ct.IsCancellationRequested){try{if(granted&&!paused){var data=JsonSerializer.SerializeToElement(await Telemetry.Read());var full=DateTime.UtcNow-fullAt>TimeSpan.FromSeconds(30);var payload=data.EnumerateObject().Where(p=>full||p.Name is not ("inventory" or "processes" or "gpuControl")).ToDictionary(p=>p.Name,p=>p.Value.Clone());payload["agentVersion"]=JsonSerializer.SerializeToElement(Application.ProductVersion.Split('+')[0]);await Request("telemetry",new{result=payload},credential,ct);if(full)fullAt=DateTime.UtcNow;detail.Text="LIVE · диагностика передана "+DateTime.Now.ToString("HH:mm:ss");}}catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}catch(UnauthorizedAccessException){return;}catch{if(!ct.IsCancellationRequested)detail.Text="Не удалось передать диагностику. Повторяем автоматически.";}try{await Task.Delay(4000,ct);}catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}}
    }
    private async Task<object> CleanTemporary(JsonElement job)
    {
        var candidates=await Task.Run(Maintenance.TemporaryCandidates);
        if(candidates.Count==0)return new{status="complete",snapshotId="",summary="Подходящих временных файлов старше 7 дней не найдено. Файлы не удалялись."};
        if(!ConfirmLocal($"В пользовательской и системной папках Temp найдено {candidates.Count} файлов старше 7 дней, всего {Maintenance.TemporaryBytes(candidates)/1073741824.0:F2} ГБ. Удалить их безвозвратно, минуя корзину? Восстановление не предусмотрено. Занятые файлы будут пропущены. Документы, загрузки и рабочий стол не затрагиваются.")||Expired(job))return Declined();
        return await Task.Run(()=>Maintenance.CleanTemporary(candidates));
    }
    private object ApplyGpu(JsonElement job)
    {
        if(!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))throw new InvalidOperationException("Для GPU-профиля нужны права администратора.");
        if(SystemInformation.PowerStatus.PowerLineStatus!=PowerLineStatus.Online)throw new InvalidOperationException("Подключите питание от сети перед пробным профилем GPU.");
        var preview=gpuTuning.Preview();
        if(!ConfirmLocal(preview)||Expired(job))return Declined();
        return gpuTuning.Apply();
    }
    private bool Elevate()
    {
        if(new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))return false;
        try
        {
            var start=new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath){UseShellExecute=true,Verb="runas",Arguments="--wait-restart --server "+client.BaseAddress!.GetLeftPart(UriPartial.Authority)};
            using var child=System.Diagnostics.Process.Start(start);return child!=null;
        }
        catch(System.ComponentModel.Win32Exception){return false;}
    }
    private object RequestElevation()
    {
        bool admin=new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if(admin)return new{status="complete",snapshotId="",summary="Приложение уже запущено с правами администратора. Частоты и напряжения не менялись."};
        exitAfterResult=Elevate();
        return new{status=exitAfterResult?"complete":"failed",snapshotId="",summary=exitAfterResult?"UAC подтверждён: запускается приложение с правами администратора. Частоты и напряжения не менялись.":"Повышение прав отменено или не удалось. Настройки не изменены."};
    }
    private static bool Expired(JsonElement job)=>job.TryGetProperty("expires",out var expires)&&DateTimeOffset.UtcNow.ToUnixTimeSeconds()>expires.GetDouble();
    private async Task<object> ApplyOptimization(JsonElement job)
    {
        var mode=job.GetProperty("mode").GetString()!;
        var services=Array.Empty<string>();var apps=Array.Empty<string>();var analysis="";
        if(job.TryGetProperty("plan",out var plan))
        {
            services=plan.GetProperty("services").EnumerateArray().Select(v=>v.GetString()!).ToArray();
            apps=plan.GetProperty("closeApps").EnumerateArray().Select(v=>v.GetString()!).ToArray();
            analysis=plan.GetProperty("analysis").GetString()??"";
        }
        if(services.Any(s=>!OptionalServices.Allowed.Contains(s))||apps.Any(s=>s is not ("Discord" or "Spotify")))throw new InvalidOperationException("План содержит неподдерживаемое действие.");
        if(mode=="basic"&&(services.Length>0||apps.Length>0))throw new InvalidOperationException("Дополнительные действия требуют подтверждения максимального режима.");
        if(mode=="maximum")
        {
            var text=analysis+"\n\nПрименить высокий приоритет игры, схему «Высокая производительность», игровой режим Windows и отключить запись в фоне? После применения перезапустите игру.";
            if(services.Length>0)text+="\nПриостановить службы: "+string.Join(", ",services)+" (с откатом).";
            if(apps.Length>0)text+="\nЗапросить закрытие окон: "+string.Join(", ",apps)+". Открывать их снова нужно самостоятельно.";
            text+="\n\nИсходные системные настройки сохраняются. Применить этот план?";
            if(!ConfirmLocal(text)||Expired(job))return Declined();
        }
        var result=await Task.Run(()=>operations.Apply(mode,services));
        if(apps.Length>0)result["summary"]+=(" "+await Task.Run(()=>Maintenance.CloseBackgroundWindows(apps)));
        return result;
    }
    private bool ConfirmLocal(string text)
    {
        Show();WindowState=FormWindowState.Normal;Activate();
        return MessageBox.Show(this,text,"MooT · Подтверждение действия",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes;
    }
    private static object Declined()=>new {status="failed",summary="Пользователь отклонил действие на компьютере.",snapshotId=""};
    private async Task FlushResult(string credential,CancellationToken ct)
    {
        if(!File.Exists(outbox))return;
        var pending=JsonSerializer.Deserialize<PendingResult>(File.ReadAllText(outbox));
        if(pending==null||pending.Owner!=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))))return;
        try{await Request("result",new{id=pending.Id,result=pending.Result},credential,ct);}catch(InvalidDataException){File.Move(outbox,outbox+".rejected",true);activity.Text="Результат отклонён сервером";return;}File.Delete(outbox);
    }
    private async Task RestoreLocal()
    {
        await operationLock.WaitAsync();
        try {if(gpuTuning.HasActive)await Task.Run(()=>gpuTuning.Restore());if(operations.HasActive)await Task.Run(()=>operations.Restore());state.Text="Настройки восстановлены";detail.Text="Сохранённые настройки возвращены.";}
        catch(Exception e){state.Text="Откат не завершён";detail.Text=e is InvalidOperationException?e.Message:"Windows отклонила откат. Повторите после закрытия игры.";}
        finally{operationLock.Release();restore.Visible=operations.HasActive||gpuTuning.HasActive;}
    }
    private async Task Disconnect(bool revoke=true)
    {
        polling?.Cancel();ResetStream();var old=token;token=null;granted=false;
        SessionStore.Clear();if(File.Exists(outbox))File.Delete(outbox);
        if(revoke&&old!=null){try{await Request("disconnect",new{},old);}catch{}}
        SetDisconnected();toggleEntry?.Invoke(true);consent.Visible=false;consent.Checked=false;
    }
    private void SetDisconnected()
    {
        CompactLayout();activity.Text="Неактивен";activityLight.Tint=Muted;
        state.Text="Подключите ваш ПК";user.Text="Вход по коду с сайта";detail.Text="Скопируйте семизначный код на сайте\nи вставьте его в поле ниже.";primary.Visible=true;primary.Text="Подключить ПК";disconnect.Visible=false;restore.Visible=operations.HasActive||gpuTuning.HasActive;
    }
    private void ConfigureCompactWindow(System.Windows.Forms.Panel entry,Control logo)
    {
        foreach(var label in Controls.OfType<Label>().Where(l=>l!=state&&l!=user&&l!=detail).ToArray())label.Visible=false;
        logo.SetBounds(20,20,49,42);
        user.SetBounds(86,19,304,24);user.Font=new Font("Segoe UI",11,FontStyle.Bold);user.ForeColor=Color.White;user.AutoEllipsis=true;
        state.SetBounds(101,48,289,18);state.Font=new Font("Segoe UI",8);state.ForeColor=Muted;state.AutoEllipsis=true;
        connectionLight.SetBounds(84,48,14,14);Controls.Add(connectionLight);
        activity.SetBounds(101,72,289,18);activity.Font=new Font("Segoe UI",8);activity.ForeColor=Muted;Controls.Add(activity);
        activityLight.SetBounds(84,73,14,14);Controls.Add(activityLight);
        detail.SetBounds(20,107,370,36);detail.Font=new Font("Segoe UI",8);
        entry.SetBounds(20,151,270,43);code.Font=new Font("Segoe UI",17);paste.SetBounds(302,151,88,43);paste.Font=new Font("Segoe UI",8);
        consent.SetBounds(20,145,370,64);consent.Font=new Font("Segoe UI",8);
        primary.SetBounds(20,216,370,34);primary.Font=new Font("Segoe UI",9,FontStyle.Bold);
        autostart.SetBounds(20,262,370,24);autostart.Font=new Font("Segoe UI",8);
        restore.Visible=false;
        state.TextChanged+=(_,_)=>{connectionLight.Tint=state.Text.StartsWith("Подключён")||state.Text.StartsWith("ПК подключён")?Color.FromArgb(96,220,155):Color.FromArgb(235,105,115);};
        restore.VisibleChanged+=(_,_)=>{if(restore.Visible)restore.Visible=false;};
        tray.ContextMenuStrip?.Items.Add("Вернуть настройки",null,async(_,_)=>await RestoreLocal());
    }
    private void CompactLayout()
    {
        ClientSize=new Size(410,granted?174:302);
        detail.Visible=!granted;
        autostart.Top=granted?119:262;
    }
    private sealed class StatusLight : Control
    {
        private Color tint=Muted;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] public Color Tint {get=>tint;set{tint=value;Invalidate();}}
        public StatusLight(){SetStyle(ControlStyles.SupportsTransparentBackColor|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);BackColor=Color.Transparent;}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var halo=new SolidBrush(Color.FromArgb(28,tint));using var middle=new SolidBrush(Color.FromArgb(60,tint));using var dot=new SolidBrush(tint);
            e.Graphics.FillEllipse(halo,0,0,14,14);e.Graphics.FillEllipse(middle,2,2,10,10);e.Graphics.FillEllipse(dot,4,4,6,6);
        }
    }
    internal void PreviewConnected(){granted=true;toggleEntry(false);consent.Visible=false;primary.Visible=false;user.Text="Пользователь";state.Text="Подключён";activity.Text="На связи · LIVE";activityLight.Tint=Color.FromArgb(96,220,155);CompactLayout();}
    private sealed class Logo : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush=new SolidBrush(Red);
            e.Graphics.FillPolygon(brush,new Point[]{new(0,40),new(0,0),new(10,0),new(24,17),new(38,0),new(48,0),new(48,40),new(38,40),new(38,19),new(24,35),new(10,19),new(10,40)});
        }
    }
}

