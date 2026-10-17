using System.Drawing.Drawing2D;

namespace Downloader;

// 文件类型徽标：实色圆角方块 + 白色加粗扩展名（对齐迅雷样式）。
// 底色一律压在中低明度：白字在浅色底上（比如最初 EXE 用的浅青蓝）对比度只有 2:1 左右，
// 7px 字号下会糊成一团，这里保证与白字的对比度不低于约 3.5:1，同时按用途归类取色
static class FileTypeBadge
{
    static readonly Color Fallback = FromRgb(0x6C, 0x75, 0x7D);

    static readonly Dictionary<string, Color> Palette = new(StringComparer.OrdinalIgnoreCase)
    {
        // 程序与安装包
        ["exe"] = FromRgb(0x2F, 0x6F, 0xDB),
        ["msi"] = FromRgb(0x2F, 0x6F, 0xDB),
        ["appx"] = FromRgb(0x2F, 0x6F, 0xDB),
        ["msix"] = FromRgb(0x2F, 0x6F, 0xDB),
        ["apk"] = FromRgb(0x2F, 0x6F, 0xDB),
        ["deb"] = FromRgb(0x2F, 0x6F, 0xDB),
        ["rpm"] = FromRgb(0x2F, 0x6F, 0xDB),

        // 压缩包
        ["zip"] = FromRgb(0xC2, 0x57, 0x0C),
        ["rar"] = FromRgb(0xC2, 0x57, 0x0C),
        ["7z"] = FromRgb(0xC2, 0x57, 0x0C),
        ["tar"] = FromRgb(0xC2, 0x57, 0x0C),
        ["gz"] = FromRgb(0xC2, 0x57, 0x0C),
        ["tgz"] = FromRgb(0xC2, 0x57, 0x0C),
        ["bz2"] = FromRgb(0xC2, 0x57, 0x0C),
        ["xz"] = FromRgb(0xC2, 0x57, 0x0C),

        // 视频
        ["mp4"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["mkv"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["avi"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["mov"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["flv"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["wmv"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["webm"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["m4v"] = FromRgb(0x6B, 0x3F, 0xD4),
        ["ts"] = FromRgb(0x6B, 0x3F, 0xD4),

        // 音频
        ["mp3"] = FromRgb(0x0C, 0x7F, 0x92),
        ["flac"] = FromRgb(0x0C, 0x7F, 0x92),
        ["wav"] = FromRgb(0x0C, 0x7F, 0x92),
        ["aac"] = FromRgb(0x0C, 0x7F, 0x92),
        ["ogg"] = FromRgb(0x0C, 0x7F, 0x92),
        ["m4a"] = FromRgb(0x0C, 0x7F, 0x92),
        ["wma"] = FromRgb(0x0C, 0x7F, 0x92),
        ["opus"] = FromRgb(0x0C, 0x7F, 0x92),

        // 图片
        ["jpg"] = FromRgb(0xD6, 0x33, 0x6C),
        ["jpeg"] = FromRgb(0xD6, 0x33, 0x6C),
        ["png"] = FromRgb(0xD6, 0x33, 0x6C),
        ["gif"] = FromRgb(0xD6, 0x33, 0x6C),
        ["webp"] = FromRgb(0xD6, 0x33, 0x6C),
        ["bmp"] = FromRgb(0xD6, 0x33, 0x6C),
        ["svg"] = FromRgb(0xD6, 0x33, 0x6C),
        ["ico"] = FromRgb(0xD6, 0x33, 0x6C),
        ["heic"] = FromRgb(0xD6, 0x33, 0x6C),
        ["tif"] = FromRgb(0xD6, 0x33, 0x6C),
        ["tiff"] = FromRgb(0xD6, 0x33, 0x6C),

        // 文档（沿用各办公套件的惯用色）
        ["pdf"] = FromRgb(0xC0, 0x39, 0x2B),
        ["doc"] = FromRgb(0x1F, 0x4E, 0x9C),
        ["docx"] = FromRgb(0x1F, 0x4E, 0x9C),
        ["odt"] = FromRgb(0x1F, 0x4E, 0x9C),
        ["rtf"] = FromRgb(0x1F, 0x4E, 0x9C),
        ["xls"] = FromRgb(0x1D, 0x7A, 0x4D),
        ["xlsx"] = FromRgb(0x1D, 0x7A, 0x4D),
        ["csv"] = FromRgb(0x1D, 0x7A, 0x4D),
        ["ods"] = FromRgb(0x1D, 0x7A, 0x4D),
        ["ppt"] = FromRgb(0xB7, 0x41, 0x1E),
        ["pptx"] = FromRgb(0xB7, 0x41, 0x1E),
        ["odp"] = FromRgb(0xB7, 0x41, 0x1E),

        // 光盘镜像
        ["iso"] = FromRgb(0x3A, 0x4E, 0x8C),
        ["img"] = FromRgb(0x3A, 0x4E, 0x8C),
        ["bin"] = FromRgb(0x3A, 0x4E, 0x8C),

        // 字体
        ["ttf"] = FromRgb(0x5C, 0x3A, 0x1E),
        ["otf"] = FromRgb(0x5C, 0x3A, 0x1E),
        ["woff"] = FromRgb(0x5C, 0x3A, 0x1E),
        ["woff2"] = FromRgb(0x5C, 0x3A, 0x1E),

        // 电子书
        ["epub"] = FromRgb(0x8A, 0x6D, 0x00),
        ["mobi"] = FromRgb(0x8A, 0x6D, 0x00),
        ["azw3"] = FromRgb(0x8A, 0x6D, 0x00),
        ["fb2"] = FromRgb(0x8A, 0x6D, 0x00),

        // 纯文本与源码
        ["txt"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["md"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["json"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["xml"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["yaml"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["yml"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["log"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["ini"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["sql"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["py"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["js"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["java"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["cs"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["go"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["rs"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["php"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["rb"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["sh"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["bat"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["ps1"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["html"] = FromRgb(0x5B, 0x6B, 0x7A),
        ["css"] = FromRgb(0x5B, 0x6B, 0x7A),
    };

    // 超过 4 个字符的扩展名（如 xlsx 之外的长尾格式）在 20px 方块里放不下，退回纯文字行
    public static bool ShowFor(string ext) => ext.Length is > 0 and <= 4;

    public static Color ColorFor(string ext) => Palette.TryGetValue(ext, out var c) ? c : Fallback;

    public static void Draw(Graphics g, RectangleF r, string ext)
    {
        var color = Palette.TryGetValue(ext, out var c) ? c : Fallback;
        using (var bg = RoundRect(r, r.Width * 0.225f))
        using (var fill = new SolidBrush(color))
        {
            g.FillPath(fill, bg);
        }

        // 字号从方块宽的 35% 起试，装得下就用它，装不下逐级缩小，
        // 保证 DOCX / JPEG 这类四字母标签不会顶到边
        string label = ext.ToUpperInvariant();
        float unit = r.Width / 20f;
        float em = r.Width * 0.35f;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new GraphicsPath();
            text.AddString(label, font.FontFamily, (int)font.Style, em, PointF.Empty, StringFormat.GenericTypographic);
            var ink = text.GetBounds();
            if (attempt == 3 || ink.Width <= r.Width - 3f * unit)
            {
                // 按字形墨迹居中：MeasureText 的宽度含侧边距，直接用它会导致文字偏左
                g.TranslateTransform(
                    r.X + (r.Width - ink.Width) / 2 - ink.X,
                    r.Y + (r.Height - ink.Height) / 2 - ink.Y);
                using var white = new SolidBrush(Color.White);
                g.FillPath(white, text);
                g.ResetTransform();
                return;
            }
            em *= 0.86f;
        }
    }

    static Color FromRgb(int red, int green, int blue) => Color.FromArgb(red, green, blue);

    static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
