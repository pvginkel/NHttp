using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using NHttp.Test.Support;
using NUnit.Framework;

namespace NHttp.Test.WebRequestFixtures
{
    [TestFixture]
    public class GracefullShutdown : FixtureBase
    {
        [Test]
        [ExpectedException(typeof(WebException))]
        public void ForcedShutdown()
        {
            using (var server = new HttpServer())
            {
                server.ShutdownTimeout = TimeSpan.FromSeconds(1);

                server.RequestReceived += (s, e) =>
                {
                    // Start closing the server.

                    ThreadPool.QueueUserWorkItem(p => server.Stop());

                    // Wait some time to fulfill the request.

                    Thread.Sleep(TimeSpan.FromSeconds(30));

                    using (var writer = new StreamWriter(e.Response.OutputStream))
                    {
                        writer.WriteLine("Hello!");
                    }
                };

                server.Start();

                var request = (HttpWebRequest)WebRequest.Create(
                    String.Format("http://{0}/", server.EndPoint)
                );

                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    Console.WriteLine("Response: " + reader.ReadToEnd());
                }
            }
        }
    }
}
