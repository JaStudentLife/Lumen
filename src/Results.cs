using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Lumen
{
    internal static class Cat
    {
        public const string TopHit = "Top Hit";
        public const string Commands = "Commands";
        public const string Suggestions = "Suggestions";
        public const string Apps = "Applications";
        public const string Calculator = "Calculator";
        public const string Windows = "Open Windows";
        public const string Settings = "Settings";
        public const string System = "System";
        public const string Paths = "Locations";
        public const string Bookmarks = "Bookmarks";
        public const string Folders = "Folders";
        public const string Files = "Documents";
        public const string Recent = "Recent";
        public const string Web = "Web";
        public const string Help = "Help";
    }

    internal enum ExecMode { Primary, Alternate, Admin }

    /// <summary>One row in the results list (or a section header).</summary>
    internal sealed class ResultItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public string Id { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Hint { get; set; }
        public string Category { get; set; }
        public string Group { get; set; }           // header text when it differs from Category
        public bool IsHeader { get; set; }
        public double Score { get; set; }

        public string Glyph { get; set; }
        public Brush GlyphBrush { get; set; }
        public string IconPath { get; set; }

        ImageSource icon;
        public ImageSource Icon
        {
            get { return icon; }
            set { icon = value; Notify("Icon"); Notify("IconVisibility"); Notify("GlyphVisibility"); }
        }

        public Action<ExecMode> Execute { get; set; }
        public string ActionName { get; set; }      // "Open", "Run", "Copy"...
        public string AltActionName { get; set; }   // Ctrl+Enter
        public bool SupportsAdmin { get; set; }     // Ctrl+Shift+Enter
        public string CopyText { get; set; }        // Ctrl+C
        public string FilePath { get; set; }        // for "show in folder" / copy file
        public string AutoComplete { get; set; }    // Tab
        public bool KeepOpen { get; set; }          // Enter changes the query instead of closing
        public bool Confirm { get; set; }           // needs a second Enter

        // Preview pane
        public string Kind { get; set; }
        public List<KeyValuePair<string, string>> Details { get; set; }
        public string Note { get; set; }
        public string PreviewImagePath { get; set; }

        bool armed;
        public bool Armed
        {
            get { return armed; }
            set { armed = value; Notify("Armed"); Notify("Subtitle"); Notify("SubtitleText"); }
        }
        public string SubtitleText { get { return armed ? "Press Enter again to confirm" : Subtitle; } }

        public Visibility HeaderVisibility { get { return IsHeader ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility ItemVisibility { get { return IsHeader ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility IconVisibility { get { return icon != null ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility GlyphVisibility { get { return icon == null && !string.IsNullOrEmpty(Glyph) ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility SubtitleVisibility { get { return string.IsNullOrEmpty(Subtitle) && !armed ? Visibility.Collapsed : Visibility.Visible; } }
        public Visibility HintVisibility { get { return string.IsNullOrEmpty(Hint) ? Visibility.Collapsed : Visibility.Visible; } }

        public void Notify(string name)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        public void AddDetail(string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (Details == null) Details = new List<KeyValuePair<string, string>>();
            Details.Add(new KeyValuePair<string, string>(label, value));
        }

        public static ResultItem Header(string title)
        {
            return new ResultItem { Title = title, IsHeader = true, Id = "header:" + title };
        }

        public override string ToString() { return Category + " | " + Title + " | " + Subtitle; }
    }

    /// <summary>Glyph tiles (Segoe Fluent Icons) for results that have no file icon.</summary>
    internal static class Glyphs
    {
        public const string Search = "", Settings = "", Calc = "", Globe = "", Lock = "",
            Power = "", Restart = "", Sleep = "", SignOut = "", Trash = "", Folder = "",
            Document = "", Bolt = "", Terminal = "", Window = "", Star = "", Link = "",
            Monitor = "", Mute = "", Volume = "", Play = "", Next = "", Prev = "",
            Sun = "", Copy = "", Info = "", Clock = "", Mail = "", Calendar = "",
            Add = "", Code = "", Tools = "", Help = "";

        static readonly Dictionary<string, Brush> cache = new Dictionary<string, Brush>();

        public static Brush Tile(string hex)
        {
            Brush b;
            if (cache.TryGetValue(hex, out b)) return b;
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var g = new LinearGradientBrush(Color.FromRgb((byte)Math.Min(255, c.R + 28), (byte)Math.Min(255, c.G + 28), (byte)Math.Min(255, c.B + 28)), c, 90);
            g.Freeze();
            cache[hex] = g;
            return g;
        }

        public static Brush Gray { get { return Tile("#6E6E73"); } }
        public static Brush Blue { get { return Tile("#0A84FF"); } }
        public static Brush Orange { get { return Tile("#FF9F0A"); } }
        public static Brush Green { get { return Tile("#30B158"); } }
        public static Brush Red { get { return Tile("#FF453A"); } }
        public static Brush Purple { get { return Tile("#9B59D0"); } }
        public static Brush Teal { get { return Tile("#1EA5B8"); } }
        public static Brush Indigo { get { return Tile("#5E5CE6"); } }
    }

    /// <summary>
    /// Loads shell icons/thumbnails on a background STA thread (IShellItemImageFactory works for
    /// files, folders, exe's and shell:AppsFolder\ items, including Store apps). Newest requests first.
    /// </summary>
    internal static class IconLoader
    {
        sealed class Request { public string Key, Path; public int Size; public bool Thumb; public Action<ImageSource> Done; }

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly LinkedList<Request> queue = new LinkedList<Request>();
        static readonly AutoResetEvent signal = new AutoResetEvent(false);
        static readonly object gate = new object();
        static Thread worker;
        static Dispatcher ui;
        static Guid IID_ImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        public static void Init(Dispatcher dispatcher)
        {
            ui = dispatcher;
            worker = new Thread(Loop) { IsBackground = true, Name = "Lumen icons" };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        }

        static string KeyOf(string path, int size, bool thumb) { return path + "|" + size + (thumb ? "|t" : ""); }

        public static ImageSource Cached(string path, int size, bool thumb)
        {
            lock (gate)
            {
                ImageSource img;
                return cache.TryGetValue(KeyOf(path, size, thumb), out img) ? img : null;
            }
        }

        public static void Get(string path, int size, bool thumb, Action<ImageSource> done)
        {
            if (string.IsNullOrEmpty(path) || ui == null) return;
            string key = KeyOf(path, size, thumb);
            lock (gate)
            {
                ImageSource img;
                if (cache.TryGetValue(key, out img)) { if (img != null) done(img); return; }
                queue.AddFirst(new Request { Key = key, Path = path, Size = size, Thumb = thumb, Done = done });
                while (queue.Count > 300) queue.RemoveLast();
            }
            signal.Set();
        }

        static void Loop()
        {
            while (true)
            {
                signal.WaitOne();
                while (true)
                {
                    Request r;
                    lock (gate)
                    {
                        if (queue.Count == 0) break;
                        r = queue.First.Value; queue.RemoveFirst();
                    }
                    ImageSource img;
                    bool known;
                    lock (gate) known = cache.TryGetValue(r.Key, out img);
                    if (!known)
                    {
                        img = Load(r.Path, r.Size, r.Thumb);
                        lock (gate) cache[r.Key] = img;
                    }
                    if (img != null)
                    {
                        var done = r.Done; var result = img;
                        ui.BeginInvoke(new Action(() => done(result)));
                    }
                }
            }
        }

        public static ImageSource Load(string path, int size, bool thumb)
        {
            Native.IShellItemImageFactory f = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref IID_ImageFactory, out f) != 0 || f == null) return null;
                int flags = thumb ? Native.SIIGBF_BIGGERSIZEOK : (Native.SIIGBF_BIGGERSIZEOK | Native.SIIGBF_ICONONLY);
                if (f.GetImage(new Native.SIZE(size, size), flags, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                return FromHBitmap(hbmp);
            }
            catch { return null; }
            finally
            {
                if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        static BitmapSource FromHBitmap(IntPtr hbmp)
        {
            var bm = new Native.BITMAP();
            Native.GetObject(hbmp, Marshal.SizeOf(typeof(Native.BITMAP)), ref bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32
            };
            var px = new byte[w * h * 4];
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            try { Native.GetDIBits(dc, hbmp, 0, (uint)h, px, ref bi, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, dc); }

            bool anyAlpha = false, premultiplied = true;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) anyAlpha = true;
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a) premultiplied = false;
            }
            if (!anyAlpha) { for (int i = 3; i < px.Length; i += 4) px[i] = 255; premultiplied = true; }
            var src = BitmapSource.Create(w, h, 96, 96, premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, px, w * 4);
            src.Freeze();
            return src;
        }
    }
}
