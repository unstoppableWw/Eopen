using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

class EOpen : Window
{
    [DllImport("user32.dll", SetLastError=true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    readonly List<Entry> apps = new List<Entry>();
    readonly string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "folders.txt");
    readonly TextBox search = new TextBox();
    readonly ListBox list = new ListBox();
    readonly TextBlock hint = new TextBlock();
    readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
    readonly List<Entry> entries = new List<Entry>();
    IntPtr hwnd, previous;
    char shortcut;
    bool registered;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory {
        [PreserveSig] int GetImage(System.Drawing.Size size, uint flags, out IntPtr bitmap);
    }
    [DllImport("shell32.dll", CharSet=CharSet.Unicode, PreserveSig=true)]
    static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    static readonly Dictionary<string,ImageSource> icons = new Dictionary<string,ImageSource>(StringComparer.OrdinalIgnoreCase);
    static ImageSource ShellIcon(string path) {
        lock(icons) return CachedShellIcon(path);
    }
    static readonly Queue<string> iconOrder=new Queue<string>();
    static ImageSource CachedShellIcon(string path) {
        ImageSource image;
        if(icons.TryGetValue(path,out image)) return image;
        Guid iid=typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory factory;
        if(SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out factory)>=0) {
            IntPtr bitmap=IntPtr.Zero;
            try {
                if(factory.GetImage(new System.Drawing.Size(32,32),4,out bitmap)>=0 && bitmap!=IntPtr.Zero)
                    image=Imaging.CreateBitmapSourceFromHBitmap(bitmap,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            } finally {
                if(bitmap!=IntPtr.Zero) DeleteObject(bitmap);
                Marshal.ReleaseComObject(factory);
            }
        }
        if(image==null) image=Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Application.Handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        if(iconOrder.Count>=96) icons.Remove(iconOrder.Dequeue());
        iconOrder.Enqueue(path); icons[path]=image;
        return image;
    }
    class Entry {
        public string Alias, Name, Folder, AppId, Target; public bool App;
        public string Label { get { return Name; } }
        public string Identity { get { return Target ?? AppId ?? Folder ?? Name; } }
        public ImageSource Icon { get { return (this==capture || this==phone || this==lan) ? brandImage : ShellIcon(AppId==null ? Folder : @"shell:AppsFolder\" + AppId); } }
        public override string ToString() { return Name; }
    }
    static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }

    static readonly Entry capture = new Entry { Name="区域截图", Alias="jt" };
    static readonly System.Drawing.Icon brand = System.Drawing.Icon.ExtractAssociatedIcon(
        System.Reflection.Assembly.GetExecutingAssembly().Location);
    static readonly ImageSource brandImage = Imaging.CreateBitmapSourceFromHIcon(brand.Handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
    static readonly Entry phone=new Entry { Name="微信文件传输助手（网页版）", Alias="sj" };
    static readonly Entry lan=new Entry { Name="局域网传送", Alias="lan" };
    PhoneTransfer receiver;
    bool capturing;
    bool captureKeyRegistered;
    const int CaptureHotkeyId = 2;
    // A five-line pixel wordmark, drawn at integer coordinates.
    static FrameworkElement Wordmark() {
        string[] rows = {
            "1111 01110 0000 0000 0000",
            "1000 10001 1110 0110 1110",
            "1110 10001 1001 1001 1001",
            "1000 10001 1110 1110 1001",
            "1111 01110 1000 0111 1001",
            "0000 00000 1000 0000 0000"
        };
        var group=new GeometryGroup();
        for(int y=0;y<rows.Length;y++)
            for(int x=0;x<rows[y].Length;x++)
                if(rows[y][x]=='1') group.Children.Add(new RectangleGeometry(new Rect(x*3,y*3,3,3)));
        return new System.Windows.Shapes.Path { Data=group, Fill=Color("#F7F7F7"), VerticalAlignment=VerticalAlignment.Center };
    }
    void TakeShot() {
        if(capturing) return;
        capturing=true;
        if(IsVisible) Dismiss();
        try {
            RegionCapture.CopySelection();
        } catch(Exception ex) { Summon(); hint.Text="截图失败："+ex.Message; }
        finally { capturing=false; }
    }

    sealed class PixelDog : FrameworkElement {
        readonly System.Windows.Threading.DispatcherTimer timer=new System.Windows.Threading.DispatcherTimer();
        int dx,dy;
        static readonly string[] pixels={
            "   dd         dd   ", "  dwwd       dwwd  ", "  dwwwdddddddwwwd  ",
            "  dwwwwwwwwwwwwwd  ", " dwwwwwwwwwwwggwwd ", " dwwwwwwwwwwgggwwd ",
            " dwwwwwwwwwwgggwwd ", " dwwwwwwwwwwwwwwwd ", "  dwwwwwdddwwwwwd  ",
            "   dwwwwwdwwwwwd   ", "    ddwwdwdwwdd    ", "      ddddddd      "};
        public PixelDog() {
            Width=50; Height=32; IsHitTestVisible=false;
            timer.Interval=TimeSpan.FromMilliseconds(55);
            timer.Tick+=delegate {
                if(PresentationSource.FromVisual(this)==null) return;
                var cursor=Forms.Cursor.Position;
                var pos=PointFromScreen(new Point(cursor.X,cursor.Y));
                var offset=pos-new Point(Width/2,Height/2);
                double radius=Math.Max(65,offset.Length);
                int x=(int)Math.Round(5*offset.X/radius);
                int y=(int)Math.Round(3*offset.Y/radius);
                if(x!=dx || y!=dy) { dx=x; dy=y; InvalidateVisual(); }
            };
            IsVisibleChanged+=delegate { if(IsVisible) timer.Start(); else timer.Stop(); };
        }
        protected override void OnRender(DrawingContext dc) {
            dc.PushTransform(new TranslateTransform(6+dx,4+dy));
            for(int y=0;y<pixels.Length;y++)
                for(int x=0;x<pixels[y].Length;x++) {
                    char c=pixels[y][x];
                    if(c!=' ') dc.DrawRectangle(c=='w'?Brushes.WhiteSmoke:c=='g'?Brushes.Silver:Brushes.Black,null,new Rect(x*2,y*2,2,2));
                }
            foreach(int x in new[]{10,24}) {
                dc.DrawRectangle(Brushes.White,null,new Rect(x-1,10,6,5));
                dc.DrawRectangle(Brushes.Black,null,new Rect(x+Math.Sign(dx),11+Math.Sign(dy),3,3));
            }
            dc.Pop();
        }
    }
    EOpen() {
        brandImage.Freeze();
        VirtualizingStackPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
        Title = "EOpen"; Icon = brandImage; UseLayoutRounding = true; SnapsToDevicePixels = true;
        Width = 520; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var panel = new StackPanel { Margin = new Thickness(22) };
        var head = new DockPanel();
        var dog = new PixelDog();
        DockPanel.SetDock(dog,Dock.Right); head.Children.Add(dog);
        var identity=new StackPanel { Orientation=Orientation.Horizontal };
        identity.Children.Add(new Image { Source=brandImage, Width=32, Height=32, Margin=new Thickness(0,0,9,0) });
        identity.Children.Add(Wordmark());
        var dragArea = new Border { Background=Brushes.Transparent, Cursor=Cursors.SizeAll, Child=identity };
        dragArea.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
            if(e.LeftButton == MouseButtonState.Pressed) { e.Handled = true; DragMove(); }
        };
        head.Children.Add(dragArea);
        panel.Children.Add(head);
        search.Foreground=Color("#FFFFFF"); search.CaretBrush=Color("#F1F1F1"); search.FontSize = 21; search.Padding = new Thickness(9,5,9,5);
        search.BorderThickness = new Thickness(0);
        search.Background = Brushes.Transparent;
        var field = new Border { CornerRadius=new CornerRadius(6), Background=Color("#101010"),
            BorderBrush=Color("#929292"), BorderThickness=new Thickness(1), Padding=new Thickness(4,2,4,2),
            Margin=new Thickness(0,14,0,8), Child=search };
        System.Windows.Automation.AutomationProperties.SetName(search,"搜索目录、应用或简称");
        panel.Children.Add(field);
        list.FontSize = 16; list.MaxHeight = Math.Min(480,SystemParameters.WorkArea.Height * 0.52); list.MinHeight = 48;
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Hidden);
        list.BorderThickness = new Thickness(0); list.Background = Brushes.Transparent;
                var row = new Style(typeof(ListBoxItem));
        row.Setters.Add(new Setter(Control.ForegroundProperty,Color("#EAEAEA")));
        row.Setters.Add(new Setter(Control.TemplateProperty,System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Border Name='tile' Background='Transparent' BorderBrush='Transparent' BorderThickness='4,0,0,0' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='tile' Property='Background' Value='#292929'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='tile' Property='Background' Value='#383838'/><Setter TargetName='tile' Property='BorderBrush' Value='#FFFFFF'/></Trigger></ControlTemplate.Triggers></ControlTemplate>")));
        row.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,5,8,5)));
        row.Setters.Add(new Setter(System.Windows.Automation.AutomationProperties.NameProperty,new Binding("Label")));
        list.ItemContainerStyle = row;
        var layout = new FrameworkElementFactory(typeof(StackPanel));
        layout.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(FrameworkElement.WidthProperty,24.0);
        icon.SetValue(FrameworkElement.HeightProperty,24.0);
        icon.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,10,0));
        icon.SetBinding(Image.SourceProperty,new Binding("Icon"){ IsAsync=true });
        layout.AppendChild(icon);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty,new Binding("Label"));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
        layout.AppendChild(label);
        list.ItemTemplate = new DataTemplate { VisualTree=layout };
        panel.Children.Add(list);
        hint.Foreground=Color("#B4B4B4"); hint.FontSize=12; hint.Margin = new Thickness(2,12,0,0); hint.TextWrapping = TextWrapping.Wrap;
        var hintStyle = new Style(typeof(TextBlock));
        var emptyHint = new Trigger { Property=TextBlock.TextProperty, Value="" };
        emptyHint.Setters.Add(new Setter(VisibilityProperty,Visibility.Collapsed));
        hintStyle.Triggers.Add(emptyHint); hint.Style=hintStyle;
        panel.Children.Add(hint);
        Content = new Border { CornerRadius = new CornerRadius(12), Background = Color("#191919"),
            BorderBrush = Color("#505050"), BorderThickness = new Thickness(2), Child = panel };
        search.TextChanged += delegate { Filter(); };
        PreviewKeyDown += OnKey;
        Activated += delegate { UpdateEscape(false); };
        Deactivated += delegate { UpdateEscape(IsVisible); };
        IsVisibleChanged += delegate { UpdateEscape(IsVisible && !IsActive); };
        Closing += delegate { UpdateEscape(false); };
        list.MouseDoubleClick += (s,e) => { if (ItemsControl.ContainerFromElement(list,e.OriginalSource as DependencyObject) != null) OpenSelected(); };
        SourceInitialized += delegate {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(Hook);
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开面板",null,delegate { Summon(); });
        menu.Items.Add("区域截图    Alt+A",null,delegate { TakeShot(); });

        menu.Items.Add("退出EOpen",null,delegate { Close(); });
        tray.Icon = brand; tray.Text = "EOpen · 快速启动器";
        tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { Summon(); }; tray.Visible = true;
        Closed += delegate { if(receiver!=null) receiver.Close(); if(registered) UnregisterHotKey(hwnd,1); if(captureKeyRegistered) UnregisterHotKey(hwnd,CaptureHotkeyId); tray.Dispose(); menu.Dispose(); };
    }
    DateTime configStamp;
    string LoadConfig() {
        var stamp=File.GetLastWriteTimeUtc(config);
        if(registered && stamp==configStamp) return "";
        var parsed = new List<Entry>(); char key = ' ';
        foreach(var raw in File.ReadAllLines(config)) {
            var line = raw.Trim(); if(line.Length == 0 || line.StartsWith("#")) continue;
            if(line.StartsWith("hotkey=",StringComparison.OrdinalIgnoreCase)) {
                var val = line.Substring(7).Trim().ToUpperInvariant();
                if(val == "ALT+SPACE") { key = ' '; continue; }
                if(val.Length != 1 || val[0] < 'A' || val[0] > 'Z') throw new FormatException("hotkey 请填写 Alt+Space 或一个 A 到 Z 的字母");
                key = val[0]; continue;
            }
            var p = line.Split('|').Select(x=>x.Trim()).ToArray();
            if(p.Length != 3 || p.Any(String.IsNullOrEmpty)) throw new FormatException("请按 简称|名称|完整目录 填写：" + line);
            var dir = p[2] == "@documents" ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : p[2] == "@desktop" ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : Environment.ExpandEnvironmentVariables(p[2]);
            parsed.Add(new Entry { Alias=p[0], Name=p[1], Folder=dir, App=String.Equals(Path.GetExtension(dir),".exe",StringComparison.OrdinalIgnoreCase) });
        }
        entries.Clear(); entries.AddRange(parsed); configStamp=stamp;
        Reindex();
        if(!registered || key != shortcut) {
            if(registered) UnregisterHotKey(hwnd,1);
            shortcut = key; registered = RegisterHotKey(hwnd,1,key == ' ' ? 0x4001u : 0x4003u,key);
        }
        return registered ? "" : (key == ' ' ? "Alt+空格" : "Ctrl+Alt+" + key) + " 注册失败，可能已被其他程序占用。可从托盘打开面板。";
    }
    // GB2312 first-level Han characters are ordered by pronunciation.
    static string Initials(string name) {
        int[] bounds = {1601,1637,1833,2078,2274,2302,2433,2594,2787,3106,3212,3472,3635,3722,3730,3858,4027,4086,4390,4558,4684,4925,5249};
        const string letters = "abcdefghjklmnopqrstwxyz";
        var encoding = System.Text.Encoding.GetEncoding(936);
        var result = new System.Text.StringBuilder();
        foreach(char ch in name) {
            if(ch < 128 && Char.IsLetterOrDigit(ch)) { result.Append(Char.ToLowerInvariant(ch)); continue; }
            var bytes = encoding.GetBytes(new[]{ch});
            if(bytes.Length != 2) continue;
            int code = (bytes[0]-160)*100 + bytes[1]-160;
            if(code < 1601 || code > 5589) continue;
            int index = Array.FindLastIndex(bounds, b=>b<=code);
            result.Append(letters[index]);
        }
        return result.ToString();
    }
    static object ShellMember(object instance,string name,params object[] args) {
        return instance.GetType().InvokeMember(name,System.Reflection.BindingFlags.GetProperty | System.Reflection.BindingFlags.InvokeMethod,
            null,instance,args);
    }
    void IndexApps() {
        var worker = new Thread(delegate() {
            var found = new Dictionary<string,Entry>(StringComparer.OrdinalIgnoreCase);
            try {
                object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                var folder=ShellMember(shell,"NameSpace","shell:AppsFolder");
                var items=(System.Collections.IEnumerable)ShellMember(folder,"Items");
                foreach(object app in items) {
                    string name,id;
                    try { name=ShellMember(app,"Name") as string; id=ShellMember(app,"ExtendedProperty","System.AppUserModel.ID") as string; }
                    finally { Marshal.ReleaseComObject(app); }
                    if(!String.IsNullOrWhiteSpace(name) && !String.IsNullOrWhiteSpace(id) && !id.Contains("\""))
                        found[name] = new Entry { Name=name, Alias=Initials(name), App=true, AppId=id };
                }
                Marshal.ReleaseComObject(items); Marshal.ReleaseComObject(folder); Marshal.ReleaseComObject(shell);
            } catch(System.Runtime.InteropServices.COMException) { } catch(System.Reflection.TargetInvocationException) { }
            var roots = new[]{ Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms };
            object shortcuts=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            foreach(var root in roots) {
                string path = Environment.GetFolderPath(root);
                if(!Directory.Exists(path)) continue;
                try {
                    foreach(string link in Directory.EnumerateFiles(path,"*.lnk",root == Environment.SpecialFolder.Programs ||
                        root == Environment.SpecialFolder.CommonPrograms ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)) {
                        string name = Path.GetFileNameWithoutExtension(link);
                        Entry entry;
                        if(!found.TryGetValue(name,out entry))
                            found[name]=entry=new Entry { Name=name, Alias=Initials(name), App=true, Folder=link };
                        if(entry.Target!=null) continue;
                        try {
                            object shortcut=ShellMember(shortcuts,"CreateShortcut",link);
                            string target,arguments;
                            try { target=ShellMember(shortcut,"TargetPath") as string; arguments=ShellMember(shortcut,"Arguments") as string; }
                            finally { Marshal.ReleaseComObject((object)shortcut); }
                            if(!String.IsNullOrWhiteSpace(target) && String.IsNullOrWhiteSpace(arguments))
                                entry.Target=target;
                        } catch(COMException) { } catch(System.Reflection.TargetInvocationException) { }
                    }
                } catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
            Marshal.ReleaseComObject(shortcuts);
            Dispatcher.BeginInvoke(new Action(delegate {
                apps.Clear(); apps.AddRange(found.Values.OrderBy(e=>e.Name)); Reindex();
                if(IsVisible) { string info=hint.Text; Filter(); if(!registered || !captureKeyRegistered) hint.Text=info; }
            }));
        });
        worker.IsBackground=true; worker.SetApartmentState(ApartmentState.STA); worker.Start();
    }
    void Summon() {
        var active = GetForegroundWindow(); if(active != hwnd) previous = active;
        Show(); Activate(); SetForegroundWindow(hwnd);
        if(!captureKeyRegistered) captureKeyRegistered=RegisterHotKey(hwnd,CaptureHotkeyId,0x4001,(uint)Forms.Keys.A);
        if(SystemParameters.ClientAreaAnimation) {
            var enter=new System.Windows.Media.Animation.DoubleAnimation(0.4,1,TimeSpan.FromMilliseconds(135));
            BeginAnimation(OpacityProperty,enter);
            var slide=new TranslateTransform();
            ((FrameworkElement)Content).RenderTransform=slide;
            slide.BeginAnimation(TranslateTransform.YProperty,new System.Windows.Media.Animation.DoubleAnimation(5,0,TimeSpan.FromMilliseconds(135)));
        }
        string message;
        try { message = LoadConfig(); } catch(Exception ex) { message = ex.Message; }
        if(search.Text.Length==0) Filter(); else search.Clear(); search.Focus(); Keyboard.Focus(search);
        if(!captureKeyRegistered) message += "  截图快捷键 Alt+A 注册失败，可从托盘截图。";
        if(message.Length > 0) hint.Text = message.Trim();
    }
    // Lower scores favor exact matches, prefixes, substrings, then ordered initials/letters.
    static int Match(string value,string query) {
        var text=value;
        if(text==query) return 0;
        int start=text.IndexOf(query,StringComparison.Ordinal);
        if(start==0) return 1+Math.Min(90,text.Length-query.Length);
        if(start>0) return 100+Math.Min(start,99);
        int pos=-1, first=-1;
        foreach(char c in query) {
            pos=text.IndexOf(c,pos+1);
            if(pos<0) return int.MaxValue;
            if(first<0) first=pos;
        }
        return 200+pos-first;
    }
    sealed class SearchItem {
        public Entry Entry;
        public string[] Aliases;
        public int Rank(string q) {
            int rank=int.MaxValue;
            foreach(string alias in Aliases) rank=Math.Min(rank,Match(alias,q));
            return rank;
        }
    }
    SearchItem[] index=new SearchItem[0];
    void Reindex() {
        var names=new HashSet<string>(entries.Select(e=>e.Name),StringComparer.OrdinalIgnoreCase);
        using(var pinyin=new PinyinIndex())
        index=new[]{capture,phone,lan}.Concat(entries).Concat(apps.Where(e=>!names.Contains(e.Name)))
            .GroupBy(e=>e.Identity,StringComparer.OrdinalIgnoreCase)
            .Select(g=>new SearchItem { Entry=g.First(), Aliases=g.SelectMany(e=>SearchTerms(e,pinyin))
                .Select(term=>term.ToUpperInvariant()).Distinct().ToArray() }).ToArray();
    }
    sealed class PinyinIndex : IDisposable {
        [DllImport("icu.dll",CharSet=CharSet.Unicode,ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern IntPtr utrans_openU(string id,int length,int direction,IntPtr rules,int rulesLength,IntPtr parseError,ref int status);
        [DllImport("icu.dll",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern void utrans_transUChars(IntPtr trans,[In,Out,MarshalAs(UnmanagedType.LPArray,ArraySubType=UnmanagedType.U2)] char[] text,ref int length,int capacity,int start,ref int limit,ref int status);
        [DllImport("icu.dll",ExactSpelling=true,CallingConvention=CallingConvention.Cdecl)]
        static extern void utrans_close(IntPtr trans);
        IntPtr converter;
        public PinyinIndex() {
            int status=0;
            try { converter=utrans_openU("Han-Latin; Latin-ASCII",-1,0,IntPtr.Zero,0,IntPtr.Zero,ref status); }
            catch(DllNotFoundException) {} catch(EntryPointNotFoundException) {}
        }
        public string Convert(string name) {
            if(converter==IntPtr.Zero || !name.Any(c=>c>=0x3400 && c<=0x9fff)) return null;
            char[] buffer=new char[name.Length*10+1]; name.CopyTo(0,buffer,0,name.Length);
            int length=name.Length,limit=length,status=0;
            utrans_transUChars(converter,buffer,ref length,buffer.Length,0,ref limit,ref status);
            if(status>0) return null;
            return new string(buffer,0,length);
        }
        public void Dispose() { if(converter!=IntPtr.Zero) utrans_close(converter); }
    }
    static IEnumerable<string> SearchTerms(Entry entry,PinyinIndex pinyin) {
        yield return entry.Name;
        yield return entry.Alias;
        string latin=pinyin.Convert(entry.Name);
        if(latin!=null) {
            yield return new string(latin.Where(Char.IsLetterOrDigit).ToArray());
            // 乐 has two common readings; in 音乐 it is always yuè.
            if(entry.Name.Contains("音乐"))
                yield return new string(latin.Where(Char.IsLetterOrDigit).ToArray()).Replace("yinle","yinyue");
        }
        string target=entry.Target ?? entry.Folder;
        if(entry.App && target!=null && target.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))
            yield return Path.GetFileNameWithoutExtension(target);
    }
    void Filter() {
        string q = search.Text.Trim().ToUpperInvariant();

        if(q.StartsWith("/",StringComparison.Ordinal)) q=q.Substring(1).TrimStart();
        bool alias=q.Length>0 && !q.StartsWith(@"\",StringComparison.Ordinal);
        var hits=!alias ? new List<Entry>() : index
            .Select(item=>new { item.Entry, Score=item.Rank(q) })
            .Where(hit=>hit.Score!=int.MaxValue).OrderBy(hit=>hit.Score)
            .Select(hit=>hit.Entry).ToList();
        list.ItemsSource = hits; list.SelectedIndex = hits.Count > 0 ? 0 : -1;
        list.Visibility=hits.Count==0 ? Visibility.Collapsed : Visibility.Visible;
        hint.Text=q.StartsWith(@"\Q",StringComparison.Ordinal) ? (q.Length==2 ? "输入要搜索的问题" : "Enter  网页搜索") :
            alias && hits.Count==0 ? "没有匹配的结果" : "";
    }
    const int EscapeHotkeyId=3;
    bool escapeRegistered;
    void UpdateEscape(bool needed) {
        if(hwnd==IntPtr.Zero || needed==escapeRegistered) return;
        if(needed) escapeRegistered=RegisterHotKey(hwnd,EscapeHotkeyId,0x4000,(uint)Forms.Keys.Escape);
        else { UnregisterHotKey(hwnd,EscapeHotkeyId); escapeRegistered=false; }
    }
    void Dismiss() {
        bool restore=GetForegroundWindow()==hwnd;
        Hide();
        if(restore && previous!=IntPtr.Zero) SetForegroundWindow(previous);
    }
    void OnKey(object sender, KeyEventArgs e) {
        if(e.Key == Key.ImeProcessed) return;
        if(e.Key==Key.A && Keyboard.Modifiers==ModifierKeys.Control && search.IsKeyboardFocused) search.SelectAll();
        else if(e.Key == Key.Escape) Dismiss();
        else if(e.Key == Key.Enter && (search.IsKeyboardFocusWithin || list.IsKeyboardFocusWithin)) OpenSelected();
        else if((e.Key == Key.Down || e.Key == Key.Up) && list.Items.Count > 0) {
            list.SelectedIndex = Math.Max(0,Math.Min(list.Items.Count-1,list.SelectedIndex+(e.Key==Key.Down?1:-1)));
            list.ScrollIntoView(list.SelectedItem);
        } else return;
        e.Handled = true;
    }
    void OpenSelected() {
        string input=search.Text.Trim();
        if(input.StartsWith(@"\q",StringComparison.OrdinalIgnoreCase)) {
            string question=input.Substring(2).Trim();
            if(question.Length==0) return;
            try {
                string url="https://www.bing.com/search?q="+Uri.EscapeDataString(question);
                Process.Start(new ProcessStartInfo(url){ UseShellExecute=true }); Hide();
            } catch(Exception ex) { hint.Text=ex.Message; }
            return;
        }
        var item = list.SelectedItem as Entry; if(item == null) return;
        if(item==phone) {
            try {
                Process.Start(new ProcessStartInfo("https://filehelper.weixin.qq.com/"){ UseShellExecute=true });
                Hide();
            } catch(Exception ex) { hint.Text="无法打开微信文件传输助手："+ex.Message; }
            return;
        }
        if(item==lan) {
            try {
                if(receiver==null) { receiver=new PhoneTransfer(); receiver.Closed+=delegate { receiver=null; }; }
                Hide(); receiver.Show(); receiver.Activate();
            } catch(Exception ex) { Summon(); hint.Text="无法启动接收："+ex.Message; }
            return;
        }
        if(item==capture) { TakeShot(); return; }
        if(item.AppId != null) {
            try { Process.Start("explorer.exe", "\"shell:AppsFolder\\" + item.AppId + "\""); Hide(); }
            catch(Exception ex) { hint.Text = ex.Message; }
            return;
        }
        if(item.App ? !File.Exists(item.Folder) : !Directory.Exists(item.Folder)) { hint.Text = "找不到目标：" + item.Folder; return; }
        try { Process.Start(new ProcessStartInfo(Path.GetFullPath(item.Folder)){ UseShellExecute=true }); Hide(); }
        catch(Exception ex) { hint.Text = ex.Message; }
    }
    IntPtr Hook(IntPtr h,int msg,IntPtr w,IntPtr l,ref bool handled) {
        if(msg==0x312 && w.ToInt32()==EscapeHotkeyId && IsVisible) {
            handled=true; Dismiss(); return IntPtr.Zero;
        }
        if(msg==0x312 && w.ToInt32()==CaptureHotkeyId) {
            handled=true;
            if(!capturing) Dispatcher.BeginInvoke(new Action(TakeShot));
            return IntPtr.Zero;
        }
        if(!capturing && msg == 0x312 && w.ToInt32()==1) { if(IsVisible && IsActive) Dismiss(); else Summon(); handled=true; }
        return IntPtr.Zero;
    }
    [STAThread] static void Main(string[] args) {
        bool trayStart=args.Contains("--tray");
        bool first;
        using(var single = new Mutex(true,"Local\\QuickFolderPersonal_v1",out first)) {
            if(!first) { if(!trayStart) MessageBox.Show("EOpen已运行，可从系统托盘打开。","EOpen"); return; }
            var app = new Application(); var window = new EOpen();
            if(trayStart) {
                new WindowInteropHelper(window).EnsureHandle();
                window.captureKeyRegistered=RegisterHotKey(window.hwnd,CaptureHotkeyId,0x4001,(uint)Forms.Keys.A);
                try { window.LoadConfig(); } catch(IOException) { } catch(FormatException) { }
            } else window.Summon();
            window.IndexApps(); app.Run();
        }
    }
}













