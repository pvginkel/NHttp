using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NHttp.Test.Support;
using NUnit.Framework;
using mshtml;

namespace NHttp.Test.WebBrowserFixtures
{
    [TestFixture]
    public class FileUpload : WebBrowserFixtureBase
    {
        private const string UploadFileContent = "Upload file content";

        [Test]
        public void FileUploadPost()
        {
            string tempFileName = Path.GetTempFileName();

            try
            {
                using (var tempFile = File.CreateText(tempFileName))
                {
                    tempFile.Write(UploadFileContent);
                    tempFile.Flush();
                }

                RegisterHandler(new ResourceHandler("/form", GetType().Namespace + ".Resources.FileUploadForm.html"));

                var submittedEvent = new ManualResetEvent(false);

                RegisterHandler("/submit", p =>
                {
                    Assert.That(p.Request.Form.AllKeys, Is.EquivalentTo(new[] { "key" }));
                    Assert.AreEqual("value", p.Request.Form["key"]);
                    Assert.That(p.Request.Files.AllKeys, Is.EquivalentTo(new[] { "file" }));

                    using (var reader = new StreamReader(p.Request.Files["file"].InputStream))
                    {
                        Assert.AreEqual(UploadFileContent, reader.ReadToEnd());
                    }

                    Assert.AreEqual(Path.GetFileName(tempFileName), p.Request.Files["file"].FileName);
                    Assert.AreEqual(UploadFileContent.Length, p.Request.Files["file"].ContentLength);
                    Assert.AreEqual("text/plain", p.Request.Files["file"].ContentType);

                    submittedEvent.Set();
                });

                DocumentCompleted += (s, e) =>
                {
                    if (e.Document.Url.AbsolutePath == "/form")
                    {
                        var fileElement = (HTMLInputElement)e.Document.GetElementById("file").DomElement;
                        var formElement = (IHTMLFormElement)e.Document.GetElementById("form").DomElement;

                        fileElement.focus();

                        // The first space is to open the file open dialog. The
                        // remainder of the spaces is to have some messages for
                        // until the open dialog actually opens.

                        SendKeys.SendWait("                    " + tempFileName + "{ENTER}");

                        formElement.submit();
                    }
                };

                Navigate("/form");

                submittedEvent.WaitOne();
            }
            finally
            {
                File.Delete(tempFileName);
            }
        }
    }
}
