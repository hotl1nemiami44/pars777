using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace RoomSchedule;

internal enum WeekParity { Both, Odd, Even }

internal sealed record Lesson(
    string Group,
    DayOfWeek Day,
    int Pair,
    WeekParity Parity,
    string Subject,
    string Teacher,
    string Room);

internal static class Program
{
    private const string BaseUrl = "https://www.smtu.ru";
    private const string ListUrl = BaseUrl + "/ru/listschedule/";

    // Звонки СПбГМТУ. При необходимости поправьте под актуальное расписание звонков.
    private static readonly (TimeOnly Start, TimeOnly End)[] Bells =
    {
        (new TimeOnly(9, 0),  new TimeOnly(10, 35)),
        (new TimeOnly(10, 45), new TimeOnly(12, 20)),
        (new TimeOnly(13, 0),  new TimeOnly(14, 35)),
        (new TimeOnly(14, 45), new TimeOnly(16, 20)),
        (new TimeOnly(16, 30), new TimeOnly(18, 5)),
        (new TimeOnly(18, 15), new TimeOnly(19, 50)),
        (new TimeOnly(20, 0),  new TimeOnly(21, 35)),
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var options = Options.Parse(args);

        string room = options.Room ?? Prompt("Введите номер кабинета: ");
        if (string.IsNullOrWhiteSpace(room))
        {
            Console.Error.WriteLine("Номер кабинета не задан.");
            return 2;
        }

        DateTime now = options.At ?? DateTime.Now;
        DayOfWeek today = now.DayOfWeek;
        var nowTime = TimeOnly.FromDateTime(now);
        int pair = CurrentPair(nowTime);
        WeekParity parity = options.Parity ?? CurrentParity(now);

        Console.WriteLine($"Кабинет:        {room}");
        Console.WriteLine($"Текущее время:  {now:dd.MM.yyyy HH:mm} ({DayName(today)})");
        Console.WriteLine($"Неделя:         {ParityName(parity)}");
        Console.WriteLine(pair > 0
            ? $"Текущая пара:   №{pair} ({Bells[pair - 1].Start:HH\\:mm}–{Bells[pair - 1].End:HH\\:mm})"
            : "Текущая пара:   сейчас пары нет");
        Console.WriteLine();

        if (pair <= 0 && options.At is null && !options.Dump)
        {
            Console.WriteLine("Сейчас занятий по расписанию звонков не идёт.");
            return 0;
        }

        using HttpClient http = CreateClient();

        Console.Error.WriteLine("Загружаю список групп…");
        List<(string Name, string Url)> groups;
        try
        {
            groups = await GetGroups(http);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось загрузить {ListUrl}: {ex.Message}");
            return 1;
        }

        if (groups.Count == 0)
        {
            Console.Error.WriteLine("На странице списка расписаний не найдено ни одной группы " +
                                    "(возможно, изменилась разметка сайта).");
            return 1;
        }
        Console.Error.WriteLine($"Найдено групп: {groups.Count}. Скачиваю расписания…");

        var allLessons = await FetchAllLessons(http, groups);

        if (options.Dump)
        {
            foreach (var l in allLessons.OrderBy(l => l.Group).ThenBy(l => l.Day).ThenBy(l => l.Pair))
                Console.WriteLine($"{l.Group} | {DayName(l.Day)} | пара {l.Pair} | " +
                                  $"{ParityName(l.Parity)} | ауд. {l.Room} | {l.Subject} | {l.Teacher}");
            return 0;
        }

        var matches = allLessons
            .Where(l => l.Day == today
                        && l.Pair == pair
                        && (l.Parity == WeekParity.Both || l.Parity == parity)
                        && RoomMatches(l.Room, room))
            .OrderBy(l => l.Group)
            .ToList();

        if (matches.Count == 0)
        {
            Console.WriteLine($"В кабинете «{room}» сейчас занятий по расписанию не найдено.");
            return 0;
        }

        Console.WriteLine($"Сейчас в кабинете «{room}»:");
        Console.WriteLine();
        foreach (var l in matches)
        {
            Console.WriteLine($"  Предмет:       {Dash(l.Subject)}");
            Console.WriteLine($"  Преподаватель: {Dash(l.Teacher)}");
            Console.WriteLine($"  Группа:        {Dash(l.Group)}");
            Console.WriteLine();
        }
        return 0;
    }

    // ---------- HTTP ----------

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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

    private static async Task<List<(string Name, string Url)>> GetGroups(HttpClient http)
    {
        string html = await GetHtml(http, ListUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var result = new List<(string, string)>();
        var seen = new HashSet<string>();

        var anchors = doc.DocumentNode.SelectNodes("//a[contains(@href,'viewschedule')]");
        if (anchors is null) return result;

        foreach (var a in anchors)
        {
            string href = a.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href)) continue;
            string url = href.StartsWith("http") ? href : BaseUrl + (href.StartsWith('/') ? "" : "/") + href;
            if (!seen.Add(url)) continue;
            string name = Clean(a.InnerText);
            if (name.Length == 0) name = url;
            result.Add((name, url));
        }
        return result;
    }

    private static async Task<List<Lesson>> FetchAllLessons(
        HttpClient http, List<(string Name, string Url)> groups)
    {
        var bag = new System.Collections.Concurrent.ConcurrentBag<Lesson>();
        using var gate = new SemaphoreSlim(8);

        var tasks = groups.Select(async g =>
        {
            await gate.WaitAsync();
            try
            {
                string html = await GetHtml(http, g.Url);
                foreach (var l in ParseSchedule(html, g.Name))
                    bag.Add(l);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  ! {g.Name}: {ex.Message}");
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return bag.ToList();
    }

    // ---------- Парсинг ----------
    //
    // Разметка viewschedule заранее неизвестна (сайт недоступен из среды сборки),
    // поэтому парсер устойчив к ориентации таблицы: он идёт по строкам таблицы,
    // запоминает «текущий день» и «текущую пару/время», а из ячеек регулярными
    // выражениями вытаскивает предмет, преподавателя, аудиторию и чётность недели.
    // Проверить результат можно ключом --dump.

    private static readonly Regex TimeRange = new(
        @"(\d{1,2})[:.](\d{2})\s*[–—-]\s*(\d{1,2})[:.](\d{2})", RegexOptions.Compiled);

    private static readonly Regex Teacher = new(
        @"[А-ЯЁ][а-яё]+(?:-[А-ЯЁ][а-яё]+)?\s+[А-ЯЁ]\.\s*[А-ЯЁ]\.?", RegexOptions.Compiled);

    private static readonly Regex RoomExplicit = new(
        @"(?:ауд(?:итория)?\.?|каб(?:инет)?\.?|пом\.?)\s*№?\s*([0-9][0-9А-Яа-яA-Za-z\-/]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LessonType = new(
        @"\b(лек(?:ция|ц)?|практ(?:ика|\.)?|пр\.|лаб(?:оратор\w*|\.)?|сем(?:инар|\.)?|конс\w*|экз\w*|зач\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<Lesson> ParseSchedule(string html, string group)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var lessons = new List<Lesson>();

        var tables = doc.DocumentNode.SelectNodes("//table");
        if (tables is null) return lessons;

        foreach (var table in tables)
        {
            var rows = table.SelectNodes(".//tr");
            if (rows is null) continue;

            DayOfWeek? currentDay = null;
            int currentPair = 0;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./td|./th");
                if (cells is null || cells.Count == 0) continue;

                var texts = cells.Select(c => Clean(c.InnerText)).ToList();
                string rowText = string.Join(" | ", texts.Where(t => t.Length > 0));
                if (rowText.Length == 0) continue;

                // День недели может быть в отдельной ячейке-заголовке.
                foreach (var t in texts)
                {
                    var d = ParseDay(t);
                    if (d.HasValue) { currentDay = d.Value; break; }
                }

                // Номер пары / время.
                var tm = TimeRange.Match(rowText);
                if (tm.Success)
                {
                    var start = new TimeOnly(int.Parse(tm.Groups[1].Value), int.Parse(tm.Groups[2].Value));
                    currentPair = NearestPair(start);
                }
                else
                {
                    var pn = Regex.Match(rowText, @"^\s*(\d{1,2})\s*(?:пара|\.)?\s*\|");
                    if (pn.Success && int.TryParse(pn.Groups[1].Value, out int p) && p >= 1 && p <= Bells.Length)
                        currentPair = p;
                }

                if (currentDay is null || currentPair == 0) continue;

                // Каждая содержательная ячейка может быть отдельным занятием
                // (в т.ч. с делением на верх/низ недели внутри ячейки).
                foreach (var cell in cells)
                {
                    foreach (var (parity, fragment) in SplitByParity(cell))
                    {
                        if (!HasLessonContent(fragment)) continue;
                        var lesson = BuildLesson(group, currentDay.Value, currentPair, parity, fragment);
                        if (lesson is not null) lessons.Add(lesson);
                    }
                }
            }
        }

        return lessons;
    }

    private static bool HasLessonContent(string text)
    {
        if (text.Length < 3) return false;
        return RoomExplicit.IsMatch(text) || Teacher.IsMatch(text) || LessonType.IsMatch(text);
    }

    private static IEnumerable<(WeekParity Parity, string Text)> SplitByParity(HtmlNode cell)
    {
        string full = Clean(cell.InnerText);
        if (full.Length == 0) yield break;

        bool hasOdd = Regex.IsMatch(full, @"нечёт|нечет|верхн|числит", RegexOptions.IgnoreCase);
        bool hasEven = Regex.IsMatch(full, @"чётн|четн|нижн|знаменат", RegexOptions.IgnoreCase);

        if (hasOdd && hasEven)
        {
            // Пытаемся разделить ячейку по строкам/блокам на «нечётную» и «чётную».
            var lines = cell.InnerText.Split('\n')
                .Select(Clean).Where(s => s.Length > 0).ToList();
            var odd = new StringBuilder();
            var even = new StringBuilder();
            WeekParity bucket = WeekParity.Both;
            foreach (var line in lines)
            {
                if (Regex.IsMatch(line, @"нечёт|нечет|верхн|числит", RegexOptions.IgnoreCase)) bucket = WeekParity.Odd;
                else if (Regex.IsMatch(line, @"чётн|четн|нижн|знаменат", RegexOptions.IgnoreCase)) bucket = WeekParity.Even;
                if (bucket == WeekParity.Odd) odd.Append(line).Append(' ');
                else if (bucket == WeekParity.Even) even.Append(line).Append(' ');
            }
            if (odd.Length > 0) yield return (WeekParity.Odd, Clean(odd.ToString()));
            if (even.Length > 0) yield return (WeekParity.Even, Clean(even.ToString()));
            yield break;
        }

        WeekParity p = hasOdd ? WeekParity.Odd : hasEven ? WeekParity.Even : WeekParity.Both;
        yield return (p, full);
    }

    private static Lesson? BuildLesson(string group, DayOfWeek day, int pair, WeekParity parity, string text)
    {
        string room = "";
        var rm = RoomExplicit.Match(text);
        if (rm.Success) room = rm.Groups[1].Value.Trim();

        string teacher = "";
        var tm = Teacher.Match(text);
        if (tm.Success) teacher = tm.Value.Trim();

        // Предмет: убираем найденные преподавателя/аудиторию/тип/маркеры чётности.
        string subject = text;
        if (teacher.Length > 0) subject = subject.Replace(teacher, " ");
        subject = RoomExplicit.Replace(subject, " ");
        subject = LessonType.Replace(subject, " ");
        subject = Regex.Replace(subject,
            @"нечёт\w*|нечет\w*|чётн\w*|четн\w*|верхн\w*|нижн\w*|числит\w*|знаменат\w*|неделя",
            " ", RegexOptions.IgnoreCase);
        subject = Clean(Regex.Replace(subject, @"[№\-–—/.,;:()]+", " "));

        if (room.Length == 0 && teacher.Length == 0 && subject.Length == 0) return null;
        return new Lesson(group, day, pair, parity, subject, teacher, room);
    }

    // ---------- Время / неделя ----------

    private static int CurrentPair(TimeOnly t)
    {
        for (int i = 0; i < Bells.Length; i++)
            if (t >= Bells[i].Start && t <= Bells[i].End) return i + 1;
        return 0;
    }

    private static int NearestPair(TimeOnly start)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < Bells.Length; i++)
        {
            double diff = Math.Abs((Bells[i].Start - start).TotalMinutes);
            if (diff < bestDiff) { bestDiff = diff; best = i + 1; }
        }
        return bestDiff <= 20 ? best : 0;
    }

    private static WeekParity CurrentParity(DateTime now)
    {
        int week = ISOWeek.GetWeekOfYear(now);
        return week % 2 == 1 ? WeekParity.Odd : WeekParity.Even;
    }

    // ---------- Утилиты ----------

    private static DayOfWeek? ParseDay(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        if (t.StartsWith("понедельник") || t == "пн") return DayOfWeek.Monday;
        if (t.StartsWith("вторник") || t == "вт") return DayOfWeek.Tuesday;
        if (t.StartsWith("сред") || t == "ср") return DayOfWeek.Wednesday;
        if (t.StartsWith("четверг") || t == "чт") return DayOfWeek.Thursday;
        if (t.StartsWith("пятниц") || t == "пт") return DayOfWeek.Friday;
        if (t.StartsWith("суббот") || t == "сб") return DayOfWeek.Saturday;
        if (t.StartsWith("воскрес") || t == "вс") return DayOfWeek.Sunday;
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

    private static string ParityName(WeekParity p) => p switch
    {
        WeekParity.Odd => "нечётная (верхняя)",
        WeekParity.Even => "чётная (нижняя)",
        _ => "любая",
    };

    private static bool RoomMatches(string lessonRoom, string query)
    {
        string a = NormalizeRoom(lessonRoom);
        string b = NormalizeRoom(query);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || a.Contains(b) || b.Contains(a);
    }

    private static string NormalizeRoom(string s) =>
        new string(s.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).ToLowerInvariant();

    private static string Clean(string s) =>
        Regex.Replace(HtmlEntity.DeEntitize(s ?? "") .Replace(' ', ' '), @"\s+", " ").Trim();

    private static string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string Prompt(string message)
    {
        Console.Write(message);
        return Console.ReadLine()?.Trim() ?? "";
    }

    private sealed class Options
    {
        public string? Room;
        public bool Dump;
        public DateTime? At;
        public WeekParity? Parity;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            var rest = new List<string>();
            foreach (var arg in args)
            {
                if (arg == "--dump") o.Dump = true;
                else if (arg.StartsWith("--at=") && DateTime.TryParse(
                             arg[5..], CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var dt))
                    o.At = dt;
                else if (arg is "--week=odd" or "--week=нечет") o.Parity = WeekParity.Odd;
                else if (arg is "--week=even" or "--week=чет") o.Parity = WeekParity.Even;
                else rest.Add(arg);
            }
            if (rest.Count > 0) o.Room = string.Join(' ', rest).Trim();
            return o;
        }
    }
}
