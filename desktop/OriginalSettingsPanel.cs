using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClickableTransparentOverlay;
using ImGuiNET;
using Core=OriginalDesktop.PhoneChatCore;
using ChatEntry=OriginalDesktop.PhoneChatCore.ChatEntry;
using PushProfile=OriginalDesktop.PhoneChatCore.PushProfile;
using PushRouter=OriginalDesktop.PhoneChatCore.PushRouter;
namespace Trial;
// The desktop shell hosts the original module UI and original QQ/push implementations.
internal sealed partial class OriginalSettingsPanel:Overlay
{
 readonly AppState app;readonly bool testMode;readonly object gate=new();
 readonly ConcurrentQueue<Action> ui=new();readonly System.Threading.Timer services;
 readonly DesktopScheduler framework=new();
 Settings settings=>app.Settings;
 readonly HostEntries entries;
 string status="",portInput="",externalPortInput="",approvalCode="",approvalRemark="";
 bool panel=true,showPushKeys;int pushPanel;
 string wxPushKey="",serverPushKey="",pushStatus="";
 PushRouter? pushRouter;
 bool enabled=>!closing;
 object fishGate=>app.Fishing.Gate;
 Core.FishSchedule fishSchedule=>app.Fishing.Schedule;
 string fishStatus=>app.Fishing.Status;
 StreamAdapters.Adapter[]? streamAdapters;string streamAdapterError="",streamDownloadStatus="";int streamDownloadRunning;
 static string ConfigFile=>Path.Combine(Log.Root,"settings.json");
 static string QrFile=>Path.Combine(Log.Root,"PhoneChat-QQ-login.png");
 static string NapCatDirectory=>Path.Combine(Log.Root,"PhoneChat-NapCat");
 static string PushFile=>Path.Combine(Log.Root,"push.keys");
 static readonly Dictionary<string,string> Commands=GameLink.Commands;
 static readonly string[] AllChannels=Commands.Keys.Concat(new[]{"悄悄话","表情"}).Distinct().ToArray();
 public static readonly string[] PageKeys=["accounts","chat","recruit","character","party","stream","fishing","wiki","risingStones","settings"];
 public static readonly string[] PageNames=["账号设置","聊天消息","招募频道","角色信息","队伍信息","游戏画面","鱼糕","灰机Wiki","石之家","应用设置"];
 public static string[] NormalizePages(IEnumerable<string>? order)=>(order??Array.Empty<string>()).Where(PageKeys.Contains).Distinct().Concat(PageKeys).Distinct().ToArray();
 bool PageEnabled(string id)=>!settings.DisabledPages.Contains(id);
 void NavigationChanged(){Save();if(!PageEnabled("stream"))app.Capture.Stop();}
 List<AuthorizedDevice> authorizedDevices=new();Dictionary<string,DeviceRequest> deviceRequests=new();
 sealed class DeviceRequest {public string id="",name="",code="",token="";public DateTime expires;}
 sealed class AuthorizedDevice {public string id="",name="",remark="";public DateTime lastSeen;}
 GameProcess[] games=[];int selected=-1;long scanAt;int scanning,connecting;
 internal int RenderedFrames;internal string RenderError="";
 bool closing,restoredQQ;
 public OriginalSettingsPanel(AppState app,bool testMode=false):base("狒狒食柿·试用版服务端")
 {
  this.app=app;this.testMode=testMode;entries=new(app);settings.PushProvider=Math.Clamp(settings.PushProvider,0,2);
  FPSLimit=30;
  if(!testMode){ReplaceFont(DesktopFont(),18,FontGlyphRangeType.ChineseFull);RestorePushKeys();}
  pushRouter=new(PushConfiguration,text=>pushStatus=text,entry=>{if(enabled)app.PublishNotice(entry);},qqSend:SendQQPush);
  app.Game.NativeChat+=OnNativeChat;
  app.Fishing.DirectAvailable=owner=>app.Server.RecentPoll(owner);
  app.Fishing.ExternalDelivery=async(owner,row,mode)=>{var profile=PushConfiguration();profile.direct=false;var result=await pushRouter.Deliver(new ChatEntry{channel="鱼糕",sender=row.title,text=row.body,target=row.key,time=DateTimeOffset.Now.ToString("O")},profile,CancellationToken.None);pushStatus=result.status;return result.ok;};
  services=new(_=>{if(closing)return;try{if(!restoredQQ&&app.Server.Running){restoredQQ=true;if(settings.QQAutoConnect)ConnectQQ(false);}MirrorQQ();if(!testMode){ObserveGameNotices();ScanGames();}}catch(Exception e){Log.Write(e);}},null,testMode?Timeout.Infinite:1000,500);
 }
 static string DesktopFont(){var bundled=Path.Combine(AppContext.BaseDirectory,"组件","fonts","NotoSansSC.ttf");return File.Exists(bundled)?bundled:Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts),"msyh.ttc");}
 sealed class HostEntries(AppState app){public long LastId=>app.Game.LastId;public void Notify(ChatEntry entry)=>app.PublishNotice(entry);}
 sealed class DesktopScheduler {public Task RunOnTick(Action action){action();return Task.CompletedTask;}public Task RunOnFrameworkThread(Func<Task> action)=>Task.Run(action);}
 void OnNativeChat(ChatLine line){if(line.channel.StartsWith("系统 /"))NotifyRequest(line.text);QueueQQEvent(line.channel,line.sender,line.text,line.time);if(line.channel=="悄悄话"&&!line.outgoing&&settings.ReceiveTell&&!app.Server.AnyDirectReceiver)pushRouter?.Enqueue(new ChatEntry{channel=line.channel,sender=line.sender,target=line.target,text=line.text,time=line.time.ToString("O")});}
 long partyAt,tradeAt,friendAt;int observing;string noticeCharacter="",lastPrompt="";HashSet<ulong> targeting=[];
 void NotifyRequest(string text){long now=Environment.TickCount64;string kind="";if(settings.NotifyParty&&new[]{"入队邀请","邀请你加入","希望加入你的小队","希望加入你的队伍"}.Any(text.Contains)&&now-partyAt>10000){partyAt=now;kind="组队邀请";}else if(settings.NotifyTrade&&new[]{"希望与你交易","请求与你交易","交易申请","交易请求"}.Any(text.Contains)&&now-tradeAt>10000){tradeAt=now;kind="交易申请";}else if(settings.NotifyFriend&&new[]{"好友申请","好友请求","请求添加你为好友"}.Any(text.Contains)&&now-friendAt>10000){friendAt=now;kind="好友请求";}if(kind.Length>0)NotifyGame(kind,text+"\n请在游戏中处理。");}
 void NotifyGame(string sender,string text){app.Game.Reminder(sender,text);if(!app.Server.AnyDirectReceiver)pushRouter?.Enqueue(new ChatEntry{channel="提醒",sender=sender,text=text,time=DateTimeOffset.Now.ToString("O")});}
 void ObserveGameNotices(){if(Interlocked.CompareExchange(ref observing,1,0)!=0)return;try{var who=app.Game.Identity;if(!who.online){targeting.Clear();lastPrompt=noticeCharacter="";return;}if(noticeCharacter!=who.contentId){targeting.Clear();lastPrompt="";noticeCharacter=who.contentId;}if(!settings.NotifyParty&&!settings.NotifyTrade&&!settings.NotifyFriend&&!settings.NotifyTarget)return;using var reader=new CharacterReader(who);var confirm=reader.Confirmation();string prompt=confirm.HasValue?confirm.Value.id+":"+confirm.Value.prompt:"";if(prompt!=lastPrompt){lastPrompt=prompt;if(confirm.HasValue)NotifyRequest(confirm.Value.prompt);}if(settings.NotifyTarget){var current=reader.PlayersTargeting();foreach(var player in current)if(!targeting.Contains(player.Key))NotifyGame(player.Value,"正在选中你");targeting=current.Keys.ToHashSet();}else targeting.Clear();}catch(IOException){}catch(Exception e){Log.Write(e);}finally{Interlocked.Exchange(ref observing,0);}}
 PushProfile PushConfiguration()=>new(){provider=settings.PushProvider,qqEnabled=settings.QQNotifications,mode=settings.PushMode,priority=settings.PushPriority,direct=settings.KeepDirectNotifications,wx=wxPushKey,server=serverPushKey,qq=settings.QQNotifyTarget};
 void RestorePushKeys(){try{if(!File.Exists(PushFile))return;using var data=JsonDocument.Parse(PrivateData.Unprotect(File.ReadAllBytes(PushFile)));wxPushKey=data.RootElement.GetProperty("wx").GetString()??"";serverPushKey=data.RootElement.GetProperty("server").GetString()??"";}catch{pushStatus="推送凭证无法读取，请重新填写";}}
 void SavePushKeys(){try{Directory.CreateDirectory(Log.Root);File.WriteAllBytes(PushFile,PrivateData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{wx=wxPushKey,server=serverPushKey}))));}catch(Exception e){pushStatus="推送凭证保存失败："+e.Message;}}
 static bool Equal(string a,string b)=>CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a),Encoding.UTF8.GetBytes(b));
 void Save(){try{settings.Save();}catch(Exception e){status="保存失败："+e.Message;Log.Write(e);}}
 void StartServer(){try{app.Server.Dispose();app.Server.Start();status="手机支持服务已启动";if(settings.QQAutoConnect&&!QQOnline())ConnectQQ(false);}catch(Exception e){status=e.Message;Log.Write(e);}}
 void StopServer(){app.Capture.Stop();app.Server.Dispose();}
 void StopStream()=>app.Capture.Stop();
 Task Send(string channel,string target,string text)=>app.Game.SendChat(channel,target,text,app.Game.Identity.contentId,CancellationToken.None);
 void ApproveDevice(string id,string code){var request=app.Pairing.Pending.FirstOrDefault(r=>r.Id==id);if(request==null||!Equal(request.Code,code))throw new InvalidOperationException("验证码不正确或请求已过期");app.Pairing.Approve(id);settings.DeviceRemarks[id]=approvalRemark;Save();}
 void PersistPairingFile(){foreach(var d in authorizedDevices)settings.DeviceRemarks[d.id]=d.remark;Save();}
 void PersistGameState()=>PersistPairingFile();
 void RefreshDevices(){deviceRequests=app.Pairing.Pending.ToDictionary(p=>p.Id,p=>new DeviceRequest{id=p.Id,name=p.Name,code=p.Code,expires=p.Expires.UtcDateTime});authorizedDevices=settings.Devices.Select(d=>new AuthorizedDevice{id=d.Key,name=d.Value.Name,remark=settings.DeviceRemarks.GetValueOrDefault(d.Key)??""}).ToList();}
 void RevokeDevice(string id)=>app.Pairing.Revoke(id);
 public void ShowPanel()=>ui.Enqueue(()=>panel=true);
 protected override Task PostInitialized(){if(window!=null){SetWindowPos(window.Handle,(nint)(-2),0,0,0,0,0x13);try{Size=Screen.PrimaryScreen?.WorkingArea.Size??new System.Drawing.Size(1280,900);}catch{}}return Task.CompletedTask;}
 [DllImport("user32")]static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
 protected override void Render(){Interlocked.Increment(ref RenderedFrames);while(ui.TryDequeue(out var action))action();if(closing)return;FPSLimit=panel?30:2;Theme();RefreshDevices();try{DrawPanel();}catch(Exception e){RenderError=e.Message;status="面板操作失败："+e.Message;Log.Write(e);}}
 internal void DrawForTest(){Theme();RefreshDevices();DrawPanel();}
 bool Header(string label,ImGuiTreeNodeFlags flags=ImGuiTreeNodeFlags.None){if(testMode)ImGui.SetNextItemOpen(true,ImGuiCond.Always);return ImGui.CollapsingHeader(label,flags);}
 void Theme(){var s=ImGui.GetStyle();s.WindowRounding=12;s.FrameRounding=8;s.PopupRounding=10;s.FrameBorderSize=1;s.WindowBorderSize=1;s.WindowPadding=new(14,12);s.FramePadding=new(10,7);s.ItemSpacing=new(9,8);var c=s.Colors;c[(int)ImGuiCol.Text]=new(.88f,.87f,.84f,1);c[(int)ImGuiCol.TextDisabled]=new(.64f,.63f,.60f,1);c[(int)ImGuiCol.WindowBg]=new(.19f,.19f,.19f,1);c[(int)ImGuiCol.PopupBg]=new(.19f,.19f,.19f,1);c[(int)ImGuiCol.TitleBg]=c[(int)ImGuiCol.TitleBgActive]=new(.24f,.24f,.24f,1);c[(int)ImGuiCol.Border]=new(.62f,.57f,.43f,1);c[(int)ImGuiCol.FrameBg]=c[(int)ImGuiCol.Button]=c[(int)ImGuiCol.Header]=new(.29f,.29f,.29f,1);c[(int)ImGuiCol.FrameBgHovered]=c[(int)ImGuiCol.ButtonHovered]=c[(int)ImGuiCol.HeaderHovered]=new(.38f,.36f,.31f,1);c[(int)ImGuiCol.FrameBgActive]=c[(int)ImGuiCol.ButtonActive]=c[(int)ImGuiCol.HeaderActive]=new(.47f,.43f,.33f,1);c[(int)ImGuiCol.CheckMark]=c[(int)ImGuiCol.SliderGrab]=new(.79f,.71f,.49f,1);}
 void ScanGames(){if(Environment.TickCount64-Interlocked.Read(ref scanAt)<6000||Interlocked.CompareExchange(ref scanning,1,0)!=0)return;Interlocked.Exchange(ref scanAt,Environment.TickCount64);_=Task.Run(async()=>{try{var next=NativeInjection.FindGames();ui.Enqueue(()=>{var old=selected>=0&&selected<games.Length?games[selected].Pid:0;games=next;selected=Array.FindIndex(games,g=>g.Pid==old);if(selected<0)selected=Array.FindIndex(games,g=>g.ContentId==settings.PreferredCharacter);if(selected<0&&games.Length==1)selected=0;});if(settings.AutoConnect&&app.Game.Identity.pid==0){var game=next.FirstOrDefault(g=>g.ContentId.Length>0&&g.ContentId==settings.PreferredCharacter);if(game!=null)await Connect(game);}}catch(Exception e){ui.Enqueue(()=>status=e.Message);}finally{Interlocked.Exchange(ref scanning,0);}});}
 async Task Connect(GameProcess game){if(Interlocked.CompareExchange(ref connecting,1,0)!=0)return;try{if(game.Error.Length>0)throw new IOException(game.Error);app.Capture.Stop();await app.Game.ConnectAsync(game);settings.PreferredCharacter=game.ContentId;Save();}catch(Exception e){ui.Enqueue(()=>status=e.Message);Log.Write(e);}finally{Interlocked.Exchange(ref connecting,0);}}
 void DrawDesktopConnection(){if(ImGui.CollapsingHeader("游戏窗口与桌面启动",ImGuiTreeNodeFlags.DefaultOpen)){ImGui.SetNextItemWidth(-1);if(ImGui.BeginCombo("##角色窗口",selected>=0&&selected<games.Length?games[selected].ToString():"选择已登录的游戏窗口／角色")){for(int i=0;i<games.Length;i++){if(ImGui.Selectable(games[i].ToString(),i==selected))selected=i;}ImGui.EndCombo();}if(ImGui.Button("刷新游戏")){Interlocked.Exchange(ref scanAt,0);if(!testMode)ScanGames();}ImGui.SameLine();ImGui.BeginDisabled(selected<0||Volatile.Read(ref connecting)!=0);if(ImGui.Button("连接所选角色")&&!testMode)_=Connect(games[selected]);ImGui.EndDisabled();ImGui.SameLine();if(ImGui.Button("断开游戏")){app.Capture.Stop();app.Game.Disconnect();}bool auto=settings.AutoConnect;if(ImGui.Checkbox("自动连接已选择的角色",ref auto)){settings.AutoConnect=auto;Save();}bool quiet=settings.Silent;if(ImGui.Checkbox("下次静默启动",ref quiet)){settings.Silent=quiet;Save();}bool startup=StartupEnabled();if(ImGui.Checkbox("开机自启动（静默）",ref startup)&&!testMode)SetStartup(startup);bool idle=settings.PreventIdleLogout;if(ImGui.Checkbox("防止长时间不动掉线",ref idle)){settings.PreventIdleLogout=idle;app.AntiIdle.Enabled=idle;Save();}if(!IsAdministrator()){if(ImGui.Button("以管理员身份重新启动")){try{Process.Start(new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",Arguments="--replace "+Environment.ProcessId,WorkingDirectory=AppContext.BaseDirectory});Application.Exit();}catch(Exception e){status=e.Message;}}}ImGui.TextDisabled(app.AntiIdle.Status);ImGui.TextWrapped(app.Game.Status);if(app.Server.Running)ImGui.TextWrapped("手机连接：https://"+LocalAddress()+":"+settings.Port);}}
 bool StartupEnabled()=>settings.StartWithWindows;
 static bool IsAdministrator()=>new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
 void SetStartup(bool value){try{
  using var run=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
  if(IsAdministrator()){
   string path=Path.Combine(Log.Root,"startup-task.xml");Directory.CreateDirectory(Log.Root);string Escape(string text)=>System.Security.SecurityElement.Escape(text)??"";
   if(value){string account=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;string xml=$"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Escape(account)}</UserId></LogonTrigger></Triggers><Principals><Principal id=\"User\"><UserId>{Escape(account)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Hidden>true</Hidden></Settings><Actions Context=\"User\"><Exec><Command>{Escape(Environment.ProcessPath!)}</Command><Arguments>--silent</Arguments><WorkingDirectory>{Escape(AppContext.BaseDirectory)}</WorkingDirectory></Exec></Actions></Task>";File.WriteAllText(path,xml,Encoding.Unicode);}
   var start=new ProcessStartInfo("schtasks.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in value?new[]{"/Create","/TN","FF14ChatStandaloneTrial","/XML",path,"/F"}:new[]{"/Delete","/TN","FF14ChatStandaloneTrial","/F"})start.ArgumentList.Add(arg);using var child=Process.Start(start)!;string error=child.StandardError.ReadToEnd();if(!child.WaitForExit(5000)){child.Kill();throw new IOException("自启动配置超时");}if(child.ExitCode!=0)throw new IOException("自启动配置失败："+error);run.DeleteValue("FF14ChatStandaloneTrial",false);
  }else if(value)run.SetValue("FF14ChatStandaloneTrial","\""+Environment.ProcessPath+"\" --silent");else run.DeleteValue("FF14ChatStandaloneTrial",false);
  settings.StartWithWindows=value;Save();
 }catch(Exception e){status=e.Message;}}
 static string LocalAddress(){try{return Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(a=>a.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork&&!IPAddress.IsLoopback(a))?.ToString()??"127.0.0.1";}catch{return "127.0.0.1";}}
 void StartStreamDownload(){string helper=Path.Combine(AppContext.BaseDirectory,"组件","StreamHost.exe"),encoder=Path.Combine(AppContext.BaseDirectory,"组件","ffmpeg","ffmpeg.exe");if(File.Exists(helper)&&File.Exists(encoder)){settings.StreamHardwareHelper=helper;settings.StreamEncoderPath=encoder;Save();streamDownloadStatus="免安装包已附带全部必要组件，路径已恢复";}else streamDownloadStatus="缺少必要组件，请将免安装服务端目录完整解压后重试";}
 void CancelStreamDownload(){}
 public void DisposeServices(){closing=true;services.Dispose();app.Game.NativeChat-=OnNativeChat;app.Fishing.ExternalDelivery=null;app.Fishing.DirectAvailable=null;DisconnectQQ();pushRouter?.Dispose();}
}


