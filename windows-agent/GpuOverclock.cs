using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace MooT.Agent;
internal record GpuState(string Id,string Name,int Offset,int Min,int Max,uint Temperature);
internal interface IGpu : IDisposable { GpuState Read();void Set(int offset); }
internal sealed class NvidiaGpu : IGpu
{
    private IntPtr device;
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlShutdown();
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index,out IntPtr device);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetName(IntPtr device,StringBuilder name,uint length);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetUUID(IntPtr device,StringBuilder name,uint length);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetGpcClkVfOffset(IntPtr device,out int offset);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetGpcClkMinMaxVfOffset(IntPtr device,out int min,out int max);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceSetGpcClkVfOffset(IntPtr device,int offset);
    [DllImport("nvml.dll",CallingConvention=CallingConvention.Cdecl)] private static extern int nvmlDeviceGetTemperature(IntPtr device,uint sensor,out uint temperature);
    private static void Check(int status){if(status!=0)throw new InvalidOperationException(status switch{3=>"Видеокарта или драйвер не поддерживает эту функцию через NVML. Повышение прав не добавляет поддержку.",4=>"Недостаточно прав для операции NVIDIA. Проверьте запуск приложения от администратора.",9=>"Драйвер NVIDIA не загружен.",_=>"Ошибка модуля NVIDIA NVML: "+status});}
    public NvidiaGpu()
    {
        Check(nvmlInit_v2());try{Check(nvmlDeviceGetCount_v2(out var count));if(count!=1)throw new InvalidOperationException("Для этого модуля требуется одна совместимая NVIDIA GPU.");Check(nvmlDeviceGetHandleByIndex_v2(0,out device));}catch{nvmlShutdown();throw;}
    }
    public GpuState Read()
    {
        var name=new StringBuilder(96);var id=new StringBuilder(96);Check(nvmlDeviceGetName(device,name,96));Check(nvmlDeviceGetUUID(device,id,96));
        Check(nvmlDeviceGetGpcClkVfOffset(device,out var offset));Check(nvmlDeviceGetGpcClkMinMaxVfOffset(device,out var min,out var max));Check(nvmlDeviceGetTemperature(device,0,out var temp));
        return new(id.ToString(),name.ToString(),offset,min,max,temp);
    }
    public void Set(int offset)=>Check(nvmlDeviceSetGpcClkVfOffset(device,offset));
    public void Dispose()=>nvmlShutdown();
}
internal sealed class GpuOverclock
{
    private record Saved(string Id,string Device,int Original,int Applied);
    private readonly object sync=new();
    private readonly Func<IGpu> factory;private readonly string path;
    internal GpuOverclock(Func<IGpu> factory,string path){this.factory=factory;this.path=path;}
    internal bool HasActive=>File.Exists(path);
    internal string Preview()
    {
        using var gpu=factory();var s=gpu.Read();Validate(s);
        return s.Name+": пробное смещение частоты ядра +15 МГц. Напряжение и память не меняются. При 80 °C приложение попробует вернуть исходное значение. Возможны сбой драйвера или игры; стабильность под нагрузкой не гарантируется. Применить?";
    }
    private static void Validate(GpuState s)
    {
        if(s.Offset!=0)throw new InvalidOperationException("Уже установлен сторонний профиль частоты. MooT не будет изменять его.");
        if(s.Min>0||s.Max<15||s.Temperature>=75)throw new InvalidOperationException("Доступный диапазон или текущая температура не подходят для пробного профиля.");
    }
    internal object Apply(){lock(sync)return ApplyCore();}
    private object ApplyCore()
    {
        if(HasActive)throw new InvalidOperationException("Сначала восстановите предыдущий профиль GPU.");
        using var gpu=factory();var s=gpu.Read();Validate(s);
        var saved=new Saved(Guid.NewGuid().ToString("N"),s.Id,s.Offset,15);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(saved));
        try{gpu.Set(15);if(gpu.Read().Offset!=15)throw new InvalidOperationException("Драйвер не подтвердил профиль.");}
        catch{try{Restore();}catch{throw new InvalidOperationException("Изменение GPU прервано; требуется восстановление профиля в приложении.");}throw;}
        return new{status="complete",snapshotId=saved.Id,summary="Применён пробный профиль NVIDIA: ядро +15 МГц, без изменения напряжения и памяти. Стабильность под нагрузкой не проверена. Доступен откат; приложение следит за температурой."};
    }
    internal object Restore(){lock(sync)return RestoreCore();}
    private object RestoreCore()
    {
        if(!HasActive)throw new InvalidOperationException("Нет сохранённого профиля GPU.");
        var saved=JsonSerializer.Deserialize<Saved>(File.ReadAllText(path))??throw new InvalidOperationException("Некорректный снимок GPU.");
        using var gpu=factory();if(gpu.Read().Id!=saved.Device)throw new InvalidOperationException("Снимок относится к другой видеокарте.");
        gpu.Set(saved.Original);if(gpu.Read().Offset!=saved.Original)throw new InvalidOperationException("Драйвер не подтвердил восстановление GPU.");File.Delete(path);
        return new{status="complete",snapshotId=saved.Id,summary="Исходное смещение частоты GPU восстановлено."};
    }
    internal void Guard(){lock(sync){if(!HasActive)return;using var gpu=factory();if(gpu.Read().Temperature>=80)RestoreCore();}}
    internal static object Capability()
    {
        try{using var gpu=new NvidiaGpu();var s=gpu.Read();return new{supported=true,name=s.Name,temperature=s.Temperature,offset=s.Offset,voltageSupported=false};}
        catch(Exception e){return new{supported=false,reason=e is InvalidOperationException?e.Message:"Совместимый модуль NVIDIA NVML недоступен.",voltageSupported=false};}
    }
    private sealed class Fake:IGpu
    {
        public int Offset;public uint Temperature=50;public bool Reject;
        public GpuState Read()=>new("synthetic","Synthetic GPU",Offset,-100,100,Temperature);
        public void Set(int value){if(Reject&&value==15)throw new InvalidOperationException();Offset=value;}
        public void Dispose(){}
    }
    internal static string SelfTest()
    {
        var folder=Path.Combine(Path.GetTempPath(),"MooT-GPU-test-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(folder,"active.json");var fake=new Fake();var o=new GpuOverclock(()=>fake,path);int checks=0;
        void Check(bool v){if(!v)throw new Exception("GPU self-test failed");checks++;}
        try{o.Apply();Check(fake.Offset==15);Check(o.HasActive);o.Restore();Check(fake.Offset==0);Check(!o.HasActive);fake.Temperature=80;try{o.Apply();throw new Exception();}catch(InvalidOperationException){checks++;}fake.Temperature=50;fake.Reject=true;try{o.Apply();throw new Exception();}catch(InvalidOperationException){checks++;}Check(fake.Offset==0&&!o.HasActive);fake.Reject=false;o.Apply();fake.Temperature=81;o.Guard();Check(fake.Offset==0&&!o.HasActive);return JsonSerializer.Serialize(new{ok=true,checks,systemChanges=false});}
        finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
