using System;
using System.Collections.Generic;
using System.Text;

namespace NHttp
{
    public class HttpContext
    {
        internal HttpContext(HttpClient client, string protocol, string method, string request, Dictionary<string, string> headers, Dictionary<string, string> postParameters)
        {
            Server = client.Server.ServerUtility;
            Request = new HttpRequest(client, protocol, method, request, headers, postParameters);
            Response = new HttpResponse(this);
        }

        public HttpServerUtility Server { get; private set; }

        public HttpRequest Request { get; private set; }

        public HttpResponse Response { get; private set; }
    }
}
