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

        // Режим журнала по явной группе (--group=NNNN): оценки/посещаемость
        // без обращения к сайту — сразу открываем список этой группы.
        if (opt.Group is not null)
            return Journal.RunForGroup(opt, opt.Group, opt.Subject, opt.At ?? DateTime.Now);

        // Запущено ли интерактивно (без аргументов) — тогда можем предложить журнал.
        bool interactive = args.Length == 0;

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

        // Сюда собираем (группа, предмет) по всем идущим сейчас занятиям —
        // чтобы потом при желании открыть по ним журнал.
        var foundForJournal = new List<(string Group, string Subject)>();

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

                // Запоминаем группу и предмет — пригодятся для журнала.
                if (!string.IsNullOrWhiteSpace(l.Group))
                    foundForJournal.Add((l.Group, l.Subject));
            }
        }

        // --- Журнал по найденной группе ---
        // Открываем, если задан флаг --journal, либо (в интерактивном режиме)
        // спрашиваем у пользователя по каждой найденной группе.
        if (foundForJournal.Count > 0 && (opt.Journal || interactive))
        {
            // Уникальные группы (одно занятие — одна группа, но на всякий случай).
            var groups = foundForJournal.DistinctBy(x => x.Group).ToList();

            RosterStore? roster = null;
            foreach (var (grp, subj) in groups)
            {
                // В интерактиве спрашиваем; с флагом --journal открываем без вопросов.
                bool open = opt.Journal || AskYesNo($"Открыть журнал группы {grp}?");
                if (!open) continue;

                // Реестр грузим один раз — при первой реальной надобности.
                roster ??= Journal.LoadRoster(opt.Roster);
                if (roster is null) break; // не нашли файл — сообщение уже выведено

                Journal.Run(roster, grp, subj, moment, opt.Out);
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

    // Вопрос «да/нет». Считаем ответом «да» строки, начинающиеся на д/y/1.
    private static bool AskYesNo(string prompt)
    {
        Console.Write($"{prompt} (д/н): ");
        string a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        return a.StartsWith('д') || a.StartsWith('y') || a == "1" || a == "+";
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

    // --- Параметры журнала оценок и посещаемости ---
    public string? Roster { get; private set; }    // путь к файлу со списками групп
    public string? Group { get; private set; }     // вести журнал сразу по этой группе (без сайта)
    public bool Journal { get; private set; }       // открыть журнал для найденной группы
    public string? Out { get; private set; }       // путь для сохранения CSV
    public string? Subject { get; private set; }   // предмет (если задаём вручную)

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

            // --- Журнал ---
            // Открыть журнал для группы найденного занятия.
            else if (arg is "--journal" or "-j")
                o.Journal = true;
            // Путь к файлу со списками групп (реестру).
            else if (arg.StartsWith("--roster=", StringComparison.Ordinal))
                o.Roster = arg["--roster=".Length..];
            // Вести журнал сразу по указанной группе, без обращения к сайту.
            else if (arg.StartsWith("--group=", StringComparison.Ordinal))
                o.Group = arg["--group=".Length..];
            // Куда сохранить CSV (по умолчанию — journal_<группа>_<дата>.csv).
            else if (arg.StartsWith("--out=", StringComparison.Ordinal))
                o.Out = arg["--out=".Length..];
            // Название предмета вручную (когда работаем по --group без расписания).
            else if (arg.StartsWith("--subject=", StringComparison.Ordinal))
                o.Subject = arg["--subject=".Length..];

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

// =============================================================================
// СПИСКИ ГРУПП, ЖУРНАЛ ОЦЕНОК И ПОСЕЩАЕМОСТИ
// =============================================================================

/// <summary>Студент из списка группы.</summary>
internal sealed record Student(string Group, string RecordId, string Name, string Track);

/// <summary>Загрузка и хранение списков групп из файла-реестра.</summary>
internal sealed class RosterStore
{
    // Группа → список её студентов.
    private readonly Dictionary<string, List<Student>> _byGroup = new();

    public int GroupCount => _byGroup.Count;
    public int StudentCount => _byGroup.Values.Sum(v => v.Count);

    // Список студентов группы (пустой список, если такой группы нет).
    public List<Student> Group(string group) =>
        _byGroup.TryGetValue(group.Trim(), out var list) ? list : new List<Student>();

    // Разбирает файл-реестр. Формат строки (разделитель «;», 6 полей):
    //   группа;внутр_id;зачётка;направление;;ФИО
    // Кодировка определяется автоматически (UTF-8 или Windows-1251).
    public static RosterStore Load(string path)
    {
        var store = new RosterStore();
        string text = TextFiles.ReadAuto(path);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            string[] f = line.Split(';');
            if (f.Length < 6) continue; // строка не в ожидаемом формате — пропускаем

            string group = f[0].Trim();
            string record = f[2].Trim();   // номер зачётки
            string track = f[3].Trim();    // направление/подгруппа (Б, К, Б / Ц …)
            string name = f[5].Trim();     // ФИО
            if (group.Length == 0 || name.Length == 0) continue;

            // Получаем (или создаём) список группы и добавляем студента.
            if (!store._byGroup.TryGetValue(group, out var list))
                store._byGroup[group] = list = new List<Student>();
            list.Add(new Student(group, record, name, track));
        }

        return store;
    }

    // Ищет файл реестра: либо явный путь, либо типовые имена рядом с программой
    // и в текущей рабочей папке.
    public static string? FindRosterPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        string[] names = { "roster.csv", "roster.txt", "all_gr_4.txt", "all_gr.txt", "groups.txt" };
        string[] dirs = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

        foreach (string dir in dirs)
            foreach (string name in names)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}

/// <summary>Чтение текстового файла с автоопределением кодировки.</summary>
internal static class TextFiles
{
    public static string ReadAuto(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        // BOM UTF-8 (EF BB BF) — точно UTF-8.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

        // Пробуем строго как UTF-8; если попадётся недопустимый байт — это
        // не UTF-8, значит, считаем файл в Windows-1251 (как выгрузка с сайта).
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp1251.Decode(bytes);
        }
    }
}

/// <summary>Декодировщик однобайтовой кодировки Windows-1251 (без внешних пакетов).</summary>
internal static class Cp1251
{
    // Сопоставление байтов 0x80–0xBF с кодами Unicode.
    // Диапазон 0xC0–0xFF — обычные буквы А..я — считается линейно.
    private static readonly int[] High =
    {
        0x0402,0x0403,0x201A,0x0453,0x201E,0x2026,0x2020,0x2021, // 80–87
        0x20AC,0x2030,0x0409,0x2039,0x040A,0x040C,0x040B,0x040F, // 88–8F
        0x0452,0x2018,0x2019,0x201C,0x201D,0x2022,0x2013,0x2014, // 90–97
        0x0098,0x2122,0x0459,0x203A,0x045A,0x045C,0x045B,0x045F, // 98–9F
        0x00A0,0x040E,0x045E,0x0408,0x00A4,0x0490,0x00A6,0x00A7, // A0–A7
        0x0401,0x00A9,0x0404,0x00AB,0x00AC,0x00AD,0x00AE,0x0407, // A8–AF (0xA8 → Ё)
        0x00B0,0x00B1,0x0406,0x0456,0x0491,0x00B5,0x00B6,0x00B7, // B0–B7
        0x0451,0x2116,0x0454,0x00BB,0x0458,0x0405,0x0455,0x0457, // B8–BF (0xB8 → ё, 0xB9 → №)
    };

    public static string Decode(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            if (b < 0x80) sb.Append((char)b);                       // ASCII как есть
            else if (b >= 0xC0) sb.Append((char)(0x0410 + (b - 0xC0))); // А..я линейно
            else sb.Append((char)High[b - 0x80]);                   // прочее — по таблице
        }
        return sb.ToString();
    }
}

/// <summary>Формирование строк CSV с корректным экранированием.</summary>
internal static class Csv
{
    // Экранирует одно поле: если внутри есть разделитель, кавычка или перевод
    // строки — оборачиваем в кавычки, а внутренние кавычки удваиваем.
    public static string Field(string s)
    {
        s ??= "";
        bool needQuotes = s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        return needQuotes ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // Собирает строку CSV из ячеек через разделитель «;».
    public static string Row(params string[] cells) => string.Join(";", cells.Select(Field));
}

/// <summary>Интерактивный журнал: оценки и посещаемость с сохранением в CSV.</summary>
internal sealed class Journal
{
    // Заголовок CSV-файла.
    public const string Header =
        "Дата;Группа;Зачетка;ФИО;Направление;Предмет;Оценка;Отсутствовал";

    // Сценарий «по номеру группы»: находит и загружает реестр, ведёт журнал.
    public static int RunForGroup(CliOptions opt, string group, string? subject, DateTime date)
    {
        RosterStore? roster = LoadRoster(opt.Roster);
        if (roster is null) return 1;
        return Run(roster, group, subject, date, opt.Out);
    }

    // Загрузка реестра с понятными сообщениями об ошибках. null — если не вышло.
    public static RosterStore? LoadRoster(string? rosterOption)
    {
        string? path = RosterStore.FindRosterPath(rosterOption);
        if (path is null)
        {
            Console.Error.WriteLine("Файл со списками групп не найден.");
            Console.Error.WriteLine("Укажите его через --roster=ПУТЬ или положите рядом с программой " +
                                    "под именем roster.csv / all_gr_4.txt.");
            return null;
        }

        try
        {
            var roster = RosterStore.Load(path);
            Console.Error.WriteLine($"Список групп: {path} (групп: {roster.GroupCount}, студентов: {roster.StudentCount}).");
            return roster;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось прочитать список групп «{path}»: {ex.Message}");
            return null;
        }
    }

    // Интерактивный проход по студентам группы и сохранение результата.
    public static int Run(RosterStore roster, string group, string? subject, DateTime date, string? outPath)
    {
        var students = roster.Group(group);
        if (students.Count == 0)
        {
            Console.Error.WriteLine($"В списке нет группы «{group}».");
            return 1;
        }

        // Предмет: из расписания/аргумента, либо спрашиваем.
        string subj = subject ?? Ask("Предмет (можно оставить пустым): ");

        Console.WriteLine();
        Console.WriteLine($"Журнал группы {group}" + (subj.Length > 0 ? $" — {subj}" : "") +
                          $"   ({date:dd.MM.yyyy})");
        Console.WriteLine($"Студентов: {students.Count}.");
        Console.WriteLine("Ввод: оценка 2–5 · «н» — отсутствует · Enter — пропустить · «q» — закончить.");
        Console.WriteLine();

        var marks = new List<(Student S, string Grade, bool Absent)>();
        bool stop = false;

        for (int i = 0; i < students.Count && !stop; i++)
        {
            Student s = students[i];
            string label = $"{i + 1,3}. {s.Name}" + (s.Track.Length > 0 ? $" [{s.Track}]" : "");

            // Цикл повтора, пока не получим корректный ввод для этого студента.
            while (true)
            {
                Console.Write($"{label}: ");
                string input = (Console.ReadLine() ?? "").Trim();

                if (input.Length == 0) { marks.Add((s, "", false)); break; }   // пропуск
                if (IsQuit(input)) { stop = true; break; }                      // закончить
                if (IsAbsent(input)) { marks.Add((s, "", true)); break; }       // отсутствует
                if (IsGrade(input)) { marks.Add((s, input, false)); break; }    // оценка

                Console.WriteLine("   ? Введите 2–5, «н», Enter или «q».");
            }
        }

        // Имя файла по умолчанию: journal_<группа>_<дата>.csv
        string file = outPath ?? $"journal_{Safe(group)}_{date:yyyy-MM-dd}.csv";
        try
        {
            Save(file, group, subj, date, marks);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось сохранить «{file}»: {ex.Message}");
            return 1;
        }

        int graded = marks.Count(m => m.Grade.Length > 0);
        int absent = marks.Count(m => m.Absent);
        Console.WriteLine();
        Console.WriteLine($"Сохранено: {file}");
        Console.WriteLine($"Оценок: {graded}, отсутствовали: {absent}, записей: {marks.Count}.");
        return 0;
    }

    // Запись в CSV. Если файл уже есть — дописываем строки (журнал накапливается
    // по датам); новый файл создаём с BOM и строкой заголовка.
    private static void Save(string file, string group, string subject, DateTime date,
                             List<(Student S, string Grade, bool Absent)> marks)
    {
        bool exists = File.Exists(file) && new FileInfo(file).Length > 0;

        using var stream = new FileStream(file, FileMode.Append, FileAccess.Write);
        // BOM (encoderShouldEmitUTF8Identifier) добавляем только для нового файла —
        // тогда Excel откроет кириллицу корректно.
        using var writer = new StreamWriter(stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: !exists));

        if (!exists)
            writer.WriteLine(Header);

        foreach (var (s, grade, absent) in marks)
            writer.WriteLine(Csv.Row(
                date.ToString("dd.MM.yyyy"),
                group,
                s.RecordId,
                s.Name,
                s.Track,
                subject,
                grade,
                absent ? "да" : ""));
    }

    // «q» (в т.ч. в русской раскладке — «й») — закончить ввод.
    private static bool IsQuit(string s) => s is "q" or "Q" or "й" or "Й";

    // Метки отсутствия: разные удобные варианты.
    private static bool IsAbsent(string s) =>
        s is "н" or "Н" or "n" or "N" or "a" or "A" or "отс" or "-";

    // Оценка — одна цифра от 2 до 5.
    private static bool IsGrade(string s) => s.Length == 1 && s[0] >= '2' && s[0] <= '5';

    // Делает имя файла безопасным: только буквы/цифры, остальное — «_».
    private static string Safe(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private static string Ask(string prompt)
    {
        Console.Write(prompt);
        return (Console.ReadLine() ?? "").Trim();
    }
}
