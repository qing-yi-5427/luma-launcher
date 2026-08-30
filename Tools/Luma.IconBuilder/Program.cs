using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Luma.IconBuilder <source.png> <output.png> <output.ico>");
    return 1;
}

var source = LoadBitmap(args[0]);
var master = RenderMaster(source, 1024);
SavePng(master, args[1]);
SaveIco(master, args[2], [16, 20, 24, 32, 40, 48, 64, 128, 256]);
return 0;

static BitmapSource LoadBitmap(string path)
{
    using var stream = File.OpenRead(path);
    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    return decoder.Frames[0];
}

static BitmapSource RenderMaster(BitmapSource source, int size)
{
    var visual = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);

    // ImageGen supplied the artwork; this rounded clip replaces its painted
    // checkerboard margin with real alpha without changing the selected mark.
    // Clip a couple of pixels inside the generated edge so none of the former
    // checkerboard's light antialiasing fringe survives at icon sizes.
    var bounds = new Rect(size * 0.044, size * 0.043, size * 0.913, size * 0.914);
    var radius = size * 0.168;
    using (var drawing = visual.RenderOpen())
    {
        drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
        drawing.DrawImage(source, new Rect(0, 0, size, size));
        drawing.Pop();
    }

    var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    target.Freeze();
    return target;
}

static BitmapSource Resize(BitmapSource source, int size)
{
    var visual = new DrawingVisual();
    RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.Fant);
    using (var drawing = visual.RenderOpen())
        drawing.DrawImage(source, new Rect(0, 0, size, size));

    var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    target.Render(visual);
    target.Freeze();
    return target;
}

static byte[] EncodePng(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

static void SavePng(BitmapSource bitmap, string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllBytes(path, EncodePng(bitmap));
}

static void SaveIco(BitmapSource master, string path, int[] sizes)
{
    var frames = sizes.Select(size => (Size: size, Data: EncodePng(Resize(master, size)))).ToArray();
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)frames.Length);

    var offset = 6 + frames.Length * 16;
    foreach (var frame in frames)
    {
        writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
        writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(frame.Data.Length);
        writer.Write(offset);
        offset += frame.Data.Length;
    }

    foreach (var frame in frames)
        writer.Write(frame.Data);
}
