package cn.xiaoyanjiu.ff14chat.trial;
import java.nio.file.*;import java.util.*;import java.net.*;
public final class StreamProtocolChecks {
 static int checks;static void check(boolean ok,String text){if(!ok)throw new AssertionError(text);checks++;}
 public static void main(String[] args)throws Exception {
  byte[] headers={0,0,0,1,0x67,0x42,0,0,1,0x68,0x12},idr={0,0,1,0x65,0x21};
  VideoPacketQueue q=new VideoPacketQueue();q.offer(headers);check(q.poll()==null,"headers alone wait for IDR");q.offer(idr);VideoPacketQueue.Packet packet=q.poll();check(packet!=null&&packet.reset&&VideoPacketQueue.parameter(packet.bytes,7)!=null&&VideoPacketQueue.parameter(packet.bytes,8)!=null,"IDR receives cached codec parameters");q.clear();q.offer(idr);check(q.poll()!=null,"reconnection retains parameters");check(VideoPacketQueue.parameter(new byte[]{0,0,0,1},7)==null,"truncated Annex B rejected");
  byte[] sample=Files.readAllBytes(Paths.get(args[0],"first-key.h264"));q=new VideoPacketQueue();q.offer(sample);packet=q.poll();check(packet!=null&&packet.reset,"real NVENC access unit accepted");Files.write(Paths.get(args[0],"tcp-decoder-input.h264"),packet.bytes);
  int port=Integer.parseInt(args[1]);byte[] key=Base64.getDecoder().decode(args[2]);java.io.ByteArrayOutputStream data=new java.io.ByteArrayOutputStream();final int[] frames={0};
  UdpVideoTransport transport=new UdpVideoTransport(new InetSocketAddress("127.0.0.1",port),key,new UdpVideoProtocol.Sink(){public void frame(byte[] bytes){synchronized(data){try{data.write(bytes);frames[0]++;}catch(Exception e){throw new RuntimeException(e);}}}public void loss(){}});
  Thread timer=new Thread(()->{try{Thread.sleep(2500);}catch(Exception ignored){}transport.close();});timer.start();try{transport.run();}catch(SocketException e){if(transport.live)throw e;}finally{transport.close();timer.join();}
  check(frames[0]>0,"C# UDP handshake and Android reassembly delivered video");Files.write(Paths.get(args[0],"udp-decoder-input.h264"),data.toByteArray());System.out.println(checks+" Android video protocol checks passed; UDP frames="+frames[0]);
 }
}
