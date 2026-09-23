using System.Text.Json;

namespace UN.Nexo.Core.Services;

internal static class BoundedPersistedJson
{
    internal const long MaxStoreBytes = 4L * 1024 * 1024;
    internal const int MaxStoreEntries = 4096;

    internal static async Task<T?> DeserializeAsync<T>(
        string path,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (file.Length > MaxStoreBytes)
            throw Oversized(path);

        await using var bounded =
            new BoundedReadStream(
                file,
                MaxStoreBytes,
                Path.GetFileName(path));

        return await JsonSerializer.DeserializeAsync<T>(
            bounded,
            options,
            cancellationToken);
    }

    private static InvalidDataException Oversized(string path)
        => new(
            $"Persisted store '{Path.GetFileName(path)}' exceeds the {MaxStoreBytes} byte safety limit. The original file was preserved.");

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private readonly string _name;
        private long _bytesRead;
        private bool _verifiedEofAtLimit;

        internal BoundedReadStream(
            Stream inner,
            long maxBytes,
            string name)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _name = name;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            if (_verifiedEofAtLimit)
                return 0;

            var allowed = GetAllowedCount(count);
            var read = _inner.Read(buffer, offset, allowed);
            _bytesRead += read;
            ProbeOverflowIfAtLimit(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_verifiedEofAtLimit)
                return 0;

            var allowed = GetAllowedCount(buffer.Length);
            var read = _inner.Read(buffer[..allowed]);
            _bytesRead += read;
            ProbeOverflowIfAtLimit(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_verifiedEofAtLimit)
                return 0;

            var allowed = GetAllowedCount(buffer.Length);
            var read = await _inner.ReadAsync(
                buffer[..allowed],
                cancellationToken);
            _bytesRead += read;
            await ProbeOverflowIfAtLimitAsync(
                read,
                cancellationToken);
            return read;
        }

        private int GetAllowedCount(int requested)
        {
            if (_bytesRead >= _maxBytes)
                return 0;

            return (int)Math.Min(
                requested,
                _maxBytes - _bytesRead);
        }

        private void ProbeOverflowIfAtLimit(int read)
        {
            if (read == 0 || _bytesRead < _maxBytes)
                return;

            if (_inner.ReadByte() != -1)
                throw Oversized(_name);

            _verifiedEofAtLimit = true;
        }

        private async ValueTask ProbeOverflowIfAtLimitAsync(
            int read,
            CancellationToken cancellationToken)
        {
            if (read == 0 || _bytesRead < _maxBytes)
                return;

            var probe = new byte[1];
            if (await _inner.ReadAsync(
                    probe.AsMemory(),
                    cancellationToken) != 0)
                throw Oversized(_name);

            _verifiedEofAtLimit = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value)
            => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
