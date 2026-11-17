<p align="center"><img src="docs/ufo.svg" width="200" alt="UFO"></p>

# UFO

App para Windows que controla o headset Alienware AW Pro: ANC, microfone, bateria, equalizador, perfis e clips da tela. Tem também o M274, motor de áudio (APO) próprio que processa a saída e o microfone.

## Estrutura

- `src/Ufo` — app WPF
- `src/Ufo.Device` — protocolo HID do headset
- `src/Ufo.Audio` — equalizador e comunicação com o M274
- `src/Ufo.Clips` — clips: replay da tela e do som, gatilhos e galeria
- `src/Ufo.M274Ctl` — instala e remove o M274
- `m274` — M274: DSP e APO em C++
- `tests` — verificação do protocolo
- `prototypes` — protótipos da interface

## Build

Requer .NET 10 SDK e Visual Studio 2022 Build Tools (C++).

```bash
m274\build.cmd
dotnet build UFO.slnx -c Release
```

## Clips

A lua **clips** guarda os últimos momentos da tela e do som (15 s a 10 min) e os transforma em MP4 por atalho global, ao rir ou gritar no microfone, ou por gravação manual. Os clips ficam em `Vídeos\UFO\<jogo>`.

A captura é feita por um `ffmpeg` filho, toda na placa de vídeo (Windows Graphics Capture + encoder de hardware), em segmentos de 1 s numa pasta temporária; salvar um clip só junta os últimos segmentos, sem recodificar.

O `ffmpeg` (8 ou mais novo, com `gfxcapture`) é procurado em `ffmpeg\ffmpeg.exe` ao lado do app e, na falta, no `PATH`. Para empacotar, coloque o executável em `tools\ffmpeg\ffmpeg.exe` antes do build.

A risada é reconhecida no próprio computador pelo YAMNet (classificador de sons do Google). O modelo não fica no git: gere `tools\yamnet\yamnet.onnx` uma vez, a partir do modelo oficial, antes do build:

```bash
curl -L -o yamnet.tar.gz "https://tfhub.dev/google/yamnet/1?tf-hub-format=compressed"
mkdir yamnet-model && tar -xzf yamnet.tar.gz -C yamnet-model
pip install tensorflow==2.16.2 tf2onnx==1.16.1 numpy==1.26.4
python -m tf2onnx.convert --saved-model yamnet-model --output tools/yamnet/yamnet.onnx --opset 15
dotnet run --project tests/Ufo.Device.Check -- --laughter tools/yamnet/yamnet.onnx
```

Teste ao vivo do gravador (grava a tela por 8 s e salva 5 s):

```bash
dotnet run --project tests/Ufo.Device.Check -- --clip saida.mp4
```
