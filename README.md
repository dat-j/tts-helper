# tts-helper

`tts-helper.exe` is a tiny, generic text-to-speech helper process: it loads a Piper / VITS voice with
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) and returns raw PCM over standard output.

## License

**GPL-3.0-or-later** — see [LICENSE](LICENSE).

It includes / links:

| Component | License |
|---|---|
| eSpeak NG (statically inside `sherpa-onnx-c-api.dll`; voices ship `espeak-ng-data`) | GPL-3.0 |
| sherpa-onnx (C API + C# wrapper) | Apache-2.0 |
| ONNX Runtime | MIT |

Source code: **https://github.com/dat-j/tts-helper**.

Applications that use this helper (for example VoxBook and SubDub) start it as a separate process and talk to it only
over stdin/stdout pipes — the same arrangement as shipping `ffmpeg.exe` next to an app ("aggregate").
They never load sherpa-onnx or eSpeak NG into their own process.

## Protocol (version 1)

One request at a time. Requests are JSON objects, one per line, UTF-8, on **stdin**:

```json
{"id":1,"cmd":"ping"}
{"id":2,"cmd":"load","voiceDir":"C:\\voices\\vits-piper-en_US-ljspeech-medium","dataDir":"C:\\optional\\espeak-ng-data","threads":2}
{"id":3,"cmd":"synth","text":"Hello world.","speed":1.0,"speakerId":0}
{"id":4,"cmd":"quit"}
```

Every request gets exactly one response on **stdout**: a JSON header line

```json
{"id":3,"ok":true,"sampleRate":22050,"samples":40960,"numSpeakers":1}
```

followed by exactly `samples × 4` bytes of mono float32 little-endian PCM (0 bytes for anything but `synth`).
On failure: `{"id":3,"ok":false,"samples":0,"error":"message"}`.

- `load`: `voiceDir` must contain one `*.onnx` model and `tokens.txt`. Phoneme data is taken from
  `voiceDir/espeak-ng-data`, else `dataDir`, else `espeak-ng-data` next to the exe. `lexicon.txt` / `dict/` in
  `voiceDir` are used when present. `threads` is optional.
- `synth`: `speed` 1.0 = normal (clamped to 0.25–4), `speakerId` for multi-speaker voices.
- `ping`: returns `version`, and `sampleRate` of the loaded voice (0 if none).
- The process exits on `quit` or when stdin is closed. Cancel = kill the process.
- Logs and native library messages go to **stderr** only (the native stdout handle is redirected to stderr at
  startup, before sherpa-onnx is loaded, so stray `printf`s cannot corrupt the binary stream).

`tts-helper.exe --version` prints the version and exits.

## Build

```powershell
dotnet publish shared/dotnet/Mso.TtsHelper -c Release -r win-x64 -p:TtsHelperAot=true -o out   # NativeAOT, no .NET needed
dotnet publish shared/dotnet/Mso.TtsHelper -c Release -r win-x64 --self-contained false -o out  # framework-dependent
```

Apps import `TtsHelper.targets`, which publishes the helper into a `tts\` subfolder of their build/publish output.

## Build

```
dotnet build -c Release            # framework-dependent (needs .NET 10)
powershell -File publish-aot.ps1   # NativeAOT single exe (needs VS C++ build tools)
```
