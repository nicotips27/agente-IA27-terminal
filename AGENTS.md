# AGENTS.md — IA27 Terminal (Estalingrado Corp · Intra-net)

## Qué es

Terminal de agente IA 100 % local en C# / .NET 8 + `ECnet`, sin Python. Estética Cyberpunk (consola negra/cian/azul), español como idioma de interfaz, estilo de mensajes de sistema en español con corchetes (`[SISTEMA · ...]`, `[buscando en la web...]`).

## Las pestañas NO son de la consola

En la consola **no hay pestañas**: se borró la barra dibujada con `Console.Write`, el `GetTabAtPosition` por coordenadas, los clics de ratón por P/Invoke (`ConsoleInput`/`ConsoleMouse`) y los comandos `/tab` `/new` `/close`. No volver a meterlas: la consola lee con `Console.ReadLine()` y el prompt es `tú> `.

## REGLA OBLIGATORIA — siempre actualizar el portable

> **Después de CUALQUIER cambio en el código fuente, hay que compilar Y publicar:**
>
> ```
> dotnet build -c Release
> dotnet publish -c Release -o publish
> ```
>
> El entregable real es `publish/portable.exe` (self-contained, single-file).
> Un cambio de código que no llega al portable **no existe para el usuario**.
> Nunca terminar una tarea de código sin dejar el portable regenerado y verificado.

`publish/` y `bin/`/`obj/` están en `.gitignore` (el portable pesa ~67 MB): el portable se actualiza **localmente**, no se sube al repo.

## Compilación y pruebas

- Build: `dotnet build -c Release` — debe terminar con 0 errores.
- Publish: `dotnet publish -c Release -o publish`.
- No hay suite de tests; verificación manual: probar los regex nuevos con PowerShell (mismo motor .NET) y correr el agente contra el modelo.

## Icono del portable

- Fuente: `icono.ico` (multi-resolución 16/24/32/48/64/128/256 px, 32 bpp BGRA) en la raíz del proyecto.
- `ECnet.csproj` lo engancha con `<ApplicationIcon>icono.ico</ApplicationIcon>`: eso pone el icono en el `.exe` que muestra el Explorador.
- `Program.ApplyWindowIcon()` además pone el icono en la **ventana de consola** al arrancar, con `ExtractIconEx` + `WM_SETICON` (P/Invoke de `shell32`/`user32`). Lee el icono del propio ejecutable (`Environment.ProcessPath`), así que no depende de archivos sueltos ni de `System.Drawing.Common` (no disponible en .NET 8 sin paquete extra). Todo va envuelto en `try/catch`: si falla, la terminal arranca igual.
- Para regenerar el `.ico` desde un JPG hay que escribir las entradas **a mano** (DIB bottom-up + máscara AND). Ojo: la `BITMAPINFOHEADER` tiene 40 bytes y `biSize` va PRIMERO, en offset 0 — si se empieza escribiendo `biWidth` en offset 0 la cabecera queda corrida 4 bytes y `System.Drawing.Icon` / el Explorador rechazan el archivo sin error visible.
- Verificar que el icono realmente quedó: `ExtractAssociatedIcon` sobre `publish\portable.exe` y comparar píxeles contra el JPG original (la diferencia media por canal debe dar 0).

## Arquitectura

- `Program.cs` — punto de entrada, tema de consola.
- `Banner.cs` — banner de arranque con Spectre.Console (panel del logo, tabla de Estado, comandos reales — bitácora PARTE 23).
- `AppConfig.cs` — configuración persistente (`config.json`).
- `ECnetServerSession.cs` — lanza `llama-server.exe`, streaming SSE a `/v1/chat/completions`, **políticas del system prompt** (`NetPolicy`, `ToolsPolicy`).
- `TerminalApplication.cs` — loop del agente, comandos `/`, búsqueda web por intención (dónde → Nominatim/OpenStreetMap; clima → Open-Meteo; general → Wikipedia con extracto + DuckDuckGo con el texto de la página ganadora — ver bitácora PARTE 22), **herramientas de agente** (`[[READ]]`, `[[CMD]]`, `[[WRITE]]`), intercepción de negativas del modelo.

## Patrón de herramientas (extensión del agente)

El patrón probado es: marcador `[[XXX]]` emitido por el modelo (mensaje debe EMPEZAR con él, sin texto previo "copiable" en la política) → detección al final del stream → ejecución → resultado inyectado como mensaje `[SISTEMA · ...]` → `AskContinueAsync`. El **buffer anti-fugas** del streaming retiene desde el último `[`, así que ningún marcador llega a pantalla. Toda herramienta nueva debe seguir este patrón.

- **Probar las trampas con el modelo real, no con "compiló".** La compuerta de artefacto en el chat, la reparación de rutas con espacios y el rechazo de carpetas se escribieron siguiendo la lógica, y los 4 bugs que aparecieron (ruta truncada en el espacio, `[[END]]` en un `[[READ]]`, denegación contada como escritura, bloque cercado dentro del archivo) **solo salieron al correr la prueba de verdad**. Usar una copia en `pruebas-tool\`, revisar que la carpeta quede sin archivos raros, y borrarla después. La prueba que se usó: el prompt "mejora el index que está aquí `<carpeta>` y hacé que se parezca a google" sobre un `index.html` de prueba, verificando **por hash** que el archivo cambió y que no quedó basura en el Escritorio.

## Modo harness (`/harness <objetivo>`)

Bucle agéntico en `RunHarnessAsync` (TerminalApplication.cs): inyecta un mensaje `[SISTEMA · MODO HARNESS ACTIVADO]` con el objetivo → en cada paso el modelo hace UNA sola cosa: plan breve, UNA herramienta, o `[[DONE]]` + resumen. Límite: `HarnessMaxSteps` (15); límite alcanzado → cierre con aviso. Marcador malformado (p. ej. `[[WRITE]]` sin `[[END]]`) → no se ejecuta nada y se fuerza reintento del paso. Los permisos de `[[CMD]]`/`[[WRITE]]` se mantienen siempre.

Reglas de seguridad vigentes:

- `[[CMD]]` y `[[WRITE]]` **siempre** piden confirmación (píldoras en consola interactiva, `s/n` cuando la stdin o la stdout está pipeada), nunca autorización automática de sesión.
- Patrones destructivos en comandos → advertencia roja adicional.
- `[[READ]]` libre solo dentro del área de trabajo; fuera pide permiso.
- Máximo 5 herramientas encadenadas por turno, 2 rondas de `[[NET]]`.

## Problemas conocidos (no regresar)

- `--cache-type q4_0` corrompe tokens en algunos GGUF → usar `f16`/`f16`.
- Plantilla de chat forzada a `chatml` (el Jinja embebido de algunos GGUF falla).
- Si un modelo 7B "parotea" el texto de una política del system prompt, reescribir la política sin frases copiables (el buffer anti-fugas cubre el resto).
- Un 7B **alucina acciones**: afirma "he creado el archivo X" sin emitir `[[WRITE]]`. Defensas en `TerminalApplication.cs`: `[[WRITE]]` exige cierre `[[END]]` (sin él no se crea nada), `WriteClaimPattern`/`CmdClaimPattern` interceptan afirmaciones sin herramienta y fuerzan reintento (máx. 2), y la intención explícita ("crea un txt/texto/tecto...") acepta ruta en cualquier posición, typos y referencias al contexto ("en esa carpeta", "con toda esta data") usando `lastToolPath`. No quitar estas trampas.
- Un 7B tiene **DOS formas de no actuar** y hay que atrapar las dos: (a) **mentir** sobre la acción ("he creado el archivo") → `WriteClaimPattern`; (b) **negarse a actuar y enseñar el código** ("abrí el archivo y reemplazá el contenido" + el bloque completo en el chat) → la compuerta `LooksLikeArtifactInChat`. La (b) exige **las dos** condiciones juntas — bloque cercado con contenido de archivo entero (`FileArtifactPattern`) **y** indicación de hacerlo a mano (`ManualInstructionPattern`) — para no interceptar una explicación normal de código. Cuando dispara, **no imprime**: descarta y fuerza `[[WRITE]]` reutilizando el borrador del modelo (`ExtractDraftFromChat`), que si no costaría el doble de tokens.
- `ExplicitModifyIntent` cubre el pedido "mejora/actualizá/modificá/cambiá **un archivo que ya existe**", que `ExplicitWriteIntent` no ve (exige el verbo "crear"). Pide verbo de acción **más ruta real**, y excluye negaciones con lookbehind (`(?<!no\s)`, `(?<!sin\s)`, …) para no pedir una escritura prohibida. `DelegationDemandPattern` cubre el caso sin ruta: "tenés las herramientas hacelo vos".
- **Las rutas se truncan en los espacios** y esto NO es hypothetical: la carpeta del proyecto es "IA 27 T". Los patrones `[^\s"']+` cortan en el primer espacio, y el modelo escribe `[[WRITE]] C:\...\Desktop\IA` en vez de `C:\...\Desktop\IA 27 T\...`. Dos reparaciones, ambas usando **el disco como oráculo**: `ExtractRealPathFromText` (host: recorta desde el último espacio hasta que la ruta exista) y `RepairToolPathFromContext` (ruta del modelo: busca en el propio mensaje la ruta más larga que empiece por lo que él puso y exista). `ExecuteWriteTool` **rechaza escribir si la ruta es una CARPETA** — sin eso se crea un archivo basura con el nombre de la carpeta.
- Otras trampas de herramientas: un `[[END]]` pegado a un `[[READ]]`/`[[CMD]]` arruina la ruta (`TrimStrayEndMarker`); una escritura **denegada** no debe contar como ejecutada ni entrar en `writtenPaths` (`ToolWasDenied`), si no el reintento se come su propia guarda de duplicado; y si el modelo mete el bloque cercado dentro del archivo, el archivo arranca con ` ```html ` (`ExecuteWriteTool` lo recorta).
- **NUNCA `Trim('"')` sobre un comando completo**: `Trim` quita comillas de AMBOS extremos del string entero, así que `Remove-Item ... "C:\...\lucher"` (bien formado) perdía la comilla de cierre y PowerShell fallaba al parsear. Solo quitar comillas si el comando ENTERO está entrecomillado (empieza Y termina con `"`). En `[[READ]]`/`[[WRITE]]` sí vale, porque ahí el argumento ES la ruta.
- **Fallback CMD → PY por código de salida, no por texto**: si el `[[CMD]]` sale con `Código de salida != 0`, se reintenta automáticamente con `[[PY]]` vía `TryConvertCmdToPython` (New-Item/mkdir → `os.makedirs`, Move-Item → `shutil.move`, Copy-Item → `shutil.copy`, Remove-Item → `rmtree`/`remove`). El patrón de extracción de ruta tolera switches (`-Recurse -Force`) ANTES y DESPUÉS de la ruta y comillas sin cerrar — sin eso, tomaba `-Recurse` como ruta y borraba nada. No disparar el fallback por la palabra "error" en el texto (frágil y falsos positivos).
- **Bibliotecas Python disponibles para `[[PY]]`** (instaladas en el Python 3.12.10 global, van anunciadas en el `ToolsPolicy` para que el modelo NO le pida al usuario instalarlas): openpyxl (Excel), python-docx (Word), pypdf (PDF), Pillow (imágenes), psutil (procesos/RAM/disco), pyperclip (portapapeles), watchdog (vigilar carpetas), rich (tablas con color), scapy (red). Si se agrega otra, anotarla acá Y en el `ToolsPolicy`, o el modelo no la va a usar.
- **Herramientas de red (PARTE 31/32):** `[[SCAN]] <rango>` (escaneo ARP con scapy) y `[[SPOOF]] <ip_victima> <ip_router> [interfaz]` (ARP spoofing, **advertencia roja obligatoria + confirmación explícita**). `[[YTDLP]] <link> [formato]` (descarga con yt-dlp, mp4 default, mp3 audio, destino Downloads). Las tres siguen el patrón marcador + intercepción y SIEMPRE piden permiso.
- **Herramientas de seguridad OFF por defecto (PARTE 32).** `AppConfig.SecurityToolsEnabled` arranca en `false` y hace TRES cosas: (a) `BuildSystemPrompt` **no anuncia** `[[SCAN]]`/`[[SPOOF]]` (viven en `SecurityToolsPolicy`, aparte del `ToolsPolicy` base), así que el modelo ni los conoce y no los emite; (b) si aun así aparecen, los casos `SCAN`/`SPOOF` de `ExecuteToolWithPermissionAsync` los rechazan **sin ejecutar y sin preguntar**, con `SecurityToolsDisabledReason`; (c) `deniedInTurn` los marca para que no reintente. Se activa a propósito con `config set security-tools on` y `config show` muestra el estado. **OJO — no es unausta real:** `[[CMD]]` sigue siendo PowerShell completo, y en la prueba el 7B esquivó el bloqueo con `[[CMD]] Test-NetConnection -ComputerName 192.168.1.1-254 -Port 445`. Para cerrar de verdad hay que implementar el `cmd-mode allowlist` (PENDIENTE).
- **Degeneración a chino (Qwen2.5)**: el modelo a veces responde en CJK pese al "respondé SIEMPRE en español" del system prompt — la instrucción no basta, hace falta trampa en el host. `ContainsExcessiveCjk` dispara cuando hay >10 caracteres CJK y >3 % de las letras del texto (comprobado en test real: la respuesta china de la sesión 29-9 dispara, una palabra china citada de pasada NO). Cuando dispara: se descarta la respuesta y se fuerza reintento en español (máx. 2). Excepción: `ChineseRequestedPattern` ("traducí al chino", "中文") desactiva la compuerta. Ojo al probarlo en PowerShell: un `.ps1` UTF-8 **sin BOM** se lee como ANSI en PS 5.1 y los CJK quedan mojibake — el test de la regla 7 hay que correrlo con `dotnet run` o con el `.ps1` guardado con BOM.
- **Borrar carpetas: el host arma el comando, no el modelo** (sesión 29-9: el 7B emitió `[[WRITE]] D:\Seguridad :: (borrar carpeta)` — WRITE no borra carpetas — y después `rmdir` de cmd.exe, que no existe en PowerShell, en un bucle de 4 reintentos). Dos defensas: `ExplicitDeleteIntent` ("borra/eliminá/suprimí esta carpeta X" → el HOST genera `Remove-Item -LiteralPath "X" -Recurse -Force`, el modelo no decide la herramienta) y `TranslateCmdExeCommand` en `MatchToolRequest` (`rmdir`/`rd`/`del`/`erase` con `/s`/`/q` se traducen a `Remove-Item` ANTES de mostrar el permiso — el usuario ve el comando ya traducido en la píldora). PENDIENTE: el harness no corre `WriteClaimPattern`, así que el modelo pudo cantar "La carpeta ha sido eliminada" sin que se ejecutara nada y el harness cerró con "OBJETIVO CUMPLIDO". Hay que llevar las compuertas de claims al camino del harness.
- **Para escribir un archivo largo en este modelo NO hay que bajar `max-tokens`**: con 400 el modelo agota los tokens antes de cerrar el `[[END]]` y el marcador queda incompleto. Con los 1536 de la configuración entra.
- Un 7B **alucina rutas**: inventa placeholders (`C:\Users\TuNombreDeUsuario\...`). Defensas: `BuildEnvironmentInfo` (ECnetServerSession.cs) inyecta usuario/cwd/Documentos/Escritorio REALES en el system prompt; `PlaceholderPathPattern` bloquea la ejecución y fuerza reemisión con la ruta real; alias "documentos/escritorio/descargas" resuelven a carpetas reales en la intención explícita; escrituras a la misma ruta en el mismo turno se bloquean (idempotencia).
- Al flushear colas del streaming hacia la consola, pasar SIEMPRE por `StripToolMarkers`: los fragmentos de marcador no deben llegar a pantalla.
- **Modelo corrupto**: una copia GGUF dañada (mismo tamaño, distinto hash) produce basura tipo `0C(G&&5B<#F06E...` sin dar ningún error. Diagnóstico: `Get-FileHash <modelo>` comparado con el original, o `runtime\llama-cli.exe -m <modelo> -p "hola" -n 32` (si sale basura, el modelo está corrupto). **Nunca confiar en que la copia "está ahí"**: verificar el hash. El hash sano de `Atenea-Omega-IB2.gguf` es `65B8FCD92AF6B4FEFA935C625D1AC27EA29DCB6EE14589C55A8F115CEAAA1423`.
- **Una sola copia del código y una sola del modelo.** Antes había dos carpetas de fuentes que divergían; ahora no. Si aparece una segunda copia, es un error.
- **Nunca hacer `git add -A` a ciegas.** Una vez la copia de trabajo mostró 3 archivos versionados como borrados (`D`) y commitear eso los habría perdido para siempre. Un `D` en `git status` significa "ausente del disco, presente en el repo": se recuperan con `git restore`, no se commitean.
- **El repositorio es el único respaldo.** Todo lo que importa va commiteado: el código, `icono.ico`, `AGENTS.md` y `IA27-BITACORA-MAESTRA-Y-REGLAS.txt`. Lo único que no entra es el `.gguf` (4,36 GB).
- **Interfaz con Spectre.Console (PARTE 23).** El banner usa paneles/tablas de `Spectre.Console`, pero **el streaming de tokens es `Console.Write` puro, nunca `Markup`**: el contenido del modelo contiene `[SISTEMA · ...]` que Markup interpretaría como tag y se tragaría texto. Todo contenido de usuario/modelo dentro de un `Markup` va con `Markup.Escape`. La tabla de comandos del banner solo lista comandos que existan de verdad (hoy NO hay atajos `ctrl+x` ni pistas `@`/`!`: son las fases B y C de P16, no agregarlos al banner sin implementarlos). `AnsiConsole.Clear()` no está: no agregarlo sin decisión explícita.
- **Comandos sin "/" (bitácora P15.37).** `NormalizeBareCommand` acepta solo formas inequívocas (palabra suelta conocida, `net`/`net on`/`net off`, `<cmd> <número>`). **Cualquier extensión del patrón tiene que contemplar el falso positivo**: la primera versión convertía `net neutro` en `/net neutro` y un argumento desconocido apagaba internet sin avisar. `system`/`harness`/texto libre siempre exigen `/`.
- **Búsqueda por intención (PARTE 22 de la bitácora).** El enrutado vive en `ClassifySearchIntent`/`SearchWebAsync`: `dónde` → Nominatim, `clima` → Open-Meteo, general → Wikipedia extracto + DDG con página ganadora. Reglas: (a) el `User-Agent` identificador de `CreateWebClient` es obligatorio para Nominatim, no quitarlo; (b) la geocodificación del clima lleva `countrycodes=ar` a propósito — sin eso "salta" matchea Salta Carnero (España), verificado en prueba real; la búsqueda general queda sin sesgo; (c) los verbos de intención se limpian en `CleanPlaceQuery`, no en la URL; (d) los recortes de ~1200-1600 caracteres por motor suben el contexto de 8192, no agrandarlos sin medir tokens.
- **Rechazo con motivo (F1, PARTE 24).** Cuando denegás un permiso de herramienta (READ fuera / CMD / WRITE), la terminal captura el motivo y se lo inyecta al modelo para que cambie de enfoque. Formato: `n <motivo>` en una línea (p. ej. `n no toques el index`), o `n` solo y luego te pregunta "¿Por qué no?" (solo en teclado real; con stdin pipeado **no** pregunta para no comerse la línea siguiente del script). El motivo va en el mensaje `[SISTEMA · el usuario denegó ...]` conservando el prefijo exacto (load-bearing para `ToolWasDenied`). Se sanitiza: tope 300 chars, `[[` → `[`. Además, si el modelo reemite la MISMA acción (tipo+objetivo) en el mismo turno, **no se vuelve a preguntar**: se auto-deniega con el mensaje "ya te lo negate con este motivo, no insistas". El set de denegados es **por turno** (nunca campo de instancia) para no violar la regla 5 (CMD/WRITE siempre preguntan). El camino de entrada es `AskPermission` → `AskPermissionFallback`.
- **Píldoras de permiso y diff (F2, PARTE 25).** En consola interactiva el permiso son píldoras `[permitir] [denegar]` con `←/→` (ReadKey crudo, NO SelectionPrompt), enter confirma, esc deniega; en las escrituras se muestra antes el diff contra el disco con cabecera `viejo b → nuevo b · +X −Y`. **Regla dura del diálogo de permiso**: es consola cruda, así que (a) si la stdin **o** la stdout está redirigida se va al fallback `s/n` de una línea — `Console.CursorLeft` con stdout a archivo lanza "Controlador no válido" (verificado, habría tirado el turno entero) y `ReadKey` rompe los tests pipeados; (b) CADA `CursorLeft` va envuelto en `TrySetCursorLeft` (try/catch), que el permiso nunca se caiga por reposicionar; (c) el pedido va en **su propia línea** y las píldoras en la siguiente — `RenderPills` rebobina con `CursorLeft=0` y si compartieran línea pisarían el texto del pedido; (d) el layout de píldoras es de largo constante entre repintados, o quedan restos. El diff (LCS línea a línea) usa tie-break `>` en el DP para que los reemplazos salgan `-` antes que `+`, como el diff de siempre. **Este camino NO se cubre con tests pipeados**: la prueba de las píldoras es manual (PARTE 25).
- **/stats (F3, PARTE 26).** `GenerationNotice.GetStats()` → `lastStats` se guarda en el `finally` de cada turno del bucle principal: `/stats` (o `stats` sin `/`, está en `BareCommandPattern`) muestra tokens/tiempo/tok/s del **último turno completado**, no el acumulado, y los turnos de `/harness` **no** lo actualizan (no pasan por `GenerationNotice`). En tests pipeados dice "Aún no hay estadísticas" porque el pipe entrega la línea antes de que termine la generación: no es bug del comando.

## Layout y despliegue (reglas)

Layout verificado y funcionando. Todo en `C:\Users\nicot\OneDrive\Desktop\Estalingrado corp\proyectos\IA 27 T\` (antes `Desktop\IA 27 T\` — la carpeta se movió el 27/9/2026; revisar los DOS config.json tras cualquier mudanza de carpetas, no solo de discos — ver bitácora P15 puntos 27 y 34):
```
IA 27 T\
  ia_terminal\                      ← repo: código, .git, AGENTS.md, bitácora
  ia_terminal\publish\portable.exe  ← portable (el entregable real)
  ia_terminal\publish\runtime\      ← llama-server.exe y DLLs
  ia_terminal\publish\config.json   ← config del portable de desarrollo
  portable 0.2.exe                  ← entrega congelada
  runtime\                          ← runtime de la entrega
  config.json                       ← config de la entrega
  modelos\                          ← el .gguf
  logs\                             ← lo crea solo al arrancar
```

- `AppConfig.GetDefaultConfigPath()` usa `AppContext.BaseDirectory\config.json` (junto al .exe) y solo cae a `%APPDATA%` si la carpeta no es escribible.
- `ResolveModelDirectory` ordena: `models\` junto al exe (solo si contiene algún `.gguf`) → ruta configurada si existe → búsqueda de `Modelo/models/Modelos` hasta 3 niveles arriba del exe → ruta configurada.
- `Save()` respeta la ruta de modelos que el usuario fija explícitamente (no la pisa la resolución automática).
- **Hay DOS `config.json`**, uno por ejecutable, y cambiár uno no cambia el otro. Para cambiar la ruta del modelo hay que usar `portable.exe config set model-dir "<ruta>"` en los DOS, no editar el archivo a mano.
- Publicar: `dotnet publish -c Release -o publish`. **Requiere que `portable.exe` NO esté corriendo** (IOException al empaquetar).
- Al arrancar, un `llama-server.exe` huérfano de una sesión anterior puede impedir la carga (memoria y archivo de modelo tomados). Limpiar: `Get-Process llama-server | Stop-Process -Force`.
- El entregable congelado NO es un .exe suelto: al lado tiene que estar `runtime\` y su propio `config.json`, si no arranca resolviendo a una ruta de C: y `doctor` da OK mentiroso.
- Verificación rápida sin abrir la sesión: `portable.exe doctor` (todo `[OK]`) y `portable.exe listar` (debe mostrar el GGUF). Un `doctor` OK no alcanza: hay que mirar de dónde sacó el runtime y el modelo.

## Documentación

- `IA27-BITACORA-MAESTRA-Y-REGLAS.txt` (en la raíz del repo) es la bitácora maestra: métodos, reglas, trampas encontradas e historial de incidentes. **Todo comportamiento nuevo o trampa encontrada se anota ahí y acá.** Los números concretos (hash del modelo, hash del .exe congelado, tokens/s, configuración óptima) viven en la bitácora, no repetidos acá.
