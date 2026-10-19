using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
if(args.Length!=2)throw new ArgumentException("需要游戏 EXE 路径和输出路径");
byte[] image=File.ReadAllBytes(args[0]);using var pe=new PEReader(new MemoryStream(image));
var h=pe.PEHeaders;var text=h.SectionHeaders.Single(x=>x.Name==".text");
var bytes=image.AsSpan(text.PointerToRawData,text.SizeOfRawData).ToArray();
uint Find(string pattern,int relative=0){var parts=pattern.Split(' ');var values=parts.Select(x=>x=="??"?-1:Convert.ToInt32(x,16)).ToArray();var hits=new List<int>();for(int i=0;i<=bytes.Length-values.Length;i++){if(values[0]>=0&&bytes[i]!=values[0])continue;bool same=true;for(int j=1;j<values.Length;j++)if(values[j]>=0&&bytes[i+j]!=values[j]){same=false;break;}if(same)hits.Add(i);}if(hits.Count!=1)throw new InvalidDataException(pattern+": matches="+hits.Count);int start=hits[0]+text.VirtualAddress;if(relative!=0)start=checked(start+relative+4+BitConverter.ToInt32(bytes,hits[0]+relative));return checked((uint)start);}
var a=new Dictionary<string,uint>{
 ["InventoryManager"]=Find("48 8D 0D ?? ?? ?? ?? 81 C2",3),
 ["UIState"]=Find("48 8D 0D ?? ?? ?? ?? 45 33 C0 BA ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? BA",3),
 ["InventoryTypeTable"]=Find("4C 8D 05 ?? ?? ?? ?? 0F 1F 80 00 00 00 00 0F B7 C8 41 39 1C 88",3),
 ["ExecuteMacro"]=Find("48 89 5C 24 ?? 41 56 48 83 EC ?? 80 B9 ?? ?? ?? ?? ?? 4C 8B F2"),
 ["OpenListing"]=Find("48 89 5C 24 ?? 57 48 83 EC ?? 48 8B FA 48 8B D9 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9"),
 ["AtkStage"]=Find("48 8B 05 ?? ?? ?? ?? 4C 8B 40 18 45 8B 40 18",3),
 ["GetAddonByName"]=Find("E8 ?? ?? ?? ?? 48 8B F8 41 B0 01",1),
 ["QuestManager"]=Find("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C6 84 24",3),
 ["CurrencyManager"]=Find("48 8B 1D ?? ?? ?? ?? 48 85 DB 74 27 48 8D 4B 20 E8 ?? ?? ?? ?? 48 8D 4B 10 E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? BA ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 33 D2",3),
 ["PrintMessage"]=Find("E8 ?? ?? ?? ?? 44 39 AE ?? ?? ?? ?? 7E",1),
 ["Framework"]=Find("48 8B 1D ?? ?? ?? ?? 8B 7C 24",3),
 ["GetUIModule"]=Find("E8 ?? ?? ?? ?? 80 7B 1D 01",1),
 ["ProcessChat"]=Find("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 48 8B F2 48 8B F9 45 84 C9"),
 ["StringCtor"]=Find("E8 ?? ?? ?? ?? 0F 5A D6",1),
 ["StringSet"]=Find("E8 ?? ?? ?? ?? 4D 39 2E",1),
 ["StringDtor"]=Find("E8 ?? ?? ?? ?? C7 44 F5",1),
 ["PlayerState"]=Find("48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 84 C0 75 06 F6 43 18 02",3),
 ["ObjectManager"]=Find("48 8D 35 ?? ?? ?? ?? 81 FA",3),
 ["GroupManager"]=Find("33 D2 48 8D 0D ?? ?? ?? ?? 33 DB",5),
 ["ReceiveListing"]=Find("40 53 41 57 48 83 EC 28 48 8B D9 4C 8B FA 48 8B 0D"),
 ["RequestCategoryListings"]=Find("48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 0F 10 81 ?? ?? ?? ?? 8B 99"),
 ["GetAgentById"]=Find("E8 ?? ?? ?? ?? 83 7B 48 00",1),
 ["RequestListingsUpdate"]=Find("E8 ?? ?? ?? ?? 8B 8B ?? ?? ?? ?? 85 C9 75 12",1)
};
var prologue=bytes.AsSpan(checked((int)a["PrintMessage"])-text.VirtualAddress,16).ToArray();
var listingPrologue=bytes.AsSpan(checked((int)a["ReceiveListing"])-text.VirtualAddress,16).ToArray();
var profile=new{ImageSha256=Convert.ToHexString(SHA256.HashData(image)),Timestamp=(uint)h.CoffHeader.TimeDateStamp,ImageSize=(uint)h.PEHeader!.SizeOfImage,Addresses=a,PrintPrologue=Convert.ToBase64String(prologue),ListingPrologue=Convert.ToBase64String(listingPrologue)};
File.WriteAllText(args[1],JsonSerializer.Serialize(profile,new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("已唯一匹配 "+a.Count+" 项地址；游戏 SHA256="+profile.ImageSha256);

