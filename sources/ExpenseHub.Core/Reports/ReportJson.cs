using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExpenseHub.Core.Reports;

/// <summary>Serialização única para resultado persistido, mensagens e respostas.</summary>
internal static class ReportJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
    }

    public static string SerializeResult(ReportResult result) => JsonSerializer.Serialize(result, Options);

    public static ReportResult DeserializeResult(string json) =>
        JsonSerializer.Deserialize<ReportResult>(json, Options)
        ?? throw new JsonException("Resultado persistido vazio.");

    public static byte[] SerializeMessage(ReportMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static bool TryParseMessage(ReadOnlySpan<byte> body, out Guid reportId)
    {
        reportId = Guid.Empty;
        try
        {
            ReportMessage? message = JsonSerializer.Deserialize<ReportMessage>(body, Options);
            if (message is null || message.ReportId == Guid.Empty)
            {
                return false;
            }

            reportId = message.ReportId;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        return options;
    }
}
