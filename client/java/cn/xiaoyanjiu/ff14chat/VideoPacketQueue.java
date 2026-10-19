// SPDX-License-Identifier: GPL-3.0-or-later
package cn.xiaoyanjiu.ff14chat.trial;
import java.util.ArrayDeque;

final class VideoPacketQueue {
 static final class Packet {final byte[] bytes;final boolean reset;Packet(byte[] b,boolean r){bytes=b;reset=r;}}
 private final ArrayDeque<Packet> queue=new ArrayDeque<>();private boolean needKey=true;private byte[] sps,pps;
 static int prefix(byte[] b,int i){if(i+3>=b.length||b[i]!=0||b[i+1]!=0)return 0;if(b[i+2]==1)return 3;return i+4<b.length&&b[i+2]==0&&b[i+3]==1?4:0;}
 static byte[] parameter(byte[] b,int type){for(int i=0;i+3<b.length;i++){int p=prefix(b,i);if(p==0)continue;int start=i+p;if((b[start]&31)!=type){i=start;continue;}int end=start+1;while(end+3<b.length&&prefix(b,end)==0)end++;if(end+3>=b.length)end=b.length;byte[] n=new byte[end-start+4];n[3]=1;System.arraycopy(b,start,n,4,end-start);return n;}return null;}
 static boolean key(byte[] b){for(int i=0;i+3<b.length;i++){int p=prefix(b,i);if(p>0&&(b[i+p]&31)==5)return true;}return false;}
 synchronized void offer(byte[] bytes){if(bytes==null||bytes.length<4||bytes.length>4194304)return;byte[] a=parameter(bytes,7),b=parameter(bytes,8);if(a!=null)sps=a;if(b!=null)pps=b;if(queue.size()>=3){queue.clear();needKey=true;}if(needKey){if(!key(bytes)||sps==null||pps==null)return;int extra=(a==null?sps.length:0)+(b==null?pps.length:0);if(extra>0){if(bytes.length+extra>4194304)return;byte[] complete=new byte[bytes.length+extra];int pos=0;if(a==null){System.arraycopy(sps,0,complete,pos,sps.length);pos+=sps.length;}if(b==null){System.arraycopy(pps,0,complete,pos,pps.length);pos+=pps.length;}System.arraycopy(bytes,0,complete,pos,bytes.length);bytes=complete;}}queue.addLast(new Packet(bytes,needKey));needKey=false;}
 synchronized Packet poll(){return queue.pollFirst();}
 // Keep codec parameters across packet loss/reconnection; an IDR alone cannot initialize a decoder.
 synchronized void clear(){queue.clear();needKey=true;}
 synchronized int size(){return queue.size();}
}

