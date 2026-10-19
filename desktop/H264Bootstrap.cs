namespace Trial;

// Both transports must recover with a self-contained IDR, including codec parameters.
internal sealed class H264Bootstrap
{
    byte[]? sps, pps;
    static int Prefix(byte[] b,int i) => i+3<b.Length&&b[i]==0&&b[i+1]==0
        ? b[i+2]==1?3:i+4<b.Length&&b[i+2]==0&&b[i+3]==1?4:0 : 0;
    internal static byte[]? Parameter(byte[] b,int type)
    {
        for(int i=0;i+3<b.Length;i++)
        {
            int p=Prefix(b,i);if(p==0)continue;int start=i+p;
            if((b[start]&31)!=type){i=start;continue;}
            int end=start+1;while(end+3<b.Length&&Prefix(b,end)==0)end++;
            if(end+3>=b.Length)end=b.Length;
            var n=new byte[end-start+4];n[3]=1;b.AsSpan(start,end-start).CopyTo(n.AsSpan(4));return n;
        }
        return null;
    }
    internal byte[] Prepare(byte[] bytes)
    {
        var a=Parameter(bytes,7);var b=Parameter(bytes,8);if(a!=null)sps=a;if(b!=null)pps=b;
        if(!PhoneUdpVideoSession.IsKey(bytes)||sps==null||pps==null)return bytes;
        int extra=(a==null?sps.Length:0)+(b==null?pps.Length:0);
        if(extra==0)return bytes;
        if(bytes.Length+extra>PhoneUdpVideoSession.MaxFrame)throw new IOException("H.264 关键帧超过大小限制");
        var result=new byte[extra+bytes.Length];int pos=0;
        if(a==null){sps.CopyTo(result,pos);pos+=sps.Length;}
        if(b==null){pps.CopyTo(result,pos);pos+=pps.Length;}
        bytes.CopyTo(result,pos);return result;
    }
}
