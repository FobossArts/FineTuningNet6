using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using FineTuningNet6.Context;
using FineTuningNet6.Models;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static System.Net.WebRequestMethods;

class Program
{
    private const string ApiUrl = "http://185.129.51.11/Help/GetFoodNameABCatering";
    private const string InputDir = @"C:\Users\m.fedorchenko\Desktop\funetuningTESTS";

    // один HttpClient на всё приложение
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private static readonly string[] _allowedExt = { ".png", ".jpg", ".jpeg", ".bmp" };

    // простая мапа расширение → MIME
    private static readonly Dictionary<string, string> _mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".bmp"] = "image/bmp"
    };
    static async Task Main()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        var files = Directory.EnumerateFiles(InputDir)
                              .Where(f => _allowedExt.Contains(Path.GetExtension(f),
                                                               StringComparer.OrdinalIgnoreCase))
                              .OrderBy(Path.GetFileName)
                              .ToArray();

        await using var db = new AppDbContext();

        foreach (var file in files)
        {
            try
            {
                Console.WriteLine($"📤 {Path.GetFileName(file)}");

                // Открываем ОРИГИНАЛ без перекодирования
                await using var fs = System.IO.File.OpenRead(file);
                var ext = Path.GetExtension(file);
                var mime = _mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";

                using var form = new MultipartFormDataContent
                {
                    {
                        new StreamContent(fs)
                        {
                            Headers = { ContentType = new MediaTypeHeaderValue(mime) }
                        },
                        "file",
                        Path.GetFileName(file)
                    }
                };

                using var resp = await _http.PostAsync(ApiUrl, form);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"⚠️  {resp.StatusCode}: {body}");
                    continue;
                }

                var foodName = ExtractNamesFromApiResponse(body);

                db.GptImageResults.Add(new GptImageResult
                {
                    FileName = Path.GetFileName(file),
                    ApiResponse = foodName,
                    CreatedAt = DateTime.UtcNow
                });

                Console.WriteLine($"✅ Найдено: {foodName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {Path.GetFileName(file)} → {ex.Message}");
            }
        }

        await db.SaveChangesAsync();
        Console.WriteLine("💾 Все результаты сохранены.");
    }

    public static async Task<ByteArrayContent> PrepareCompressedImageContent(string imagePath)
    {
        using var image = await Image.LoadAsync<Rgba32>(imagePath);

        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(1024, 1024),
            Mode = ResizeMode.Max
        }));

        var ms = new MemoryStream();
        await image.SaveAsync(ms, new JpegEncoder { Quality = 75 });
        ms.Seek(0, SeekOrigin.Begin);

        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return content;
    }
    public static string ExtractNamesFromApiResponse(string json)
    {
        try
        {
            using var root = JsonDocument.Parse(json);

            // 1. success == true
            if (!root.RootElement.TryGetProperty("success", out var succ) || !succ.GetBoolean())
                return $"Ошибка: success = false. JSON: {json}";

            // 2. data как строка
            if (!root.RootElement.TryGetProperty("data", out var dataProp) ||
                dataProp.ValueKind != JsonValueKind.String)
                return $"Ошибка: отсутствует строковое поле data. JSON: {json}";

            var raw = dataProp.GetString() ?? string.Empty;
            if (raw.Length == 0) return "Пустой data.";

            // 3. Пытаемся выдернуть JSON-массив
            var jsonArray = TryExtractJsonArray(raw);
            if (jsonArray is null)
                return $"JSON-массив не найден в data. JSON: {json}";

            using var arrDoc = JsonDocument.Parse(jsonArray);

            if (arrDoc.RootElement.ValueKind != JsonValueKind.Array)
                return "Извлечённая строка не является JSON-массивом.";

            var names = arrDoc.RootElement
                              .EnumerateArray()
                              .Select(el => el.TryGetProperty("name", out var n) ? n.GetString() : null)
                              .Where(s => !string.IsNullOrWhiteSpace(s))
                              .ToArray();

            return names.Length > 0
                   ? string.Join(';', names)
                   : "Имена не найдены.";
        }
        catch (JsonException jex)
        {
            return $"Ошибка JSON-разбора: {jex.Message}. JSON: {json}";
        }
        catch (Exception ex)
        {
            return $"Неожиданная ошибка: {ex.Message}. JSON: {json}";
        }
    }

    /// <summary>
    /// Пытается найти первый корректно закрытый JSON-массив в произвольном тексте.
    /// Возвращает null, если не найден.
    /// </summary>
    private static string? TryExtractJsonArray(string text)
    {
        // Variant 1: ```json [...] ```
        var cb = Regex.Match(text, "```json\\s*(\\[.*?\\])\\s*```", RegexOptions.Singleline);
        if (cb.Success) return cb.Groups[1].Value.Trim();

        // Variant 2: любой первый сбалансированный [...]
        int start = text.IndexOf('[');
        if (start < 0) return null;

        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[': depth++; break;
                case ']':
                    depth--;
                    if (depth == 0)
                        return text.Substring(start, i - start + 1).Trim();
                    break;
                case '"':                     // пропускаем строковые литералы
                    i = SkipString(text, i);
                    break;
            }
        }
        return null; // не нашли закрывающей ]
    }

    private static int SkipString(string s, int i)
    {
        // i указывает на открывающую кавычку
        i++; // уходим за неё
        while (i < s.Length)
        {
            if (s[i] == '\\') { i += 2; continue; } // экранированный символ
            if (s[i] == '"') return i;              // закрывающая кавычка
            i++;
        }
        return s.Length - 1; // обошли строку, не нашли - пусть валится дальше
    }

    //public static string ExtractNameFromApiResponse(string json)
    //{
    //    try
    //    {
    //        using var rootDoc = JsonDocument.Parse(json);

    //        // Проверяем флаг success
    //        if (rootDoc.RootElement.TryGetProperty("success", out var successProp) &&
    //            successProp.ValueKind == JsonValueKind.True)
    //        {
    //            var rawData = rootDoc.RootElement.GetProperty("data").GetString();

    //            if (string.IsNullOrWhiteSpace(rawData))
    //                return "Пустой ответ";

    //            var cleaned = Regex.Replace(rawData, @"^```json|```$", "", RegexOptions.Multiline).Trim();

    //            using var innerDoc = JsonDocument.Parse(cleaned);

    //            // Проверка: пустой ли массив
    //            var rootArray = innerDoc.RootElement;
    //            if (rootArray.ValueKind != JsonValueKind.Array || !rootArray.EnumerateArray().Any())
    //            {
    //                return "Результат не найден"; // Вот тут возвращаем сообщение
    //            }

    //            var firstItem = rootArray.EnumerateArray().First();

    //            return firstItem.TryGetProperty("name", out var nameProp)
    //                ? nameProp.GetString() ?? "Без имени"
    //                : "Без имени";
    //        }
    //        else
    //        {
    //            return "Ошибка: success = true or false, ничего не найдено";
    //        }
    //    }
    //    catch (Exception ex)
    //    {
    //        return $"Ошибка разбора: {ex.Message}. JSON : {json}";
    //    }
    //}
}