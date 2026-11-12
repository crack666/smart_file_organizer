using System.Globalization;
using System.IO.Compression;
using System.Text;
using SmartFileOrganizer.Infrastructure.Content;

namespace SmartFileOrganizer.Infrastructure.Evaluation;

/// <summary>A small BGRA pixel buffer with a few drawing primitives, enough to paint corpus images.</summary>
internal sealed class Canvas
{
    private readonly byte[] _bgra;

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        _bgra = new byte[width * height * 4];
    }

    public int Width { get; }
    public int Height { get; }

    public void Set(int x, int y, (int R, int G, int B) c)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var i = (y * Width + x) * 4;
        _bgra[i] = Clamp(c.B);
        _bgra[i + 1] = Clamp(c.G);
        _bgra[i + 2] = Clamp(c.R);
        _bgra[i + 3] = 255;
    }

    public (int R, int G, int B) Get(int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (_bgra[i + 2], _bgra[i + 1], _bgra[i]);
    }

    public void Fill(int x0, int y0, int x1, int y1, (int R, int G, int B) c)
    {
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
                Set(x, y, c);
    }

    public void VerticalGradient(int y0, int y1, (int R, int G, int B) top, (int R, int G, int B) bottom)
    {
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
        {
            var t = (double)(y - y0) / Math.Max(1, y1 - y0 - 1);
            var c = (Lerp(top.R, bottom.R, t), Lerp(top.G, bottom.G, t), Lerp(top.B, bottom.B, t));
            for (var x = 0; x < Width; x++) Set(x, y, c);
        }
    }

    public void Circle(int cx, int cy, int r, (int R, int G, int B) c)
    {
        for (var y = cy - r; y <= cy + r; y++)
            for (var x = cx - r; x <= cx + r; x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                    Set(x, y, c);
    }

    /// <summary>A filled triangle with its tip at (cx, top) and its base on <paramref name="baseY"/>.</summary>
    public void Peak(int cx, int top, int baseY, int halfWidth, (int R, int G, int B) c)
    {
        for (var y = top; y <= baseY; y++)
        {
            var w = (int)((double)(y - top) / Math.Max(1, baseY - top) * halfWidth);
            for (var x = cx - w; x <= cx + w; x++) Set(x, y, c);
        }
    }

    public void Noise(Random random, int amount)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = Get(x, y);
                var n = random.Next(-amount, amount + 1);
                Set(x, y, (r + n, g + n, b + n));
            }
    }

    public byte[] ToPng() => PngEncoder.EncodeBgra(_bgra, Width, Height);

    /// <summary>Gray values (one byte per pixel), for an image embedded in a PDF.</summary>
    public byte[] ToGray()
    {
        var gray = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var (r, g, b) = Get(x, y);
                gray[y * Width + x] = Clamp((r * 299 + g * 587 + b * 114) / 1000);
            }
        return gray;
    }

    private static int Lerp(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);
    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}

/// <summary>
/// Writes minimal, valid single-page PDF files: text in Helvetica, or one gray image filling the
/// page (a "scan"). ASCII text only, so no font encoding is needed.
/// </summary>
internal static class MiniPdf
{
    public static byte[] Text(string title, IEnumerable<string> lines)
    {
        var content = new StringBuilder();
        content.Append("BT /F1 15 Tf 56 780 Td (").Append(Escape(title)).Append(") Tj ET\n");
        content.Append("BT /F1 10.5 Tf 56 750 Td 15 TL\n");
        foreach (var line in lines)
            content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
        content.Append("ET\n");

        return Build(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Encoding.Latin1.GetBytes(content.ToString()),
            extraObject: null);
    }

    /// <summary>A page that is one gray image, like a scanned sheet.</summary>
    public static byte[] Scan(Canvas page)
    {
        var compressed = Deflate(page.ToGray());
        var image = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} ")
            .Append(CultureInfo.InvariantCulture, $"/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n")
            .ToString();
        var content = Encoding.Latin1.GetBytes("q 595 0 0 842 0 0 cm /Im1 Do Q\n");
        return Build(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im1 6 0 R >> >> /Contents 5 0 R >>",
            content,
            extraObject: (Encoding.Latin1.GetBytes(image), compressed));
    }

    private static byte[] Build(string page, byte[] content, (byte[] Head, byte[] Stream)? extraObject)
    {
        using var output = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) { var b = Encoding.Latin1.GetBytes(s); output.Write(b); }
        void Obj(int n, string body)
        {
            offsets.Add(output.Position);
            Raw($"{n} 0 obj\n{body}\nendobj\n");
        }

        Raw("%PDF-1.4\n%âãÏÓ\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, page);
        Obj(4, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        offsets.Add(output.Position);
        Raw(string.Create(CultureInfo.InvariantCulture, $"5 0 obj\n<< /Length {content.Length} >>\nstream\n"));
        output.Write(content);
        Raw("\nendstream\nendobj\n");

        if (extraObject is { } extra)
        {
            offsets.Add(output.Position);
            Raw("6 0 obj\n");
            output.Write(extra.Head);
            output.Write(extra.Stream);
            Raw("\nendstream\nendobj\n");
        }

        var xref = output.Position;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(sb.ToString());
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
