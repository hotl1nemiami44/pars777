// Поиск текущего занятия в аудитории СПбГМТУ.
//
// На вход подаётся номер кабинета. Программа:
//   1. загружает страницу https://www.smtu.ru/ru/listschedule/;
//   2. читает с неё текущий день недели и тип недели (верхняя/нижняя) —
//      они напечатаны прямо в заголовке («Сегодня: … Среда, нижняя неделя»);
//   3. из встроенного на страницу JS-объекта `arRoom` сопоставляет номер
//      кабинета с его внутренним id и hex-кодом;
//   4. запрашивает расписание аудитории /viewschedule/room/{id}/{hex}/;
//   5. в табличном представлении находит занятие, идущее в текущий момент,
//      и выводит предмет, преподавателя и группу.
//
// Сторонних библиотек нет — только стандартная библиотека .NET.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RoomSchedule;

/// <summary>Тип недели в расписании.</summary>
internal enum WeekKind { Up, Down, Both }

/// <summary>Аудитория: корпус, внутренний id, отображаемый номер и hex-ключ.</summary>
internal sealed record Room(string Building, string Id, string Title, string Hex)
{
    public string ScheduleUrl => $"{SmtuClient.BaseUrl}/viewschedule/room/{Id}/{Hex}/";
}

/// <summary>Одно занятие из расписания аудитории.</summary>
internal sealed record Lesson(
    DayOfWeek Day,
    TimeOnly Start,
    TimeOnly End,
    WeekKind Week,
    string Subject,
    string Type,
    string Group,
    string Teacher);

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Чтобы кириллица в консоли Windows не превращалась в «?».
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* терминал без UTF-8 */ }

        bool interactive = args.Length == 0;
        try
        {
            int code = await Run(args);
            if (interactive) WaitForKey();
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Непредвиденная ошибка:");
            Console.Error.WriteLine(ex);
            if (interactive) WaitForKey();
            return 1;
        }
    }

    private static async Task<int> Run(string[] args)
    {
        var opt = CliOptions.Parse(args);

        string roomQuery = opt.Room ?? Ask("Введите номер кабинета: ");
        if (string.IsNullOrWhiteSpace(roomQuery))
        {
            Console.Error.WriteLine("Номер кабинета не задан.");
            return 2;
        }

        var moment = opt.At ?? DateTime.Now;
        var nowTime = TimeOnly.FromDateTime(moment);

        using var client = new SmtuClient();

        // --- Страница со списком: текущая неделя и карта аудиторий. ---
        string listHtml;
        try
        {
            listHtml = await client.GetListScheduleAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось загрузить список расписаний: {ex.Message}");
            return 1;
        }

        var (siteDay, siteWeek) = ScheduleParser.ParseTodayLine(listHtml);
        WeekKind week = opt.Week ?? siteWeek ?? WeekKind.Both;
        DayOfWeek day = opt.At?.DayOfWeek ?? siteDay ?? moment.DayOfWeek;

        List<Room> rooms = ScheduleParser.ParseRooms(listHtml);
        if (rooms.Count == 0)
        {
            Console.Error.WriteLine("Не удалось разобрать список аудиторий (возможно, изменилась разметка сайта).");
            return 1;
        }

        List<Room> matches = RoomFinder.Find(rooms, roomQuery, opt.Building);
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"Аудитория «{roomQuery}» не найдена.");
            Console.Error.WriteLine("Примеры доступных: " +
                string.Join(", ", rooms.Take(15).Select(r => $"{r.Title} ({r.Building})")) + " …");
            return 1;
        }

        Console.WriteLine($"Кабинет:       {roomQuery}");
        Console.WriteLine($"Момент:        {moment:dd.MM.yyyy HH:mm}, {Ru.DayName(day)}, {Ru.WeekName(week)}");
        Console.WriteLine();

        // --- По каждой подходящей аудитории смотрим расписание. ---
        foreach (var room in matches)
        {
            string html;
            try
            {
                html = await client.GetAsync(room.ScheduleUrl);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ! {room.Title} ({room.Building}): {ex.Message}");
                continue;
            }

            List<Lesson> lessons = ScheduleParser.ParseRoomSchedule(html);
            string header = matches.Count > 1
                ? $"Аудитория {room.Title} ({room.Building})"
                : $"Аудитория {room.Title}";

            if (opt.Dump)
            {
                Console.WriteLine($"=== {header} — {room.ScheduleUrl}");
                foreach (var l in lessons.OrderBy(l => l.Day).ThenBy(l => l.Start))
                    Console.WriteLine($"  {Ru.DayName(l.Day),-12} {l.Start:HH\\:mm}-{l.End:HH\\:mm} " +
                                      $"{Ru.WeekName(l.Week),-14} гр.{l.Group,-7} {l.Subject} " +
                                      $"({l.Type}) — {l.Teacher}");
                Console.WriteLine();
                continue;
            }

            var current = lessons
                .Where(l => l.Day == day
                            && nowTime >= l.Start && nowTime <= l.End
                            && (l.Week == WeekKind.Both || l.Week == week))
                .ToList();

            if (current.Count == 0)
            {
                Console.WriteLine($"{header}: сейчас занятий нет.");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"{header} — сейчас идёт:");
            foreach (var l in current)
            {
                Console.WriteLine($"  Время:         {l.Start:HH\\:mm}-{l.End:HH\\:mm}");
                Console.WriteLine($"  Предмет:       {Ru.OrDash(l.Subject)}" +
                                  (l.Type.Length > 0 ? $" ({l.Type})" : ""));
                Console.WriteLine($"  Преподаватель: {Ru.OrDash(l.Teacher)}");
                Console.WriteLine($"  Группа:        {Ru.OrDash(l.Group)}");
                Console.WriteLine();
            }
        }

        return 0;
    }

    private static string Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private static void WaitForKey()
    {
        try
        {
            Console.WriteLine();
            Console.Write("Нажмите любую клавишу для выхода…");
            Console.ReadKey(intercept: true);
        }
        catch { /* ввод недоступен */ }
    }
}

/// <summary>HTTP-клиент к сайту СПбГМТУ с браузерными заголовками.</summary>
internal sealed class SmtuClient : IDisposable
{
    public const string BaseUrl = "https://www.smtu.ru";
    private const string ListUrl = BaseUrl + "/ru/listschedule/";

    private readonly HttpClient _http;

    public SmtuClient()
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
    }

    public Task<string> GetListScheduleAsync() => GetAsync(ListUrl);

    public async Task<string> GetAsync(string url)
    {
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Разбор HTML-страниц расписания.</summary>
internal static class ScheduleParser
{
    // --- Список аудиторий: JSON-объект var arRoom = { "1": { building_title, ar: {...} }, ... } ---

    private sealed class BuildingDto
    {
        [JsonPropertyName("building_title")] public string Title { get; set; } = "";
        [JsonPropertyName("ar")] public Dictionary<string, RoomDto> Rooms { get; set; } = new();
    }

    private sealed class RoomDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("hex")] public string Hex { get; set; } = "";
    }

    public static List<Room> ParseRooms(string html)
    {
        var rooms = new List<Room>();
        string? json = ExtractArRoomJson(html);
        if (json is null) return rooms;

        Dictionary<string, BuildingDto>? buildings;
        try
        {
            buildings = JsonSerializer.Deserialize<Dictionary<string, BuildingDto>>(json);
        }
        catch (JsonException)
        {
            return rooms;
        }
        if (buildings is null) return rooms;

        foreach (var b in buildings.Values)
            foreach (var (id, dto) in b.Rooms)
                rooms.Add(new Room(b.Title, id, dto.Title, dto.Hex));

        return rooms;
    }

    // Вырезает тело объекта из `var arRoom = {...};` по балансу фигурных скобок.
    private static string? ExtractArRoomJson(string html)
    {
        int at = html.IndexOf("arRoom", StringComparison.Ordinal);
        if (at < 0) return null;
        int start = html.IndexOf('{', at);
        if (start < 0) return null;

        int depth = 0;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
                return html.Substring(start, i - start + 1);
        }
        return null;
    }

    // --- Строка «Сегодня: 27 Мая 2026 года, Среда, нижняя неделя» ---

    public static (DayOfWeek? Day, WeekKind? Week) ParseTodayLine(string html)
    {
        var m = Regex.Match(html, @"Сегодня:[^<]*");
        string s = m.Success ? m.Value : "";

        DayOfWeek? day = Ru.ParseDay(s);
        WeekKind? week =
            Regex.IsMatch(s, "верхн", RegexOptions.IgnoreCase) ? WeekKind.Up :
            Regex.IsMatch(s, "нижн", RegexOptions.IgnoreCase) ? WeekKind.Down :
            null;
        return (day, week);
    }

    // --- Расписание аудитории (табличный вид #table-container) ---
    //
    // Структура: на каждый день карточка с заголовком <h3 class="h5 my-0">День</h3>,
    // внутри таблица; каждое занятие — строка <tr id="week-{up|down|both}-container">
    // с ячейками: th=время, td0=иконка недели, td1=аудитория, td2=группа,
    // td3=предмет+тип, td4=преподаватель.

    private static readonly Regex DayHeaderOrRow = new(
        @"<h3\s+class=""h5 my-0"">(?<day>[^<]+)</h3>" +
        @"|<tr[^>]*\bid=""week-(?<week>up|down|both)-container""[^>]*>(?<row>.*?)</tr>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ThCell = new(@"<th\b[^>]*>(.*?)</th>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TdCell = new(@"<td\b[^>]*>(.*?)</td>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InnerSpan = new(@"<span\b[^>]*>(.*?)</span>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MutedSmall = new(
        @"<small\b[^>]*class=""[^""]*text-muted[^""]*""[^>]*>(.*?)</small>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TimeRange = new(
        @"(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    public static List<Lesson> ParseRoomSchedule(string html)
    {
        var lessons = new List<Lesson>();

        // Работаем только с табличным видом: он идёт после вида-карточек,
        // поэтому карточки в выборку не попадают и дублей не будет.
        int tableAt = html.IndexOf("id=\"table-container\"", StringComparison.Ordinal);
        string scope = tableAt >= 0 ? html[tableAt..] : html;

        DayOfWeek? day = null;
        foreach (Match m in DayHeaderOrRow.Matches(scope))
        {
            if (m.Groups["day"].Success)
            {
                day = Ru.ParseDay(Html.Text(m.Groups["day"].Value));
                continue;
            }
            if (day is null) continue;

            string row = m.Groups["row"].Value;

            var th = ThCell.Match(row);
            if (!th.Success) continue;
            var t = TimeRange.Match(Html.Text(th.Groups[1].Value));
            if (!t.Success) continue;
            var start = new TimeOnly(int.Parse(t.Groups[1].Value), int.Parse(t.Groups[2].Value));
            var end = new TimeOnly(int.Parse(t.Groups[3].Value), int.Parse(t.Groups[4].Value));

            var tds = TdCell.Matches(row);
            if (tds.Count < 5) continue;

            WeekKind week = m.Groups["week"].Value switch
            {
                "up" => WeekKind.Up,
                "down" => WeekKind.Down,
                _ => WeekKind.Both,
            };

            string group = Html.Text(tds[2].Groups[1].Value);

            string subjectCell = tds[3].Groups[1].Value;
            var span = InnerSpan.Match(subjectCell);
            string subject = Html.Text(span.Success ? span.Groups[1].Value : subjectCell);
            var typeM = MutedSmall.Match(subjectCell);
            string type = typeM.Success ? Html.Text(typeM.Groups[1].Value) : "";

            string teacher = Html.Text(tds[4].Groups[1].Value);

            lessons.Add(new Lesson(day.Value, start, end, week, subject, type, group, teacher));
        }

        return lessons;
    }
}

/// <summary>Поиск аудитории по введённому номеру.</summary>
internal static class RoomFinder
{
    public static List<Room> Find(List<Room> rooms, string query, string? building)
    {
        IEnumerable<Room> pool = rooms;
        if (!string.IsNullOrWhiteSpace(building))
            pool = pool.Where(r => Key(r.Building).Contains(Key(building)));

        var list = pool.ToList();
        string q = Key(query);

        var exact = list.Where(r => Key(r.Title) == q).ToList();
        if (exact.Count > 0) return exact;

        return list.Where(r => Key(r.Title).StartsWith(q) || Key(r.Title).Contains(q)).ToList();
    }

    // Нормализация для сравнения: без пробелов, в нижнем регистре.
    private static string Key(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}

/// <summary>Очистка HTML: снятие тегов и декодирование сущностей.</summary>
internal static class Html
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NumEntity = new(@"&#(\d+);", RegexOptions.Compiled);

    public static string Text(string html)
    {
        string s = Tags.Replace(html ?? "", " ");
        s = Decode(s);
        return Spaces.Replace(s, " ").Trim();
    }

    private static string Decode(string s)
    {
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&quot;", "\"")
             .Replace("&laquo;", "«").Replace("&raquo;", "»")
             .Replace("&mdash;", "—").Replace("&ndash;", "–")
             .Replace("&#39;", "'").Replace("&apos;", "'")
             .Replace("&lt;", "<").Replace("&gt;", ">");
        return NumEntity.Replace(s, m => ((char)int.Parse(m.Groups[1].Value)).ToString());
    }
}

/// <summary>Русские названия дней/недель и разбор дня недели из текста.</summary>
internal static class Ru
{
    public static DayOfWeek? ParseDay(string text)
    {
        string t = (text ?? "").ToLowerInvariant();
        if (t.Contains("понедельник")) return DayOfWeek.Monday;
        if (t.Contains("вторник")) return DayOfWeek.Tuesday;
        if (t.Contains("сред")) return DayOfWeek.Wednesday;
        if (t.Contains("четверг")) return DayOfWeek.Thursday;
        if (t.Contains("пятниц")) return DayOfWeek.Friday;
        if (t.Contains("суббот")) return DayOfWeek.Saturday;
        if (t.Contains("воскрес")) return DayOfWeek.Sunday;
        return null;
    }

    public static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "понедельник",
        DayOfWeek.Tuesday => "вторник",
        DayOfWeek.Wednesday => "среда",
        DayOfWeek.Thursday => "четверг",
        DayOfWeek.Friday => "пятница",
        DayOfWeek.Saturday => "суббота",
        _ => "воскресенье",
    };

    public static string WeekName(WeekKind w) => w switch
    {
        WeekKind.Up => "верхняя неделя",
        WeekKind.Down => "нижняя неделя",
        _ => "обе недели",
    };

    public static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
}

/// <summary>Разбор аргументов командной строки.</summary>
internal sealed class CliOptions
{
    public string? Room { get; private set; }
    public string? Building { get; private set; }
    public bool Dump { get; private set; }
    public DateTime? At { get; private set; }
    public WeekKind? Week { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var rest = new List<string>();

        foreach (string arg in args)
        {
            if (arg is "--dump" or "-d")
                o.Dump = true;
            else if (arg.StartsWith("--building=", StringComparison.Ordinal))
                o.Building = arg["--building=".Length..];
            else if (arg is "--week=up" or "--week=верх")
                o.Week = WeekKind.Up;
            else if (arg is "--week=down" or "--week=ниж")
                o.Week = WeekKind.Down;
            else if (arg.StartsWith("--at=", StringComparison.Ordinal))
            {
                string v = arg["--at=".Length..];
                if (DateTime.TryParse(v, ru, DateTimeStyles.None, out var dt))
                    o.At = dt;
                else if (TimeOnly.TryParse(v, ru, out var time))
                    o.At = DateTime.Today.Add(time.ToTimeSpan());
            }
            else
                rest.Add(arg);
        }

        if (rest.Count > 0)
            o.Room = string.Join(' ', rest).Trim();

        return o;
    }
}
