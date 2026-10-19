using System.Text.Json;
namespace OriginalDesktop;
internal partial class PhoneChatCore
{
    internal static int CheckOriginalBusiness()
    {
        int count=0; void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;}
        string folder=Path.Combine(Path.GetTempPath(),"ff14-trial-original-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"chat.db");
        using(var store=new DiskChatStore(path,false))
        {
            store.Add(new ChatEntry{id=1,channel="说话",sender="测试",text="原始聊天存储",time=DateTimeOffset.UtcNow.ToString("O"),recordSource="native"});
            Check(store.Count==1,"original chat store count");
            Check(store.After(0,100).Single().text=="原始聊天存储","original chat query");
        }
        using(var store=new DiskChatStore(path,false))Check(store.ById(1).sender=="测试","original storage persists");
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var raw=JsonSerializer.SerializeToElement(new[]{new{key="fish-1",title="钓鱼提醒",body="测试",at=now+1000,expires=now+60000}});
        var fish=new FishSchedule();fish.Replace("device-a",raw,true,1,now);
        Check(fish.TakeDue(now).Count==0,"original schedule not early");
        var due=fish.TakeDue(now+2000);Check(due.Count==1&&due[0].owner=="device-a","original schedule routes owner");
        Check(fish.TakeDue(now+3000).Count==0,"original schedule deduplicates");
        Check(PushRouter.ParseResponse(1,200,"{\"code\":1000}").ok,"original push response");
        Check(!PushRouter.ParseResponse(1,500,"{\"code\":1000}").ok,"original push rejects HTTP error");
        Check(PushRouter.Payload(1,new ChatEntry{channel="悄悄话",text="测试"}).Contains("xiaoyanjiu-trial"),"trial push link isolated");
        return count;
    }
}
