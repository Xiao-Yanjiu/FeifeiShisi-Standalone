using System.Reflection;
using ImGuiNET;
namespace Trial;
internal static class OriginalUiChecks
{
 public static unsafe int Run()
 {
  var context=ImGui.CreateContext();int count=0;
  try{
   var io=ImGui.GetIO();io.DisplaySize=new(1280,2000);io.DeltaTime=1f/30;io.NativePtr->IniFilename=null;
   io.Fonts.AddFontDefault();io.Fonts.GetTexDataAsRGBA32(out byte* pixels,out int width,out int height);io.Fonts.SetTexID((nint)1);
   using var app=new AppState();var scene=new OriginalSettingsPanel(app,true);
   try{
    foreach(int pane in new[]{0,1,2,3}){
     typeof(OriginalSettingsPanel).GetField("pushPanel",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(scene,pane);
     ImGui.NewFrame();scene.DrawForTest();ImGui.Render();
     if(ImGui.GetDrawData().TotalVtxCount<1)throw new Exception("Original ImGui settings did not draw");count++;
    }
    if(typeof(Settings).GetProperty("ReceiveWorldWhisper")!=null||typeof(Settings).GetProperty("NotifyMention")!=null)throw new Exception("Removed world settings returned");count++;
    if(OriginalSettingsPanel.PageKeys.Any(x=>x is "glamour" or "office" or "collections" or "itemSearch"))throw new Exception("Removed pages returned");count++;
   }finally{scene.DisposeServices();}
  }finally{ImGui.DestroyContext(context);}
  return count;
 }
}

