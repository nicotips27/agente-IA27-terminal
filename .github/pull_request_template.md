## Qué cambia

<!-- Una línea. El "qué" va en el título del commit; acá va el "por qué". -->

## Por qué

<!-- El problema que resuelve. Si no resuelve un problema, ¿por qué existe? -->

## Verificación

- [ ] `dotnet build -c Release` → 0 errores
- [ ] `dotnet publish -c Release -o publish` → el portable regenerado
- [ ] Probado contra el **modelo real**, no solo compilado

<!--
La tercera es la que se olvida y la que más cuesta. Compilar no prueba nada
de un agente: los bugs de rutas truncadas en los espacios, los [[WRITE]] sin
[[END]], las afirmaciones en falso del modelo y los [[END]] pegados al
marcador COMPILABAN todos. Solo aparecen corriendo el agente de verdad.

Si tocás una trampa anti-alucinación, un parser de marcadores o una ruta,
probá contra el modelo real: copia de prueba, verificá por hash que el
archivo cambió, revisá que la carpeta quede sin basura, borrá la copia.
-->

- [ ] Sin basura en la carpeta ni en el Escritorio
- [ ] `git status` limpio, sin `D` sin explicación

## Impacto en el entregable

- [ ] El hash de `publish\portable.exe` cambió (esperado)
- [ ] La bitácora tiene una PARTE nueva, o una regla nueva en `AGENTS.md`

## Checklist de quien revisa

- [ ] No se tocaron las reglas de permiso (`[[CMD]]` y `[[WRITE]]` siempre preguntan)
- [ ] No se agregaron rutas modelo-alucinadas ni placeholders
- [ ] No se metió `publish/`, `bin/`, `obj/`, `runtime/` ni el `.gguf`
- [ ] Los finales de línea quedaron en LF