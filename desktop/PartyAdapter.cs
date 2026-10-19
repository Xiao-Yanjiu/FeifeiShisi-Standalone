using System.Text.Json;
namespace Trial;
internal sealed class PartyAdapter(GameLink game)
{
 readonly SemaphoreSlim serial=new(1);string owner="",cid="",pending="",memberName="";int confirmId;long issued;
 internal async Task<object> Action(JsonElement r,string auth,CancellationToken token){await serial.WaitAsync(token);try{var who=game.Identity;string action=r.TryGetProperty("action",out var a)?a.GetString()??"read":"read";var snapshot=JsonSerializer.SerializeToElement(NativeInjection.ReadParty(who));
  using var reader=new CharacterReader(who);
  (int id,string prompt)? Confirm(){if(owner!=auth||cid!=who.contentId||Environment.TickCount64-issued>30000||pending.Length==0)return null;var confirm=reader.Confirmation();if(!confirm.HasValue)return null;string text=confirm.Value.prompt;bool relevant=pending=="leave"?text.Contains("离开")||text.Contains("leave",StringComparison.OrdinalIgnoreCase):pending=="disband"?text.Contains("解散")||text.Contains("disband",StringComparison.OrdinalIgnoreCase):memberName.Length>0&&text.Contains(memberName);if(!relevant)return null;if(confirmId==0)confirmId=confirm.Value.id;return confirmId==confirm.Value.id?confirm:null;}
  if(action.StartsWith("confirm-")){var confirm=Confirm()??throw new IOException("队伍确认窗口已改变");if(action is not ("confirm-yes" or "confirm-no"))throw new InvalidDataException("未知确认操作");await game.NativeAction($"confirm:{(action=="confirm-yes"?1:0)}:{confirm.id}",who.contentId,token);pending="";confirmId=0;}
  else if(action!="read"){
   if(!snapshot.GetProperty("inParty").GetBoolean())throw new IOException("当前没有小队");if(reader.Confirmation()!=null)throw new IOException("请先处理游戏中已有的确认窗口");
   string command=action switch{"leave"=>"/pcmd leave","disband"=>"/pcmd breakup","kick"=>"/pcmd kick ","promote"=>"/pcmd leader ",_=>throw new InvalidDataException("未知队伍操作")};
   if(action!="leave"&&!snapshot.GetProperty("isLeader").GetBoolean())throw new IOException("只有队长可以管理队伍");
   if(action is "kick" or "promote"){string id=r.GetProperty("memberId").GetString()??"";var member=snapshot.GetProperty("members").EnumerateArray().SingleOrDefault(m=>m.GetProperty("id").GetString()==id);if(member.ValueKind==JsonValueKind.Undefined||member.GetProperty("self").GetBoolean())throw new IOException("队员已变化或不能操作自己");memberName=member.GetProperty("name").GetString()??"";command+=member.GetProperty("target").GetString();}
   await game.NativeAction(command,who.contentId,token,3);owner=auth;cid=who.contentId;pending=action;confirmId=0;issued=Environment.TickCount64;await Task.Delay(200,token);
  }
  var response=JsonSerializer.Deserialize<Dictionary<string,object>>(JsonSerializer.Serialize(NativeInjection.ReadParty(who)))!;response["confirmation"]=Confirm()?.prompt??"";response["status"]=action=="read"?"":"操作已提交游戏，请以游戏返回的状态为准";if(game.Identity.contentId!=who.contentId)throw new IOException("角色已切换");return response;
 }finally{serial.Release();}}
}
