# Arquitectura

Índice, no duplicado. La fuente de verdad es
[`IA27-BITACORA-MAESTRA-Y-REGLAS.txt`](IA27-BITACORA-MAESTRA-Y-REGLAS.txt); acá
van los enlaces a las PARTes que la explican.

## Archivos

| Archivo | Qué hace | PARTE |
| --- | --- | --- |
| `ECnet.csproj` | Proyecto .NET 8, win-x64 self-contained | [P2bis](#) |
| `Program.cs` | Punto de entrada, tema de consola, icono de la ventana | [P18](#) |
| `Banner.cs` | Banner de arranque con Spectre.Console | [P23](#) |
| `AppConfig.cs` | `config.json` persistente y resolución de modelos portable | [P17](#) |
| `ECnetServerSession.cs` | Lanza `llama-server.exe`, streaming SSE, políticas del prompt | [P2](#) |
| `TerminalApplication.cs` | El loop del agente: comandos, búsqueda, herramientas, harness | [P3](#) |
| `ModelRegistry.cs` / `ModelCatalog.cs` / `GgufMetadataReader.cs` | Catálogo y metadatos de GGUF | [P17](#) |
| `ChildProcessJob.cs` | Job Object: mata los `llama-server` huérfanos | [P19](#) |
| `NotificationHelper.cs` | Notificaciones de escritorio de fin de turno | [P31](#) |

## El loop en una línea

`Console.ReadLine()` → `ECnetServerSession` pide streaming a `llama-server` →
los tokens se escriben con `Console.Write` **puro** (nunca `Markup`) →
al final del stream se busca un marcador `[[XXX]]` → si hay, se ejecuta la
herramienta con permiso → el resultado vuelve como `[SISTEMA · ...]` → se
pregunta si sigue.

Ese último paso es el que define al proyecto: **el humano está en el medio** de
cada acción sensible.

## Tres decisiones que no se revierten

**El streaming nunca pasa por `Markup`.** El contenido del modelo contiene
`[SISTEMA · ...]`, que Spectre.Console interpretaría como tag y se tragaría
texto. Todo contenido de modelo o usuario dentro de un `Markup` va con
`Markup.Escape`. Ver [P23](#).

**No hay sandbox.** `[[CMD]]` es PowerShell completo, sin allowlist. La
seguridad es la confirmación humana, no el aislamiento. Ver
[seguridad.md](seguridad.md) y el pendiente del allowlist.

**El portable se republica siempre.** `dotnet build` no alcanza: el entregable
real es `publish/portable.exe`. Ver [AGENTS.md](../AGENTS.md).

## Lo que este proyecto NO es

No es un clon de opencode. Se tomó de opencode la **capa de gobernanza del
repo** —`LICENSE`, `CONTRIBUTING`, `SECURITY`, `CHANGELOG`, `.github/`,
`.editorconfig`, `docs/`— porque es lo que aporta valor. No su stack: opencode
es un monorepo TypeScript/Bun, esto es C#/.NET de un solo dueño, y copiar
`packages/` + `turbo.json` + `bun.lock` sería teatro.

---

Índices: [herramientas](herramientas.md) · [seguridad](seguridad.md) · [entregas](entregas.md)