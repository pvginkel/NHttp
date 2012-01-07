using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace NHttp
{
	partial class HttpClient
	{
        private abstract class RequestParser
        {
            protected HttpClient Client { get; private set; }
            protected int ContentLength { get; private set; }

            protected RequestParser(HttpClient client, int contentLength)
            {
                if (client == null)
                    throw new ArgumentNullException("client");

                Client = client;
                ContentLength = contentLength;
            }

            public abstract void Parse(ref int offset, int available);

            protected void EndParsing(int offset, int available)
            {
                Debug.Assert(offset == available);

                // Disconnect the parser.

                Client._parser = null;

                // Resume processing the request.

                Client.ExecuteRequest();
            }
        }

        private class UrlEncodedParser : RequestParser
        {
            private readonly MemoryStream _stream;

            public UrlEncodedParser(HttpClient client, int contentLength)
                : base(client, contentLength)
            {
                _stream = new MemoryStream();
            }

            public override void Parse(ref int offset, int available)
            {
                int toRead = Math.Min(
                    available - offset,
                    ContentLength - (int)_stream.Length
                );

                _stream.Write(Client._readBuffer, offset, toRead);

                offset += toRead;

                if (_stream.Length == ContentLength)
                {
                    ParseContent();

                    EndParsing(offset, available);
                }
                else
                {
                    Debug.Assert(offset == available);
                }
            }

            private void ParseContent()
            {
                _stream.Position = 0;

                string content;

                using (var reader = new StreamReader(_stream, Encoding.ASCII))
                {
                    content = reader.ReadToEnd();
                }

                Client.PostParameters = HttpUtil.UrlDecode(content);
            }
        }
	}
}
