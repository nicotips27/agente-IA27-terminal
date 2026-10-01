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
| CORS | **no se pasa `--cors`** | llama.cpp deja CORS desactivado salvo que se pida explícitamente |
| API key | **no se pasa `--api-key`** | Sin autenticación |
| Paralelo | `--parallel 1` | Una sesión a la vez |

**El riesgo residual real no es el CORS: es que no hay autenticación y escucha en un
puerto de loopback.** Cualquier proceso local puede hablar con el modelo mientras la
sesión está viva. CORS no protege contra eso — protege contra el *navegador*, y es
justamente lo contrario de lo que pasa acá.

Lo que **sí** mitiga el ataque desde el navegador: sin `--cors`, una página web no
puede leer respuestas cross-origin de este puerto, y los endpoints de llama.cpp reciben
`application/json`, que dispara *preflight* y falla sin cabeceras CORS. El vector real
es otro software corriendo en la misma sesión de usuario, no una página.

Mitigaciones actuales: el puerto es efímero y lo elige el SO (no es adivinable desde
fuera ni queda fijo entre arranques), y `ChildProcessJob.cs` mata los `llama-server`
huérfanos al cerrar, así el modelo no queda cargado en memoria para el proceso que siga.

**Pendiente:** evaluar un `--api-key`, o al menos dejar registrado que el puerto queda
abierto a todo lo local mientras la sesión está viva.

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