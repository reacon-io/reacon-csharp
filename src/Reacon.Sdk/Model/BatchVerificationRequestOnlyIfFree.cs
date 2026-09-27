// Copyright Reacon contributors. Licensed under Apache-2.0.
// Maintained scalar-union adapter for OpenAPI Generator 7.25.0 generichost.
#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reacon.Sdk.Model
{
    /// <summary>A boolean or the literal string "true" or "false". Preserves the JSON scalar type.</summary>
    [JsonConverter(typeof(BatchVerificationRequestOnlyIfFreeJsonConverter))]
    public sealed class BatchVerificationRequestOnlyIfFree
    {
        internal object Value { get; }

        /// <summary>Create a JSON boolean value.</summary>
        public BatchVerificationRequestOnlyIfFree(bool value) { Value = value; }

        /// <summary>Create a JSON string value. Only "true" and "false" are accepted by the API.</summary>
        public BatchVerificationRequestOnlyIfFree(string value)
        {
            if (value != "true" && value != "false")
                throw new ArgumentException("Expected the string true or false.", nameof(value));
            Value = value;
        }

        /// <summary>Convert a boolean without changing its wire type.</summary>
        public static implicit operator BatchVerificationRequestOnlyIfFree(bool value) => new(value);
        /// <summary>Convert an accepted string without changing its wire type.</summary>
        public static implicit operator BatchVerificationRequestOnlyIfFree(string value) => new(value);
    }

    /// <summary>Reads and writes a single scalar, including when nested in a request.</summary>
    public sealed class BatchVerificationRequestOnlyIfFreeJsonConverter : JsonConverter<BatchVerificationRequestOnlyIfFree>
    {
        /// <inheritdoc />
        public override bool HandleNull => true;

        /// <inheritdoc />
        public override BatchVerificationRequestOnlyIfFree Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.True || reader.TokenType == JsonTokenType.False)
                return new(reader.GetBoolean());
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is string value && (value == "true" || value == "false"))
                return new(value);
            throw new JsonException("Expected a boolean or the string true or false.");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, BatchVerificationRequestOnlyIfFree value, JsonSerializerOptions options)
        {
            if (value is null) throw new JsonException("onlyIfFree cannot be null; omit it instead.");
            if (value.Value is bool boolean) writer.WriteBooleanValue(boolean);
            else writer.WriteStringValue((string)value.Value);
        }
    }
}
