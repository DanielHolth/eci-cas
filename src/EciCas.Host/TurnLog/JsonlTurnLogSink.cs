using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EciCas.Host.TurnLog;

/// <summary>
/// Where a settled event goes. One projection, any number of sinks — the
/// SSE surface is not one of these, since it wants records as they change
/// rather than once they are done.
/// </summary>
public interface ITurnLogSink
{
    Task WriteAsync(TurnRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// Appends one JSON object per settled event. This is the readable half of
/// what the console prints, kept on disk deliberately: ArchiveLogger writes
/// the envelope stream without any meta, which is the right shape for an
/// audit trail and useless for reading back what a turn actually said.
///
/// Off unless TurnLog:Path is set. A surface that starts writing files
/// nobody asked for is a surface that gets turned off.
/// </summary>
public sealed class JsonlTurnLogSink : ITurnLogSink
{
    private readonly string _path;
    private readonly ILogger<JsonlTurnLogSink> _logger;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlTurnLogSink(IOptions<TurnLogOptions> options, ILogger<JsonlTurnLogSink> logger, JsonSerializerOptions json)
    {
        _path = options.Value.Path;
        _logger = logger;
        _json = json;
    }

    public async Task WriteAsync(TurnRecord record, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(record, _json);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written is not a turn that failed. The
            // console still has every line this would have held.
            _logger.LogWarning(ex, "Turn log could not be appended to {Path}", _path);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
