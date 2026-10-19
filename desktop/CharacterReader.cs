using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace Trial;
// Read-only adapters for the original character pages. All offsets belong to the
// exact executable SHA-256 in game-profile.json; no process writes occur here.
internal static class GameCatalog
{
 static readonly Lazy<JsonDocument> data=new(()=>{var a=typeof(GameCatalog).Assembly;using var s=a.GetManifestResourceStream(a.GetManifestResourceNames().Single(n=>n.EndsWith("catalog.json")))!;return JsonDocument.Parse(s);});
 static readonly System.Collections.Concurrent.ConcurrentDictionary<string,Dictionary<int,JsonElement>> tables=new();
 public static string ChatKind(int kind)=>data.Value.RootElement.TryGetProperty("chatKinds",out var rows)&&rows.TryGetProperty(kind.ToString(),out var label)?label.GetString()??kind.ToString():kind.ToString();
 public static JsonElement[] Rows(string name)=>data.Value.RootElement.GetProperty(name).EnumerateArray().ToArray();
 public static JsonElement? Row(string table,int id)=>tables.GetOrAdd(table,k=>Rows(k).ToDictionary(r=>r.GetProperty("id").GetInt32())).GetValueOrDefault(id) is var row&&row.ValueKind!=JsonValueKind.Undefined?row:null;
 public static string Text(JsonElement? row,string key)=>row.HasValue&&row.Value.TryGetProperty(key,out var v)?v.GetString()??"":"";
 public static int Number(JsonElement? row,string key)=>row.HasValue&&row.Value.TryGetProperty(key,out var v)?v.GetInt32():0;
}
internal sealed class CharacterReader:IDisposable
{
 readonly GameIdentity who;readonly GameProfile profile;readonly Process process;readonly SafeProcessHandle handle;readonly nint image;
 readonly byte[] state;readonly nint ui,inventory,order,actor,module;
 readonly Dictionary<int,byte[]> containers=new();
 internal CharacterReader(GameIdentity identity)
 {
  if(!identity.online)throw new IOException("请先连接已登录的游戏角色");who=identity;profile=GameProfile.Load();process=Process.GetProcessById(who.pid);
  handle=NativeInjection.OpenRead(who.pid);try{if(handle.IsInvalid)throw new IOException("无法读取当前角色");image=process.MainModule!.BaseAddress;
  state=Bytes(image+(int)profile.Addresses["PlayerState"],2344);CheckCharacter();
  ui=image+(int)profile.Addresses["UIState"];inventory=image+(int)profile.Addresses["InventoryManager"];
  var framework=Pointer(image+(int)profile.Addresses["Framework"]);module=Pointer(framework+0x2b68);order=module==0?0:module+588088;
  actor=Pointer(image+(int)profile.Addresses["ObjectManager"]+32);
  if(actor==0||BitConverter.ToUInt64(Bytes(actor+9048,8))!=ulong.Parse(who.contentId))throw new IOException("角色对象已变化，请刷新");
  var types=Bytes(image+(int)profile.Addresses["InventoryTypeTable"],84*4);var pointer=Pointer(inventory+7688);
  if(pointer==0)throw new IOException("库存尚未加载");var block=Bytes(pointer,84*32);
  for(int i=0;i<84;i++){int type=BitConverter.ToInt32(types,i*4);var c=block.AsSpan(i*32,32).ToArray();if(BitConverter.ToInt32(c,16)!=type)throw new IOException("库存布局不匹配");containers[type]=c;}
 }catch{handle.Dispose();process.Dispose();throw;}
 }
 byte[] Bytes(nint p,int length)=>NativeInjection.Read(handle,p,length);
 nint Pointer(nint p)=>(nint)BitConverter.ToInt64(Bytes(p,8));
 void CheckCharacter(){if(state[0]==0||BitConverter.ToUInt64(state,104).ToString()!=who.contentId||BitConverter.ToUInt64(Bytes(image+(int)profile.Addresses["PlayerState"]+104,8)).ToString()!=who.contentId)throw new IOException("角色已切换，已取消读取");}
 internal static bool Bit(byte[] bytes,int index)=>index>=0&&index<bytes.Length*8&&(bytes[index/8]&(1<<(index%8)))!=0;
 internal static object Item(byte[] bytes,int offset,int slot)
 {
  if(bytes.Length-offset<72)throw new InvalidDataException("物品槽位截断");var symbolic=bytes[offset+14]!=0;if(symbolic)throw new IOException("库存存在引用槽位，请等待游戏加载完成");
  var id=BitConverter.ToUInt32(bytes,offset+16)%1000000;var row=GameCatalog.Row("items",(int)id);
  return new{slot,itemId=id,name=GameCatalog.Text(row,"name"),iconId=GameCatalog.Number(row,"icon"),quantity=BitConverter.ToInt32(bytes,offset+20),hq=(bytes[offset+28]&1)!=0,condition=BitConverter.ToUInt16(bytes,offset+26)};
 }
 byte[]? Items(int type,out int size)
 {
  size=0;if(!containers.TryGetValue(type,out var c)||c[24]==0)return null;size=BitConverter.ToInt32(c,20);if(size is <0 or >200)throw new IOException("库存大小不匹配");var ptr=(nint)BitConverter.ToInt64(c,8);return ptr==0?null:Bytes(ptr,size*72);
 }
 (int type,int slot) Position(int type,int slot)
 {
  if(order==0)return(type,slot);
  int offset=type switch{<4=>72,3500=>80,3200=>128,3201=>88,3202=>96,3203=>104,3205=>112,3206=>120,3207=>136,3208=>144,3209=>152,3300=>160,3400=>168,4000 or 4001=>208,4100 or 4101=>216,_=>0};if(offset==0)return(type,slot);
  var sorter=Pointer(order+offset);if(sorter==0)return(type,slot);var d=Bytes(sorter,64);if(BitConverter.ToInt32(d,56)!=-1||BitConverter.ToInt32(d,60)!=100)throw new IOException("游戏库存正在排序，请稍后刷新");
  int index=type<4?type*35+slot:type is 4000 or 4001?(type-4000)*35+slot:type is 4100 or 4101?(type-4100)*35+slot:slot;
  long first=BitConverter.ToInt64(d,8),last=BitConverter.ToInt64(d,16);if(first<=0||last<first||(last-first)%8!=0||last-first>2000*8)throw new IOException("库存排序布局无效");if(index>=(last-first)/8)return(type,slot);
  var entry=Pointer((nint)first+index*8);if(entry==0)return(type,slot);var e=Bytes(entry,4);int page=BitConverter.ToUInt16(e),sourceSlot=BitConverter.ToUInt16(e,2);return(type<4?page:type is 4000 or 4001?4000+page:type is 4100 or 4101?4100+page:type,sourceSlot);
 }
 uint Gil(){var raw=Items(2000,out int count);if(raw==null)return 0;for(int i=0;i<count;i++)if(BitConverter.ToUInt32(raw,i*72+16)==1)return (uint)Math.Max(0,BitConverter.ToInt32(raw,i*72+20));return 0;}
 object Container(int type,bool sorted=true)
 {
  var bytes=Items(type,out int size);if(bytes==null)return new{id=type,loaded=false,size=0,slots=Array.Empty<object>()};var slots=new List<object>();
  for(int i=0;i<size;i++){var pos=sorted?Position(type,i):(type,i);var raw=pos.Item1==type?bytes:Items(pos.Item1,out _);if(raw==null||pos.Item2<0||pos.Item2*72>=raw.Length)throw new IOException("库存槽位正在变化，请刷新");slots.Add(Item(raw,pos.Item2*72,i));}
  return new{id=type,loaded=true,size,slots};
 }
 public object Read(string section)
 {
  object result;
  if(section is "items" or "armoury" or "saddlebag"){
   int[] types=section switch{"items"=>[0,1,2,3,2004,2001],"armoury"=>[3500,3200,3201,3202,3203,3205,3206,3207,3208,3209,3300,3400],_=>state[351]!=0?[4000,4001,4100,4101]:[4000,4001]};
   result=new{ok=true,section,character=who.name,containers=types.Select(t=>Container(t)).ToArray(),gil=Gil()};
  }else if(section is "mounts" or "minions"){
   var bits=section=="mounts"?state.AsSpan(769,45).ToArray():Bytes(ui+106644,75);
   var rows=GameCatalog.Rows(section).Where(r=>r.GetProperty("id").GetInt32()>0&&Bit(bits,r.GetProperty(section=="mounts"?"order":"id").GetInt32())).Select(r=>new{id=r.GetProperty("id").GetInt32(),name=r.GetProperty("name").GetString(),iconId=r.GetProperty("icon").GetInt32()});
   result=new{ok=true,section,rows=rows.ToArray()};
  }else if(section=="currency"){
   var currencies=new List<object>();foreach(var type in new[]{2000,2001}){var raw=Items(type,out var size);if(raw==null)continue;for(int i=0;i<size;i++){uint id=BitConverter.ToUInt32(raw,i*72+16);if(id==0)continue;var row=GameCatalog.Row("items",(int)id);string name=GameCatalog.Text(row,"name");currencies.Add(new{itemId=id,name,iconId=GameCatalog.Number(row,"icon"),quantity=BitConverter.ToInt32(raw,i*72+20),max=GameCatalog.Number(row,"max"),tab=type==2001?2:name.Contains("神典石")||name.Contains("徽章")||name.Contains("狼印")?1:0,group=type==2001?"水晶":name.Contains("神典石")?"亚拉戈神典石":"通用货币",retired=false});}}
   var existing=currencies.Select(c=>JsonSerializer.SerializeToElement(c).GetProperty("itemId").GetUInt32()).ToHashSet();foreach(var currency in CurrencyTree()){if(!existing.Add(currency.id))continue;var row=GameCatalog.Row("items",(int)currency.id);if(row==null)continue;var beast=GameCatalog.Rows("tribesReputation").FirstOrDefault(b=>b.GetProperty("currency").GetUInt32()==currency.id);bool tribal=beast.ValueKind!=JsonValueKind.Undefined;string name=GameCatalog.Text(row,"name");currencies.Add(new{itemId=currency.id,name,iconId=GameCatalog.Number(row,"icon"),quantity=currency.quantity,max=currency.max,tab=tribal?beast.GetProperty("expansionId").GetInt32()>=5?4:3:currency.id==10307?1:2,group=tribal?GameCatalog.Text(beast,"expansion"):name.Contains("大地")?"大地使者":name.Contains("巧手")||name.Contains("能工")?"能工巧匠":"其他货币",retired=name.Contains("白票")||name.Contains("黄票")||name.Contains("红票")});}
   result=new{ok=true,section,currencies,warning=""};
  }else if(section=="character"){
   var d=Bytes(actor+416,80);var attributes=ReadAttributes();
   var stats=attributes.ToDictionary(p=>p.Key,p=>BitConverter.ToInt32(state,424+p.Value*4));
   string GenderName(string table,int id)=>GameCatalog.Text(GameCatalog.Row(table,id),state[123]==0?"m":"f");
   var equipped=Items(1000,out int count);var gear=equipped==null?Array.Empty<object>():Enumerable.Range(0,Math.Min(count,14)).Select(i=>Item(equipped,i*72,i)).ToArray();
   var jobs=GameCatalog.Rows("jobs").Where(r=>r.GetProperty("id").GetInt32()>0&&r.GetProperty("levelIndex").GetInt32() is >=0 and <35).Select(r=>new{id=r.GetProperty("id").GetInt32(),name=r.GetProperty("name").GetString(),level=BitConverter.ToInt16(state,138+r.GetProperty("levelIndex").GetInt32()*2),role=r.GetProperty("role").GetInt32(),craft=r.GetProperty("craft").GetBoolean()}).ToArray();
   result=new{ok=true,section,name=who.name,world=who.world,level=BitConverter.ToInt16(state,136),job=state[126],itemLevel=BitConverter.ToUInt16(Bytes(ui+106452,2)),hp=BitConverter.ToUInt32(d,12),maxHp=BitConverter.ToUInt32(d,16),mp=BitConverter.ToUInt32(d,20),maxMp=BitConverter.ToUInt32(d,24),currentGp=BitConverter.ToUInt16(d,28),maxGp=BitConverter.ToUInt16(d,30),currentCp=BitConverter.ToUInt16(d,32),maxCp=BitConverter.ToUInt16(d,34),stats,attributes=new{strength=stats["Strength"],dexterity=stats["Dexterity"],vitality=stats["Vitality"],intelligence=stats["Intelligence"],mind=stats["Mind"]},jobs,gear,reputations=Reputations(),commendations=BitConverter.ToInt16(state,1324),race=GenderName("races",state[124]),tribe=GenderName("tribes",state[125]),sex=state[123]==0?"男":"女",birthday=new{month=state[355],day=state[356]},deity=GameCatalog.Text(GameCatalog.Row("deities",state[354]),"name"),company=GameCatalog.Text(GameCatalog.Row("companies",state[720]),"name"),warning=""};
  }else throw new InvalidDataException("未知角色页面");
  CheckCharacter();return result;
 }
 internal Dictionary<ulong,string> PlayersTargeting(){var result=new Dictionary<ulong,string>();uint self=BitConverter.ToUInt32(Bytes(actor+120,4));var objects=Bytes(image+(int)profile.Addresses["ObjectManager"]+32,819*8);for(int i=0;i<819;i++){var pointer=(nint)BitConverter.ToInt64(objects,i*8);if(pointer==0||pointer==actor)continue;try{var header=Bytes(pointer,416);if(header[144]!=1)continue;if(BitConverter.ToUInt64(Bytes(pointer+8968,8))!=self)continue;ulong id=BitConverter.ToUInt32(header,120);result[id]=GameLink.SeStringText(header.AsSpan(48,64).ToArray());}catch(IOException){}}CheckCharacter();return result;}
 internal string[] TabNames()=>Enumerable.Range(0,4).Select(i=>{string name=Utf8(module+6848+1328+i*2344);return name.Length>0?name:new[]{"通常","战斗","剧情","聊天4"}[i];}).ToArray();
 object[] Reputations(){var quest=image+(int)profile.Addresses["QuestManager"];var bytes=Bytes(quest+3304,20*16);return GameCatalog.Rows("tribesReputation").Where(r=>r.GetProperty("id").GetInt32() is >0 and <=20).Select(r=>{int index=r.GetProperty("id").GetInt32()-1,rank=bytes[index*16+8]&127;return new{name=GameCatalog.Text(r,"name"),iconId=GameCatalog.Number(r,"icon"),expansion=GameCatalog.Text(r,"expansion"),rank,current=BitConverter.ToUInt16(bytes,index*16+10),needed=GameCatalog.Number(GameCatalog.Row("reputationRanks",rank),"needed")};}).Where(r=>r.rank>0).Cast<object>().ToArray();}
 IEnumerable<(uint id,uint quantity,uint? max)> CurrencyTree(){var manager=Pointer(image+(int)profile.Addresses["CurrencyManager"]);if(manager==0)yield break;for(int table=0;table<3;table++){var map=Bytes(manager+table*16,16);var head=(nint)BitConverter.ToInt64(map);ulong count=BitConverter.ToUInt64(map,8);if(count>4096)throw new IOException("货币数量无效");if(head==0||count==0)continue;var stack=new Stack<nint>();stack.Push(Pointer(head+8));var visited=new HashSet<nint>();while(stack.TryPop(out var node)){if(node==0||node==head)continue;if(!visited.Add(node)||visited.Count>4096)throw new IOException("货币结构已变化");var d=Bytes(node,72);if(d[25]!=0)continue;stack.Push((nint)BitConverter.ToInt64(d));stack.Push((nint)BitConverter.ToInt64(d,16));uint id=BitConverter.ToUInt32(d,32);uint quantity=table==0?BitConverter.ToUInt32(d,44):table==1?BitConverter.ToUInt16(d,38):BitConverter.ToUInt16(d,40);uint? max=table==0?BitConverter.ToUInt32(d,40):table==1?d[40]!=0?null:BitConverter.ToUInt16(d,36):d[44]!=0?null:BitConverter.ToUInt16(d,36);yield return(id,quantity,max);}}}
 internal string Utf8(nint pointer,int max=4096){var d=Bytes(pointer,32);long size=BitConverter.ToInt64(d,16);if(size<=1)return "";if(size>max)throw new IOException("游戏文本超出边界");return GameLink.SeStringText(Bytes((nint)BitConverter.ToInt64(d),checked((int)size)));}
 internal object[] Macros(){var rows=new List<object>();for(int set=0;set<2;set++)for(int i=0;i<100;i++){var p=module+25008+96+set*167200+i*1672;string name=Utf8(p+8);var lines=Enumerable.Range(0,15).Select(n=>Utf8(p+112+n*104)).Where(x=>x.Length>0).ToArray();if(name.Length==0&&lines.Length==0)continue;rows.Add(new{set,index=i,name,lines=string.Join("\n",lines),icon=GameAssets.Icon(BitConverter.ToInt32(Bytes(p,4)))});}CheckCharacter();return rows.ToArray();}
 internal byte[]? Addon(string name){var stage=Pointer(image+(int)profile.Addresses["AtkStage"]);if(stage==0)return null;var manager=Pointer(stage+32);if(manager==0)return null;var pointers=Bytes(manager+26880+8,2048);for(int i=0;i<256;i++){var p=(nint)BitConverter.ToInt64(pointers,i*8);if(p==0)continue;byte[] d;try{d=Bytes(p,568);}catch(IOException){continue;}if(GameLink.SeStringText(d.AsSpan(8,32).ToArray())!=name)continue;if((BitConverter.ToUInt32(d,408)&(1u<<21))==0||(d[417]&1)==0)return null;return Bytes(p,name=="SelectYesno"?744:name=="LookingForGroupDetail"?1024:568);}return null;}
 internal (int id,string prompt)? Confirmation(){var d=Addon("SelectYesno");if(d==null)return null;var text=(nint)BitConverter.ToInt64(d,568);return(BitConverter.ToUInt16(d,484),text==0?"":Utf8(text+208));}
 static Dictionary<string,int> ReadAttributes(){var a=typeof(CharacterReader).Assembly;using var s=a.GetManifestResourceStream(a.GetManifestResourceNames().Single(n=>n.EndsWith("attributes.json")))!;return JsonSerializer.Deserialize<Dictionary<string,int>>(s)!;}
 public void Dispose(){handle.Dispose();process.Dispose();}
}
