# Vosk C# Speech Recognition API Research

## Research Questions

1. NuGet package name and latest version
2. Streaming recognition API in C# (VoskRecognizer, Model classes)
3. Expected audio format (sample rate, channels, bit depth)
4. How to get partial results (hypotheses) and final results
5. JSON format of results (both partial and final)
6. How to get word-level timing from results
7. Where to download vosk-model-small-en-us and its size
8. IDisposable patterns needed

---

## 1. NuGet Package

- **Package name:** `Vosk`
- **Latest version:** `0.3.38`
- **Last updated:** 2022-05-24
- **Target:** .NET Standard 2.0
- **License:** Apache 2.0
- **Install:** `dotnet add package Vosk`
- **NuGet URL:** https://www.nuget.org/packages/Vosk
- **Package size:** 22.95 MB (includes native `libvosk` binaries for win-x64 and linux-x64)
- **Namespace:** `Vosk`

---

## 2. C# Streaming Recognition API

### Key Classes (all in `Vosk` namespace)

#### `Model` : `IDisposable`

```csharp
// Constructor - takes path to extracted model directory
public Model(string model_path);

// Check if word exists in model vocabulary (-1 if not found)
public int FindWord(string word);

// IDisposable
public void Dispose();
```

Source: `csharp/nuget/src/Model.cs`

#### `VoskRecognizer` : `IDisposable`

```csharp
// Constructors
public VoskRecognizer(Model model, float sample_rate);
public VoskRecognizer(Model model, float sample_rate, SpkModel spk_model);
public VoskRecognizer(Model model, float sample_rate, string grammar);

// Configuration
public void SetMaxAlternatives(int max_alternatives);
public void SetWords(bool words);          // Enable word-level timing in final results
public void SetPartialWords(bool partial_words);  // Enable word-level timing in partial results
public void SetSpkModel(SpkModel spk_model);
public void SetEndpointerMode(EndpointerMode mode);
public void SetEndpointerDelays(float t_start_max, float t_end, float t_max);

// Feed audio data - returns true when endpoint (silence) detected = full result available
public bool AcceptWaveform(byte[] data, int len);    // Raw PCM bytes
public bool AcceptWaveform(short[] sdata, int len);  // 16-bit PCM samples
public bool AcceptWaveform(float[] fdata, int len);  // Float samples

// Get results (all return JSON strings)
public string Result();        // Full result after AcceptWaveform returns true
public string PartialResult(); // Partial/interim result (hypothesis in progress)
public string FinalResult();   // Force-flush final result (call at end of stream)

// Reset recognizer state (continue from scratch)
public void Reset();

// IDisposable
public void Dispose();
```

Source: `csharp/nuget/src/VoskRecognizer.cs`

#### `SpkModel` : `IDisposable`

```csharp
public SpkModel(string model_path);
public void Dispose();
```

#### `BatchModel` : `IDisposable`

```csharp
public BatchModel(string model_path);
public void WaitForCompletion();
public void Dispose();
```

#### `VoskBatchRecognizer` : `IDisposable`

For batch/server processing. Not needed for real-time streaming.

#### `EndpointerMode` enum

```csharp
// Maps to VoskEndpointerMode in C API
// DEFAULT = 0, SHORT = 1, LONG = 2, VERY_LONG = 3
```

#### Static utility

```csharp
Vosk.Vosk.SetLogLevel(int level);  // -1 to disable, 0 for info
```

### Native Library

All classes P/Invoke into `libvosk` (native Kaldi-based library). The NuGet package bundles the native DLL for win-x64 and linux-x64.

---

## 3. Expected Audio Format

- **Sample rate:** Typically **16000 Hz** (16 kHz). The `VoskRecognizer` constructor takes a `float sample_rate` so you specify it.
- **Channels:** **1 (mono)**. All examples enforce mono audio.
- **Bit depth / format:**
  - `AcceptWaveform(byte[], int)` — raw PCM 16-bit LE bytes (the standard WAV PCM format)
  - `AcceptWaveform(short[], int)` — 16-bit signed integer samples
  - `AcceptWaveform(float[], int)` — float samples (not normalized to [-1,1]; the demo converts int16 to float directly: `BitConverter.ToInt16(buffer, n)`)
- **Summary:** PCM 16-bit mono 16 kHz is the standard format

From all example code and documentation:
> "Audio file must be WAV format mono PCM."
> "Make sure it has the correct format - PCM 16khz 16bit mono."

---

## 4. Partial Results vs Final Results

### Streaming Loop Pattern

```csharp
using var model = new Model("path/to/model");
using var rec = new VoskRecognizer(model, 16000.0f);
rec.SetWords(true);
rec.SetPartialWords(true);

// Feed audio buffers in a loop
while (haveAudioData)
{
    byte[] buffer = GetNextAudioChunk(); // PCM 16-bit mono 16kHz
    int bytesRead = buffer.Length;

    if (rec.AcceptWaveform(buffer, bytesRead))
    {
        // Endpoint detected (silence) — full result available
        string json = rec.Result();
        // Process finalized segment
    }
    else
    {
        // Still processing — partial/interim hypothesis available
        string json = rec.PartialResult();
        // Display interim text to user
    }
}

// End of stream — flush remaining audio
string finalJson = rec.FinalResult();
```

### Key Semantics

- `AcceptWaveform()` returns **`true`** when Vosk detects an endpoint (silence after speech). Call `Result()` to get the finalized text for that segment.
- `AcceptWaveform()` returns **`false`** while speech is ongoing. Call `PartialResult()` for the current hypothesis — this changes as more audio arrives.
- `FinalResult()` forces the recognizer to process all remaining buffered audio and return a result. Call this at the **end of the stream** to get any leftover recognized text.
- After `Result()` or `FinalResult()` is called, the recognizer resets its internal state for the next utterance. `Reset()` can also be called explicitly.

---

## 5. JSON Format of Results

### Partial Result (from `PartialResult()`)

Without `SetPartialWords(true)`:
```json
{
  "partial": "cyril one eight zero"
}
```

With `SetPartialWords(true)`:
```json
{
  "partial": "cyril one eight zero",
  "partial_result": [
    {
      "conf": 1.000000,
      "end": 1.020000,
      "start": 0.360000,
      "word": "cyril"
    },
    {
      "conf": 1.000000,
      "end": 1.350000,
      "start": 1.020000,
      "word": "one"
    }
  ]
}
```

### Final/Full Result (from `Result()` or `FinalResult()`)

Without `SetWords(true)`:
```json
{
  "text": "what zero zero zero one"
}
```

With `SetWords(true)`:
```json
{
  "text": "what zero zero zero one",
  "result": [
    {
      "conf": 1.000000,
      "end": 0.840000,
      "start": 0.360000,
      "word": "what"
    },
    {
      "conf": 1.000000,
      "end": 1.110000,
      "start": 0.870000,
      "word": "zero"
    },
    {
      "conf": 1.000000,
      "end": 1.530000,
      "start": 1.110000,
      "word": "zero"
    },
    {
      "conf": 1.000000,
      "end": 1.950000,
      "start": 1.530000,
      "word": "zero"
    },
    {
      "conf": 1.000000,
      "end": 2.610000,
      "start": 2.340000,
      "word": "one"
    }
  ]
}
```

With `SetMaxAlternatives(n)` where n > 0:
```json
{
  "alternatives": [
    { "confidence": 0.98, "text": "what zero zero zero one" },
    { "confidence": 0.02, "text": "what zero zero zero won" }
  ]
}
```

### Empty/silence result

```json
{
  "text": ""
}
```

---

## 6. Word-Level Timing

Enable word timing with:
- `rec.SetWords(true)` — adds `"result"` array with per-word timing to `Result()` and `FinalResult()`
- `rec.SetPartialWords(true)` — adds `"partial_result"` array with per-word timing to `PartialResult()`

Each word entry contains:
| Field   | Type   | Description                                    |
|---------|--------|------------------------------------------------|
| `word`  | string | The recognized word                            |
| `start` | float  | Start time in seconds from beginning of stream |
| `end`   | float  | End time in seconds from beginning of stream   |
| `conf`  | float  | Confidence score (0.0 to 1.0)                  |

Times are in **seconds** (floating point) relative to the start of audio fed to the recognizer. Internal frame resolution is 30ms (`0.03` seconds per frame).

Source: `src/recognizer.cc` line 882:
```cpp
word["start"] = samples_round_start_ / sample_frequency_ + (frame_offset_ + times[i].first) * 0.03;
word["end"]   = samples_round_start_ / sample_frequency_ + (frame_offset_ + times[i].second) * 0.03;
```

---

## 7. Model Download

### vosk-model-small-en-us-0.15

- **Size:** ~40 MB (compressed zip), ~50 MB extracted
- **Runtime memory:** ~300 MB
- **WER:** 9.85 (librispeech test-clean), 10.38 (tedlium)
- **Description:** Lightweight wideband model for Android and RPi
- **License:** Apache 2.0
- **Download URL:** https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip

### Usage

Download and extract the zip. Pass the extracted folder path to `new Model("path/to/vosk-model-small-en-us-0.15")`.

### Other English models

| Model | Size | Notes |
|-------|------|-------|
| vosk-model-small-en-us-0.15 | 40M | Lightweight, recommended for desktop |
| vosk-model-en-us-0.22-lgraph | 128M | Better accuracy, dynamic graph |
| vosk-model-en-us-0.22 | 1.8G | High accuracy, server use |

---

## 8. IDisposable Patterns

Both `Model` and `VoskRecognizer` implement `IDisposable` with proper P/Invoke cleanup:

```csharp
// Model.Dispose() calls VoskPINVOKE.delete_Model(handle)
// VoskRecognizer.Dispose() calls VoskPINVOKE.delete_VoskRecognizer(handle)
```

Both also have **finalizers** (`~Model()`, `~VoskRecognizer()`) as safety nets.

### Recommended Pattern

```csharp
using var model = new Model(modelPath);
using var rec = new VoskRecognizer(model, 16000.0f);
rec.SetWords(true);

// ... use rec ...
// Both disposed automatically at end of scope
```

**Important:** The `Model` must outlive all `VoskRecognizer` instances that reference it. The native library uses reference counting — when the last recognizer referring to a model is freed, the model is released. But in practice, keep the C# `Model` alive as long as any recognizer uses it.

`SpkModel` and `BatchModel` also implement `IDisposable` with the same pattern.

---

## Complete C# Real-Time Streaming Example

From the official demo (`csharp/demo/VoskDemo.cs`):

```csharp
using System;
using System.IO;
using Vosk;

public class VoskDemo
{
    public static void DemoBytes(Model model)
    {
        VoskRecognizer rec = new VoskRecognizer(model, 16000.0f);
        rec.SetMaxAlternatives(0);
        rec.SetWords(true);

        using (Stream source = File.OpenRead("test.wav"))
        {
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (rec.AcceptWaveform(buffer, bytesRead))
                    Console.WriteLine(rec.Result());
                else
                    Console.WriteLine(rec.PartialResult());
            }
        }
        Console.WriteLine(rec.FinalResult());
    }

    public static void DemoFloats(Model model)
    {
        VoskRecognizer rec = new VoskRecognizer(model, 16000.0f);
        rec.SetEndpointerMode(EndpointerMode.LONG);

        using (Stream source = File.OpenRead("test.wav"))
        {
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                float[] fbuffer = new float[bytesRead / 2];
                for (int i = 0, n = 0; i < fbuffer.Length; i++, n += 2)
                    fbuffer[i] = BitConverter.ToInt16(buffer, n);

                if (rec.AcceptWaveform(fbuffer, fbuffer.Length))
                    Console.WriteLine(rec.Result());
                else
                    Console.WriteLine(rec.PartialResult());
            }
        }
        Console.WriteLine(rec.FinalResult());
    }

    public static void Main()
    {
        Vosk.Vosk.SetLogLevel(0);
        Model model = new Model("model");
        DemoBytes(model);
        DemoFloats(model);
    }
}
```

---

## Key Implementation Notes for PaceApp

1. **Thread safety:** Each `VoskRecognizer` should run in its own thread. The `Model` can be shared across threads.
2. **Buffer size:** Use 4096 bytes (2048 samples at 16-bit) per chunk — matches the official examples.
3. **NAudio integration:** NAudio's WASAPI capture at 16kHz/16-bit/mono can feed `byte[]` buffers directly to `AcceptWaveform(byte[], int)`. If capture is at a different rate, resample first.
4. **AcceptWaveform overloads:**
   - `byte[]` — raw PCM bytes from WAV/capture (most natural for NAudio)
   - `short[]` — int16 samples
   - `float[]` — float samples (the demo does NOT normalize to [-1,1]; it casts int16 values directly to float)
5. **Reset behavior:** Calling `Result()` or `FinalResult()` resets internal state automatically. Use `Reset()` for explicit mid-stream resets.
6. **No async API:** All methods are synchronous. Run recognition on a background thread.
7. **Model loading is slow:** `new Model(path)` loads the entire model into memory. Do this once at startup.

---

## References

- NuGet page: https://www.nuget.org/packages/Vosk
- GitHub repo: https://github.com/alphacep/vosk-api
- C# wrapper source: https://github.com/alphacep/vosk-api/tree/main/csharp/nuget/src
- C# demo: https://github.com/alphacep/vosk-api/tree/main/csharp/demo
- Models page: https://alphacephei.com/vosk/models
- Install page: https://alphacephei.com/vosk/install
- C API header (authoritative): https://github.com/alphacep/vosk-api/blob/main/src/vosk_api.h
