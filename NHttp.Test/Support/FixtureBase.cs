using System;
using System.Collections.Generic;
using System.Text;
using Common.Logging;
using Common.Logging.Simple;
using NUnit.Framework;

namespace NHttp.Test.Support
{
    public abstract class FixtureBase
    {
        [TestFixtureSetUp]
        public void SetupFixture()
        {
            LogManager.Adapter = new ConsoleOutLoggerFactoryAdapter();
        }
    }
}
