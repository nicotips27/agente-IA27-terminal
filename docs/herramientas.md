# Herramientas del agente

Índice, no duplicado. Los detalles y las trampas están en
[`IA27-BITACORA-MAESTRA-Y-REGLAS.txt`](IA27-BITACORA-MAESTRA-Y-REGLAS.txt).

## El patrón que toda herramienta nueva sigue

```
el modelo emite un marcador [[XXX]]  →  el host lo intercepta al final del stream
  →  pide permiso  →  ejecuta  →  inyecta el resultado como [SISTEMA · ...]
  →  AskContinueAsync
```

Tres reglas que hacen que funcione:

1. **El mensaje debe EMPEZAR con el marcador**, sin texto "copiable" antes en la
   política. Un 7B parotea las frases de la política.
2. **El buffer anti-fugas del streaming retiene desde el último `[`**, así que ningún
   marcador llega a pantalla aunque el stream se corte.
3. **Al flushear colas hacia la consola, siempre por `StripToolMarkers`.**

PARTE 3 ([método](IA27-BITACORA-MAESTRA-Y-REGLAS.txt)), PARTE 4 ([catálogo](IA27-BITACORA-MAESTRA-Y-REGLAS.txt)).

## Catálogo

| Marcador | Qué hace | Permiso | PARTE |
| --- | --- | --- | --- |
| `[[READ]]` | Lee archivo o carpeta | Libre dentro del área de trabajo, pide fuera | [P4](#) |
| `[[CMD]]` | Ejecuta en PowerShell | **Siempre** | [P4](#) |
| `[[WRITE]]` | Escribe archivo (exige `[[END]]`) | **Siempre** | [P4](#) |
| `[[PY]]` | Ejecuta Python, cierra con `[[END]]` | **Siempre** | [P28](#) |
| `[[NET]]` | Búsqueda web — máximo 2 rondas por turno | — | [P22](#) |
| `[[YTDLP]]` | Descarga con `yt-dlp` | **Siempre** | [P31](#) |
| `[[SCAN]]` | Escaneo ARP con `scapy` | **Siempre** + advertencia roja | [P31](#) |
| `[[SPOOF]]` | ARP spoofing | **Siempre** + advertencia roja | [P31](#) |

Límites vigentes: máximo **5 herramientas encadenadas por turno**, **2 rondas** de
`[[NET]]`.

## Permisos: las reglas que no se tocan

- `[[CMD]]` y `[[WRITE]]` **siempre** preguntan. Píldoras en consola interactiva,
  `s/n` cuando la stdin o la stdout están pipeadas. **Nunca** autorización
  automática de sesión.
- Denegación **con motivo** (`n <por qué>`): el motivo se inyecta al modelo para
  que cambie de enfoque. Si reintenta la misma acción en el mismo turno, se
  auto-deniega sin volver a preguntar. El set de denegados es **por turno**, nunca
  campo de instancia, para no violar la regla anterior. (PARTE 24)
- Patrones destructivos en comandos → **advertencia roja adicional**.

### El diálogo de permiso en crudo

Es consola cruda, así que hay tres trampas ya pisadas:

1. Si la stdin **o** la stdout está redirigida, se va al fallback `s/n`. `CursorLeft`
   con stdout a archivo lanza *"Controlador no válido"* y habría tirado el turno entero.
2. Cada `CursorLeft` va envuelto en `TrySetCursorLeft`.
3. El pedido va en **su propia línea** y las píldoras en la siguiente — `RenderPills`
   rebobina con `CursorLeft=0`.

**Este camino no se cubre con tests pipeados**: la prueba de las píldoras es manual.
(PARTE 25)

## El modelo hay que probarlo en vivo

Un 7B tiene **dos formas de no actuar**, y hay que atrapar las dos:

- **Mentir** sobre la acción ("he creado el archivo X" sin emitir `[[WRITE]]`) →
  `WriteClaimPattern` intercepta y fuerza reintento (máx. 2).
- **Negarse a actuar y enseñar el código** ("abrí el archivo y reemplazá el contenido"
  + el bloque completo en el chat) → `LooksLikeArtifactInChat`, que exige **las dos**
  condiciones juntas: bloque cercado con contenido de archivo entero **y** indicación
  de hacerlo a mano. Cuando dispara, descarta y fuerza `[[WRITE]]` reutilizando el
  borrador del modelo (`ExtractDraftFromChat`); si no costaría el doble de tokens.

También: rutas alucinadas (`PlaceholderPathPattern`), truncadas en los espacios
(`ExtractRealPathFromText` + `RepairToolPathFromContext`, que usan **el disco como
oráculo**), el `[[END]]` pegado (`TrimStrayEndMarker`), el bloque cercado dentro del
archivo, y la degeneración a chino (`ContainsExcessiveCjk`).

**Todo eso compilaba.** Solo aparece corriendo el agente de verdad. Ver
[CONTRIBUTING.md](../CONTRIBUTING.md).

## Bibliotecas Python disponibles para `[[PY]]`

Instaladas en el Python 3.12.10 global y **anunciadas en el `ToolsPolicy`** para que el
modelo no le pida al usuario instalarlas:

`openpyxl` (Excel) · `python-docx` (Word) · `pypdf` (PDF) · `Pillow` (imágenes) ·
`psutil` (procesos/RAM/disco) · `pyperclip` (portapapeles) · `watchdog` (vigilar
carpetas) · `rich` (tablas con color) · `scapy` (red)

Si se agrega otra, hay que anotarla **acá y en el `ToolsPolicy`**, o el modelo no la
va a usar.

## Pendientes conocidos

- **cmd-mode allowlist.** `[[CMD]]` sigue siendo PowerShell completo. En la prueba de
  la PARTE 32 el 7B esquivó el bloqueo de `[[SCAN]]` con
  `[[CMD]] Test-NetConnection -ComputerName 192.168.1.1-254 -Port 445`.
- **El harness no corre `WriteClaimPattern`**, así que en modo harness el modelo
  puede cantar "objetivo cumplido" sin que se ejecutara nada.

---

Índices: [arquitectura](arquitectura.md) · [seguridad](seguridad.md) · [entregas](entregas.md)