# Tier-1 WebAssembly — JIT y AOT de WAM a wasm nativo

## Context

WebShumway corre el motor entero en el browser, pero **solo Tier-0, interpretado
por el intérprete Mono** (sin `RunAOTCompilation`): intérprete sobre intérprete.
Tier-1 no existe ahí porque `Reflection.Emit` no funciona en browser-wasm, y el
feature switch `Shumway.RuntimeCodegen=false` recorta el subsistema IL del
payload. En desktop, Tier-1 vale 1,9–5,5× (geomean ~3,3×) sobre un Tier-0 que ya
corre JIT (`docs/benchmarks/analysis.md:38-58`); contra el Tier-0 interpretado
del browser el margen posible es mayor. No existe hoy ningún benchmark
browser-vs-nativo.

**La idea**: un backend paralelo que compile WAM directamente a **módulos
WebAssembly** — JIT (promover predicados calientes emitiendo e instanciando un
módulo en runtime dentro del browser) y AOT (`shumway-link` horneando `.wasm` en
bundles). **Nada de IL interpretado** (decisión del usuario): el objetivo es
código nativo en el motor wasm del browser. Los benchmarks deciden si el arco
continúa.

Lo que la exploración estableció (verificado en el árbol):

- **El lado consumidor ya es agnóstico del backend.** `ITier1Dispatcher`
  (`src/Shumway.Core/ITier1Dispatcher.cs`), el contrato
  `bool PredicateDelegate(Activation, int cursor)`
  (`src/Shumway.Compiler.Il/PredicateDelegate.cs:28`), los resume markers +
  `IlTailCallPending` (camino del intérprete:
  `BytecodeInterpreter.cs:512-617`), y los IL choice points ADR-014
  (`PushIlChoicePoint`, BP=-1, `_ilCpStack`) funcionan igual para cualquier
  productor de delegados. **Cero cambios de intérprete.**
- **El productor no está abstraído**: `Sigil.Emit<PredicateDelegate>` atraviesa
  ~7k líneas. El backend wasm es un fork del emisor, no una retro-abstracción.
- **El ABI real son ~93 llamadas a helpers** (68 `Activation`, 14
  `ArithEvalStack`, ~9 `Cell`); `Cell` es `struct{long}` (mapea a i64),
  `Activation` no marshalea. El obstáculo nombrado por ADR-042 §2: el heap es
  `Cell[]` managed — se resuelve con pinning + `WebAssembly.Memory` compartida.
- **AOT ya tiene plantilla**: `PersistedIlBuilder` + `IlPatchSite` + carga con
  patcheo (`BundleLoader.ApplyIlPatches`) y binding por functor
  (`RegisterBoundDelegate`, instalación incondicional — sirve en browser).
- **Browser**: un segundo módulo puede importar la memoria del runtime .NET
  (compartida, por `WasmEnableThreads`); instanciación síncrona legal en
  workers; tail calls en wasm 3.0 (Chrome 112+/FF 121+/Safari 18.4 — WebShumway
  ya exige browsers modernos por threads). El motor corre en pool threads; el
  interop JS es afín al runtime thread (patrón de referencia:
  `PageInput.cs:58-82`).

**Emisor** (decisión del usuario: híbrido): paquete NuGet `WebAssembly`
(dotnet-webassembly, **Apache-2.0**, activo — 2.1.0 jul-2026, wasm 3.0, cero
deps; su motor de ejecución wasm→IL da tests xUnit sin browser), aislado tras
una interfaz propia para poder internalizar un emisor después.

## Decisiones de diseño (D1–D7)

- **D1 — Camino de llamada: `calli` por índice de tabla, sin JS en el camino
  caliente.** Al instanciar (JS, runtime thread), `Module.addFunction(export)`
  registra la función en la tabla del módulo dotnet y devuelve un índice i32;
  C# la invoca vía `delegate* unmanaged<int,int,int>` desde el pool thread.
  Si esto no anda bajo Mono interp → **No-Go del spike** (el camino por thunk
  JS queda rechazado como producto: afinidad de thread + marshalling por
  llamada).
- **D2 — Contrato de memoria: mailbox + bases pinneadas por entrada.** La vista
  del módulo es (i) la memoria compartida dotnet importada
  (`(import "env" "memory" (memory 0 65536 shared))`) y (ii) un **mailbox**
  `long[]` pinneado (POH) por `Activation`. El wrapper C#, en **cada entrada**,
  dentro de `fixed(Cell* …)` sobre `_heap/_stack/_registers/trails`, escribe
  bases frescas + registros escalares WAM en el mailbox, hace `calli`, y copia
  los escalares de vuelta. El heap solo puede ser reemplazado (crecimiento/GC)
  en código managed, y el managed solo corre cuando el wasm bailó ⇒ bases
  estables por construcción durante cada activación wasm. Esto decide la
  pregunta abierta de ADR-042.
- **D3 — Protocolo de bail: el wasm nunca llama managed.** Export
  `(mailbox: i32, cursor: i32) -> i32 veredicto`: 0=Fail, 1=Success,
  2=SuccessTailCall (Pc del mailbox → `IlTailCallPending`), 3=BuiltinRequest,
  4=PushChoicePoint, 5=Safepoint (watermark GC o palabra de flags de
  wakeups/interrupciones, chequeada en cada back edge). El wrapper **es** el
  `PredicateDelegate`: loop que refresca bases, llama, atiende veredictos 3–5
  (invoca el builtin / `PushIlChoicePoint` / `MaybeCollectHeap`+wakeups) y
  reentra por cursor de continuación (casos extra del `br_table`, mismo
  mecanismo que los cursores de resume del IL).
- **D4 — Choice points por veredicto 4** (no pre-registro): las formas de CP
  demorado (ADR-031) pushean a mitad de cuerpo; el push ya es frontera. Usa el
  `PushIlChoicePoint` existente con el wrapper como delegado.
- **D5 — Emisor aislado**: interfaz propia `IWasmModuleWriter` con una
  implementación sobre dotnet-webassembly. Si a la biblioteca le falta el flag
  `shared` de límites de memoria, se post-patchea ese byte en la sección de
  imports (ubicación binaria bien definida).
- **D6 — Binding de ids: constantes para JIT, globals importadas para AOT.**
  JIT compila en proceso con ids vivos → `i64.const`. AOT importa globals
  inmutables resueltas al instanciar desde una tabla `WasmBindSite[]`
  (Kind/Name/Arity/Cursor, espejo de `IlPatchKind` incl. ResumeMarker) — el
  import object es el mecanismo natural, sin patcheo de bytes.
- **D7 — Capability + trimming**: `RuntimeCaps.SupportsWasmCodegen` con
  `[FeatureSwitchDefinition("Shumway.WasmCodegen")]`, default false; solo
  Shumway.Web lo prende. Desktop recorta `Shumway.Compiler.Wasm` + el paquete,
  simétrico a `Shumway.RuntimeCodegen`. Consultar la propiedad, nunca cachear
  (regla documentada en `RuntimeCaps.cs`).

## Fase 0 — SPIKE Go/No-Go (~1,5-2 semanas)

Un módulo armado a mano para el contador self-tail
(`loop(N) :- N > 0, N1 is N - 1, loop(N1). loop(0).`), solo interop + memoria.

Archivos:
- NUEVO `src/Shumway.Compiler.Wasm/Shumway.Compiler.Wasm.csproj` (net10,
  refs: paquete WebAssembly + Shumway.Core).
- NUEVO `src/Shumway.Compiler.Wasm/WasmAbi.cs` — layout del mailbox (slots con
  nombre) + enum de veredictos. Se reusa tal cual en el backend completo.
- NUEVO `src/Shumway.Compiler.Wasm/SpikeCounterModule.cs` — arma el módulo vía
  la biblioteca: deref de X0 open-coded, test de tag small-int, aritmética i64,
  self-tail como `loop`/`br`, chequeo watermark+flags en el back edge →
  veredicto 5. Variante `BuildForTest(shared: false)` para xUnit.
- NUEVO `src/Shumway.Web/WasmTier.cs` — servicio de instanciación (post al
  `_jsThread` estilo `PageInput`, asíncrono) + wrapper spike que implementa
  `PredicateDelegate` sobre el mailbox.
- NUEVO `src/Shumway.Web/wwwroot/wasmtier.js` — `getDotnetRuntime(0)`,
  `Module.wasmMemory`, `WebAssembly.instantiate` (async), `addFunction`,
  devuelve el índice.
- MODIFICAR `src/Shumway.Web/wwwroot/main.js` (~:1633, junto a `#selftest`) +
  NUEVO `wwwroot/wasmspike.js` — hook `#wasmspike`: contador N=10⁷ Tier-0 vs
  wasm (instalado vía `IlPromotionStore.RegisterBoundDelegate`), mediana de 5,
  tabla.

Mediciones obligatorias: (1) costo de frontera `calli` (módulo degenerado,
10⁶ entradas; y el thunk JS solo como registro comparativo); (2) estabilidad de
bases bajo crecimiento/GC del heap entre reentradas (bail veredicto 5 →
collect → reentrar con bases nuevas); (3) aptitud de la biblioteca (flag
shared; valida e instancia en Chrome y Firefox; la variante no-shared corre en
xUnit con el motor wasm→IL de la biblioteca).

**Criterio Go (numérico): wasm ≥ 2,0× sobre Tier-0-interp en el contador, en
Chrome Y Firefox, con frontera ≤ 1µs por entrada.** Menos que eso en la forma
más amiga posible = el impuesto de frontera/memoria se comió la ganancia →
No-Go, hallazgos a `docs/benchmarks/browser-spike.md`, fin del arco.

## Fase 1 — Backend (~3-4 semanas, condicional a Go)

`WasmPredicateCompiler.Compile(CompiledPredicate, WasmIdSource) → (byte[],
WasmEntry)`, fork del emisor (reusa análisis neutrales: censo `CanCompile`,
clasificación de formas, streams RPN ADR-018).

- **Open-coded en wasm**: deref, tests de tag, box/unbox small-int
  (`i64.load/store` directo), bind + push a los dos trails (bases y cursores en
  mailbox), movimientos X/Y, build/match de estructuras y listas, cut escalar,
  compare/branch, los 14 ops RPN de `ArithEvalStack` sobre small ints como i64
  puro (overflow o operando no-small → bail).
- **Bail**: call/execute a otro predicado (= continuación enhebrada existente:
  `Cp = EncodeResumeMarker(selfFid, cursor)`, Pc, veredicto 2 — el camino de
  markers del intérprete hace el resto), builtins (3), CP push (4), safepoints
  (5), bigint/rational, binding de attvars.
- **Imports wasm: ninguno en v1** (solo memoria + globals de ids).
- **Tabla de tiers de opcodes** (`WasmOpcodeTiers.cs`), partiendo del universo
  de 57: T=traducible, B=bail, R=rechazar predicado (v1: dispatch indexado,
  regiones ITE, dinámicos ADR-023, native blocks — revisar post-benchmark).
  Primer hito: self-tail + head matching + aritmética (cuerpos clase tak/nrev).

Archivos: `WasmPredicateCompiler.cs`, `.Emit.cs`, `WasmOpcodeTiers.cs`,
`IWasmModuleWriter.cs` + `DotnetWebAssemblyWriter.cs`, `WasmIdSource.cs`,
`WasmEntry.cs`/`WasmBindSite.cs`; en Shumway.Web `WasmDelegateFactory.cs`
(loop de veredictos, generaliza el wrapper del spike).

Gate de fase: corpus T verde en xUnit + el contador sigue ≥2× con el compilador
real.

## Fase 2 — JIT (~1,5-2 semanas)

- NUEVO `src/Shumway.Embedding/WasmPromotionStore.cs` — store paralelo (no
  generalizar `IlPromotionStore`, que guarda `PredicateDelegate` y llama al
  compilador IL directo): contadores por functor + threshold + worker de
  compilación en background espejando la forma existente, gateado por
  `SupportsWasmCodegen`. Compila bytes en pool thread, instancia vía `WasmTier`
  (async, runtime thread), instala por el
  `IlPromotionStore.RegisterBoundDelegate` **existente**
  (`IlPromotionStore.cs:541`, instalación incondicional) → reusa el rewrite
  Call→CallIl, `IlByFunctorId` e `ITier1Dispatcher` sin tocar.
- MODIFICAR `src/Shumway.Core/RuntimeCaps.cs` (D7),
  `src/Shumway.Embedding/IlPromotionStore.cs:555` +
  `BundleLoader.cs:639` (`IsPermanentlyBytecodeOnly` debe dar false con
  `SupportsWasmCodegen` — hoy el linker reescribe a `CallBytecode` y elimina el
  dispatch estáticamente en web), `src/Shumway.Web/EngineBoot.cs` (`Tier0Only`)
  y `Shumway.Web.csproj` (switch nuevo en true; `Shumway.RuntimeCodegen` sigue
  false).

Hasta que la instalación completa, el predicado sigue en Tier-0 — misma UX que
el background compile actual.

## Fase 3 — AOT (~1,5-2 semanas)

- MODIFICAR `src/Shumway.Link/LinkCli.cs` — `--with-wasm` (compone con
  `--with-compiled-il`; para target web, wasm reemplaza al IL persistido).
- MODIFICAR `BundleWriter.cs`/`BundleLoader.cs` — sección nueva de bundle
  `{module, WasmEntry[], WasmBindSite[]}` con su magic, espejo del patrón
  `IlPersistedEntryCodec` (NUEVO `WasmPersistedCodec.cs`). Loader web: resolver
  binds → import object → instanciar (runtime thread) →
  `RegisterBoundDelegate` por entry, first-wins, como el binding de IL
  persistido (`BundleLoader.cs:362`). Loaders no-web saltean la sección.

## Fase T — Tests (paralela a 1-3)

- NUEVO `tests/Shumway.Tests.Wasm/` — xUnit desktop sin browser: módulos con
  memoria no compartida ejecutados por el motor wasm→IL de la biblioteca contra
  un harness de mailbox/memoria, + corridas diferenciales vs Tier-0 sobre el
  corpus T.
- Browser: extender `wwwroot/selftest.js` con sección wasm (promover, re-correr
  el corpus del selftest, comparar respuestas).
- Regresión: `Shumway.Tests.Embedding` completa verde con el switch en false
  (default) en todos lados.

## Fase B — Benchmarks + cierre (~1 semana)

Página `#bench` (NUEVO `wwwroot/wasmbench.js`): contador, tak, nrev, crypt,
zebra; Tier-0-interp vs wasm-Tier-1, mediana de N con warmup (metodología de
`docs/benchmarks/analysis.md`); reporte a `docs/benchmarks/browser.md` con la
tabla desktop de referencia. **Gate para "on por default en web": geomean ≥ 2×
en el subset.** ADR nuevo `docs/architecture/adr/050-wasm-tier1-backend.md`
registrando D1–D7 (cumple la política de decisión: backend nuevo + dependencia
nueva ⇒ ADR).

## Registro de riesgos

| Riesgo | Exposición | Mitigación / kill switch |
|---|---|---|
| `calli` a índice de addFunction no viable bajo Mono interp | Mata D1 y el diseño | Medición 1 del spike; No-Go documentado; thunk JS rechazado como producto |
| Semántica de pinning (fixed/POH) de SGen en browser-wasm con threads | Corrupción | Medición 2 del spike, con stress de GC |
| `Cell[]` del heap reemplazado por crecimiento/GC | Bases stale | Estructural: managed solo corre con el wasm bailado; refresh de bases en cada iteración del wrapper; bail por watermark antes de alocar |
| La biblioteca no emite el flag shared | Bloquea instanciación | Post-patch del byte de límites (D5) |
| Costo de instanciación por promoción JIT | Latencia | Instalación async; Tier-0 sigue corriendo; el threshold amortiza |
| Payload (paquete + Compiler.Wasm en el bundle web) | Tiempo de carga | Writer trim-friendly; deploys solo-AOT pueden excluir el emisor |
| Frecuencia de veredictos en predicados builtin-heavy | Se come la ganancia | Tier R los rechaza hasta que el benchmark justifique open-codear más; contadores de bails por forma en el wrapper |
| Wakeups/interrupciones perdidos en loops wasm largos (ADR-049) | Correctitud | Palabra de flags en mailbox chequeada en cada back edge → veredicto 5 |

## Verificación de punta a punta

1. **Spike**: `#wasmspike` en Chrome y Firefox imprime la tabla; criterio
   numérico Go/No-Go; xUnit de la variante no-shared en verde.
2. **Post-fase 1**: corpus T diferencial vs Tier-0 en xUnit; contador ≥2× con
   el compilador real.
3. **Post-fase 2/3**: `#selftest` extendido verde con JIT activo; bundle
   `--with-wasm` bootea WebShumway y promueve desde AOT; Embedding completa
   verde con el switch off.
4. **Cierre**: `#bench` publicado en `docs/benchmarks/browser.md`; geomean ≥2×
   decide el default; ADR-050 escrito.
