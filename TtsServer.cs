// tts-helper — Copyright (C) 2026 dooxx — GPL-3.0-or-later (see LICENSE).

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SherpaOnnx;

namespace Mso.TtsHelper;

/// <summary>
/// Handles one JSON request line, writes one response (JSON header line + float32 LE PCM) to the protocol stream.
/// Holds at most one loaded voice.
/// </summary>
internal sealed class TtsServer(Stream output) : IDisposable
{
    private readonly BufferedStream _out = new(output, 1 << 16);
    private OfflineTts? _tts;

    /// <returns>false = quit.</returns>
    public bool Handle(string line)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number) id = idEl.GetInt64();
            var cmd = Str(root, "cmd") ?? "";
            switch (cmd)
            {
                case "ping":
                    Respond(id, true, _tts?.SampleRate ?? 0, ReadOnlySpan<float>.Empty, null, _tts?.NumSpeakers ?? 0, Program.Version);
                    return true;
                case "load":
                    Load(root);
                    Respond(id, true, _tts!.SampleRate, ReadOnlySpan<float>.Empty, null, _tts.NumSpeakers);
                    return true;
                case "synth":
                    Synth(id, root);
                    return true;
                case "quit":
                    Respond(id, true, 0, ReadOnlySpan<float>.Empty, null);
                    return false;
                default:
                    Respond(id, false, 0, ReadOnlySpan<float>.Empty, $"unknown command '{cmd}'");
                    return true;
            }
        }
        catch (IOException) { throw; }   // protocol pipe broken → exit
        catch (Exception ex)
        {
            Program.Log("error: " + ex);
            Respond(id, false, 0, ReadOnlySpan<float>.Empty, ex.Message);
            return true;
        }
    }

    private void Load(JsonElement root)
    {
        var voiceDir = Str(root, "voiceDir");
        if (string.IsNullOrWhiteSpace(voiceDir) || !Directory.Exists(voiceDir))
            throw new ArgumentException($"voice folder not found: {voiceDir}");
        var model = Str(root, "model");
        if (string.IsNullOrEmpty(model))
        {
            var models = Directory.EnumerateFiles(voiceDir)
                .Where(f => string.Equals(Path.GetExtension(f), ".onnx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (models.Count == 0) throw new ArgumentException("no .onnx model in the voice folder");
            if (models.Count > 1) throw new ArgumentException("more than one .onnx model in the voice folder");
            model = models[0];
        }
        var tokens = Path.Combine(voiceDir, "tokens.txt");
        if (!File.Exists(tokens)) throw new ArgumentException("tokens.txt not found in the voice folder");

        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = model;
        config.Model.Vits.Tokens = tokens;
        var dataDir = FirstExisting(
            Path.Combine(voiceDir, "espeak-ng-data"),
            Str(root, "dataDir"),
            Path.Combine(AppContext.BaseDirectory, "espeak-ng-data"));
        if (dataDir is not null) config.Model.Vits.DataDir = dataDir;
        var lexicon = Path.Combine(voiceDir, "lexicon.txt");
        if (File.Exists(lexicon)) config.Model.Vits.Lexicon = lexicon;
        var dict = Path.Combine(voiceDir, "dict");
        if (Directory.Exists(dict)) config.Model.Vits.DictDir = dict;
        if (dataDir is null && !File.Exists(lexicon))
            throw new ArgumentException("espeak-ng-data not found (needed by Piper voices)");

        int threads = root.TryGetProperty("threads", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        config.Model.NumThreads = threads > 0 ? Math.Min(threads, 16) : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        config.MaxNumSentences = 1;

        _tts?.Dispose();
        _tts = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tts = new OfflineTts(config);
        if (tts.SampleRate <= 0) throw new InvalidOperationException("the voice model could not be loaded");
        _tts = tts;
        Program.Log($"loaded {Path.GetFileName(voiceDir)} sr={tts.SampleRate} speakers={tts.NumSpeakers} in {sw.Elapsed.TotalSeconds:0.00}s");
    }

    private void Synth(long id, JsonElement root)
    {
        if (_tts is null) throw new InvalidOperationException("no voice loaded");
        var text = Str(root, "text") ?? "";
        float speed = root.TryGetProperty("speed", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetSingle() : 1f;
        if (!float.IsFinite(speed) || speed <= 0) speed = 1f;
        speed = Math.Clamp(speed, 0.25f, 4f);
        int sid = root.TryGetProperty("speakerId", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : 0;
        if (sid < 0 || (_tts.NumSpeakers > 0 && sid >= _tts.NumSpeakers)) sid = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            Respond(id, true, _tts.SampleRate, ReadOnlySpan<float>.Empty, null);
            return;
        }
        var audio = _tts.Generate(text, speed, sid);
        Respond(id, true, audio.SampleRate, audio.Samples, null);
    }

    private void Respond(long id, bool ok, int sampleRate, ReadOnlySpan<float> samples, string? error, int numSpeakers = -1,
        string? version = null)
    {
        var header = new MemoryStream(256);
        using (var w = new Utf8JsonWriter(header))
        {
            w.WriteStartObject();
            w.WriteNumber("id", id);
            w.WriteBoolean("ok", ok);
            w.WriteNumber("sampleRate", sampleRate);
            w.WriteNumber("samples", samples.Length);
            if (numSpeakers >= 0) w.WriteNumber("numSpeakers", numSpeakers);
            if (version is not null)
            {
                w.WriteString("version", version);
                w.WriteNumber("protocol", Program.ProtocolVersion);
            }
            if (error is not null) w.WriteString("error", error);
            w.WriteEndObject();
        }
        header.WriteByte((byte)'\n');
        _out.Write(header.GetBuffer(), 0, (int)header.Length);
        if (!samples.IsEmpty)
        {
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("big-endian");
            _out.Write(MemoryMarshal.AsBytes(samples));
        }
        _out.Flush();
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string? FirstExisting(params string?[] dirs) =>
        dirs.FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d));

    public void Dispose()
    {
        _tts?.Dispose();
        try { _out.Flush(); } catch (IOException) { }
    }
}
