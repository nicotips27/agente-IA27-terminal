# Seguridad

Índice, no duplicado. El detalle vive en
[`IA27-BITACORA-MAESTRA-Y-REGLAS.txt`](IA27-BITACORA-MAESTRA-Y-REGLAS.txt) y en
[`SECURITY.md`](../SECURITY.md) (el que va dirigido a quien reporta vulnerabilidades).

## La idea en una frase

**Este agente ejecuta PowerShell completo. No hay sandbox. La seguridad es la
confirmación humana, no el aislamiento.**

## Superficie, por riesgo

| Riesgo | Estado | Dónde |
| --- | --- | --- |
| `[[CMD]]` = PowerShell sin allowlist | **ABIERTO** | [P32](#) |
| `security-tools on/off` como barrera | **NO ES barrera** | [P32](#) |
| CORS `*` + sin API key en `llama-server` | **DECISIÓN ABIERTA** | [P20](#) |
| `[[SCAN]]` / `[[SPOOF]]` | OFF por defecto, piden permiso | [P31](#) |
| Escritura fuera del área de trabajo | Pide permiso | [P24](#) |
| Descargas (`[[YTDLP]]`) | Pide permiso, destino `Downloads` | [P31](#) |

## El punto que más se confunde

`config set security-tools off` (que es el default) **no restringe nada**. Hace tres
cosas, todas de UX:

1. El system prompt no menciona `[[SCAN]]`/`[[SPOOF]]`, así que el modelo ni las conoce.
2. Si aparecen igual, se rechazan **sin ejecutar y sin preguntar**.
3. Se marcan como denegadas para que no reintente.

Pero `[[CMD]]` sigue siendo PowerShell completo. En la prueba real el 7B esquivó el
bloqueo de `[[SCAN]]` con `Test-NetConnection`. Tratar el flag como si fuera una
barrera es un error de lectura.

## Lo que este proyecto NO tiene

- **Sandbox.** Ninguno. Todo corre con los permisos del usuario que lanzó la terminal.
- **Allowlist de comandos.** Ver arriba.
- **Auditoría persistente.** Solo el log de sesión y `writtenPaths`, que no sobrevive
  al proceso.

Por eso el modelo de permisos es humano en el medio: todo lo sensible pasa por una
confirmación. **Quitar una de esas confirmaciones no es una mejora, es un cambio de
arquitectura.**

## Reportar

Vulnerabilidades por [GitHub Security Advisory](https://github.com/nicotips27/agente-IA27-terminal/security/advisories/new),
no por issue.

---

Índices: [arquitectura](arquitectura.md) · [herramientas](herramientas.md) · [entregas](entregas.md)