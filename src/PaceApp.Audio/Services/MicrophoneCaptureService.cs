using NAudio.CoreAudioApi;
using NAudio.Wave;
using PaceApp.Core.Abstractions;
using PaceApp.Core.Models;

namespace PaceApp.Audio.Services;

public sealed class MicrophoneCaptureService : IMicrophoneCaptureService
{
    private readonly AudioDeviceWatcher deviceWatcher;
    private readonly MMDeviceEnumerator deviceEnumerator = new();
    private readonly object syncRoot = new();

    private WasapiCapture? capture;
    private MMDevice? currentDevice;
    private bool disposed;

    public MicrophoneCaptureService(AudioDeviceWatcher deviceWatcher)
    {
        this.deviceWatcher = deviceWatcher;
        this.deviceWatcher.DefaultCaptureDeviceChanged += OnDefaultCaptureDeviceChanged;
    }

    public event EventHandler<AudioFrame>? AudioFrameCaptured;

    public event EventHandler<CaptureStatusUpdate>? StatusChanged;

    public bool IsRunning { get; private set; }

    public string? CurrentDeviceName { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        lock (syncRoot)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            return RestartCaptureLocked("Monitoring your Teams microphone.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            if (!IsRunning && capture is null)
            {
                return Task.CompletedTask;
            }

            StopCaptureLocked("Monitoring paused.");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        lock (syncRoot)
        {
            disposed = true;
            StopCaptureLocked(null);
            deviceWatcher.DefaultCaptureDeviceChanged -= OnDefaultCaptureDeviceChanged;
            deviceWatcher.Dispose();
            deviceEnumerator.Dispose();
        }
    }

    private void OnDefaultCaptureDeviceChanged(object? sender, string nextDeviceId)
    {
        lock (syncRoot)
        {
            if (!IsRunning)
            {
                return;
            }

            _ = RestartCaptureLocked("Microphone changed. Rebinding to the new device.");
        }
    }

    private Task RestartCaptureLocked(string statusMessage)
    {
        StopCaptureLocked(null);

        try
        {
            currentDevice = GetPreferredDevice();
            if (currentDevice is null)
            {
                RaiseStatus(new CaptureStatusUpdate
                {
                    IsRunning = false,
                    Message = "No recording device is available.",
                });
                return Task.CompletedTask;
            }

            CurrentDeviceName = currentDevice.FriendlyName;
            capture = new WasapiCapture(currentDevice);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            IsRunning = true;
            deviceWatcher.Start();

            RaiseStatus(new CaptureStatusUpdate
            {
                IsRunning = true,
                DeviceName = CurrentDeviceName,
                Message = statusMessage,
            });
        }
        catch (Exception exception)
        {
            StopCaptureLocked(null);
            RaiseStatus(new CaptureStatusUpdate
            {
                IsRunning = false,
                Message = $"Could not start microphone capture. {exception.Message}",
            });
        }

        return Task.CompletedTask;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        var activeCapture = capture;
        if (activeCapture is null)
        {
            return;
        }

        var monoSamples = ConvertToMonoSamples(eventArgs.Buffer, eventArgs.BytesRecorded, activeCapture.WaveFormat);
        if (monoSamples.Length == 0)
        {
            return;
        }

        AudioFrameCaptured?.Invoke(this, new AudioFrame
        {
            Timestamp = DateTimeOffset.UtcNow,
            SampleRate = activeCapture.WaveFormat.SampleRate,
            Samples = monoSamples,
            SignalLevel = CalculateRms(monoSamples),
            DeviceName = CurrentDeviceName ?? "Unknown microphone",
        });
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (disposed || eventArgs.Exception is null)
        {
            return;
        }

        lock (syncRoot)
        {
            StopCaptureLocked(null);
        }

        RaiseStatus(new CaptureStatusUpdate
        {
            IsRunning = false,
            Message = $"Microphone capture stopped unexpectedly. {eventArgs.Exception.Message}",
        });
    }

    private void StopCaptureLocked(string? statusMessage)
    {
        var captureToStop = capture;
        capture = null;

        if (captureToStop is not null)
        {
            captureToStop.DataAvailable -= OnDataAvailable;
            captureToStop.RecordingStopped -= OnRecordingStopped;
            try
            {
                captureToStop.StopRecording();
            }
            catch
            {
            }

            captureToStop.Dispose();
        }

        currentDevice?.Dispose();
        currentDevice = null;
        deviceWatcher.Stop();
        IsRunning = false;
        CurrentDeviceName = null;

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            RaiseStatus(new CaptureStatusUpdate
            {
                IsRunning = false,
                Message = statusMessage,
            });
        }
    }

    private MMDevice? GetPreferredDevice()
    {
        try
        {
            return deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch
        {
            try
            {
                return deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
            catch
            {
                return null;
            }
        }
    }

    private void RaiseStatus(CaptureStatusUpdate update)
    {
        StatusChanged?.Invoke(this, update);
    }

    private static float[] ConvertToMonoSamples(byte[] buffer, int bytesRecorded, WaveFormat waveFormat)
    {
        var bytesPerSample = waveFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0 || waveFormat.Channels <= 0)
        {
            return [];
        }

        var frameSize = bytesPerSample * waveFormat.Channels;
        var frameCount = bytesRecorded / frameSize;
        var mono = new float[frameCount];

        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            double sum = 0;
            for (var channel = 0; channel < waveFormat.Channels; channel++)
            {
                var offset = (frameIndex * frameSize) + (channel * bytesPerSample);
                sum += ReadSample(buffer, offset, waveFormat);
            }

            mono[frameIndex] = (float)(sum / waveFormat.Channels);
        }

        return mono;
    }

    private static double ReadSample(byte[] buffer, int offset, WaveFormat waveFormat)
    {
        return waveFormat.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when waveFormat.BitsPerSample == 32 => BitConverter.ToSingle(buffer, offset),
            WaveFormatEncoding.Pcm when waveFormat.BitsPerSample == 16 => BitConverter.ToInt16(buffer, offset) / 32768d,
            WaveFormatEncoding.Pcm when waveFormat.BitsPerSample == 24 => Read24BitSample(buffer, offset),
            WaveFormatEncoding.Pcm when waveFormat.BitsPerSample == 32 => BitConverter.ToInt32(buffer, offset) / 2147483648d,
            _ => 0d,
        };
    }

    private static double Read24BitSample(byte[] buffer, int offset)
    {
        var sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((sample & 0x800000) != 0)
        {
            sample |= unchecked((int)0xFF000000);
        }

        return sample / 8388608d;
    }

    private static double CalculateRms(IEnumerable<float> samples)
    {
        var sum = 0d;
        var count = 0;

        foreach (var sample in samples)
        {
            sum += sample * sample;
            count++;
        }

        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}