# Changelog

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/).
Versionado según [SemVer](https://semver.org/lang/es/).

Las fechas son las de los commits reales (`git log`), no estimaciones aproximadas.

> **Estado del versionado:** la entrega congelada es **0.2** (ver PARTE 17 y PARTE 21 de
> la bitácora). El trabajo posterior a ese congelamiento — PARTE 31, PARTE 32, F1/F2/F3 y
> la gobernanza del repo — está bajo `[Unreleased]` porque el número de versión no se
> volvió a subir. No hay forma honesta de trazar la línea exacta sin regenerar la entrega,
> así que no se inventa acá.

## [Unreleased]

### Agregado

- **Herramientas de red** (`[[YTDLP]]`, `[[SCAN]]`, `[[SPOOF]]`) — descarga con `yt-dlp`,
  escaneo ARP con `scapy` y ARP spoofing. Las tres piden permiso siempre; `[[SPOOF]]`
  además exige advertencia roja explícita. (PARTE 31)
- **Notificaciones de escritorio** para fin de turno y error, con sonidos propios.
  (PARTE 31)
- **Búsqueda por intención** — el enrutado es según la intención de la pregunta:
  *dónde* → Nominatim/OpenStreetMap, *clima* → Open-Meteo, general → Wikipedia con
  extracto + DuckDuckGo quedándose con la página ganadora. (PARTE 22)
- **`/stats`** — tokens, tiempo y tokens/s del **último turno completado**. (PARTE 26)
- **Rechazo de permiso con motivo** — el usuario responde `n <por qué>` y el motivo se
  inyecta al modelo para que cambie de enfoque. Si reintenta la misma acción en el
  mismo turno, se auto-deniega sin volver a preguntar. (PARTE 24)
- **Interfaz con Spectre.Console** — banner de arranque con panel del logo y tabla de
  Estado, que lista **solo comandos que existen de verdad**. (PARTE 23)
- **Job Object + mutex de instancia** — se matan los `llama-server` huérfanos al cerrar
  y se evita la doble instancia.
- **Portable USB** — `config.json` junto al ejecutable, resolución de modelos portable y
  protección contra copias corruptas del GGUF.
- **Gobernanza del repo** — `LICENSE` (MIT), `CONTRIBUTING.md`, `SECURITY.md`,
  `.editorconfig`, `.gitattributes` (LF en todo el repo) y CI de compilación.
  Ver [CONTRIBUTING.md](CONTRIBUTING.md).

### Cambiado

- **Herramientas de seguridad OFF por defecto.** `config set security-tools on` para
  habilitarlas. Con el flag apagado el system prompt ni las menciona, así que el modelo
  no las conoce. (PARTE 32)
- **Portable a red** — se eliminó la copia entregable anterior, junto con los archivos
  previos al rename. (commit `c68ba81`)

### Corregido

- **Píldoras de permiso que pisaban la línea del pedido** y **`CursorLeft` con stdout
  redirigida** — `Console.CursorLeft` lanzaba *"Controlador no válido"* y habría tirado
  el turno entero. Los dos bugs **solo aparecieron en consola interactiva**: ningún test
  pipeado los podía ver. (PARTE 25)
- **Diff de escrituras** — diff línea a línea (LCS) contra el disco, con cabecera
  `viejo b → nuevo b · +X −Y`, mostrando el cambio *antes* de escribir. (PARTE 25)
- **Rutas truncadas en los espacios** — la carpeta del proyecto se llama `IA 27 T`, y los
  patrones de ruta cortaban en el primer espacio. Se reparan usando **el disco como
  oráculo**. (PARTE 24)
- **Fin de línea mezclados** — sin `.gitattributes` y con `core.autocrlf=true`, un
  `git add` podía reintroducir CRLF. Ahora hay LF forzado en índice y disco.

## [0.2] — 2026-09-28

Entrega congelada. Hash y tamaño en la PARTE 17 de la bitácora.

- F1: rechazo de permiso con motivo. (PARTE 24)
- F2: píldoras de permiso y diff de escrituras. (PARTE 25)
- F3: `/stats`. (PARTE 26)
- Correcciones de los dos bugs del camino interactivo.

## [0.1] — 2026-09-27

Entrega inicial. Ya no está en esta máquina (se fue con el pendrive); su hash queda
anotado en la PARTE 17 por si hay que reconstruirla.

### Agregado

- Agente de IA local con búsqueda web autorizada.
- Herramientas de agente `[[READ]]` / `[[CMD]]` / `[[WRITE]]` con confirmación.
- Portable USB: `config.json` junto al ejecutable y resolución de modelos portable.
- La bitácora maestra entra al repo, versionada con el código.

---

La historia completa —con el detalle de cada incidente, cada trampa y cada método— está
en [`docs/IA27-BITACORA-MAESTRA-Y-REGLAS.txt`](docs/IA27-BITACORA-MAESTRA-Y-REGLAS.txt).
Este archivo es el resumen; la bitácora es la fuente.