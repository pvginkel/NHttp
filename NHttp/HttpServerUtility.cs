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
    }
}
