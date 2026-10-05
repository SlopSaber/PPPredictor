using Newtonsoft.Json;
using PPPredictor.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PPPredictor.Utilities
{
    internal static class ProfileJsonTextWorker
    {
        private const int MinimumCharacters = 64 * 1024;

        internal static string Serialize(object value, Formatting formatting, JsonSerializerSettings settings)
        {
            JsonSerializer serializer = JsonSerializer.CreateDefault(settings);
            serializer.Formatting = formatting;
            var output = new CapturingStringWriter();
            using (var writer = new JsonTextWriter(output))
            {
                writer.Formatting = serializer.Formatting;
                serializer.Serialize(writer, value, null);
            }
            return output.ToString();
        }

        private static string Assemble(object value)
        {
            var input = (OwnedText)value;
            var builder = new StringBuilder(256);
            builder.Append(input.Prefix);
            foreach (TextChunk chunk in input.Chunks)
            {
                if (chunk.Text != null)
                    builder.Append(chunk.Text);
                else if (chunk.Characters != null)
                    builder.Append(chunk.Characters);
                else
                    builder.Append(chunk.Character);
            }
            return builder.ToString();
        }

        private sealed class OwnedText
        {
            internal readonly string Prefix;
            internal readonly TextChunk[] Chunks;

            internal OwnedText(string prefix, TextChunk[] chunks)
            {
                Prefix = prefix;
                Chunks = chunks;
            }
        }

        private readonly struct TextChunk
        {
            internal readonly string Text;
            internal readonly char[] Characters;
            internal readonly char Character;

            internal TextChunk(string text, char[] characters, char character)
            {
                Text = text;
                Characters = characters;
                Character = character;
            }
        }

        private sealed class CapturingStringWriter : StringWriter
        {
            private readonly bool _allowCapture = !Thread.CurrentThread.IsThreadPoolThread;
            private string _prefix;
            private List<TextChunk> _chunks;

            internal CapturingStringWriter()
                : base(new StringBuilder(256), CultureInfo.InvariantCulture)
            {
            }

            private void CaptureIfReady()
            {
                if (_chunks != null || !_allowCapture || GetStringBuilder().Length < MinimumCharacters)
                    return;

                base.Write(string.Empty);
                _prefix = base.ToString();
                _chunks = new List<TextChunk>();
            }

            public override void Write(char value)
            {
                CaptureIfReady();
                if (_chunks == null)
                {
                    base.Write(value);
                    return;
                }

                base.Write(string.Empty);
                _chunks.Add(new TextChunk(null, null, value));
            }

            public override void Write(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    base.Write(value);
                    return;
                }

                CaptureIfReady();
                if (_chunks == null)
                {
                    base.Write(value);
                    return;
                }

                base.Write(string.Empty);
                _chunks.Add(new TextChunk(value, null, '\0'));
            }

            public override void Write(char[] buffer, int index, int count)
            {
                if (buffer == null || index < 0 || count < 0 || buffer.Length - index < count || count == 0)
                {
                    base.Write(buffer, index, count);
                    return;
                }

                CaptureIfReady();
                if (_chunks == null)
                {
                    base.Write(buffer, index, count);
                    return;
                }

                base.Write(string.Empty);
                // The serializer may reuse its character buffer immediately after this call.
                var owned = new char[count];
                Array.Copy(buffer, index, owned, 0, count);
                _chunks.Add(new TextChunk(null, owned, '\0'));
            }

            public override string ToString()
            {
                if (_chunks == null || _chunks.Count == 0)
                    return base.ToString();

                return OwnedNumericWorker.Run(Assemble, new OwnedText(_prefix, _chunks.ToArray()));
            }
        }
    }
}
