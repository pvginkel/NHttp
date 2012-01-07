using System;
using System.Collections.Generic;
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
        private readonly byte[] _readBuffer;
        private readonly byte[] _writeBuffer;
        private NetworkStream _stream;
        private ClientState _state;
        private StringBuilder _lineBuffer;
        private string _method;
        private string _protocol;
        private string _request;
        private Dictionary<string, string> _headers;
        private Dictionary<string, string> _postParameters;
        private MemoryStream _writeStream;
        private RequestParser _parser;
        private HttpContext _context;

        public HttpServer Server { get; private set; }

        public TcpClient TcpClient { get; private set; }

        public HttpClient(HttpServer server, TcpClient client)
        {
            if (server == null)
                throw new ArgumentNullException("server");
            if (client == null)
                throw new ArgumentNullException("client");

            Server = server;
            TcpClient = client;

            _readBuffer = new byte[server.ReadBufferSize];
            _writeBuffer = new byte[server.WriteBufferSize];

            _stream = client.GetStream();
        }

        private void Reset()
        {
            _state = ClientState.ReadingProlog;
            _lineBuffer = null;
            _method = null;
            _protocol = null;
            _request = null;
            _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _postParameters = null;
            _parser = null;
            _context = null;

            if (_writeStream != null)
            {
                _writeStream.Dispose();
                _writeStream = null;
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
                _stream.BeginRead(_readBuffer, 0, _readBuffer.Length, ReadCallback, null);
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to read", ex);

                Dispose();
            }
        }

        private void ReadCallback(IAsyncResult asyncResult)
        {
            if (_disposed)
                return;

            try
            {
                int read = _stream.EndRead(asyncResult);

                ProcessReadBuffer(read);

                if (read > 0 && _state != ClientState.Closed)
                {
                    // If we have a write stream, we're writing.

                    if (_writeStream == null)
                        BeginRead();
                }
                else
                {
                    // A value of 0 returned by EndRead means that the remote
                    // side has already closed the connection.

                    Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to read from the HTTP connection", ex);

                Dispose();
            }
        }

        private void ProcessReadBuffer(int read)
        {
            switch (_state)
            {
                case ClientState.ReadingProlog:
                    ProcessProlog(0, read);
                    break;

                case ClientState.ReadingHeaders:
                    ProcessHeaders(0, read);
                    break;

                case ClientState.ReadingContent:
                    ProcessContent(0, read);
                    break;
            }
        }

        private void ProcessProlog(int offset, int available)
        {
            string line = ReadLine(ref offset, available);

            if (line == null)
                return;

            // Parse the prolog.

            var match = PrologRegex.Match(line);

            if (!match.Success)
            {
                Log.Debug(String.Format("Could not parse prolog '{0}'", line));

                _state = ClientState.Closed;
                return;
            }

            _method = match.Groups[1].Value;
            _request = match.Groups[2].Value;
            _protocol = match.Groups[3].Value;

            // Continue reading the headers.

            _state = ClientState.ReadingHeaders;

            ProcessHeaders(offset, available);
        }

        private void ProcessHeaders(int offset, int available)
        {
            string line = ReadLine(ref offset, available);

            if (line == null)
                return;

            // Have we completed receiving the headers?

            if (line.Length == 0)
            {
                // Start processing the body of the request.

                _state = ClientState.ReadingContent;

                ProcessContent(offset, available);
            }
            else
            {
                string[] parts = line.Split(new[] { ':' }, 2);

                if (parts.Length != 2)
                {
                    _state = ClientState.Closed;
                }
                else
                {
                    _headers[parts[0].Trim()] = parts[1].Trim();

                    // Continue reading the next header.

                    ProcessHeaders(offset, available);
                }
            }
        }

        private void ProcessContent(int offset, int available)
        {
            if (_parser != null)
            {
                _parser.Parse(ref offset, available);
                return;
            }

            if (ProcessExpectHeader())
                return;

            if (ProcessContentLengthHeader(offset, available))
                return;

            // The request has been completely parsed now.

            ExecuteRequest();
        }

        private bool ProcessExpectHeader()
        {
            // Process the Expect: 100-continue header.

            string expectHeader;

            if (_headers.TryGetValue("Expect", out expectHeader))
            {
                // Remove the expect header for the next run.

                _headers.Remove("Expect");

                int pos = expectHeader.IndexOf(';');

                if (pos != -1)
                    expectHeader = expectHeader.Substring(0, pos).Trim();

                if (!String.Equals("100-continue", expectHeader, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warn(String.Format("Could not process Expect header '{0}'", expectHeader));

                    _state = ClientState.Closed;
                    return true;
                }

                SendContinueResponse();
                return true;
            }

            return false;
        }

        private bool ProcessContentLengthHeader(int offset, int available)
        {
            // Read the content.

            string contentLengthHeader;

            if (_headers.TryGetValue("Content-Length", out contentLengthHeader))
            {
                int contentLength;

                if (!int.TryParse(contentLengthHeader, out contentLength))
                {
                    Log.Warn(String.Format("Could not parse Content-Length header '{0}'", contentLengthHeader));

                    _state = ClientState.Closed;
                    return true;
                }

                string contentTypeHeader;

                if (!_headers.TryGetValue("Content-Type", out contentTypeHeader))
                {
                    Log.Warn("Expected Content-Type header with Content-Length header");

                    _state = ClientState.Closed;
                    return true;
                }

                switch (contentTypeHeader.ToLowerInvariant())
                {
                    case "application/x-www-form-urlencoded":
                        _parser = new UrlEncodedParser(this, contentLength);
                        break;

                    default:
                        Log.Warn(String.Format("Could not process Content-Type header '{0}'", contentTypeHeader));

                        _state = ClientState.Closed;
                        return true;
                }

                // We've made a parser available. Recurs back to start processing
                // with the parser.

                ProcessContent(offset, available);
                return true;
            }

            return false;
        }

        private void SendContinueResponse()
        {
            var sb = new StringBuilder();

            sb.Append(_protocol);
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
                Log.Warn("Failed to write", ex);

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
                    BeginWrite();
                }
                else
                {
                    if (_writeStream != null)
                        _writeStream.Dispose();

                    _writeStream = null;

                    switch (_state)
                    {
                        case ClientState.WritingHeaders:
                            WriteResponseContent();
                            break;

                        case ClientState.WritingContent:
                            ProcessRequestCompleted();
                            break;

                        default:
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

        private string ReadLine(ref int offset, int available)
        {
            if (_lineBuffer == null)
                _lineBuffer = new StringBuilder();

            while (offset < available)
            {
                int c = _readBuffer[offset++];

                if (c == '\n')
                {
                    string line = _lineBuffer.ToString();

                    _lineBuffer = new StringBuilder();

                    return line;
                }
                else if (c != '\r')
                {
                    _lineBuffer.Append((char)c);
                }
            }

            return null;
        }

        private void ExecuteRequest()
        {
            _context = new HttpContext(this, _protocol, _method, _request, _headers, _postParameters);

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

            sb.Append(_protocol);
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
                _headers.TryGetValue("Connection", out connectionHeader) &&
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

                if (_writeStream != null)
                {
                    _writeStream.Dispose();
                    _writeStream = null;
                }

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
