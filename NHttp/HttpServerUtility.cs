using System;
using System.Collections.Generic;
using System.Text;

namespace NHttp
{
    public class HttpServerUtility
    {
        internal HttpServerUtility(HttpServer server)
        {
            Server = server;
        }

        internal HttpServer Server { get; private set; }

        public string MachineName
        {
            get { return Environment.MachineName; }
        }
    }
}
