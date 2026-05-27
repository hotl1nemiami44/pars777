using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoomSchedule;

internal enum WeekKind { Up, Down, Both } // верхняя / нижняя / обе недели

internal sealed record RoomRef(string Building, string RoomId, string Title, string Hex)
{
    public string Url => $"{Program.BaseUrl}/viewschedule/room/{RoomId}/{Hex}/";
}

internal sealed record Lesson(
    DayOfWeek Day,
    TimeOnly Start,
    TimeOnly End,
    WeekKind Week,
    string Subject,
    string Type,
    string Group,
    string Teacher,
    string Room);

internal static class Program
{
    public const string BaseUrl = "https://www.smtu.ru";
    private const string ListUrl = BaseUrl + "/ru/listschedule/";

    private static async Task<int> Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* консоль без UTF-8 */ }

        var options = Options.Parse(args);

        string room = options.Room ?? Prompt("Введите номер кабинета: ");
        if (string.IsNullOrWhiteSpace(room))
        {
            Console.Error.WriteLine("Номер кабинета не задан.");
            return 2;
        }

        var nowTime = TimeOnly.FromDateTime(options.At ?? DateTime.Now);

        using HttpClient http = CreateClient();

        string listHtml;
        try
        {
            listHtml = await GetHtml(http, ListUrl);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось загрузить {ListUrl}: {ex.Message}");
            return 1;
        }

        (DayOfWeek? today, WeekKind? parity) = ParseToday(listHtml);
        WeekKind currentParity = options.Week ?? parity ?? WeekKind.Both;
        DayOfWeek currentDay = options.At?.DayOfWeek ?? today ?? DateTime.Now.DayOfWeek;

        List<RoomRef> rooms = ParseRooms(listHtml);
        if (rooms.Count == 0)
        {
            Console.Error.WriteLine("Не удалось разобрать список аудиторий (изменилась разметка сайта?).");
            return 1;
        }

        var matches = FindRooms(rooms, room, options.Building);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Аудитория «{room}» не найдена. Примеры доступных: " +
                string.Join(", ", rooms.Take(15).Select(r => $"{r.Title} ({r.Building})")) + " …");
            return 1;
        }

        Console.WriteLine($"Кабинет:       {room}");
        Console.WriteLine($"Сейчас:        {(options.At ?? DateTime.Now):dd.MM.yyyy HH:mm}, " +
                          $"{DayName(currentDay)}, {ParityName(currentParity)}");
        Console.WriteLine();

        foreach (var rm in matches)
        {
            string html;
            try
            {
                html = await GetHtml(http, rm.Url);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ! {rm.Title} ({rm.Building}): {ex.Message}");
                continue;
            }

            var lessons = ParseRoomSchedule(html);

            if (options.Dump)
            {
                Console.WriteLine($"=== {rm.Title} ({rm.Building}) — {rm.Url}");
                foreach (var l in lessons.OrderBy(l => l.Day).ThenBy(l => l.Start))
                    Console.WriteLine($"  {DayName(l.Day)} {l.Start:HH\\:mm}-{l.End:HH\\:mm} " +
                                      $"{ParityName(l.Week)} | гр.{l.Group} | {l.Subject} ({l.Type}) | {l.Teacher}");
                continue;
            }

            var now = lessons.Where(l =>
                l.Day == currentDay &&
                nowTime >= l.Start && nowTime <= l.End &&
                (l.Week == WeekKind.Both || l.Week == currentParity)).ToList();

            string header = matches.Count > 1 ? $"Аудитория {rm.Title} ({rm.Building})" : $"Аудитория {rm.Title}";
            if (now.Count == 0)
            {
                Console.WriteLine($"{header}: сейчас занятий нет.");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"{header} — сейчас идёт:");
            foreach (var l in now)
            {
                Console.WriteLine($"  Время:         {l.Start:HH\\:mm}-{l.End:HH\\:mm}");
                Console.WriteLine($"  Предмет:       {Dash(l.Subject)}{(l.Type.Length > 0 ? $" ({l.Type})" : "")}");
                Console.WriteLine($"  Преподаватель: {Dash(l.Teacher)}");
                Console.WriteLine($"  Группа:        {Dash(l.Group)}");
                Console.WriteLine();
            }
        }

        return 0;
    }

    // ---------- HTTP ----------

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
        return http;
    }

    private static async Task<string> GetHtml(HttpClient http, string url)
    {
        using var resp = await http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    // ---------- Разбор списка аудиторий (arRoom) и текущей недели ----------

    private sealed class BuildingJson
    {
        [JsonPropertyName("building_title")] public string Title { get; set; } = "";
        [JsonPropertyName("ar")] public Dictionary<string, RoomJson> Rooms { get; set; } = new();
    }

    private sealed class RoomJson
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("hex")] public string Hex { get; set; } = "";
    }

    private static List<RoomRef> ParseRooms(string html)
    {
        var result = new List<RoomRef>();
        string? json = ExtractArRoom(html);
        if (json is null) return result;

        Dictionary<string, BuildingJson>? buildings;
        try
        {
            buildings = JsonSerializer.Deserialize<Dictionary<string, BuildingJson>>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return result;
        }
        if (buildings is null) return result;

        foreach (var b in buildings.Values)
            foreach (var (roomId, info) in b.Rooms)
                result.Add(new RoomRef(b.Title, roomId, info.Title, info.Hex));

        return result;
    }

    // Достаёт JSON-объект из `var arRoom = {...};` балансировкой скобок.
    private static string? ExtractArRoom(string html)
    {
        int marker = html.IndexOf("arRoom", StringComparison.Ordinal);
        if (marker < 0) return null;
        int start = html.IndexOf('{', marker);
        if (start < 0) return null;

        int depth = 0;
        for (int i = start; i < html.Length; i++)
        {
            if (html[i] == '{') depth++;
            else if (html[i] == '}' && --depth == 0)
                return html.Substring(start, i - start + 1);
        }
        return null;
    }

    private static (DayOfWeek? Day, WeekKind? Parity) ParseToday(string html)
    {
        var m = Regex.Match(html, @"Сегодня:[^<]*");
        string s = m.Success ? m.Value : "";

        DayOfWeek? day = ParseDay(s);
        WeekKind? parity = Regex.IsMatch(s, "верхн", RegexOptions.IgnoreCase) ? WeekKind.Up
            : Regex.IsMatch(s, "нижн", RegexOptions.IgnoreCase) ? WeekKind.Down
            : null;
        return (day, parity);
    }

    private static List<RoomRef> FindRooms(List<RoomRef> rooms, string query, string? building)
    {
        IEnumerable<RoomRef> pool = rooms;
        if (!string.IsNullOrWhiteSpace(building))
            pool = pool.Where(r => Norm(r.Building).Contains(Norm(building)));

        var pooled = pool.ToList();
        string q = Norm(query);

        var exact = pooled.Where(r => Norm(r.Title) == q).ToList();
        if (exact.Count > 0) return exact;

        return pooled.Where(r => Norm(r.Title).StartsWith(q) || Norm(r.Title).Contains(q)).ToList();
    }

    // ---------- Разбор расписания аудитории (табличный вид) ----------
    //
    // Берём фрагмент страницы начиная с id="table-container" (вид-таблица идёт
    // в разметке после вида-карточек, поэтому карточки в выборку не попадают),
    // затем по порядку идём по заголовкам дней и строкам занятий.

    private static readonly Regex DayOrRow = new(
        @"<h3\s+class=""h5 my-0"">(?<day>[^<]+)</h3>" +
        @"|<tr[^>]*id=""week-(?<week>up|down|both)-container""[^>]*>(?<body>.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CellTd = new(@"<td\b[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex CellTh = new(@"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SpanIn = new(@"<span\b[^>]*>(.*?)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex SmallMuted = new(@"<small\b[^>]*class=""[^""]*text-muted[^""]*""[^>]*>(.*?)</small>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TimeRange = new(@"(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    private static List<Lesson> ParseRoomSchedule(string html)
    {
        var lessons = new List<Lesson>();

        int tableStart = html.IndexOf("id=\"table-container\"", StringComparison.Ordinal);
        string scope = tableStart >= 0 ? html[tableStart..] : html;

        DayOfWeek? currentDay = null;
        foreach (Match m in DayOrRow.Matches(scope))
        {
            if (m.Groups["day"].Success)
            {
                currentDay = ParseDay(Text(m.Groups["day"].Value));
                continue;
            }
            if (currentDay is null) continue;

            string body = m.Groups["body"].Value;

            var thm = CellTh.Match(body);
            if (!thm.Success) continue;
            var tm = TimeRange.Match(Text(thm.Groups[1].Value));
            if (!tm.Success) continue;
            var start = new TimeOnly(int.Parse(tm.Groups[1].Value), int.Parse(tm.Groups[2].Value));
            var end = new TimeOnly(int.Parse(tm.Groups[3].Value), int.Parse(tm.Groups[4].Value));

            var tds = CellTd.Matches(body);
            if (tds.Count < 5) continue;

            WeekKind week = m.Groups["week"].Value switch
            {
                "up" => WeekKind.Up,
                "down" => WeekKind.Down,
                _ => WeekKind.Both,
            };

            string roomTitle = Text(tds[1].Groups[1].Value);
            string group = Text(tds[2].Groups[1].Value);

            string subjCell = tds[3].Groups[1].Value;
            var sm = SpanIn.Match(subjCell);
            string subject = Text(sm.Success ? sm.Groups[1].Value : subjCell);
            var tym = SmallMuted.Match(subjCell);
            string type = tym.Success ? Text(tym.Groups[1].Value) : "";

            string teacher = Text(tds[4].Groups[1].Value);

            lessons.Add(new Lesson(currentDay.Value, start, end, week, subject, type, group, teacher, roomTitle));
        }

        return lessons;
    }

    // ---------- Утилиты ----------

    private static DayOfWeek? ParseDay(string text)
    {
        string t = text.ToLowerInvariant();
        if (t.Contains("понедельник")) return DayOfWeek.Monday;
        if (t.Contains("вторник")) return DayOfWeek.Tuesday;
        if (t.Contains("сред")) return DayOfWeek.Wednesday;
        if (t.Contains("четверг")) return DayOfWeek.Thursday;
        if (t.Contains("пятниц")) return DayOfWeek.Friday;
        if (t.Contains("суббот")) return DayOfWeek.Saturday;
        if (t.Contains("воскрес")) return DayOfWeek.Sunday;
        return null;
    }

    private static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "понедельник",
        DayOfWeek.Tuesday => "вторник",
        DayOfWeek.Wednesday => "среда",
        DayOfWeek.Thursday => "четверг",
        DayOfWeek.Friday => "пятница",
        DayOfWeek.Saturday => "суббота",
        _ => "воскресенье",
    };

    private static string ParityName(WeekKind w) => w switch
    {
        WeekKind.Up => "верхняя неделя",
        WeekKind.Down => "нижняя неделя",
        _ => "обе недели",
    };

    private static string Norm(string s) => Clean(s).Replace(" ", "").ToLowerInvariant();

    // Снимает теги и декодирует HTML-сущности, нормализует пробелы.
    private static string Text(string html) => Clean(Decode(Regex.Replace(html ?? "", "<[^>]+>", " ")));

    private static string Decode(string s)
    {
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&quot;", "\"")
             .Replace("&laquo;", "«").Replace("&raquo;", "»").Replace("&mdash;", "—")
             .Replace("&ndash;", "–").Replace("&#39;", "'").Replace("&apos;", "'")
             .Replace("&lt;", "<").Replace("&gt;", ">");
        return Regex.Replace(s, @"&#(\d+);", m => ((char)int.Parse(m.Groups[1].Value)).ToString());
    }

    private static string Clean(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string Prompt(string message)
    {
        Console.Write(message);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private sealed class Options
    {
        public string? Room;
        public string? Building;
        public bool Dump;
        public DateTime? At;
        public WeekKind? Week;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            var rest = new List<string>();
            foreach (var arg in args)
            {
                if (arg == "--dump") o.Dump = true;
                else if (arg.StartsWith("--building=")) o.Building = arg["--building=".Length..];
                else if (arg is "--week=up" or "--week=верх") o.Week = WeekKind.Up;
                else if (arg is "--week=down" or "--week=ниж") o.Week = WeekKind.Down;
                else if (arg.StartsWith("--at="))
                {
                    string v = arg["--at=".Length..];
                    if (DateTime.TryParse(v, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var dt))
                        o.At = dt;
                    else if (TimeOnly.TryParse(v, CultureInfo.GetCultureInfo("ru-RU"), out var t))
                        o.At = DateTime.Today.Add(t.ToTimeSpan());
                }
                else rest.Add(arg);
            }
            if (rest.Count > 0) o.Room = string.Join(' ', rest).Trim();
            return o;
        }
    }
}
