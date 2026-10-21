using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hlas;

internal sealed record VoiceModel(string Id, string[] Voices) { public override string ToString() => Id; }
internal sealed record Catalog(VoiceModel[] Speech, string[] Text)
{
    internal static Catalog Bundled()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hlas.Catalog")!;
        return JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
internal sealed record Bill(string Kind, string Model, string? Id)
{
    public string? Id { get; set; } = Id;
    public Guid RecordId { get; init; } = Guid.NewGuid();
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public int Characters { get; init; }
    public decimal? Cost { get; set; }
    public string Provider { get; set; } = "OpenRouter";
    public string Status { get; set; } = "Hotovo";
}
internal sealed record AudioData(byte[] Bytes, string Extension, Bill Bill);
internal interface IApi
{
    Task<string> Prepare(string input, Settings settings, string key, Action<Bill> billed, CancellationToken token);
    Task<AudioData> Speak(string input, Settings settings, string key, CancellationToken token);
}
internal sealed class Api : IApi, IDisposable
{
    readonly HttpClient http;
    readonly UsageStore? usage;
    internal Api(HttpMessageHandler? handler = null, UsageStore? usage = null) { this.usage = usage; http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 80 * 1024 * 1024 }; }
    internal static decimal? DecimalCost(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out decimal n) && n >= 0) return n;
        if (element.ValueKind == JsonValueKind.String && decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n) && n >= 0) return n;
        return null;
    }
    async Task<HttpResponseMessage> Post(string endpoint, object body, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/" + endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("X-Title", "Hlas Clipboard");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, token);
        if (response.IsSuccessStatusCode) return response;
        using (response) {
            string message = response.StatusCode switch { HttpStatusCode.Unauthorized => "Neplatný OpenRouter kľúč.", HttpStatusCode.PaymentRequired => "Nedostatok kreditu v OpenRouteri.", HttpStatusCode.TooManyRequests => "Služba je dočasne preťažená alebo bol prekročený limit.", _ => "OpenRouter odmietol požiadavku." };
            try { using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) message += " " + m.GetString();
            } catch (JsonException) { }
            if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[skryté]");
            throw new InvalidOperationException($"{message[..Math.Min(message.Length, 500)]} (HTTP {(int)response.StatusCode})");
        }
    }
    public async Task<string> Prepare(string input, Settings s, string key, Action<Bill> billed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bill = usage?.Start("Text", s.TextModel, input.Length) ?? new Bill("Text", s.TextModel, null);
        try {
        using var response = await Post("chat/completions", new { model = s.TextModel, stream = false, max_tokens = 8000,
            messages = new[] { new { role = "system", content = PreparationPrompt.Prompt }, new { role = "user", content = input } } }, key, token);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)); var root = doc.RootElement;
        bill.Id = root.TryGetProperty("id", out var id) ? id.GetString() : null;
        if (root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("cost", out var cost)) bill.Cost = DecimalCost(cost);
        billed(bill);
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) throw new InvalidOperationException("Model nevrátil upravený text.");
        var choice = choices[0];
        if (!choice.TryGetProperty("finish_reason", out var reason) || reason.GetString() != "stop") throw new InvalidOperationException("Úprava textu nie je úplná. Čítanie bolo zastavené.");
        var content = choice.GetProperty("message").GetProperty("content");
        string text = content.ValueKind == JsonValueKind.String ? content.GetString()!.Trim() : "";
        if (text.Length == 0) throw new InvalidOperationException("Model vrátil prázdny text.");
        bill.Status = "Hotovo"; return text;
        } catch (OperationCanceledException) { bill.Status = "Zrušené / časový limit"; throw; }
        catch { bill.Status = "Chyba"; throw; }
        finally { usage?.Update(bill); }
    }
    public async Task<AudioData> Speak(string input, Settings s, string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (s.InternalSpeech) return await SpeakInternal(input, s.InternalLanguage, token);
        var bill = usage?.Start("Audio", s.SpeechModel, input.Length) ?? new Bill("Audio", s.SpeechModel, null);
        try {
        bool pcm = s.SpeechModel.StartsWith("google/gemini-", StringComparison.Ordinal);
        var body = new Dictionary<string, object> { ["model"] = s.SpeechModel, ["input"] = input, ["voice"] = s.Voice, ["response_format"] = pcm ? "pcm" : "mp3" };
        if (pcm || s.SpeechModel.StartsWith("openai/") || s.SpeechModel.StartsWith("fish-audio/"))
            body["instructions"] = "Read the text verbatim in its original language, especially Slovak or English. Use a calm, clear, consistent narration. Do not translate or add commentary.";
        using var response = await Post("audio/speech", body, key, token);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        string? id = response.Headers.TryGetValues("X-Generation-Id", out var ids) ? ids.FirstOrDefault() : null;
        bill.Id = id;
        if (pcm) {
            var type = response.Content.Headers.ContentType;
            if (type?.MediaType != "audio/pcm" || !int.TryParse(type.Parameters.FirstOrDefault(x => x.Name == "rate")?.Value?.Trim('"'), out int rate) || !short.TryParse(type.Parameters.FirstOrDefault(x => x.Name == "channels")?.Value?.Trim('"'), out short channels))
                throw new InvalidOperationException("Server nevrátil PCM s údajmi o frekvencii a kanáloch.");
            bytes = WrapPcm(bytes, rate, channels);
        } else if (bytes.Length < 4 || !((bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3') || (bytes[0] == 255 && (bytes[1] & 224) == 224)))
            throw new InvalidOperationException("Server nevrátil rozpoznateľné MP3 audio.");
        bill.Status = "Hotovo"; return new(bytes, pcm ? "wav" : "mp3", bill);
        } catch (OperationCanceledException) { bill.Status = "Zrušené / časový limit"; throw; }
        catch { bill.Status = "Chyba"; throw; }
        finally { usage?.Update(bill); }
    }
    async Task<AudioData> SpeakInternal(string input, string language, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 5000) throw new InvalidOperationException("Interný model: text musí obsahovať 1 až 5000 znakov.");
        if (language is not ("sk" or "en")) throw new InvalidOperationException("Interný model podporuje slovenčinu a angličtinu.");
        var bill = usage?.Start("Audio", "internal/omnivoice", input.Length, "Interný model") ?? new Bill("Audio", "internal/omnivoice", null);
        bill.Provider = "Interný model";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        try {
            var baseUri = new Uri("https://ai.vaskop.me/transcribe/");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "?mode=speech"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("X-Requested-With", "fetch");
            var form = new MultipartFormDataContent(); request.Content = form;
            form.Add(new StringContent("speech"), "action");
            form.Add(new StringContent(input, Encoding.UTF8), "text");
            form.Add(new StringContent(language), "language");
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Interný model: požiadavka zlyhala (HTTP {(int)response.StatusCode}).");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("Interný model oznámil zlyhanie prevodu.");
            if (!root.TryGetProperty("audio", out var audio) || audio.ValueKind != JsonValueKind.Object || !audio.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(url.GetString()))
                throw new InvalidOperationException("Interný model nevrátil adresu audia.");
            if (!Uri.TryCreate(baseUri, url.GetString(), out var audioUri) || audioUri.Scheme != baseUri.Scheme || audioUri.Host != baseUri.Host || audioUri.Port != baseUri.Port || audioUri.UserInfo.Length != 0)
                throw new InvalidOperationException("Interný model vrátil nepovolenú adresu audia.");
            using var download = await http.GetAsync(audioUri, timeout.Token);
            if (!download.IsSuccessStatusCode) throw new InvalidOperationException($"Interný model: stiahnutie audia zlyhalo (HTTP {(int)download.StatusCode}).");
            byte[] bytes = await download.Content.ReadAsByteArrayAsync(timeout.Token);
            if (bytes.Length < 4 || !((bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3') || (bytes[0] == 255 && (bytes[1] & 224) == 224)))
                throw new InvalidOperationException("Interný model nevrátil rozpoznateľné MP3 audio.");
            bill.Status = "Hotovo"; return new(bytes, "mp3", bill);
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            bill.Status = "Časový limit"; throw new InvalidOperationException("Interný model neodpovedal do 180 sekúnd. Na OpenRouter neprepínam.");
        } catch (OperationCanceledException) { bill.Status = "Zrušené"; throw; }
        catch (JsonException ex) { bill.Status = "Chyba"; throw new InvalidOperationException("Interný model vrátil neplatný JSON.", ex); }
        catch (HttpRequestException ex) { bill.Status = "Chyba"; throw new InvalidOperationException("Interný model nie je dostupný alebo zlyhalo spojenie.", ex); }
        catch { bill.Status = "Chyba"; throw; }
        finally { usage?.Update(bill); }
    }
    internal static byte[] WrapPcm(byte[] pcm, int rate, short channels)
    {
        if (rate is < 8000 or > 192000 || channels is < 1 or > 2 || pcm.Length == 0 || pcm.Length % (2 * channels) != 0) throw new InvalidDataException("Neplatné PCM audio.");
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write(channels); writer.Write(rate); writer.Write(rate * channels * 2);
        writer.Write((short)(channels * 2)); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }
    internal async Task<decimal?> Cost(string id, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/generation?id=" + Uri.EscapeDataString(id));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("total_cost", out var c) ? DecimalCost(c) : null;
    }
    internal async Task<Catalog> GetCatalog(CancellationToken token)
    {
        async Task<JsonDocument> Get(string kind) { using var r = await http.GetAsync("https://openrouter.ai/api/v1/models?output_modalities=" + kind, token); r.EnsureSuccessStatusCode(); return JsonDocument.Parse(await r.Content.ReadAsStringAsync(token)); }
        using var speech = await Get("speech"); using var text = await Get("text");
        return new(speech.RootElement.GetProperty("data").EnumerateArray().Select(m => new VoiceModel(m.GetProperty("id").GetString()!, m.TryGetProperty("supported_voices", out var voices) && voices.ValueKind == JsonValueKind.Array ? voices.EnumerateArray().Select(v => v.GetString()!).ToArray() : Array.Empty<string>())).OrderBy(m => m.Id).ToArray(),
            text.RootElement.GetProperty("data").EnumerateArray().Where(m => m.GetProperty("architecture").GetProperty("input_modalities").EnumerateArray().Any(v => v.GetString() == "text")).Select(m => m.GetProperty("id").GetString()!).Where(id => !id.EndsWith(":batch")).Order().ToArray());
    }
    public void Dispose() => http.Dispose();
}
