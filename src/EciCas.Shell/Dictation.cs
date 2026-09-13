using System.IO;
using System.Text;
using System.Windows.Threading;
using EciCas.Core;
using NAudio.Wave;
using Whisper.net;

namespace EciCas.Shell;

/// <summary>
/// The microphone, and what was said into it.
///
/// Push to talk, held: the key opens the microphone, releasing it closes it and
/// the take is transcribed in one pass. Not continuous listening, and not a
/// wake word -- a companion that is always recording is a different product
/// with a different conversation to have about it, and this one is silent until
/// a key is held down.
///
/// Local, on the CPU, in this process. Every other model in this system can be
/// a vendor's if the tier says so; this one cannot, because it is the only
/// input that carries the room. whisper.cpp's base model is roughly 150MB and
/// transcribes a spoken sentence in well under a second on any machine that can
/// run a game, which is the bar that matters here.
///
/// Three things had to be true for a held key to be trustworthy, and each is a
/// guard below: a typed hyphen must not open the microphone (HoldMs), a stuck
/// key must not record forever (MaxSeconds), and silence must not be
/// transcribed into words nobody said (SilenceFloor).
/// </summary>
internal sealed class Dictation : IDisposable
{
    private const int SampleRate = 16000;

    private readonly DictationOptions _options;
    private readonly Lazy<Task<WhisperFactory>> _factory;

    /// <summary>The hold threshold, and the ceiling on one take. Dispatcher
    /// timers because every event this class raises is read by a window: the
    /// only thing that leaves the UI thread is the transcription itself.</summary>
    private readonly DispatcherTimer _arm;
    private readonly DispatcherTimer _limit;

    private WaveIn? _capture;
    private readonly List<float> _samples = [];
    private readonly Lock _gate = new();

    /// <summary>The microphone is open, or it is not. The face draws it.</summary>
    public event Action<bool>? Listening;

    /// <summary>Something was said. Sent as-is, the way the text box sends what
    /// was typed into it.</summary>
    public event Action<string>? Transcribed;

    /// <summary>Nothing was said, or nothing could be: a sentence for the
    /// person to read, never an exception for them to find in a log. This is
    /// the whole difference between a key that does nothing and a key that
    /// tells you why.</summary>
    public event Action<string>? Trouble;

    public Dictation(DictationOptions options)
    {
        _options = options;

        // Loaded once, off the UI thread, on first use rather than at boot:
        // 150MB of weights is a second or two that the watermark should not
        // wait for, and someone who never holds the key never pays it.
        _factory = new Lazy<Task<WhisperFactory>>(() => Task.Run(Load));

        _arm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(0, options.HoldMs)) };
        _arm.Tick += (_, _) => Open();

        _limit = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(1, options.MaxSeconds)) };
        _limit.Tick += (_, _) => _ = CloseAsync();
    }

    /// <summary>The voice key went down. Nothing is recorded yet.</summary>
    public void Press()
    {
        if (!_options.Enabled || _capture is not null) return;
        _arm.Start();
    }

    /// <summary>The voice key came up. A take that never armed is a keystroke,
    /// and a keystroke is not a question.</summary>
    public void Release()
    {
        _arm.Stop();
        _ = CloseAsync();
    }

    private void Open()
    {
        _arm.Stop();
        if (_capture is not null) return;

        if (WaveIn.DeviceCount == 0)
        {
            Trouble?.Invoke("No microphone.");
            return;
        }

        lock (_gate)
        {
            _samples.Clear();
        }

        try
        {
            _capture = new WaveIn
            {
                // What the model wants, asked of the driver rather than
                // resampled afterwards: 16kHz mono is whisper's only input.
                WaveFormat = new WaveFormat(SampleRate, 16, 1),

                // Short buffers so releasing the key ends the take promptly
                // rather than at the end of a long block.
                BufferMilliseconds = 50,
            };
            _capture.DataAvailable += OnAudio;
            _capture.StartRecording();
        }
        catch (Exception failure)
        {
            _capture?.Dispose();
            _capture = null;
            Trouble?.Invoke($"The microphone would not open: {failure.Message}");
            return;
        }

        _limit.Start();
        Listening?.Invoke(true);
    }

    private void OnAudio(object? sender, WaveInEventArgs e)
    {
        // 16-bit PCM to the floats the model reads, on the capture thread, so
        // the take is already in the shape it will be used in.
        lock (_gate)
        {
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                _samples.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
            }
        }
    }

    private async Task CloseAsync()
    {
        _limit.Stop();

        var capture = _capture;
        if (capture is null) return;
        _capture = null;

        capture.DataAvailable -= OnAudio;
        capture.StopRecording();
        capture.Dispose();
        Listening?.Invoke(false);

        float[] take;
        lock (_gate)
        {
            take = [.. _samples];
        }

        // Anything this short is a key held a beat too long, not a sentence.
        if (take.Length < SampleRate / 4) return;

        if (Peak(take) < _options.SilenceFloor)
        {
            Trouble?.Invoke("I did not hear anything.");
            return;
        }

        string text;
        try
        {
            text = await TranscribeAsync(take);
        }
        catch (Exception failure)
        {
            Trouble?.Invoke($"I could not make that out: {failure.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            Trouble?.Invoke("I did not catch that.");
            return;
        }

        Transcribed?.Invoke(text);
    }

    private static double Peak(float[] samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
        }

        return peak;
    }

    private async Task<string> TranscribeAsync(float[] take)
    {
        var factory = await _factory.Value;

        // A processor per take, deliberately: it carries the decoder's state,
        // and one take should not be understood in the light of the last one.
        await using var processor = factory.CreateBuilder()
            .WithLanguage(_options.Language)

            // Half the machine at most. The other half is running whatever the
            // person was doing when they held the key down.
            .WithThreads(Math.Max(1, Environment.ProcessorCount / 2))
            .Build();

        var said = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(take))
        {
            var text = segment.Text.Trim();

            // Whisper narrates what it cannot transcribe: [BLANK_AUDIO], a
            // parenthesised (wind), an asterisked *laughs*. Those are
            // annotations about the recording rather than things the person
            // said, and Perception would take them for an utterance.
            if (text.Length == 0 || Annotation(text))
            {
                continue;
            }

            if (said.Length > 0)
            {
                said.Append(' ');
            }

            said.Append(text);
        }

        return said.ToString().Trim();
    }

    private static bool Annotation(string text) =>
        (text[0] == '[' && text[^1] == ']')
        || (text[0] == '(' && text[^1] == ')')
        || (text[0] == '*' && text[^1] == '*');

    private WhisperFactory Load()
    {
        var path = Model()
            ?? throw new FileNotFoundException(
                $"No speech model at {ModelFile.Resolve(_options.ModelPath)}. " +
                "Run scripts/get-whisper-model.ps1 once.");

        return WhisperFactory.FromPath(path);
    }

    /// <summary>
    /// The configured model, or whichever one is actually there. Downloading a
    /// bigger model is a reasonable thing to do and having to edit a path
    /// afterwards is not, so one ggml file in the configured directory wins
    /// over a configured name that is missing.
    /// </summary>
    private string? Model()
    {
        var configured = ModelFile.Resolve(_options.ModelPath);
        if (File.Exists(configured))
        {
            return configured;
        }

        var directory = Path.GetDirectoryName(configured);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        var found = Directory.GetFiles(directory, "ggml*.bin");
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>Whether the key will do anything, asked without opening
    /// anything to find out -- the tray says so at startup.</summary>
    public bool Ready => _options.Enabled && Model() is not null;

    public void Dispose()
    {
        _arm.Stop();
        _limit.Stop();

        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;

        // Only if it was ever asked for: touching .Value here would load the
        // weights in order to throw them away.
        if (_factory.IsValueCreated && _factory.Value.IsCompletedSuccessfully)
        {
            _factory.Value.Result.Dispose();
        }
    }
}
