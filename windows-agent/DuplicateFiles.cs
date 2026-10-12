using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;
namespace MooT.Agent;

internal static class DuplicateFiles
{
    internal record Copy(string Path,string Keep,string Hash);
    internal static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    internal static List<Copy> Scan(string folder,Func<string,bool> allowed,Func<bool> expired)
    {
        var result=new List<Copy>();long read=0;
        var files=Directory.EnumerateFiles(folder).Take(501).Where(allowed).Select(p=>new FileInfo(p)).Where(f=>f.Length<=256L*1024*1024).ToArray();
        foreach(var group in files.GroupBy(f=>f.Length).Where(g=>g.Count()>1))
        {
            var seen=new Dictionary<string,string>();
            foreach(var file in group.OrderBy(f=>f.Name,StringComparer.OrdinalIgnoreCase))
            {
                if(expired()||read+file.Length>2L*1024*1024*1024)return result;
                try{read+=file.Length;var hash=Hash(file.FullName);if(seen.TryGetValue(hash,out var keep))result.Add(new(file.FullName,keep,hash));else seen[hash]=file.FullName;}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
        return result;
    }
    internal static async Task<object> Review(Form owner,string location,Func<bool> expired)
    {
        var folder=location switch{"downloads"=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads"),"documents"=>Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"desktop"=>Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),_=>throw new InvalidOperationException("Неизвестная личная папка.")};
        var copies=await Task.Run(()=>Scan(folder,UserMaintenance.UserFile,expired));
        if(expired())return Result(false,"Срок команды истёк. Файлы не менялись.");
        if(copies.Count==0)return Result(true,"В проверенной части папки одинаковых файлов нет. Проверяются файлы первого уровня: до 501 файла, до 256 МБ каждый и до 2 ГБ суммарного чтения. Вложенные папки не проверяются.");
        using var dialog=new Form{Text="MooT · Дубликаты — выберите копии для корзины",ClientSize=new Size(800,430),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false};
        var note=new Label{Dock=DockStyle.Top,Height=65,Text="Папка: "+folder+"\nОдинаковое содержимое проверено SHA-256. Одна копия каждой группы сохраняется.\nСканирование ограничено первым уровнем, 501 файлом, 256 МБ на файл и 2 ГБ чтения."};
        var list=new CheckedListBox{Dock=DockStyle.Fill,CheckOnClick=true,HorizontalScrollbar=true};
        foreach(var copy in copies)list.Items.Add(Path.GetFileName(copy.Path)+"  → сохранить: "+Path.GetFileName(copy.Keep),false);
        var accept=new Button{Text="Перенести выбранные в корзину",Dock=DockStyle.Bottom,Height=42,DialogResult=DialogResult.OK};
        dialog.Controls.Add(list);dialog.Controls.Add(note);dialog.Controls.Add(accept);
        if(dialog.ShowDialog(owner)!=DialogResult.OK||expired())return Result(false,"Работа с дубликатами отменена. Файлы не менялись.");
        var selected=list.CheckedIndices.Cast<int>().Select(i=>copies[i]).ToArray();
        if(selected.Length==0)return Result(true,"Файлы не выбраны. Ничего не удалено.");
        int done=0,skipped=0;
        foreach(var copy in selected)
        {
            if(expired()){skipped+=selected.Length-done-skipped;break;}
            try
            {
                // Recheck both copies immediately before recycling; never remove the keeper.
                if(!UserMaintenance.UserFile(copy.Path)||!UserMaintenance.UserFile(copy.Keep)||Hash(copy.Path)!=copy.Hash||Hash(copy.Keep)!=copy.Hash){skipped++;continue;}
                FileSystem.DeleteFile(copy.Path,UIOption.OnlyErrorDialogs,RecycleOption.SendToRecycleBin,UICancelOption.ThrowException);done++;
            }
            catch{skipped++;}
        }
        return Result(skipped==0,$"В корзину перемещено копий: {done}. Пропущено: {skipped}. По одному экземпляру каждой группы оставлено. Восстановление доступно через корзину Windows.");
    }
    private static object Result(bool ok,string summary)=>new{status=ok?"complete":"failed",snapshotId="",summary};
}
