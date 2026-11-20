using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Reflection;
using QRCoder;

sealed class PhoneTransfer : Window {
    static readonly string Inbox=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"EOpen 接收");
    readonly string route="/"+Guid.NewGuid().ToString("N")+"/";
    readonly TextBlock status=new TextBlock { Text="等待手机发送内容", TextWrapping=TextWrapping.Wrap };
    readonly TcpListener listener;
    readonly object gate=new object();
    readonly System.Collections.Generic.HashSet<TcpClient> clients=new System.Collections.Generic.HashSet<TcpClient>();
    bool stopped;

    public PhoneTransfer() {
        Title="EOpen · 手机传送"; Width=400; SizeToContent=SizeToContent.Height;
        WindowStartupLocation=WindowStartupLocation.CenterScreen; ResizeMode=ResizeMode.CanMinimize;
        Background=Brushes.Black; Foreground=Brushes.White; FontSize=14;
        var ips=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up &&
            (n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211 || n.NetworkInterfaceType==NetworkInterfaceType.Ethernet))
            .OrderByDescending(n=>n.GetIPProperties().GatewayAddresses.Count>0)
            .ThenByDescending(n=>n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211)
            .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address)
            .Where(a=>a.AddressFamily==AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        var ip=ips.FirstOrDefault();
        if(ip==null) throw new IOException("请先连接 Wi-Fi 或有线局域网。");
        Directory.CreateDirectory(Inbox);
        listener=new TcpListener(ip,0);
        listener.Start(8);
        try {
            string url="http://"+listener.LocalEndpoint+route;
            var panel=new StackPanel { Margin=new Thickness(20) }; Content=panel;
            panel.Children.Add(new TextBlock { Text="手机传送",FontSize=22,FontWeight=FontWeights.Bold });
            panel.Children.Add(new TextBlock { Text="手机连接同一 Wi-Fi 后扫码，关闭窗口即停止接收。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,12) });
            using(var generator=new QRCodeGenerator())
            using(var data=generator.CreateQrCode(url,QRCodeGenerator.ECCLevel.M))
            using(var qr=new PngByteQRCode(data))
            using(var bytes=new MemoryStream(qr.GetGraphic(6))) {
                var bitmap=new BitmapImage(); bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
                bitmap.StreamSource=bytes;bitmap.EndInit();bitmap.Freeze();
                var image=new Image { Source=bitmap,Width=216,Height=216 };RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.NearestNeighbor);
                panel.Children.Add(image);
            }
            var link=new TextBox { Text=url,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,10) };
            panel.Children.Add(link);panel.Children.Add(status);
            var folder=new Button { Content="打开接收文件夹",Padding=new Thickness(8),Margin=new Thickness(0,12,0,0) };
            folder.Click+=delegate { Process.Start(new ProcessStartInfo(Inbox){UseShellExecute=true}); };panel.Children.Add(folder);
            Closed+=delegate { lock(gate){stopped=true;listener.Stop();foreach(var c in clients)c.Close();} };
            var thread=new Thread(Serve){IsBackground=true};thread.Start();
        } catch { listener.Stop(); throw; }
    }
    void Serve() {
        while(true) {
            try {
                var client=listener.AcceptTcpClient();
                lock(gate){
                    if(stopped){client.Close();return;}
                    if(clients.Count>=8){client.Close();continue;}
                    clients.Add(client);
                }
                ThreadPool.QueueUserWorkItem(delegate {
                    try {
                        client.ReceiveTimeout=30000;client.SendTimeout=10000;
                        using(var stream=client.GetStream()) Handle(stream);
                    } catch(IOException) { } catch(SocketException) { } catch(ObjectDisposedException) { }
                    catch(Exception ex) { lock(gate){if(!stopped)Dispatcher.BeginInvoke(new Action(()=>status.Text="接收失败："+ex.Message));} }
                    finally { lock(gate){clients.Remove(client);}client.Close(); }
                });
            } catch(Exception ex) {
                lock(gate){if(stopped)return;}
                if(!(ex is IOException || ex is SocketException || ex is ObjectDisposedException))
                    Dispatcher.BeginInvoke(new Action(()=>status.Text="接收失败："+ex.Message));
            }
        }
    }
    static void Reply(Stream s,int code,string type,string body) {
        byte[] bytes=Encoding.UTF8.GetBytes(body);
        byte[] hdr=Encoding.ASCII.GetBytes("HTTP/1.1 "+code+" "+(code==201?"Created":code==200?"OK":"Error")+"\r\nContent-Type: "+type+"; charset=utf-8\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\nContent-Length: "+bytes.Length+"\r\n\r\n");
        s.Write(hdr,0,hdr.Length);s.Write(bytes,0,bytes.Length);
    }
    void Handle(Stream stream) {
        var header=new StringBuilder();int suffix=0;
        while(header.Length<16384){
            int b=stream.ReadByte();if(b<0)return;
            suffix=(suffix<<8)|b;header.Append((char)b);if(suffix==0x0d0a0d0a)break;
        }
        if(suffix!=0x0d0a0d0a){Reply(stream,400,"text/plain","请求头过长");return;}
        var lines=header.ToString().Split(new[]{"\r\n"},StringSplitOptions.None);
        var req=lines[0].Split(' ');
        if(req.Length!=3 || !req[1].StartsWith(route,StringComparison.Ordinal)){Reply(stream,404,"text/plain","接收地址无效");return;}
        var target=req[1].Substring(route.Length);
        if(req[0]=="GET" && target==""){
            using(var page=new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("phone.html")))
                Reply(stream,200,"text/html",page.ReadToEnd());
            return;
        }
        bool text=target.StartsWith("text?name=",StringComparison.Ordinal);
        if(req[0]!="POST" || (!text && !target.StartsWith("file?name=",StringComparison.Ordinal))){Reply(stream,404,"text/plain","请求无效");return;}
        long length;var lengths=lines.Where(l=>l.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(lengths.Length!=1 || !long.TryParse(lengths[0].Substring(15).Trim(),out length) || length<0 || length>(text?4L:512L)*1024*1024){
            Reply(stream,413,"text/plain",text?"文字限 4 MB":"单个文件限 512 MB");return;
        }
        string raw=text?"文字.txt":Uri.UnescapeDataString(target.Substring(10));
        string name=new string(raw.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c).Take(120).ToArray()).TrimEnd(' ','.');
        if(name.Length==0)name="文件";
        string id=Guid.NewGuid().ToString("N");
        string temp=Path.Combine(Inbox,"."+id+".part");
        string final=Path.Combine(Inbox,DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+id.Substring(0,8)+"-"+name);
        try {
            using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                byte[] chunk=new byte[65536];long left=length;
                while(left>0){int n=stream.Read(chunk,0,(int)Math.Min(left,chunk.Length));if(n<=0)throw new EndOfStreamException();file.Write(chunk,0,n);left-=n;}
            }
            File.Move(temp,final);
        } finally {if(File.Exists(temp))File.Delete(temp);}
        Dispatcher.BeginInvoke(new Action(()=>status.Text="已收到："+name));
        Reply(stream,201,"text/plain","已收到");
    }
}

