using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Common.Logging;

namespace NHttp
{
	partial class HttpClient
	{
        private abstract class RequestParser : IDisposable
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

            public abstract void Parse();

            protected void EndParsing()
            {
                // Disconnect the parser.

                Client._parser = null;

                // Resume processing the request.

                Client.ExecuteRequest();
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            protected virtual void Dispose(bool disposing)
            {
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

            public override void Parse()
            {
                Client._readBuffer.CopyToStream(_stream, ContentLength);

                if (_stream.Length == ContentLength)
                {
                    ParseContent();

                    EndParsing();
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

        private class MultiPartParser : RequestParser
        {
            private static readonly ILog Log = LogManager.GetLogger(typeof(MultiPartParser));
            private static readonly byte[] MoreBoundary = Encoding.ASCII.GetBytes("\r\n");
            private static readonly byte[] EndBoundary = Encoding.ASCII.GetBytes("--");

            private Dictionary<string, string> _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private ParserState _state = ParserState.BeforeFirstHeaders;
            private readonly byte[] _firstBoundary;
            private readonly byte[] _separatorBoundary;
            private bool _readingFile;
            private MemoryStream _fieldStream;
            private Stream _fileStream;
            private string _fileName;
            private bool _disposed;

            public MultiPartParser(HttpClient client, int contentLength, string boundary)
                : base(client, contentLength)
            {
                if (boundary == null)
                    throw new ArgumentNullException("boundary");

                _firstBoundary = Encoding.ASCII.GetBytes("--" + boundary + "\r\n");
                _separatorBoundary = Encoding.ASCII.GetBytes("\r\n--" + boundary);

                Client.MultiPartItems = new List<MultiPartItem>();
            }

            public override void Parse()
            {
                switch (_state)
                {
                    case ParserState.BeforeFirstHeaders:
                        ParseFirstHeader();
                        break;

                    case ParserState.ReadingHeaders:
                        ParseHeaders();
                        break;

                    case ParserState.ReadingContent:
                        ParseContent();
                        break;

                    case ParserState.ReadingBoundary:
                        ParseBoundary();
                        break;
                }
            }

            private void ParseFirstHeader()
            {
                bool? atBoundary = Client._readBuffer.AtBoundary(_firstBoundary);

                if (atBoundary.HasValue)
                {
                    if (!atBoundary.Value)
                        throw new ProtocolException("Expected multipart content to start with the boundary");

                    _state = ParserState.ReadingHeaders;

                    ParseHeaders();
                }
            }

            private void ParseHeaders()
            {
                string line;

                while ((line = Client._readBuffer.ReadLine()) != null)
                {
                    string[] parts;

                    if (line.Length == 0)
                    {
                        // Test whether we're reading a file or a field.

                        string contentDispositionHeader;

                        if (!_headers.TryGetValue("Content-Disposition", out contentDispositionHeader))
                            throw new ProtocolException("Expected Content-Disposition header with multipart");

                        parts = contentDispositionHeader.Split(';');

                        _readingFile = false;

                        for (int i = 0; i < parts.Length; i++)
                        {
                            string part = parts[i].Trim();

                            if (part.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                            {
                                _readingFile = true;
                                break;
                            }
                        }

                        // Prepare our state for whether we're reading a file
                        // or a field.

                        if (_readingFile)
                        {
                            _fileName = Path.GetTempFileName();
                            _fileStream = File.Create(_fileName, 4096, FileOptions.DeleteOnClose);
                        }
                        else
                        {
                            if (_fieldStream == null)
                            {
                                _fieldStream = new MemoryStream();
                            }
                            else
                            {
                                _fieldStream.Position = 0;
                                _fieldStream.SetLength(0);
                            }
                        }

                        _state = ParserState.ReadingContent;

                        ParseContent();
                        return;
                    }

                    parts = line.Split(new[] { ':' }, 2);

                    if (parts.Length != 2)
                        throw new ProtocolException("Received header without colon");

                    _headers[parts[0].Trim()] = parts[1].Trim();
                }
            }

            private void ParseContent()
            {
                bool result = Client._readBuffer.CopyToStream(
                    _readingFile ? _fileStream : _fieldStream,
                    ContentLength,
                    _separatorBoundary
                );

                if (result)
                {
                    string value = null;
                    Stream stream = null;

                    if (!_readingFile)
                    {
                        value = Encoding.ASCII.GetString(_fieldStream.ToArray());
                    }
                    else
                    {
                        stream = _fileStream;
                        _fileStream = null;

                        stream.Position = 0;
                    }

                    Client.MultiPartItems.Add(new MultiPartItem(_headers, value, stream));

                    _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    _state = ParserState.ReadingBoundary;

                    ParseBoundary();
                }
            }

            private void ParseBoundary()
            {
                bool? atMore = Client._readBuffer.AtBoundary(MoreBoundary);

                if (atMore.HasValue)
                {
                    if (atMore.Value)
                    {
                        _state = ParserState.ReadingHeaders;

                        ParseHeaders();
                    }
                    else
                    {
                        bool? atEnd = Client._readBuffer.AtBoundary(EndBoundary);

                        // The more and end boundaries have the same length.

                        Debug.Assert(atEnd.HasValue);

                        if (!atEnd.Value)
                            throw new ProtocolException("Unexpected content after boundary");

                        EndParsing();
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    if (_fieldStream != null)
                    {
                        _fieldStream.Dispose();
                        _fieldStream = null;
                    }

                    if (_fileStream != null)
                    {
                        _fileStream.Dispose();
                        _fileStream = null;
                    }

                    _disposed = true;
                }

                base.Dispose(disposing);
            }

            private enum ParserState
            {
                BeforeFirstHeaders,
                ReadingHeaders,
                ReadingContent,
                ReadingBoundary
            }
        }

        public class MultiPartItem
        {
            public MultiPartItem(Dictionary<string, string> headers, string value, Stream stream)
            {
                if (headers == null)
                    throw new ArgumentNullException("headers");

                Headers = headers;
                Value = value;
                Stream = stream;
            }

            public Dictionary<string, string> Headers { get; private set; }

            public string Value { get; private set; }

            public Stream Stream { get; private set; }
        }
	}
}
