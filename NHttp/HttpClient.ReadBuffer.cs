using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NHttp
{
	partial class HttpClient
	{
        private class ReadBuffer
        {
            private int _offset;
            private StringBuilder _lineBuffer;

            public int Available { get; private set; }

            public byte[] Buffer { get; private set; }

            public bool DataAvailable
            {
                get { return _offset < Available; }
            }

            public ReadBuffer(int size)
            {
                Buffer = new byte[size];
            }

            public void SetAvailable(int available)
            {
                _offset = 0;
                Available = available;
            }

            public string ReadLine()
            {
                if (_lineBuffer == null)
                    _lineBuffer = new StringBuilder();

                while (_offset < Available)
                {
                    int c = Buffer[_offset++];

                    if (c == '\n')
                    {
                        string line = _lineBuffer.ToString();

                        _lineBuffer = new StringBuilder();

                        return line;
                    }
                    else if (c != '\r')
                    {
                        _lineBuffer.Append((char)c);
                    }
                }

                return null;
            }

            public void Reset()
            {
                _lineBuffer = null;
            }

            internal void CopyToStream(Stream stream, int maximum)
            {
                int toRead = Math.Min(
                    Available - _offset,
                    maximum - (int)stream.Length
                );

                stream.Write(Buffer, _offset, toRead);

                _offset += toRead;
            }
        }
	}
}
