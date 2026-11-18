using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;

namespace Harbor.Abstractions.Dsl;

/// <summary>
///     Wire codec for a mutation batch. It writes and reads the ordered
///     <c>[op, ...args]</c> tuples with <see cref="Utf8JsonWriter"/> and
///     <see cref="Utf8JsonReader"/> only. No reflection, no source-generated
///     serializer and no markup parser live here; the parser from #2489 reads
///     <c>.harbor-ui</c> text, this codec reads the finished batch.
/// </summary>
internal static class MutationBatchCodec
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    /// <summary>Serializes a batch to UTF-8 JSON.</summary>
    internal static byte[] Write(MutationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var command in batch.Commands)
                WriteCommand(writer, command);
            writer.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    ///     Parses a batch from UTF-8 JSON. Unknown operations, missing
    ///     arguments and extra trailing data all fail the whole batch.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> utf8, out MutationBatch batch, out string error)
    {
        batch = new MutationBatch([]);
        error = string.Empty;
        try
        {
            var reader = new Utf8JsonReader(utf8, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                error = "expected a batch array";
                return false;
            }

            var commands = new List<MutationCommand>();
            while (true)
            {
                if (!reader.Read())
                {
                    error = "unexpected end of batch";
                    return false;
                }

                if (reader.TokenType == JsonTokenType.EndArray)
                    break;
                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    error = "expected a command array";
                    return false;
                }

                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    error = "expected an operation name";
                    return false;
                }

                string operation = reader.GetString()!;
                if (!ReadCommand(ref reader, operation, out var command, out error))
                    return false;
                commands.Add(command);

                if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                {
                    error = "expected the end of a command";
                    return false;
                }
            }

            if (reader.Read())
            {
                error = "trailing data after the batch";
                return false;
            }

            batch = new MutationBatch(commands);
            return true;
        }
        catch (JsonException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void WriteCommand(Utf8JsonWriter writer, in MutationCommand command)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(OperationName(command.Op));
        switch (command.Op)
        {
            case MutationOp.CreateElement:
                writer.WriteNumberValue(command.Id);
                writer.WriteNumberValue((int)command.Kind);
                break;
            case MutationOp.SetStyle:
                writer.WriteNumberValue(command.Id);
                writer.WriteStartArray();
                for (int i = 0; i < command.Style!.Count; i++)
                {
                    var property = command.Style[i];
                    writer.WriteStartArray();
                    writer.WriteStringValue(property.Name);
                    writer.WriteStringValue(property.Value);
                    writer.WriteEndArray();
                }

                writer.WriteEndArray();
                break;
            case MutationOp.SetText:
                writer.WriteNumberValue(command.Id);
                writer.WriteStringValue(command.Text);
                break;
            case MutationOp.AppendChild:
                writer.WriteNumberValue(command.Id);
                writer.WriteNumberValue(command.SecondaryId);
                break;
            case MutationOp.InsertBefore:
                writer.WriteNumberValue(command.Id);
                writer.WriteNumberValue(command.SecondaryId);
                writer.WriteNumberValue(command.ExtraId);
                break;
            case MutationOp.SetEventListener:
                writer.WriteNumberValue(command.Id);
                writer.WriteStringValue(command.EventType);
                writer.WriteBooleanValue(command.Enabled);
                break;
            case MutationOp.SetRoot:
            case MutationOp.DestroyElement:
                writer.WriteNumberValue(command.Id);
                break;
        }

        writer.WriteEndArray();
    }

    private static bool ReadCommand(
        ref Utf8JsonReader reader,
        string operation,
        out MutationCommand command,
        out string error)
    {
        command = default;
        switch (operation)
        {
            case "createElement":
                if (!ReadInt(ref reader, out int created, out error))
                    return false;
                if (!ReadInt(ref reader, out int kindValue, out error))
                    return false;
                if (kindValue < 0 || kindValue > (int)SpecKind.VirtualList)
                {
                    error = "unknown element kind";
                    return false;
                }

                command = MutationCommand.CreateElement(created, (SpecKind)kindValue);
                return true;

            case "setStyle":
                if (!ReadInt(ref reader, out int styled, out error))
                    return false;
                if (!ReadStyle(ref reader, out var style, out error))
                    return false;
                command = MutationCommand.SetStyle(styled, style);
                return true;

            case "setText":
                if (!ReadInt(ref reader, out int textId, out error))
                    return false;
                if (!ReadString(ref reader, out var text, out error))
                    return false;
                command = MutationCommand.SetText(textId, text);
                return true;

            case "appendChild":
                if (!ReadInt(ref reader, out int parent, out error))
                    return false;
                if (!ReadInt(ref reader, out int child, out error))
                    return false;
                command = MutationCommand.AppendChild(parent, child);
                return true;

            case "insertBefore":
                if (!ReadInt(ref reader, out int insertParent, out error))
                    return false;
                if (!ReadInt(ref reader, out int insertChild, out error))
                    return false;
                if (!ReadInt(ref reader, out int before, out error))
                    return false;
                command = MutationCommand.InsertBefore(insertParent, insertChild, before);
                return true;

            case "setEventListener":
                if (!ReadInt(ref reader, out int listenerId, out error))
                    return false;
                if (!ReadString(ref reader, out var eventType, out error))
                    return false;
                if (!reader.Read() || (reader.TokenType != JsonTokenType.True && reader.TokenType != JsonTokenType.False))
                {
                    error = "expected a boolean";
                    return false;
                }

                command = MutationCommand.SetEventListener(listenerId, eventType, reader.TokenType == JsonTokenType.True);
                return true;

            case "setRoot":
                if (!ReadInt(ref reader, out int root, out error))
                    return false;
                command = MutationCommand.SetRoot(root);
                return true;

            case "destroyElement":
                if (!ReadInt(ref reader, out int destroyed, out error))
                    return false;
                command = MutationCommand.DestroyElement(destroyed);
                return true;

            default:
                error = $"unknown operation '{operation}'";
                return false;
        }
    }

    private static bool ReadStyle(
        ref Utf8JsonReader reader,
        out MutationStyle style,
        out string error)
    {
        style = MutationStyle.Empty;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
        {
            error = "expected a style array";
            return false;
        }

        var properties = new List<(string Name, string Value)>();
        while (true)
        {
            if (!reader.Read())
            {
                error = "unexpected end of style";
                return false;
            }

            if (reader.TokenType == JsonTokenType.EndArray)
                break;
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                error = "expected a style property";
                return false;
            }

            if (!ReadString(ref reader, out var name, out error))
                return false;
            if (!ReadString(ref reader, out var value, out error))
                return false;
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
            {
                error = "expected the end of a style property";
                return false;
            }

            properties.Add((name, value));
        }

        style = MutationStyle.Create(properties.ToArray());
        error = string.Empty;
        return true;
    }

    private static bool ReadInt(ref Utf8JsonReader reader, out int value, out string error)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out value))
        {
            value = 0;
            error = "expected an integer id";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool ReadString(ref Utf8JsonReader reader, out string value, out string error)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
        {
            value = string.Empty;
            error = "expected a string";
            return false;
        }

        value = reader.GetString()!;
        error = string.Empty;
        return true;
    }

    private static string OperationName(MutationOp operation) => operation switch
    {
        MutationOp.CreateElement => "createElement",
        MutationOp.SetStyle => "setStyle",
        MutationOp.SetText => "setText",
        MutationOp.AppendChild => "appendChild",
        MutationOp.InsertBefore => "insertBefore",
        MutationOp.SetEventListener => "setEventListener",
        MutationOp.SetRoot => "setRoot",
        MutationOp.DestroyElement => "destroyElement",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
