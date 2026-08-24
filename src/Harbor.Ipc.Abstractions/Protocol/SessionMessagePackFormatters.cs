using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Tools;
using MessagePack;
using MessagePack.Formatters;

namespace Harbor.Ipc.Protocol;

/// <summary>
///     MessagePack formatter for <see cref="Session" /> (A1, sprint 5).
///     Domain models are annotated for MemoryPack (the storage path); the IPC
///     wire is MessagePack, so the protocol layer owns this encoding instead
///     of polluting domain records with wire attributes.
/// </summary>
/// <remarks>
///     Field order is a stable contract: both IPC peers ship from the same
///     build, but keep append-only discipline anyway so older payloads stay
///     decodable. <see cref="SessionMetadata" /> has its own formatter;
///     enums encode as their underlying value via StandardResolver.
/// </remarks>
public sealed class SessionMessagePackFormatter : IMessagePackFormatter<Session?>
{
    public void Serialize(
        ref MessagePackWriter writer,
        Session? value,
        MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(14);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Id, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.ProjectId, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Directory, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Title, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Agent, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Model, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.ProviderId, options);

        options.Resolver.GetFormatterWithVerify<DateTimeOffset>()
            .Serialize(ref writer, value.CreatedAt, options);
        options.Resolver.GetFormatterWithVerify<DateTimeOffset>()
            .Serialize(ref writer, value.UpdatedAt, options);

        options.Resolver.GetFormatterWithVerify<SessionMetadata>()
            .Serialize(ref writer, value.Metadata, options);

        options.Resolver.GetFormatterWithVerify<string?>().Serialize(ref writer, value.ParentSessionId, options);
        options.Resolver.GetFormatterWithVerify<SessionStatus>()
            .Serialize(ref writer, value.Status, options);
        options.Resolver.GetFormatterWithVerify<string?>().Serialize(ref writer, value.GitBranch, options);
        writer.Write(value.GitIsDirty);
    }

    public Session? Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        options.Security.DepthStep(ref reader);
        try
        {
            int fieldCount = reader.ReadArrayHeader();
            if (fieldCount != 14)
            {
                throw new MessagePackSerializationException(
                    $"Session payload expects 14 fields, found {fieldCount}.");
            }

            string id = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string projectId = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string directory = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string title = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string agent = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string model = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string providerId = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);

            var createdAt = options.Resolver.GetFormatterWithVerify<DateTimeOffset>()
                .Deserialize(ref reader, options);
            var updatedAt = options.Resolver.GetFormatterWithVerify<DateTimeOffset>()
                .Deserialize(ref reader, options);
            var metadata = options.Resolver.GetFormatterWithVerify<SessionMetadata>()
                .Deserialize(ref reader, options);

            string? parentSessionId = options.Resolver.GetFormatterWithVerify<string?>().Deserialize(ref reader, options);
            var status = options.Resolver.GetFormatterWithVerify<SessionStatus>()
                .Deserialize(ref reader, options);
            string? gitBranch = options.Resolver.GetFormatterWithVerify<string?>().Deserialize(ref reader, options);
            bool gitIsDirty = reader.ReadBoolean();

            return new Session(
                id, projectId, directory, title, agent, model, providerId,
                createdAt, updatedAt, metadata, parentSessionId, status,
                gitBranch, gitIsDirty);
        }
        finally
        {
            reader.Depth--;
        }
    }
}

/// <summary>MessagePack encoding for <see cref="SessionMetadata" /> — see <see cref="SessionMessagePackFormatter" />.</summary>
public sealed class SessionMetadataMessagePackFormatter : IMessagePackFormatter<SessionMetadata?>
{
    public void Serialize(
        ref MessagePackWriter writer,
        SessionMetadata? value,
        MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        writer.WriteArrayHeader(8);
        options.Resolver.GetFormatterWithVerify<decimal>().Serialize(ref writer, value.Cost, options);
        writer.WriteInt32(value.TokensInput);
        writer.WriteInt32(value.TokensOutput);
        writer.WriteInt32(value.TokensReasoning);
        writer.WriteInt32(value.TokensCacheRead);
        writer.WriteInt32(value.TokensCacheWrite);
        writer.WriteInt32(value.MessageCount);

        if (value.TimeCompacting is { } tc)
        {
            writer.Write(true);
            options.Resolver.GetFormatterWithVerify<TimeSpan>().Serialize(ref writer, tc, options);
        }
        else
        {
            writer.Write(false);
        }
    }

    public SessionMetadata? Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        options.Security.DepthStep(ref reader);
        try
        {
            int metaFieldCount = reader.ReadArrayHeader();
            if (metaFieldCount != 8)
            {
                throw new MessagePackSerializationException(
                    $"SessionMetadata payload expects 8 fields, found {metaFieldCount}.");
            }

            decimal cost = options.Resolver.GetFormatterWithVerify<decimal>().Deserialize(ref reader, options);
            int tokensInput = reader.ReadInt32();
            int tokensOutput = reader.ReadInt32();
            int tokensReasoning = reader.ReadInt32();
            int tokensCacheRead = reader.ReadInt32();
            int tokensCacheWrite = reader.ReadInt32();
            int messageCount = reader.ReadInt32();

            TimeSpan? timeCompacting = null;
            if (reader.ReadBoolean())
            {
                timeCompacting = options.Resolver.GetFormatterWithVerify<TimeSpan>()
                    .Deserialize(ref reader, options);
            }

            return new SessionMetadata(
                cost, tokensInput, tokensOutput, tokensReasoning,
                tokensCacheRead, tokensCacheWrite, messageCount, timeCompacting);
        }
        finally
        {
            reader.Depth--;
        }
    }
}

/// <summary>Wire encoding for the <c>ProviderId</c> value object (string wrapper).</summary>
public sealed class ProviderIdMessagePackFormatter : IMessagePackFormatter<ProviderId?>
{
    public void Serialize(ref MessagePackWriter writer, ProviderId? value, MessagePackSerializerOptions options)
    {
        if (value is null) { writer.WriteNil(); return; }
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Value, options);
    }

    public ProviderId? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;
        string raw = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
        return Harbor.Abstractions.Models.Identifiers.ProviderId.Create(raw);
    }
}

/// <summary>Wire encoding for <see cref="ToolDescriptor" /> — schema travels as raw JSON text.</summary>
public sealed class ToolDescriptorMessagePackFormatter : IMessagePackFormatter<ToolDescriptor?>
{
    public void Serialize(ref MessagePackWriter writer, ToolDescriptor? value, MessagePackSerializerOptions options)
    {
        if (value is null) { writer.WriteNil(); return; }

        writer.WriteArrayHeader(7);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Name.Value, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.DisplayName, options);
        options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, value.Description, options);
        options.Resolver.GetFormatterWithVerify<string>()
            .Serialize(ref writer, value.Schema.RootElement.GetRawText(), options);
        writer.WriteInt32((int)value.ExecutionMode);
        options.Resolver.GetFormatterWithVerify<string?>().Serialize(ref writer, value.PromptSnippet, options);

        writer.WriteArrayHeader(value.PromptGuidelines.Count);
        foreach (string guideline in value.PromptGuidelines)
        {
            options.Resolver.GetFormatterWithVerify<string>().Serialize(ref writer, guideline, options);
        }
    }

    public ToolDescriptor? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;

        options.Security.DepthStep(ref reader);
        try
        {
            int fieldCount = reader.ReadArrayHeader();
            if (fieldCount != 7)
            {
                throw new MessagePackSerializationException(
                    $"ToolDescriptor payload expects 7 fields, found {fieldCount}.");
            }

            string name = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string displayName = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string description = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            string schemaJson = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            var executionMode = (ExecutionMode)reader.ReadInt32();
            string? promptSnippet = options.Resolver.GetFormatterWithVerify<string?>().Deserialize(ref reader, options);

            int guidelineCount = reader.ReadArrayHeader();
            var guidelines = new string[guidelineCount];
            for (int i = 0; i < guidelineCount; i++)
            {
                guidelines[i] = options.Resolver.GetFormatterWithVerify<string>().Deserialize(ref reader, options);
            }

            return new ToolDescriptor(
                ToolName.Create(name),
                displayName,
                description,
                System.Text.Json.JsonDocument.Parse(schemaJson),
                executionMode,
                promptSnippet,
                guidelines);
        }
        finally
        {
            reader.Depth--;
        }
    }
}
