using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using NHttp.Test.Support;
using NUnit.Framework;

namespace NHttp.Test.WebRequestFixtures
{
    [TestFixture]
    public class BasicRequest : FixtureBase
    {
        private const string ResponseText = "Response text";

        [Test]
        public void SingleRequest()
        {
            using (var server = new HttpServer())
            {
                server.RequestReceived += (s, e) =>
                {
                    Assert.That(e.Request.QueryString.AllKeys, Is.EquivalentTo(new[] { "key" }));
                    Assert.AreEqual(e.Request.QueryString["key"], "value");

                    using (var writer = new StreamWriter(e.Response.OutputStream))
                    {
                        writer.Write(ResponseText);
                    }
                };

                server.Start();

                var request = (HttpWebRequest)WebRequest.Create(
                    String.Format("http://{0}/?key=value", server.EndPoint)
                );

                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    Assert.AreEqual(reader.ReadToEnd(), ResponseText);
                }
            }
        }
    }
}
