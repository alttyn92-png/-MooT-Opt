namespace MooT.Agent;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        for (int i=0;i+1<args.Length;i++) if(args[i]=="--server") Environment.SetEnvironmentVariable("MOOT_SERVER_URL",args[i+1]);
        if (args.Length == 2 && args[0] == "--self-test")
        {
            File.WriteAllText(args[1], Operations.SelfTest());
            return;
        }
        if(args.Length==2&&args[0]=="--command-self-test"){File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(ReviewedCommand.SelfTest().GetAwaiter().GetResult()));return;}
        if(args.Length==2&&args[0]=="--diagnostics-test"){File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(Telemetry.Read().GetAwaiter().GetResult()));return;}
        if(args.Length==2&&args[0]=="--gpu-self-test"){File.WriteAllText(args[1],GpuOverclock.SelfTest());return;}
        if(args.Length==2&&args[0]=="--ui-self-test")
        {
            Environment.SetEnvironmentVariable("MOOT_UI_PREVIEW","1");
            using var testWindow=new AgentWindow();testWindow.Show();Application.DoEvents();
            testWindow.Close();Application.DoEvents();
            bool hidden=!testWindow.Visible&&!testWindow.IsDisposed;
            testWindow.Show();Application.DoEvents();bool restored=testWindow.Visible;
            File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(new{ok=hidden&&restored,closeHides=hidden,reopen=restored}));return;
        }
        if(args.Length==2&&(args[0]=="--preview"||args[0]=="--preview-connected"))
        {
            Environment.SetEnvironmentVariable("MOOT_UI_PREVIEW","1");
            using var window=new AgentWindow();
            window.Show();if(args[0]=="--preview-connected")window.PreviewConnected();Application.DoEvents();
            using var bitmap=new System.Drawing.Bitmap(window.Width,window.Height);
            window.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,window.Width,window.Height));
            bitmap.Save(args[1]);return;
        }
        using var mutex=new Mutex(false,"Local\\MooT.Agent.Singleton");
        bool created;
        try{created=mutex.WaitOne(args.Contains("--wait-restart")?TimeSpan.FromSeconds(45):TimeSpan.Zero);}catch(AbandonedMutexException){created=true;}
        if(!created){MessageBox.Show("MooT уже работает. Откройте его через значок возле часов.","MooT");return;}
        Application.Run(new AgentWindow());
    }    
}
