using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Backdrop;

// Public demo assets use invented sample content, never a user's conversations.
// The pixels are composited by the production KeyDetector and GpuRenderer.
// This tool creates no windows, capture sessions, or app configuration.
static unsafe class Demo
{
    const int W = 984, H = 480, Frames = 135;
    static readonly Color Ink = Color.FromArgb(14, 35, 46);
    static readonly Color Muted = Color.FromArgb(72, 98, 110);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 1) { Console.Error.WriteLine("usage: Backdrop.Demo <output-directory>"); return 2; }
        string output = Path.GetFullPath(args[0]);
        foreach (string folder in new[] { "overview", "readability", "wallpaper" }) Directory.CreateDirectory(Path.Combine(output, folder));
        using var gpu = new GpuDevice();
        foreach (var app in new[] { TargetApp.Claude, TargetApp.Codex, TargetApp.T3Code })
        {
            int index = (int)app;
            using var source = Sample(app);
            uint[] raw = Pixels(source);
            using var renderer = new GpuRenderer(gpu, offscreen: true);
            IntPtr texture = gpu.CreateTexture(W, H, GpuDevice.UsageDefault, GpuDevice.BindShaderResource);
            try
            {
                fixed (uint* p = raw) gpu.Upload(texture, p, W * 4);
                renderer.SetCapture(texture, W, H);
                BackgroundKeys keys;
                fixed (uint* p = raw)
                    keys = KeyDetector.Detect((byte*)p, W * 4, W, H, false, -1, app)
                        ?? throw new InvalidOperationException($"sample layout detection failed for {app}");
                renderer.SetKeys(keys, app == TargetApp.T3Code ? 4 : 1);
                renderer.SetWash(0.36);
                renderer.SetCalm(0.82);
                for (int local = 0; local < Frames / 3; local++)
                {
                    int frame = index * (Frames / 3) + local;
                    uint[] wall = WallpaperFrame(frame / (double)Frames);
                    renderer.SetWallpaper(wall, 256, 144);
                    using var rendered = new Bitmap(W, H, PixelFormat.Format32bppArgb);
                    var bits = rendered.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        renderer.RenderOffscreen(1, false, (rows, pitch, rw, rh) =>
                        {
                            for (int y = 0; y < rh; y++) Buffer.MemoryCopy(rows + y * pitch, (byte*)bits.Scan0 + y * bits.Stride, W * 4, W * 4);
                        });
                    }
                    finally { rendered.UnlockBits(bits); }
                    using var hero = Overview(rendered, app);
                    hero.Save(Path.Combine(output, "overview", $"{frame:D4}.png"), ImageFormat.Png);
                    if (frame == 12)
                    {
                        hero.Save(Path.Combine(output, "demo-still.png"), ImageFormat.Png);
                        using var social = Social(rendered);
                        social.Save(Path.Combine(output, "social-preview.png"), ImageFormat.Png);
                    }
                    if (app == TargetApp.T3Code)
                    {
                        using var comparison = Comparison(source, rendered);
                        comparison.Save(Path.Combine(output, "readability", $"{local:D4}.png"), ImageFormat.Png);
                    }
                    using var wallpaper = BitmapFrom(wall, 256, 144);
                    wallpaper.Save(Path.Combine(output, "wallpaper", $"{frame:D4}.png"), ImageFormat.Png);
                }
                Console.WriteLine($"{app}: 45 frames, production GPU compositor; {keys}");
            }
            finally { Marshal.Release(texture); }
        }
        return 0;
    }

    static Bitmap Sample(TargetApp app)
    {
        var b = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(b); Quality(g);
        Color bg = app switch { TargetApp.Claude => Color.FromArgb(252,252,251), TargetApp.Codex => Color.White, _ => Color.FromArgb(228,240,245) };
        Color sidebar = app == TargetApp.T3Code ? Color.FromArgb(214,230,236) : Color.FromArgb(237,240,242);
        g.Clear(bg);
        using (var brush = new SolidBrush(sidebar)) g.FillRectangle(brush, 0, 0, 180, H);
        using (var brush = new SolidBrush(Color.FromArgb(245,247,248))) g.FillRectangle(brush, 0, 0, W, 38);
        Text(g, AppLocator.DisplayName(app), 14, FontStyle.Bold, Ink, 20, 9);
        Text(g, "sample interface", 11, FontStyle.Regular, Muted, 827, 11);
        Text(g, "workspace", 18, FontStyle.Bold, Ink, 20, 64);
        Round(g, Color.FromArgb(222,233,238), 14, 108, 151, 35, 8);
        Text(g, "+  new conversation", 12, FontStyle.Regular, Ink, 24, 117);
        Text(g, "sample chats", 10, FontStyle.Bold, Muted, 23, 170);
        Text(g, "a fresh perspective", 12, FontStyle.Regular, Ink, 23, 200);
        Text(g, "ideas for the weekend", 12, FontStyle.Regular, Muted, 23, 236);
        Text(g, "something worth making", 11, FontStyle.Regular, Muted, 23, 272);
        Text(g, "settings", 12, FontStyle.Regular, Muted, 23, 438);
        Text(g, "a little room to think", 13, FontStyle.Bold, Ink, 219, 60);
        Round(g, Color.FromArgb(218,232,240), 461, 101, 478, 54, 15);
        Text(g, "make my workspace feel a little more like me.", 16, FontStyle.Regular, Ink, 481, 117);
        Text(g, "your favorite view. the same sharp words.", 23, FontStyle.Bold, Ink, 225, 190);
        Text(g, "bring an image or animated gif behind your conversations.", 16, FontStyle.Regular, Ink, 225, 237);
        Text(g, "readability wash softens the view. calm protects the details.", 16, FontStyle.Regular, Ink, 225, 269);
        Round(g, Color.FromArgb(218,230,236), 225, 319, 713, 58, 8);
        Text(g, "claude + codex + t3 code", 16, FontStyle.Bold, Ink, 244, 330);
        Text(g, "one app  /  one tray icon  /  your wallpaper", 12, FontStyle.Regular, Muted, 244, 355);
        Round(g, Color.FromArgb(248,251,252), 224, 418, 714, 43, 12);
        Text(g, "keep the conversation going...", 14, FontStyle.Regular, Muted, 241, 431);
        return b;
    }

    static Bitmap Overview(Bitmap app, TargetApp target)
    {
        var b = new Bitmap(1100, 720);
        using var g = Graphics.FromImage(b); Quality(g);
        g.Clear(Color.FromArgb(233,244,248));
        Text(g, "backdrop", 15, FontStyle.Bold, Color.FromArgb(8,118,148), 58, 25);
        Text(g, "your chats. your backdrop.", 43, FontStyle.Bold, Ink, 54, 56);
        Text(g, "images + animated gifs for claude, codex, and t3 code.", 16, FontStyle.Regular, Muted, 58, 113);
        Round(g, Color.FromArgb(196,215,224), 56, 156, 988, 484, 12);
        g.DrawImageUnscaled(app, 58, 158);
        Text(g, AppLocator.DisplayName(target), 14, FontStyle.Bold, Ink, 58, 658);
        Text(g, "sample interface / production compositor", 11, FontStyle.Regular, Muted, 245, 661);
        Text(g, "github.com/okisooo/backdrop", 12, FontStyle.Bold, Color.FromArgb(8,118,148), 799, 660);
        return b;
    }

    static Bitmap Comparison(Bitmap plain, Bitmap rendered)
    {
        var b = new Bitmap(1100, 490);
        using var g = Graphics.FromImage(b); Quality(g);
        g.Clear(Color.FromArgb(233,244,248));
        Text(g, "a view behind the work.", 34, FontStyle.Bold, Ink, 36, 25);
        Text(g, "the wallpaper moves. your text stays clear.", 16, FontStyle.Regular, Muted, 39, 77);
        Text(g, "plain", 14, FontStyle.Bold, Muted, 39, 125);
        Text(g, "with backdrop", 14, FontStyle.Bold, Color.FromArgb(8,118,148), 570, 125);
        var crop = new Rectangle(183, 92, 790, 388);
        g.DrawImage(plain, new Rectangle(39,160,490,241), crop, GraphicsUnit.Pixel);
        g.DrawImage(rendered, new Rectangle(570,160,490,241), crop, GraphicsUnit.Pixel);
        Text(g, "sample interface rendered by Backdrop's actual GPU compositor", 12, FontStyle.Regular, Muted, 39, 447);
        return b;
    }

    static Bitmap Social(Bitmap app)
    {
        var b = new Bitmap(1280, 640);
        using var g = Graphics.FromImage(b); Quality(g);
        g.Clear(Color.FromArgb(229,242,247));
        Text(g, "backdrop", 20, FontStyle.Bold, Color.FromArgb(8,118,148), 56, 47);
        Text(g, "your chats.", 49, FontStyle.Bold, Ink, 50, 145);
        Text(g, "your backdrop.", 49, FontStyle.Bold, Ink, 50, 210);
        Text(g, "claude / codex / t3 code", 18, FontStyle.Regular, Muted, 56, 306);
        Text(g, "one windows tray app.", 18, FontStyle.Regular, Muted, 56, 340);
        Text(g, "images + animated gifs.", 18, FontStyle.Regular, Muted, 56, 374);
        Round(g, Color.FromArgb(197,215,224), 486, 140, 746, 365, 10);
        g.DrawImage(app, new Rectangle(488,142,742,361));
        Text(g, "sample interface / actual compositor", 11, FontStyle.Regular, Muted, 488, 526);
        Text(g, "github.com/okisooo/backdrop", 17, FontStyle.Bold, Color.FromArgb(8,118,148), 56, 554);
        Text(g, "free + open source", 14, FontStyle.Regular, Muted, 1062, 560);
        return b;
    }

    static uint[] WallpaperFrame(double time)
    {
        var result = new uint[256 * 144];
        double phase = time * Math.Tau;
        for (int y = 0; y < 144; y++) for (int x = 0; x < 256; x++)
        {
            double u = x / 255.0, v = y / 143.0;
            double a = Math.Exp(-((u-.25-.18*Math.Sin(phase))*(u-.25-.18*Math.Sin(phase)) + (v-.4-.2*Math.Cos(phase))*(v-.4-.2*Math.Cos(phase)))/.17);
            double c = Math.Exp(-((u-.8-.13*Math.Cos(phase))*(u-.8-.13*Math.Cos(phase)) + (v-.7-.2*Math.Sin(phase))*(v-.7-.2*Math.Sin(phase)))/.15);
            double d = .5 + .5*Math.Sin(phase + u*4 + v*2);
            int r = (int)Math.Clamp(147 + 63*c - 78*a + 16*d, 0, 255);
            int green = (int)Math.Clamp(211 - 58*c - 33*a + 9*d, 0, 255);
            int blue = (int)Math.Clamp(232 - 54*c - 20*a + 12*d, 0, 255);
            result[y*256+x] = 0xFF000000u | (uint)(r<<16 | green<<8 | blue);
        }
        return result;
    }

    static uint[] Pixels(Bitmap image)
    {
        var result = new uint[image.Width * image.Height];
        var data = image.LockBits(new Rectangle(0,0,image.Width,image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { fixed (uint* p = result) for (int y=0;y<image.Height;y++) Buffer.MemoryCopy((byte*)data.Scan0+y*data.Stride,p+y*image.Width,image.Width*4,image.Width*4); }
        finally { image.UnlockBits(data); }
        return result;
    }
    static Bitmap BitmapFrom(uint[] pixels, int width, int height)
    {
        var image = new Bitmap(width,height,PixelFormat.Format32bppArgb);
        var data = image.LockBits(new Rectangle(0,0,width,height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { fixed (uint* p = pixels) for (int y=0;y<height;y++) Buffer.MemoryCopy(p+y*width,(byte*)data.Scan0+y*data.Stride,width*4,width*4); }
        finally { image.UnlockBits(data); }
        return image;
    }
    static void Quality(Graphics g) { g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit; g.InterpolationMode=InterpolationMode.HighQualityBicubic; }
    static void Text(Graphics g,string text,float size,FontStyle weight,Color color,float x,float y)
    {
        using var font = new Font("Segoe UI",size,weight,GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        g.DrawString(text,font,brush,x,y,StringFormat.GenericTypographic);
    }
    static void Round(Graphics g,Color color,float x,float y,float w,float h,float r)
    {
        using var path = new GraphicsPath(); float d=r*2;
        path.AddArc(x,y,d,d,180,90);path.AddArc(x+w-d,y,d,d,270,90);path.AddArc(x+w-d,y+h-d,d,d,0,90);path.AddArc(x,y+h-d,d,d,90,90);path.CloseFigure();
        using var brush = new SolidBrush(color);g.FillPath(brush,path);
    }
}
