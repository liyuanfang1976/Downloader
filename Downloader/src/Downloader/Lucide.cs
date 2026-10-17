using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Downloader;

// Lucide 图标运行时渲染：解析 24x24 viewBox 的描边 SVG（path/rect/circle/line/polyline/polygon），
// 转成 GraphicsPath 后按目标尺寸矢量绘制，颜色随画笔变化，任意 DPI 下清晰
static class Lucide
{
    static readonly Dictionary<string, GraphicsPath?> Cache = new();

    public static void Draw(Graphics g, string name, PointF location, float size, Color color, float strokeWidth = 2f)
    {
        var path = GetPath(name);
        if (path == null)
        {
            return;
        }

        var state = g.Save();
        g.TranslateTransform(location.X, location.Y);
        g.ScaleTransform(size / 24f, size / 24f);
        using var pen = new Pen(color, strokeWidth);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        g.DrawPath(pen, path);
        g.Restore(state);
    }

    static GraphicsPath? GetPath(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            Cache[name] = Load(name);
            return Cache[name];
        }
    }

    static GraphicsPath? Load(string name)
    {
        try
        {
            using var stream = typeof(Lucide).Assembly
                .GetManifestResourceStream($"{typeof(Lucide).Namespace}.Icons.{name}.svg");
            if (stream == null)
            {
                return null;
            }
            using var reader = new StreamReader(stream);
            return ParseSvg(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    static GraphicsPath ParseSvg(string svg)
    {
        var path = new GraphicsPath();
        foreach (Match m in Regex.Matches(svg, @"<(path|rect|circle|ellipse|line|polyline|polygon)\b[^>]*>"))
        {
            string tag = m.Groups[1].Value;
            string attrs = m.Value;
            string Attr(string a) => Regex.Match(attrs, $@"\b{a}\s*=\s*""([^""]*)""").Groups[1].Value.Trim();

            switch (tag)
            {
                case "path":
                    ParsePathData(path, Attr("d"));
                    break;
                case "rect":
                {
                    float x = F(Attr("x")), y = F(Attr("y"));
                    float w = F(Attr("width")), h = F(Attr("height"));
                    float rx = Attr("rx").Length > 0 ? F(Attr("rx")) : 0;
                    float ry = Attr("ry").Length > 0 ? F(Attr("ry")) : rx;
                    if (rx > 0 || ry > 0)
                    {
                        AddRoundedRect(path, x, y, w, h, Math.Min(rx, w / 2), Math.Min(ry, h / 2));
                    }
                    else
                    {
                        path.StartFigure();
                        path.AddRectangle(new RectangleF(x, y, w, h));
                    }
                    break;
                }
                case "circle":
                    float cx = F(Attr("cx")), cy = F(Attr("cy")), r = F(Attr("r"));
                    path.StartFigure();
                    path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                    break;
                case "ellipse":
                {
                    float ecx = F(Attr("cx")), ecy = F(Attr("cy"));
                    float erx = F(Attr("rx")), ery = F(Attr("ry"));
                    path.StartFigure();
                    path.AddEllipse(ecx - erx, ecy - ery, erx * 2, ery * 2);
                    break;
                }
                case "line":
                    path.StartFigure();
                    path.AddLine(F(Attr("x1")), F(Attr("y1")), F(Attr("x2")), F(Attr("y2")));
                    break;
                case "polyline":
                case "polygon":
                {
                    var nums = Numbers(Attr("points")).ToArray();
                    if (nums.Length >= 4)
                    {
                        var pts = new PointF[nums.Length / 2];
                        for (int i = 0; i < pts.Length; i++)
                        {
                            pts[i] = new PointF((float)nums[i * 2], (float)nums[i * 2 + 1]);
                        }
                        path.StartFigure();
                        path.AddLines(pts);
                        if (tag == "polygon")
                        {
                            path.CloseFigure();
                        }
                    }
                    break;
                }
            }
        }
        return path;

        static float F(string v) => string.IsNullOrEmpty(v) ? 0 : float.Parse(v, CultureInfo.InvariantCulture);
    }

    static IEnumerable<double> Numbers(string s)
    {
        foreach (Match m in Regex.Matches(s, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"))
        {
            yield return double.Parse(m.Value, CultureInfo.InvariantCulture);
        }
    }

    // SVG path 语法子集：M/m L/l H/h V/v C/c S/s Q/q T/t A/a Z/z，支持隐式重复命令
    static void ParsePathData(GraphicsPath path, string d)
    {
        var tokens = new List<object>(); // char 命令或 double 数值
        for (int i = 0; i < d.Length;)
        {
            char c = d[i];
            if (char.IsLetter(c) && (c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v'
                or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z'))
            {
                tokens.Add(c);
                i++;
            }
            else if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
            }
            else
            {
                var m = Regex.Match(d[i..], @"^[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
                if (!m.Success || m.Length == 0)
                {
                    i++;
                    continue;
                }
                tokens.Add(double.Parse(m.Value, CultureInfo.InvariantCulture));
                i += m.Length;
            }
        }

        float x = 0, y = 0, sx = 0, sy = 0;
        float lastCx = 0, lastCy = 0, lastQx = 0, lastQy = 0;
        bool hasCubic = false, hasQuad = false;
        int i2 = 0;
        char cmd = '\0';

        static int ArgCount(char c) => c switch
        {
            'M' or 'm' or 'L' or 'l' or 'T' or 't' => 2,
            'H' or 'h' or 'V' or 'v' => 1,
            'C' or 'c' or 'S' or 's' or 'Q' or 'q' => 6,
            'A' or 'a' => 7,
            _ => 0,
        };

        float N(ref int idx)
        {
            while (idx < tokens.Count && tokens[idx] is char)
            {
                cmd = (char)tokens[idx];
                idx++;
            }
            if (idx >= tokens.Count)
            {
                throw new FormatException("path data ended unexpectedly");
            }
            return (float)(double)tokens[idx++];
        }

        while (i2 < tokens.Count)
        {
            if (tokens[i2] is char ch)
            {
                cmd = ch;
                i2++;
            }

            if (cmd is 'Z' or 'z')
            {
                path.CloseFigure();
                x = sx;
                y = sy;
                i2++;
                continue;
            }

            bool rel = char.IsLower(cmd);
            int argc = ArgCount(cmd);
            int consumed = 0;
            do
            {
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        float nx = N(ref i2), ny = N(ref i2);
                        if (rel)
                        {
                            nx += x;
                            ny += y;
                        }
                        path.StartFigure();
                        x = sx = nx;
                        y = sy = ny;
                        if (consumed == 0)
                        {
                            cmd = rel ? 'l' : 'L';
                        }
                        break;
                    }
                    case 'L':
                    {
                        float nx = N(ref i2), ny = N(ref i2);
                        if (rel)
                        {
                            nx += x;
                            ny += y;
                        }
                        path.AddLine(x, y, nx, ny);
                        x = nx;
                        y = ny;
                        break;
                    }
                    case 'H':
                    {
                        float nx = N(ref i2);
                        if (rel)
                        {
                            nx += x;
                        }
                        path.AddLine(x, y, nx, y);
                        x = nx;
                        break;
                    }
                    case 'V':
                    {
                        float ny = N(ref i2);
                        if (rel)
                        {
                            ny += y;
                        }
                        path.AddLine(x, y, x, ny);
                        y = ny;
                        break;
                    }
                    case 'C':
                    {
                        float c1x = N(ref i2), c1y = N(ref i2), c2x = N(ref i2), c2y = N(ref i2), ex = N(ref i2), ey = N(ref i2);
                        if (rel)
                        {
                            c1x += x; c1y += y; c2x += x; c2y += y; ex += x; ey += y;
                        }
                        path.AddBezier(x, y, c1x, c1y, c2x, c2y, ex, ey);
                        lastCx = c2x;
                        lastCy = c2y;
                        hasCubic = true;
                        x = ex;
                        y = ey;
                        break;
                    }
                    case 'S':
                    {
                        float c1x = N(ref i2), c1y = N(ref i2), c2x = N(ref i2), c2y = N(ref i2), ex = N(ref i2), ey = N(ref i2);
                        if (rel)
                        {
                            c1x += x; c1y += y; c2x += x; c2y += y; ex += x; ey += y;
                        }
                        if (!hasCubic)
                        {
                            c1x = x;
                            c1y = y;
                        }
                        else
                        {
                            c1x = 2 * x - lastCx;
                            c1y = 2 * y - lastCy;
                        }
                        path.AddBezier(x, y, c1x, c1y, c2x, c2y, ex, ey);
                        lastCx = c2x;
                        lastCy = c2y;
                        hasCubic = true;
                        x = ex;
                        y = ey;
                        break;
                    }
                    case 'Q':
                    {
                        float qx = N(ref i2), qy = N(ref i2), ex = N(ref i2), ey = N(ref i2);
                        if (rel)
                        {
                            qx += x; qy += y; ex += x; ey += y;
                        }
                        AddQuad(path, x, y, qx, qy, ex, ey);
                        lastQx = qx;
                        lastQy = qy;
                        hasQuad = true;
                        x = ex;
                        y = ey;
                        break;
                    }
                    case 'T':
                    {
                        float ex = N(ref i2), ey = N(ref i2);
                        if (rel)
                        {
                            ex += x; ey += y;
                        }
                        float qx = hasQuad ? 2 * x - lastQx : x;
                        float qy = hasQuad ? 2 * y - lastQy : y;
                        AddQuad(path, x, y, qx, qy, ex, ey);
                        lastQx = qx;
                        lastQy = qy;
                        hasQuad = true;
                        x = ex;
                        y = ey;
                        break;
                    }
                    case 'A':
                    {
                        float rx = N(ref i2), ry = N(ref i2);
                        float rot = N(ref i2);
                        float laf = N(ref i2), swf = N(ref i2);
                        float ex = N(ref i2), ey = N(ref i2);
                        if (rel)
                        {
                            ex += x; ey += y;
                        }
                        AddArcTo(path, x, y, rx, ry, rot, laf > 0.5f, swf > 0.5f, ex, ey);
                        x = ex;
                        y = ey;
                        break;
                    }
                }
                consumed += argc;
            } while (i2 < tokens.Count && tokens[i2] is not char && consumed > 0 && ArgCount(cmd) > 0);
        }
    }

    static void AddQuad(GraphicsPath path, float x0, float y0, float qx, float qy, float x1, float y1)
    {
        // 二次贝塞尔转三次：c1 = p0 + 2/3(q - p0)，c2 = p1 + 2/3(q - p1)
        path.AddBezier(x0, y0,
            x0 + 2f / 3 * (qx - x0), y0 + 2f / 3 * (qy - y0),
            x1 + 2f / 3 * (qx - x1), y1 + 2f / 3 * (qy - y1),
            x1, y1);
    }

    static void AddRoundedRect(GraphicsPath path, float x, float y, float w, float h, float rx, float ry)
    {
        float d = 2 * rx;
        path.StartFigure();
        path.AddArc(x, y, d, 2 * ry, 180, 90);
        path.AddArc(x + w - d, y, d, 2 * ry, 270, 90);
        path.AddArc(x + w - d, y + h - 2 * ry, d, 2 * ry, 0, 90);
        path.AddArc(x, y + h - 2 * ry, d, 2 * ry, 90, 90);
        path.CloseFigure();
    }

    // SVG 端点式椭圆弧转 GDI+ 贝塞尔（W3C 规范 F.6.5 端点参数化）
    static void AddArcTo(GraphicsPath path, float x0, float y0, float rx, float ry, float rotDeg, bool largeArc, bool sweep, float x1, float y1)
    {
        if (x0 == x1 && y0 == y1)
        {
            return;
        }
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx < 1e-6f || ry < 1e-6f)
        {
            path.AddLine(x0, y0, x1, y1);
            return;
        }

        double rot = rotDeg * Math.PI / 180.0;
        double cosR = Math.Cos(rot), sinR = Math.Sin(rot);
        double dx2 = (x0 - x1) / 2.0, dy2 = (y0 - y1) / 2.0;
        double x1p = cosR * dx2 + sinR * dy2;
        double y1p = -sinR * dx2 + cosR * dy2;

        double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            double s = Math.Sqrt(lambda);
            rx = (float)(s * rx);
            ry = (float)(s * ry);
        }

        double sign = largeArc != sweep ? 1 : -1;
        double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double coef = Math.Sqrt(Math.Max(0, num / den)) * sign;
        double cxp = coef * rx * y1p / ry;
        double cyp = -coef * ry * x1p / rx;

        double cx = cosR * cxp - sinR * cyp + (x0 + x1) / 2.0;
        double cy = sinR * cxp + cosR * cyp + (y0 + y1) / 2.0;

        static double Angle(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy);
            double a = Math.Acos(Math.Clamp(dot / (len == 0 ? 1 : len), -1, 1));
            if (ux * vy - uy * vx < 0)
            {
                a = -a;
            }
            return a;
        }

        double theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double dTheta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && dTheta > 0)
        {
            dTheta -= 2 * Math.PI;
        }
        else if (sweep && dTheta < 0)
        {
            dTheta += 2 * Math.PI;
        }

        int segments = (int)Math.Ceiling(Math.Abs(dTheta) / (Math.PI / 2));
        double delta = dTheta / segments;
        double t = 4.0 / 3.0 * Math.Tan(delta / 4);

        double ux = Math.Cos(theta1), uy = Math.Sin(theta1);
        double px = x0, py = y0;
        double th = theta1;
        for (int i = 0; i < segments; i++)
        {
            double th2 = th + delta;
            double vx = Math.Cos(th2), vy = Math.Sin(th2);
            double x2 = cx + rx * Math.Cos(th2) * cosR - ry * Math.Sin(th2) * sinR;
            double y2 = cy + rx * Math.Cos(th2) * sinR + ry * Math.Sin(th2) * cosR;
            double c1x = px + t * (-rx * ux * sinR - ry * uy * cosR);
            double c1y = py + t * (-rx * ux * cosR + ry * uy * sinR);
            double c2x = x2 - t * (-rx * vx * sinR - ry * vy * cosR);
            double c2y = y2 - t * (-rx * vx * cosR + ry * vy * sinR);
            path.AddBezier((float)px, (float)py, (float)c1x, (float)c1y, (float)c2x, (float)c2y, (float)x2, (float)y2);
            px = x2;
            py = y2;
            ux = vx;
            uy = vy;
            th = th2;
        }
    }
}
