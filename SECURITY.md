# Seguridad

IA27 Terminal es un agente de IA que **ejecuta cosas en la máquina del usuario**.
Eso no es un detalle de la documentación: es la superficie de ataque principal del
proyecto. Esta página describe el estado **real** del código, no el ideal.

Reportá una vulnerabilidad por [GitHub Security Advisory](https://github.com/nicotips27/agente-IA27-terminal/security/advisories/new).

---

## Superficie de ataque

### 1. `[[CMD]]` ejecuta PowerShell completo — sin allowlist

**Este es el riesgo más grande del proyecto y está abierto.**

`[[CMD]]` ejecuta el comando **tal cual** en PowerShell. No hay allowlist, ni de
verbos ni de binarios. Todo lo que el usuario puede hacer en una terminal, el agente
lo puede hacer.

La mitigación real es la **confirmación humana**: `[[CMD]]` siempre pide permiso, con
píldoras en consola interactiva y `s/n` cuando la stdin o la stdout están pipeadas.
Nunca hay autorización automática de sesión.

Sobre eso hay dos capas más:

- Aviso rojo adicional cuando el comando matchea patrones destructivos.
- Denegación **con motivo**: el usuario puede responder `n <por qué>` y el motivo se
  le inyecta al modelo, que cambia de enfoque. Si reintenta la misma acción en el
  mismo turno, se auto-deniega sin volver a preguntar.

**Lo que falta:** un `cmd-mode allowlist`. Está anotado como pendiente desde la
PARTE 32 y es un agujero conocido, no un descuido. La primera versión del bloqueo de
`[[SCAN]]` se esquivó en la prueba real con:

```
[[CMD]] Test-NetConnection -ComputerName 192.168.1.1-254 -Port 445
```

Un flag que "desactiva escanear la red" no sirve si queda PowerShell completo.

### 2. `[[SCAN]]` y `[[SPOOF]]` — OFF por defecto

Escaneo ARP con `scapy` y ARP spoofing. Ambas exigen **advertencia roja y
confirmación explícita** en cada uso.

Vienen **desactivadas** (`AppConfig.SecurityToolsEnabled`, default `false`). El flag
hace tres cosas: no anuncia las herramientas en el system prompt (el modelo ni las
conoce), las rechaza **sin ejecutar y sin preguntar** si aparecen igual, y las marca
como denegadas para que no reintente.

Se activan a propósito:

```
portable.exe config set security-tools on
portable.exe config show    # muestra el estado
```

> **Ojo: esto NO es una restricción real.** El flag frena `[[SCAN]]`/`[[SPOOF]]`, pero
> `[[CMD]]` sigue siendo PowerShell completo. Tratar `security-tools` como si fuera
> una barrera de seguridad es un error: es una guarda de UX, no un sandbox.

### 3. Escrituras fuera del área de trabajo

`[[WRITE]]` libre solo dentro del área de trabajo; fuera pide permiso. `[[READ]]`
libre solo dentro del área de trabajo; fuera pide permiso. `[[WRITE]]` rechaza
escribir si la ruta es una **carpeta** (evita el archivo basura con nombre de carpeta).

### 4. El servidor de inferencia

`llama-server.exe` se lanza con un conjunto de argumentos fijo y explícito
(`ECnetServerSession.cs`), entre ellos:

| Aspecto | Estado | Por qué |
| --- | --- | --- |
| Bind | `--host 127.0.0.1` | Solo loopback. No es alcanzable desde la red. |
| Puerto | **libre, asignado por el SO** | `FindFreePort()` pide un puerto efímero al sistema en cada arranque. No está en `config.json` |
| CORS | **`*` por defecto de llama.cpp** | No se restringe. Ver abajo |
| API key | **ninguno** | Sin autenticación |
| Paralelo | `--parallel 1` | Una sesión a la vez |

**Este es el punto que hay que entender bien**, porque es contraintuitivo.

`llama-server` levanta con CORS en comodín y sin API key por defecto. Cada arranque
escribe este aviso en el log:

```
CORS is set to allow all origins ('*') and no API key is set
this can be a security risk (cross-origin attacks)
```

No hace falta pasar `--cors` para que esté activo: el comodín ya es el default del
upstream. (Esta es la **PARTE 20** de la bitácora, y su premisa es correcta.)

Combinado con `--host 127.0.0.1`, el servidor **no es alcanzable desde la red**: la
amenaza es local, no remota. Pero "local" aquí incluye **cualquier página web abierta
en el navegador**: con CORS en `*`, una página puede pegarle a
`http://127.0.0.1:<puerto>` y **leer la respuesta del modelo**. Eso es cross-site
request forgery contra un servidor local, y es un vector real, no teórico.

El puerto es aleatorio (`FindFreePort`), lo que baja la probabilidad pero no la anula:
una página podría barrer un rango de puertos altos. **Un puerto aleatorio NO es una
mitigación de seguridad**, solo agrega ruido.

Aparte del navegador, cualquier proceso local puede hablar con el modelo mientras la
sesión está viva. CORS no protege contra eso — protege contra el navegador.

Mitigaciones actuales: bind a loopback (saca la red, no el navegador) y puerto
aleatorio (ruido, no seguridad). `ChildProcessJob.cs` mata los `llama-server`
huérfanos al cerrar, así el modelo no queda cargado en memoria para el proceso que
siga.

**Pendiente, sin resolver:** las tres opciones de la PARTE 20 siguen sin elegirse —
(a) `--api-key` aleatorio por sesión mandándolo en cada request, que es lo que
recomienda el aviso upstream; (b) restringir el CORS con `--cors-origins`; o
(c) aceptar el riesgo a sabiendas y dejarlo escrito. Mientras no se elija una, es una
decisión pendiente.

Verificado contra el `--help` del runtime: `--cors-origins` tiene default `*`,
`--cors-headers` default `*` y `--cors-credentials` default **habilitado**. Ese
último es lo que agrava el cuadro: con credenciales permitidas y orígenes en `*`, el
ataque desde el navegador tiene menos trabas de las que parecería.

**Pendiente:** evaluar un `--api-key`, o al menos documentar el puerto para que quien
lo use sepa que está abierto a todo lo local.

### 5. Descargas

`[[YTDLP]]` descarga con `yt-dlp` y siempre pide permiso. El destino por defecto es
`Downloads`.

## Lo que este proyecto NO tiene

- **No hay sandbox.** Ninguna aislamiento del proceso que ejecuta las herramientas.
  Todo corre con los permisos del usuario que lanzó la terminal.
- **No hay allowlist de comandos.** Ver arriba.
- **No hay auditoría de las acciones ejecutadas** más allá del log de sesión y de
  `writtenPaths` en memoria, que no sobrevive al proceso.

Por eso el modelo de permisos es **humano en el medio**. Todo lo sensible pasa por
una confirmación. Si alguien propone quitar una de esas confirmaciones, eso es un
cambio de arquitectura, no una mejora.

## Versiones afectadas

Todo en `main`. Ver [el CHANGELOG](CHANGELOG.md) para el detalle por versión.

## Divulgación

Este es un proyecto de un solo dueño. Si encontrás algo, abrí un issue o un advisory
privado. Se agradece el detalle del paso a paso para reproducirlo.