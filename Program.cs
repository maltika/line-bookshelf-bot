using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
var app = builder.Build();

var channelSecret = builder.Configuration["Line:ChannelSecret"]
    ?? throw new Exception("Missing Line:ChannelSecret");
var accessToken = builder.Configuration["Line:AccessToken"]
    ?? throw new Exception("Missing Line:AccessToken");

app.MapGet("/", () => "Bookshelf bot is running");

app.MapPost("/webhook", async (HttpRequest req, IHttpClientFactory httpFactory, AppDbContext db) =>
{
    // 1) อ่าน body ดิบ + ตรวจ signature
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();

    var signature = req.Headers["X-Line-Signature"].ToString();
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(channelSecret));
    var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    if (expected != signature) return Results.Unauthorized();

    // 2) วนอ่าน events
    using var doc = JsonDocument.Parse(body);
    var http = httpFactory.CreateClient();
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

    foreach (var ev in doc.RootElement.GetProperty("events").EnumerateArray())
    {
        if (ev.GetProperty("type").GetString() != "message") continue;

        var message = ev.GetProperty("message");
        if (message.GetProperty("type").GetString() != "text") continue;

        var lineUserId = ev.GetProperty("source").GetProperty("userId").GetString()!;
        var text = message.GetProperty("text").GetString() ?? "";
        var replyToken = ev.GetProperty("replyToken").GetString()!;

        // 3) หา user ถ้ายังไม่มีให้สร้างใหม่ (เก็บ LINE userId ลง SQL Server)
        var user = await db.Users.FirstOrDefaultAsync(u => u.LineUserId == lineUserId);
        if (user is null)
        {
            user = new AppUser { LineUserId = lineUserId };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            Console.WriteLine($"New user: {lineUserId}");
        }

        // 4) ประมวลผลคำสั่ง แล้วตอบกลับ
        var reply = await HandleCommand(db, user, text);
        await ReplyAsync(http, replyToken, reply);
    }

    return Results.Ok();
});

app.Run();

// ---------------------------------------------------------------

static async Task<string> HandleCommand(AppDbContext db, AppUser user, string text)
{
    var parts = text.Split(' ', 2,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var cmd = parts.Length > 0 ? parts[0] : "";
    var arg = parts.Length > 1 ? parts[1] : "";

    switch (cmd)
    {
        case "เพิ่ม":
        {
            if (arg == "") return "พิมพ์แบบนี้นะ: เพิ่ม ชื่อหนังสือ";
            if (arg.Length > 200) return "ชื่อยาวเกินไป (ไม่เกิน 200 ตัวอักษร)";
            var exists = await db.Books.AnyAsync(b => b.AppUserId == user.Id && b.Title == arg);
            if (exists) return $"มี \"{arg}\" อยู่ในชั้นแล้ว";
            db.Books.Add(new Book { AppUserId = user.Id, Title = arg });
            await db.SaveChangesAsync();
            return $"เพิ่ม \"{arg}\" เข้าชั้นแล้ว 📚";
        }

        case "ชั้นหนังสือ":
        {
            var books = await db.Books
                .Where(b => b.AppUserId == user.Id)
                .OrderBy(b => b.CreatedAt)
                .ToListAsync();
            if (books.Count == 0) return "ชั้นยังว่างอยู่ ลองพิมพ์: เพิ่ม ชื่อหนังสือ";
            var lines = books.Select((b, i) => $"{i + 1}. {(b.IsRead ? "✅" : "📖")} {b.Title}");
            return "ชั้นหนังสือของคุณ\n" + string.Join("\n", lines);
        }

        case "อ่านแล้ว":
        {
            if (arg == "") return "พิมพ์แบบนี้นะ: อ่านแล้ว ชื่อหนังสือ";
            var book = await db.Books.FirstOrDefaultAsync(b => b.AppUserId == user.Id && b.Title == arg);
            if (book is null) return $"ไม่เจอ \"{arg}\" ในชั้น";
            book.IsRead = true;
            await db.SaveChangesAsync();
            return $"ทำเครื่องหมายว่าอ่าน \"{arg}\" แล้ว ✅";
        }

        case "ลบ":
        {
            if (arg == "") return "พิมพ์แบบนี้นะ: ลบ ชื่อหนังสือ";
            var book = await db.Books.FirstOrDefaultAsync(b => b.AppUserId == user.Id && b.Title == arg);
            if (book is null) return $"ไม่เจอ \"{arg}\" ในชั้น";
            db.Books.Remove(book);
            await db.SaveChangesAsync();
            return $"ลบ \"{arg}\" ออกแล้ว";
        }

        default:
            return "คำสั่งที่ใช้ได้\n" +
                   "• เพิ่ม ชื่อหนังสือ\n" +
                   "• ชั้นหนังสือ\n" +
                   "• อ่านแล้ว ชื่อหนังสือ\n" +
                   "• ลบ ชื่อหนังสือ";
    }
}

static async Task ReplyAsync(HttpClient http, string replyToken, string text)
{
    var payload = JsonSerializer.Serialize(new
    {
        replyToken,
        messages = new[] { new { type = "text", text } }
    });

    var res = await http.PostAsync(
        "https://api.line.me/v2/bot/message/reply",
        new StringContent(payload, Encoding.UTF8, "application/json"));

    if (!res.IsSuccessStatusCode)
        Console.WriteLine($"Reply failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
}