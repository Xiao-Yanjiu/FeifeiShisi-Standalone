using System.Diagnostics;
namespace Trial;
// Windows lifecycle/tray only. All visible settings remain in the original ImGui panel.
internal sealed class MainWindow:Form
{
 readonly AppState app;readonly NotifyIcon tray;readonly OriginalSettingsPanel scene;
 bool quitting,started;Task? rendering;
 public MainWindow(AppState app,bool silent)
 {
  this.app=app;ShowInTaskbar=false;Opacity=0;Width=1;Height=1;FormBorderStyle=FormBorderStyle.None;
  using var stream=typeof(MainWindow).Assembly.GetManifestResourceStream(typeof(MainWindow).Assembly.GetManifestResourceNames().First(n=>n.EndsWith("app.ico")))!;Icon=new Icon(stream);
  scene=new(app);
  var menu=new ContextMenuStrip();menu.Items.Add("打开服务端",null,(_,_)=>ShowPanel());menu.Items.Add("退出",null,(_,_)=>{quitting=true;Close();});
  tray=new(){Icon=Icon,Text="狒狒食柿 · 试用版",Visible=true,ContextMenuStrip=menu};tray.DoubleClick+=(_,_)=>ShowPanel();
  Shown+=(_,_)=>{Hide();try{app.Server.Start();}catch(Exception e){Log.Write(e);tray.ShowBalloonTip(8000,"手机服务未启动",e.Message,ToolTipIcon.Error);}if(!silent&&!app.Settings.Silent)ShowPanel();};
  FormClosing+=(_,e)=>{if(!quitting&&e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
 }
 void ShowPanel()
 {
  if(started){scene.ShowPanel();return;}started=true;
  rendering=Task.Run(()=>scene.Run());_=rendering.ContinueWith(t=>{if(t.Exception is{} error){Log.Write(error.GetBaseException());if(!IsDisposed)BeginInvoke(()=>tray.ShowBalloonTip(8000,"服务端界面启动失败",error.GetBaseException().Message,ToolTipIcon.Error));}},TaskScheduler.Default);
 }
 protected override void Dispose(bool disposing)
 {
  if(disposing){tray.Visible=false;tray.Dispose();scene.DisposeServices();if(started){scene.Close();try{rendering?.Wait(TimeSpan.FromSeconds(3));}catch{}scene.Dispose();}}
  base.Dispose(disposing);
 }
}

