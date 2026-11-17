<p align="center"><img src="docs/ufo.svg" width="200" alt="UFO"></p>

# UFO

App para Windows que controla o headset Alienware AW Pro: ANC, microfone, bateria, equalizador e perfis. Tem também o M274, motor de áudio (APO) próprio que processa a saída e o microfone.

## Estrutura

- `src/Ufo` — app WPF
- `src/Ufo.Device` — protocolo HID do headset
- `src/Ufo.Audio` — equalizador e comunicação com o engine
- `src/Ufo.EngineCtl` — instala e remove o engine
- `engine` — DSP e APO em C++
- `tests` — verificação do protocolo
- `prototypes` — protótipos da interface

## Build

Requer .NET 10 SDK e Visual Studio 2022 Build Tools (C++).

```bash
engine\build.cmd
dotnet build UFO.slnx -c Release
```
