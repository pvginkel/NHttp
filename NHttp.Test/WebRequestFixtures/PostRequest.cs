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
    public class PostRequest : FixtureBase
    {
        private const string ResponseText = "Response text";

        [Test]
        public void SimplePost()
        {
            using (var server = new HttpServer())
            {
                server.RequestReceived += (s, e) =>
                {
                    Assert.That(e.Request.Form.AllKeys, Is.EquivalentTo(new[] { "key" }));
                    Assert.AreEqual(e.Request.Form["key"], "value");

                    using (var writer = new StreamWriter(e.Response.OutputStream))
                    {
                        writer.Write(ResponseText);
                    }
                };

                server.Start();

                var request = (HttpWebRequest)WebRequest.Create(
                    String.Format("http://{0}/", server.EndPoint)
                );

                byte[] postBytes = Encoding.ASCII.GetBytes("key=value");

                request.Method = "POST";
                request.ContentLength = postBytes.Length;
                request.ContentType = "application/x-www-form-urlencoded";

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(postBytes, 0, postBytes.Length);
                }

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
