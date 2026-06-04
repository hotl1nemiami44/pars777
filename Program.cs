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

// --- Подключаем нужные пространства имён из стандартной библиотеки ---
using System.Globalization;            // CultureInfo — для русской локали при разборе дат
using System.Text;                     // Encoding.UTF8 — для корректного вывода кириллицы
using System.Text.Json;                // JsonSerializer — встроенный разбор JSON (объекта arRoom)
using System.Text.Json.Serialization;  // [JsonPropertyName] — связь полей JSON с C#-свойствами
using System.Text.RegularExpressions;  // Regex — регулярные выражения для парсинга HTML

// File-scoped namespace: всё в файле живёт в пространстве имён RoomSchedule.
namespace RoomSchedule;

// =============================================================================
// МОДЕЛИ ДАННЫХ
// =============================================================================

/// <summary>Тип недели в расписании.</summary>
// На сайте занятие может идти на верхней неделе (Up), нижней (Down)
// или каждую неделю (Both — отмечено как «обе недели»).
internal enum WeekKind { Up, Down, Both }

/// <summary>Аудитория: корпус, внутренний id, отображаемый номер и hex-ключ.</summary>
// record — компактное объявление неизменяемого типа: компилятор сам создаст
// конструктор с этими параметрами, свойства, Equals, GetHashCode и ToString.
internal sealed record Room(string Building, string Id, string Title, string Hex)
{
    // Вычисляемое свойство: собирает полный URL расписания этой аудитории.
    // Формат ссылки на сайте: /viewschedule/room/{id}/{hex}/
    public string ScheduleUrl => $"{SmtuClient.BaseUrl}/viewschedule/room/{Id}/{Hex}/";
}

/// <summary>Одно занятие из расписания аудитории.</summary>
// Все поля занятия в одной структуре: когда, тип недели, что и кто.
internal sealed record Lesson(
    DayOfWeek Day,     // день недели
    TimeOnly Start,    // время начала пары
    TimeOnly End,      // время конца пары
    WeekKind Week,     // верхняя/нижняя/обе
    string Subject,    // название предмета
    string Type,       // вид занятия (лекция / практика / лабораторное)
    string Group,      // номер группы
    string Teacher);   // ФИО преподавателя

// =============================================================================
// ТОЧКА ВХОДА И ОСНОВНОЙ СЦЕНАРИЙ
// =============================================================================

internal static class Program
{
    // Main — точка входа. Возвращает int (код возврата процесса).
    // async Task<int> — поддерживает await внутри.
    private static async Task<int> Main(string[] args)
    {
        // Переключаем вывод консоли на UTF-8, чтобы кириллица не превратилась в «?».
        // Если терминал не поддерживает — молча идём дальше.
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* терминал без UTF-8 */ }

        // Если запустили без аргументов — будем спрашивать кабинет с клавиатуры
        // и подождём нажатие клавиши перед выходом (чтобы окно не закрылось сразу).
        bool interactive = args.Length == 0;

        // Главный try/catch: ловим ЛЮБЫЕ необработанные исключения и печатаем их,
        // чтобы пользователь увидел причину сбоя, а не загадочный код выхода.
        try
        {
            int code = await Run(args);                  // вся реальная работа здесь
            if (interactive) WaitForKey();               // ждём клавишу при интерактивном запуске
            return code;
        }
        catch (Exception ex)
        {
            // Печатаем ошибку в поток ошибок (stderr).
            Console.Error.WriteLine();
            Console.Error.WriteLine("Непредвиденная ошибка:");
            Console.Error.WriteLine(ex);                 // ex.ToString() — полный текст со стеком
            if (interactive) WaitForKey();
            return 1;                                    // 1 — общий код ошибки
        }
    }

    // Основной сценарий: разделён с Main, чтобы Main работал чисто как «обёртка».
    private static async Task<int> Run(string[] args)
    {
        // Разбираем аргументы командной строки в удобный объект.
        var opt = CliOptions.Parse(args);

        // Берём номер из --аргумента, либо спрашиваем у пользователя.
        // Оператор ?? — «если слева null, возьми справа».
        string roomQuery = opt.Room ?? Ask("Введите номер кабинета: ");
        if (string.IsNullOrWhiteSpace(roomQuery))
        {
            // Пустой ввод — выход с кодом 2 (отличается от обычной ошибки 1).
            Console.Error.WriteLine("Номер кабинета не задан.");
            return 2;
        }

        // Момент времени, для которого ищем занятие: либо из --at=..., либо «сейчас».
        var moment = opt.At ?? DateTime.Now;
        // TimeOnly — только время суток, без даты. Нужно для сравнения с интервалом пары.
        var nowTime = TimeOnly.FromDateTime(moment);

        // Создаём HTTP-клиент. using — гарантирует Dispose() в конце метода.
        using var client = new SmtuClient();

        // --- Страница со списком расписаний: текущая неделя и карта аудиторий ---
        string listHtml;
        try
        {
            // Асинхронно качаем /ru/listschedule/.
            listHtml = await client.GetListScheduleAsync();
        }
        catch (Exception ex)
        {
            // Сюда попадём при сетевых ошибках, 403, таймауте и т.п.
            Console.Error.WriteLine($"Не удалось загрузить список расписаний: {ex.Message}");
            return 1;
        }

        // Парсим строку «Сегодня: 27 Мая 2026 года, Среда, нижняя неделя».
        // Деструктурируем возвращаемый кортеж в две переменные.
        var (siteDay, siteWeek) = ScheduleParser.ParseTodayLine(listHtml);

        // Финальные значения с приоритетами:
        // 1) если пользователь явно указал — берём его значение,
        // 2) иначе значение с сайта,
        // 3) иначе разумный дефолт (обе недели / системный день).
        WeekKind week = opt.Week ?? siteWeek ?? WeekKind.Both;
        DayOfWeek day = opt.At?.DayOfWeek ?? siteDay ?? moment.DayOfWeek;

        // Достаём список всех аудиторий со страницы (из встроенного JS-объекта arRoom).
        List<Room> rooms = ScheduleParser.ParseRooms(listHtml);
        if (rooms.Count == 0)
        {
            // Если ничего не нашли — значит, разметка сайта поменялась.
            Console.Error.WriteLine("Не удалось разобрать список аудиторий (возможно, изменилась разметка сайта).");
            return 1;
        }

        // Ищем кабинет с учётом возможного фильтра по корпусу (--building=...).
        // Может вернуться несколько (например, «215» есть в нескольких корпусах).
        List<Room> matches = RoomFinder.Find(rooms, roomQuery, opt.Building);
        if (matches.Count == 0)
        {
            // Не нашли ничего — подсказываем первые 15 доступных номеров.
            Console.Error.WriteLine($"Аудитория «{roomQuery}» не найдена.");
            Console.Error.WriteLine("Примеры доступных: " +
                string.Join(", ", rooms.Take(15).Select(r => $"{r.Title} ({r.Building})")) + " …");
            return 1;
        }

        // Шапка результата: эхо запроса, дата/время, день, тип недели.
        // {moment:dd.MM.yyyy HH:mm} — формат вывода даты внутри интерполяции строки.
        Console.WriteLine($"Кабинет:       {roomQuery}");
        Console.WriteLine($"Момент:        {moment:dd.MM.yyyy HH:mm}, {Ru.DayName(day)}, {Ru.WeekName(week)}");
        Console.WriteLine();

        // --- По каждой подходящей аудитории качаем её страницу и ищем текущее занятие ---
        foreach (var room in matches)
        {
            string html;
            try
            {
                // Качаем страницу конкретной аудитории.
                html = await client.GetAsync(room.ScheduleUrl);
            }
            catch (Exception ex)
            {
                // Если для одной аудитории не вышло — печатаем и идём дальше.
                Console.Error.WriteLine($"  ! {room.Title} ({room.Building}): {ex.Message}");
                continue;
            }

            // Разбираем расписание в плоский список занятий.
            List<Lesson> lessons = ScheduleParser.ParseRoomSchedule(html);

            // Заголовок блока: если кабинетов несколько — указываем корпус,
            // чтобы пользователь различал «215» в разных корпусах.
            string header = matches.Count > 1
                ? $"Аудитория {room.Title} ({room.Building})"
                : $"Аудитория {room.Title}";

            // Режим --dump: высыпаем всё расписание кабинета (для проверки/отладки).
            if (opt.Dump)
            {
                Console.WriteLine($"=== {header} — {room.ScheduleUrl}");
                // Сортируем по дню недели, потом по времени начала.
                foreach (var l in lessons.OrderBy(l => l.Day).ThenBy(l => l.Start))
                    // ,-12 и ,-14 — выравнивание текста на N символов влево (для красивых столбцов).
                    // HH\\:mm — формат «09:00»: \\: экранирует двоеточие в формат-спецификаторе.
                    Console.WriteLine($"  {Ru.DayName(l.Day),-12} {l.Start:HH\\:mm}-{l.End:HH\\:mm} " +
                                      $"{Ru.WeekName(l.Week),-14} гр.{l.Group,-7} {l.Subject} " +
                                      $"({l.Type}) — {l.Teacher}");
                Console.WriteLine();
                continue;
            }

            // Главное: фильтруем «что идёт прямо сейчас».
            // LINQ-цепочка читается как фраза: «возьми занятия, где день нужный,
            // время попадает в интервал и тип недели подходит, и собери в список».
            var current = lessons
                .Where(l => l.Day == day
                            && nowTime >= l.Start && nowTime <= l.End
                            && (l.Week == WeekKind.Both || l.Week == week))
                .ToList();

            if (current.Count == 0)
            {
                // Подходящих занятий нет — пара не идёт, перерыв, выходной и т.п.
                Console.WriteLine($"{header}: сейчас занятий нет.");
                Console.WriteLine();
                continue;
            }

            // Печатаем найденные занятия.
            Console.WriteLine($"{header} — сейчас идёт:");
            foreach (var l in current)
            {
                Console.WriteLine($"  Время:         {l.Start:HH\\:mm}-{l.End:HH\\:mm}");
                // OrDash превращает пустую строку в «—», чтобы не печатать пустоту.
                // Тип занятия добавляем в скобках только если он указан.
                Console.WriteLine($"  Предмет:       {Ru.OrDash(l.Subject)}" +
                                  (l.Type.Length > 0 ? $" ({l.Type})" : ""));
                Console.WriteLine($"  Преподаватель: {Ru.OrDash(l.Teacher)}");
                Console.WriteLine($"  Группа:        {Ru.OrDash(l.Group)}");
                Console.WriteLine();
            }
        }

        return 0; // успешный выход
    }

    // Простой ввод строки с приглашением. ?.Trim() — обрезка пробелов
    // по краям, если ReadLine() вернул не null; ?? "" — заменяем null на пусто.
    private static string Ask(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine()?.Trim() ?? "";
    }

    // Ждёт нажатия любой клавиши, чтобы консоль не закрылась мгновенно.
    private static void WaitForKey()
    {
        try
        {
            Console.WriteLine();
            Console.Write("Нажмите любую клавишу для выхода…");
            // intercept: true — клавиша не отобразится в консоли как символ.
            Console.ReadKey(intercept: true);
        }
        catch { /* ввод недоступен (например, перенаправлён) — просто выходим */ }
    }
}

// =============================================================================
// HTTP-КЛИЕНТ К САЙТУ smtu.ru
// =============================================================================

/// <summary>HTTP-клиент к сайту СПбГМТУ с браузерными заголовками.</summary>
// sealed — нельзя наследоваться; IDisposable — у объекта надо вызывать Dispose().
internal sealed class SmtuClient : IDisposable
{
    // Базовый URL и адрес страницы со списком расписаний.
    public const string BaseUrl = "https://www.smtu.ru";
    private const string ListUrl = BaseUrl + "/ru/listschedule/";

    // readonly — поле задаётся только в конструкторе и потом не меняется.
    private readonly HttpClient _http;

    public SmtuClient()
    {
        // Создаём HttpClient с автоматическим следованием за HTTP-редиректами.
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(30), // защита от «вечного» зависания запроса
        };

        // Браузерные заголовки — некоторые сайты блокируют запросы без них или
        // отдают другую версию страницы. Имитируем обычный Chrome.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
    }

    // Сокращение для часто используемой страницы.
    // Expression-bodied member: => эквивалент { return ...; }.
    public Task<string> GetListScheduleAsync() => GetAsync(ListUrl);

    // Универсальный GET — возвращает тело ответа как строку.
    public async Task<string> GetAsync(string url)
    {
        using var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();           // бросит исключение, если код не 2xx
        return await resp.Content.ReadAsStringAsync();
    }

    // Освобождаем внутренний HttpClient (закрываем соединения и т.п.).
    public void Dispose() => _http.Dispose();
}

// =============================================================================
// РАЗБОР HTML-СТРАНИЦ РАСПИСАНИЯ
// =============================================================================

/// <summary>Разбор HTML-страниц расписания.</summary>
internal static class ScheduleParser
{
    // --- DTO для разбора JSON-объекта arRoom ---
    //
    // Структура на сайте:
    //   var arRoom = {
    //     "1": { "building_title": "Корпус А", "ar": {
    //         "1951": { "title": "215", "hex": "0666..." }, ... } },
    //     "2": { ... }, ...
    //   };
    //
    // Внешний ключ ("1", "2", ...) — id корпуса; внутренний — id кабинета.

    private sealed class BuildingDto
    {
        // [JsonPropertyName(...)] говорит сериализатору: «в JSON это поле
        // называется building_title, в C# — Title».
        [JsonPropertyName("building_title")] public string Title { get; set; } = "";
        // Словарь: id кабинета (строка) → описание кабинета.
        [JsonPropertyName("ar")] public Dictionary<string, RoomDto> Rooms { get; set; } = new();
    }

    private sealed class RoomDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("hex")] public string Hex { get; set; } = "";
    }

    // Главный публичный метод: HTML → плоский список аудиторий.
    public static List<Room> ParseRooms(string html)
    {
        var rooms = new List<Room>();

        // 1) Вырезаем тело JS-объекта arRoom.
        string? json = ExtractArRoomJson(html);
        if (json is null) return rooms; // вернёт пустой список, если не нашли

        // 2) Разбираем JSON в словарь корпусов.
        Dictionary<string, BuildingDto>? buildings;
        try
        {
            buildings = JsonSerializer.Deserialize<Dictionary<string, BuildingDto>>(json);
        }
        catch (JsonException)
        {
            // JSON битый или формат изменился — просто вернём пустой список.
            return rooms;
        }
        if (buildings is null) return rooms;

        // 3) Разворачиваем вложенную структуру в плоский список.
        // Двойной foreach: внешний по корпусам, внутренний по кабинетам в корпусе.
        // var (id, dto) — деструктуризация пары «ключ-значение» словаря.
        foreach (var b in buildings.Values)
            foreach (var (id, dto) in b.Rooms)
                rooms.Add(new Room(b.Title, id, dto.Title, dto.Hex));

        return rooms;
    }

    // Регуляркой надёжно вырезать вложенный JSON сложно, поэтому делаем
    // вручную: находим var arRoom = { и идём по символам, считая баланс
    // открывающих и закрывающих фигурных скобок. Возвращаем подстроку
    // от первой { до парной ей } включительно.
    private static string? ExtractArRoomJson(string html)
    {
        // Находим слово arRoom (StringComparison.Ordinal — побайтовое сравнение).
        int at = html.IndexOf("arRoom", StringComparison.Ordinal);
        if (at < 0) return null;

        // Первая { после arRoom.
        int start = html.IndexOf('{', at);
        if (start < 0) return null;

        int depth = 0;
        for (int i = start; i < html.Length; i++)
        {
            char c = html[i];
            if (c == '{') depth++;                       // углубились
            else if (c == '}' && --depth == 0)           // вышли обратно на уровень 0
                return html.Substring(start, i - start + 1); // от { до } включительно
        }
        return null; // не нашли парную закрывающую скобку
    }

    // --- Разбор заголовка «Сегодня: 27 Мая 2026 года, Среда, нижняя неделя» ---

    public static (DayOfWeek? Day, WeekKind? Week) ParseTodayLine(string html)
    {
        // Берём текст от «Сегодня:» до ближайшего HTML-тега ([^<]* — что угодно, кроме «<»).
        var m = Regex.Match(html, @"Сегодня:[^<]*");
        string s = m.Success ? m.Value : "";

        // Пытаемся опознать день недели по слову внутри строки.
        DayOfWeek? day = Ru.ParseDay(s);

        // Тип недели: цепочка тернарных операторов.
        // «верхняя/верхней/...» → Up, «нижняя/нижней/...» → Down, иначе null.
        WeekKind? week =
            Regex.IsMatch(s, "верхн", RegexOptions.IgnoreCase) ? WeekKind.Up :
            Regex.IsMatch(s, "нижн", RegexOptions.IgnoreCase) ? WeekKind.Down :
            null;

        return (day, week); // возвращаем кортеж из двух значений
    }

    // --- Расписание аудитории (табличный вид #table-container) ---
    //
    // На странице аудитории два вида: карточки и таблица — с одинаковыми
    // данными. Берём таблицу, потому что её проще разбирать.
    // Структура: на каждый день карточка с заголовком <h3 class="h5 my-0">День</h3>,
    // внутри таблица; каждое занятие — строка <tr id="week-{up|down|both}-container">
    // с ячейками: th=время, td0=иконка недели, td1=аудитория, td2=группа,
    // td3=предмет+тип, td4=преподаватель.

    // Комбинированная регулярка: ловит ЛИБО заголовок дня, ЛИБО строку занятия.
    // | — «или». (?<name>...) — именованная группа: к ней можно обратиться
    // как m.Groups["name"].
    private static readonly Regex DayHeaderOrRow = new(
        @"<h3\s+class=""h5 my-0"">(?<day>[^<]+)</h3>" +
        @"|<tr[^>]*\bid=""week-(?<week>up|down|both)-container""[^>]*>(?<row>.*?)</tr>",
        RegexOptions.Singleline                // точка совпадает с переводом строки
        | RegexOptions.Compiled);              // прекомпиляция → быстрее повторное использование

    // Ячейки таблицы внутри строки.
    private static readonly Regex ThCell = new(@"<th\b[^>]*>(.*?)</th>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TdCell = new(@"<td\b[^>]*>(.*?)</td>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    // Первый <span> внутри ячейки — название предмета.
    private static readonly Regex InnerSpan = new(@"<span\b[^>]*>(.*?)</span>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    // <small class="...text-muted..."> — тип занятия (Лекция / Практическое / ...).
    // Двойная кавычка "" внутри @"..." обозначает одну " в самой регулярке.
    private static readonly Regex MutedSmall = new(
        @"<small\b[^>]*class=""[^""]*text-muted[^""]*""[^>]*>(.*?)</small>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    // Интервал времени вида 11:50-13:20 (с разными вариантами тире).
    private static readonly Regex TimeRange = new(
        @"(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    // HTML страницы аудитории → список занятий.
    public static List<Lesson> ParseRoomSchedule(string html)
    {
        var lessons = new List<Lesson>();

        // На странице сначала идёт вид-карточек, потом #table-container с таблицей.
        // Берём всё начиная с table-container, чтобы карточки не попали в выборку
        // (иначе каждое занятие посчиталось бы дважды).
        int tableAt = html.IndexOf("id=\"table-container\"", StringComparison.Ordinal);
        string scope = tableAt >= 0 ? html[tableAt..] : html;  // html[tableAt..] — подстрока с индекса tableAt до конца

        // Текущий «обрабатываемый день»: меняется, когда встречаем заголовок дня.
        DayOfWeek? day = null;

        // Идём по всем совпадениям комбинированной регулярки В ПОРЯДКЕ их появления.
        // Так заголовки дней и строки занятий чередуются естественно.
        foreach (Match m in DayHeaderOrRow.Matches(scope))
        {
            // Совпадение — заголовок дня: обновляем текущий день, идём дальше.
            if (m.Groups["day"].Success)
            {
                day = Ru.ParseDay(Html.Text(m.Groups["day"].Value));
                continue;
            }

            // Защита: если строка занятия попалась до первого заголовка дня — пропустим.
            if (day is null) continue;

            // Тело строки <tr>...</tr>.
            string row = m.Groups["row"].Value;

            // Ищем <th> с временем.
            var th = ThCell.Match(row);
            if (!th.Success) continue;
            var t = TimeRange.Match(Html.Text(th.Groups[1].Value));
            if (!t.Success) continue;

            // Часы и минуты начала/конца — это группы 1..4 регулярки TimeRange.
            var start = new TimeOnly(int.Parse(t.Groups[1].Value), int.Parse(t.Groups[2].Value));
            var end = new TimeOnly(int.Parse(t.Groups[3].Value), int.Parse(t.Groups[4].Value));

            // Все <td> строки. Их должно быть минимум 5:
            // 0=иконка недели, 1=аудитория, 2=группа, 3=предмет+тип, 4=преподаватель.
            var tds = TdCell.Matches(row);
            if (tds.Count < 5) continue;

            // Преобразуем строковое значение группы week ("up"/"down"/...) в enum.
            // switch-выражение: компактная форма множественного выбора;
            // _ — «иначе» (на случай неожиданного значения).
            WeekKind week = m.Groups["week"].Value switch
            {
                "up" => WeekKind.Up,
                "down" => WeekKind.Down,
                _ => WeekKind.Both,
            };

            // Группа — третья ячейка (td[2]). Html.Text снимает теги и нормализует пробелы.
            string group = Html.Text(tds[2].Groups[1].Value);

            // Предмет и тип — в одной ячейке td[3].
            // Внутри: <span>Название</span><br><small class="text-muted">Тип</small>...
            string subjectCell = tds[3].Groups[1].Value;
            var span = InnerSpan.Match(subjectCell);
            // Если есть <span> — берём его текст; иначе пытаемся взять всё.
            string subject = Html.Text(span.Success ? span.Groups[1].Value : subjectCell);
            var typeM = MutedSmall.Match(subjectCell);
            string type = typeM.Success ? Html.Text(typeM.Groups[1].Value) : "";

            // Преподаватель — td[4]. Внутри может быть <a>...</a> или <span>...</span> с ФИО.
            string teacher = Html.Text(tds[4].Groups[1].Value);

            // Собираем объект Lesson и кладём в список. day.Value безопасно,
            // т.к. выше проверили, что day != null.
            lessons.Add(new Lesson(day.Value, start, end, week, subject, type, group, teacher));
        }

        return lessons;
    }
}

// =============================================================================
// ПОИСК АУДИТОРИИ ПО НОМЕРУ
// =============================================================================

/// <summary>Поиск аудитории по введённому номеру.</summary>
internal static class RoomFinder
{
    public static List<Room> Find(List<Room> rooms, string query, string? building)
    {
        // IEnumerable — «ленивая» последовательность: фильтры применяются
        // в момент перебора, без копирования списка.
        IEnumerable<Room> pool = rooms;

        // Если задан корпус (--building=...), оставляем только подходящие.
        // Сравнение по нормализованной форме (без пробелов, в нижнем регистре).
        if (!string.IsNullOrWhiteSpace(building))
            pool = pool.Where(r => Key(r.Building).Contains(Key(building)));

        var list = pool.ToList();     // материализуем результат фильтрации
        string q = Key(query);        // нормализованный запрос

        // Сначала пытаемся найти ТОЧНОЕ совпадение номера: «215» → «215».
        var exact = list.Where(r => Key(r.Title) == q).ToList();
        if (exact.Count > 0) return exact;

        // Если точных нет — более мягкий поиск: начинается с или содержит.
        // Так «3» найдёт «313», «320», а «215» — «215А» и т.п.
        return list.Where(r => Key(r.Title).StartsWith(q) || Key(r.Title).Contains(q)).ToList();
    }

    // Нормализация для сравнения: убираем ВСЕ пробельные символы
    // (обычные, табы, неразрывные) и приводим к нижнему регистру.
    // Так «А 215» совпадёт с «а215», а «Корпус А» — с «корпуса».
    private static string Key(string s) =>
        new string((s ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}

// =============================================================================
// УТИЛИТЫ ОЧИСТКИ HTML
// =============================================================================

/// <summary>Очистка HTML: снятие тегов и декодирование сущностей.</summary>
internal static class Html
{
    // Регулярки прекомпилируем один раз для скорости.
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);       // любой тег
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);        // подряд идущие пробельные
    private static readonly Regex NumEntity = new(@"&#(\d+);", RegexOptions.Compiled);// числовая сущность вида &#1040;

    // Превращает кусок HTML в чистый текст:
    //   1) убираем теги (заменяем на пробел, чтобы слова не слипались),
    //   2) декодируем HTML-сущности (&nbsp;, &amp;, ...),
    //   3) схлопываем подряд идущие пробелы в один и обрезаем края.
    public static string Text(string html)
    {
        string s = Tags.Replace(html ?? "", " ");
        s = Decode(s);
        return Spaces.Replace(s, " ").Trim();
    }

    // Декодирование часто встречающихся HTML-сущностей.
    private static string Decode(string s)
    {
        // Цепочка Replace для именованных сущностей.
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&quot;", "\"")
             .Replace("&laquo;", "«").Replace("&raquo;", "»")
             .Replace("&mdash;", "—").Replace("&ndash;", "–")
             .Replace("&#39;", "'").Replace("&apos;", "'")
             .Replace("&lt;", "<").Replace("&gt;", ">");
        // Числовые сущности вида &#1040; → символ с кодом 1040 («А»).
        // Лямбда m => ... вызывается для каждой найденной сущности.
        return NumEntity.Replace(s, m => ((char)int.Parse(m.Groups[1].Value)).ToString());
    }
}

// =============================================================================
// РУССКАЯ ЛОКАЛИЗАЦИЯ
// =============================================================================

/// <summary>Русские названия дней/недель и разбор дня недели из текста.</summary>
internal static class Ru
{
    // По куску текста пытается определить день недели.
    // Ищем корень слова (без окончания), чтобы поймать любую форму:
    // «среда», «среды», «средам» — все начинаются на «сред».
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
        return null; // ничего не подошло
    }

    // Обратное: enum → русское название.
    // switch-выражение возвращает значение по сопоставлению с шаблонами.
    public static string DayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "понедельник",
        DayOfWeek.Tuesday => "вторник",
        DayOfWeek.Wednesday => "среда",
        DayOfWeek.Thursday => "четверг",
        DayOfWeek.Friday => "пятница",
        DayOfWeek.Saturday => "суббота",
        _ => "воскресенье", // _ — на все остальные случаи (Sunday)
    };

    // То же для типа недели.
    public static string WeekName(WeekKind w) => w switch
    {
        WeekKind.Up => "верхняя неделя",
        WeekKind.Down => "нижняя неделя",
        _ => "обе недели",
    };

    // Подменяет пустую строку на «—», чтобы вывод не выглядел поломанным.
    public static string OrDash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
}

// =============================================================================
// АРГУМЕНТЫ КОМАНДНОЙ СТРОКИ
// =============================================================================

/// <summary>Разбор аргументов командной строки.</summary>
internal sealed class CliOptions
{
    // Свойства с приватным сеттером: читать можно отовсюду, менять — только
    // изнутри этого класса (в методе Parse).
    public string? Room { get; private set; }      // номер кабинета
    public string? Building { get; private set; }  // фильтр по корпусу
    public bool Dump { get; private set; }         // флаг --dump
    public DateTime? At { get; private set; }      // момент времени (вместо «сейчас»)
    public WeekKind? Week { get; private set; }    // переопределение типа недели

    // Фабричный метод: принимает args, возвращает заполненный объект.
    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();

        // Русская культура для разбора «27.05.2026 12:00» (с точками и пробелом).
        var ru = CultureInfo.GetCultureInfo("ru-RU");

        // Сюда попадёт всё, что НЕ похоже на флаг — это будет номер кабинета
        // (может состоять из нескольких слов: «Корпус М, ауд 11»).
        var rest = new List<string>();

        foreach (string arg in args)
        {
            // arg is "--dump" or "-d" — паттерн-матчинг: «равен ли одной из этих строк».
            if (arg is "--dump" or "-d")
                o.Dump = true;

            // --building=ИМЯ. arg["--building=".Length..] — range-оператор на строке:
            // «возьми подстроку с такого-то индекса до конца» (всё после знака =).
            else if (arg.StartsWith("--building=", StringComparison.Ordinal))
                o.Building = arg["--building=".Length..];

            // Переключатели типа недели — английские и русские сокращения.
            else if (arg is "--week=up" or "--week=верх")
                o.Week = WeekKind.Up;
            else if (arg is "--week=down" or "--week=ниж")
                o.Week = WeekKind.Down;

            // --at=ВРЕМЯ или --at=ДАТА_ВРЕМЯ.
            else if (arg.StartsWith("--at=", StringComparison.Ordinal))
            {
                string v = arg["--at=".Length..]; // отрезаем значение после «=»

                // Сначала пробуем как полную дату+время: «27.05.2026 12:00».
                if (DateTime.TryParse(v, ru, DateTimeStyles.None, out var dt))
                    o.At = dt;
                // Иначе как время суток: «12:00» → берём сегодняшнюю дату с этим временем.
                else if (TimeOnly.TryParse(v, ru, out var time))
                    o.At = DateTime.Today.Add(time.ToTimeSpan());
                // Если и это не вышло — молча игнорируем (At останется null = «сейчас»).
            }

            // Всё остальное — куски номера кабинета.
            else
                rest.Add(arg);
        }

        // Если в rest что-то было — склеиваем через пробел и кладём как номер кабинета.
        // Так работает запуск «dotnet run -- Корпус М 11» (номер из нескольких слов).
        if (rest.Count > 0)
            o.Room = string.Join(' ', rest).Trim();

        return o;
    }
}
