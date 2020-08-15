namespace Schema.NET
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Reflection;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Xml;

    /// <summary>
    /// Converts a <see cref="IValues"/> object to and from JSON.
    /// </summary>
    /// <seealso cref="JsonConverter" />
    public class ValuesJsonConverter : JsonConverter<IValues>
    {
        private static readonly Dictionary<string, Type> BuiltInThingTypeLookup = new Dictionary<string, Type>(StringComparer.Ordinal);

        static ValuesJsonConverter()
        {
            var thisAssembly = typeof(IThing).Assembly;
            foreach (var type in thisAssembly.ExportedTypes)
            {
                if (type.IsClass && typeof(IThing).IsAssignableFrom(type))
                {
                    BuiltInThingTypeLookup.Add(type.Name, type);
                }
            }
        }

        /// <inheritdoc />
        public override bool CanConvert(Type typeToConvert) => typeof(IValues).IsAssignableFrom(typeToConvert);

        /// <summary>
        /// Reads the JSON representation of the object.
        /// </summary>
        /// <param name="reader">The <see cref="Utf8JsonReader"/> to read from.</param>
        /// <param name="typeToConvert">Type of the object.</param>
        /// <param name="options">The serializer options.</param>
        /// <returns>The object value.</returns>
        public override IValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (typeToConvert is null)
            {
                throw new ArgumentNullException(nameof(typeToConvert));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var dynamicConstructor = FastActivator.GetDynamicConstructor<IEnumerable<object>>(typeToConvert);

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var items = new List<object>();

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }

                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        continue;
                    }

                    var item = ProcessToken(ref reader, typeToConvert.GenericTypeArguments, options);
                    items.Add(item);
                }

                return (IValues)dynamicConstructor(items);
            }
            else if (reader.TokenType != JsonTokenType.Null)
            {
                var item = ProcessToken(ref reader, typeToConvert.GenericTypeArguments, options);
                return (IValues)dynamicConstructor(new[] { item });
            }

            return default;
        }

        /// <summary>
        /// Writes the specified <see cref="IValues"/> object using the JSON writer.
        /// </summary>
        /// <param name="writer">The JSON writer.</param>
        /// <param name="value">The <see cref="IValues"/> object.</param>
        /// <param name="options">The JSON serializer options.</param>
        public override void Write(Utf8JsonWriter writer, IValues value, JsonSerializerOptions options)
        {
            if (writer is null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var values = (IValues)value;
            if (values.Count == 0)
            {
                writer.WriteNullValue();
            }
            else if (values.Count == 1)
            {
                var enumerator = values.GetEnumerator();
                _ = enumerator.MoveNext();
                this.WriteObject(writer, enumerator.Current, options);
            }
            else
            {
                writer.WriteStartArray();
                foreach (var item in values)
                {
                    this.WriteObject(writer, item, options);
                }

                writer.WriteEndArray();
            }
        }

        /// <summary>
        /// Writes the object retrieved from <see cref="IValues"/> when one is found.
        /// </summary>
        /// <param name="writer">The JSON writer.</param>
        /// <param name="value">The value to write.</param>
        /// <param name="options">The JSON serializer options.</param>
        public virtual void WriteObject(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            if (writer is null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            JsonSerializer.Serialize(writer, value, value?.GetType(), options);
        }

        private static object ProcessToken(ref Utf8JsonReader reader, Type[] targetTypes, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                var objectRoot = JsonDocument.ParseValue(ref reader).RootElement;

                // Use the type property (if provided) to identify the correct type
                if (objectRoot.TryGetProperty("@type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String &&
                    TryGetConcreteType(typeElement.GetString(), out var explicitType))
                {
                    for (var i = 0; i < targetTypes.Length; i++)
                    {
                        var targetType = targetTypes[i];
                        if (targetType.IsAssignableFrom(explicitType))
                        {
                            return ProcessObject(objectRoot, explicitType, options);
                        }
                    }
                }

                // Brute force the type by checking against every possible target
                for (var i = targetTypes.Length - 1; i >= 0; i--)
                {
                    var underlyingTargetType = targetTypes[i].GetUnderlyingTypeFromNullable();
                    if (!underlyingTargetType.IsPrimitiveType())
                    {
                        try
                        {
                            // If the target is an interface, attempt to identify concrete target
                            var localTargetType = underlyingTargetType;
                            var typeInfo = localTargetType.GetTypeInfo();
                            if (typeInfo.IsInterface && TryGetConcreteType(typeInfo.Name.Substring(1), out var concreteType))
                            {
                                localTargetType = concreteType;
                            }

                            return ProcessObject(objectRoot, localTargetType, options);
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex.Message);
                        }
#pragma warning restore CA1031 // Do not catch general exception types
                    }
                }
            }
            else
            {
                // Brute force the type by checking against every possible target
                for (var i = targetTypes.Length - 1; i >= 0; i--)
                {
                    var underlyingTargetType = targetTypes[i].GetUnderlyingTypeFromNullable();
                    if (TryProcessTokenAsType(ref reader, underlyingTargetType, out var value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static bool TryProcessTokenAsType(ref Utf8JsonReader reader, Type targetType, out object value)
        {
            var success = false;
            object result = null;

            var tokenType = reader.TokenType;
            if (tokenType == JsonTokenType.String)
            {
                var valueString = reader.GetString();
                if (targetType.GetTypeInfo().IsPrimitive)
                {
                    if (targetType == typeof(int))
                    {
                        success = int.TryParse(valueString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localResult);
                        result = localResult;
                    }
                    else if (targetType == typeof(long))
                    {
                        success = long.TryParse(valueString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var localResult);
                        result = localResult;
                    }
                    else if (targetType == typeof(float))
                    {
                        success = float.TryParse(valueString, NumberStyles.Float, CultureInfo.InvariantCulture, out var localResult);
                        result = localResult;
                    }
                    else if (targetType == typeof(double))
                    {
                        success = double.TryParse(valueString, NumberStyles.Float, CultureInfo.InvariantCulture, out var localResult);
                        result = localResult;
                    }
                    else if (targetType == typeof(bool))
                    {
                        success = bool.TryParse(valueString, out var localResult);
                        result = localResult;
                    }
                }
                else if (targetType == typeof(decimal))
                {
                    success = decimal.TryParse(valueString, NumberStyles.Float, CultureInfo.InvariantCulture, out var localResult);
                    result = localResult;
                }
                else if (targetType.GetTypeInfo().IsEnum)
                {
                    const string SCHEMA_ORG = "http://schema.org/";
                    const int SCHEMA_ORG_LENGTH = 18; // equivalent to "http://schema.org/".Length
                    const string SCHEMA_ORG_HTTPS = "https://schema.org/";
                    const int SCHEMA_ORG_HTTPS_LENGTH = 19; // equivalent to "https://schema.org/".Length

                    string enumString;
                    if (valueString.StartsWith(SCHEMA_ORG, StringComparison.OrdinalIgnoreCase))
                    {
                        enumString = valueString.Substring(SCHEMA_ORG_LENGTH);
                    }
                    else if (valueString.StartsWith(SCHEMA_ORG_HTTPS, StringComparison.OrdinalIgnoreCase))
                    {
                        enumString = valueString.Substring(SCHEMA_ORG_HTTPS_LENGTH);
                    }
                    else
                    {
                        enumString = valueString;
                    }

                    try
                    {
                        result = Enum.Parse(targetType, enumString);
                        success = true;
                    }
#pragma warning disable CA1031 // Do not catch general exception types
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                        success = false;
                    }
#pragma warning restore CA1031 // Do not catch general exception types
                }
                else if (targetType == typeof(DateTime))
                {
                    if (DateTimeHelper.TryParseMSDateTime(valueString, out var localResult))
                    {
                        success = true;
                        result = localResult;
                    }
                    else
                    {
                        success = DateTime.TryParse(valueString, CultureInfo.InvariantCulture, DateTimeStyles.None, out localResult);
                        result = localResult;
                    }
                }
                else if (targetType == typeof(DateTimeOffset))
                {
                    if (DateTimeHelper.TryParseMSDateTimeOffset(valueString, out var localResult))
                    {
                        success = true;
                        result = localResult;
                    }
                    else if (DateTimeHelper.ContainsTimeOffset(valueString))
                    {
                        success = DateTimeOffset.TryParse(valueString, CultureInfo.InvariantCulture, DateTimeStyles.None, out localResult);
                        result = localResult;
                    }
                }
                else if (targetType == typeof(TimeSpan))
                {
                    if (TimeSpan.TryParse(valueString, CultureInfo.InvariantCulture, out var localResult))
                    {
                        success = true;
                        result = localResult;
                    }
                    else
                    {
                        try
                        {
                            result = XmlConvert.ToTimeSpan(valueString);
                            success = true;
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex);
                        }
#pragma warning restore CA1031 // Do not catch general exception types
                    }
                }
                else if (targetType == typeof(Uri))
                {
                    success = Uri.TryCreate(valueString, UriKind.Absolute, out var localResult);
                    result = localResult;
                }
                else if (targetType == typeof(Guid))
                {
                    success = Guid.TryParse(valueString, out var localResult);
                    result = localResult;
                }
            }
            else if (tokenType == JsonTokenType.Number)
            {
                if (targetType == typeof(short) || targetType == typeof(int) || targetType == typeof(long) || targetType == typeof(float) || targetType == typeof(double))
                {
                    result = Convert.ChangeType(reader.GetDecimal(), targetType, CultureInfo.InvariantCulture);
                    success = true;
                }
            }
            else if (tokenType == JsonTokenType.True || tokenType == JsonTokenType.False)
            {
                if (targetType == typeof(bool))
                {
                    result = reader.GetBoolean();
                    success = true;
                }
            }

            value = result;
            return success;
        }

        private static bool TryGetConcreteType(string typeName, out Type type)
        {
            if (BuiltInThingTypeLookup.TryGetValue(typeName, out type))
            {
                return true;
            }
            else
            {
                try
                {
                    var localType = Type.GetType(typeName, false);
                    if (typeof(IThing).IsAssignableFrom(localType.GetTypeInfo()))
                    {
                        type = localType;
                        return !(type is null);
                    }
                    else
                    {
                        type = null;
                        return false;
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                    type = null;
                    return false;
                }
#pragma warning restore CA1031 // Do not catch general exception types
            }
        }

        private static object ProcessObject(JsonElement element, Type objectType, JsonSerializerOptions options)
        {
            // TODO: Investigate avoiding the string allocation
            // Related issue: https://github.com/dotnet/runtime/issues/31274
            var json = element.GetRawText();
            return JsonSerializer.Deserialize(json, objectType, options);
        }
    }
}
