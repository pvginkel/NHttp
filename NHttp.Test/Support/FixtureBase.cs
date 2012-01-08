using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Common.Logging;
using Common.Logging.Simple;
using NUnit.Framework;

namespace NHttp.Test.Support
{
    public abstract class FixtureBase
    {
        private readonly Random _random = new Random();

        [TestFixtureSetUp]
        public void SetupFixture()
        {
            LogManager.Adapter = new ConsoleOutLoggerFactoryAdapter();
        }

        protected void WriteRandomData(Stream stream, int bytes)
        {
            var randomBytes = new byte[128];

            _random.NextBytes(randomBytes);

            for (int i = 0; i < bytes / randomBytes.Length; i++)
            {
                stream.Write(randomBytes, 0, randomBytes.Length);
            }
        }
    }
}
