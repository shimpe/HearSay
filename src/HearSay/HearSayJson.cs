using System.Text.Json;
using System.Text.Json.Serialization;

namespace HearSay;

/// <summary>JSON support for every result, option and optimization model of the library, based on System.Text.Json.</summary>
public static class HearSayJson
{
    /// <summary>Indented serializer options: camelCase names, enums as strings, 2D arrays as arrays of rows.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>Compact variant of <see cref="Options"/>.</summary>
    public static JsonSerializerOptions CompactOptions { get; } = Create(indented: false);

    /// <summary>Serializes a model to JSON.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="value">Value to serialize.</param>
    /// <param name="indented">Write indented JSON.</param>
    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : CompactOptions);

    /// <summary>Deserializes a model from JSON.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="json">JSON text.</param>
    /// <exception cref="JsonException">The JSON is invalid or describes null.</exception>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("The JSON document is null.");

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new Matrix2DConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Serializes <c>double[,]</c> as an array of rows.</summary>
    internal sealed class Matrix2DConverter : JsonConverter<double[,]>
    {
        public override double[,] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            double[][] rows = JsonSerializer.Deserialize<double[][]>(ref reader, options) ?? [];
            int columns = rows.Length > 0 ? rows[0].Length : 0;
            var matrix = new double[rows.Length, columns];
            for (int r = 0; r < rows.Length; r++)
            {
                if (rows[r].Length != columns)
                {
                    throw new JsonException("Matrix rows have different lengths.");
                }

                for (int c = 0; c < columns; c++)
                {
                    matrix[r, c] = rows[r][c];
                }
            }

            return matrix;
        }

        public override void Write(Utf8JsonWriter writer, double[,] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            for (int r = 0; r < value.GetLength(0); r++)
            {
                writer.WriteStartArray();
                for (int c = 0; c < value.GetLength(1); c++)
                {
                    writer.WriteNumberValue(value[r, c]);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }
    }
}
