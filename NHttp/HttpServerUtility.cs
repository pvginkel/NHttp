using System;
using System.Collections.Generic;
using System.Text;

namespace NHttp
{
    public class HttpServerUtility
    {
        internal HttpServerUtility()
        {
        }

        public string MachineName
        {
            get { return Environment.MachineName; }
        }

        public string HtmlEncode(string value)
        {
            return HttpUtil.HtmlEncode(value);
        }

        public string HtmlDecode(string value)
        {
            return HttpUtil.HtmlDecode(value);
        }

        public string UrlEncode(string text)
        {
            return Uri.EscapeDataString(text);
        }

        public string UrlDecode(string text)
        {
            return HttpUtil.UriDecode(text);
        }
    }
}
