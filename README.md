# XssFinInjector v2

Scanner de **XSS (Cross-Site Scripting)** em C# / .NET 6, com console interativo.
Usa **Playwright** (Chromium headless) para renderizar as páginas e confirmar a
execução dos payloads, com técnicas *stealth* para reduzir detecção por anti-bots
e roteamento opcional de tráfego pelo **Burp Suite**.

> ⚠️ **Uso autorizado apenas.** Esta ferramenta é destinada a testes de segurança
> em alvos que você **possui** ou para os quais tem **autorização explícita**
> (pentest com escopo, laboratório próprio, CTF, bug bounty dentro do escopo).
> Testar sistemas de terceiros sem permissão é ilegal.

## Recursos

- Biblioteca de payloads: básicos, bypass de case, event handlers alternativos,
  `javascript:` URI, URL-encoded, HTML entities, DOM/fragment e bypasses de WAF simples.
- **Scan server-side**: parâmetros GET, formulários e varredura completa.
- **Scan DOM XSS** via Playwright (execução real no navegador).
- **Scan de SPA**: inputs e parâmetros de rota (`#/rota?q=PAYLOAD`).
- **Auto-recon**: descoberta automática de alvos/parâmetros.
- **Payload manual** para testes pontuais.
- Integração com **XSS Hunter** (xss.report / xsshunter.com / servidor próprio)
  para callbacks de XSS cego (blind XSS).
- Modo *stealth*: user-agent de Chrome real, remoção de `navigator.webdriver`,
  `PluginArray` realista e args do Chromium para evitar `AutomationControlled`.
- Tráfego roteado pelo **Burp Suite** (`127.0.0.1:8080`) por padrão.
- Relatório de vulnerabilidades encontradas com exportação.

## Estrutura do projeto

```
.
├── README.md
├── .gitignore
└── src/
    └── XssFinInjector/
        ├── Program.cs
        └── XssFinInjector.csproj
```

## Requisitos

- [.NET SDK 6.0+](https://dotnet.microsoft.com/download)
- Navegadores do Playwright (instalados no primeiro uso, veja abaixo)
- (Opcional) **Burp Suite** rodando em `127.0.0.1:8080` — o tráfego é roteado
  para lá por padrão. Sem o Burp aberto, os scans HTTP podem falhar.

## Como rodar

```bash
cd src/XssFinInjector

# Restaurar dependências e compilar
dotnet build

# Instalar os navegadores do Playwright (primeira vez apenas)
pwsh bin/Debug/net6.0/playwright.ps1 install chromium

# Executar
dotnet run
```

## Menu interativo

```
  0.  auto-recon                   7.  scan completo (server+DOM)
  1.  definir URL alvo             8.  scan SPA inputs
  2.  configurar XSS Hunter        9.  scan SPA route param
  3.  scan GET params             10.  payload manual
  4.  scan formulários            11.  ver resultados
  5.  scan completo server-side   12.  exportar relatório
  6.  scan DOM XSS                13.  sair
```

Fluxo típico:

1. Opção **1** — definir a URL alvo (ex: `http://localhost:3000/rest/products/search?q=test`).
2. (Opcional) Opção **2** — configurar o XSS Hunter para blind XSS.
3. Opção **0** — auto-recon, ou escolha um scan específico (3–9).
4. Opção **11** — ver resultados / **12** — exportar relatório.

## Configuração

Alguns parâmetros ficam no topo de `src/XssFinInjector/Program.cs`:

- `BurpProxy` — endereço do proxy do Burp (padrão `http://127.0.0.1:8080`).
- `XssHunterUrl` — URL de callback do XSS Hunter (definível pela opção 2 do menu).
- `SpaRouteTemplate` — template de rota SPA para o scan de route param.
- `RealUserAgent` — user-agent usado no modo stealth.

## Licença

Sem licença definida. Adicione uma se for distribuir.
