using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;

namespace NHttp.Test.Support
{
    public abstract class WebBrowserFixtureBase : FixtureBase
    {
        private WebBrowserFixtureProxy _proxy;

        [SetUp]
        public void Setup()
        {
            _proxy = new WebBrowserFixtureProxy();
        }

        [TearDown]
        public void TearDown()
        {
            _proxy.Dispose();
            _proxy = null;
        }

        protected void Navigate(string location)
        {
            _proxy.Navigate(location);
        }

        public event DocumentCompletedEventHandler DocumentCompleted
        {
            add { _proxy.DocumentCompleted += value; }
            remove { _proxy.DocumentCompleted -= value; }
        }

        protected void RegisterHandler(IRequestHandler handler)
        {
            _proxy.RegisterHandler(handler);
        }

        protected void RegisterHandler(string path, RequestHandlerCallback callback)
        {
            RegisterHandler(new RequestHandler(path, callback));
        }
    }
}
