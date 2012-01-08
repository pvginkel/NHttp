using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using NHttp.Test.Support;
using NUnit.Framework;

namespace NHttp.Test.WebRequestFixtures
{
    [TestFixture]
    public class Upload : FixtureBase
    {
        private const int UploadSize = 1 * 1024 * 1024;

        [Test]
        public void UploadBinaryData()
        {
            using (var server = new HttpServer())
            using (var uploadStream = new MemoryStream())
            {
                WriteRandomData(uploadStream, UploadSize);

                server.RequestReceived += (s, e) =>
                {
                    uploadStream.Position = 0;

                    Assert.AreEqual(uploadStream, e.Request.InputStream);
                    Assert.AreEqual(e.Request.ContentLength, UploadSize);
                    Assert.AreEqual(e.Request.ContentType, "application/octet-stream");
                    Assert.AreEqual(e.Request.HttpMethod, "POST");
                };

                server.Start();

                var request = (HttpWebRequest)WebRequest.Create(
                    String.Format("http://{0}/", server.EndPoint)
                );

                request.Method = "POST";
                request.ContentType = "application/octet-stream";
                request.ContentLength = UploadSize;

                using (var stream = request.GetRequestStream())
                {
                    uploadStream.Position = 0;

                    uploadStream.CopyTo(stream);
                }

                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    reader.ReadToEnd();
                }
            }
        }
    }
}
