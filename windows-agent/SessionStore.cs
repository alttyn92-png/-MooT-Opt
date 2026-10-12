using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MooT.Agent;
internal record SavedSession(string Token,string User,bool Granted,string Server,int Version=0);
internal static class SessionStore
{
    private static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MooT");
    private static readonly string FilePath=Path.Combine(Folder,"session.protected");
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length;public IntPtr Data; }
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input,string? description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool CryptUnprotectData(ref Blob input,IntPtr description,IntPtr entropy,IntPtr reserved,IntPtr prompt,uint flags,out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    private static byte[] Transform(byte[] value,bool protect)
    {
        var input=new Blob{Length=value.Length,Data=Marshal.AllocHGlobal(value.Length)};Blob output=default;
        try
        {
            Marshal.Copy(value,0,input.Data,value.Length);
            var ok=protect?CryptProtectData(ref input,"MooT device session",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);
            if(!ok)throw new InvalidOperationException("Не удалось защитить сессию Windows.");
            var result=new byte[output.Length];Marshal.Copy(output.Data,result,0,result.Length);return result;
        }
        finally{Marshal.FreeHGlobal(input.Data);if(output.Data!=IntPtr.Zero)LocalFree(output.Data);}
    }
    public static void Save(SavedSession session)
    {
        Directory.CreateDirectory(Folder);File.WriteAllBytes(FilePath,Transform(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session)),true));
    }
    public static SavedSession? Load()
    {
        try{return File.Exists(FilePath)?JsonSerializer.Deserialize<SavedSession>(Encoding.UTF8.GetString(Transform(File.ReadAllBytes(FilePath),false))):null;}catch{return null;}
    }
    public static void Clear(){if(File.Exists(FilePath))File.Delete(FilePath);}
}
