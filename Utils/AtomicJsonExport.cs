using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ZenStatesDebugTool
{
    public static class AtomicJsonExport
    {
        // Stream UTF-8 once; enforce the byte limit before replacing an existing report.
        public static void Write(string path, object snapshot, long maximumBytes)
        {
            if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            path = Path.GetFullPath(path); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
                using (var bounded = new ByteLimitStream(file, maximumBytes))
                using (var text = new StreamWriter(bounded, new UTF8Encoding(false), 4096))
                using (var json = new JsonTextWriter(text) { Formatting = Formatting.Indented }) JsonSerializer.CreateDefault().Serialize(json, snapshot);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private sealed class ByteLimitStream : Stream
        {
            private readonly Stream stream;
            private readonly long limit;
            private long written;
            public ByteLimitStream(Stream stream, long limit) { this.stream = stream; this.limit = limit; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count > limit - written) throw new InvalidOperationException("Session report exceeds its UTF-8 byte limit.");
                stream.Write(buffer, offset, count); written += count;
            }
            public override void Flush() => stream.Flush();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => stream.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => written; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
        }
    }
}
