using System.Text;
namespace Trial;
internal static class NativePlayerLink
{
 static bool Number(byte[] data,ref int at,int end,out uint value){value=0;if(at>=end)return false;byte first=data[at++];if(first is >0 and <0xD0){value=(uint)(first-1);return true;}if(first<0xF0||first==0xFF)return false;int mask=(first+1)&15;for(int shift=24;shift>=0;shift-=8)if((mask&(1<<(shift/8)))!=0){if(at>=end)return false;value|=(uint)data[at++]<<shift;}return true;}
 internal static string Target(byte[] data){for(int i=0;i<data.Length;){if(data[i++]!=2)continue;if(i>=data.Length)break;int type=data[i++];if(!Number(data,ref i,data.Length,out uint size)||i>=data.Length||size>(uint)(data.Length-i-1))break;int end=i+(int)size;if(end>=data.Length||data[end]!=3)break;if(type==0x27){int at=i;if(Number(data,ref at,end,out var link)&&link==0&&Number(data,ref at,end,out var world)&&world is >0 and <=65535&&Number(data,ref at,end,out _)&&at<end&&data[at++]==0xFF&&Number(data,ref at,end,out var length)&&length<=(uint)(end-at)){string name=GameLink.SeStringText(data.AsSpan(at,(int)length).ToArray());if(name.Length>0)return name+"@"+GameNames.World(world.ToString());}}i=end+1;}return "";}
}
