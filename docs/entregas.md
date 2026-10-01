# Entregas y despliegue

Índice, no duplicado. Los hashes y el historial completo están en
[`IA27-BITACORA-MAESTRA-Y-REGLAS.txt`](IA27-BITACORA-MAESTRA-Y-REGLAS.txt) (PARTE 17).

## La regla

```
dotnet build -c Release
dotnet publish -c Release -o publish
```

`publish/portable.exe` **es** el entregable. Un cambio que no llega al portable no
existe para el usuario. `publish/` está en `.gitignore`: son ~70 MB, el portable se
actualiza localmente, no se sube al repo. Por eso el CI hace `build` y no `publish`.

`dotnet publish` **requiere que `portable.exe` NO esté corriendo** (IOException al
empaquetar).

## Entrega vigente

| | |
| --- | --- |
| Archivo | `publish/portable.exe` |
| SHA-256 | `D9764CAB5E27496349A124EA1D341A5D3118ADF54E92601CDF2084466058E31B` |
| Tamaño | 69.934.095 bytes |
| Fecha | 30/9/2026 21:59 |

Idéntica byte a byte a `..\portable 0.2.exe` (la entrega congelada). Los números
concretos viven acá y en la bitácora, no en `AGENTS.md`, que es la regla y no el
inventario.

Ojo con dos builds del mismo día: el de las 12:45 era `85E0B7F6…E773CBB3` y **no**
tenía el banner con el estado real de la seguridad de red. Si comparás hashes contra
una nota vieja, el tamaño es el mismo (69.934.095) y por tamaño no se distinguen.

## Layout

```
IA 27 T\
  ia_terminal\                          ← repo: código, .git, AGENTS.md
  ia_terminal\docs\                     ← bitácora maestra e índices
  ia_terminal\publish\portable.exe      ← portable (el entregable real)
  ia_terminal\publish\runtime\          ← llama-server.exe y DLLs (51 archivos)
  ia_terminal\publish\config.json       ← config del portable de desarrollo
  portable 0.2.exe                      ← entrega congelada
  runtime\                              ← runtime de la entrega
  config.json                           ← config de la entrega
  modelos\                              ← el .gguf (4,36 GB)
  logs\                                 ← lo crea solo al arrancar
```

## Hay DOS `config.json`

Uno por ejecutable, y **cambiár uno no cambia el otro**. Para cambiar la ruta del
modelo hay que usar los dos:

```
portable.exe config set model-dir "<ruta>"
```

No editar el archivo a mano. La carpeta del proyecto se movió el 27/9 y hubo que
revisar los dos (ver PARTE 15, puntos 27 y 34).

## Resolución de `config.json`

`AppConfig.GetDefaultConfigPath()` usa `AppContext.BaseDirectory\config.json` (junto al
`.exe`) y solo cae a `%APPDATA%` si la carpeta no es escribible.

## Resolución de la ruta del modelo

`ResolveModelDirectory` ordena:

1. `models\` junto al `.exe` — solo si contiene algún `.gguf`
2. la ruta configurada, si existe
3. búsqueda de `Modelo`/`models`/`Modelos` hasta 3 niveles arriba del `.exe`
4. la ruta configurada, como último recurso

`Save()` respeta la ruta que el usuario fija explícitamente: la resolución automática
no la pisa.

## Verificación sin abrir la sesión

```
portable.exe doctor     # todo [OK]
portable.exe listar     # debe mostrar el GGUF
```

**Un `doctor` OK no alcanza**: hay que mirar de dónde sacó el runtime y el modelo. Si
`doctor` dice OK pero apunta a una ruta de `C:`, está resolviendo mal.

Un `llama-server.exe` huérfano de una sesión anterior puede impedir la carga (memoria y
archivo de modelo tomados):

```
Get-Process llama-server | Stop-Process -Force
```

## El entregable congelado no es un `.exe` suelto

Al lado tiene que estar `runtime\` **y su propio `config.json`**. Si no, arranca
resolviendo a una ruta de `C:` y el `doctor` da un OK mentiroso.

## Un modelo corrupto no da error

Una copia GGUF dañada —mismo tamaño, distinto hash— produce basura tipo
`0C(G&&5B<#F06E...` **sin ningún error**. Diagnóstico:

```
Get-FileHash <modelo>                              # comparado con el original
runtime\llama-cli.exe -m <modelo> -p "hola" -n 32   # si sale basura, está corrupto
```

**Nunca confiar en que la copia "está ahí": verificar el hash.** Si sale basura de ese
tipo, el diagnóstico está en [P7](#).

---

Índices: [arquitectura](arquitectura.md) · [herramientas](herramientas.md) · [seguridad](seguridad.md)