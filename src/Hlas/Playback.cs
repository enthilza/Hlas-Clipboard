using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;

namespace Hlas;

internal static class AudioDecode
{
    internal static float[] Decode(AudioData audio)
    {
        using var memory = new MemoryStream(audio.Bytes);
        using WaveStream reader = audio.Extension == "wav" ? new WaveFileReader(memory) : new Mp3FileReader(memory);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.Channels != 1) throw new InvalidDataException("Nepodporovaný počet zvukových kanálov.");
        if (samples.WaveFormat.SampleRate != 24000) samples = new WdlResamplingSampleProvider(samples, 24000);
        var result = new List<float>(); var buffer = new float[8192]; int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.AsSpan(0, read).ToArray());
        if (result.Count == 0) throw new InvalidDataException("Audio je prázdne.");
        return result.ToArray();
    }
}

internal sealed class AudioQueue : ISampleProvider
{
    readonly ConcurrentQueue<float[]> queue = new();
    readonly Action consumed;
    float[]? current; int offset, part;
    volatile bool finished;
    internal bool Waiting { get; private set; }
    internal int Part => Volatile.Read(ref part);
    internal long FramesRead;
    internal AudioQueue(Action consumed) => this.consumed = consumed;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(24000, 1);
    internal void Enqueue(float[] samples) => queue.Enqueue(samples);
    internal void Finish() => finished = true;
    public int Read(float[] buffer, int start, int count)
    {
        int written = 0; Waiting = false;
        while (written < count) {
            if (current is null) {
                if (!queue.TryDequeue(out current)) {
                    if (finished) return written;
                    Waiting = true; buffer.AsSpan(start + written, count - written).Clear(); return count;
                }
                offset = 0; Interlocked.Increment(ref part);
            }
            int take = Math.Min(count - written, current.Length - offset);
            current.AsSpan(offset, take).CopyTo(buffer.AsSpan(start + written, take)); offset += take; written += take;
            Interlocked.Add(ref FramesRead, take);
            if (offset == current.Length) { current = null; consumed(); }
        }
        return written;
    }
}

internal sealed class TempoSamples : ISampleProvider
{
    readonly ISampleProvider source;
    readonly SoundTouchProcessor processor;
    readonly float[] input = new float[2048];
    bool ended;
    double speed = 1;
    internal double Speed { get => Volatile.Read(ref speed); set => Volatile.Write(ref speed, Math.Clamp(value, .5, 2)); }
    internal TempoSamples(ISampleProvider source)
    {
        this.source = source;
        processor = new SoundTouchProcessor { SampleRate = source.WaveFormat.SampleRate, Channels = 1, Tempo = 1 };
    }
    public WaveFormat WaveFormat => source.WaveFormat;
    public int Read(float[] buffer, int offset, int count)
    {
        processor.Tempo = Speed;
        int written = 0;
        while (written < count) {
            int read = processor.ReceiveSamples(buffer.AsSpan(offset + written, count - written), count - written);
            written += read;
            if (written == count || (ended && read == 0)) break;
            if (read == 0) {
                int n = source.Read(input, 0, input.Length);
                if (n == 0) { processor.Flush(); ended = true; }
                else processor.PutSamples(input.AsSpan(0, n), n);
            }
        }
        return written;
    }
}

internal sealed class ReadingSession : IDisposable
{
    readonly IApi api;
    readonly Settings settings;
    readonly string key;
    readonly SemaphoreSlim slots = new(3, 3);
    readonly CancellationTokenSource cancel = new();
    readonly TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object audioLock = new();
    WaveOutEvent? output;
    TempoSamples? tempo;
    bool paused;
    double speed;
    readonly Func<AudioQueue, Func<bool>, CancellationToken, Task>? testSink;
    internal AudioQueue Queue { get; }
    internal event Action<Bill>? Billed;
    internal event Action? Changed;
    internal string Preparation { get; private set; } = "Načítavam text…";
    internal int Prepared { get; private set; }
    internal bool Started { get; private set; }
    internal bool Paused { get { lock (audioLock) return paused; } }
    internal bool ProductionDone { get; private set; }
    internal ReadingSession(IApi api, Settings settings, string key, Func<AudioQueue, Func<bool>, CancellationToken, Task>? testSink = null)
    {
        this.api = api; this.settings = settings with { }; this.key = key; speed = settings.Speed; this.testSink = testSink;
        Queue = new AudioQueue(() => slots.Release());
    }
    internal void SetSpeed(double value) { lock (audioLock) { speed = value; if (tempo is not null) tempo.Speed = value; } }
    internal void TogglePause()
    {
        lock (audioLock) { paused = !paused; if (paused) output?.Pause(); else output?.Play(); }
        Changed?.Invoke();
    }
    internal void Stop() { cancel.Cancel(); lock (audioLock) output?.Stop(); }
    internal async Task Run(string text)
    {
        var token = cancel.Token; Exception? failure = null;
        async Task Produce()
        {
            try {
                int section = 0;
                foreach (string original in TextChunks.Split(text)) {
                    // Reserve one slot before any paid work; at most three clips are ahead.
                    await slots.WaitAsync(token);
                    section++;
                    Preparation = settings.PrepareText ? $"Upravujem text · časť {section}…" : $"Vytváram audio · časť {section}…"; Changed?.Invoke();
                    string prepared = settings.PrepareText ? await api.Prepare(original, settings, key, b => Billed?.Invoke(b), token) : original;
                    bool reserved = true;
                    foreach (string chunk in TextChunks.Split(prepared, 2000, 2000)) {
                        if (!reserved) await slots.WaitAsync(token); reserved = false;
                        Preparation = $"Vytváram audio · časť {section}…"; Changed?.Invoke();
                        var audio = await api.Speak(chunk, settings, key, token);
                        Billed?.Invoke(audio.Bill);
                        token.ThrowIfCancellationRequested();
                        float[] samples = AudioDecode.Decode(audio);
                        token.ThrowIfCancellationRequested();
                        Queue.Enqueue(samples); Prepared++; first.TrySetResult(); Changed?.Invoke();
                    }
                }
                ProductionDone = true; Preparation = "Celý text je pripravený."; Queue.Finish(); Changed?.Invoke();
                if (Prepared == 0) throw new InvalidOperationException("Text neobsahuje nič na prečítanie.");
            } catch (Exception ex) { failure = ex; first.TrySetCanceled(); cancel.Cancel(); throw; }
        }
        async Task Consume()
        {
            await first.Task.WaitAsync(token);
            if (testSink is not null) { Started = true; await testSink(Queue, () => Paused, token); return; }
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (audioLock) {
                token.ThrowIfCancellationRequested();
                tempo = new TempoSamples(Queue) { Speed = speed };
                output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
                output.PlaybackStopped += (_, e) => { if (e.Exception is not null) completed.TrySetException(e.Exception); else completed.TrySetResult(); };
                output.Init(tempo); Started = true;
                if (!paused) output.Play();
            }
            Changed?.Invoke();
            try { await completed.Task.WaitAsync(token); }
            finally { lock (audioLock) { output?.Stop(); output?.Dispose(); output = null; } }
        }
        var producer = Task.Run(Produce);
        var consumer = Task.Run(async () => { try { await Consume(); } catch (Exception ex) { failure ??= ex; cancel.Cancel(); throw; } });
        try { await Task.WhenAll(producer, consumer); }
        catch { if (failure is not null && failure is not OperationCanceledException && failure is not TaskCanceledException) throw failure; throw new OperationCanceledException(); }
        finally { lock (audioLock) { output?.Stop(); output?.Dispose(); output = null; } }
    }
    public void Dispose() { cancel.Dispose(); slots.Dispose(); }
}
