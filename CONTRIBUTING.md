# Cómo contribuir a IA27 Terminal

Agente de IA **100 % local** en C# / .NET 8, sin Python. Corre contra `llama-server.exe`
sobre un GGUF en la máquina del usuario. La interfaz es una consola Cyberpunk en español.

Antes que nada: leé [`AGENTS.md`](AGENTS.md). Es el contrato técnico del proyecto y
tiene las reglas que hacen que funcione. Este documento es la capa de arriba.

---

## 1. La regla que no se negocia

**Después de CUALQUIER cambio en código fuente:**

```
dotnet build -c Release
dotnet publish -c Release -o publish
```

El entregable real es `publish/portable.exe` (self-contained, single-file). Un cambio
de código que no llega al portable **no existe para el usuario**.

`dotnet build` no alcanza. `publish/` está en `.gitignore` — es un binario de ~70 MB,
así que el CI **no lo publica** ni lo sube como artefacto. La republished es tarea de
quien está en la máquina, y por eso es obligatoria y manual.

Compilar en Release, no en Debug: varios bugs de este proyecto (trampas de rutas,
`[[CMD]]` malarmado) solo se manifiestan fuera de la configuración de depuración.

## 2. La doctrina: probá con el modelo real, no con "compiló"

Esta es la lección más cara del proyecto y no es negociable.

**Compilar no prueba nada de un agente.** El código de este repo compila perfectamente
mientras el modelo:

- afirma "ya creé el archivo" sin emitir `[[WRITE]]` → `WriteClaimPattern` lo intercepta
- se niega a actuar y **enseña el código en el chat** para que lo copies a mano →
  `LooksLikeArtifactInChat` lo descarta y reemite la herramienta
- emite `[[WRITE]] C:\...\Desktop\IA` y **corta la ruta en el primer espacio**
  (la carpeta del proyecto se llama `IA 27 T`) → `ExtractRealPathFromText` /
  `RepairToolPathFromContext` la reparan **usando el disco como oráculo**
- escribe un `[[END]]` pegado al marcador y arruina la ruta → `TrimStrayEndMarker`
- mete el bloque cercado ```` ```html ```` dentro del archivo → `ExecuteWriteTool` lo recorta

Todos esos bugs **compilaban**. Aparecieron al correr el agente de verdad.

**Regla práctica:** si tocás una trampa anti-alucinación, un parser de marcadores o
una ruta, probá contra el modelo real antes de dar por hecho que está. Usá una copia
en una carpeta de prueba, verificá por hash que el archivo cambió, revisá que la
carpeta quede sin basura, y borrá la copia.

Los tests pipeados no alcanzan para el camino interactivo de la consola. Eso se
aprendió en la PARTE 25: dos bugs (`CursorLeft` con stdout redirigida, píldoras de
permiso pisando la línea del pedido) no se veían en ningún test y se rompieron en
la sesión real.

## 3. Qué se acepta

- Arreglos de trampas anti-alucinación, parsing de marcadores o rutas.
- Herramientas nuevas que sigan el patrón existente (`marcador [[XXX]]` → intercepción
  al final del stream → resultado como `[SISTEMA · ...]` → `AskContinueAsync`).
- Búsqueda, interfaz, documentación, herramientas de la terminal.
- Correcciones de las trampas de compuertas documentadas en `AGENTS.md`.

### Qué necesita permiso explícito antes de tocarlo

- **El loop de agente** (`TerminalApplication.cs`, `ECnetServerSession.cs`): orden de
  Streaming, intercepción de marcadores, contadores de reintento, política de system prompt.
- **Las reglas de permisos.** `[[CMD]]` y `[[WRITE]]` **siempre** piden confirmación.
  Nunca autorización automática de sesión, nunca un flag que las esquive.
- **El `.gitignore`.** `config.json`, `bin/`, `obj/`, `publish/`, `logs/`, `runtime/`
  y el `.gguf` (4,36 GB) no se versionan. Agregar el modelo o el runtime al repo rompe
  todo.

### Qué no entra

- Código generado, `bin/`, `obj/`, `publish/`, `runtime/`, el `.gguf`.
- Una **segunda copia** del código o del modelo. Una sola carpeta de fuentes, un solo
  `.git`, un solo modelo. Si aparece un duplicado, es un error.
- Secretos, tokens, claves de API.

## 4. Convenciones de commit

A partir de ahora, [Conventional Commits](https://www.conventionalcommits.org/):

```
feat:   una herramienta o capacidad nueva
fix:    un bug
docs:   documentación
chore:  mantenimiento, .gitignore, deps
refactor:   reorganización sin cambio de comportamiento
test:   tests
```

El historial anterior no sigue esta convención (usa prefijos como `PARTE 32:` o `F2:`).
**No se reescribe el historial**: el repo es el único respaldo, y reescribir commits ya
pusheados paraeuxstilo no vale el riesgo. La convención aplica hacia adelante.

Mensajes en español, en imperativo, que digan **por qué** y no solo **qué**.
Cerrá el cuerpo con la razón cuando el "qué" no se explica solo — que es casi siempre
en este repo, porque casi todo bug era una trampa del modelo.

## 5. Git: lo que no hay que hacer

**NUNCA `git add -A` a ciegas.**

Una vez la copia de trabajo mostró 3 archivos versionados como borrados (`D`) y
commitear eso los habría perdido para siempre. Un `D` en `git status` significa
**ausente en el disco, presente en el repo**: se recuperan con `git restore`, no se
commitean.

Antes de commitear:

```
git status
git diff
git log --oneline -10
```

Mirá las tres. `git diff` en especial: si toca cientos de líneas de un `.cs`,
asegurate de que es lo que querés.

Los finales de línea están fijados por `.gitattributes` (`* text=auto eol=lf`). Si ves
`w/mixed` en `git ls-files --eol`, el archivo quedó mezclado en el disco:

```
git ls-files --eol | findstr w/mixed
```

## 6. Documentación

Todo comportamiento nuevo o trampa encontrada se anota en:

- **`docs/IA27-BITACORA-MAESTRA-Y-REGLAS.txt`** — la bitácora maestra, con las PARTes.
- **`AGENTS.md`** — el contrato técnico y las reglas vigentes.

Las dos. La bitácora es la historia (qué pasó, en qué orden, con qué incidente);
`AGENTS.md` es el estado actual (qué reglas aplican hoy). Una trampa nueva va en las
dos: en la bitácora como PARTE, en `AGENTS.md` como regla.

**No dupliques contenido.** Si un doc nuevo podría copiar un párrafo de la bitácora,
enlazá a la PARte en vez de copiarla. Dos copias de la misma regla divergen, y la que
diverga primero miente.

Si tocás una regla de `AGENTS.md` que afecta el layout o el despliegue, actualizá
también la sección de despliegue: **hay dos `config.json`**, uno por ejecutable, y
cambiar uno no cambia el otro.

## 7. Style del código

- Español rioplatense en los mensajes de sistema, los comentarios y los nombres de
  método. Es lo que ve el usuario final.
- C# con 4 espacios, UTF-8, tildes y ñ directas (no escapes).
- Los `using` en orden alfabético.
- Sin dependencias nuevas sin pensar: el proyecto es **local y autocontenido**.
  Cada paquete NuGet es algo que puede fallar sin internet, que es justo el escenario
  en el que más se usa este agente.