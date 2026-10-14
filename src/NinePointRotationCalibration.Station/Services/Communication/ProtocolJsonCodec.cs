using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Web.Script.Serialization;
using NinePointRotationCalibration.Station.Models;

namespace NinePointRotationCalibration.Station.Services.Communication
{
    [Serializable]
    public sealed class ProtocolSerializationException : SerializationException
    {
        public ProtocolSerializationException(string message)
            : base(message)
        {
        }

        public ProtocolSerializationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        private ProtocolSerializationException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    public sealed class ProtocolJsonCodec
    {
        private const int JsonRecursionLimit = 128;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public byte[] Serialize(ProtocolEnvelope envelope)
        {
            ValidateEnvelope(envelope);

            Dictionary<string, object> wireEnvelope = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "version", envelope.Version },
                { "messageId", envelope.MessageId },
                { "correlationId", envelope.CorrelationId },
                { "type", envelope.Type },
                { "timestampUtc", envelope.TimestampUtc },
                {
                    "data",
                    envelope.Data == null
                        ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        : NormalizeDictionary(
                            envelope.Data,
                            "$.data",
                            StringComparer.OrdinalIgnoreCase,
                            new HashSet<object>(ReferenceEqualityComparer.Instance))
                }
            };

            try
            {
                return StrictUtf8.GetBytes(CreateSerializer().Serialize(wireEnvelope));
            }
            catch (ProtocolSerializationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                throw new ProtocolSerializationException("Protocol envelope could not be serialized.", exception);
            }
        }

        public string SerializeToString(ProtocolEnvelope envelope)
        {
            return StrictUtf8.GetString(Serialize(envelope));
        }

        public ProtocolEnvelope Deserialize(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0) throw new ProtocolSerializationException("Protocol JSON payload is empty.");

            string json;
            try
            {
                json = StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException exception)
            {
                throw new ProtocolSerializationException("Protocol JSON payload is not valid UTF-8.", exception);
            }

            return DeserializeJson(json);
        }

        public ProtocolEnvelope Deserialize(string json)
        {
            if (json == null) throw new ArgumentNullException(nameof(json));
            return DeserializeJson(json);
        }

        private static ProtocolEnvelope DeserializeJson(string json)
        {
            object parsed;
            try
            {
                parsed = CreateSerializer().DeserializeObject(json);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                throw new ProtocolSerializationException("Protocol JSON payload could not be deserialized.", exception);
            }

            IDictionary<string, object> root = parsed as IDictionary<string, object>;
            if (root == null)
            {
                throw new ProtocolSerializationException("Protocol JSON root must be an object.");
            }

            object rawData;
            IDictionary<string, object> data = null;
            if (root.TryGetValue("data", out rawData) && rawData != null)
            {
                data = rawData as IDictionary<string, object>;
                if (data == null)
                {
                    throw new ProtocolSerializationException("Protocol envelope data must be a JSON object.");
                }
            }

            ProtocolEnvelope envelope = new ProtocolEnvelope
            {
                Version = ReadString(root, "version", true),
                MessageId = ReadString(root, "messageId", true),
                CorrelationId = ReadString(root, "correlationId", false),
                Type = ReadString(root, "type", true),
                TimestampUtc = ReadString(root, "timestampUtc", false),
                Data = data == null
                    ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    : NormalizeDictionary(
                        data,
                        "$.data",
                        StringComparer.OrdinalIgnoreCase,
                        new HashSet<object>(ReferenceEqualityComparer.Instance))
            };

            ValidateEnvelope(envelope);
            return envelope;
        }

        private static string ReadString(IDictionary<string, object> source, string key, bool required)
        {
            object value;
            if (!source.TryGetValue(key, out value) || value == null)
            {
                if (required)
                {
                    throw new ProtocolSerializationException("Protocol envelope " + key + " is required.");
                }

                return null;
            }

            string text = value as string;
            if (text == null)
            {
                throw new ProtocolSerializationException("Protocol envelope " + key + " must be a string.");
            }

            return text;
        }

        private static Dictionary<string, object> NormalizeDictionary(
            IDictionary<string, object> source,
            string path,
            IEqualityComparer<string> comparer,
            HashSet<object> activeContainers)
        {
            EnterContainer(source, path, activeContainers);
            try
            {
                Dictionary<string, object> normalized = new Dictionary<string, object>(comparer);
                foreach (KeyValuePair<string, object> entry in source)
                {
                    if (entry.Key == null)
                    {
                        throw new ProtocolSerializationException(path + " contains a null object key.");
                    }

                    if (normalized.ContainsKey(entry.Key))
                    {
                        throw new ProtocolSerializationException(
                            path + " contains duplicate object key '" + entry.Key + "'.");
                    }

                    normalized.Add(
                        entry.Key,
                        NormalizeValue(entry.Value, path + "['" + EscapePathSegment(entry.Key) + "']", activeContainers));
                }

                return normalized;
            }
            finally
            {
                activeContainers.Remove(source);
            }
        }

        private static object NormalizeValue(object value, string path, HashSet<object> activeContainers)
        {
            if (value == null) return null;

            Type valueType = value.GetType();
            if (valueType == typeof(string) || valueType == typeof(bool)) return value;
            if (valueType == typeof(char)) return value.ToString();
            if (IsInteger(valueType) || valueType == typeof(decimal)) return value;

            if (valueType == typeof(float))
            {
                float number = (float)value;
                if (float.IsNaN(number) || float.IsInfinity(number))
                {
                    throw new ProtocolSerializationException(path + " must be a finite JSON number.");
                }

                return value;
            }

            if (valueType == typeof(double))
            {
                double number = (double)value;
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new ProtocolSerializationException(path + " must be a finite JSON number.");
                }

                return value;
            }

            IDictionary<string, object> dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                return NormalizeDictionary(dictionary, path, StringComparer.Ordinal, activeContainers);
            }

            Array array = value as Array;
            if (array != null)
            {
                if (array.Rank != 1)
                {
                    throw new ProtocolSerializationException(path + " must be a one-dimensional array.");
                }

                return NormalizeList(array, path, activeContainers);
            }

            IList list = value as IList;
            if (list != null) return NormalizeList(list, path, activeContainers);

            throw new ProtocolSerializationException(
                path + " has unsupported CLR type '" + valueType.FullName +
                "'. Flatten business objects to primitives, IDictionary<string, object>, or IList before sending.");
        }

        private static List<object> NormalizeList(IList source, string path, HashSet<object> activeContainers)
        {
            EnterContainer(source, path, activeContainers);
            try
            {
                List<object> normalized = new List<object>(source.Count);
                for (int index = 0; index < source.Count; index++)
                {
                    normalized.Add(NormalizeValue(source[index], path + "[" + index + "]", activeContainers));
                }

                return normalized;
            }
            finally
            {
                activeContainers.Remove(source);
            }
        }

        private static bool IsInteger(Type type)
        {
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong);
        }

        private static void EnterContainer(object container, string path, HashSet<object> activeContainers)
        {
            if (!activeContainers.Add(container))
            {
                throw new ProtocolSerializationException(path + " contains a circular container reference.");
            }
        }

        private static string EscapePathSegment(string value)
        {
            return value.Replace("\\", "\\\\").Replace("'", "\\'");
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = JsonRecursionLimit
            };
        }

        private static void ValidateEnvelope(ProtocolEnvelope envelope)
        {
            if (envelope == null) throw new ProtocolSerializationException("Protocol envelope is null.");
            if (string.IsNullOrWhiteSpace(envelope.Version))
            {
                throw new ProtocolSerializationException("Protocol envelope version is required.");
            }

            if (string.IsNullOrWhiteSpace(envelope.MessageId))
            {
                throw new ProtocolSerializationException("Protocol envelope messageId is required.");
            }

            if (string.IsNullOrWhiteSpace(envelope.Type))
            {
                throw new ProtocolSerializationException("Protocol envelope type is required.");
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
