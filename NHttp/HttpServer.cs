using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Common.Logging;

namespace NHttp
{
    public class HttpServer : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HttpServer));

        private bool _disposed;
        private TcpListener _listener;

        public event HttpRequestEventHandler RequestReceived;

        protected virtual void OnRequestReceived(HttpRequestEventArgs e)
        {
            var ev = RequestReceived;

            if (ev != null)
                ev(this, e);
        }

        public event EventHandler Started;

        protected virtual void OnStarted(EventArgs e)
        {
            var ev = Started;

            if (ev != null)
                ev(this, e);
        }

        protected event EventHandler Stopping;

        protected virtual void OnStopping(EventArgs e)
        {
            var ev = Stopping;

            if (ev != null)
                ev(this, e);
        }

        public IPEndPoint EndPoint { get; set; }

        public bool IsActive { get; private set; }

        public int ReadBufferSize { get; set; }

        public int WriteBufferSize { get; set; }

        public string ServerBanner { get; set; }

        internal HttpServerUtility ServerUtility { get; private set; }

        public HttpServer()
        {
            EndPoint = new IPEndPoint(IPAddress.Loopback, 0);

            ReadBufferSize = 4096;
            WriteBufferSize = 4096;

            ServerBanner = String.Format("NHttp/{0}", GetType().Assembly.GetName().Version);
        }

        public void Start()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (IsActive)
                throw new InvalidOperationException("Server is already active");

            Log.Debug(String.Format("Starting HTTP server at {0}", EndPoint));

            // Start the listener.

            var listener = new TcpListener(EndPoint);

            try
            {
                listener.Start();

                EndPoint = (IPEndPoint)listener.LocalEndpoint;

                _listener = listener;

                ServerUtility = new HttpServerUtility(this);

                IsActive = true;

                Log.Info(String.Format("HTTP server running at {0}", EndPoint));
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to start HTTP server", ex);

                throw new NHttpException("Failed to start HTTP server", ex);
            }

            BeginAcceptTcpClient();

            OnStarted(EventArgs.Empty);
        }

        public void Stop()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (!IsActive)
                throw new InvalidOperationException("Server is not active");

            Log.Debug("Stopping HTTP server");

            OnStopping(EventArgs.Empty);

            try
            {
                _listener.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to stop HTTP server", ex);

                throw new NHttpException("Failed to stop HTTP server", ex);
            }
            finally
            {
                _listener = null;

                IsActive = false;

                Log.Info("Stopped HTTP server");
            }
        }

        private void BeginAcceptTcpClient()
        {
            if (!IsActive)
                return;

            _listener.BeginAcceptTcpClient(AcceptTcpClientCallback, null);
        }

        private void AcceptTcpClientCallback(IAsyncResult asyncResult)
        {
            if (!IsActive)
                return;

            try
            {
                var listener = _listener; // Prevent race condition.

                if (listener == null)
                    return;

                var tcpClient = listener.EndAcceptTcpClient(asyncResult);

                var client = new HttpClient(this, tcpClient);

                client.BeginRequest();

                BeginAcceptTcpClient();
            }
            catch (ObjectDisposedException)
            {
                // EndAcceptTcpClient will throw a ObjectDisposedException
                // when we're shutting down. This can safely be ignored.
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to accept TCP client", ex);
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (IsActive)
                    Stop();

                _disposed = true;
            }
        }

        internal void RaiseRequest(HttpContext context)
        {
            if (context == null)
                throw new ArgumentNullException("context");

            OnRequestReceived(new HttpRequestEventArgs(context));
        }
    }
}
