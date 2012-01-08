using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Common.Logging;

namespace NHttp
{
    internal partial class HttpClient : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HttpClient));

        private static readonly Regex PrologRegex = new Regex("^(GET|POST) ([^ ]+) (HTTP/[^ ]+)$");

        private bool _disposed;
        private readonly ReadBuffer _readBuffer;
        private readonly byte[] _writeBuffer;
        private NetworkStream _stream;
        private ClientState _state;
        private MemoryStream _writeStream;
        private RequestParser _parser;
        private HttpContext _context;

        public HttpServer Server { get; private set; }

        public TcpClient TcpClient { get; private set; }

        public string Method { get; private set; }

        public string Protocol { get; private set; }

        public string Request { get; private set; }

        public Dictionary<string, string> Headers { get; private set; }

        public Dictionary<string, string> PostParameters { get; private set; }

        public List<MultiPartItem> MultiPartItems { get; private set; }

        public HttpClient(HttpServer server, TcpClient client)
        {
            if (server == null)
                throw new ArgumentNullException("server");
            if (client == null)
                throw new ArgumentNullException("client");

            Server = server;
            TcpClient = client;

            _readBuffer = new ReadBuffer(server.ReadBufferSize);
            _writeBuffer = new byte[server.WriteBufferSize];

            _stream = client.GetStream();
        }

        private void Reset()
        {
            _state = ClientState.ReadingProlog;
            _context = null;

            if (_parser != null)
            {
                _parser.Dispose();
                _parser = null;
            }

            if (_writeStream != null)
            {
                _writeStream.Dispose();
                _writeStream = null;
            }

            _readBuffer.Reset();

            Method = null;
            Protocol = null;
            Request = null;
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PostParameters = null;

            if (MultiPartItems != null)
            {
                foreach (var item in MultiPartItems)
                {
                    if (item.Stream != null)
                        item.Stream.Dispose();
                }

                MultiPartItems = null;
            }
        }

        public void BeginRequest()
        {
            Reset();

            BeginRead();
        }

        private void BeginRead()
        {
            if (_disposed)
                return;

            try
            {
                _readBuffer.BeginRead(_stream, ReadCallback, null);
            }
            catch (Exception ex)
            {
                Log.Warn("BeginRead failed", ex);

                Dispose();
            }
        }

        private void ReadCallback(IAsyncResult asyncResult)
        {
            if (_disposed)
                return;

            try
            {
                _readBuffer.EndRead(_stream, asyncResult);

                ProcessReadBuffer();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to read from the HTTP connection", ex);

                Dispose();
            }
        }

        private void ProcessReadBuffer()
        {
            while (_writeStream == null && _readBuffer.DataAvailable)
            {
                switch (_state)
                {
                    case ClientState.ReadingProlog:
                        ProcessProlog();
                        break;

                    case ClientState.ReadingHeaders:
                        ProcessHeaders();
                        break;

                    case ClientState.ReadingContent:
                        ProcessContent();
                        break;
                }
            }

            if (_writeStream == null)
                BeginRead();
        }

        private void ProcessProlog()
        {
            string line = _readBuffer.ReadLine();

            if (line == null)
                return;

            // Parse the prolog.

            var match = PrologRegex.Match(line);

            if (!match.Success)
                throw new ProtocolException(String.Format("Could not parse prolog '{0}'", line));

            Method = match.Groups[1].Value;
            Request = match.Groups[2].Value;
            Protocol = match.Groups[3].Value;

            // Continue reading the headers.

            _state = ClientState.ReadingHeaders;

            ProcessHeaders();
        }

        private void ProcessHeaders()
        {
            string line;

            while ((line = _readBuffer.ReadLine()) != null)
            {
                // Have we completed receiving the headers?

                if (line.Length == 0)
                {
                    // Reset the read buffer which resets the bytes read.

                    _readBuffer.Reset();

                    // Start processing the body of the request.

                    _state = ClientState.ReadingContent;

                    ProcessContent();

                    return;
                }

                string[] parts = line.Split(new[] { ':' }, 2);

                if (parts.Length != 2)
                    throw new ProtocolException("Received header without colon");

                Headers[parts[0].Trim()] = parts[1].Trim();
            }
        }

        private void ProcessContent()
        {
            if (_parser != null)
            {
                _parser.Parse();
                return;
            }

            if (ProcessExpectHeader())
                return;

            if (ProcessContentLengthHeader())
                return;

            // The request has been completely parsed now.

            ExecuteRequest();
        }

        private bool ProcessExpectHeader()
        {
            // Process the Expect: 100-continue header.

            string expectHeader;

            if (Headers.TryGetValue("Expect", out expectHeader))
            {
                // Remove the expect header for the next run.

                Headers.Remove("Expect");

                int pos = expectHeader.IndexOf(';');

                if (pos != -1)
                    expectHeader = expectHeader.Substring(0, pos).Trim();

                if (!String.Equals("100-continue", expectHeader, StringComparison.OrdinalIgnoreCase))
                    throw new ProtocolException(String.Format("Could not process Expect header '{0}'", expectHeader));

                SendContinueResponse();
                return true;
            }

            return false;
        }

        private bool ProcessContentLengthHeader()
        {
            // Read the content.

            string contentLengthHeader;

            if (Headers.TryGetValue("Content-Length", out contentLengthHeader))
            {
                int contentLength;

                if (!int.TryParse(contentLengthHeader, out contentLength))
                    throw new ProtocolException(String.Format("Could not parse Content-Length header '{0}'", contentLengthHeader));

                string contentTypeHeader;

                if (!Headers.TryGetValue("Content-Type", out contentTypeHeader))
                    throw new ProtocolException("Expected Content-Type header with Content-Length header");

                string[] parts = contentTypeHeader.Split(new[] { ';' }, 2);

                HttpUtil.TrimAll(parts);

                if (_parser != null)
                {
                    _parser.Dispose();
                    _parser = null;
                }

                switch (parts[0].ToLowerInvariant())
                {
                    case "application/x-www-form-urlencoded":
                        _parser = new UrlEncodedParser(this, contentLength);
                        break;

                    case "multipart/form-data":
                        string boundary = null;

                        if (parts.Length == 2)
                        {
                            parts = parts[1].Split(new[] { '=' }, 2);

                            if (
                                parts.Length == 2 &&
                                String.Equals(parts[0], "boundary", StringComparison.OrdinalIgnoreCase)
                            )
                                boundary = parts[1];
                        }

                        if (boundary == null)
                            throw new ProtocolException("Expected boundary with multipart content type");

                        _parser = new MultiPartParser(this, contentLength, parts[1]);
                        break;

                    default:
                        throw new ProtocolException(String.Format("Could not process Content-Type header '{0}'", contentTypeHeader));
                }

                // We've made a parser available. Recurs back to start processing
                // with the parser.

                ProcessContent();
                return true;
            }

            return false;
        }

        private void SendContinueResponse()
        {
            var sb = new StringBuilder();

            sb.Append(Protocol);
            sb.Append(" 100 Continue\r\nServer: ");
            sb.Append(Server.ServerBanner);
            sb.Append("\r\nDate: ");
            sb.Append(DateTime.UtcNow.ToString("R"));
            sb.Append("\r\n\r\n");

            var bytes = Encoding.ASCII.GetBytes(sb.ToString());

            if (_writeStream != null)
                _writeStream.Dispose();

            _writeStream = new MemoryStream();
            _writeStream.Write(bytes, 0, bytes.Length);
            _writeStream.Position = 0;

            BeginWrite();
        }

        private void BeginWrite()
        {
            try
            {
                // Copy the next part from the write stream.

                int read = _writeStream.Read(_writeBuffer, 0, _writeBuffer.Length);

                _stream.BeginWrite(_writeBuffer, 0, read, WriteCallback, null);
            }
            catch (Exception ex)
            {
                Log.Warn("BeginWrite failed", ex);

                Dispose();
            }
        }

        private void WriteCallback(IAsyncResult asyncResult)
        {
            if (_disposed)
                return;

            try
            {
                _stream.EndWrite(asyncResult);

                if (_writeStream != null && _writeStream.Length != _writeStream.Position)
                {
                    // Continue writing from the write stream.

                    BeginWrite();
                }
                else
                {
                    if (_writeStream != null)
                    {
                        _writeStream.Dispose();
                        _writeStream = null;
                    }

                    switch (_state)
                    {
                        case ClientState.WritingHeaders:
                            WriteResponseContent();
                            break;

                        case ClientState.WritingContent:
                            ProcessRequestCompleted();
                            break;

                        default:
                            Debug.Assert(_state != ClientState.Closed);

                            if (_readBuffer.DataAvailable)
                                ProcessReadBuffer();
                            else
                                BeginRead();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to write", ex);

                Dispose();
            }
        }

        private void ExecuteRequest()
        {
            _context = new HttpContext(this);

            Log.Debug(String.Format("Accepted request '{0}'", _context.Request.RawUrl));

            Server.RaiseRequest(_context);

            WriteResponseHeaders();
        }

        private void WriteResponseHeaders()
        {
            var headers = BuildResponseHeaders();

            if (_writeStream != null)
                _writeStream.Dispose();

            _writeStream = new MemoryStream(headers);

            _state = ClientState.WritingHeaders;

            BeginWrite();
        }

        private byte[] BuildResponseHeaders()
        {
            var response = _context.Response;
            var sb = new StringBuilder();

            // Write the prolog.

            sb.Append(Protocol);
            sb.Append(' ');
            sb.Append(response.StatusCode);

            if (!String.IsNullOrEmpty(response.StatusDescription))
            {
                sb.Append(' ');
                sb.Append(response.StatusDescription);
            }

            sb.Append("\r\n");

            // Write all headers provided by Response.

            if (!String.IsNullOrEmpty(response.CacheControl))
                WriteHeader(sb, "Cache-Control", response.CacheControl);

            if (!String.IsNullOrEmpty(response.ContentType))
            {
                string contentType = response.ContentType;

                if (!String.IsNullOrEmpty(response.CharSet))
                    contentType += "; charset=" + response.CharSet;

                WriteHeader(sb, "Content-Type", contentType);
            }

            WriteHeader(sb, "Expires", response.ExpiresAbsolute.ToString("R"));

            if (!String.IsNullOrEmpty(response.RedirectLocation))
                WriteHeader(sb, "Location", response.RedirectLocation);

            // Write the remainder of the headers.

            foreach (string key in response.Headers.AllKeys)
            {
                WriteHeader(sb, key, response.Headers[key]);
            }

            // Write the content length (we override custom headers for this).

            WriteHeader(sb, "Content-Length", response.OutputStream.BaseStream.Length.ToString(CultureInfo.InvariantCulture));

            sb.Append("\r\n");

            return response.HeadersEncoding.GetBytes(sb.ToString());
        }

        private void WriteHeader(StringBuilder sb, string key, string value)
        {
            sb.Append(key);
            sb.Append(": ");
            sb.Append(value);
            sb.Append("\r\n");
        }

        private void WriteResponseContent()
        {
            if (_writeStream != null)
                _writeStream.Dispose();

            _writeStream = _context.Response.OutputStream.BaseStream;
            _writeStream.Position = 0;

            _state = ClientState.WritingContent;

            BeginWrite();
        }

        private void ProcessRequestCompleted()
        {
            string connectionHeader;

            if (
                Headers.TryGetValue("Connection", out connectionHeader) &&
                String.Equals(connectionHeader, "keep-alive", StringComparison.OrdinalIgnoreCase)
            )
                BeginRequest();
            else
                Dispose();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _state = ClientState.Closed;

                if (_stream != null)
                {
                    _stream.Dispose();
                    _stream = null;
                }

                if (TcpClient != null)
                {
                    TcpClient.Close();
                    TcpClient = null;
                }

                Reset();

                _disposed = true;
            }
        }

        private enum ClientState
        {
            ReadingProlog,
            ReadingHeaders,
            ReadingContent,
            WritingHeaders,
            WritingContent,
            Closed
        }
    }
}
