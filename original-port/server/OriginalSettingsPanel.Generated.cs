// SPDX-License-Identifier: GPL-3.0-or-later
// Original DrawPanel/settings code; only deleted features and host coupling adjusted.
using System;using System.Linq;using System.IO;using System.Collections.Generic;using System.Collections.Concurrent;using System.Threading;using System.Threading.Tasks;using System.Text.RegularExpressions;using System.Text.Json;using System.Numerics;using ImGuiNET;using ChatEntry=OriginalDesktop.PhoneChatCore.ChatEntry;using PushRouter=OriginalDesktop.PhoneChatCore.PushRouter;using PushResult=OriginalDesktop.PhoneChatCore.PushResult;
namespace Trial;
partial class OriginalSettingsPanel {    private string qqStatus="QQ 未连接";

    private PhoneQr qqLoginQr;

    private PhoneNapCat qqBridge;

    private readonly object qqGate=new();

    private long qqMirrorCursor;
private bool qqMirrorReady;

    private DateTimeOffset qqMirrorSince;

    private readonly ConcurrentQueue<(uint group,string text,int generation)> qqMirrorQueue=new();

    private int qqMirrorWorker,qqLoginRunning,qqLoginGeneration;

    private Task qqCleanup=Task.CompletedTask;

    private readonly HashSet<string> qqMirrored=new();
private readonly Queue<string> qqMirroredOrder=new();

    private DateTime qqLastReply;

    private void DrawPageNavigation(){
        
        if(!Header("页面导航编辑"))return;
        ImGui.TextWrapped("勾选启用页面，使用上移/下移排列导航。保存后自动同步至手机；手机端不可编辑。");
        var order=NormalizePages(settings.PageOrder).ToList();string move="";int direction=0;
        if(ImGui.BeginTable("页面导航项目",4,ImGuiTableFlags.SizingStretchProp)){
            ImGui.TableSetupColumn("启用",ImGuiTableColumnFlags.WidthFixed,48);ImGui.TableSetupColumn("页面");ImGui.TableSetupColumn("上移",ImGuiTableColumnFlags.WidthFixed,60);ImGui.TableSetupColumn("下移",ImGuiTableColumnFlags.WidthFixed,60);
            for(int i=0;i<order.Count;i++){string id=order[i];ImGui.PushID(id);ImGui.TableNextRow();ImGui.TableNextColumn();bool on=PageEnabled(id);bool available=true;ImGui.BeginDisabled(!available);
                if(ImGui.Checkbox("##启用",ref on)){lock(gate){settings.DisabledPages??=new();settings.DisabledPages.RemoveAll(x=>x==id);if(!on)settings.DisabledPages.Add(id);}NavigationChanged();}
                ImGui.EndDisabled();ImGui.TableNextColumn();ImGui.TextUnformatted(PageNames[Array.IndexOf(PageKeys,id)]);                ImGui.TableNextColumn();ImGui.BeginDisabled(i==0);if(ImGui.Button("上移")){move=id;direction=-1;}ImGui.EndDisabled();
                ImGui.TableNextColumn();ImGui.BeginDisabled(i==order.Count-1);if(ImGui.Button("下移")){move=id;direction=1;}ImGui.EndDisabled();ImGui.PopID();
            }ImGui.EndTable();
        }
        if(move.Length>0){int i=order.IndexOf(move);(order[i],order[i+direction])=(order[i+direction],order[i]);lock(gate)settings.PageOrder=order;NavigationChanged();}
        if(ImGui.Button("恢复默认导航")){lock(gate){settings.PageOrder=PageKeys.ToList();settings.DisabledPages=new();}NavigationChanged();}
    }

    private void DrawStreamAdapterSettings(){
        if(streamAdapters==null){try{streamAdapters=StreamAdapters.List();streamAdapterError="";}catch(Exception e){streamAdapters=Array.Empty<StreamAdapters.Adapter>();streamAdapterError=e.Message;}}
        bool gpu=settings.StreamGpuDirect;if(ImGui.Checkbox("GPU直通采集编码（推荐）",ref gpu)){settings.StreamGpuDirect=gpu;Save();}int transportMode=settings.StreamTransport=="udp"?1:0;if(ImGui.Combo("串流模式",ref transportMode,"TCP\0UDP\0")){settings.StreamTransport=transportMode==1?"udp":"tcp";Save();}if(settings.StreamTransport=="udp"){int udpPort=settings.StreamUdpPort;if(ImGui.InputInt("UDP端口",ref udpPort)&&(udpPort==0||udpPort>=1024&&udpPort<=65535)){settings.StreamUdpPort=udpPort;Save();}int externalUdp=settings.StreamUdpExternalPort;if(ImGui.InputInt("UDP外部端口（0沿用）",ref externalUdp)&&externalUdp>=0&&externalUdp<=65535){settings.StreamUdpExternalPort=externalUdp;Save();}}
        bool bring=settings.StreamBringToFront;if(ImGui.Checkbox("开始串流时自动将游戏置于前台",ref bring)){settings.StreamBringToFront=bring;Save();}
        bool enabled=settings.HardwareStream;if(ImGui.Checkbox("启用 H.264 硬件串流",ref enabled)){settings.HardwareStream=enabled;Save();}
        ImGui.TextUnformatted("硬件串流组件");string helper=settings.StreamHardwareHelper;ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##硬件串流组件路径","使用包内组件，也可输入本地完整路径",ref helper,1024)){settings.StreamHardwareHelper=helper;Save();}

        ImGui.TextUnformatted("FFmpeg 编码器");string encoder=settings.StreamEncoderPath;ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##FFmpeg路径","使用包内组件，也可输入本地完整路径",ref encoder,1024)){settings.StreamEncoderPath=encoder;Save();}
        ImGui.BeginDisabled(Volatile.Read(ref streamDownloadRunning)!=0);if(ImGui.Button("校验包内组件"))StartStreamDownload();ImGui.EndDisabled();if(Volatile.Read(ref streamDownloadRunning)!=0){ImGui.SameLine();if(ImGui.Button("取消准备"))CancelStreamDownload();}if(streamDownloadStatus.Length>0)ImGui.TextWrapped(streamDownloadStatus);

        string selected=settings.StreamAdapter.Length==0?"自动选择硬件显卡":streamAdapters.FirstOrDefault(a=>a.Id==settings.StreamAdapter)?.Name??"所选显卡已不可用";
        ImGui.SetNextItemWidth(Math.Max(80,ImGui.GetContentRegionAvail().X-ImGui.CalcTextSize("串流显卡").X-ImGui.CalcTextSize("刷新显卡列表").X-55));if(ImGui.BeginCombo("串流显卡",selected)){if(ImGui.Selectable("自动选择硬件显卡",settings.StreamAdapter.Length==0)){settings.StreamAdapter="";Save();}foreach(var a in streamAdapters){if(ImGui.Selectable(a.Name+" · "+a.Encoder+"###"+a.Id,settings.StreamAdapter==a.Id)){settings.StreamAdapter=a.Id;Save();}}ImGui.EndCombo();}
        ImGui.SameLine();if(ImGui.Button("刷新显卡列表"))streamAdapters=null;
        if(streamAdapterError.Length>0)ImGui.TextWrapped(streamAdapterError);
    }

    private void DrawPanel()
    {
        if(!panel)return;
        ImGui.SetNextWindowSize(new Vector2(620,700),ImGuiCond.FirstUseEver);
        bool visible=ImGui.Begin("狒狒食柿《服务端》###PhoneChat",ref panel);
        try
        {
            if(!visible)return;DrawDesktopConnection();if(!string.IsNullOrWhiteSpace(status))ImGui.TextWrapped(status);
            if(Header("连接端口与服务")){if(ImGui.BeginTable("端口",2,ImGuiTableFlags.SizingStretchSame)){
                ImGui.TableNextColumn();ImGui.TextUnformatted("内部端口");ImGui.SameLine();ImGui.SetNextItemWidth(-1);if(portInput.Length==0)portInput=settings.Port.ToString();
                if(ImGui.InputTextWithHint("##内部端口","内部端口 · 18765",ref portInput,6,ImGuiInputTextFlags.CharsDecimal|ImGuiInputTextFlags.EnterReturnsTrue)&&int.TryParse(portInput,out var inner)&&inner>=1024&&inner<=65535){settings.Port=inner;Save();StartServer();}
                if(ImGui.IsItemHovered())ImGui.SetTooltip("内部端口：按回车应用并重启监听；路由器转发到此端口");
                ImGui.TableNextColumn();ImGui.TextUnformatted("外部端口");ImGui.SameLine();ImGui.SetNextItemWidth(-1);if(externalPortInput.Length==0)externalPortInput=settings.ExternalPort.ToString();
                if(ImGui.InputTextWithHint("##外部端口","外部端口 · 18765",ref externalPortInput,6,ImGuiInputTextFlags.CharsDecimal|ImGuiInputTextFlags.EnterReturnsTrue)&&int.TryParse(externalPortInput,out var outer)&&outer>0&&outer<=65535){settings.ExternalPort=outer;Save();}
                if(ImGui.IsItemHovered())ImGui.SetTooltip("外部端口：须与路由器及手机填写的端口一致；不能替你修改路由器");ImGui.EndTable();
            }
            if(ImGui.Button("重启服务"))StartServer();ImGui.SameLine();if(ImGui.Button("停止服务"))StopServer();}
            DeviceRequest[] requests;AuthorizedDevice[] devices;lock(gate){requests=deviceRequests.Values.Where(r=>r.expires>DateTime.UtcNow&&r.token.Length==0).ToArray();devices=authorizedDevices.ToArray();}
            if(Header("设备授权",ImGuiTreeNodeFlags.DefaultOpen)){
                ImGui.SetNextItemWidth(130);ImGui.InputTextWithHint("##手机验证码","手机六位验证码",ref approvalCode,7,ImGuiInputTextFlags.CharsDecimal);ImGui.SameLine();ImGui.SetNextItemWidth(180);ImGui.InputTextWithHint("##授权备注","设备备注（选填）",ref approvalRemark,64);
                foreach(var device in requests){ImGui.PushID(device.id);ImGui.TextUnformatted(device.name);ImGui.SameLine();ImGui.BeginDisabled(!Equal(approvalCode,device.code));if(ImGui.Button("授权")){ApproveDevice(device.id,approvalCode);approvalCode=approvalRemark="";}ImGui.EndDisabled();ImGui.SameLine();if(ImGui.Button("拒绝")){app.Pairing.Reject(device.id);}ImGui.PopID();}
                foreach(var device in devices){ImGui.PushID(device.id);string remark=device.remark;ImGui.SetNextItemWidth(180);if(ImGui.InputTextWithHint("##设备备注",device.name,ref remark,64)){lock(gate){device.remark=remark;PersistPairingFile();PersistGameState();}}ImGui.SameLine();ImGui.TextUnformatted(device.lastSeen==default?"未连接":device.lastSeen.ToLocalTime().ToString("MM-dd HH:mm"));ImGui.SameLine();if(ImGui.Button("删除"))RevokeDevice(device.id);ImGui.PopID();}
            }
            ImGui.Separator();
            if(Header("提醒与操作权限")){if(ImGui.BeginTable("提醒选项",3,ImGuiTableFlags.SizingStretchSame)){
            ImGui.TableNextColumn();var tell=settings.ReceiveTell;if(ImGui.Checkbox("悄悄话",ref tell)){settings.ReceiveTell=tell;Save();}
            ImGui.TableNextColumn();var party=settings.NotifyParty;if(ImGui.Checkbox("组队邀请",ref party)){settings.NotifyParty=party;Save();}
            ImGui.TableNextColumn();var trade=settings.NotifyTrade;if(ImGui.Checkbox("交易申请",ref trade)){settings.NotifyTrade=trade;Save();}
            ImGui.TableNextColumn();var friend=settings.NotifyFriend;if(ImGui.Checkbox("好友请求",ref friend)){settings.NotifyFriend=friend;Save();}
            ImGui.TableNextColumn();var target=settings.NotifyTarget;if(ImGui.Checkbox("被玩家选中",ref target)){settings.NotifyTarget=target;Save();}
            ImGui.TableNextColumn();var macro=settings.AllowCommands;if(ImGui.Checkbox("允许手机宏指令",ref macro)){settings.AllowCommands=macro;Save();}
            ImGui.EndTable();}}
            ImGui.Separator();
            if(Header("离线推送")){
            int provider=settings.PushProvider;ImGui.SetNextItemWidth(220);if(ImGui.Combo("推送服务",ref provider,"不使用\0WxPusher\0Server酱\0")){settings.PushProvider=provider;Save();}
            ImGui.SameLine();ImGui.BeginDisabled(provider==0);if(ImGui.Button("设置##离线推送"))pushPanel=provider;ImGui.EndDisabled();
            if(pushStatus.Length>0)ImGui.TextWrapped(pushStatus);}
            if(Header("QQ 提醒")){
            bool qqNotify=settings.QQNotifications;if(ImGui.Checkbox("启用 QQ 提醒（可与离线推送同时使用）",ref qqNotify)){settings.QQNotifications=qqNotify;Save();}
            ImGui.TextWrapped(qqStatus);if(ImGui.Button("个人 QQ 设置"))pushPanel=3;
            }
            if(Header("鱼糕服务端提醒")){int count;lock(fishGate)count=fishSchedule.plans.Values.Where(p=>p.enabled).Sum(p=>p.rows.Count);ImGui.TextUnformatted("待发送计划："+count+" 条");ImGui.TextWrapped(fishStatus);ImGui.TextWrapped("在手机鱼糕中选择目标鱼并开启提醒，计划自动同步到此服务端。App / QQ / 推送方式在手机“手机提醒”中选择；QQ与推送凭证沿用本模块设置。服务端须保持运行。");}
            DrawPageNavigation();
            DrawPushSettings();
            if(Header("串流设置"))DrawStreamAdapterSettings();
        }
        finally{ImGui.End();}
    }

    private static uint[] QQIds(string input)=>Regex.Split(input??"",@"[,，;；\s]+").Select(x=>uint.TryParse(x,out var id)?id:0).Where(id=>id>0).Distinct().Take(32).ToArray();

    private bool QQOnline(){lock(qqGate)return qqBridge?.Online==true;}

    private static string QQLoginError(Exception exception,string token)
    {
        string detail=exception.GetBaseException().Message;
        if(!string.IsNullOrEmpty(token))detail=detail.Replace(token,"[授权凭证]");
        detail=Regex.Replace(detail,@"https?://\S+","[服务地址]");
        return "QQ 登录失败："+(detail.Length>220?detail[..220]+"…":detail);
    }

    private void PrepareNapCat()
    {
        if(!enabled){qqStatus="请先启用狒狒食柿《服务端》";return;}
        if(Interlocked.CompareExchange(ref qqLoginRunning,1,0)!=0)return;
        int generation=Volatile.Read(ref qqLoginGeneration);
        qqStatus="正在准备 QQ 服务…";
        _=Task.Run(async()=>{try{await PhoneNapCat.Prepare(NapCatDirectory,text=>{if(enabled&&Volatile.Read(ref qqLoginGeneration)==generation)qqStatus=text;},CancellationToken.None);}catch(Exception e){if(enabled&&Volatile.Read(ref qqLoginGeneration)==generation)qqStatus=QQLoginError(e,"");}finally{Interlocked.Exchange(ref qqLoginRunning,0);}});
    }

    private void ConnectQQ(bool scan)
    {
        if(!enabled){qqStatus="请先启用狒狒食柿《服务端》，再登录 QQ";return;}
        if(!PhoneNapCat.ValidAccount(settings.QQBotAccount)){qqStatus="机器人 QQ 号格式不正确";return;}
        if(Interlocked.CompareExchange(ref qqLoginRunning,1,0)!=0){qqStatus="QQ 正在准备登录，请稍候";return;}
        int generation=Volatile.Read(ref qqLoginGeneration);string token="",bot=settings.QQBotAccount;
        qqStatus="正在启动 QQ 服务…";
        _=Task.Run(async()=>{PhoneNapCat current=null;try{
            await qqCleanup;
            if(!enabled||Volatile.Read(ref qqLoginGeneration)!=generation)return;
            DisconnectQQ("正在启动 QQ 服务…",false);await qqCleanup;
            bool Active()=>enabled&&Volatile.Read(ref qqLoginGeneration)==generation&&ReferenceEquals(qqBridge,current);
            current=new PhoneNapCat(NapCatDirectory,text=>{if(Active())qqStatus=text;},(url,bytes)=>{
                if(!Active())return;Directory.CreateDirectory(Path.GetDirectoryName(QrFile));File.WriteAllBytes(QrFile,bytes);
                qqLoginQr=PhoneQr.EncodeText(url,0);
            },(group,user,name,text)=>{if(Active())ReceiveQQ(group,user,name,text);},id=>{
                if(Active()&&settings.QQBotAccount!=id&&framework!=null)_=framework.RunOnTick(()=>{if(Active()){settings.QQBotAccount=id;Save();}});
            });
            lock(qqGate){if(!enabled||Volatile.Read(ref qqLoginGeneration)!=generation){current.Dispose();return;}qqBridge=current;}
            // Manual login explicitly prepares the pinned runtime; auto-restore never downloads or updates code.
            await current.Start(bot,scan,scan);
        }catch(OperationCanceledException){if(enabled&&Volatile.Read(ref qqLoginGeneration)==generation)DisconnectQQ("QQ 登录已取消",false);}
        catch(Exception e){if(enabled&&Volatile.Read(ref qqLoginGeneration)==generation){var message=QQLoginError(e,token);DisconnectQQ(message,false);}}
        finally{Interlocked.Exchange(ref qqLoginRunning,0);}});
    }

    private void DisconnectQQ(string finalStatus="QQ 已断开",bool cancelLogin=true)
    {
        if(cancelLogin)Interlocked.Increment(ref qqLoginGeneration);
        qqMirrorReady=false;PhoneNapCat old;lock(qqGate){old=qqBridge;qqBridge=null;qqLoginQr=null;qqStatus=finalStatus;while(qqMirrorQueue.TryDequeue(out _)){}}
        if(old!=null)qqCleanup=Task.Run(old.Dispose);
        try{if(File.Exists(QrFile))File.Delete(QrFile);}catch{}
    }

    private async Task<PushResult> SendQQPush(string target,string text,CancellationToken token)
    {if(!uint.TryParse(target,out var id)||id==0)return new PushResult{status="填写接收 QQ 号或群号"};return await SendQQMessage(id,settings.QQNotifyGroup,text,settings.QQMention,token);}

    private async Task<PushResult> SendQQMessage(uint id,bool group,string text,string mention,CancellationToken token)
    {
        PhoneNapCat instance;lock(qqGate)instance=qqBridge;if(instance==null)return new PushResult{status="QQ 未登录"};
        uint.TryParse(mention,out var at);
        try{var error=await instance.Send(id,group,text,at,token);return new PushResult{ok=error.Length==0,status=error};}
        catch(OperationCanceledException){throw;}catch{return new PushResult{status="QQ 发送失败"};}
    }

    private void ReceiveQQ(uint group,uint user,string name,string text)
    {
        if(!settings.QQReply||!QQIds(settings.QQReplyUsers).Contains(user)||group>0&&!QQIds(settings.QQGroups).Contains(group)||!text.StartsWith('#')||text.Length>450)return;
        string content=text[1..].Trim();if(content.Length==0||content.StartsWith('/')||content.Contains('\n')||content.Contains('\r'))return;
        lock(qqGate){if(DateTime.UtcNow-qqLastReply<TimeSpan.FromSeconds(2))return;qqLastReply=DateTime.UtcNow;}
        int replyGeneration=Volatile.Read(ref qqLoginGeneration);long replyQueuedAt=Environment.TickCount64;string channel=settings.QQReplyChannel;
        if(!Commands.ContainsKey(channel)||framework==null)return;
        _=framework.RunOnFrameworkThread(async()=>{if(!enabled||!settings.QQReply||replyGeneration!=Volatile.Read(ref qqLoginGeneration)||!QQOnline()||Environment.TickCount64-replyQueuedAt>5000)return;try{await Send(channel,"",content);}catch{qqStatus="QQ 回复未发送，请检查游戏频道";}});
    }

    private void QueueQQEvent(string channel,string sender,string text,DateTimeOffset sentAt){if(!enabled||!qqMirrorReady||!settings.QQMirror||!QQOnline()||!settings.QQChannels.Contains(channel)||sentAt<qqMirrorSince||string.IsNullOrWhiteSpace(text))return;int generation=Volatile.Read(ref qqLoginGeneration);foreach(var group in QQIds(settings.QQGroups)){if(qqMirrorQueue.Count>=64){qqStatus="QQ 同步队列已满";return;}qqMirrorQueue.Enqueue((group,"["+channel+"]"+(sender.Length>0?sender+"：":"")+text,generation));}}

    private void MirrorQQ(){bool available=enabled&&settings.QQMirror&&settings.QQChannels.Count>0&&QQOnline();if(!available){qqMirrorReady=false;while(qqMirrorQueue.TryDequeue(out _)){}return;}if(!qqMirrorReady){qqMirrorSince=DateTimeOffset.UtcNow;qqMirrorReady=true;while(qqMirrorQueue.TryDequeue(out _)){}return;}if(qqMirrorQueue.IsEmpty||Interlocked.CompareExchange(ref qqMirrorWorker,1,0)!=0)return;_=Task.Run(async()=>{try{while(enabled&&qqMirrorQueue.TryDequeue(out var job)){if(job.generation!=Volatile.Read(ref qqLoginGeneration)||!settings.QQMirror||!QQOnline())continue;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));var result=await SendQQMessage(job.group,true,job.text,"",deadline.Token);if(!result.ok)qqStatus=result.status;await Task.Delay(1300);}}catch{qqStatus="QQ 同步失败";}finally{Interlocked.Exchange(ref qqMirrorWorker,0);}});}

    private void DrawQQSettings()
    {
        ImGui.TextWrapped(qqStatus);
        string bot=settings.QQBotAccount;ImGui.SetNextItemWidth(180);if(ImGui.InputTextWithHint("##机器人QQ","机器人 QQ 号（选填）",ref bot,12,ImGuiInputTextFlags.CharsDecimal)){settings.QQBotAccount=bot;Save();}
        ImGui.TextDisabled("请用作为机器人的 QQ 账号扫描登录码");
        ImGui.BeginDisabled(Volatile.Read(ref qqLoginRunning)!=0);
        if(ImGui.Button("准备 QQ 服务"))PrepareNapCat();if(ImGui.IsItemHovered())ImGui.SetTooltip("使用免安装包内的官方 NapCat 组件，校验 SHA-256 后本地解压；QQ 数据单独保存");
        ImGui.SameLine();if(ImGui.Button("机器人 QQ 扫码登录"))ConnectQQ(true);
        ImGui.SameLine();if(ImGui.Button("恢复登录"))ConnectQQ(false);ImGui.EndDisabled();
        if(ImGui.Button("断开 QQ"))DisconnectQQ();ImGui.SameLine();if(ImGui.Button("查看登录码")){if(File.Exists(QrFile))OpenUrl(QrFile);else qqStatus="暂无登录码，请先点击机器人 QQ 扫码登录";}
        var loginCode=qqLoginQr;if(loginCode!=null&&!QQOnline())DrawQr(loginCode,Math.Min(260,ImGui.GetContentRegionAvail().X));
        bool auto=settings.QQAutoConnect;if(ImGui.Checkbox("随模块恢复 QQ",ref auto)){settings.QQAutoConnect=auto;Save();}
        string target=settings.QQNotifyTarget;ImGui.SetNextItemWidth(210);if(ImGui.InputTextWithHint("##QQ接收","提醒接收 QQ 号 / 群号",ref target,12,ImGuiInputTextFlags.CharsDecimal)){settings.QQNotifyTarget=target;Save();}ImGui.SameLine();bool group=settings.QQNotifyGroup;if(ImGui.Checkbox("发到群",ref group)){settings.QQNotifyGroup=group;Save();}
        string at=settings.QQMention;ImGui.SetNextItemWidth(210);if(ImGui.InputTextWithHint("##QQ提醒对象","群提醒 @QQ（选填）",ref at,12,ImGuiInputTextFlags.CharsDecimal)){settings.QQMention=at;Save();}
        if(ImGui.Button("测试个人 QQ"))pushRouter?.Enqueue(new ChatEntry{channel="推送测试",sender="小烟酒",text="QQ 测试通知"},3);
        bool mirror=settings.QQMirror;if(ImGui.Checkbox("游戏聊天同步到 QQ 群",ref mirror)){settings.QQMirror=mirror;qqMirrorReady=false;while(qqMirrorQueue.TryDequeue(out _)){}qqMirrorCursor=entries.LastId;qqMirrorSince=DateTimeOffset.UtcNow;Save();}
        string groups=settings.QQGroups;ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##QQ同步群","同步群号，多个用逗号分隔",ref groups,500)){settings.QQGroups=groups;Save();}
        ImGui.TextDisabled("仅同步连接后的实时新消息；默认不选择频道，重启不补发旧记录");
        if(ImGui.TreeNode("同步频道")){foreach(string channel in AllChannels.Concat(new[]{"提醒"})){bool selected=settings.QQChannels.Contains(channel);if(ImGui.Checkbox(channel+"##QQ频道",ref selected)){if(selected)settings.QQChannels.Add(channel);else settings.QQChannels.Remove(channel);Save();}}ImGui.TreePop();}
        bool reply=settings.QQReply;if(ImGui.Checkbox("QQ 回复游戏（以 # 开头）",ref reply)){settings.QQReply=reply;Save();}
        string users=settings.QQReplyUsers;ImGui.SetNextItemWidth(-1);if(ImGui.InputTextWithHint("##QQ回复白名单","允许回复的 QQ 号，多个用逗号分隔",ref users,500)){settings.QQReplyUsers=users;Save();}
        string[] channels=Commands.Keys.ToArray();int selectedChannel=Math.Max(0,Array.IndexOf(channels,settings.QQReplyChannel));ImGui.SetNextItemWidth(160);if(ImGui.Combo("回复频道",ref selectedChannel,string.Join('\0',channels)+"\0")){settings.QQReplyChannel=channels[selectedChannel];Save();}
        if(ImGui.TreeNode("协议与许可证")){ImGui.TextWrapped("NapCat 4.18.38 · 本机后台服务，包含 QQ 运行组件。模块独立实现本地 API 适配，不内嵌或修改 NapCat 源码，不重新分发其运行包。上游采用特殊分发许可及各组件自身许可。");if(ImGui.Button("查看 NapCat 来源与许可"))OpenUrl("https://github.com/NapNeko/NapCatQQ");ImGui.TreePop();}
    }

    private static void OpenUrl(string url){try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}catch{}}

    private void DrawPushSettings()
    {
        if(pushPanel==0)return;bool open=true;ImGui.SetNextWindowSize(new Vector2(480,430),ImGuiCond.FirstUseEver);
        if(ImGui.Begin(PushRouter.Name(pushPanel)+" 设置###PhonePushConfig",ref open)){
            if(pushPanel==3)DrawQQSettings();else{
                ImGui.Checkbox("显示凭证",ref showPushKeys);var flags=showPushKeys?ImGuiInputTextFlags.None:ImGuiInputTextFlags.Password;
                ImGui.SetNextItemWidth(-1);if(pushPanel==1){if(ImGui.InputTextWithHint("##推送凭证","WxPusher SPT",ref wxPushKey,300,flags))SavePushKeys();}else if(ImGui.InputTextWithHint("##推送凭证","Server酱 SendKey",ref serverPushKey,300,flags))SavePushKeys();
                if(ImGui.Button("测试"))pushRouter?.Enqueue(new ChatEntry{channel="推送测试",sender="小烟酒",text="测试通知"},pushPanel);ImGui.SameLine();if(ImGui.Button("获取凭证"))OpenUrl(pushPanel==1?"https://wxpusher.zjiecode.com/docs/spt.html":"https://sc3.ft07.com/");ImGui.TextWrapped(pushStatus);
            }
        }ImGui.End();if(!open)pushPanel=0;
    }


    private static void DrawQr(PhoneQr code,float size)
    {
        var start=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();float cell=size/(code.Size+8);
        draw.AddRectFilled(start,start+new Vector2(size),0xffffffff);
        for(int y=0;y<code.Size;y++)for(int x=0;x<code.Size;x++)if(code.GetModule(x,y))
        {var point=start+new Vector2((x+4)*cell,(y+4)*cell);draw.AddRectFilled(point,point+new Vector2(cell+0.1f),0xff000000);}
        ImGui.Dummy(new Vector2(size));
    }
}

