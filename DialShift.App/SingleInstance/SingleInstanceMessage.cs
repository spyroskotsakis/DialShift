using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DialShift.App.SingleInstance;

/// <summary>
/// The v1 single-instance wire message (acceptance matrix §8.2.4): one UTF-8 JSON line per connection,
/// e.g. <c>{"version":1,"command":"activate"}\n</c>. Validation happens before any action is taken.
/// </summary>
public sealed record SingleInstanceMessage(int Version, string Command)
{
    public const int CurrentVersion = 1;
    public const string ActivateCommand = "activate";
    public const int MaxMessageBytes = 4096;

    public static SingleInstanceMessage Activate { get; } = new(CurrentVersion, ActivateCommand);

    /// <summary>UTF-8 JSON + '\n', e.g. {"version":1,"command":"activate"}\n</summary>
    public byte[] ToUtf8Line()
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("command", Command);
            writer.WriteEndObject();
        }
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Strict: object with exactly "version" (== 1) and "command" (== "activate"); size ≤ MaxMessageBytes.</summary>
    /// <remarks>
    /// A single trailing '\n' is accepted and not counted toward <see cref="MaxMessageBytes"/>; any other newline
    /// is rejected because a message is exactly one line. Property names
    /// and the command are case-sensitive; duplicate, missing or extra properties, comments and trailing
    /// content are rejected.
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> utf8Line, [NotNullWhen(true)] out SingleInstanceMessage? message)
    {
        message = null;
        if (!utf8Line.IsEmpty && utf8Line[^1] == (byte)'\n') utf8Line = utf8Line[..^1];
        if (utf8Line.IsEmpty || utf8Line.Length > MaxMessageBytes || utf8Line.Contains((byte)'\n')) return false;

        try
        {
            var reader = new Utf8JsonReader(utf8Line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            int? version = null;
            string? command = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                if (reader.ValueTextEquals("version"u8))
                {
                    if (version != null || !reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var v)) return false;
                    version = v;
                }
                else if (reader.ValueTextEquals("command"u8))
                {
                    if (command != null || !reader.Read() || reader.TokenType != JsonTokenType.String) return false;
                    command = reader.GetString();
                }
                else return false;
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            if (version != CurrentVersion || command != ActivateCommand) return false;

            message = Activate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
