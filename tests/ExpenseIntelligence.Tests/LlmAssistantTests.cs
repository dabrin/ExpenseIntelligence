using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ExpenseIntelligence.Application;
using ExpenseIntelligence.Infrastructure.Assistant;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExpenseIntelligence.Tests;

public class LlmAssistantTests
{
    /// <summary>Scripted OpenAI: replays the given responses in order and records every request body.</summary>
    private sealed class Fake(params (HttpStatusCode, string)[] replies) : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        private int _i;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Requests.Add(await r.Content!.ReadAsStringAsync(ct));
            var (code, body) = replies[Math.Min(_i++, replies.Length - 1)];
            return new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Text(string content) => new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content } }) }.ToJsonString();
    private static string ToolCall(string name, string args) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject
        {
            ["role"] = "assistant", ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } }),
        } }),
    }.ToJsonString();

    private static LlmAssistant Build(Sandbox sb, string? key, Fake? fake, out ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore st)
    {
        st = sb.OpenStore();
        var an = new AnalyticsService(st);
        return new LlmAssistant(new AssistantService(st, an, Sandbox.Clock), new ExpenseQueryService(st), an, new InsightsService(st), st,
            new LlmOptions(key, "gpt-test"), new HttpClient(fake ?? new Fake((HttpStatusCode.OK, Text("x")))), Sandbox.Clock, NullLogger<LlmAssistant>.Instance);
    }

    [Fact]
    public async Task Without_an_api_key_the_rules_assistant_answers_and_nothing_is_sent()
    {
        using var sb = new Sandbox(); var fake = new Fake(); var a = Build(sb, null, fake, out var st); using var _ = st;
        var r = await a.AskAsync("¿Cuál es el total gastado por moneda?", null, null, null);
        Assert.Equal("rules", r.Mode);
        Assert.Empty(fake.Requests);
        Assert.False(a.Enabled);
    }

    [Fact]
    public async Task Answers_come_from_tool_results_computed_on_live_data()
    {
        using var sb = new Sandbox(); var fake = new Fake((HttpStatusCode.OK, ToolCall("get_summary", """{"monedas":["USD"]}""")), (HttpStatusCode.OK, Text("Total en USD: 123.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("¿Cuánto se gastó en USD?", null, null, null);
        Assert.Equal("llm", r.Mode);
        Assert.Equal("Total en USD: 123.", r.Answer);
        Assert.Contains("Moneda: USD", r.Scope);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Contains("CONTEXTO ACTUAL: hoy es 2026-10-30", fake.Requests[0]);       // the model is told today's date and the real data range, so it cannot assume a year
        Assert.Contains("totalesPorMoneda", fake.Requests[1]);          // the second call carries the real aggregates
        Assert.DoesNotContain("Gasto operativo", fake.Requests[1]);     // and never the raw records for a summary
    }

    [Fact]
    public async Task The_model_chooses_the_chart_but_the_server_computes_every_number()
    {
        using var sb = new Sandbox();
        var fake = new Fake((HttpStatusCode.OK, ToolCall("show_visual", """{"kind":"bar","metric":"departamento","moneda":"USD","top":5}""")), (HttpStatusCode.OK, Text("Aquí tienes el ranking.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("muéstrame un gráfico de gasto por departamento en USD", null, null, null);
        var v = Assert.Single(r.Visuals!);
        Assert.Equal("bar", v.Kind);
        Assert.Equal("USD", v.Currency);
        Assert.NotEmpty(v.Labels);
        Assert.Equal(v.Labels.Length, v.Series[0].Values.Length);
        var expected = analyticsTotalByDepartment(st, "USD");
        Assert.Equal(expected.OrderByDescending(p => p.Value).First().Key, v.Labels[0]);   // top of the ranking comes from the real data
        Assert.Equal((double)Math.Round(expected.Max(p => p.Value), 2), v.Series[0].Values[0]);
    }

    private static Dictionary<string, decimal> analyticsTotalByDepartment(ExpenseIntelligence.Infrastructure.Storage.JsonExpenseStore st, string cur) =>
        new AnalyticsService(st).Summary(ExpenseFilter.None).ByDepartment.Where(d => d.Moneda == cur).ToDictionary(d => d.Key, d => d.Total);

    [Fact]
    public async Task A_chart_with_no_comment_from_the_model_gets_a_short_caption_instead_of_falling_back()
    {
        using var sb = new Sandbox();
        var fake = new Fake((HttpStatusCode.OK, ToolCall("show_visual", """{"kind":"line","metric":"mes","moneda":"USD","title":"Evolución mensual (USD)"}""")), (HttpStatusCode.OK, Text("")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("evolución mensual en USD", null, null, null);
        Assert.Equal("llm", r.Mode);
        Assert.Equal("Aquí tienes Evolución mensual (USD).", r.Answer);                  // the currency in the title is not repeated
        Assert.Equal("line", r.Visuals![0].Kind);
    }

    [Fact]
    public async Task Tables_typed_by_the_model_are_removed_and_the_server_table_is_what_is_shown()
    {
        using var sb = new Sandbox();
        var fake = new Fake((HttpStatusCode.OK, ToolCall("show_visual", """{"kind":"table","metric":"gastos","sort":"monto","top":3}""")),
            (HttpStatusCode.OK, Text("Aquí tienes los más grandes.\n| ID | Monto |\n|---|---|\n| 99999 | 1.00 |\nLidera el primero.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("tabla con los 3 gastos más grandes", null, null, null);
        Assert.DoesNotContain("|", r.Answer);
        Assert.DoesNotContain("99999", r.Answer);
        Assert.Equal("table", r.Visuals![0].Kind);
        Assert.Equal(3, r.Visuals[0].Rows.Count);
        Assert.Contains("\"gastos\"", fake.Requests[1]);                                  // the model receives the real rows, so it has nothing to invent
    }

    [Fact]
    public async Task Money_charts_without_a_currency_are_refused_so_currencies_are_never_mixed()
    {
        using var sb = new Sandbox();
        var fake = new Fake((HttpStatusCode.OK, ToolCall("show_visual", """{"kind":"bar","metric":"categoria"}""")), (HttpStatusCode.OK, Text("¿En qué moneda lo quieres?")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("gráfico por categoría", null, null, null);
        Assert.Null(r.Visuals);
        Assert.Contains("Indica la moneda", fake.Requests[1]);
    }

    [Fact]
    public async Task Off_topic_questions_get_the_fixed_refusal()
    {
        using var sb = new Sandbox(); var fake = new Fake((HttpStatusCode.OK, Text(LlmAssistant.OffTopicToken)));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("¿Quién ganó el mundial?", null, null, null);
        Assert.Equal("off-topic", r.Intent);
        Assert.Equal(LlmAssistant.OffTopicAnswer, r.Answer);
    }

    [Fact]
    public async Task The_model_can_word_the_refusal_and_chat_about_itself_but_never_with_figures()
    {
        using var sb = new Sandbox(); var fake = new Fake(
            (HttpStatusCode.OK, Text("FUERA_DE_ALCANCE: Eso se sale de mi terreno, yo solo conozco los gastos de este sistema. ¿Te muestro los pendientes?")),
            (HttpStatusCode.OK, Text("CONVERSACION: Puedo responder con texto aquí; los gráficos están en el Dashboard.")),
            (HttpStatusCode.OK, Text("CONVERSACION: Llevamos 12345 gastos.")),
            (HttpStatusCode.OK, Text("CONVERSACION: Supuse 2023 porque no indicaste el año, fue un error mío.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var off = await a.AskAsync("¿Quién ganó el mundial?", null, null, null);
        Assert.Equal("off-topic", off.Intent);
        Assert.StartsWith("Eso se sale de mi terreno", off.Answer);
        var chat = await a.AskAsync("¿puedes mostrarme gráficos aquí?", null, null, null);
        Assert.Equal("chat", chat.Intent);
        Assert.Contains("Dashboard", chat.Answer);
        Assert.Equal(LlmAssistant.OffTopicAnswer, (await a.AskAsync("¿cuántos gastos hay?", null, null, null)).Answer);   // untagged figures without tools are refused
        Assert.Equal("chat", (await a.AskAsync("¿por qué asumes 2023?", null, null, null)).Intent);                       // a year inside an explanation is fine
    }

    [Fact]
    public async Task A_short_follow_up_answered_without_tools_is_sent_back_once_and_then_uses_the_data()
    {
        using var sb = new Sandbox(); var fake = new Fake(
            (HttpStatusCode.OK, Text("No hay datos de ese año.")),
            (HttpStatusCode.OK, ToolCall("get_summary", """{"monedas":["EUR"]}""")),
            (HttpStatusCode.OK, Text("En EUR el total es 1.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("¿y en EUR?", null, null, null);
        Assert.Equal("llm", r.Intent);
        Assert.Equal("En EUR el total es 1.", r.Answer);
        Assert.Contains("Tu respuesta anterior", fake.Requests[1]);                      // the model was told what was wrong
    }

    [Fact]
    public async Task A_reply_that_never_consulted_the_data_is_not_trusted()
    {
        using var sb = new Sandbox(); var fake = new Fake((HttpStatusCode.OK, Text("Claro, el dólar suele subir en diciembre.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("¿Qué opinas del dólar?", null, null, null);
        Assert.Equal(LlmAssistant.OffTopicAnswer, r.Answer);
    }

    [Fact]
    public async Task Provider_failure_falls_back_to_rules_even_for_small_talk()
    {
        using var sb = new Sandbox(); var fake = new Fake((HttpStatusCode.InternalServerError, "{}"));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        Assert.Equal("rules", (await a.AskAsync("¿Cuál es el total gastado por moneda?", null, null, null)).Mode);
        Assert.Equal("greeting", (await a.AskAsync("hola", null, null, null)).Intent);
    }

    [Fact]
    public async Task Markdown_emphasis_is_removed_from_data_answers()
    {
        using var sb = new Sandbox(); var fake = new Fake((HttpStatusCode.OK, ToolCall("get_facets", "{}")), (HttpStatusCode.OK, Text("Hay **4** departamentos.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        Assert.Equal("Hay 4 departamentos.", (await a.AskAsync("¿Qué departamentos hay?", null, null, null)).Answer);
    }

    [Fact]
    public async Task Budget_per_window_is_enforced_then_rules_take_over()
    {
        using var sb = new Sandbox(); var fake = new Fake((HttpStatusCode.OK, ToolCall("get_facets", "{}")), (HttpStatusCode.OK, Text("ok")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        for (var i = 0; i < 30; i++) Assert.Equal("llm", (await a.AskAsync("¿Qué departamentos hay?", null, null, null)).Mode);
        Assert.Equal("rules", (await a.AskAsync("¿Cuál es el total gastado por moneda?", null, null, null)).Mode);
    }

    [Fact]
    public async Task Opening_a_screen_returns_a_whitelisted_route_with_filters_and_the_real_match_count()
    {
        using var sb = new Sandbox();
        var fake = new Fake((HttpStatusCode.OK, ToolCall("open_page", """{"page":"gastos","estado":"pendiente","monedas":["USD"],"open_now":true}""")), (HttpStatusCode.OK, Text("Te llevo a Gastos.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        var r = await a.AskAsync("llévame a los gastos pendientes en USD", null, null, null, default, "/dashboard");
        var act = Assert.Single(r.Actions!);
        Assert.Equal(("/gastos", true), (act.Path, act.Auto));
        Assert.Equal("pendiente", act.Filters!.Estado);
        Assert.Equal(["USD"], act.Filters.Monedas);
        Assert.Contains("coinciden", fake.Requests[1]);                       // the model is told how many expenses match, from the server
        Assert.Contains("la pantalla Dashboard", fake.Requests[0]);    // and where the user is now
    }

    [Fact]
    public async Task Unknown_screens_are_refused_and_screens_without_filters_drop_them()
    {
        using var sb = new Sandbox();
        var fake = new Fake((HttpStatusCode.OK, ToolCall("open_page", """{"page":"https://evil.example"}""")), (HttpStatusCode.OK, Text("No encontré esa pantalla.")),
            (HttpStatusCode.OK, ToolCall("open_page", """{"page":"archivos","estado":"pendiente"}""")), (HttpStatusCode.OK, Text("Aquí está.")));
        var a = Build(sb, "k", fake, out var st); using var _ = st;
        Assert.Null((await a.AskAsync("abre evil", null, null, null)).Actions);
        Assert.Contains("Pantalla desconocida", fake.Requests[1]);
        var r = await a.AskAsync("ve a archivos", null, null, null);
        var act = Assert.Single(r.Actions!);
        Assert.Equal("/archivos", act.Path);
        Assert.Null(act.Filters);                                              // Archivos does not read the shared filters
        Assert.False(act.Auto);                                                // not explicitly requested: offered, not forced
    }
}
