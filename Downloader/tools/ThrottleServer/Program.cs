// 本地测试台架：限速 + Range 分片 + 三类可注入故障，每个请求都打印完整请求头。
//
//   /test.bin    4GB，限速 2MB/s/连接，正常分片路径
//   /small.bin   1MB，小于引擎的 MinSizeForSegments，走单线程路径
//   /login       下发一枚会话 Cookie 并返回带下载链接的页面
//   /private.bin 1MB，没有那枚会话 Cookie 就 403；配合 /login 可验浏览器接管的 Cookie/Referer 透传
//   /ref.bin     2MB，不带指定 Referer（与本站不同源）就 403 → 验每任务 Referer：
//                添加弹窗失败行的引用页补救、右键属性持久化、探测/下载带头
//   /short       HEAD 声称可分片，GET 回 206 但只发请求区间的一部分，
//                且 Content-Length 与实发字节数自洽 → HTTP 层看不出被截断，
//                专门用来验"引擎是否自己核对收口长度"（?ratio=0.4 可调）
//   /no206       HEAD 声称可分片，GET 无视 Range 直接 200 回整包 → 验回落单线程
//
// query 参数一律不参与路径判定，所以 /test.bin?token=A 与 ?token=B 返回同一串字节，
// 可以直接拿来模拟"签名链接每次刷新"
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

const long FourGB = 4L * 1024 * 1024 * 1024;
const int RateBytesPerSec = 2 * 1024 * 1024;
const int ChunkSize = 64 * 1024;
const int DelayMs = ChunkSize * 1000 / RateBytesPerSec;

const string RequiredCookie = "dl_session=TEST123";
// 与本站不同源的引用页：引擎没带 Referer 时会补同源根路径，用同源值当门槛的话
// "用户显式设置过"和"引擎自动补的"就区分不出来
const string RequiredReferer = "https://ref.example.test/page.html";
const string LoginPage = $"""
<!doctype html>
<meta charset="utf-8">
<body style="font-family:sans-serif">
<p>会话 Cookie 已下发：<code>{RequiredCookie}</code></p>
<p><a href="/private.bin">点这里下载 /private.bin（1MB，需 Cookie）</a></p>
</body>
""";

// 输出常被重定向到文件里回看，不显式设 UTF-8 的话中文会按控制台代码页（GBK）二次编码成乱码
Console.OutputEncoding = Encoding.UTF8;

var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:8080/");
listener.Start();
Console.WriteLine("throttle server on http://127.0.0.1:8080/  可用路径: /test.bin /small.bin /mid.bin /login /private.bin /ref.bin /short /no206");

while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = Task.Run(() => Handle(ctx));
}

async Task Handle(HttpListenerContext ctx)
{
    var request = ctx.Request;
    var response = ctx.Response;
    string path = request.Url!.AbsolutePath;

    try
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {request.HttpMethod} {path}{request.Url.Query} {request.Headers["Range"] ?? "(no range)"}");
        foreach (string? key in request.Headers.AllKeys)
        {
            Console.WriteLine($"      {key}: {request.Headers[key]}");
        }

        if (path == "/login")
        {
            // 不加 Secure/SameSite=None，否则 http://127.0.0.1 上存不住
            SendText(ctx, 200, "text/html; charset=utf-8", LoginPage, $"{RequiredCookie}; Path=/; SameSite=Lax");
            Console.WriteLine($"      >>> 下发 Cookie: {RequiredCookie}");
            return;
        }

        // 校验必须排在 HEAD 分支前，否则 HEAD 探测会被无条件放行，这条用例就失效了
        if (path == "/ref.bin")
        {
            string? referer = request.Headers["Referer"];
            bool refererOk = referer == RequiredReferer;
            Console.WriteLine($"      >>> 鉴权判定 referer={(referer ?? "无")}{(refererOk ? "（命中）" : "（不符）")}");
            if (!refererOk)
            {
                SendText(ctx, 403, "text/plain; charset=utf-8",
                    $"403: 这个文件要带引用页才能取（需 {RequiredReferer}）\n");
                return;
            }
        }

        // 校验必须排在 HEAD 分支前，否则 HEAD 探测会被无条件放行，这条用例就失效了
        if (path == "/private.bin")
        {
            bool cookieOk = (request.Headers["Cookie"] ?? "").Contains(RequiredCookie, StringComparison.Ordinal);
            string? referer = request.Headers["Referer"];
            Console.WriteLine($"      >>> 鉴权判定 cookie={(cookieOk ? "命中" : "缺失/无效")} referer={(referer ?? "无")}");
            if (!cookieOk)
            {
                SendText(ctx, 403, "text/plain; charset=utf-8", "403: 这个私有文件要带会话 Cookie 才能取，参见 /login\n");
                return;
            }
        }

        long size = path switch
        {
            "/small.bin" => 1024 * 1024,
            // 3MB：低于引擎 4MB 的分段门槛但又能跑过暂停点，专测单线程续传路径
            "/mid.bin" => 3 * 1024 * 1024,
            "/private.bin" => 1024 * 1024,
            // 2MB：低于 4MB 分段门槛走单线程，专测"指定 Referer 后才能取"的门
            "/ref.bin" => 2 * 1024 * 1024,
            // 8MB：既大于引擎的 4MB 分段门槛，又能在几秒内跑完一轮故障注入
            "/short" => 8 * 1024 * 1024,
            "/no206" => 5 * 1024 * 1024,
            _ => FourGB,
        };

        // HEAD 一律声称支持分片，连故意使坏的 /short 和 /no206 也一样，
        // 否则引擎压根不会走分段路径，故障就注入不进去。
        // ?range=off 让 GET 无视 Range 回整包，用来制造"重启后服务器突然不收分片"的场景
        bool ignoreRange = Regex.IsMatch(request.Url.Query, @"(?:^|&)range=off(?:&|$)");
        if (request.HttpMethod == "HEAD")
        {
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = size;
            response.AddHeader("Accept-Ranges", "bytes");
            AddIdentityHeaders(response, path, size);
            response.Close();
            return;
        }

        if (path == "/no206" || ignoreRange)
        {
            await SendAsync(ctx, 200, 0, size - 1, size, contentRangeTotal: null);
            return;
        }

        long start = 0;
        long end = size - 1;
        bool ranged = false;
        var match = Regex.Match(request.Headers["Range"] ?? "", @"^bytes=(\d*)-(\d*)$");
        if (match.Success)
        {
            ranged = true;
            if (match.Groups[1].Value.Length > 0) start = long.Parse(match.Groups[1].Value);
            if (match.Groups[2].Value.Length > 0) end = Math.Min(long.Parse(match.Groups[2].Value), size - 1);
        }

        if (path == "/short" && ranged)
        {
            double ratio = 0.4;
            var rm = Regex.Match(request.Url.Query, @"ratio=([\d.]+)");
            if (rm.Success) double.TryParse(rm.Groups[1].Value, out ratio);
            long len = Math.Max(1, (long)((end - start + 1) * ratio));
            end = start + len - 1;
            // 不发 Content-Range：body 长度与自身 Content-Length 完全自洽，
            // .NET 的 HTTP 层因此认为这是一次正常结束，只有引擎自己核对才拦得住
            await SendAsync(ctx, 206, start, end, size, contentRangeTotal: null);
            return;
        }

        await SendAsync(ctx, ranged ? 206 : 200, start, end, size, contentRangeTotal: ranged ? size : null);
    }
    catch (Exception)
    {
        // 客户端中断（暂停/取消）属正常情况
        try { ctx.Response.Abort(); } catch { }
    }
}

// 真实服务器都会发这两个头，引擎靠它们判断"服务端文件是否变过"；
// 一台不发的服务器会让续传校验的 ETag/Last-Modified 分支永远走不到
static void AddIdentityHeaders(HttpListenerResponse response, string path, long size)
{
    response.AddHeader("ETag", $"\"{path.Trim('/')}-{size}\"");
    response.AddHeader("Last-Modified", "Wed, 01 Jan 2025 00:00:00 GMT");
}

static void SendText(HttpListenerContext ctx, int status, string contentType, string body, string? setCookie = null)
{
    if (setCookie != null)
    {
        ctx.Response.AddHeader("Set-Cookie", setCookie);
    }

    byte[] payload = Encoding.UTF8.GetBytes(body);
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = contentType;
    ctx.Response.ContentLength64 = payload.Length;
    // HEAD 请求带 body 会让 HttpListener 把连接弄坏（客户端看到空回复而不是 403），
    // 探测路径第一个请求就是 HEAD，403 门必须让它干净地失败
    if (ctx.Request.HttpMethod != "HEAD")
    {
        ctx.Response.OutputStream.Write(payload, 0, payload.Length);
    }
    ctx.Response.Close();
}

static async Task SendAsync(HttpListenerContext ctx, int status, long start, long end, long fileSize, long? contentRangeTotal)
{
    var response = ctx.Response;
    long length = end - start + 1;
    response.StatusCode = status;
    response.ContentType = "application/octet-stream";
    response.ContentLength64 = length;
    response.AddHeader("Accept-Ranges", "bytes");
    // 传文件总长而非区间末尾：ETag 必须对同一文件的所有请求保持一致，否则续传校验永远不匹配
    AddIdentityHeaders(response, ctx.Request.Url!.AbsolutePath, fileSize);
    if (contentRangeTotal is long total)
    {
        response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
    }
    if (status == 200)
    {
        // 名字故意与 URL 不同：本机按 URL 取名落成 private.bin，只有 Chrome 自己下载才会听它
        string name = ctx.Request.Url!.AbsolutePath == "/private.bin" ? "protected-file.bin" : "throttled-test.bin";
        response.AddHeader("Content-Disposition", $"attachment; filename=\"{name}\"");
    }

    var buffer = new byte[ChunkSize];
    Array.Fill(buffer, (byte)'A');
    long pos = start;
    while (pos <= end)
    {
        int n = (int)Math.Min(ChunkSize, end - pos + 1);
        await response.OutputStream.WriteAsync(buffer.AsMemory(0, n));
        pos += n;
        await Task.Delay(DelayMs);
    }
    response.Close();
}
