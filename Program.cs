using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;


// create webapp & load auto config
var builder = WebApplication.CreateBuilder(args);
// call api LINE
builder.Services.AddHttpClient();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

var app = builder.Build();

// read channelSecret & accessToken
var channelSecret = builder.Configuration["Line:ChannelSecret"]
    ?? throw new Exception("Missing Line:ChannelSecret");
var accessToken = builder.Configuration["Line:AccessToken"]
    ?? throw new Exception("Missing Line:AccessToken");

// check server
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

        // 3) หา user ถ้ายังไม่มีให้สร้างใหม่
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
// คำสั่งของบอท (ตอบกลับครั้งละ 1 ข้อความ)
// ---------------------------------------------------------------

static async Task<object> HandleCommand(AppDbContext db, AppUser user, string text)
{
    //HandleCommand
    var parts = text.Split(' ', 2,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var cmd = parts.Length > 0 ? parts[0] : "";
    var arg = parts.Length > 1 ? parts[1] : "";

    switch (cmd)
    {
        case "เพิ่ม":
            {
                if (arg == "") return TextMessage("ใส่ชื่อหนังสือต่อท้ายด้วย เช่น เพิ่ม Atomic Habits");
                if (arg.Length > 200) return TextMessage("ชื่อยาวเกิน 200 ตัวอักษร");
                var exists = await db.Books.AnyAsync(b => b.AppUserId == user.Id && b.Title == arg);
                if (exists) return TextMessage($"{arg} มีอยู่แล้ว");
                db.Books.Add(new Book { AppUserId = user.Id, Title = arg });
                await db.SaveChangesAsync();
                return TextMessage($"เพิ่ม {arg} แล้ว");
            }

        case "ชั้นหนังสือ":
            return await ShelfOrEmpty(db, user);

        case "อ่านแล้ว":
            {
                if (arg == "") return TextMessage("ใส่ชื่อหนังสือต่อท้ายด้วย เช่น อ่านแล้ว Atomic Habits");
                var book = await db.Books.FirstOrDefaultAsync(b => b.AppUserId == user.Id && b.Title == arg);
                if (book is null) return TextMessage($"ไม่เจอ {arg} ในชั้น");
                book.IsRead = true;
                await db.SaveChangesAsync();
                return await ShelfOrEmpty(db, user); // การ์ดที่อัปเดตแล้วคือตัวยืนยัน
            }

        case "ลบ":
            {
                if (arg == "") return TextMessage("ใส่ชื่อหนังสือต่อท้ายด้วย เช่น ลบ Atomic Habits");
                var book = await db.Books.FirstOrDefaultAsync(b => b.AppUserId == user.Id && b.Title == arg);
                if (book is null) return TextMessage($"ไม่เจอ {arg} ในชั้น");
                db.Books.Remove(book);
                await db.SaveChangesAsync();
                return await ShelfOrEmpty(db, user);
            }

        case "วิธีใช้":
        default:
            return HelpFlex();
    }
}

static async Task<object> ShelfOrEmpty(AppDbContext db, AppUser user)
{
    var books = await db.Books
        .Where(b => b.AppUserId == user.Id)
        .OrderBy(b => b.CreatedAt)
        .ToListAsync();

    return books.Count == 0
        ? TextMessage("ยังไม่มีหนังสือในชั้น พิมพ์ เพิ่ม ตามด้วยชื่อหนังสือได้เลย")
        : ShelfFlex(books);
}

// ---------------------------------------------------------------
// ตัวช่วยสร้างข้อความ LINE
// ---------------------------------------------------------------

static object TextMessage(string text) => new { type = "text", text };

// Flex Message: การ์ดชั้นหนังสือ มีปุ่ม "อ่านแล้ว" / "ลบ" ในแต่ละเล่ม

static object ShelfFlex(List<Book> books)
{
    const int max = 10;
    var rows = new List<object>();

    for (var i = 0; i < Math.Min(books.Count, max); i++)
    {
        var b = books[i];
        var buttons = new List<object>();

        if (!b.IsRead)
        {
            buttons.Add(new
            {
                type = "button",
                style = "primary",
                color = "#B5603B",
                height = "sm",
                action = new
                {
                    type = "message",
                    label = "อ่านแล้ว",
                    text = $"อ่านแล้ว {b.Title}"
                }
            });
        }

        buttons.Add(new
        {
            type = "button",
            style = "secondary",
            height = "sm",
            action = new
            {
                type = "message",
                label = "ลบ",
                text = $"ลบ {b.Title}"
            }
        });

        rows.Add(new
        {
            type = "box",
            layout = "vertical",
            spacing = "sm",
            contents = new object[]
            {
                new
                {
                    type = "text",
                    text = $"{i + 1}. {b.Title}",
                    wrap = true,
                    weight = "bold",
                    color = "#4A3426"
                },
                new
                {
                    type = "text",
                    text = b.IsRead ? "อ่านแล้ว" : "ยังไม่ได้อ่าน",
                    size = "xs",
                    color = b.IsRead ? "#2E8B57" : "#888888"
                },
                new
                {
                    type = "box",
                    layout = "horizontal",
                    spacing = "sm",
                    contents = buttons
                },
                new
                {
                    type = "separator",
                    margin = "md",
                    color = "#E2D6C0"
                }
            }
        });
    }

    if (books.Count > max)
    {
        rows.Add(new
        {
            type = "text",
            text = $"และอีก {books.Count - max} เล่ม",
            size = "sm",
            color = "#888888"
        });
    }

    var readCount = books.Count(b => b.IsRead);

    return new
    {
        type = "flex",
        altText = "ชั้นหนังสือ",
        contents = new
        {
            type = "bubble",
            size = "mega",

            header = new
            {
                type = "box",
                layout = "vertical",
                backgroundColor = "#4A3426",
                paddingAll = "lg",
                spacing = "xs",
                contents = new object[]
                {
                    new
                    {
                        type = "text",
                        text = "ชั้นหนังสือของฉัน",
                        weight = "bold",
                        size = "lg",
                        color = "#FFFFFF",
                        wrap = true
                    },
                    new
                    {
                        type = "text",
                        text = $"ทั้งหมด {books.Count} เล่ม อ่านแล้ว {readCount} เล่ม",
                        size = "xs",
                        color = "#E2D6C0",
                        wrap = true
                    }
                }
            },

            body = new
            {
                type = "box",
                layout = "vertical",
                spacing = "md",
                paddingAll = "lg",
                backgroundColor = "#FFFFFF",
                contents = rows
            },

            styles = new
            {
                header = new { backgroundColor = "#4A3426" },
                body = new { backgroundColor = "#FFFFFF" }
            }
        }
    };
}


// Flex Message: การ์ดวิธีใช้

static object HelpFlex()
{
    static object Row(string cmd, string desc, string? example)
    {
        var lines = new List<object>
        {
            new
            {
                type = "text",
                text = cmd,
                weight = "bold",
                size = "md",
                color = "#4A3426"
            },
            new
            {
                type = "text",
                text = desc,
                size = "sm",
                color = "#6A4A35",
                wrap = true
            }
        };

        if (example is not null)
        {
            lines.Add(new
            {
                type = "text",
                text = $"เช่น {example}",
                size = "xs",
                color = "#B5603B",
                wrap = true
            });
        }

        return new
        {
            type = "box",
            layout = "vertical",
            spacing = "xs",
            contents = lines
        };
    }

    var sep = new
    {
        type = "separator",
        margin = "md",
        color = "#E2D6C0"
    };

    return new
    {
        type = "flex",
        altText = "วิธีใช้บอทชั้นหนังสือ",
        contents = new
        {
            type = "bubble",
            styles = new
            {
                body = new { backgroundColor = "#FFFFFF" },
                //footer = new { backgroundColor = "#E2D6C0" }
            },
            header = new
            {
                type = "box",
                layout = "vertical",
                backgroundColor = "#4A3426",
                paddingAll = "lg",
                spacing = "xs",
                contents = new object[]
                {
                    new
                    {
                        type = "text",
                        text = "วิธีใช้ ShelfBot",
                        weight = "bold",
                        size = "lg",
                        color = "#FFFFFF"
                    },
                    new
                    {
                        type = "text",
                        text = "พิมพ์คำสั่งตามนี้ได้เลย",
                        size = "xs",
                        color = "#E2D6C0"
                    }
                }
            },
            body = new
            {
                type = "box",
                layout = "vertical",
                spacing = "md",
                paddingAll = "lg",
                contents = new object[]
                {
                    Row("เพิ่ม ชื่อหนังสือ",
                        "เพิ่มหนังสือเข้าชั้น",
                        "เพิ่ม Atomic Habits"),
                    sep,
                    Row("ชั้นหนังสือ",
                        "ดูหนังสือทั้งหมดพร้อมสถานะ",
                        null),
                    sep,
                    Row("อ่านแล้ว ชื่อหนังสือ",
                        "ทำเครื่องหมายว่าอ่านจบแล้ว",
                        "อ่านแล้ว Atomic Habits"),
                    sep,
                    Row("ลบ ชื่อหนังสือ",
                        "เอาหนังสือออกจากชั้น",
                        "ลบ Atomic Habits")
                }
            },
            footer = new
            {
                type = "box",
                layout = "vertical",
                paddingAll = "md",
                contents = new object[]
                {
                    new
                    {
                        type = "button",
                        style = "primary",
                        color = "#4A3426",
                        height = "sm",
                        action = new
                        {
                            type = "message",
                            label = "ดูชั้นหนังสือ",
                            text = "ชั้นหนังสือ"
                        }
                    }
                }
            }
        }
    };
}


static async Task ReplyAsync(HttpClient http, string replyToken, object message)
{
    var payload = JsonSerializer.Serialize(new { replyToken, messages = new[] { message } });

    var res = await http.PostAsync(
        "https://api.line.me/v2/bot/message/reply",
        new StringContent(payload, Encoding.UTF8, "application/json"));

    if (!res.IsSuccessStatusCode)
        Console.WriteLine($"Reply failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
}